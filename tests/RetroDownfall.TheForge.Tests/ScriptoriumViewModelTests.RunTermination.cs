using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public partial class ScriptoriumViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTermination_failure_replaces_progress_status_and_preserves_partial_output(bool throws)
    {
        List<IntelligenceEvent> events =
        [
            new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "partial answer\n"),
            new IntelligenceEvent(IntelligenceEventType.Status, "Checking output."),
        ];

        if (!throws)
        {
            events.Add(new IntelligenceEvent(IntelligenceEventType.Error, "fixture failed"));
        }

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            ExecutionEvents = events,
            ExecutionFailure = throws ? new InvalidOperationException("fixture failed") : null,
        };

        FakeWhispersService whispers = new();

        using ScriptoriumViewModel viewModel = NewScriptorium(dataSource, whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.RunUserMessage = "operator question";

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.False(viewModel.IsRunning);

        Assert.Equal("partial answer\n", viewModel.RunResultText);

        Assert.Equal("Run failed.", viewModel.StatusText);

        Assert.Equal("fixture failed", viewModel.LastError);

        Assert.Equal(WhisperSeverity.Error, Assert.Single(whispers.Calls).Severity);

        Assert.Equal(1, dataSource.ExecuteCallCount);
    }
}
