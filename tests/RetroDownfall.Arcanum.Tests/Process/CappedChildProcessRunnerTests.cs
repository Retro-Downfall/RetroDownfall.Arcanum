using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.ProcessExecution;

public sealed class CappedChildProcessRunnerTests
{

    private const string SentinelToken = "ARCANUM_RUNNER_TEST";

    [Fact]
    public async Task RunAsync_harmless_echo_returns_exit_code_zero()
    {

        ProcessStartInfo psi = CreateHarmlessEchoProcessStartInfo();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(30),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Contains(SentinelToken, result.Stdout.Text, StringComparison.Ordinal);

        Assert.Equal(0, result.ExitCode);

    }

    [Fact]
    public async Task RunAsync_truncates_stdout_when_exceeding_per_stream_cap()
    {

        ProcessStartInfo psi = CreateLargeOutputProcessStartInfo(payloadCharCount: 5000);

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 2048,
            timeout: TimeSpan.FromSeconds(30),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.True(result.Stdout.Truncated);

        Assert.Equal(1024L, result.PerStreamCapBytes);

    }

    [Fact]
    public async Task RunAsync_drains_output_past_cap_so_chatty_process_still_exits_promptly()
    {

        // A payload well beyond a typical OS pipe buffer (a few tens of KB): without continuing to
        // drain the pipe after the cap is hit, the child would block on its next write() once the
        // kernel pipe buffer fills, and RunAsync would only unblock via the timeout below — this
        // test's timeout is intentionally short so a regression here fails fast instead of hanging.
        ProcessStartInfo psi = CreateLargeOutputProcessStartInfo(payloadCharCount: 5_000_000);

        Stopwatch stopwatch = Stopwatch.StartNew();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 2048,
            timeout: TimeSpan.FromSeconds(30),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        stopwatch.Stop();

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.True(result.Stdout.Truncated);

        // Well under the timeout — proves the child exited on its own because the pipe kept
        // draining, rather than RunAsync only unblocking once the timeout killed the process tree.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Expected a prompt exit; took {stopwatch.Elapsed}.");

    }

    [Fact]
    public async Task RunAsync_times_out_and_kills_long_running_process()
    {

        ProcessStartInfo psi = CreateSleepProcessStartInfo(seconds: 60);

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromMilliseconds(750),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.TimedOut, result.Outcome);

    }

    [SkippableFact]
    public void Unix_process_group_supervisor_without_setsid_uses_shell_fallback()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux-specific launch construction.");
        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/echo",
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("hello");

        bool directGroup = UnixProcessGroupSupervisor.Apply(
            startInfo,
            static () => null);

        Assert.False(directGroup);
        Assert.Equal("/bin/sh", startInfo.FileName);
        Assert.Equal("-c", startInfo.ArgumentList[0]);
        Assert.Contains("set -m", startInfo.ArgumentList[1], StringComparison.Ordinal);
        Assert.Equal("arcanum-process-group", startInfo.ArgumentList[2]);
        Assert.Equal("/bin/echo", startInfo.ArgumentList[3]);
        Assert.Equal("-n", startInfo.ArgumentList[4]);
        Assert.Equal("hello", startInfo.ArgumentList[5]);
    }

    [SkippableFact]
    public void Unix_process_group_supervisor_with_setsid_preserves_argv()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux-specific launch construction.");
        ProcessStartInfo startInfo = new()
        {
            FileName = "-option-shaped-command",
        };
        startInfo.ArgumentList.Add("argument with spaces");

        bool directGroup = UnixProcessGroupSupervisor.Apply(
            startInfo,
            static () => "/usr/bin/setsid");

        Assert.True(directGroup);
        Assert.Equal("/usr/bin/setsid", startInfo.FileName);
        Assert.Equal(
            ["--", "-option-shaped-command", "argument with spaces"],
            startInfo.ArgumentList);
    }

    [SkippableFact]
    public async Task RunAsync_kills_process_group_descendants_after_normal_parent_exit()
    {

        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix process-group cleanup is covered on Unix hosts.");
        ProcessStartInfo psi = new()
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 60 </dev/null >/dev/null 2>&1 & echo $!; exit 0");

        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);
        int descendantPid = int.Parse(
            result.Stdout.Text.Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(200);

        try
        {

            using global::System.Diagnostics.Process descendant =
                global::System.Diagnostics.Process.GetProcessById(descendantPid);
            Assert.True(
                await WaitForProcessExitOrZombieAsync(
                    descendantPid,
                    TimeSpan.FromSeconds(2)),
                $"Detached descendant {descendantPid} survived normal parent exit.");

        }
        catch (ArgumentException)
        {

            // Process no longer exists.

        }
        finally
        {

            try
            {

                using global::System.Diagnostics.Process descendant =
                    global::System.Diagnostics.Process.GetProcessById(descendantPid);
                descendant.Kill(entireProcessTree: true);

            }
            catch (Exception)
            {

            }

        }

    }

    [SkippableFact]
    public async Task RunAsync_kills_process_group_descendants_on_timeout()
    {

        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix process-group cleanup is covered on Unix hosts.");
        ProcessStartInfo psi = new()
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(
            "sleep 60 </dev/null >/dev/null 2>&1 & echo $!; sleep 60");

        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 65_536,
                timeout: TimeSpan.FromMilliseconds(750),
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None);

        Assert.Equal(
            CappedChildProcessOutcome.TimedOut,
            result.Outcome);
        int descendantPid = int.Parse(
            result.Stdout.Text.Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(200);

        try
        {
            using global::System.Diagnostics.Process descendant =
                global::System.Diagnostics.Process.GetProcessById(
                    descendantPid);
            Assert.True(
                await WaitForProcessExitOrZombieAsync(
                    descendantPid,
                    TimeSpan.FromSeconds(2)),
                $"Detached descendant {descendantPid} survived timeout cleanup.");
        }
        catch (ArgumentException)
        {
            // Process no longer exists.
        }
        finally
        {
            try
            {
                using global::System.Diagnostics.Process descendant =
                    global::System.Diagnostics.Process.GetProcessById(
                        descendantPid);
                descendant.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }
    }

    [SkippableTheory]
    [InlineData("Process.setsid")]
    [InlineData("Process.setpgid(0, 0)")]
    public async Task RunAsync_kills_group_escaping_closed_pipe_descendant_after_parent_success(
        string escape)
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS(),
            "macOS descendant ancestry containment is platform-specific.");
        ProcessStartInfo psi = new()
        {
            FileName = "/usr/bin/ruby",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(
            $"fork {{ {escape}; puts Process.pid; STDOUT.flush; STDOUT.close; STDERR.close; sleep 15 }}; sleep 1.5; exit 0");

        Stopwatch stopwatch = Stopwatch.StartNew();
        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(
            CappedChildProcessOutcome.Completed,
            result.Outcome);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Containment cleanup took {stopwatch.Elapsed}.");
        int descendantPid = int.Parse(
            result.Stdout.Text.Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(200);

        try
        {
            using global::System.Diagnostics.Process descendant =
                global::System.Diagnostics.Process.GetProcessById(
                    descendantPid);
            Assert.True(
                await WaitForProcessExitOrZombieAsync(
                    descendantPid,
                    TimeSpan.FromSeconds(2)),
                $"Escaped descendant {descendantPid} survived parent success.");
        }
        catch (ArgumentException)
        {
            // Process no longer exists.
        }
        finally
        {
            try
            {
                using global::System.Diagnostics.Process descendant =
                    global::System.Diagnostics.Process.GetProcessById(
                        descendantPid);
                descendant.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public async Task RunAsync_revalidates_trusted_identity_immediately_before_spawn()
    {

        ProcessStartInfo psi = CreateHarmlessEchoProcessStartInfo();
        int validationCount = 0;

        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.WorkspaceCheck,
                totalOutputCapBytes: 65_536,
                timeout: TimeSpan.FromSeconds(30),
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None,
                preStartValidation: () =>
                {
                    validationCount++;
                    return new CappedChildProcessPreStartValidationResult(
                        false,
                        "trusted identity changed");
                });

        Assert.Equal(1, validationCount);
        Assert.Equal(
            CappedChildProcessOutcome.PreStartValidationFailed,
            result.Outcome);
        Assert.Empty(result.Stdout.Text ?? string.Empty);
    }

    [Fact]
    public async Task RunAsync_rechecks_outer_cancellation_after_prestart_validation()
    {

        ProcessStartInfo startInfo = CreateHarmlessEchoProcessStartInfo();
        using CancellationTokenSource cancellation = new();

        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                startInfo,
                ChildProcessEnvironmentProfile.WorkspaceCheck,
                totalOutputCapBytes: 65_536,
                timeout: TimeSpan.FromSeconds(30),
                resourceLimits: null,
                resourceLimiter: null,
                cancellation.Token,
                preStartValidation: () =>
                {
                    cancellation.Cancel();
                    return new CappedChildProcessPreStartValidationResult(
                        true,
                        Error: null,
                        Code: null);
                });

        Assert.Equal(
            CappedChildProcessOutcome.CanceledBeforeStart,
            result.Outcome);
        Assert.Empty(result.Stdout.Text ?? string.Empty);

    }

    [Fact]
    public void ApplyProfile_ToolExec_strips_arcanum_prefixed_keys_and_keeps_others()
    {

        ProcessStartInfo psi = new()
        {

            FileName = "noop",

        };

        psi.Environment["ARCANUM_Arcanum__Providers__0__ApiKey"] = "sk-secret";

        psi.Environment["arcanum_lower"] = "also-secret";

        psi.Environment["PATH"] = "/usr/bin";

        psi.Environment["HOME"] = "/home/user";

        ChildProcessEnvironmentScrubber.ApplyProfile(psi, ChildProcessEnvironmentProfile.ToolExec);

        Assert.False(psi.Environment.ContainsKey("ARCANUM_Arcanum__Providers__0__ApiKey"));

        Assert.False(psi.Environment.ContainsKey("arcanum_lower"));

        Assert.Equal("/usr/bin", psi.Environment["PATH"]);

        Assert.Equal("/home/user", psi.Environment["HOME"]);

    }

    [Fact]
    public void ApplyProfile_ToolExec_strips_hijackable_variables_but_preserves_path()
    {

        ProcessStartInfo psi = new()
        {

            FileName = "noop",

        };

        // Interpreter/dynamic-linker preload hooks, credential-phishing SSH/Git helpers, TLS key
        // logging, and proxy redirection — the same denylist MCP child processes are scrubbed
        // against by default (McpSecurityLimits.IsBlockedEnvironmentVariable).
        psi.Environment["LD_PRELOAD"] = "/tmp/evil.so";

        psi.Environment["NODE_OPTIONS"] = "--require /tmp/evil.js";

        psi.Environment["PYTHONPATH"] = "/tmp/evil-site-packages";

        psi.Environment["GIT_SSH_COMMAND"] = "/tmp/steal-creds.sh";

        psi.Environment["SSLKEYLOGFILE"] = "/tmp/keys.log";

        psi.Environment["HTTPS_PROXY"] = "http://attacker.example/";

        psi.Environment["PATH"] = "/usr/bin";

        psi.Environment["HOME"] = "/home/user";

        ChildProcessEnvironmentScrubber.ApplyProfile(psi, ChildProcessEnvironmentProfile.ToolExec);

        Assert.False(psi.Environment.ContainsKey("LD_PRELOAD"));

        Assert.False(psi.Environment.ContainsKey("NODE_OPTIONS"));

        Assert.False(psi.Environment.ContainsKey("PYTHONPATH"));

        Assert.False(psi.Environment.ContainsKey("GIT_SSH_COMMAND"));

        Assert.False(psi.Environment.ContainsKey("SSLKEYLOGFILE"));

        Assert.False(psi.Environment.ContainsKey("HTTPS_PROXY"));

        // PATH is deliberately preserved: execute_command's entire purpose is running arbitrary
        // shell commands that need normal PATH resolution to work at all.
        Assert.Equal("/usr/bin", psi.Environment["PATH"]);

        Assert.Equal("/home/user", psi.Environment["HOME"]);

    }

    [Fact]
    public void ApplyProfile_SpellScript_strips_arcanum_and_hijack_vars_like_ToolExec()
    {

        ProcessStartInfo psi = new()
        {

            FileName = "noop",

        };

        psi.Environment["ARCANUM_Arcanum__Providers__0__ApiKey"] = "sk-secret";

        psi.Environment["LD_PRELOAD"] = "/tmp/evil.so";

        psi.Environment["DOTNET_STARTUP_HOOKS"] = "/tmp/hook.dll";

        psi.Environment["NODE_OPTIONS"] = "--require /tmp/evil.js";

        psi.Environment["PATH"] = "/usr/bin";

        psi.Environment["HOME"] = "/home/user";

        ChildProcessEnvironmentScrubber.ApplyProfile(psi, ChildProcessEnvironmentProfile.SpellScript);

        Assert.False(psi.Environment.ContainsKey("ARCANUM_Arcanum__Providers__0__ApiKey"));

        Assert.False(psi.Environment.ContainsKey("LD_PRELOAD"));

        Assert.False(psi.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));

        Assert.False(psi.Environment.ContainsKey("NODE_OPTIONS"));

        Assert.Equal("/usr/bin", psi.Environment["PATH"]);

        Assert.Equal("/home/user", psi.Environment["HOME"]);

    }

    [Fact]
    public async Task RunAsync_SigKillExit_NotClassifiedAsMemory_WhenNoOomEvidence()
    {

        if (OperatingSystem.IsWindows())
        {

            // No POSIX signal-exit-code semantics on Windows; nothing to verify on this host.
            return;

        }

        ProcessStartInfo psi = CreateSelfSigKillProcessStartInfo();

        ResourceLimits limits = new() { MaxMemoryMb = 256 };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            resourceLimits: limits,
            resourceLimiter: new FakeResourceLimiter(wasOomKilled: false),
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Null(result.ExceededResource);

    }

    [Fact]
    public async Task RunAsync_SigKillExit_ClassifiedAsMemory_WhenOomEvidenceConfirmed()
    {

        if (OperatingSystem.IsWindows())
        {

            // No POSIX signal-exit-code semantics on Windows; nothing to verify on this host.
            return;

        }

        ProcessStartInfo psi = CreateSelfSigKillProcessStartInfo();

        ResourceLimits limits = new() { MaxMemoryMb = 256 };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            resourceLimits: limits,
            resourceLimiter: new FakeResourceLimiter(wasOomKilled: true),
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.ResourceLimitExceeded, result.Outcome);

        Assert.Equal(ResourceLimitKind.Memory, result.ExceededResource);

    }

    private static ProcessStartInfo CreateSelfSigKillProcessStartInfo() =>
        new()
        {

            FileName = "/bin/sh",

            ArgumentList = { "-c", "kill -9 $$" },

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,

        };

    /// <summary>
    /// Bypasses real OS-level enforcement entirely (Apply leaves ProcessStartInfo untouched) so
    /// tests can isolate CappedChildProcessRunner's exit-code classification logic from the actual
    /// setrlimit/cgroups mechanics covered by ProcessResourceLimiterTests.
    /// </summary>
    private sealed class FakeResourceLimiter(bool? wasOomKilled) : IProcessResourceLimiter
    {

        public ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits) =>
            new(null, null, wasOomKilled is null ? null : () => Task.FromResult(wasOomKilled.Value));

    }

    private static ProcessStartInfo CreateHarmlessEchoProcessStartInfo()
    {

        if (OperatingSystem.IsWindows())
        {

            return new ProcessStartInfo
            {

                FileName = "powershell.exe",

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

                ArgumentList = { "-NoProfile", "-Command", $"Write-Output {SentinelToken}" },

            };

        }

        return new ProcessStartInfo
        {

            FileName = "/bin/echo",

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,

            ArgumentList = { SentinelToken },

        };

    }

    private static ProcessStartInfo CreateLargeOutputProcessStartInfo(int payloadCharCount)
    {

        if (OperatingSystem.IsWindows())
        {

            return new ProcessStartInfo
            {

                FileName = "powershell.exe",

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

                ArgumentList =
                {
                    "-NoProfile",
                    "-Command",
                    $"Write-Output ('x' * {payloadCharCount})",
                },

            };

        }

        return new ProcessStartInfo
        {

            FileName = "/bin/sh",

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,

            ArgumentList = { "-c", $"printf '%*s' {payloadCharCount} | tr ' ' 'x'" },

        };

    }

    private static async Task<bool> WaitForProcessExitOrZombieAsync(
        int processId,
        TimeSpan timeout)
    {
        long deadline = global::System.Environment.TickCount64
            + (long)Math.Max(1, timeout.TotalMilliseconds);

        do
        {
            if (IsExitedOrZombie(processId))
            {
                return true;
            }

            await Task.Delay(25);
        }
        while (global::System.Environment.TickCount64 < deadline);

        return IsExitedOrZombie(processId);
    }

    private static bool IsExitedOrZombie(int processId)
    {
        if (OperatingSystem.IsLinux())
        {
            string statPath = $"/proc/{processId}/stat";
            if (File.Exists(statPath))
            {
                try
                {
                    string stat = File.ReadAllText(statPath);
                    int commandEnd = stat.LastIndexOf(')');
                    if (commandEnd >= 0
                        && commandEnd + 2 < stat.Length
                        && stat[commandEnd + 2] is 'Z' or 'X')
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        try
        {
            using global::System.Diagnostics.Process process =
                global::System.Diagnostics.Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static ProcessStartInfo CreateSleepProcessStartInfo(int seconds)
    {

        if (OperatingSystem.IsWindows())
        {

            return new ProcessStartInfo
            {

                FileName = "powershell.exe",

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

                ArgumentList = { "-NoProfile", "-Command", $"Start-Sleep -Seconds {seconds}" },

            };

        }

        return new ProcessStartInfo
        {

            FileName = "/bin/sleep",

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,

            ArgumentList = { seconds.ToString() },

        };

    }

}
