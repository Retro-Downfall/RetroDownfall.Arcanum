using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class GrimoireTurnWriterTests
{

    [Fact]
    public async Task BeginBufferedAssistantReplyAsync_StatelessRequest_ReturnsEmptyHandle()
    {

        GrimoireTurnWriter writer = CreateWriter(new TrackingGrimoireRepository());

        GrimoireTurnWriter.TurnHandle handle = (await writer.BeginBufferedAssistantReplyAsync(
            new PingRequest(
                Prompt: "hello",
                Model: "test-model",
                WorkingDirectory: string.Empty,
                StatelessMessages: [new CoreChatMessage("user", "prior")]),
            InvocationContexts.AttendedSession(),
            "hello",
            "test-model",
            CancellationToken.None)).Value;

        Assert.Null(handle.AssistantEntryId);

        Assert.Null(handle.SessionId);

        Assert.False(handle.IsFinalized);

    }

    [Fact]
    public async Task BeginBufferedAssistantReplyAsync_SessionRequest_BeginsAndPublishes()
    {

        Guid sessionId = Guid.NewGuid();

        TrackingGrimoireRepository grimoire = new()
        {

            FixedSessionId = sessionId,

        };

        FakeSessionTurnBeginStore beginStore = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire, beginStore);

        GrimoireTurnWriter.TurnHandle handle = (await writer.BeginBufferedAssistantReplyAsync(
            new PingRequest(
                Prompt: "hello",
                Model: "test-model",
                WorkingDirectory: string.Empty,
                SessionId: sessionId),
            InvocationContexts.AttendedSession(),
            "hello",
            "test-model",
            CancellationToken.None)).Value;

        Assert.Equal(sessionId, handle.SessionId);

        Assert.NotNull(handle.AssistantEntryId);

        Assert.Equal(1, beginStore.BeginCalls);

        // The request named a Session, so nothing may create one.
        Assert.Equal(0, beginStore.CreateCalls);

        Assert.Equal(sessionId, beginStore.LastBeginSessionId);

        Assert.Equal(1, grimoire.RecentEntriesPublishCount);

    }

    [Fact]
    public async Task TryFinalizeBufferedAssistantEntryAsync_SetsFinalizedFlag()
    {

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = (await writer.BeginBufferedAssistantReplyAsync(
            new PingRequest(
                Prompt: "hello",
                Model: "test-model",
                WorkingDirectory: string.Empty,
                SessionId: Guid.NewGuid()),
            InvocationContexts.AttendedSession(),
            "hello",
            "test-model",
            CancellationToken.None)).Value;

        bool ok = await writer.TryFinalizeBufferedAssistantEntryAsync(handle, "done", "test-model", CancellationToken.None);

        Assert.True(ok);

        Assert.True(handle.IsFinalized);

        Assert.Equal(1, grimoire.FinalizeCallCount);

        Assert.Equal(1, grimoire.EntryByIdPublishCount);

    }

    [Fact]
    public async Task TryFinalizeBufferedAssistantEntryAsync_DbFailure_ReturnsFalse_InterruptsAndMarksFinalized()
    {

        TrackingGrimoireRepository grimoire = new() { FinalizeThrows = true };

        CapturingLogger logger = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire, logger);

        GrimoireTurnWriter.TurnHandle handle = new()
        {

            AssistantEntryId = Guid.NewGuid(),

            SessionId = Guid.NewGuid(),

        };

        bool ok = await writer.TryFinalizeBufferedAssistantEntryAsync(handle, "done", "test-model", CancellationToken.None);

        Assert.False(ok);

        Assert.True(handle.IsFinalized);

        Assert.Equal(1, grimoire.DiscardCallCount);

        Assert.Contains(
            logger.Entries,
            e => e.Exception is InvalidOperationException && e.Message.Contains("could not finalize", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task TryFinalizeBufferedAssistantEntryAsync_HubPublishFailureAfterDbSuccess_ReturnsTrue()
    {

        TrackingGrimoireRepository grimoire = new()
        {

            EntryByIdThrows = true,

            ReturnEntryOnLookup = true,

        };

        CapturingLogger logger = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire, logger);

        GrimoireTurnWriter.TurnHandle handle = (await writer.BeginBufferedAssistantReplyAsync(
            new PingRequest(
                Prompt: "hello",
                Model: "test-model",
                WorkingDirectory: string.Empty,
                SessionId: Guid.NewGuid()),
            InvocationContexts.AttendedSession(),
            "hello",
            "test-model",
            CancellationToken.None)).Value;

        bool ok = await writer.TryFinalizeBufferedAssistantEntryAsync(handle, "done", "test-model", CancellationToken.None);

        Assert.True(ok);

        Assert.True(handle.IsFinalized);

        Assert.Equal(1, grimoire.FinalizeCallCount);

        Assert.Equal(0, grimoire.DiscardCallCount);

        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains("could not publish finalized", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task TryFinalizeBufferedAssistantEntryAsync_DbAndCleanupBothFail_PreservesOriginalFailurePath()
    {

        TrackingGrimoireRepository grimoire = new()
        {

            FinalizeThrows = true,

            DiscardThrows = true,

        };

        CapturingLogger logger = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire, logger);

        GrimoireTurnWriter.TurnHandle handle = new()
        {

            AssistantEntryId = Guid.NewGuid(),

            SessionId = Guid.NewGuid(),

        };

        bool ok = await writer.TryFinalizeBufferedAssistantEntryAsync(handle, "done", "test-model", CancellationToken.None);

        Assert.False(ok);

        Assert.True(handle.IsFinalized);

        Assert.Contains(
            logger.Entries,
            e => e.Exception is InvalidOperationException
                && e.Message.Contains("could not finalize", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            logger.Entries,
            e => e.Exception is InvalidOperationException
                && e.Message.Contains("after finalize failure", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task BeginBufferedAssistantReplyAsync_OperationCanceled_Rethrows()
    {

        FakeSessionTurnBeginStore beginStore = new()
        {
            BeginThrows = new OperationCanceledException("begin cancelled"),
        };

        GrimoireTurnWriter writer = CreateWriter(new TrackingGrimoireRepository(), beginStore);

        using CancellationTokenSource cts = new();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.BeginBufferedAssistantReplyAsync(
                    new PingRequest(
                    Prompt: "hello",
                    Model: "test-model",
                    WorkingDirectory: string.Empty,
                    SessionId: Guid.NewGuid()),
                InvocationContexts.AttendedSession(),
                "hello",
                "test-model",
                cts.Token));

    }

    [Fact]
    public async Task ResolveInterruptedAsync_WithPartialContent_FinalizesEntry()
    {

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = new()
        {

            AssistantEntryId = Guid.NewGuid(),

        };

        await writer.ResolveInterruptedAsync(handle, "partial", CancellationToken.None);

        Assert.Equal(1, grimoire.FinalizeCallCount);

        Assert.Equal(0, grimoire.DiscardCallCount);

    }

    [Fact]
    public async Task ResolveInterruptedAsync_WithoutContent_DiscardsEntry()
    {

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = new()
        {

            AssistantEntryId = Guid.NewGuid(),

        };

        await writer.ResolveInterruptedAsync(handle, null, CancellationToken.None);

        Assert.Equal(0, grimoire.FinalizeCallCount);

        Assert.Equal(1, grimoire.DiscardCallCount);

    }

    [Fact]
    public async Task ResolveInterruptedAndMarkFinalizedAsync_SetsFinalizedFlag()
    {

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = new()
        {

            AssistantEntryId = Guid.NewGuid(),

        };

        bool resolved = await writer.ResolveInterruptedAndMarkFinalizedAsync(
            handle,
            null,
            CancellationToken.None);

        Assert.True(resolved);
        Assert.True(handle.IsFinalized);

    }

    [Fact]
    public async Task TryResolveInterruptedOnStreamExitAsync_UsesNonCancellableToken()
    {

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire);

        GrimoireTurnWriter.TurnHandle handle = new()
        {

            AssistantEntryId = Guid.NewGuid(),

        };

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        await writer.TryResolveInterruptedOnStreamExitAsync(handle, null);

        Assert.Equal(CancellationToken.None, grimoire.LastDiscardToken);

        Assert.True(handle.IsFinalized);

    }

    [Fact]
    public async Task ResolveInterruptedAndMarkFinalizedAsync_FailedCleanup_RemainsRetryable()
    {
        TrackingGrimoireRepository grimoire = new()
        {
            FinalizeFailuresRemaining = 1,
        };
        GrimoireTurnWriter writer = CreateWriter(grimoire);
        GrimoireTurnWriter.TurnHandle handle = new()
        {
            AssistantEntryId = Guid.NewGuid(),
        };

        bool first = await writer.ResolveInterruptedAndMarkFinalizedAsync(
            handle,
            "partial",
            CancellationToken.None);
        bool second = await writer.ResolveInterruptedAndMarkFinalizedAsync(
            handle,
            "partial",
            CancellationToken.None);

        Assert.False(first);
        Assert.True(second);
        Assert.True(handle.IsFinalized);
        Assert.Equal(2, grimoire.FinalizeCallCount);
    }

    [Fact]
    public async Task BeginBufferedAssistantReplyAsync_RethrowsOperationCanceledException()
    {

        FakeSessionTurnBeginStore beginStore = new()
        {
            BeginThrows = new OperationCanceledException("begin cancelled"),
        };

        GrimoireTurnWriter writer = CreateWriter(new TrackingGrimoireRepository(), beginStore);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            writer.BeginBufferedAssistantReplyAsync(
                    new PingRequest(
                    Prompt: "hello",
                    Model: "test-model",
                    WorkingDirectory: string.Empty,
                    SessionId: Guid.NewGuid()),
                InvocationContexts.AttendedSession(),
                "hello",
                "test-model",
                CancellationToken.None));

    }

    [Fact]
    public async Task BeginBufferedAssistantReplyAsync_DoesNotDowngradeBeginFailureToHandleFreeTurn()
    {

        // The exact defect this slice removes. A deleted Campaign, a missing Session, or a binding
        // mismatch used to be caught and returned as an empty handle, so the turn continued and the
        // operator received a normal-looking answer that nothing durable was attached to (§10.12).
        FakeSessionTurnBeginStore beginStore = new()
        {
            BeginResult = Result<AssistantReplyBeginReceipt>.Failure(
                new Error(ErrorCodes.Session.NotFound, "Session not found.")),
        };

        GrimoireTurnWriter writer = CreateWriter(new TrackingGrimoireRepository(), beginStore);

        Result<GrimoireTurnWriter.TurnHandle> result = await writer.BeginBufferedAssistantReplyAsync(
            new PingRequest(
                Prompt: "hello",
                Model: "test-model",
                WorkingDirectory: string.Empty,
                SessionId: Guid.NewGuid()),
            InvocationContexts.AttendedSession(),
            "hello",
            "test-model",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Session.NotFound, result.Error.Code);

    }

    [Fact]
    public async Task BeginBufferedAssistantReplyAsync_CampaignDeletedBeforeBeginCreatesNoEntries()
    {

        FakeSessionTurnBeginStore beginStore = new()
        {
            BeginResult = Result<AssistantReplyBeginReceipt>.Failure(
                new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier.")),
        };

        TrackingGrimoireRepository grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire, beginStore);

        Result<GrimoireTurnWriter.TurnHandle> result = await writer.BeginBufferedAssistantReplyAsync(
            new PingRequest(
                Prompt: "hello",
                Model: "test-model",
                WorkingDirectory: string.Empty,
                SessionId: Guid.NewGuid()),
            InvocationContexts.AttendedSession(),
            "hello",
            "test-model",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Campaign.NotFound, result.Error.Code);

        // No placeholder was published, because none was written.
        Assert.Equal(0, grimoire.RecentEntriesPublishCount);

    }

    [Fact]
    public async Task BeginBufferedAssistantReplyAsync_ARequestWithNoSessionCreatesOneBoundToTheResolvedCampaign()
    {

        FakeSessionTurnBeginStore beginStore = new();

        GrimoireTurnWriter writer = CreateWriter(new TrackingGrimoireRepository(), beginStore);

        Result<GrimoireTurnWriter.TurnHandle> result = await writer.BeginBufferedAssistantReplyAsync(
            new PingRequest(
                Prompt: "hello",
                Model: "test-model",
                WorkingDirectory: string.Empty),
            InvocationContexts.AttendedSession(),
            "hello",
            "test-model",
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, beginStore.CreateCalls);
        Assert.Equal(1, beginStore.BeginCalls);
        Assert.True(beginStore.LastCampaign!.Value.IsCampaignBound);

    }

    [Fact]
    public async Task ResolveInterruptedAsync_MissingEntry_IsNoOp()
    {
        TrackingGrimoireRepository grimoire = new();
        GrimoireTurnWriter writer = CreateWriter(grimoire);

        await writer.ResolveInterruptedAsync(
            new GrimoireTurnWriter.TurnHandle(),
            "partial",
            CancellationToken.None);

        Assert.Equal(0, grimoire.FinalizeCallCount);
        Assert.Equal(0, grimoire.DiscardCallCount);
    }

    [Fact]
    public async Task ResolveInterruptedAsync_PersistenceFailure_IsLoggedAndSwallowed()
    {
        TrackingGrimoireRepository grimoire = new() { FinalizeThrows = true };
        CapturingLogger logger = new();
        GrimoireTurnWriter writer = CreateWriter(grimoire, logger);
        GrimoireTurnWriter.TurnHandle handle = new()
        {
            AssistantEntryId = Guid.NewGuid(),
        };

        bool resolved = await writer.ResolveInterruptedAsync(handle, "partial", CancellationToken.None);

        Assert.False(resolved);
        Assert.Equal(1, grimoire.FinalizeCallCount);
        Assert.Contains(
            logger.Entries,
            entry => entry.Exception is InvalidOperationException
                && entry.Message.Contains("resolve interrupted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveInterruptedAsync_Cancellation_IsRethrown()
    {
        TrackingGrimoireRepository grimoire = new()
        {
            FinalizeException = new OperationCanceledException("cancelled"),
        };
        GrimoireTurnWriter writer = CreateWriter(grimoire);
        GrimoireTurnWriter.TurnHandle handle = new()
        {
            AssistantEntryId = Guid.NewGuid(),
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => writer.ResolveInterruptedAsync(handle, "partial", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveInterruptedAndMarkFinalizedAsync_AlreadyFinalized_IsNoOp()
    {
        TrackingGrimoireRepository grimoire = new();
        GrimoireTurnWriter writer = CreateWriter(grimoire);
        GrimoireTurnWriter.TurnHandle handle = new()
        {
            AssistantEntryId = Guid.NewGuid(),
            IsFinalized = true,
        };

        await writer.ResolveInterruptedAndMarkFinalizedAsync(
            handle,
            "ignored",
            CancellationToken.None);

        Assert.Equal(0, grimoire.FinalizeCallCount);
        Assert.Equal(0, grimoire.DiscardCallCount);
    }

    [Fact]
    public async Task TryResolveInterruptedOnStreamExitAsync_UnexpectedCancellation_IsLogged()
    {
        TrackingGrimoireRepository grimoire = new()
        {
            DiscardException = new OperationCanceledException("unexpected cleanup cancellation"),
        };
        CapturingLogger logger = new();
        GrimoireTurnWriter writer = CreateWriter(grimoire, logger);
        GrimoireTurnWriter.TurnHandle handle = new()
        {
            AssistantEntryId = Guid.NewGuid(),
        };

        await writer.TryResolveInterruptedOnStreamExitAsync(handle, streamedContent: null);

        Assert.Contains(
            logger.Entries,
            entry => entry.Exception is OperationCanceledException
                && entry.Message.Contains("during cleanup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TryAppendToolInteractionAsync_PersistsAndPublishesSavedEntries()
    {
        Guid sessionId = Guid.NewGuid();
        Guid entryId = Guid.NewGuid();
        DateTimeOffset createdAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        TrackingGrimoireRepository grimoire = new()
        {
            RecentEntries =
            [
                new GrimoireEntryDto(
                    entryId,
                    MessageRole.Tool,
                    "result",
                    "test-model",
                    createdAt),
            ],
        };
        SessionEventHub hub = CreateHub();
        GrimoireTurnWriter writer = CreateWriter(
            grimoire,
            NullLogger<GrimoireTurnWriter>.Instance,
            hub);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<Entry?> publishedTask = ReadOneAsync(hub, sessionId, timeout.Token);

        await writer.TryAppendToolInteractionAsync(
            sessionId,
            "lookup",
            """{"id":1}""",
            "result",
            "test-model",
            CancellationToken.None);

        Entry published = Assert.IsType<Entry>(await publishedTask);
        Assert.Equal(1, grimoire.AppendCallCount);
        Assert.Equal(entryId, published.Id);
        Assert.Equal(sessionId, published.SessionId);
        Assert.Equal(MessageRole.Tool, published.Role);
        Assert.Equal("result", published.Content);
        Assert.Equal("test-model", published.ModelUsed);
        Assert.Equal(createdAt, published.CreatedAt);
    }

    [Fact]
    public async Task TryAppendToolInteractionAsync_PersistenceFailure_IsLoggedAndSwallowed()
    {
        TrackingGrimoireRepository grimoire = new()
        {
            AppendException = new InvalidOperationException("append failed"),
        };
        CapturingLogger logger = new();
        GrimoireTurnWriter writer = CreateWriter(grimoire, logger);

        await writer.TryAppendToolInteractionAsync(
            Guid.NewGuid(),
            "lookup",
            "{}",
            "result",
            "model",
            CancellationToken.None);

        Assert.Equal(1, grimoire.AppendCallCount);
        Assert.Contains(
            logger.Entries,
            entry => entry.Exception is InvalidOperationException
                && entry.Message.Contains("could not append", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TryAppendToolInteractionAsync_Cancellation_IsRethrown()
    {
        TrackingGrimoireRepository grimoire = new()
        {
            AppendException = new OperationCanceledException("append cancelled"),
        };
        GrimoireTurnWriter writer = CreateWriter(grimoire);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            writer.TryAppendToolInteractionAsync(
                Guid.NewGuid(),
                "lookup",
                "{}",
                "result",
                "model",
                CancellationToken.None));
    }

    [Fact]
    public async Task BeginBufferedAssistantReplyAsync_PublishFailure_PreservesHandleAndLogs()
    {
        Guid sessionId = Guid.NewGuid();
        TrackingGrimoireRepository grimoire = new()
        {
            FixedSessionId = sessionId,
            RecentEntriesException = new InvalidOperationException("read failed"),
        };
        CapturingLogger logger = new();
        GrimoireTurnWriter writer = CreateWriter(grimoire, logger);

        GrimoireTurnWriter.TurnHandle handle = (await writer.BeginBufferedAssistantReplyAsync(
            new PingRequest("hello", SessionId: sessionId),
            InvocationContexts.AttendedSession(),
            "hello",
            "test-model",
            CancellationToken.None)).Value;

        Assert.Equal(sessionId, handle.SessionId);
        Assert.NotNull(handle.AssistantEntryId);
        Assert.Contains(
            logger.Entries,
            entry => entry.Exception is InvalidOperationException
                && entry.Message.Contains("could not publish begin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TryFinalizeBufferedAssistantEntryAsync_Cancellation_IsRethrown()
    {
        TrackingGrimoireRepository grimoire = new()
        {
            FinalizeException = new OperationCanceledException("finalize cancelled"),
        };
        GrimoireTurnWriter writer = CreateWriter(grimoire);
        GrimoireTurnWriter.TurnHandle handle = new()
        {
            AssistantEntryId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            writer.TryFinalizeBufferedAssistantEntryAsync(
                handle,
                "done",
                "test-model",
                CancellationToken.None));

        Assert.False(handle.IsFinalized);
        Assert.Equal(0, grimoire.DiscardCallCount);
    }

    [Theory]
    [InlineData(MandatoryToolInteractionAppendOutcome.Failed)]
    [InlineData(MandatoryToolInteractionAppendOutcome.Ambiguous)]
    public async Task HandlePendingApplyPatchReceiptAsync_UncommittedOutcome_DoesNotPublishSessionEvents(
        MandatoryToolInteractionAppendOutcome outcome)
    {
        Guid sessionId = Guid.NewGuid();
        TrackingGrimoireRepository grimoire = new()
        {
            MandatoryAppendOutcome = outcome,
            ReturnEntryOnLookup = true,
        };
        SessionEventHub hub = CreateHub();
        GrimoireTurnWriter writer = CreateWriter(
            grimoire,
            NullLogger<GrimoireTurnWriter>.Instance,
            hub);
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<Entry> subscription =
            hub.SubscribeAsync(sessionId, cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
        Task<bool> pendingEvent = subscription.MoveNextAsync().AsTask();
        ToolInteractionReceipt receipt = ToolInteractionReceiptDerivation.Derive(
            new ToolInvocationIdentity(
                "outcome-publication-test",
                "call-1",
                0,
                0,
                ToolRiskClassifier.ApplyPatchToolName));
        ReversibleWorkspaceCommit transaction = new(
            Path.GetTempPath(),
            [],
            [],
            new MultiFileCommitCoordinatorOptions());
        PendingApplyPatchReceipt pending = new(
            sessionId,
            receipt,
            "call-1",
            ToolRiskClassifier.ApplyPatchToolName,
            "{}",
            """{"status":"ok"}""",
            "test-model",
            DateTimeOffset.UtcNow,
            Recovery: null,
            transaction);

        ApplyPatchPendingReceiptHandoffResult result =
            await writer.HandlePendingApplyPatchReceiptAsync(
                pending,
                CancellationToken.None);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(1, grimoire.MandatoryAppendCallCount);
        Assert.Equal(0, grimoire.EntryByIdPublishCount);
        Assert.False(
            pendingEvent.IsCompleted,
            "A failed or ambiguous mandatory receipt published a session event.");

        if (outcome == MandatoryToolInteractionAppendOutcome.Ambiguous)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => transaction.RollbackAsync(CancellationToken.None));
        }

        cancellation.Cancel();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => _ = await pendingEvent);
    }

    private static GrimoireTurnWriter CreateWriter(
        IGrimoireRepository grimoire,
        FakeSessionTurnBeginStore? beginStore = null) =>
        CreateWriter(grimoire, NullLogger<GrimoireTurnWriter>.Instance, beginStore);

    private static GrimoireTurnWriter CreateWriter(
        IGrimoireRepository grimoire,
        ILogger<GrimoireTurnWriter> logger,
        FakeSessionTurnBeginStore? beginStore = null) =>
        CreateWriter(grimoire, logger, CreateHub(), beginStore);

    private static GrimoireTurnWriter CreateWriter(
        IGrimoireRepository grimoire,
        ILogger<GrimoireTurnWriter> logger,
        SessionEventHub hub,
        FakeSessionTurnBeginStore? beginStore = null) =>
        new(
            grimoire,
            beginStore ?? new FakeSessionTurnBeginStore(),
            hub,
            logger);

    private static SessionEventHub CreateHub() =>
        new(NullLogger<SessionEventHub>.Instance);

    private static async Task<Entry?> ReadOneAsync(
        SessionEventHub hub,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await foreach (Entry entry in hub.SubscribeAsync(sessionId, cancellationToken))
        {
            return entry;
        }

        return null;
    }

    private sealed class CapturingLogger : ILogger<GrimoireTurnWriter>
    {

        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {

            get
            {

                lock (_entries)
                {

                    return _entries.ToList();

                }

            }

        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {

            lock (_entries)
            {

                _entries.Add((logLevel, formatter(state, exception), exception));

            }

        }

    }

    private sealed class TrackingGrimoireRepository : IGrimoireRepository
    {

        public Guid? FixedSessionId { get; init; }

        public int BeginCallCount { get; private set; }

        public int FinalizeCallCount { get; private set; }

        public int DiscardCallCount { get; private set; }

        public int AppendCallCount { get; private set; }

        public int MandatoryAppendCallCount { get; private set; }

        public int RecentEntriesPublishCount { get; private set; }

        public int EntryByIdPublishCount { get; private set; }

        public CancellationToken LastDiscardToken { get; private set; }

        public Exception? BeginThrows { get; init; }

        public bool BeginThrowsCanceled { get; init; }

        public bool FinalizeThrows { get; init; }

        public int FinalizeFailuresRemaining { get; set; }

        public Exception? FinalizeException { get; init; }

        public bool DiscardThrows { get; init; }

        public Exception? DiscardException { get; init; }

        public Exception? AppendException { get; init; }

        public MandatoryToolInteractionAppendOutcome MandatoryAppendOutcome { get; init; } =
            MandatoryToolInteractionAppendOutcome.Failed;

        public Exception? RecentEntriesException { get; init; }

        public List<GrimoireEntryDto>? RecentEntries { get; init; }

        public bool EntryByIdThrows { get; init; }

        public bool ReturnEntryOnLookup { get; init; }

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId,
            string prompt,
            string model,
            CancellationToken cancellationToken = default)
        {

            BeginCallCount++;

            if (BeginThrowsCanceled)
            {

                cancellationToken.ThrowIfCancellationRequested();

                throw new OperationCanceledException(cancellationToken);

            }

            if (BeginThrows is not null)
            {

                throw BeginThrows;

            }

            return Task.FromResult((FixedSessionId ?? sessionId ?? Guid.NewGuid(), Guid.NewGuid()));

        }

        public Task FinalizeAssistantEntryAsync(Guid assistantEntryId, string fullContent, CancellationToken cancellationToken = default)
        {

            FinalizeCallCount++;

            if (FinalizeException is not null)
            {

                throw FinalizeException;

            }

            if (FinalizeThrows)
            {

                throw new InvalidOperationException("finalize failed");

            }

            if (FinalizeFailuresRemaining > 0)
            {

                FinalizeFailuresRemaining--;

                throw new InvalidOperationException("finalize failed");

            }

            return Task.CompletedTask;

        }

        public Task DiscardAssistantEntryAsync(Guid assistantEntryId, CancellationToken cancellationToken = default)
        {

            DiscardCallCount++;

            LastDiscardToken = cancellationToken;

            if (DiscardException is not null)
            {

                throw DiscardException;

            }

            if (DiscardThrows)
            {

                throw new InvalidOperationException("discard failed");

            }

            return Task.CompletedTask;

        }

        public Task AppendToolInteractionAsync(
            Guid sessionId,
            string toolName,
            string arguments,
            string result,
            string modelUsed,
            CancellationToken cancellationToken = default)
        {

            AppendCallCount++;

            if (AppendException is not null)
            {

                throw AppendException;

            }

            return Task.CompletedTask;

        }

        public Task<MandatoryToolInteractionAppendResult>
            AppendMandatoryToolInteractionAsync(
            MandatoryToolInteraction interaction,
            CancellationToken cancellationToken = default)
        {
            MandatoryAppendCallCount++;
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new MandatoryToolInteractionAppendResult(
                    MandatoryAppendOutcome,
                    interaction.Receipt));
        }

        public Task SaveCompletedExchangeAsync(string userPrompt, string assistantText, string modelUsed, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<List<GrimoireEntryDto>?>(null);

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(Guid sessionId, int takeLast, CancellationToken cancellationToken = default)
        {

            RecentEntriesPublishCount++;

            if (RecentEntriesException is not null)
            {

                throw RecentEntriesException;

            }

            return Task.FromResult(RecentEntries);

        }

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default)
        {

            EntryByIdPublishCount++;

            if (EntryByIdThrows)
            {

                throw new InvalidOperationException("hub lookup failed");

            }

            if (!ReturnEntryOnLookup)
            {

                return Task.FromResult<GrimoireEntryDto?>(null);

            }

            return Task.FromResult<GrimoireEntryDto?>(new GrimoireEntryDto(
                entryId,
                MessageRole.Assistant,
                "content",
                "test-model",
                DateTimeOffset.UtcNow));

        }

        public Task<bool> DeleteEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> SetEntryPinnedAsync(Guid sessionId, Guid entryId, bool pinned, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> GetPinnedEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(int threshold, DateTime idleCutoff, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Guid>());

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(Guid sessionId, DateTime watermark, int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Entry>());

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task IncrementSessionTokensAsync(Guid sessionId, long totalTokens, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task IncrementSessionTokensAndCostAsync(Guid sessionId, long totalTokens, decimal costUsd, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateSessionCampaignRollupAsync(Guid sessionId, string summary, DateTime lastSummarizedMessageAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LoreDto(key, value, DateTime.UtcNow));

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<ListPageResult<LoreDto>> ListLoreAsync(int? limit = null, int offset = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<LoreDto>([], false));

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<LoreDto?>(null);

        public Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceContext?>(null);

        public Task<Session?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Session?>(null);

        public Task<Session?> GetSessionHeaderAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Session?>(null);

    }

}
