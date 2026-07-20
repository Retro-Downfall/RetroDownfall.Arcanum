using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SessionAttachmentTurnServiceTests
{

    [Fact]
    public async Task PrepareAsync_WhenDisabled_ReturnsEmptyWithoutTouchingStore()
    {

        FakeSessionAttachmentStore store = new();
        ArcanumSettings settings = new() { Attachments = new AttachmentsSettings { Enabled = false } };
        PingRequest request = new(
            "hi",
            AttachedFiles: [new AttachedFileDto("notes.txt", "hello")]);

        SessionAttachmentTurnPreparation prep = await SessionAttachmentTurnService.PrepareAsync(
            request,
            store,
            settings,
            turnSessionId: Guid.NewGuid(),
            turnEntryId: Guid.NewGuid(),
            pendingTurnId: null);

        Assert.Null(prep.ErrorMessage);
        Assert.Empty(prep.IndexItems);
        Assert.Empty(prep.RehydratedContents);
        Assert.Null(prep.PendingTurnId);
        Assert.Equal(0, store.PersistCallCount);

    }

    [Fact]
    public async Task PrepareAsync_PersistsAttachedFilesAndBuildsIndex()
    {

        Guid sessionId = Guid.NewGuid();
        Guid entryId = Guid.NewGuid();
        FakeSessionAttachmentStore store = new() { IndexSessionId = sessionId };
        ArcanumSettings settings = new();
        PingRequest request = new(
            "hi",
            SessionId: sessionId,
            AttachedFiles: [new AttachedFileDto("folder/notes.txt", "hello world")]);

        SessionAttachmentTurnPreparation prep = await SessionAttachmentTurnService.PrepareAsync(
            request,
            store,
            settings,
            turnSessionId: sessionId,
            turnEntryId: entryId,
            pendingTurnId: null);

        Assert.Null(prep.ErrorMessage);
        Assert.Equal(1, store.PersistCallCount);
        Assert.Equal(sessionId, store.LastPersistSessionId);
        Assert.Null(store.LastPersistPendingTurnId);
        Assert.Equal(entryId, store.LastPersistEntryId);
        Assert.Equal("notes.txt", store.LastLogicalNameHint);
        Assert.Equal(SessionAttachmentKind.Text, store.LastKind);
        Assert.Equal("text/plain", store.LastMimeType);
        Assert.Equal("hello world", Encoding.UTF8.GetString(store.LastBytes.Span));
        Assert.Single(prep.IndexItems);
        Assert.Empty(prep.RehydratedContents);

    }

    [Fact]
    public async Task PrepareAsync_PersistsScryingFociAsImages()
    {

        Guid sessionId = Guid.NewGuid();
        FakeSessionAttachmentStore store = new();
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        PingRequest request = new(
            "hi",
            SessionId: sessionId,
            ScryingFoci: [new ScryingFocusDto(Convert.ToBase64String(png), "image/png")]);

        SessionAttachmentTurnPreparation prep = await SessionAttachmentTurnService.PrepareAsync(
            request,
            store,
            new ArcanumSettings(),
            turnSessionId: sessionId,
            turnEntryId: null,
            pendingTurnId: null);

        Assert.Null(prep.ErrorMessage);
        Assert.Equal(1, store.PersistCallCount);
        Assert.Equal("image-0.png", store.LastLogicalNameHint);
        Assert.Equal(SessionAttachmentKind.Image, store.LastKind);
        Assert.Equal("image/png", store.LastMimeType);
        Assert.True(store.LastBytes.Span.SequenceEqual(png));

    }

    [Fact]
    public async Task PrepareAsync_UsesPendingWhenNoSessionId()
    {

        FakeSessionAttachmentStore store = new();
        string pending = Guid.NewGuid().ToString("N");
        PingRequest request = new(
            "hi",
            AttachedFiles: [new AttachedFileDto("a.txt", "x")]);

        SessionAttachmentTurnPreparation prep = await SessionAttachmentTurnService.PrepareAsync(
            request,
            store,
            new ArcanumSettings(),
            turnSessionId: null,
            turnEntryId: null,
            pendingTurnId: pending);

        Assert.Null(prep.ErrorMessage);
        Assert.Equal(pending, prep.PendingTurnId);
        Assert.Null(store.LastPersistSessionId);
        Assert.Equal(pending, store.LastPersistPendingTurnId);

    }

    [Fact]
    public async Task PrepareAsync_PersistFailure_SetsErrorMessage()
    {

        FakeSessionAttachmentStore store = new() { PersistThrows = true };
        PingRequest request = new(
            "hi",
            SessionId: Guid.NewGuid(),
            AttachedFiles: [new AttachedFileDto("a.txt", "x")]);

        SessionAttachmentTurnPreparation prep = await SessionAttachmentTurnService.PrepareAsync(
            request,
            store,
            new ArcanumSettings(),
            turnSessionId: Guid.NewGuid(),
            turnEntryId: null,
            pendingTurnId: null);

        Assert.NotNull(prep.ErrorMessage);
        Assert.Contains("persist failed", prep.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(prep.RehydratedContents);

    }

    [Fact]
    public async Task PrepareAsync_CallerCancellation_PropagatesWithoutValidationError()
    {

        FakeSessionAttachmentStore store = new() { PersistThrowsCanceled = true };
        PingRequest request = new(
            "hi",
            SessionId: Guid.NewGuid(),
            AttachedFiles: [new AttachedFileDto("a.txt", "x")]);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SessionAttachmentTurnService.PrepareAsync(
                request,
                store,
                new ArcanumSettings(),
                turnSessionId: Guid.NewGuid(),
                turnEntryId: null,
                pendingTurnId: null,
                cts.Token));

    }

    [Fact]
    public async Task PrepareAsync_RehydratesTextAndImageReferences()
    {

        Guid sessionId = Guid.NewGuid();
        Guid textId = Guid.NewGuid();
        Guid imageId = Guid.NewGuid();
        byte[] imageBytes = [1, 2, 3, 4];

        FakeSessionAttachmentStore store = new()
        {
            Records =
            {
                [textId] = new SessionAttachmentRecord(
                    textId,
                    sessionId,
                    null,
                    null,
                    SessionAttachmentState.Bound,
                    "notes",
                    "notes.txt",
                    1,
                    "rel/notes.txt",
                    "abc",
                    "text/plain",
                    5,
                    SessionAttachmentKind.Text,
                    DateTimeOffset.UtcNow),
                [imageId] = new SessionAttachmentRecord(
                    imageId,
                    sessionId,
                    null,
                    null,
                    SessionAttachmentState.Bound,
                    "shot",
                    "shot.png",
                    1,
                    "rel/shot.png",
                    "def",
                    "image/png",
                    4,
                    SessionAttachmentKind.Image,
                    DateTimeOffset.UtcNow),
            },
            BytesById =
            {
                [textId] = Encoding.UTF8.GetBytes("hello"),
                [imageId] = imageBytes,
            },
            ValidIds = { textId, imageId },
        };

        PingRequest request = new(
            "hi",
            SessionId: sessionId,
            Model: "vision-model",
            AttachmentReferences: [textId, imageId]);

        ArcanumSettings settings = new()
        {
            Scrying = new ScryingSettings { Enabled = true, MaxImageBytes = 1024 * 1024 },
            Providers =
            [
                new ProviderSettings
                {
                    Name = "test",
                    Models = [new ModelEntry("vision-model", SupportsVision: true)],
                },
            ],
        };

        SessionAttachmentTurnPreparation prep = await SessionAttachmentTurnService.PrepareAsync(
            request,
            store,
            settings,
            turnSessionId: sessionId,
            turnEntryId: null,
            pendingTurnId: null);

        Assert.Null(prep.ErrorMessage);
        Assert.Equal(2, prep.RehydratedContents.Count);
        Assert.IsType<TextContent>(prep.RehydratedContents[0]);
        Assert.Equal("hello", ((TextContent)prep.RehydratedContents[0]).Text);
        Assert.IsType<DataContent>(prep.RehydratedContents[1]);
        Assert.True(((DataContent)prep.RehydratedContents[1]).Data.Span.SequenceEqual(imageBytes));

    }

    [Fact]
    public async Task PrepareAsync_InvalidReferences_FailClosed()
    {

        FakeSessionAttachmentStore store = new();
        PingRequest request = new(
            "hi",
            SessionId: null,
            AttachmentReferences: [Guid.NewGuid()]);

        SessionAttachmentTurnPreparation prep = await SessionAttachmentTurnService.PrepareAsync(
            request,
            store,
            new ArcanumSettings(),
            turnSessionId: null,
            turnEntryId: null,
            pendingTurnId: null);

        Assert.Equal("AttachmentReferences require a SessionId.", prep.ErrorMessage);
        Assert.Equal(0, store.PersistCallCount);

    }

    private sealed class FakeSessionAttachmentStore : ISessionAttachmentStore
    {

        public int PersistCallCount { get; private set; }

        public Guid? LastPersistSessionId { get; private set; }

        public string? LastPersistPendingTurnId { get; private set; }

        public Guid? LastPersistEntryId { get; private set; }

        public string? LastLogicalNameHint { get; private set; }

        public SessionAttachmentKind LastKind { get; private set; }

        public string? LastMimeType { get; private set; }

        public ReadOnlyMemory<byte> LastBytes { get; private set; }

        public bool PersistThrows { get; init; }

        public bool PersistThrowsCanceled { get; init; }

        public Guid? IndexSessionId { get; init; }

        public Dictionary<Guid, SessionAttachmentRecord> Records { get; } = new();

        public Dictionary<Guid, ReadOnlyMemory<byte>> BytesById { get; } = new();

        public HashSet<Guid> ValidIds { get; } = [];

        public Task<SessionAttachmentRecord> PersistNewAsync(
            Guid? sessionId,
            string? pendingTurnId,
            Guid? entryId,
            string logicalNameHint,
            string originalFileName,
            ReadOnlyMemory<byte> bytes,
            string mimeType,
            SessionAttachmentKind kind,
            CancellationToken cancellationToken = default)
        {

            PersistCallCount++;
            LastPersistSessionId = sessionId;
            LastPersistPendingTurnId = pendingTurnId;
            LastPersistEntryId = entryId;
            LastLogicalNameHint = logicalNameHint;
            LastKind = kind;
            LastMimeType = mimeType;
            LastBytes = bytes.ToArray();

            if (PersistThrowsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (PersistThrows)
            {
                throw new InvalidOperationException("persist failed");
            }

            Guid id = Guid.NewGuid();

            return Task.FromResult(new SessionAttachmentRecord(
                id,
                sessionId,
                entryId,
                pendingTurnId,
                sessionId is null ? SessionAttachmentState.Pending : SessionAttachmentState.Bound,
                logicalNameHint,
                originalFileName,
                1,
                "rel",
                "hash",
                mimeType,
                bytes.Length,
                kind,
                DateTimeOffset.UtcNow));

        }

        public Task PromotePendingAsync(
            string pendingTurnId,
            Guid sessionId,
            Guid? entryId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SessionAttachmentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Records.TryGetValue(id, out SessionAttachmentRecord? record) ? record : null);

        public Task<SessionAttachmentRecord?> GetByLogicalAsync(
            Guid sessionId,
            string logicalKey,
            int? version,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(
            Guid sessionId,
            int maxItems,
            CancellationToken cancellationToken = default)
        {

            if (IndexSessionId is { } expected && expected == sessionId)
            {
                return Task.FromResult<IReadOnlyList<SessionAttachmentIndexItem>>(
                [
                    new SessionAttachmentIndexItem("notes", "notes.txt", [1], SessionAttachmentKind.Text, 11),
                ]);
            }

            return Task.FromResult<IReadOnlyList<SessionAttachmentIndexItem>>([]);

        }

        public Task<ReadOnlyMemory<byte>> ReadBytesAsync(
            SessionAttachmentRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                BytesById.TryGetValue(record.Id, out ReadOnlyMemory<byte> bytes)
                    ? bytes
                    : ReadOnlyMemory<byte>.Empty);

        public Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task ReconcileAsync(TimeSpan pendingOlderThan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ValidateReferencesAsync(
            Guid sessionId,
            IReadOnlyList<Guid> attachmentIds,
            int maxReferences,
            CancellationToken cancellationToken = default)
        {

            foreach (Guid id in attachmentIds)
            {
                if (!ValidIds.Contains(id))
                {
                    throw new InvalidOperationException($"Attachment '{id}' was not found.");
                }
            }

            return Task.CompletedTask;

        }

        public Task<IDisposable> AcquireSessionGateAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDisposable>(EmptyDisposable.Instance);

        public Task DeleteRowsForSessionInAmbientTransactionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteSessionDirectory(Guid sessionId) => true;

        public Task ClearEntryIdsInAmbientTransactionAsync(
            Guid sessionId,
            IReadOnlyList<Guid> entryIds,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundForForkAsync(
            Guid sourceSessionId,
            IReadOnlySet<Guid>? copiedSourceEntryIds,
            CancellationToken cancellationToken = default)
        {

            IEnumerable<SessionAttachmentRecord> bound = Records.Values.Where(r =>
                r.SessionId == sourceSessionId && r.State == SessionAttachmentState.Bound);

            if (copiedSourceEntryIds is not null)
            {
                bound = bound.Where(r => r.EntryId is { } eid && copiedSourceEntryIds.Contains(eid));
            }

            return Task.FromResult<IReadOnlyList<SessionAttachmentRecord>>(bound.ToList());

        }

        public Task CopyBytesForForkAsync(
            Guid forkSessionId,
            IReadOnlyList<SessionAttachmentForkCopyPlan> plans,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task InsertForkRowsInAmbientTransactionAsync(
            Guid forkSessionId,
            IReadOnlyList<SessionAttachmentForkCopyPlan> plans,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private sealed class EmptyDisposable : IDisposable
        {

            public static readonly EmptyDisposable Instance = new();

            public void Dispose()
            {
            }

        }

    }

}
