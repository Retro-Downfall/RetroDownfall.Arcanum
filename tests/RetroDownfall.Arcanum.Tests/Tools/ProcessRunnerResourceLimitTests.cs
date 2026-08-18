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
/// exceed tests are opt-in (see remarks) on macOS and Linux.
/// </summary>
/// <remarks>
/// Deliberately spawns processes that spin the CPU or grow unbounded memory to trigger a kernel-level
/// kill (SIGXCPU/SIGKILL). Running these unconditionally in CI risks destabilizing the shared test
/// runner process (e.g. if the enclosing cgroup/rlimit setup differs from what the assertion expects),
/// so they are gated on <c>ARCANUM_TEST_RESOURCE_LIMIT_ENFORCEMENT=true</c> and belong on a disposable
/// runner, mirroring the <c>ARCANUM_TEST_OS_CREDENTIAL_STORE</c> opt-in for the real credential store.
/// <para>
/// They were previously <c>[Fact(Skip = "…")]</c> — the only two unconditional skips in a suite that
/// otherwise uses <c>Skip.IfNot</c> in 893 places. An unconditional skip cannot be turned on without
/// editing source, so "run manually on macOS/Linux" was not a thing anyone could actually do from a
/// command line, and these are the only tests that prove a runaway child is genuinely killed rather
/// than that a <c>ulimit</c> string was assembled.
/// </para>
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

    /// <summary>
    /// Opt-in gate for the two tests that spawn a process the kernel must kill.
    /// </summary>
    private static void SkipUnlessEnforcementOptIn()
    {

        Skip.IfNot(
            string.Equals(
                global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_RESOURCE_LIMIT_ENFORCEMENT"),
                "true",
                StringComparison.OrdinalIgnoreCase),
            "Set ARCANUM_TEST_RESOURCE_LIMIT_ENFORCEMENT=true to run the tests that spawn a runaway "
            + "child and wait for the kernel to kill it. Off by default: they load the machine and "
            + "depend on the enclosing rlimit/cgroup setup.");

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

        // The header alone is emitted for ANY completed outcome, whatever the exit code, so asserting
        // it proved only that a process ran — not that the ulimit prelude the real
        // ProcessResourceLimiter wraps around the script is well-formed enough for the target to exec.
        // A broken prelude (a dropped `* 1024L` in the -v clause, a mangled `exec "$@"`) left the shell
        // exiting non-zero with nothing on stdout, and this test still passed. Assert the effect.
        Assert.Contains("--- exit code ---\n0", result, StringComparison.Ordinal);

        Assert.Contains("ok", result, StringComparison.Ordinal);

        Assert.Empty(guard.RecordedBreaches);

    }

    [SkippableFact]
    public async Task Process_exceeding_cpu_limit_is_terminated_and_breached()
    {
        SkipUnlessEnforcementOptIn();

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

    [SkippableFact]
    public async Task Process_exceeding_memory_limit_is_terminated_and_breached()
    {
        SkipUnlessEnforcementOptIn();

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
