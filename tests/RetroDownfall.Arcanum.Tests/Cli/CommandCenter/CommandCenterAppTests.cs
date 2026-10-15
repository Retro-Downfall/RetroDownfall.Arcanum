using System.Diagnostics;

using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// <see cref="CommandCenterApp"/>'s viewport-size probe drains a child process's redirected
/// pipes with a one-second deadline. Before the fix, both pipes were read synchronously to EOF
/// before <see cref="Process.WaitForExit(int)"/> was ever consulted, so a child that never writes
/// and never exits blocked the calling thread forever — the deadline and the
/// <see cref="Process.Kill(bool)"/> recovery were unreachable dead code.
/// </summary>
public sealed class CommandCenterAppTests
{
    [Fact]
    public void TryReadWithDeadline_returns_false_within_the_deadline_when_the_child_never_exits()
    {
        ProcessStartInfo startInfo = CreateNeverWritesNeverExitsProcessStartInfo();

        Stopwatch stopwatch = Stopwatch.StartNew();

        bool result = CommandCenterApp.TryReadWithDeadline(
            startInfo,
            timeoutMilliseconds: 1000,
            out string output);

        stopwatch.Stop();

        Assert.False(result);

        Assert.Equal(string.Empty, output);

        Assert.True(
            stopwatch.ElapsedMilliseconds < 20_000,
            $"Expected the 1000ms deadline to govern the read, not the child's 120s lifetime; took {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void TryReadWithDeadline_returns_the_captured_output_when_the_child_exits_in_time()
    {
        ProcessStartInfo startInfo = CreateEchoProcessStartInfo("24 80");

        // Not 1000ms: this test exercises the captured-output path, not the deadline, and a cold
        // powershell.exe start on Windows CI routinely takes 1-3s on its own.
        bool result = CommandCenterApp.TryReadWithDeadline(
            startInfo,
            timeoutMilliseconds: 30_000,
            out string output);

        Assert.True(result);

        Assert.Contains("24", output, StringComparison.Ordinal);

        Assert.Contains("80", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A child that writes nothing to stdout/stderr and outlives the 1s deadline many times
    /// over — the shape the finding names ("a child that writes nothing and never exits").
    /// </summary>
    /// <remarks>
    /// The child lives far longer than the bound the test puts on the call, so that the call can only
    /// have ended because the deadline did, and so that spawning it, killing its tree and disposing it
    /// on a loaded coverage runner can take many seconds beyond the deadline without failing the test.
    /// If a read ever blocked on the child again, the call would take the full 120 seconds and still
    /// fail the bound.
    /// </remarks>
    private static ProcessStartInfo CreateNeverWritesNeverExitsProcessStartInfo()
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
                ArgumentList = { "-NoProfile", "-Command", "Start-Sleep -Seconds 120" },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/sleep",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "120" },
        };
    }

    private static ProcessStartInfo CreateEchoProcessStartInfo(string text)
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
                ArgumentList = { "-NoProfile", "-Command", $"Write-Output '{text}'" },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/echo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { text },
        };
    }
}
