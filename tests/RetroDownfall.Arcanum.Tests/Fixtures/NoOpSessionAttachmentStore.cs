using RetroDownfall.Arcanum.Core.Storage;

using System.Runtime.CompilerServices;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>No-op <see cref="ISessionAttachmentStore"/> for WizardIntelligenceProvider test factories.</summary>
internal sealed class NoOpSessionAttachmentStore(
    SessionAttachmentRecord? record = null,
    Func<Guid, CancellationToken, Task>? acquireSessionGate = null,
    IReadOnlyList<SessionAttachmentRecord>? forkRecords = null,
    Func<Guid, IReadOnlyList<SessionAttachmentForkCopyPlan>, CancellationToken, Task>? copyForkPage = null,
    Func<Guid, IReadOnlyList<SessionAttachmentForkCopyPlan>, CancellationToken, Task>? insertForkPage = null,
    IReadOnlyDictionary<Guid, SessionAttachmentRecord>? records = null,
    Func<SessionAttachmentRecord, CancellationToken, Task<ReadOnlyMemory<byte>>>? readBytes = null,
    Func<SessionAttachmentRecord, CancellationToken, Task<Stream>>? openRead = null,
    Func<Guid, IReadOnlyList<Guid>, CancellationToken, Task>? clearEntryIds = null) : ISessionAttachmentStore
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
            records?.GetValueOrDefault(id)
            ?? (record?.Id == id ? record : null));

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
        Task.FromResult<IReadOnlyList<SessionAttachmentRecord>>(
            (records?.Values ?? (record is null ? [] : [record]))
                .Where(item => item.SessionId == sessionId
                    && item.State == SessionAttachmentState.Bound)
                .ToArray());

    public Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(
        Guid sessionId,
        int maxItems,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionAttachmentIndexItem>>([]);

    public Task<ReadOnlyMemory<byte>> ReadBytesAsync(
        SessionAttachmentRecord record,
        CancellationToken cancellationToken = default) =>
        readBytes?.Invoke(record, cancellationToken)
        ?? Task.FromResult(ReadOnlyMemory<byte>.Empty);

    public Task<Stream> OpenReadAsync(
        SessionAttachmentRecord record,
        CancellationToken cancellationToken = default) =>
        openRead?.Invoke(record, cancellationToken)
        ?? ISessionAttachmentStoreOpenReadFallback.ReadAsync(
            this,
            record,
            cancellationToken);

    public Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ReconcileAsync(TimeSpan pendingOlderThan, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ValidateReferencesAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
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
        clearEntryIds?.Invoke(sessionId, entryIds, cancellationToken)
        ?? Task.CompletedTask;

    public Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundForForkAsync(
        Guid sourceSessionId,
        IReadOnlySet<Guid>? copiedSourceEntryIds,
        CancellationToken cancellationToken = default)
    {

        IReadOnlyList<SessionAttachmentRecord> selected = forkRecords ?? [];

        if (copiedSourceEntryIds is not null)
        {

            selected = selected
                .Where(item => item.EntryId is Guid entryId && copiedSourceEntryIds.Contains(entryId))
                .ToArray();

        }

        return Task.FromResult(selected);

    }

    public async IAsyncEnumerable<IReadOnlyList<SessionAttachmentRecord>> ReadBoundForForkPagesAsync(
        Guid sourceSessionId,
        long maximumSourceEntrySequence,
        bool includeEntrylessAttachments,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        const int pageSize = 128;

        SessionAttachmentRecord[] selected = (forkRecords ?? [])
            .Where(item => includeEntrylessAttachments || item.EntryId is not null)
            .ToArray();

        for (int offset = 0; offset < selected.Length; offset += pageSize)
        {

            cancellationToken.ThrowIfCancellationRequested();

            yield return selected
                .Skip(offset)
                .Take(pageSize)
                .ToArray();

            await Task.Yield();

        }

    }

    public Task CopyBytesForForkAsync(
        Guid forkSessionId,
        IReadOnlyList<SessionAttachmentForkCopyPlan> plans,
        CancellationToken cancellationToken = default) =>
        copyForkPage?.Invoke(forkSessionId, plans, cancellationToken)
        ?? Task.CompletedTask;

    public Task InsertForkRowsInAmbientTransactionAsync(
        Guid forkSessionId,
        IReadOnlyList<SessionAttachmentForkCopyPlan> plans,
        CancellationToken cancellationToken = default) =>
        insertForkPage?.Invoke(forkSessionId, plans, cancellationToken)
        ?? Task.CompletedTask;

    private sealed class EmptyDisposable : IDisposable
    {

        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }

    }

    private static class ISessionAttachmentStoreOpenReadFallback
    {

        public static async Task<Stream> ReadAsync(
            ISessionAttachmentStore store,
            SessionAttachmentRecord record,
            CancellationToken cancellationToken)
        {

            ReadOnlyMemory<byte> bytes = await store
                .ReadBytesAsync(record, cancellationToken)
                .ConfigureAwait(false);

            return new MemoryStream(bytes.ToArray(), writable: false);

        }

    }

}
