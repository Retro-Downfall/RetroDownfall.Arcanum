using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// <c>run_spell_script</c> takes its resource limits, child-process boundary and breach record from the Campaign that governs the turn.
/// A Session-bound or explicitly bound turn has no working directory to find that Campaign from, and a working directory can sit in
/// another Campaign than the one that governs the turn, so the tool is told the governing Campaign's root and looks that up instead.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class ArcanumSpellScriptToolGoverningCampaignTests : IDisposable
{
    private const string CampaignRoot = "/campaigns/governing";

    private readonly string _scriptsRoot;

    private readonly HostProcessToolsEscapeHatchScope _hostProcessTools;

    public ArcanumSpellScriptToolGoverningCampaignTests()
    {
        _hostProcessTools = new HostProcessToolsEscapeHatchScope();

        _scriptsRoot = Path.Combine(Path.GetTempPath(), "arcanum-governing-" + Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(_scriptsRoot);
    }

    public void Dispose()
    {
        _hostProcessTools.Dispose();

        _ = TestDirectoryCleanup.TryDelete(_scriptsRoot, nameof(ArcanumSpellScriptToolGoverningCampaignTests));
    }

    [SkippableFact]
    public async Task A_turn_with_no_working_directory_looks_up_the_governing_campaign_for_the_limits_and_the_boundary()
    {
        RecordingGuard guard = new();

        ArcanumSpellScriptTool tool = await CreateToolAsync(guard, campaignWorkspaceRoot: string.Empty);

        tool.BindGoverningCampaignRoot(CampaignRoot);

        string? result = await RunAsync(tool);

        Assert.Contains("--- exit code ---\n0", result, StringComparison.Ordinal);

        Assert.Equal([CampaignRoot], guard.LimitLookups);

        Assert.Equal([CampaignRoot], guard.BoundaryLookups);
    }

    [SkippableFact]
    public async Task A_working_directory_that_sits_in_another_campaign_does_not_decide_which_campaign_limits_the_script()
    {
        RecordingGuard guard = new();

        ArcanumSpellScriptTool tool = await CreateToolAsync(guard, campaignWorkspaceRoot: "/campaigns/nested-and-open");

        tool.BindGoverningCampaignRoot(CampaignRoot);

        _ = await RunAsync(tool);

        Assert.Equal([CampaignRoot], guard.LimitLookups);

        Assert.Equal([CampaignRoot], guard.BoundaryLookups);
    }

    // The negative twin: nothing is bound, so the tool asks about the directory it runs in, exactly as before.
    [SkippableTheory]
    [InlineData("/work/dir")]
    [InlineData("")]
    public async Task A_tool_that_is_not_bound_to_a_campaign_looks_up_its_working_directory(string workspaceRoot)
    {
        RecordingGuard guard = new();

        ArcanumSpellScriptTool tool = await CreateToolAsync(guard, workspaceRoot);

        _ = await RunAsync(tool);

        Assert.Equal([workspaceRoot], guard.LimitLookups);

        Assert.Equal([workspaceRoot], guard.BoundaryLookups);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Binding_no_campaign_root_leaves_the_working_directory_lookup_in_place(string? campaignRoot)
    {
        RecordingGuard guard = new();

        ArcanumSpellScriptTool tool = await CreateToolAsync(guard, "/work/dir");

        tool.BindGoverningCampaignRoot(campaignRoot);

        _ = await RunAsync(tool);

        Assert.Equal(["/work/dir"], guard.LimitLookups);
    }

    // A script that exceeds a limit is recorded against the Campaign that governs the turn, found from its root.
    [SkippableFact]
    public async Task A_resource_limit_breach_is_recorded_against_the_governing_campaign()
    {
        Skip.IfNot(
            string.Equals(
                global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_RESOURCE_LIMIT_ENFORCEMENT"),
                "true",
                StringComparison.OrdinalIgnoreCase),
            "Set ARCANUM_TEST_RESOURCE_LIMIT_ENFORCEMENT=true to run the tests that spawn a runaway child and wait for the kernel to kill it.");

        Skip.If(OperatingSystem.IsWindows(), "The scripts these tests run are POSIX shell scripts.");

        RecordingGuard guard = new() { Limits = new ResourceLimits { MaxCpuSeconds = 1, MaxMemoryMb = 0, MaxFileDescriptors = 0 } };

        string script = await WriteScriptAsync("spin.sh", "#!/bin/sh\nwhile true; do :; done\n");

        ArcanumSpellScriptTool tool = new(
            [_scriptsRoot],
            sanctumGuard: guard,
            resourceLimiter: new ProcessResourceLimiter(),
            campaignWorkspaceRoot: string.Empty,
            allowUnsandboxedToolChildren: true);

        tool.BindGoverningCampaignRoot(CampaignRoot);

        string? result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = Path.GetFileName(script) })) as string;

        Assert.Contains("exceeded the CPU time limit", result, StringComparison.Ordinal);

        Assert.Equal([CampaignRoot], guard.BreachRoots);
    }

    private async Task<ArcanumSpellScriptTool> CreateToolAsync(RecordingGuard guard, string campaignWorkspaceRoot)
    {
        Skip.If(OperatingSystem.IsWindows(), "The scripts these tests run are POSIX shell scripts.");

        _ = await WriteScriptAsync("hello.sh", "#!/bin/sh\necho ok\n");

        return new ArcanumSpellScriptTool(
            [_scriptsRoot],
            sanctumGuard: guard,
            resourceLimiter: new ProcessResourceLimiter(),
            campaignWorkspaceRoot: campaignWorkspaceRoot,
            allowUnsandboxedToolChildren: true);
    }

    private static async Task<string?> RunAsync(ArcanumSpellScriptTool tool) =>
        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = "hello.sh" })) as string;

    private async Task<string> WriteScriptAsync(string name, string body)
    {
        string path = Path.Combine(_scriptsRoot, name);

        await File.WriteAllTextAsync(path, body);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private sealed class RecordingGuard : ISanctumGuard
    {
        public ResourceLimits Limits { get; init; } = new();

        public List<string?> LimitLookups { get; } = [];

        public List<string?> BoundaryLookups { get; } = [];

        public List<string?> BreachRoots { get; } = [];

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(string? workspaceRoot, CancellationToken ct = default)
        {
            LimitLookups.Add(workspaceRoot);

            return Task.FromResult(Limits);
        }

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default)
        {
            BoundaryLookups.Add(workspaceRoot);

            return Task.FromResult<SanctumChildProcessBoundary?>(null);
        }

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default)
        {
            BreachRoots.Add(workspaceRoot);

            return Task.CompletedTask;
        }
    }
}
