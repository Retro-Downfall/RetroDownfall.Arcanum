using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
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
    public async Task SendAsync_streams_reasoning_into_separate_live_message_without_contaminating_answer()
    {
        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            PingEvents =
            [
                new IntelligenceEvent(
                    IntelligenceEventType.Reasoning,
                    "think ",
                    Reasoning: new ReasoningContentSegment("think ", ReasoningOutputMode.Summary)),
                new IntelligenceEvent(
                    IntelligenceEventType.Reasoning,
                    "carefully",
                    Reasoning: new ReasoningContentSegment("carefully", ReasoningOutputMode.Summary)),
                new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "answer"),
            ],
        };
        TomeViewModel viewModel = CreateViewModel(dataSource);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.InputText = "question";

        await viewModel.SendAsync(CancellationToken.None);

        Assert.Equal(["user", "reasoning", "assistant"], viewModel.Messages.Select(static message => message.Role));
        ChatMessageViewModel reasoning = viewModel.Messages[1];
        ChatMessageViewModel assistant = viewModel.Messages[2];
        Assert.True(reasoning.IsReasoning);
        Assert.Equal("think carefully", reasoning.Content);
        Assert.Null(reasoning.EntryId);
        Assert.Equal("answer", assistant.Content);
        Assert.DoesNotContain("think", assistant.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_message_high_delta_appends_are_bounded_and_coalesced()
    {
        ChatMessageViewModel message = new("reasoning", string.Empty);
        int contentNotifications = 0;
        message.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatMessageViewModel.Content))
            {
                contentNotifications++;
            }
        };

        for (int i = 0; i < 7_000; i++)
        {
            message.AppendContent("abcdefghij");
        }

        Assert.True(message.Content.Length <= ChatMessageViewModel.DefaultMaxReasoningChars);
        Assert.StartsWith("abcdefghijabcdefghij", message.Content, StringComparison.Ordinal);
        Assert.EndsWith(ChatMessageViewModel.ReasoningTruncationMarker, message.Content, StringComparison.Ordinal);
        Assert.True(
            contentNotifications < 300,
            $"Expected coalesced content notifications, got {contentNotifications}.");
    }

    [Fact]
    public void Chat_message_completion_flushes_content_and_supports_later_appends()
    {
        ChatMessageViewModel message = new("assistant", string.Empty);
        int contentNotifications = 0;
        message.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatMessageViewModel.Content))
            {
                contentNotifications++;
            }
        };

        message.AppendContent("first");
        Assert.Equal(string.Empty, message.Content);

        message.CompleteStreamingContent();

        Assert.Equal("first", message.Content);
        Assert.Equal(1, contentNotifications);

        message.AppendContent(" second");
        message.CompleteStreamingContent();

        Assert.Equal("first second", message.Content);
        Assert.Equal(2, contentNotifications);
    }

    [Fact]
    public async Task SendAsync_high_delta_reasoning_is_bounded_and_final_answer_order_is_preserved()
    {
        const string answerChunk = "ab";
        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            PingEvents =
            [
                .. Enumerable.Range(0, 7_000).Select(static _ =>
                    new IntelligenceEvent(
                        IntelligenceEventType.Reasoning,
                        "abcdefghij",
                        Reasoning: new ReasoningContentSegment(
                            "abcdefghij",
                            ReasoningOutputMode.Summary))),
                .. Enumerable.Range(0, 1_001).Select(static _ =>
                    new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, answerChunk)),
                new IntelligenceEvent(IntelligenceEventType.Result, "complete"),
            ],
        };
        TomeViewModel viewModel = CreateViewModel(dataSource);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.InputText = "question";

        await viewModel.SendAsync(CancellationToken.None);

        ChatMessageViewModel reasoning = Assert.Single(
            viewModel.Messages,
            static message => message.IsReasoning);
        ChatMessageViewModel assistant = Assert.Single(
            viewModel.Messages,
            static message => message.IsAssistant);
        Assert.True(reasoning.Content.Length <= ChatMessageViewModel.DefaultMaxReasoningChars);
        Assert.EndsWith(ChatMessageViewModel.ReasoningTruncationMarker, reasoning.Content, StringComparison.Ordinal);
        Assert.Equal(string.Concat(Enumerable.Repeat(answerChunk, 1_001)), assistant.Content);
        Assert.DoesNotContain("abcdefghij", assistant.Content, StringComparison.Ordinal);
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

    // Issue #53: a server-side auto-approved ward is already resolved, so The Forge must report it
    // rather than raising a pending-approval state the Gatehouse could no longer act on.
    [Fact]
    public async Task SendAsync_AutoApprovedWard_ReportsWithoutRaisingPendingApproval()
    {

        Guid wardId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            PingEvents =
            [
                new IntelligenceEvent(
                    IntelligenceEventType.Warded,
                    "apply_patch",
                    WardId: wardId.ToString(),
                    WardToolName: "apply_patch",
                    WardOrigin: WardResolutionOrigin.AutoApproved),
                new IntelligenceEvent(
                    IntelligenceEventType.WardResolved,
                    "allowed",
                    WardId: wardId.ToString(),
                    WardAllowed: true,
                    WardOrigin: WardResolutionOrigin.AutoApproved),
                new IntelligenceEvent(IntelligenceEventType.Result, "complete"),
            ],
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.InputText = "patch it";

        await viewModel.SendAsync(CancellationToken.None);

        Assert.False(viewModel.WardPending);

        Assert.Null(viewModel.PendingWardId);

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

        FoundryFloorViewModel foundryFloor = new(new NullLogService());

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

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            ForkedSession = NewSession("Fork", forkedId),
        };

        TomeViewModel viewModel = new(
            SessionId,
            dataSource,
            navigation,
            new FoundryFloorViewModel(new NullLogService()),
            new FakeClipboardService(),
            new ScriptedConfirmationDialogService(confirm: true),
            ImmediateTheForgeLocalMutationRunner.Instance);

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

    [Fact]
    public async Task RefreshEntries_PopulatesIdentity()
    {

        Guid entryId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            Entries =
            [
                new EntryDto(entryId, SessionId, "user", "hello", null, null, DateTimeOffset.UtcNow),
            ],
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Single(viewModel.Messages);

        Assert.Equal(entryId, viewModel.Messages[0].EntryId);

        Assert.Equal("user", viewModel.Messages[0].Role);

        Assert.Equal("hello", viewModel.Messages[0].Content);

    }

    [Fact]
    public async Task PinEntry_CallsDataSource()
    {

        Guid entryId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            Entries =
            [
                new EntryDto(entryId, SessionId, "assistant", "pin me", null, null, DateTimeOffset.UtcNow),
            ],
            PinResult = new DataSourceResult<bool>(true, true, null, null),
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        ChatMessageViewModel message = viewModel.Messages[0];

        await viewModel.PinEntryAsync(message, CancellationToken.None);

        Assert.Equal(entryId, dataSource.LastPinEntryId);

        Assert.True(message.IsPinned);

        Assert.Equal("Entry pinned.", viewModel.MemoryStatusText);

    }

    [Fact]
    public async Task PinEntry_MemoryManagementDisabled_SetsFlag()
    {

        Guid entryId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            Entries =
            [
                new EntryDto(entryId, SessionId, "user", "blocked", null, null, DateTimeOffset.UtcNow),
            ],
            PinResult = new DataSourceResult<bool>(
                false,
                false,
                ErrorCodes.Session.MemoryManagementDisabled,
                "memory off"),
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.PinEntryAsync(viewModel.Messages[0], CancellationToken.None);

        Assert.True(viewModel.MemoryManagementDisabled);

        Assert.Contains(DisabledSettingPaths.AllowMemoryManagement, viewModel.MemoryManagementDisabledMessage);

    }

    [Fact]
    public async Task CopyDisabledPaths_CopiesJoinedPaths()
    {

        FakeClipboardService clipboard = new();

        TomeViewModel viewModel = CreateViewModel(new FakeTomeDataSource(), clipboard: clipboard);

        viewModel.MemoryManagementDisabled = true;

        await viewModel.CopyDisabledPathsCommand.ExecuteAsync(null);

        Assert.Equal(
            DisabledSettingPaths.JoinForClipboard(DisabledSettingPaths.SessionMemoryManagement),
            clipboard.LastText);

    }

    [Fact]
    public async Task PinEntry_TooManyPinned_SurfacesMessage()
    {

        Guid entryId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            Entries =
            [
                new EntryDto(entryId, SessionId, "user", "overflow", null, null, DateTimeOffset.UtcNow),
            ],
            PinResult = new DataSourceResult<bool>(
                false,
                false,
                ErrorCodes.Session.TooManyPinned,
                "pin limit reached"),
        };

        FoundryFloorViewModel foundryFloor = new(new NullLogService());

        TomeViewModel viewModel = CreateViewModel(dataSource, foundryFloor);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.PinEntryAsync(viewModel.Messages[0], CancellationToken.None);

        Assert.Equal("pin limit reached", viewModel.MemoryStatusText);

        Assert.Contains(foundryFloor.Lines, static line => line.Contains("pin limit reached"));

    }

    [Fact]
    public async Task Compact_CallsDataSource()
    {

        CompactResult compact = new(100, 40, 3);

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            CompactResult = new DataSourceResult<CompactResult>(compact, true, null, null),
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.CompactAsync(CancellationToken.None);

        Assert.True(dataSource.CompactCalled);

        Assert.Equal(2, dataSource.GetEntriesCallCount);

        Assert.Equal("0 entries.", viewModel.MemoryStatusText);

    }

    // AppendEntryIfNew backfill during SSE observation is not unit-tested here — the path is private
    // and only reachable mid-stream; RefreshEntries is the identity source of truth (see above).

    [Fact]
    public async Task DeleteEntry_WhenDeclined_KeepsTheEntry()
    {

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
        };

        ScriptedConfirmationDialogService confirmation = new(confirm: false);

        TomeViewModel viewModel = CreateViewModel(dataSource, confirmation: confirmation);

        ChatMessageViewModel message = new("assistant", "irreplaceable transcript turn", entryId: Guid.NewGuid());

        viewModel.Messages.Add(message);

        // Removing a transcript entry is not undoable from the Tome, so it must be confirmed.
        await viewModel.DeleteEntryAsync(message, CancellationToken.None);

        Assert.Null(dataSource.LastDeletedEntryId);

        Assert.Single(confirmation.Prompts);

        Assert.Contains(message, viewModel.Messages);

    }

    [Fact]
    public async Task DeleteEntry_WhenConfirmed_RemovesTheEntry()
    {

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
        };

        TomeViewModel viewModel = CreateViewModel(
            dataSource,
            confirmation: new ScriptedConfirmationDialogService(confirm: true));

        Guid entryId = Guid.NewGuid();

        ChatMessageViewModel message = new("assistant", "disposable", entryId: entryId);

        viewModel.Messages.Add(message);

        await viewModel.DeleteEntryAsync(message, CancellationToken.None);

        Assert.Equal(entryId, dataSource.LastDeletedEntryId);

        Assert.DoesNotContain(message, viewModel.Messages);

    }

    [Fact]
    public async Task RefreshEntriesAsync_ManyServerEntries_TrimsMessagesToTheDocumentedCap()
    {

        EntryDto[] entries = Enumerable.Range(0, 5_000)
            .Select(i => new EntryDto(
                Guid.NewGuid(),
                SessionId,
                i % 2 == 0 ? "user" : "assistant",
                $"entry-{i}",
                null,
                null,
                DateTimeOffset.UnixEpoch.AddSeconds(i)))
            .ToArray();

        FakeTomeDataSource dataSource = new()
        {
            Session = NewSession(),
            Entries = entries,
        };

        TomeViewModel viewModel = CreateViewModel(dataSource);

        await viewModel.RefreshEntriesAsync(CancellationToken.None);

        Assert.Equal(TomeViewModel.MaxMessages, viewModel.Messages.Count);

        // Newest entries win — the cap drops from the front, oldest first.
        Assert.Equal("entry-4999", viewModel.Messages[^1].Content);

        Assert.Equal($"entry-{5_000 - TomeViewModel.MaxMessages}", viewModel.Messages[0].Content);

    }

    private static TomeViewModel CreateViewModel(
        FakeTomeDataSource dataSource,
        FoundryFloorViewModel? foundryFloor = null,
        FakeClipboardService? clipboard = null,
        IConfirmationDialogService? confirmation = null) =>
        new(
            SessionId,
            dataSource,
            new NavigationService(),
            foundryFloor ?? new FoundryFloorViewModel(new NullLogService()),
            clipboard ?? new FakeClipboardService(),
            confirmation ?? new ScriptedConfirmationDialogService(confirm: true),
            ImmediateTheForgeLocalMutationRunner.Instance);

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

        public IReadOnlyList<EntryDto> Entries { get; init; } = [];

        public DataSourceResult<bool> PinResult { get; init; } =
            new(true, true, null, null);

        public DataSourceResult<CompactResult> CompactResult { get; init; } =
            new(null, true, null, null);

        public PingRequest? LastPingRequest { get; private set; }

        public AppendEntryRequest? LastAppendRequest { get; private set; }

        public string? LastExportFormat { get; private set; }

        public bool ObserveStarted { get; private set; }

        public Guid? LastPinEntryId { get; private set; }

        public bool CompactCalled { get; private set; }

        public int GetEntriesCallCount { get; private set; }

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

        public async IAsyncEnumerable<EntryDto> StreamEntriesAsync(Guid id, Guid? since, [EnumeratorCancellation] CancellationToken cancellationToken)
        {

            ObserveStarted = true;

            foreach (EntryDto entry in LiveEntries)
            {

                yield return entry;

                await Task.Yield();

            }

        }

        public Task<DataSourceResult<EntryDto[]>> GetEntriesAsync(Guid id, int? offset, int? limit, CancellationToken cancellationToken)
        {

            GetEntriesCallCount++;

            return Task.FromResult(new DataSourceResult<EntryDto[]>(Entries.ToArray(), true, null, null));

        }

        public Task<DataSourceResult<bool>> PinEntryAsync(Guid id, Guid entryId, CancellationToken cancellationToken)
        {

            LastPinEntryId = entryId;

            return Task.FromResult(PinResult);

        }

        public Task<DataSourceResult<bool>> UnpinEntryAsync(Guid id, Guid entryId, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<bool>(true, true, null, null));

        public Guid? LastDeletedEntryId { get; private set; }

        public Task<DataSourceResult<bool>> DeleteEntryAsync(Guid id, Guid entryId, CancellationToken cancellationToken)
        {

            LastDeletedEntryId = entryId;

            return Task.FromResult(new DataSourceResult<bool>(true, true, null, null));

        }

        public Task<DataSourceResult<CompactResult>> CompactAsync(Guid id, CancellationToken cancellationToken)
        {

            CompactCalled = true;

            return Task.FromResult(CompactResult);

        }

    }

}
