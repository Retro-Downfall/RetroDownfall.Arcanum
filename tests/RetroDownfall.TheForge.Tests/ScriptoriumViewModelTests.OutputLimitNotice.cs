using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public partial class ScriptoriumViewModelTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData(" \t\n", true)]
    [InlineData("", false)]
    [InlineData(" \t\n", false)]
    public async Task OutputLimitNotice_Empty_length_warns_separately_and_preserves_exact_answer(
        string answer,
        bool includeStatus)
    {
        InferenceNotice notice = new((InferenceNoticeCode)3);

        List<IntelligenceEvent> events =
        [
            new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, answer),
        ];

        if (includeStatus)
        {
            events.Add(new IntelligenceEvent(IntelligenceEventType.Status, notice.Message)
            {
                Notices = [notice],
            });
        }

        events.Add(new IntelligenceEvent(IntelligenceEventType.Result, answer, answer, FinishReason: "length")
        {
            Notices = [notice],
        });

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            ExecutionEvents = events,
        };

        FakeWhispersService whispers = new();

        using ScriptoriumViewModel viewModel = NewScriptorium(dataSource, whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.RunUserMessage = "operator question";

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.Equal(answer, viewModel.RunResultText);

        Assert.Contains("output limit", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(notice.Message, viewModel.StatusText);

        (WhisperSeverity Severity, string Message, string? Title) whisper = Assert.Single(whispers.Calls);

        Assert.Equal(WhisperSeverity.Warning, whisper.Severity);

        Assert.Equal(notice.Message, whisper.Message);

        Assert.Null(viewModel.LastError);

        Assert.False(viewModel.IsRunning);

        Assert.Equal(1, dataSource.ExecuteCallCount);
    }

    [Fact]
    public async Task OutputLimitNotice_Partial_length_preserves_answer_and_warns_it_may_be_incomplete()
    {
        const string answer = "Keep this partial answer exactly.\n";

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            ExecutionEvents =
            [
                new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, answer),
                new IntelligenceEvent(IntelligenceEventType.Result, answer, answer, FinishReason: "length"),
            ],
        };

        FakeWhispersService whispers = new();

        using ScriptoriumViewModel viewModel = NewScriptorium(dataSource, whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.RunUserMessage = "operator question";

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.Equal(answer, viewModel.RunResultText);

        Assert.Contains("incomplete", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        (WhisperSeverity Severity, string Message, string? Title) whisper = Assert.Single(whispers.Calls);

        Assert.Equal(WhisperSeverity.Warning, whisper.Severity);

        Assert.Contains("incomplete", whisper.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("without returning", whisper.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Null(viewModel.LastError);

        Assert.Equal(1, dataSource.ExecuteCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ordinary complete answer")]
    public async Task OutputLimitNotice_Ordinary_stop_keeps_success_without_warning(string answer)
    {
        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            ExecutionEvents =
            [
                new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, answer),
                new IntelligenceEvent(IntelligenceEventType.Result, answer, answer, FinishReason: "stop"),
            ],
        };

        FakeWhispersService whispers = new();

        using ScriptoriumViewModel viewModel = NewScriptorium(dataSource, whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.RunUserMessage = "operator question";

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.Equal(answer, viewModel.RunResultText);

        Assert.Equal("Run complete.", viewModel.StatusText);

        (WhisperSeverity Severity, string Message, string? Title) whisper = Assert.Single(whispers.Calls);

        Assert.Equal(WhisperSeverity.Success, whisper.Severity);

        Assert.Equal("Run complete.", whisper.Message);

        Assert.Null(viewModel.LastError);
    }

    [Fact]
    public async Task OutputLimitNotice_Next_successful_run_does_not_reuse_previous_warning()
    {
        InferenceNotice notice = new((InferenceNoticeCode)3);

        List<IntelligenceEvent> events =
        [
            new IntelligenceEvent(IntelligenceEventType.Status, notice.Message)
            {
                Notices = [notice],
            },
            new IntelligenceEvent(IntelligenceEventType.Result, string.Empty, string.Empty, FinishReason: "length")
            {
                Notices = [notice],
            },
        ];

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            ExecutionEvents = events,
        };

        FakeWhispersService whispers = new();

        using ScriptoriumViewModel viewModel = NewScriptorium(dataSource, whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.RunUserMessage = "first question";

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.Equal(WhisperSeverity.Warning, Assert.Single(whispers.Calls).Severity);

        events.Clear();

        events.Add(new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "next answer"));

        events.Add(new IntelligenceEvent(IntelligenceEventType.Result, "next answer", "next answer", FinishReason: "stop"));

        whispers.Calls.Clear();

        viewModel.RunUserMessage = "next question";

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.Equal("next answer", viewModel.RunResultText);

        Assert.Equal("Run complete.", viewModel.StatusText);

        Assert.Equal(WhisperSeverity.Success, Assert.Single(whispers.Calls).Severity);

        Assert.Equal(2, dataSource.ExecuteCallCount);
    }
}
