using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Tools;

/// <summary>
/// Integration coverage for OS-enforced resource limits on <c>run_spell_script</c>. The CPU/memory
/// exceed tests are skipped by default (see remarks) but document the expected behavior and can be
/// run manually on macOS/Linux.
/// </summary>
/// <remarks>
/// Deliberately spawns processes that spin the CPU or grow unbounded memory to trigger a kernel-level
/// kill (SIGXCPU/SIGKILL). Running these unconditionally in CI risks destabilizing the shared test
/// runner process (e.g. if the enclosing cgroup/rlimit setup differs from what the assertion expects),
/// so they are marked <see cref="FactAttribute.Skip"/> and intended for manual verification.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class ProcessRunnerResourceLimitTests : IDisposable
{

    private readonly string _scriptsRoot;

    public ProcessRunnerResourceLimitTests()
    {

        _scriptsRoot = Path.Combine(Path.GetTempPath(), "arcanum-resourcelimit-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_scriptsRoot);

    }

    public void Dispose()
    {

        try
        {

            Directory.Delete(_scriptsRoot, recursive: true);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {

        }

    }

    [Fact]
    public async Task Process_within_limits_completes_normally()
    {
        using HostProcessToolsEscapeHatchScope _ = new();

        string script = await WriteScriptAsync("harmless.sh", "#!/bin/sh\necho ok\n");

        FakeSanctumGuard guard = new(new ResourceLimits { MaxCpuSeconds = 30, MaxMemoryMb = 512, MaxFileDescriptors = 256 });

        ArcanumSpellScriptTool tool = new(
            [_scriptsRoot],
            sanctumGuard: guard,
            resourceLimiter: new ProcessResourceLimiter(),
            campaignWorkspaceRoot: "/fake/workspace",
            allowUnsandboxedToolChildren: true);

        string? result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = Path.GetFileName(script) })) as string;

        Assert.NotNull(result);

        Assert.Contains("--- exit code ---", result, StringComparison.Ordinal);

        Assert.Empty(guard.RecordedBreaches);

    }

    [Fact(Skip = "Requires OS-level resource enforcement — run manually on macOS/Linux")]
    public async Task Process_exceeding_cpu_limit_is_terminated_and_breached()
    {
        using HostProcessToolsEscapeHatchScope _ = new();

        string script = await WriteScriptAsync("spin.sh", "#!/bin/sh\nwhile true; do :; done\n");

        FakeSanctumGuard guard = new(new ResourceLimits { MaxCpuSeconds = 1, MaxMemoryMb = 0, MaxFileDescriptors = 0 });

        ArcanumSpellScriptTool tool = new(
            [_scriptsRoot],
            sanctumGuard: guard,
            resourceLimiter: new ProcessResourceLimiter(),
            campaignWorkspaceRoot: "/fake/workspace",
            allowUnsandboxedToolChildren: true);

        string? result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = Path.GetFileName(script) })) as string;

        Assert.NotNull(result);

        Assert.Contains("exceeded the CPU time limit", result, StringComparison.Ordinal);

        Assert.Contains(guard.RecordedBreaches, b => b.Resource == ResourceLimitKind.Cpu);

    }

    [Fact(Skip = "Requires OS-level resource enforcement — run manually on macOS/Linux")]
    public async Task Process_exceeding_memory_limit_is_terminated_and_breached()
    {
        using HostProcessToolsEscapeHatchScope _ = new();

        string script = await WriteScriptAsync(
            "hog.py",
            "data = []\n"
            + "while True:\n"
            + "    data.append(bytearray(1024 * 1024))\n");

        FakeSanctumGuard guard = new(new ResourceLimits { MaxCpuSeconds = 0, MaxMemoryMb = 32, MaxFileDescriptors = 0 });

        ArcanumSpellScriptTool tool = new(
            [_scriptsRoot],
            sanctumGuard: guard,
            resourceLimiter: new ProcessResourceLimiter(),
            campaignWorkspaceRoot: "/fake/workspace",
            allowUnsandboxedToolChildren: true);

        string? result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["script_name"] = Path.GetFileName(script) })) as string;

        Assert.NotNull(result);

        Assert.Contains("exceeded the memory limit", result, StringComparison.Ordinal);

        Assert.Contains(guard.RecordedBreaches, b => b.Resource == ResourceLimitKind.Memory);

    }

    [Fact]
    public async Task Denial_message_is_sanitized()
    {

        FakeSanctumGuard guard = new(new ResourceLimits());

        string message = await ResourceLimitDenialFormatter.RecordAndDescribeAsync(
            guard,
            "/fake/workspace",
            "run_spell_script",
            new ResourceLimits { MaxCpuSeconds = 30, MaxMemoryMb = 512, MaxFileDescriptors = 256 },
            ResourceLimitKind.Cpu,
            CancellationToken.None);

        Assert.Contains("CPU time", message, StringComparison.Ordinal);

        Assert.Contains("30s", message, StringComparison.Ordinal);

        // No signal numbers, PIDs, cgroup paths, or stack traces may leak to the model.
        Assert.DoesNotContain("SIGXCPU", message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("SIGKILL", message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("pid", message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("/sys/fs/cgroup", message, StringComparison.Ordinal);

        Assert.DoesNotContain(".cs:line", message, StringComparison.Ordinal);

        Assert.DoesNotContain("Exception", message, StringComparison.Ordinal);

        Assert.Single(guard.RecordedBreaches);

    }

    private async Task<string> WriteScriptAsync(string fileName, string contents)
    {

        string path = Path.Combine(_scriptsRoot, fileName);

        await File.WriteAllTextAsync(path, contents);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

        return path;

    }

    private sealed class FakeSanctumGuard(ResourceLimits limits) : ISanctumGuard
    {

        internal List<(string? WorkspaceRoot, string ToolName, ResourceLimitKind Resource, string LimitValue, string? ActualValue)> RecordedBreaches { get; } = [];

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

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(string? workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(limits);


        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default)
        {

            RecordedBreaches.Add((workspaceRoot, toolName, resource, limitValue, actualValue));

            return Task.CompletedTask;

        }

    }

}
