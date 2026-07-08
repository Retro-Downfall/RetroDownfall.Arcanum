using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class TomeViewModelTests
{

    private static readonly Guid SessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task LoadAsync_PopulatesSessionAndTitle()
    {

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession("Forge chat"),
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(SessionId, viewModel.Session?.Id);

        Assert.Equal("Forge chat", viewModel.Title);

        Assert.Equal(DocumentKind.Session, viewModel.Kind);

    }

    [Fact]
    public async Task SendAsync_AppendsUserMessageAndStreamsTokenDataIntoAssistantBubble()
    {

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            PingEvents =
            [
                new IntelligenceEvent(IntelligenceEventType.Token, "", "Hel"),
                new IntelligenceEvent(IntelligenceEventType.Token, "", "lo"),
            ],
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.InputText = "greet me";

        await viewModel.SendAsync(CancellationToken.None);

        Assert.Equal(string.Empty, viewModel.InputText);

        Assert.False(viewModel.IsStreaming);

        Assert.Equal("greet me", dataSource.LastPingRequest?.Prompt);

        Assert.Equal(SessionId, dataSource.LastPingRequest?.SessionId);

        Assert.Equal(2, viewModel.Messages.Count);

        Assert.Equal("user", viewModel.Messages[0].Role);

        Assert.Equal("greet me", viewModel.Messages[0].Content);

        Assert.Equal("assistant", viewModel.Messages[1].Role);

        Assert.Equal("Hello", viewModel.Messages[1].Content);

    }

    [Fact]
    public async Task SendAsync_HandlesToolCallResultErrorStatusWardedAndResult()
    {

        Guid wardId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            PingEvents =
            [
                new IntelligenceEvent(
                    IntelligenceEventType.ToolCall,
                    "calling hammer",
                    null,
                    ToolCall: new IntelligenceToolCallEvent("call-1", "hammer", "{\"force\":1}")),
                new IntelligenceEvent(
                    IntelligenceEventType.ToolError,
                    "hammer slipped",
                    null,
                    ToolCall: new IntelligenceToolCallEvent("call-1", "hammer", "{\"force\":1}")),
                new IntelligenceEvent(
                    IntelligenceEventType.ToolResult,
                    "done",
                    "spark",
                    ToolCall: new IntelligenceToolCallEvent("call-1", "hammer", "{\"force\":1}")),
                new IntelligenceEvent(IntelligenceEventType.Status, "compressing memory"),
                new IntelligenceEvent(
                    IntelligenceEventType.Warded,
                    "awaiting approval",
                    WardId: wardId.ToString(),
                    WardToolName: "hammer"),
                new IntelligenceEvent(
                    IntelligenceEventType.WardResolved,
                    "allowed",
                    WardId: wardId.ToString(),
                    WardAllowed: true),
                new IntelligenceEvent(
                    IntelligenceEventType.Result,
                    "complete",
                    Usage: new ChatCompletionUsage(10, 20, 30)),
            ],
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.InputText = "strike";

        await viewModel.SendAsync(CancellationToken.None);

        ToolCallCardViewModel? toolCard = viewModel.Messages
            .Select(static message => message.ToolCall)
            .FirstOrDefault(static card => card is not null);

        Assert.NotNull(toolCard);

        Assert.Equal("hammer", toolCard.Name);

        Assert.Equal("{\"force\":1}", toolCard.ArgumentsJson);

        Assert.Equal("spark", toolCard.Result);

        Assert.True(toolCard.HasError);

        Assert.Contains(viewModel.Messages, static message => message.Role == "status" && message.Content.Contains("compressing memory"));

        Assert.False(viewModel.WardPending);

        Assert.Contains("allowed", viewModel.LastWhisper ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(viewModel.LastUsage);

        Assert.Equal(30, viewModel.LastUsage.TotalTokens);

        Assert.True(viewModel.ManaPercent > 0);

    }

    [Fact]
    public async Task SendAsync_SessionBoundUpdatesSessionIdAndIgnoresConversationBound()
    {

        Guid boundId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            PingEvents =
            [
                new IntelligenceEvent(IntelligenceEventType.SessionBound, boundId.ToString()),
                new IntelligenceEvent(IntelligenceEventType.ConversationBound, boundId.ToString()),
            ],
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.InputText = "bind";

        await viewModel.SendAsync(CancellationToken.None);

        Assert.Equal(boundId, viewModel.SessionId);

        Assert.Single(viewModel.Messages);

        Assert.Equal("user", viewModel.Messages[0].Role);

    }

    [Fact]
    public async Task SendAsync_ErrorLogsToFoundryFloorAndAddsInlineError()
    {

        FoundryFloorViewModel foundryFloor = new();

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            PingEvents =
            [
                new IntelligenceEvent(IntelligenceEventType.Error, "the anvil cracked"),
            ],
        };

        TomeViewModel viewModel = CreateViewModel(dataSource, foundryFloor);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.InputText = "fail";

        await viewModel.SendAsync(CancellationToken.None);

        Assert.Contains(viewModel.Messages, static message => message.Role == "error" && message.Content.Contains("the anvil cracked"));

        Assert.Contains(foundryFloor.Lines, static line => line.Contains("the anvil cracked"));

    }

    [Fact]
    public async Task AppendManualEntryAsync_PostsEntryAndAddsMessage()
    {

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            AppendedEntry = new EntryDto(Guid.NewGuid(), SessionId, "system", "operator note", null, null, DateTimeOffset.UtcNow),
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        viewModel.ManualEntryText = "operator note";

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.AppendManualEntryAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastAppendRequest);

        Assert.Equal(MessageRole.System, dataSource.LastAppendRequest.Role);

        Assert.Equal("operator note", dataSource.LastAppendRequest.Content);

        Assert.Contains(viewModel.Messages, static message => message.Role == "system" && message.Content == "operator note");

        Assert.Equal(string.Empty, viewModel.ManualEntryText);

    }

    [Fact]
    public async Task ForkAsync_OpensForkedSessionDocument()
    {

        Guid forkedId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        NavigationService navigation = new();

        (DocumentKind Kind, string Id)? opened = null;

        navigation.DocumentOpenRequested += (kind, id) => opened = (kind, id);

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            ForkedSession = NewSession("Fork", forkedId),
        };

        TomeViewModel viewModel = new(SessionId, dataSource, navigation, new FoundryFloorViewModel());

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ForkAsync(CancellationToken.None);

        Assert.Equal((DocumentKind.Session, forkedId.ToString("D")), opened);

    }

    [Fact]
    public async Task ExportAsync_StoresMarkdownContent()
    {

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            ExportResult = new SessionExportResult(SessionId, "markdown", "# transcript", "text/markdown"),
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ExportAsync(CancellationToken.None);

        Assert.Equal("# transcript", viewModel.LastExportContent);

        Assert.Equal("markdown", dataSource.LastExportFormat);

    }

    [Fact]
    public async Task LoadAsync_StartsSessionObservationAndAppendsLiveEntries()
    {

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            LiveEntries =
            [
                new EntryDto(Guid.NewGuid(), SessionId, "assistant", "from the stream", null, null, DateTimeOffset.UtcNow),
            ],
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        await Task.Delay(50);

        Assert.Contains(viewModel.Messages, static message => message.Role == "assistant" && message.Content == "from the stream");

        Assert.True(dataSource.ObserveStarted);

    }

    [Fact]
    public void Dispose_CanBeCalledTwiceSafely()
    {

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        viewModel.Dispose();

        viewModel.Dispose();

    }

    private static TomeViewModel CreateViewModel(FakeTomeDataSource dataSource, FoundryFloorViewModel? foundryFloor = null) =>
        new(SessionId, dataSource, new NavigationService(), foundryFloor ?? new FoundryFloorViewModel());

    private static SessionDetailDto NewSession(string title = "Session", Guid? id = null) =>
        new(
            id ?? SessionId,
            null,
            title,
            "active",
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            0);

    private sealed class FakeTomeDataSource : ITomeDataSource
    {

        public SessionDetailDto? Session { get; init; }

        public IReadOnlyList<IntelligenceEvent> PingEvents { get; init; } = [];

        public IReadOnlyList<EntryDto> LiveEntries { get; init; } = [];

        public EntryDto? AppendedEntry { get; init; }

        public SessionDetailDto? ForkedSession { get; init; }

        public SessionExportResult? ExportResult { get; init; }

        public PingRequest? LastPingRequest { get; private set; }

        public AppendEntryRequest? LastAppendRequest { get; private set; }

        public string? LastExportFormat { get; private set; }

        public bool ObserveStarted { get; private set; }

        public Task<SessionDetailDto?> GetSessionAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Session);

        public async IAsyncEnumerable<IntelligenceEvent> PingStreamAsync(PingRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {

            LastPingRequest = request;

            foreach (IntelligenceEvent ev in PingEvents)
            {

                yield return ev;

                await Task.Yield();

            }

        }

        public Task<EntryDto?> AppendEntryAsync(Guid id, AppendEntryRequest request, CancellationToken cancellationToken)
        {

            LastAppendRequest = request;

            return Task.FromResult(AppendedEntry);

        }

        public Task<SessionDetailDto?> ForkAsync(Guid id, ForkSessionRequest? request, CancellationToken cancellationToken) =>
            Task.FromResult(ForkedSession);

        public Task<SessionExportResult?> ExportAsync(Guid id, string format, CancellationToken cancellationToken)
        {

            LastExportFormat = format;

            return Task.FromResult(ExportResult);

        }

        public async IAsyncEnumerable<EntryDto> StreamEntriesAsync(Guid id, DateTimeOffset? since, [EnumeratorCancellation] CancellationToken cancellationToken)
        {

            ObserveStarted = true;

            foreach (EntryDto entry in LiveEntries)
            {

                yield return entry;

                await Task.Yield();

            }

        }

    }

}
