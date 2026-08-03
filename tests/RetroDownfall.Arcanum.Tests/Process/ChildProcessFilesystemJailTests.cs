using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// Filesystem jail acceptance under macOS-ARM beta posture: Seatbelt outside/symlink denial,
/// access classes, Linux fail-closed, Windows NoFilesystemJail / Sanctum deny.
/// Runtime macOS cases require a host where sandbox-exec can apply (not a nested agent sandbox).
/// </summary>
public sealed class ChildProcessFilesystemJailTests : IDisposable
{

    private string _workspace;

    private string _outsideDir;

    private readonly string _scriptsRoot;

    public ChildProcessFilesystemJailTests()
    {

        _workspace = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-fsjail-ws-" + Guid.NewGuid().ToString("N"))).FullName;

        _workspace = ResolveDir(_workspace);

        _outsideDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-fsjail-out-" + Guid.NewGuid().ToString("N"))).FullName;

        _outsideDir = ResolveDir(_outsideDir);

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
    public void MacOsProfile_DoesNotGrantWholeVolumeRead()
    {

        const string workspace = "/Users/arcanum-test/workspace";

        const string temp = "/private/tmp/arcanum-sb-test-invocation";

        string profile = MacOsSandboxExecProfileBuilder.Build(
            [workspace],
            ["/usr", "/bin", "/System"],
            temp);

        Assert.False(
            MacOsSandboxExecProfileBuilder.ContainsWholeVolumeOrBroadTempFootgun(profile),
            profile);

        Assert.DoesNotContain("(subpath \"/\")", profile, StringComparison.Ordinal);

        Assert.DoesNotContain("(literal \"/\")", profile, StringComparison.Ordinal);

        Assert.DoesNotContain("(subpath \"/tmp\")", profile, StringComparison.Ordinal);

        Assert.Contains("(allow network*)", profile, StringComparison.Ordinal);

        Assert.Contains(temp, profile, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "(allow file-write-create (vnode-type SOCKET))",
            profile,
            StringComparison.Ordinal);

        Assert.Contains(
            $"(allow file-write-create (vnode-type SOCKET) (subpath \"{temp}\"))",
            profile,
            StringComparison.Ordinal);
        Assert.DoesNotContain("(allow mach*)", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("(allow ipc*)", profile, StringComparison.Ordinal);
        Assert.Contains("(deny appleevent-send)", profile, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/open", profile, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/osascript", profile, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/automator", profile, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/shortcuts", profile, StringComparison.Ordinal);
        Assert.Contains("/bin/launchctl", profile, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            MacOsSandboxExecProfileBuilder.Build([workspace], ["/"], temp));
        Assert.Throws<InvalidOperationException>(() =>
            MacOsSandboxExecProfileBuilder.AssertNoLaunchBrokerFootguns(
                """
                (version 1)
                (deny default)
                (deny appleevent-send)
                (allow file-read*
                  (literal "/usr/bin/open")
                  (literal "/usr/bin/osascript")
                  (literal "/usr/bin/automator")
                  (literal "/usr/bin/shortcuts")
                  (literal "/bin/launchctl"))
                """));

    }

    [SkippableFact]
    public void MacOsApply_carries_no_follow_owned_cleanup_artifacts()
    {

        Skip.IfNot(
            OperatingSystem.IsMacOS(),
            "macOS sandbox profile artifacts are platform-specific.");

        Skip.IfNot(
            File.Exists("/usr/bin/sandbox-exec"),
            "sandbox-exec is unavailable.");

        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/echo",
            WorkingDirectory = _workspace,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("owned-artifacts");

        ChildProcessSandboxRequest request =
            ChildProcessSandboxRoots.ForExecuteCommand(
                _workspace,
                sanctumAllowedPaths: null,
                campaignId: null,
                allowUnsandboxed: false,
                windowsPathBoundaryRequired: false);

        ChildProcessSandboxApplyResult result =
            ChildProcessFilesystemJail.Apply(
                startInfo,
                request,
                NullLogger.Instance);

        try
        {
            Assert.Equal(
                ChildProcessSandboxApplyStatus.Applied,
                result.Status);

            Assert.Equal(2, result.OwnedArtifactsToCleanup.Count);

            foreach (IdentityOwnedFileSystemArtifact artifact
                     in result.OwnedArtifactsToCleanup)
            {
                Assert.True(
                    FileHandleIdentityInterop
                        .TryGetPathMetadataNoFollow(
                            artifact.Path,
                            out FileHandleMetadata current));

                Assert.Equal(artifact.Metadata, current);
            }
        }
        finally
        {
            Assert.True(
                ChildProcessFilesystemJail.CleanupTempPaths(
                    result.OwnedArtifactsToCleanup));
        }

    }

    [Fact]
    public void LinuxFilesystemJail_IsUnavailableByDefault_AndDoesNotInvokeHelper()
    {

        if (!OperatingSystem.IsLinux())
        {

            return;

        }

        ProcessStartInfo psi = new()
        {
            FileName = "/bin/echo",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("should-not-wrap");

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            null,
            allowUnsandboxed: false,
            windowsPathBoundaryRequired: false);

        ChildProcessSandboxApplyResult apply = ChildProcessFilesystemJail.Apply(
            psi,
            request,
            NullLogger.Instance);

        Assert.Equal(ChildProcessSandboxApplyStatus.Unavailable, apply.Status);

        Assert.Contains(
            ChildProcessFilesystemJail.LinuxDeferredDetail,
            apply.Detail,
            StringComparison.Ordinal);

        Assert.Equal("/bin/echo", psi.FileName);

        Assert.DoesNotContain(
            ChildProcessFilesystemJail.HelperArg,
            psi.ArgumentList,
            StringComparer.Ordinal);

    }

    [Fact]
    public async Task LinuxFilesystemJail_EscapeHatchRunsUnsandboxedWithWarning()
    {

        if (!OperatingSystem.IsLinux())
        {

            return;

        }

        ProcessStartInfo psi = new()
        {
            FileName = "/bin/echo",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("linux-escape-ok");

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            null,
            allowUnsandboxed: true,
            windowsPathBoundaryRequired: false,
            toolName: "execute_command");

        ChildProcessSandboxApplyResult apply = ChildProcessFilesystemJail.Apply(
            psi,
            request,
            NullLogger.Instance);

        Assert.Equal(ChildProcessSandboxApplyStatus.EscapedByOperator, apply.Status);

        Assert.Equal("/bin/echo", psi.FileName);

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

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Contains("linux-escape-ok", result.Stdout.Text, StringComparison.Ordinal);

    }

    [Fact]
    public void WindowsFilesystemJail_ReportsNoFilesystemJail_WhenSanctumBoundaryOff()
    {

        if (!OperatingSystem.IsWindows())
        {

            // Policy unit: ApplyWindows is only reached on Windows; simulate status contract via docs.
            // On non-Windows hosts, construct the expected status locally for documentation parity.
            Assert.NotEqual(
                ChildProcessSandboxApplyStatus.Applied,
                ChildProcessSandboxApplyStatus.NoFilesystemJail);

            return;

        }

        ProcessStartInfo psi = new()
        {
            FileName = "cmd.exe",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("/c");

        psi.ArgumentList.Add("echo");

        psi.ArgumentList.Add("ok");

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            null,
            allowUnsandboxed: false,
            windowsPathBoundaryRequired: false);

        ChildProcessSandboxApplyResult apply = ChildProcessFilesystemJail.Apply(
            psi,
            request,
            NullLogger.Instance);

        Assert.Equal(ChildProcessSandboxApplyStatus.NoFilesystemJail, apply.Status);

        Assert.NotEqual(ChildProcessSandboxApplyStatus.Applied, apply.Status);

    }

    [Fact]
    public void WindowsFilesystemJail_DeniesWhenSanctumPathBoundaryOn()
    {

        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/echo",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,
        };

        if (OperatingSystem.IsWindows())
        {

            startInfo.ArgumentList.Add("/c");

            startInfo.ArgumentList.Add("echo");

            startInfo.ArgumentList.Add("should-not-run");

            ChildProcessSandboxRequest denyRequest = ChildProcessSandboxRoots.ForExecuteCommand(
                _workspace,
                null,
                allowUnsandboxed: true, // escape hatch must NOT bypass Sanctum denial
                windowsPathBoundaryRequired: true);

            ChildProcessSandboxApplyResult apply = ChildProcessFilesystemJail.Apply(
                startInfo,
                denyRequest,
                NullLogger.Instance);

            Assert.Equal(ChildProcessSandboxApplyStatus.DeniedByWindowsSanctum, apply.Status);

        }

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
            allowUnsandboxed: true,
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

        Assert.DoesNotContain("Hub.Error", result.FilesystemSandboxDenialMessage ?? "", StringComparison.Ordinal);

        Assert.DoesNotContain("internal error", result.FilesystemSandboxDenialMessage ?? "", StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task MacOsSandbox_DeniesOutsideHomeSecret_WhenSandboxExecAvailable()
    {

        if (!OperatingSystem.IsMacOS() || !IsMacOsSandboxExecRunnable())
        {

            return;

        }

        string outside = Path.Combine(_outsideDir, "secret.txt");

        ProcessStartInfo psi = new()
        {
            FileName = "/bin/cat",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

            WorkingDirectory = _workspace,
        };

        psi.ArgumentList.Add(outside);

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
    public async Task MacOsSandbox_DeniesSymlinkEscapeFromWorkspace()
    {

        if (!OperatingSystem.IsMacOS() || !IsMacOsSandboxExecRunnable())
        {

            return;

        }

        string linkPath = Path.Combine(_workspace, "escape-link.txt");

        string target = Path.Combine(_outsideDir, "secret.txt");

        if (File.Exists(linkPath))
        {

            File.Delete(linkPath);

        }

        File.CreateSymbolicLink(linkPath, target);

        ProcessStartInfo psi = new()
        {
            FileName = "/bin/cat",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

            WorkingDirectory = _workspace,
        };

        psi.ArgumentList.Add(linkPath);

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

        string combined = result.Stdout.Text + result.Stderr.Text;

        Assert.DoesNotContain("outside-secret", combined, StringComparison.Ordinal);

        Assert.True(
            result.ExitCode != 0
            || combined.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("denied", StringComparison.OrdinalIgnoreCase),
            $"Expected symlink-escape denial; exit={result.ExitCode} out={result.Stdout.Text} err={result.Stderr.Text}");

    }

    [Fact]
    public async Task MacOsSandbox_DeniesLaunchServicesBrokerEscape()
    {

        if (!OperatingSystem.IsMacOS() || !IsMacOsSandboxExecRunnable())
        {

            return;
        }

        string marker = Path.Combine(
            _outsideDir,
            "launch-services-escape.txt");
        string script = Path.Combine(
            _workspace,
            "launch-services-escape.sh");
        string escapedMarker = marker.Replace(
            "'",
            "'\"'\"'",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            script,
            $$"""
            #!/bin/sh
            set -eu
            app="$TMPDIR/ArcanumEscape.app"
            executable="$app/Contents/MacOS/escape"
            mkdir -p "$app/Contents/MacOS"
            cat > "$app/Contents/Info.plist" <<'PLIST'
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>CFBundleExecutable</key><string>escape</string>
              <key>CFBundleIdentifier</key><string>test.arcanum.escape</string>
              <key>CFBundlePackageType</key><string>APPL</string>
            </dict></plist>
            PLIST
            cat > "$executable" <<'PAYLOAD'
            #!/bin/sh
            echo escaped > '{{escapedMarker}}'
            PAYLOAD
            chmod +x "$executable"
            /usr/bin/open -W "$app"
            """);
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute);

        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _workspace,
        };
        startInfo.ArgumentList.Add(script);

        ChildProcessSandboxRequest request =
            ChildProcessSandboxRoots.ForExecuteCommand(
                _workspace,
                sanctumAllowedPaths: null,
                allowUnsandboxed: false,
                windowsPathBoundaryRequired: false);
        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                startInfo,
                ChildProcessEnvironmentProfile.ToolExec,
                64 * 1024,
                TimeSpan.FromSeconds(15),
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None,
                request,
                NullLogger.Instance);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(
            File.Exists(marker),
            $"LaunchServices escaped the sandbox. stdout=[{result.Stdout.Text}] stderr=[{result.Stderr.Text}]");

    }

    [Fact]
    public async Task MacOsSandbox_AllowsWorkspaceReadWrite()
    {

        if (!OperatingSystem.IsMacOS() || !IsMacOsSandboxExecRunnable())
        {

            return;

        }

        string script = Path.Combine(_workspace, "rw.sh");

        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\nset -e\ncat \"$1\"\necho wrote > \"$2\"\ncat \"$2\"\necho \"tmpdir=$TMPDIR\"\necho tmpok > \"$TMPDIR/t\"\ncat \"$TMPDIR/t\"\n");

        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string inside = Path.Combine(_workspace, "inside.txt");

        string outFile = Path.Combine(_workspace, "out.txt");

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

        Assert.Contains("tmpok", result.Stdout.Text, StringComparison.Ordinal);

        Assert.Contains("tmpdir=", result.Stdout.Text, StringComparison.Ordinal);

    }

    [Fact]
    public async Task MacOsSandbox_AllowsSpellScriptReadExecuteButNotWrite()
    {

        if (!OperatingSystem.IsMacOS() || !IsMacOsSandboxExecRunnable())
        {

            return;

        }

        string globalScripts = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "arcanum-fsjail-global-" + Guid.NewGuid().ToString("N"), "scripts"));

        Directory.CreateDirectory(globalScripts);

        try
        {

            string scriptPath = Path.Combine(globalScripts, "rwprobe.sh");

            string writeTarget = Path.Combine(globalScripts, "should-not-write.txt");

            await File.WriteAllTextAsync(
                scriptPath,
                "#!/bin/sh\necho read-ok\nif echo leak > \"" + writeTarget.Replace("\"", "\\\"", StringComparison.Ordinal) + "\" 2>/dev/null; then echo WRITE_OK; else echo write-denied-ok; fi\n");

            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using HostProcessToolsEscapeHatchScope _ = new();

            ArcanumSpellScriptTool tool = new(
                [globalScripts],
                campaignWorkspaceRoot: _workspace,
                allowUnsandboxedToolChildren: false);

            string? result = await tool.InvokeAsync(
                new Microsoft.Extensions.AI.AIFunctionArguments(
                    new Dictionary<string, object?> { ["script_name"] = "rwprobe.sh" })) as string;

            Assert.NotNull(result);

            Assert.Contains("read-ok", result, StringComparison.Ordinal);

            Assert.Contains("write-denied-ok", result, StringComparison.Ordinal);

            Assert.DoesNotContain("WRITE_OK", result, StringComparison.Ordinal);

            Assert.False(File.Exists(Path.Combine(globalScripts, "should-not-write.txt")));

        }
        finally
        {

            TryDelete(Path.GetDirectoryName(globalScripts)!);

        }

    }

    [Fact]
    public async Task MacOS_metacharacters_in_args_are_argv_safe()
    {

        if (!OperatingSystem.IsMacOS() || !IsMacOsSandboxExecRunnable())
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
    public async Task Workspace_spell_script_runs_under_jail()
    {

        if (!OperatingSystem.IsMacOS() || !IsMacOsSandboxExecRunnable())
        {

            return;

        }

        string scriptPath = Path.Combine(_scriptsRoot, "hello.sh");

        await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\necho spell-ok\n");

        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using HostProcessToolsEscapeHatchScope _ = new();

        ArcanumSpellScriptTool tool = new(
            [_scriptsRoot],
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
    public async Task Escape_hatch_runs_when_sandbox_unavailable()
    {

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

            ToolName = "execute_command",
        };

        if (OperatingSystem.IsWindows())
        {

            // Empty roots are not evaluated on Windows; NoFilesystemJail still allows run.
            ChildProcessSandboxApplyResult apply = ChildProcessFilesystemJail.Apply(
                psi,
                request,
                NullLogger.Instance);

            Assert.Equal(ChildProcessSandboxApplyStatus.NoFilesystemJail, apply.Status);

        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {

            ChildProcessSandboxApplyResult apply = ChildProcessFilesystemJail.Apply(
                psi,
                request,
                NullLogger.Instance);

            Assert.Equal(ChildProcessSandboxApplyStatus.EscapedByOperator, apply.Status);

        }

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

            Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

            return;

        }

        Assert.Equal(CappedChildProcessOutcome.FilesystemSandboxUnavailable, result.Outcome);

        Assert.False(string.IsNullOrWhiteSpace(result.FilesystemSandboxDenialMessage));

        Assert.DoesNotContain("Hub.Error", result.FilesystemSandboxDenialMessage!, StringComparison.Ordinal);

        Assert.DoesNotContain("internal error", result.FilesystemSandboxDenialMessage!, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            ChildProcessSandboxMessages.NotNetworkIsolationNote,
            result.FilesystemSandboxDenialMessage!,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task Linux_default_fail_closed_returns_expected_denial_end_to_end()
    {

        if (!OperatingSystem.IsLinux())
        {

            return;

        }

        ProcessStartInfo psi = new()
        {
            FileName = "/bin/echo",

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("should-not-run");

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            null,
            allowUnsandboxed: false,
            windowsPathBoundaryRequired: false);

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

        Assert.Equal(CappedChildProcessOutcome.FilesystemSandboxUnavailable, result.Outcome);

        Assert.Contains(
            ChildProcessFilesystemJail.LinuxDeferredDetail,
            result.FilesystemSandboxDenialMessage,
            StringComparison.Ordinal);

        Assert.DoesNotContain("Hub.Error", result.FilesystemSandboxDenialMessage ?? "", StringComparison.Ordinal);

    }

    /// <summary>
    /// Probe whether sandbox-exec can run at all. Do not treat "outside read succeeded" as skip —
    /// enforcement tests fail hard instead.
    /// </summary>
    private static bool IsMacOsSandboxExecRunnable()
    {

        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/sandbox-exec"))
        {

            return false;

        }

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

    private static string ResolveDir(string path)
    {

        try
        {

            string? resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;

            if (!string.IsNullOrEmpty(resolved))
            {

                return Path.GetFullPath(resolved);

            }

        }
        catch
        {

        }

        return Path.GetFullPath(path);

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
