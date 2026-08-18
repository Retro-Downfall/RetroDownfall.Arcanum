using System.Diagnostics;
using RetroDownfall.TheForge.Ux.Services.Terminal;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class TerminalCommandRunnerTests
{
    [Fact]
    public async Task ReadLinesAsync_truncates_oversize_line_and_continues()
    {

        string payload =
            new string('x', TerminalCommandRunner.MaxOutputLineChars + 100)
            + "\ntail\n";

        await using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(payload));

        using StreamReader reader = new(stream);

        List<TerminalOutputEvent> events = [];

        await TerminalCommandRunner.ReadLinesAsync(
            reader,
            TerminalOutputKind.StandardOutput,
            new InlineProgress<TerminalOutputEvent>(events.Add),
            CancellationToken.None);

        Assert.Equal(2, events.Count);

        Assert.Contains("truncated", events[0].Text, StringComparison.OrdinalIgnoreCase);

        Assert.InRange(
            events[0].Text.Length,
            1,
            TerminalCommandRunner.MaxOutputLineChars + 100);

        Assert.Equal("tail", events[1].Text);

    }

    [Fact]
    public async Task RunAsync_EchoesStdout()
    {

        TerminalCommandRunner runner = new(new TerminalShellResolver());

        string tempDir = Path.GetTempPath();

        List<TerminalOutputEvent> events = [];

        Progress<TerminalOutputEvent> progress = new(events.Add);

        string command = OperatingSystem.IsWindows() ? "echo hello" : "printf hello";

        TerminalCommandResult result = await runner.RunAsync(command, tempDir, progress, CancellationToken.None);

        Assert.False(result.Cancelled);

        Assert.False(result.FailedToStart);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains(events, e => e.Kind == TerminalOutputKind.StandardOutput && e.Text.Contains("hello", StringComparison.Ordinal));

    }

    [Fact]
    public async Task RunAsync_CapturesStderrAndNonZeroExit()
    {

        TerminalCommandRunner runner = new(new TerminalShellResolver());

        string tempDir = Path.GetTempPath();

        List<TerminalOutputEvent> events = [];

        Progress<TerminalOutputEvent> progress = new(events.Add);

        string command = OperatingSystem.IsWindows()
            ? "echo error 1>&2 && exit /b 7"
            : "printf error >&2; exit 7";

        TerminalCommandResult result = await runner.RunAsync(command, tempDir, progress, CancellationToken.None);

        Assert.False(result.Cancelled);

        Assert.False(result.FailedToStart);

        Assert.Equal(7, result.ExitCode);

        Assert.Contains(events, e => e.Kind == TerminalOutputKind.StandardError && e.Text.Contains("error", StringComparison.Ordinal));

    }

    [Fact]
    public async Task RunAsync_Cancel_ReturnsCancelledWithoutThrowing()
    {

        TerminalCommandRunner runner = new(new TerminalShellResolver());

        string tempDir = Path.GetTempPath();

        string command = OperatingSystem.IsWindows()
            ? "ping 127.0.0.1 -n 10 > nul"
            : "sleep 10";

        using CancellationTokenSource cts = new();

        Task<TerminalCommandResult> runTask = runner.RunAsync(command, tempDir, null, cts.Token);

        await Task.Delay(200);

        cts.Cancel();

        TerminalCommandResult result = await runTask;

        Assert.True(result.Cancelled);

        Assert.Null(result.ExitCode);

    }

    [Fact]
    public void BuildStartInfo_CmdShell_PassesTheCommandThroughWithoutCrtEscaping()
    {

        // cmd.exe does not parse its command line by MSVCRT rules, so the \" escapes ArgumentList inserts
        // survive into the executed command. /S /C makes cmd strip exactly the outer quote pair.
        ProcessStartInfo startInfo = TerminalCommandRunner.BuildStartInfo(
            new TerminalShellSpec("cmd.exe", ["/C"]),
            """git commit -m "fix: parser" """.TrimEnd(),
            Path.GetTempPath());

        Assert.Empty(startInfo.ArgumentList);

        Assert.Equal("""/S /C "git commit -m "fix: parser"" """.TrimEnd(), startInfo.Arguments);

        Assert.DoesNotContain("\\\"", startInfo.Arguments, StringComparison.Ordinal);

    }

    [Fact]
    public void BuildStartInfo_UnixShell_StillUsesArgumentList()
    {

        ProcessStartInfo startInfo = TerminalCommandRunner.BuildStartInfo(
            new TerminalShellSpec("/bin/zsh", ["-lc"]),
            """git commit -m "fix: parser" """.TrimEnd(),
            Path.GetTempPath());

        Assert.Equal(string.Empty, startInfo.Arguments);

        Assert.Equal(["-lc", """git commit -m "fix: parser" """.TrimEnd()], startInfo.ArgumentList);

    }

    [Fact]
    public async Task RunAsync_WhenDescendantOutlivesShell_ReturnsWithoutWaitingForEof()
    {

        TerminalCommandRunner runner = new(new BackgroundingShellResolver());

        // The shell exits immediately but the backgrounded grandchild keeps the inherited stdout/stderr
        // write handles open, so the readers never observe EOF. The run must still return.
        string command = OperatingSystem.IsWindows()
            ? "start /b ping 127.0.0.1 -n 30"
            : "sleep 30 &";

        Task<TerminalCommandResult> runTask = runner.RunAsync(
            command,
            Path.GetTempPath(),
            null,
            CancellationToken.None);

        Task completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(runTask, completed);

        TerminalCommandResult result = await runTask;

        Assert.False(result.Cancelled);

        Assert.False(result.FailedToStart);

    }

    [Fact]
    public async Task RunAsync_MissingShell_ReturnsFailedToStart()
    {

        TerminalCommandRunner runner = new(new MissingShellResolver());

        TerminalCommandResult result = await runner.RunAsync(
            "echo hello",
            Path.GetTempPath(),
            null,
            CancellationToken.None);

        Assert.True(result.FailedToStart);

        Assert.False(result.Cancelled);

        Assert.Null(result.ExitCode);

        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));

    }

    private sealed class BackgroundingShellResolver : ITerminalShellResolver
    {

        // Not TerminalShellResolver: macOS zsh SIGHUPs its own background jobs on exit, which would tear
        // the grandchild down and hide the defect. /bin/sh -c leaves it running.
        public TerminalShellSpec Resolve() =>
            OperatingSystem.IsWindows()
                ? new TerminalShellSpec("cmd.exe", ["/C"])
                : new TerminalShellSpec("/bin/sh", ["-c"]);

    }

    private sealed class MissingShellResolver : ITerminalShellResolver
    {

        public TerminalShellSpec Resolve() =>
            new(Path.Combine(Path.GetTempPath(), $"missing-shell-{Guid.NewGuid():N}"), ["/C"]);

    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

}
