using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>No-op <see cref="ISessionAttachmentStore"/> for WizardIntelligenceProvider test factories.</summary>
internal sealed class NoOpSessionAttachmentStore(
    SessionAttachmentRecord? record = null,
    Func<Guid, CancellationToken, Task>? acquireSessionGate = null) : ISessionAttachmentStore
{

    public int PersistNewCallCount { get; private set; }

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

        PersistNewCallCount++;

        return Task.FromResult(new SessionAttachmentRecord(

            Guid.NewGuid(),

            sessionId,

            entryId,

            pendingTurnId,

            sessionId is null ? SessionAttachmentState.Pending : SessionAttachmentState.Bound,

            logicalNameHint,

            originalFileName,

            1,

            "noop",

            "noop",

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
        Task.FromResult(
            record?.Id == id
                ? record
                : null);

    public Task<SessionAttachmentRecord?> GetByLogicalAsync(
        Guid sessionId,
        string logicalKey,
        int? version,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            record is not null
                && record.SessionId == sessionId
                && string.Equals(record.LogicalKey, logicalKey, StringComparison.Ordinal)
                && (version is null || record.Version == version)
                    ? record
                    : null);

    public Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionAttachmentRecord>>([]);

    public Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(
        Guid sessionId,
        int maxItems,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionAttachmentIndexItem>>([]);

    public Task<ReadOnlyMemory<byte>> ReadBytesAsync(
        SessionAttachmentRecord record,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ReadOnlyMemory<byte>.Empty);

    public Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ReconcileAsync(TimeSpan pendingOlderThan, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ValidateReferencesAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        int maxReferences,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task<IDisposable> AcquireSessionGateAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {

        if (acquireSessionGate is not null)
        {

            await acquireSessionGate(sessionId, cancellationToken);

        }

        return EmptyDisposable.Instance;

    }

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
