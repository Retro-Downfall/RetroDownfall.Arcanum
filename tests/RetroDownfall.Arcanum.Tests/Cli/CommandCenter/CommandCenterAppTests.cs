using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Terminal.Gui.App;

using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// <see cref="CommandCenterApp"/>'s viewport-size probe drains a child process's redirected
/// pipes with a one-second deadline. Before the fix, both pipes were read synchronously to EOF
/// before <see cref="Process.WaitForExit(int)"/> was ever consulted, so a child that never writes
/// and never exits blocked the calling thread forever — the deadline and the
/// <see cref="Process.Kill(bool)"/> recovery were unreachable dead code.
/// </summary>
public sealed class CommandCenterAppTests : IAsyncLifetime
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_initialization_failure_disposes_the_app_and_restores_terminal_modes(bool disposalThrows)
    {
        using IApplication application = Application.Create();

        List<string> order = [];

        CommandCenterApp app = new(NullLogger<CommandCenterApp>.Instance, () => application,
            actual =>
            {
                Assert.Same(application, actual);

                order.Add("init");

                throw new InvalidOperationException("initialization failed after touching terminal modes");
            },
            actual =>
            {
                Assert.Same(application, actual);

                order.Add("dispose");

                if (disposalThrows)
                {
                    throw new IOException("dispose failed");
                }
            },
            () => order.Add("restore"));

        bool bodyCalled = false;

        int result = app.Run((_, _) => { bodyCalled = true; return 0; });

        Assert.Equal(CommandCenterApp.StartFailed, result);

        Assert.False(bodyCalled);

        Assert.Equal(new[] { "init", "dispose", "restore" }, order);
    }

    [Fact]
    public void Factory_failure_before_initialization_does_not_restore_untouched_terminal_modes()
    {
        List<string> order = [];

        CommandCenterApp app = new(NullLogger<CommandCenterApp>.Instance,
            () => throw new InvalidOperationException("factory failure"),
            _ => order.Add("init"), _ => order.Add("dispose"), () => order.Add("restore"));

        Assert.Equal(CommandCenterApp.StartFailed, app.Run((_, _) => throw new InvalidOperationException("unexpected body")));

        Assert.Empty(order);
    }

    [Theory]
    [InlineData(false, false, 17)]
    [InlineData(false, true, 17)]
    [InlineData(true, false, CommandCenterApp.CrashedAfterStart)]
    [InlineData(true, true, CommandCenterApp.CrashedAfterStart)]
    public void Started_exit_preserves_result_and_attempts_both_cleanup_steps(bool bodyThrows, bool disposalThrows, int expected)
    {
        using IApplication application = Application.Create();

        List<string> order = [];

        CommandCenterApp app = new(NullLogger<CommandCenterApp>.Instance, () => application,
            _ => order.Add("init"),
            _ =>
            {
                order.Add("dispose");

                if (disposalThrows)
                {
                    throw new IOException("dispose failed");
                }
            },
            () => order.Add("restore"));

        int result = app.Run((actual, _) =>
        {
            Assert.Same(application, actual);

            order.Add("body");

            if (bodyThrows)
            {
                throw new InvalidOperationException("body failed");
            }

            return 17;
        });

        Assert.Equal(expected, result);

        Assert.Equal(new[] { "init", "body", "dispose", "restore" }, order);
    }

    /// <summary>
    /// Runs PowerShell once, outside every test's deadline and stopwatch, so no test measures its cold start. A
    /// no-op wherever the children are POSIX tools.
    /// </summary>
    public Task InitializeAsync() => WindowsPowerShellWarmUp.EnsureAsync();

    public Task DisposeAsync() => Task.CompletedTask;

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

        // Not 1000ms: this test exercises the captured-output path, not the deadline, and the deadline runs from the
        // process start, which on Windows includes powershell.exe starting. That has taken more than ten seconds on a
        // loaded CI runner even before this class warmed it up, so the deadline here is only a guard against a hang
        // and a passing run never comes near it.
        bool result = CommandCenterApp.TryReadWithDeadline(
            startInfo,
            timeoutMilliseconds: 60_000,
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
