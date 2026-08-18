using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using System.Text.Json;
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

        Assert.Equal("0.7", viewModel.TemperatureText);

        Assert.Equal("0.9", viewModel.TopPText);

        Assert.Equal("512", viewModel.MaxOutputTokensText);

        Assert.Equal("{\"type\":\"object\"}", viewModel.ParameterSchemaJson);

        Assert.Equal("{\"name\":\"world\"}", viewModel.DefaultParametersJson);

    }

    [Fact]
    public async Task LoadAsync_WithUnsavedEdits_RefusesInsteadOfDiscardingThem()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
        };

        FakeWhispersService whispers = new();

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource, whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.Template = "unsaved operator work";

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("unsaved operator work", viewModel.Template);

        Assert.True(viewModel.IsEditorDirty);

        Assert.Contains(whispers.Calls, static call => call.Severity == WhisperSeverity.Warning);

    }

    [Fact]
    public async Task Save_SendsSamplingAndEmptyObjectJson()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
        };

        FakeWhispersService whispers = new();

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource, whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.TemperatureText = "0.5";

        viewModel.TopPText = string.Empty;

        viewModel.MaxOutputTokensText = "1024";

        viewModel.ParameterSchemaJson = "   ";

        viewModel.DefaultParametersJson = "{\"name\":\"forge\"}";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Null(viewModel.LastError);

        Assert.NotNull(dataSource.LastSaveRequest);

        Assert.Equal(0.5, dataSource.LastSaveRequest!.Temperature);

        Assert.Null(dataSource.LastSaveRequest.TopP);

        Assert.Equal(1024, dataSource.LastSaveRequest.MaxOutputTokens);

        Assert.Equal("{}", dataSource.LastSaveRequest.ParameterSchema!.RootElement.GetRawText());

        Assert.Equal("{\"name\":\"forge\"}", dataSource.LastSaveRequest.DefaultParameters!.RootElement.GetRawText());

        Assert.Equal(1, dataSource.SaveCallCount);

        (WhisperSeverity Severity, string Message, string? Title) whisper = Assert.Single(whispers.Calls);

        Assert.Equal(WhisperSeverity.Success, whisper.Severity);

        Assert.Equal("Prompt saved.", whisper.Message);

    }

    [Fact]
    public async Task SaveAsync_WhenRejected_ShowsShortErrorWhisper()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            SaveSucceeds = false,
        };

        FakeWhispersService whispers = new();

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource, whispers: whispers);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Equal("Save failed — the server rejected the request.", viewModel.LastError);

        (WhisperSeverity Severity, string Message, string? Title) whisper = Assert.Single(whispers.Calls);

        Assert.Equal(WhisperSeverity.Error, whisper.Severity);

        Assert.Equal("Prompt save failed.", whisper.Message);

        Assert.DoesNotContain("rejected", whisper.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Save_InvalidJson_BlocksWithoutApi()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.ParameterSchemaJson = "{not json";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(viewModel.LastError));

        Assert.Null(dataSource.LastSaveRequest);

        Assert.Equal(0, dataSource.SaveCallCount);

    }

    [Fact]
    public async Task Execute_IncludesRunOverrides()
    {

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = NewPromptDetail(),
            ExecutionEvents =
            [
                new IntelligenceEvent(IntelligenceEventType.Result, "done"),
            ],
        };

        ScriptoriumViewModel viewModel = NewScriptorium(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.RunUserMessage = "hello";

        viewModel.RunModel = "gpt-4o-mini";

        viewModel.RunTemperatureText = "0.8";

        viewModel.RunTopPText = "0.95";

        viewModel.RunMaxOutputTokensText = "256";

        viewModel.RunSeedText = "42";

        viewModel.RunStopText = "END, STOP ";

        viewModel.RunResponseFormat = "json_object";

        viewModel.RunPresencePenaltyText = "0.1";

        viewModel.RunFrequencyPenaltyText = "0.2";

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.Null(viewModel.LastError);

        Assert.NotNull(dataSource.LastExecuteRequest);

        Assert.Equal("hello", dataSource.LastExecuteRequest!.UserMessage);

        Assert.Equal("gpt-4o-mini", dataSource.LastExecuteRequest.Model);

        Assert.Equal(0.8f, dataSource.LastExecuteRequest.Temperature);

        Assert.Equal(0.95f, dataSource.LastExecuteRequest.TopP);

        Assert.Equal(256, dataSource.LastExecuteRequest.MaxOutputTokens);

        Assert.Equal(42L, dataSource.LastExecuteRequest.Seed);

        Assert.Equal(["END", "STOP"], dataSource.LastExecuteRequest.Stop);

        Assert.Equal("json_object", dataSource.LastExecuteRequest.ResponseFormat);

        Assert.Equal(0.1f, dataSource.LastExecuteRequest.PresencePenalty);

        Assert.Equal(0.2f, dataSource.LastExecuteRequest.FrequencyPenalty);

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

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

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

    private static ScriptoriumViewModel NewScriptorium(
        FakePromptEditorDataSource dataSource,
        NavigationService? navigation = null,
        IWhispersService? whispers = null) =>
        new(
            dataSource.Prompt!.Id,
            dataSource,
            navigation ?? new NavigationService(),
            new FoundryFloorViewModel(new NullLogService()),
            new NullConfirmationDialogService(),
            new NullArtifactFileDialogService(),
            new NullTextInputDialogService(),
            whispers ?? new FakeWhispersService());

    private sealed class NullConfirmationDialogService : IConfirmationDialogService
    {

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken, bool confirmIsDefault = true) =>
            Task.FromResult(false);

    }

    private sealed class NullArtifactFileDialogService : IArtifactFileDialogService
    {

        public Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveCsvPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveAnyPathAsync(string suggestedFileName, string? defaultExtension, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);


    }

    private sealed class NullTextInputDialogService : ITextInputDialogService
    {

        public Task<string?> PromptAsync(string title, string label, string? defaultValue, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

    }

    private static PromptDetailDto NewPromptDetail() =>
        new(
            Id: Guid.NewGuid(),
            CampaignId: null,
            Name: "greeting",
            Version: "v1",
            Description: "Say hello",
            Tags: ["social", "intro"],
            Template: "Hello {{name}}",
            ParameterSchema: JsonDocument.Parse("{\"type\":\"object\"}"),
            DefaultParameters: JsonDocument.Parse("{\"name\":\"world\"}"),
            Model: "gpt-4o",
            Provider: "openai",
            Temperature: 0.7,
            TopP: 0.9,
            MaxOutputTokens: 512,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class FakePromptEditorDataSource : IPromptEditorDataSource
    {

        public PromptDetailDto? Prompt { get; init; }

        public bool SaveSucceeds { get; init; } = true;

        public PromptRenderResultDto? RenderResult { get; init; }

        public PromptTestResultDto? TestResult { get; init; }

        public IReadOnlyList<IntelligenceEvent> ExecutionEvents { get; init; } = [];

        public bool StallAfterFirstToken { get; init; }

        public TaskCompletionSource SignalYielded { get; } = new();

        public UpdatePromptRequest? LastSaveRequest { get; private set; }

        public int SaveCallCount { get; private set; }

        public PromptRenderRequest? LastRenderRequest { get; private set; }

        public int RenderCallCount { get; private set; }

        public PromptExecuteRequest? LastExecuteRequest { get; private set; }

        public int ExecuteCallCount { get; private set; }

        public Task<PromptDetailDto?> LoadPromptAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Prompt);

        public Task<PromptDetailDto?> SaveAsync(Guid id, UpdatePromptRequest request, CancellationToken cancellationToken)
        {

            LastSaveRequest = request;

            SaveCallCount++;

            return Task.FromResult(SaveSucceeds ? Prompt : null);

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

        public Task<IReadOnlyList<PromptVersionDto>> ListVersionsAsync(string name, Guid? campaignId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PromptVersionDto>>([]);

        public Task<PromptDetailDto?> CloneAsync(Guid id, ClonePromptRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<PromptDetailDto?>(null);

        public Task<PromptExportDto?> ExportAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PromptExportDto?>(null);

        public Task<DataSourceResult<PromptSummaryDto>> ImportAsync(PromptImportRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<PromptSummaryDto>(null, false, "test", "not used"));

        public Task<DeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(DeleteOutcome.Fail("Http.404", "not used"));

    }

}
