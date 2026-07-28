using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SessionAttachmentRequestValidatorTests
{

    [Fact]
    public async Task ValidateAsync_NullSessionIdWithRefs_Fails()
    {

        Guid attachmentId = Guid.NewGuid();
        StubSessionAttachmentStore store = new();
        AttachmentsSettings settings = ResolveAttachments(enabled: true);

        PingRequest request = new(
            Prompt: "hi",
            SessionId: null,
            AttachmentReferences: [attachmentId]);

        string? error = await SessionAttachmentRequestValidator.ValidateAsync(
            request,
            store,
            settings);

        Assert.Equal("AttachmentReferences require a SessionId.", error);
        Assert.Equal(0, store.ValidateCallCount);

    }

    [Fact]
    public async Task ValidateAsync_OverMaxReferences_Fails()
    {

        Guid sessionId = Guid.NewGuid();
        StubSessionAttachmentStore store = new();
        AttachmentsSettings settings = ResolveAttachments(enabled: true);
        int maxReferences = ArcanumSettingClamps.AttachmentsMaxReferencesPerTurn(
            ArcanumRuntimeDefaults.Attachments.MaxReferencesPerTurn);

        PingRequest request = new(
            Prompt: "hi",
            SessionId: sessionId,
            AttachmentReferences: Enumerable.Range(0, maxReferences + 1)
                .Select(static _ => Guid.NewGuid())
                .ToList());

        string? error = await SessionAttachmentRequestValidator.ValidateAsync(
            request,
            store,
            settings);

        Assert.Equal(
            $"At most {maxReferences} attachment references are allowed per request.",
            error);
        Assert.Equal(0, store.ValidateCallCount);

    }

    [Fact]
    public async Task ValidateAsync_HappyPath_Succeeds()
    {

        Guid sessionId = Guid.NewGuid();
        Guid attachmentId = Guid.NewGuid();
        StubSessionAttachmentStore store = new() { ValidIds = [attachmentId] };
        AttachmentsSettings settings = ResolveAttachments(enabled: true);

        PingRequest request = new(
            Prompt: "hi",
            SessionId: sessionId,
            AttachmentReferences: [attachmentId]);

        string? error = await SessionAttachmentRequestValidator.ValidateAsync(
            request,
            store,
            settings);

        Assert.Null(error);
        Assert.Equal(1, store.ValidateCallCount);
        Assert.Equal(sessionId, store.LastSessionId);
        Assert.Equal([attachmentId], store.LastIds);

    }

    [Fact]
    public async Task ValidateAsync_DisabledWithRefs_Fails()
    {

        Guid sessionId = Guid.NewGuid();
        StubSessionAttachmentStore store = new();
        AttachmentsSettings settings = ResolveAttachments(enabled: false);

        PingRequest request = new(
            Prompt: "hi",
            SessionId: sessionId,
            AttachmentReferences: [Guid.NewGuid()]);

        string? error = await SessionAttachmentRequestValidator.ValidateAsync(
            request,
            store,
            settings);

        Assert.Contains("disabled", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.ValidateCallCount);

    }

    [Fact]
    public async Task ValidateAsync_NullOrEmptyRefs_NoOp()
    {

        StubSessionAttachmentStore store = new();
        AttachmentsSettings settings = ResolveAttachments(enabled: false);

        string? nullRefs = await SessionAttachmentRequestValidator.ValidateAsync(
            new PingRequest(Prompt: "hi", AttachmentReferences: null),
            store,
            settings);

        string? emptyRefs = await SessionAttachmentRequestValidator.ValidateAsync(
            new PingRequest(Prompt: "hi", AttachmentReferences: []),
            store,
            settings);

        Assert.Null(nullRefs);
        Assert.Null(emptyRefs);
        Assert.Equal(0, store.ValidateCallCount);

    }

    private static AttachmentsSettings ResolveAttachments(bool enabled) =>
        new ArcanumSettings
        {
            Features = new FeatureSettings { Attachments = enabled },
        }.ResolveAttachments();

    private sealed class StubSessionAttachmentStore : ISessionAttachmentStore
    {

        public HashSet<Guid> ValidIds { get; init; } = [];

        public int ValidateCallCount { get; private set; }

        public Guid? LastSessionId { get; private set; }

        public IReadOnlyList<Guid>? LastIds { get; private set; }

        public Task ValidateReferencesAsync(
            Guid sessionId,
            IReadOnlyList<Guid> attachmentIds,
            int maxReferences,
            CancellationToken cancellationToken = default)
        {

            ValidateCallCount++;
            LastSessionId = sessionId;
            LastIds = attachmentIds.ToList();

            if (attachmentIds.Count > maxReferences)
            {
                throw new InvalidOperationException(
                    $"Too many attachment references ({attachmentIds.Count}); max is {maxReferences}.");
            }

            foreach (Guid id in attachmentIds)
            {
                if (!ValidIds.Contains(id))
                {
                    throw new InvalidOperationException($"Attachment '{id}' was not found.");
                }
            }

            return Task.CompletedTask;

        }

        public Task<SessionAttachmentRecord> PersistNewAsync(
            Guid? sessionId,
            string? pendingTurnId,
            Guid? entryId,
            string logicalNameHint,
            string originalFileName,
            ReadOnlyMemory<byte> bytes,
            string mimeType,
            SessionAttachmentKind kind,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PromotePendingAsync(
            string pendingTurnId,
            Guid sessionId,
            Guid? entryId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionAttachmentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionAttachmentRecord?> GetByLogicalAsync(
            Guid sessionId,
            string logicalKey,
            int? version,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(
            Guid sessionId,
            int maxItems,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReadOnlyMemory<byte>> ReadBytesAsync(
            SessionAttachmentRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReconcileAsync(TimeSpan pendingOlderThan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionAttachmentRecord>>([]);

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
