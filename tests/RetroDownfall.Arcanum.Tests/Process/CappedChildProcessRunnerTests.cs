using System.Diagnostics;
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
