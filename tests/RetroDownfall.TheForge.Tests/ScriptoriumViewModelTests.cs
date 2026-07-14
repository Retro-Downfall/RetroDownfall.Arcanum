using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class ScriptoriumViewModelTests
{

    [Fact]
    public async Task LoadAsync_PopulatesTemplateAndMetadata()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("Hello {{name}}", viewModel.Template);

        Assert.Equal("Say hello", viewModel.Description);

        Assert.Equal("v1", viewModel.Version);

        Assert.Equal("social, intro", viewModel.TagsText);

        Assert.Equal("gpt-4o", viewModel.Model);

        Assert.Equal("openai", viewModel.Provider);

    }

    [Fact]
    public async Task SaveAsync_SendsEditedFieldsAndVersion()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.Template = "Hello {{name}}, updated";

        viewModel.Description = "Warm greeting";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastSaveRequest);

        Assert.Equal("Hello {{name}}, updated", dataSource.LastSaveRequest!.Template);

        Assert.Equal("v1", dataSource.LastSaveRequest.Version);

        Assert.Equal("Warm greeting", dataSource.LastSaveRequest.Description);

        Assert.Null(viewModel.LastError);

    }

    [Fact]
    public async Task SaveAsync_ClearsTagsWhenTagsTextEmpty()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.TagsText = string.Empty;

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastSaveRequest);

        Assert.Empty(dataSource.LastSaveRequest!.Tags!);

    }

    [Fact]
    public async Task RenderAsync_ParsesParametersAndSurfacesRenderedText()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            RenderResult = new PromptRenderResultDto("Hello world", 7),
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.ParametersText = "name=world\neq=a=b";

        await viewModel.RenderAsync(CancellationToken.None);

        Assert.Null(viewModel.LastError);

        Assert.NotNull(dataSource.LastRenderRequest);

        Assert.NotNull(dataSource.LastRenderRequest!.Parameters);

        Assert.Equal("world", dataSource.LastRenderRequest.Parameters!["name"]);

        Assert.Equal("a=b", dataSource.LastRenderRequest.Parameters["eq"]);

        Assert.Equal("Hello world", viewModel.RenderedText);

        Assert.Equal(7, viewModel.RenderTokenCount);

    }

    [Fact]
    public async Task RenderAsync_RejectsMalformedParameters()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            RenderResult = new PromptRenderResultDto("rendered", 1),
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.ParametersText = "noequalssign";

        await viewModel.RenderAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(viewModel.LastError));

        Assert.Null(dataSource.LastRenderRequest);

        Assert.Equal(0, dataSource.RenderCallCount);

    }

    [Fact]
    public async Task TestAsync_SurfacesAssembledTextAndCounts()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            TestResult = new PromptTestResultDto("Assembled system prompt", 42, new ResolvedSpellInfoDto("greeting", "v1"), 3),
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.TestAsync(CancellationToken.None);

        Assert.Equal("Assembled system prompt", viewModel.TestAssembledText);

        Assert.Equal(42, viewModel.TestTokenCount);

        Assert.Equal("greeting v1", viewModel.TestResolvedSpell);

        Assert.Equal(3, viewModel.TestMcpServerCount);

    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyUserMessage_SetsLastErrorAndDoesNotCallService()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.RunUserMessage = "   ";

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(viewModel.LastError));

        Assert.Equal(0, dataSource.ExecuteCallCount);

        Assert.False(viewModel.IsRunning);

    }

    [Fact]
    public async Task ExecuteAsync_StreamsTokensAndOpensSessionWhenSessionBound()
    {

        NavigationService navigation = new();

        (DocumentKind Kind, string Id)? opened = null;

        navigation.DocumentOpenRequested += (kind, id) => opened = (kind, id);

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            ExecutionEvents =
            [
                new IntelligenceEvent(IntelligenceEventType.Token, "", "Hel"),
                new IntelligenceEvent(IntelligenceEventType.Token, "", "lo"),
                new IntelligenceEvent(IntelligenceEventType.SessionBound, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc").ToString()),
                new IntelligenceEvent(IntelligenceEventType.Result, "done"),
            ],
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource, navigation);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.RunUserMessage = "hi";

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.Equal("Hello", viewModel.RunResultText);

        Assert.Equal((DocumentKind.Session, "cccccccc-cccc-cccc-cccc-cccccccccccc"), opened);

        Assert.False(viewModel.IsRunning);

    }

    [Fact]
    public async Task StopExecution_CancelsInFlightRun()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            StallAfterFirstToken = true,
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.RunUserMessage = "hi";

        Task run = viewModel.ExecuteAsync(CancellationToken.None);

        await dataSource.SignalYielded.Task.ConfigureAwait(true);

        viewModel.StopExecutionCommand.Execute(null);

        await run.ConfigureAwait(true);

        Assert.False(viewModel.IsRunning);

        Assert.Contains("Hel", viewModel.RunResultText);

    }

    private static ScriptoriumViewModel NewScriptorium(FakePromptEditorDataSource dataSource, NavigationService? navigation = null) =>
        new(dataSource.Prompt!.Id, dataSource, navigation ?? new NavigationService(), new FoundryFloorViewModel(new NullLogService()));

    private static PromptDetailDto NewPromptDetail() =>
        new(
            Id: Guid.NewGuid(),
            CampaignId: null,
            Name: "greeting",
            Version: "v1",
            Description: "Say hello",
            Tags: ["social", "intro"],
            Template: "Hello {{name}}",
            ParameterSchema: null,
            DefaultParameters: null,
            Model: "gpt-4o",
            Provider: "openai",
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class FakePromptEditorDataSource : IPromptEditorDataSource
    {

        public PromptDetailDto? Prompt { get; init; }

        public PromptRenderResultDto? RenderResult { get; init; }

        public PromptTestResultDto? TestResult { get; init; }

        public IReadOnlyList<IntelligenceEvent> ExecutionEvents { get; init; } = [];

        public bool StallAfterFirstToken { get; init; }

        public TaskCompletionSource SignalYielded { get; } = new();

        public UpdatePromptRequest? LastSaveRequest { get; private set; }

        public PromptRenderRequest? LastRenderRequest { get; private set; }

        public int RenderCallCount { get; private set; }

        public PromptExecuteRequest? LastExecuteRequest { get; private set; }

        public int ExecuteCallCount { get; private set; }

        public Task<PromptDetailDto?> LoadPromptAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Prompt);

        public Task<PromptDetailDto?> SaveAsync(Guid id, UpdatePromptRequest request, CancellationToken cancellationToken)
        {

            LastSaveRequest = request;

            return Task.FromResult(Prompt);

        }

        public Task<PromptRenderResultDto?> RenderAsync(Guid id, PromptRenderRequest request, CancellationToken cancellationToken)
        {

            LastRenderRequest = request;

            RenderCallCount++;

            return Task.FromResult(RenderResult);

        }

        public Task<PromptTestResultDto?> TestAsync(Guid id, TestPromptRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(TestResult);

        public async IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(Guid id, PromptExecuteRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {

            LastExecuteRequest = request;

            ExecuteCallCount++;

            if (StallAfterFirstToken)
            {

                yield return new IntelligenceEvent(IntelligenceEventType.Token, "", "Hel");

                SignalYielded.TrySetResult();

                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(true);

                yield break;

            }

            foreach (IntelligenceEvent ev in ExecutionEvents)
            {

                yield return ev;

                await Task.Yield();

            }

        }

    }

}
