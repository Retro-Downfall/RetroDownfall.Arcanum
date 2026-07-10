using RetroDownfall.TheForge.Ux.Services.Terminal;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class TerminalCommandRunnerTests
{

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

    private sealed class MissingShellResolver : ITerminalShellResolver
    {

        public TerminalShellSpec Resolve() =>
            new(Path.Combine(Path.GetTempPath(), $"missing-shell-{Guid.NewGuid():N}"), ["/C"]);

    }

}
