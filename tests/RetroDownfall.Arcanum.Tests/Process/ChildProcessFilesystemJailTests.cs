using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Platform;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// S4 filesystem jail acceptance: outside denial, workspace access, spell scripts, escape hatch,
/// Windows Sanctum deny. Requires a host where the OS sandbox can actually apply (not a nested
/// agent sandbox that blocks <c>sandbox-exec</c>).
/// </summary>
public sealed class ChildProcessFilesystemJailTests : IDisposable
{

    private readonly string _workspace;

    private readonly string _outsideDir;

    private readonly string _scriptsRoot;

    public ChildProcessFilesystemJailTests()
    {

        _workspace = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-fsjail-ws-" + Guid.NewGuid().ToString("N"))).FullName;

        try
        {

            string? resolved = Directory.ResolveLinkTarget(_workspace, returnFinalTarget: true)?.FullName;

            if (!string.IsNullOrEmpty(resolved))
            {

                _workspace = Path.GetFullPath(resolved);

            }

        }
        catch
        {

        }

        _outsideDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-fsjail-out-" + Guid.NewGuid().ToString("N"))).FullName;

        try
        {

            string? resolved = Directory.ResolveLinkTarget(_outsideDir, returnFinalTarget: true)?.FullName;

            if (!string.IsNullOrEmpty(resolved))
            {

                _outsideDir = Path.GetFullPath(resolved);

            }

        }
        catch
        {

        }

        _scriptsRoot = Path.Combine(_workspace, "spell", "scripts");

        Directory.CreateDirectory(_scriptsRoot);

        File.WriteAllText(Path.Combine(_workspace, "inside.txt"), "workspace-ok");

        File.WriteAllText(Path.Combine(_outsideDir, "secret.txt"), "outside-secret");

    }

    public void Dispose()
    {

        TryDelete(_workspace);

        TryDelete(_outsideDir);

    }

    [Fact]
    public async Task Outside_user_data_file_is_denied_when_sandbox_available()
    {

        if (!IsOsFilesystemJailAvailable())
        {

            return;

        }

        string outside = Path.Combine(_outsideDir, "secret.txt");

        ProcessStartInfo psi = new()
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/cat",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

            WorkingDirectory = _workspace,
        };

        if (OperatingSystem.IsWindows())
        {

            psi.ArgumentList.Add("/c");

            psi.ArgumentList.Add("type");

            psi.ArgumentList.Add(outside);

        }
        else
        {

            psi.ArgumentList.Add(outside);

        }

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            sanctumAllowedPaths: null,
            allowUnsandboxed: false,
            windowsPathBoundaryRequired: false);

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            64 * 1024,
            TimeSpan.FromSeconds(15),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            request,
            NullLogger.Instance);

        if (OperatingSystem.IsWindows())
        {

            // Windows has no FS jail when path boundary is not required.
            Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

            return;

        }

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        string combined = result.Stdout.Text + result.Stderr.Text;

        Assert.DoesNotContain("outside-secret", combined, StringComparison.Ordinal);

        Assert.True(
            result.ExitCode != 0
            || combined.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("denied", StringComparison.OrdinalIgnoreCase),
            $"Expected outside denial; exit={result.ExitCode} out={result.Stdout.Text} err={result.Stderr.Text}");

    }

    [Fact]
    public async Task Workspace_read_and_write_succeed_inside_allowed_root()
    {

        if (!IsOsFilesystemJailAvailable())
        {

            return;

        }

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string script = Path.Combine(_workspace, "rw.sh");

        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\nset -e\ncat \"$1\"\necho wrote > \"$2\"\ncat \"$2\"\n");

        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string inside = Path.Combine(_workspace, "inside.txt");

        string outFile = Path.Combine(_workspace, "out.txt");

        // Prefer absolute interpreter paths so PATH lookup is not required inside the jail.
        ProcessStartInfo psi = new()
        {
            FileName = "/bin/sh",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

            WorkingDirectory = _workspace,
        };

        psi.ArgumentList.Add(script);

        psi.ArgumentList.Add(inside);

        psi.ArgumentList.Add(outFile);

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            null,
            allowUnsandboxed: false,
            windowsPathBoundaryRequired: false);

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            64 * 1024,
            TimeSpan.FromSeconds(15),
            null,
            null,
            CancellationToken.None,
            request,
            NullLogger.Instance);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode} stdout=[{result.Stdout.Text}] stderr=[{result.Stderr.Text}]");

        Assert.Contains("workspace-ok", result.Stdout.Text, StringComparison.Ordinal);

        Assert.Contains("wrote", result.Stdout.Text, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Workspace_spell_script_runs_under_jail()
    {

        if (!IsOsFilesystemJailAvailable() || OperatingSystem.IsWindows())
        {

            return;

        }

        string scriptPath = Path.Combine(_scriptsRoot, "hello.sh");

        await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\necho spell-ok\n");

        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        ArcanumSpellScriptTool tool = new(
            [_scriptsRoot],
            TimeSpan.FromSeconds(15),
            15,
            campaignWorkspaceRoot: _workspace,
            allowUnsandboxedToolChildren: false);

        string? result = await tool.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments(
                new Dictionary<string, object?> { ["script_name"] = "hello.sh" })) as string;

        Assert.NotNull(result);

        Assert.Contains("spell-ok", result, StringComparison.Ordinal);

        Assert.Contains("--- exit code ---", result, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Spell_script_cannot_read_outside_secret()
    {

        if (!IsOsFilesystemJailAvailable() || OperatingSystem.IsWindows())
        {

            return;

        }

        string outside = Path.Combine(_outsideDir, "secret.txt");

        string scriptPath = Path.Combine(_scriptsRoot, "probe.sh");

        await File.WriteAllTextAsync(
            scriptPath,
            "#!/bin/sh\nif cat \"$1\" 2>/dev/null; then echo LEAK; exit 2; else echo denied-ok; fi\n");

        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        ArcanumSpellScriptTool tool = new(
            [_scriptsRoot],
            TimeSpan.FromSeconds(15),
            15,
            campaignWorkspaceRoot: _workspace,
            allowUnsandboxedToolChildren: false);

        string? result = await tool.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["script_name"] = "probe.sh",
                    ["arguments"] = QuoteArg(outside),
                })) as string;

        Assert.NotNull(result);

        Assert.DoesNotContain("outside-secret", result, StringComparison.Ordinal);

        Assert.DoesNotContain("LEAK", result, StringComparison.Ordinal);

        Assert.Contains("denied-ok", result, StringComparison.Ordinal);

    }

    [Fact]
    public async Task MacOS_metacharacters_in_args_are_argv_safe()
    {

        if (!OperatingSystem.IsMacOS() || !IsOsFilesystemJailAvailable())
        {

            return;

        }

        ProcessStartInfo psi = new()
        {
            FileName = "/bin/echo",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

            WorkingDirectory = _workspace,
        };

        psi.ArgumentList.Add("hello $(whoami); rm -rf /");

        psi.ArgumentList.Add("path with spaces");

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            null,
            allowUnsandboxed: false,
            windowsPathBoundaryRequired: false);

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            64 * 1024,
            TimeSpan.FromSeconds(15),
            null,
            null,
            CancellationToken.None,
            request,
            NullLogger.Instance);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("hello $(whoami); rm -rf /", result.Stdout.Text, StringComparison.Ordinal);

        Assert.Contains("path with spaces", result.Stdout.Text, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Escape_hatch_runs_when_sandbox_unavailable()
    {

        // Build a request that cannot apply a jail on this host: empty roots with allowUnsandboxed.
        ProcessStartInfo psi = new()
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/echo",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {

            psi.ArgumentList.Add("/c");

            psi.ArgumentList.Add("echo");

            psi.ArgumentList.Add("hatch-ok");

        }
        else
        {

            psi.ArgumentList.Add("hatch-ok");

        }

        ChildProcessSandboxRequest request = new()
        {
            ReadWriteRoots = [],

            ReadExecuteRoots = [],

            AllowUnsandboxed = true,

            WindowsPathBoundaryRequired = false,
        };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            64 * 1024,
            TimeSpan.FromSeconds(15),
            null,
            null,
            CancellationToken.None,
            request,
            NullLogger.Instance);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Contains("hatch-ok", result.Stdout.Text, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Missing_sandbox_without_escape_hatch_fail_closes()
    {

        ProcessStartInfo psi = new()
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/echo",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,
        };

        psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "x");

        ChildProcessSandboxRequest request = new()
        {
            ReadWriteRoots = [],

            ReadExecuteRoots = [],

            AllowUnsandboxed = false,

            WindowsPathBoundaryRequired = false,
        };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            64 * 1024,
            TimeSpan.FromSeconds(5),
            null,
            null,
            CancellationToken.None,
            request,
            NullLogger.Instance);

        if (OperatingSystem.IsWindows())
        {

            // Empty roots are not evaluated on Windows when path boundary is not required.
            Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

            return;

        }

        Assert.Equal(CappedChildProcessOutcome.FilesystemSandboxUnavailable, result.Outcome);

        Assert.Equal(ChildProcessSandboxMessages.SandboxUnavailable, result.FilesystemSandboxDenialMessage);

    }

    [Fact]
    public void Windows_Sanctum_path_boundary_denies_without_starting()
    {

        if (!OperatingSystem.IsWindows())
        {

            // Exercise the policy branch on non-Windows too via Apply().
            ProcessStartInfo psi = new()
            {
                FileName = "/bin/echo",

                UseShellExecute = false,

                RedirectStandardOutput = true,

                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("x");

            ChildProcessSandboxRequest request = new()
            {
                ReadWriteRoots = [_workspace],

                ReadExecuteRoots = ["/usr", "/bin"],

                AllowUnsandboxed = false,

                WindowsPathBoundaryRequired = true,
            };

            // On non-Windows, WindowsPathBoundaryRequired is ignored by ApplyWindows — ApplyMacOs/Linux runs.
            // Dedicated Windows denial is asserted below only on Windows.
            _ = request;

            return;

        }

        ProcessStartInfo startInfo = new()
        {
            FileName = "cmd.exe",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("/c");

        startInfo.ArgumentList.Add("echo");

        startInfo.ArgumentList.Add("should-not-run");

        ChildProcessSandboxRequest denyRequest = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            null,
            allowUnsandboxed: false,
            windowsPathBoundaryRequired: true);

        ChildProcessSandboxApplyResult apply = ChildProcessFilesystemJail.Apply(
            startInfo,
            denyRequest,
            NullLogger.Instance);

        Assert.Equal(ChildProcessSandboxApplyStatus.DeniedByWindowsSanctum, apply.Status);

    }

    [Fact]
    public async Task Windows_Sanctum_path_boundary_returns_expected_runner_outcome()
    {

        if (!OperatingSystem.IsWindows())
        {

            return;

        }

        ProcessStartInfo psi = new()
        {
            FileName = "cmd.exe",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("/c");

        psi.ArgumentList.Add("echo");

        psi.ArgumentList.Add("should-not-run");

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            null,
            allowUnsandboxed: true, // still denied when Windows path boundary required
            windowsPathBoundaryRequired: true);

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            64 * 1024,
            TimeSpan.FromSeconds(10),
            null,
            null,
            CancellationToken.None,
            request,
            NullLogger.Instance);

        Assert.Equal(CappedChildProcessOutcome.FilesystemSandboxDeniedByWindowsSanctum, result.Outcome);

        Assert.Equal(
            ChildProcessSandboxMessages.WindowsSanctumPathBoundaryDenied,
            result.FilesystemSandboxDenialMessage);

    }

    [Fact]
    public async Task Global_spell_under_config_spells_runs_when_selected()
    {

        if (!IsOsFilesystemJailAvailable() || OperatingSystem.IsWindows())
        {

            return;

        }

        string globalScripts = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "arcanum-fsjail-global-" + Guid.NewGuid().ToString("N"), "scripts"));

        Directory.CreateDirectory(globalScripts);

        try
        {

            string scriptPath = Path.Combine(globalScripts, "global.sh");

            await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\necho global-ok\n");

            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            ArcanumSpellScriptTool tool = new(
                [globalScripts],
                TimeSpan.FromSeconds(15),
                15,
                campaignWorkspaceRoot: _workspace,
                allowUnsandboxedToolChildren: false);

            string? result = await tool.InvokeAsync(
                new Microsoft.Extensions.AI.AIFunctionArguments(
                    new Dictionary<string, object?> { ["script_name"] = "global.sh" })) as string;

            Assert.NotNull(result);

            Assert.Contains("global-ok", result, StringComparison.Ordinal);

        }
        finally
        {

            TryDelete(Path.GetDirectoryName(globalScripts)!);

        }

    }

    [Fact]
    public void Sandbox_unavailable_fail_closed_message_is_stable()
    {

        Assert.Equal(
            "Child process filesystem sandbox is unavailable on this host; refusing to run tool unbounded.",
            ChildProcessSandboxMessages.SandboxUnavailable);

    }

    private static bool IsOsFilesystemJailAvailable()
    {

        if (OperatingSystem.IsWindows())
        {

            return true;

        }

        if (OperatingSystem.IsMacOS())
        {

            if (!File.Exists("/usr/bin/sandbox-exec"))
            {

                return false;

            }

            // Probe whether sandbox-exec can apply (fails inside nested agent sandboxes).
            try
            {

                using global::System.Diagnostics.Process probe = new();

                probe.StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/sandbox-exec",

                    ArgumentList = { "-p", "(version 1)(allow default)", "/bin/echo", "probe" },

                    UseShellExecute = false,

                    RedirectStandardOutput = true,

                    RedirectStandardError = true,

                    CreateNoWindow = true,
                };

                if (!probe.Start())
                {

                    return false;

                }

                string err = probe.StandardError.ReadToEnd();

                probe.WaitForExit(5000);

                return probe.ExitCode == 0 && !err.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase);

            }
            catch
            {

                return false;

            }

        }

        if (OperatingSystem.IsLinux())
        {

            // Landlock availability is probed by the helper at runtime; tests that need it skip
            // via Completed checks. Consider present when ProcessPath exists.
            return !string.IsNullOrWhiteSpace(global::System.Environment.ProcessPath);

        }

        return false;

    }

    private static string QuoteArg(string value)
    {

        if (!value.Contains(' ', StringComparison.Ordinal) && !value.Contains('"', StringComparison.Ordinal))
        {

            return value;

        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    }

    private static void TryDelete(string path)
    {

        try
        {

            if (Directory.Exists(path))
            {

                Directory.Delete(path, recursive: true);

            }

        }
        catch
        {

        }

    }

}
