using RetroDownfall.TheForge.Ux.Services.Terminal;
using RetroDownfall.TheForge.Ux.ViewModels.Hearth;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class HearthViewModelTests
{

    [Fact]
    public async Task Run_EmptyCommand_DoesNothing()
    {

        FakeTerminalCommandRunner runner = new();

        HearthViewModel viewModel = new(runner);

        viewModel.CommandText = "   ";

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Lines);

        Assert.Equal(0, runner.CallCount);

        viewModel.Dispose();

    }

    [Fact]
    public async Task Run_AppendsCommandAndOutput()
    {

        FakeTerminalCommandRunner runner = new()
        {
            Outputs =
            [
                new TerminalOutputEvent("hello", TerminalOutputKind.StandardOutput),
            ],
            Result = TerminalCommandResult.Completed(0),
        };

        HearthViewModel viewModel = new(runner)
        {
            CommandText = "echo hello",
        };

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(3, viewModel.Lines.Count);

        Assert.Equal(HearthLineKind.Command, viewModel.Lines[0].Kind);

        Assert.Equal("echo hello", viewModel.Lines[0].Text);

        Assert.Equal(HearthLineKind.StandardOutput, viewModel.Lines[1].Kind);

        Assert.Equal("hello", viewModel.Lines[1].Text);

        Assert.Equal(HearthLineKind.System, viewModel.Lines[2].Kind);

        Assert.Contains("0", viewModel.Lines[2].Text);

        Assert.False(viewModel.IsRunning);

        viewModel.Dispose();

    }

    [Fact]
    public async Task Clear_RemovesLines()
    {

        FakeTerminalCommandRunner runner = new()
        {
            Outputs = [new TerminalOutputEvent("x", TerminalOutputKind.StandardOutput)],
            Result = TerminalCommandResult.Completed(0),
        };

        HearthViewModel viewModel = new(runner)
        {
            CommandText = "echo x",
        };

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasNoLines);

        viewModel.ClearCommand.Execute(null);

        Assert.True(viewModel.HasNoLines);

        Assert.Empty(viewModel.Lines);

        viewModel.Dispose();

    }

    [Fact]
    public async Task Run_TogglesIsRunning()
    {

        TaskCompletionSource runStarted = new();

        TaskCompletionSource allowFinish = new();

        FakeTerminalCommandRunner runner = new()
        {
            OnRun = async (_, _, _, _) =>
            {
                runStarted.SetResult();

                await allowFinish.Task.WaitAsync(TimeSpan.FromSeconds(30));

                return TerminalCommandResult.Completed(0);
            },
        };

        HearthViewModel viewModel = new(runner)
        {
            CommandText = "long",
        };

        Task runTask = viewModel.RunCommand.ExecuteAsync(null)!;

        await runStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(viewModel.IsRunning);

        allowFinish.SetResult();

        await runTask;

        Assert.False(viewModel.IsRunning);

        viewModel.Dispose();

    }

    [Fact]
    public async Task LineCap_EnforcedAfterBatchedAdds()
    {

        List<TerminalOutputEvent> outputs = [];

        for (int i = 0; i < HearthViewModel.MaxLines + 50; i++)
        {

            outputs.Add(new TerminalOutputEvent($"line-{i}", TerminalOutputKind.StandardOutput));

        }

        FakeTerminalCommandRunner runner = new()
        {
            Outputs = outputs,
            Result = TerminalCommandResult.Completed(0),
        };

        HearthViewModel viewModel = new(runner)
        {
            CommandText = "flood",
        };

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.True(viewModel.Lines.Count <= HearthViewModel.MaxLines);

        viewModel.Dispose();

    }

    [Fact]
    public async Task Cd_NoArgs_GoesHome()
    {

        FakeTerminalCommandRunner runner = new();

        HearthViewModel viewModel = new(runner);

        string elsewhere = Path.GetTempPath();

        viewModel.CurrentDirectory = elsewhere;

        viewModel.CommandText = "cd";

        await viewModel.RunCommand.ExecuteAsync(null);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.GetFullPath(home), Path.GetFullPath(viewModel.CurrentDirectory));

        Assert.Equal(0, runner.CallCount);

        viewModel.Dispose();

    }

    [Fact]
    public async Task Cd_Parent_ChangesDirectory()
    {

        FakeTerminalCommandRunner runner = new();

        HearthViewModel viewModel = new(runner);

        string child = Path.Combine(Path.GetTempPath(), $"hearth-cd-{Guid.NewGuid():N}");

        Directory.CreateDirectory(child);

        try
        {

            viewModel.CurrentDirectory = child;

            viewModel.CommandText = "cd ..";

            await viewModel.RunCommand.ExecuteAsync(null);

            Assert.Equal(Path.GetFullPath(Path.GetDirectoryName(child)!), Path.GetFullPath(viewModel.CurrentDirectory));

        }
        finally
        {

            Directory.Delete(child, recursive: true);

            viewModel.Dispose();

        }

    }

    [Fact]
    public async Task Cd_MissingDir_AppendsErrorAndKeepsCwd()
    {

        FakeTerminalCommandRunner runner = new();

        HearthViewModel viewModel = new(runner);

        string original = viewModel.CurrentDirectory;

        viewModel.CommandText = "cd missing-dir-that-does-not-exist-xyz";

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(original, viewModel.CurrentDirectory);

        Assert.Contains(viewModel.Lines, l => l.Kind == HearthLineKind.StandardError);

        viewModel.Dispose();

    }

    [Fact]
    public async Task Cd_QuotedPath_Works()
    {

        FakeTerminalCommandRunner runner = new();

        HearthViewModel viewModel = new(runner);

        string dir = Path.Combine(Path.GetTempPath(), $"hearth spaced {Guid.NewGuid():N}");

        Directory.CreateDirectory(dir);

        try
        {

            viewModel.CommandText = $"cd \"{dir}\"";

            await viewModel.RunCommand.ExecuteAsync(null);

            Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(viewModel.CurrentDirectory));

        }
        finally
        {

            Directory.Delete(dir, recursive: true);

            viewModel.Dispose();

        }

    }

    [Fact]
    public async Task Cd_Tilde_GoesHome()
    {

        FakeTerminalCommandRunner runner = new();

        HearthViewModel viewModel = new(runner);

        viewModel.CurrentDirectory = Path.GetTempPath();

        viewModel.CommandText = "cd ~";

        await viewModel.RunCommand.ExecuteAsync(null);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.GetFullPath(home), Path.GetFullPath(viewModel.CurrentDirectory));

        viewModel.Dispose();

    }

    [Fact]
    public async Task Cd_TildeSubdir_ResolvesUnderHome()
    {

        FakeTerminalCommandRunner runner = new();

        HearthViewModel viewModel = new(runner);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string sub = Path.Combine(home, $"hearth-tilde-{Guid.NewGuid():N}");

        Directory.CreateDirectory(sub);

        try
        {

            string leaf = Path.GetFileName(sub);

            viewModel.CommandText = $"cd ~/{leaf}";

            await viewModel.RunCommand.ExecuteAsync(null);

            Assert.Equal(Path.GetFullPath(sub), Path.GetFullPath(viewModel.CurrentDirectory));

        }
        finally
        {

            Directory.Delete(sub, recursive: true);

            viewModel.Dispose();

        }

    }

    [Fact]
    public void Home_UpdatesPromptText()
    {

        FakeTerminalCommandRunner runner = new();

        HearthViewModel viewModel = new(runner);

        viewModel.CurrentDirectory = Path.GetTempPath();

        string before = viewModel.PromptText;

        viewModel.ResetWorkingDirectoryCommand.Execute(null);

        Assert.NotEqual(before, viewModel.PromptText);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.GetFullPath(home), Path.GetFullPath(viewModel.CurrentDirectory));

        viewModel.Dispose();

    }

    [Fact]
    public async Task Cancel_AppendsSystemLine()
    {

        FakeTerminalCommandRunner runner = new()
        {
            Result = TerminalCommandResult.CancelledResult(),
        };

        HearthViewModel viewModel = new(runner)
        {
            CommandText = "sleep",
        };

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Contains(viewModel.Lines, l => l.Kind == HearthLineKind.System && l.Text.Contains("cancelled", StringComparison.OrdinalIgnoreCase));

        viewModel.Dispose();

    }

    [Fact]
    public async Task Dispose_WhileRunning_DoesNotThrow()
    {

        TaskCompletionSource runStarted = new();

        TaskCompletionSource allowFinish = new();

        FakeTerminalCommandRunner runner = new()
        {
            OnRun = async (_, _, _, ct) =>
            {
                runStarted.SetResult();

                try
                {

                    await allowFinish.Task.WaitAsync(ct);

                }
                catch (OperationCanceledException)
                {

                    return TerminalCommandResult.CancelledResult();

                }

                return TerminalCommandResult.Completed(0);
            },
        };

        HearthViewModel viewModel = new(runner)
        {
            CommandText = "long",
        };

        Task runTask = viewModel.RunCommand.ExecuteAsync(null)!;

        await runStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        viewModel.Dispose();

        viewModel.Dispose();

        allowFinish.TrySetResult();

        await runTask;

    }

    [Theory]
    [InlineData("cd", null, true)]
    [InlineData("cd ", null, true)]
    [InlineData("cd ~", "~", true)]
    [InlineData("cd ~/Source", "~/Source", true)]
    [InlineData("cd \"path with spaces\"", "path with spaces", true)]
    [InlineData("echo hi", null, false)]
    public void TryParseCd_ParsesExpected(string command, string? expectedPath, bool isCd)
    {

        bool parsed = HearthViewModel.TryParseCd(command, out string? pathArg);

        Assert.Equal(isCd, parsed);

        if (isCd)
        {

            Assert.Equal(expectedPath, pathArg);

        }

    }

}

internal sealed class FakeTerminalCommandRunner : ITerminalCommandRunner
{

    public int CallCount { get; private set; }

    public IReadOnlyList<TerminalOutputEvent> Outputs { get; init; } = [];

    public TerminalCommandResult Result { get; init; } = TerminalCommandResult.Completed(0);

    public Func<string, string, IProgress<TerminalOutputEvent>?, CancellationToken, Task<TerminalCommandResult>>? OnRun { get; init; }

    public async Task<TerminalCommandResult> RunAsync(
        string command,
        string workingDirectory,
        IProgress<TerminalOutputEvent>? progress,
        CancellationToken cancellationToken)
    {

        CallCount++;

        if (OnRun is not null)
        {

            return await OnRun(command, workingDirectory, progress, cancellationToken);

        }

        foreach (TerminalOutputEvent output in Outputs)
        {

            progress?.Report(output);

        }

        return Result;

    }

}
