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
            timeout: TimeSpan.FromSeconds(10),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        stopwatch.Stop();

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.True(result.Stdout.Truncated);

        // Well under the 10s timeout — proves the child exited on its own because the pipe kept
        // draining, rather than RunAsync only unblocking once the timeout killed the process tree.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"Expected a prompt exit; took {stopwatch.Elapsed}.");

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
