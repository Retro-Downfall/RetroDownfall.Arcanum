using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<SessionAttachmentKind>))]
public enum SessionAttachmentKind
{

    Text,

    Image,

    Binary,

}

public enum SessionAttachmentState
{

    Pending,

    Bound,

}

public enum AttachmentSourceKind
{
    SnapshotOnly,
    WorkspaceFile,
}

public enum AttachmentSourceStatus
{
    NotApplicable,
    Refreshable,
    PriorVersion,
    Missing,
    Moved,
    Inaccessible,
    Unsafe,
    WorkspaceUnavailable,
    WorkspaceChanged,
    CorruptMetadata,
}

/// <summary>Encrypted-at-rest provenance for an attachment snapshot.</summary>
public sealed record AttachmentSourceMetadata(
    AttachmentSourceKind Kind,
    string? WorkspaceIdentity,
    string? WorkspaceRelativePath,
    string? LastKnownCanonicalPath,
    string? LastObservedContentSha256,
    string? LastObservedFileIdentity,
    DateTimeOffset? LastObservedWriteTime,
    long? LastObservedByteLength,
    AttachmentSourceStatus Status,
    string? DiagnosticReason)
{
    public bool IsRefreshable => Kind == AttachmentSourceKind.WorkspaceFile
        && Status == AttachmentSourceStatus.Refreshable;

    public static AttachmentSourceMetadata SnapshotOnly { get; } = new(
        AttachmentSourceKind.SnapshotOnly, null, null, null, null, null, null, null,
        AttachmentSourceStatus.NotApplicable, null);
}

/// <summary>
/// Host-trusted source claim. API clients must never construct this from an arbitrary path.
/// </summary>
public sealed record AttachmentSourceClaim(
    string AbsolutePath,
    string? WorkspaceRoot = null);

public sealed record AttachmentSourceResolution(
    AttachmentSourceMetadata Metadata,
    ReadOnlyMemory<byte> VerifiedBytes,
    string? DetectedMimeType = null);

public delegate Task<bool> AttachmentSourcePathAuthorizer(
    string canonicalPath,
    CancellationToken cancellationToken);

public interface IAttachmentSourceResolver
{
    Task<AttachmentSourceResolution> ResolveForPersistenceAsync(
        AttachmentSourceClaim claim,
        ReadOnlyMemory<byte> snapshotBytes,
        CancellationToken cancellationToken = default);

    Task<AttachmentSourceMetadata> RevalidateAsync(
        AttachmentSourceMetadata source,
        CancellationToken cancellationToken = default);

    Task<AttachmentSourceResolution> ResolveForReferenceAsync(
        AttachmentSourceClaim claim,
        long maxBytes,
        AttachmentSourcePathAuthorizer authorizeCanonicalPath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Live attachment references are not supported by this resolver.");

    Task<AttachmentSourceResolution> ResolveCurrentAsync(
        AttachmentSourceMetadata source,
        string expectedSnapshotSha256,
        long maxBytes,
        AttachmentSourcePathAuthorizer authorizeCanonicalPath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Current attachment source reads are not supported by this resolver.");
}

public sealed record SessionAttachmentRecord(
    Guid Id,
    Guid? SessionId,
    Guid? EntryId,
    string? PendingTurnId,
    SessionAttachmentState State,
    string LogicalKey,
    string OriginalFileName,
    int Version,
    string RelativePath,
    string ContentSha256,
    string MimeType,
    long ByteLength,
    SessionAttachmentKind Kind,
    DateTimeOffset CreatedAt,
    AttachmentSourceMetadata? Source = null,
    int EncryptionVersion = 0,
    string? EncryptionKeyId = null);

public sealed record SessionAttachmentIndexItem(
    string LogicalKey,
    string OriginalFileName,
    IReadOnlyList<int> Versions,
    SessionAttachmentKind Kind,
    long LatestByteLength,
    bool HasMoreVersions = false,
    int? NextVersion = null);

/// <summary>
/// Preallocated fork attachment plan: new row identity + remapped entry, with source metadata for FS copy.
/// </summary>
public sealed record SessionAttachmentForkCopyPlan(
    SessionAttachmentRecord Source,
    Guid NewAttachmentId,
    Guid? NewEntryId);

public sealed record SessionAttachmentRefreshPersistence(
    SessionAttachmentRecord Record,
    bool NewVersionCreated);

public interface ISessionAttachmentStore
{

    Task<SessionAttachmentRecord> PersistNewAsync(
        Guid? sessionId,
        string? pendingTurnId,
        Guid? entryId,
        string logicalNameHint,
        string originalFileName,
        ReadOnlyMemory<byte> bytes,
        string mimeType,
        SessionAttachmentKind kind,
        CancellationToken cancellationToken = default);

    Task<SessionAttachmentRecord> PersistNewFromSourceAsync(
        Guid? sessionId,
        string? pendingTurnId,
        Guid? entryId,
        string logicalNameHint,
        string originalFileName,
        ReadOnlyMemory<byte> bytes,
        string mimeType,
        SessionAttachmentKind kind,
        AttachmentSourceClaim source,
        CancellationToken cancellationToken = default) =>
        PersistNewAsync(
            sessionId,
            pendingTurnId,
            entryId,
            logicalNameHint,
            originalFileName,
            bytes,
            mimeType,
            kind,
            cancellationToken);

    Task<SessionAttachmentRecord> PersistNewResolvedSourceAsync(
        Guid? sessionId,
        string? pendingTurnId,
        Guid? entryId,
        string logicalNameHint,
        string originalFileName,
        SessionAttachmentKind kind,
        AttachmentSourceResolution source,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Resolved attachment source persistence is not supported by this store.");

    Task<SessionAttachmentRefreshPersistence> PersistRefreshedAsync(
        SessionAttachmentRecord latest,
        Guid? entryId,
        AttachmentSourceResolution current,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Attachment source refresh persistence is not supported by this store.");

    Task PromotePendingAsync(string pendingTurnId, Guid sessionId, Guid? entryId, CancellationToken cancellationToken = default);

    Task<SessionAttachmentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SessionAttachmentRecord?> GetByLogicalAsync(Guid sessionId, string logicalKey, int? version, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every bound version in stable logical-key/version pages. <paramref name="pageSize"/>
    /// bounds one yielded allocation only; iteration has no total row ceiling.
    /// </summary>
    async IAsyncEnumerable<IReadOnlyList<SessionAttachmentRecord>> ReadBoundPagesAsync(
        Guid sessionId,
        int pageSize = 128,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        int effectivePageSize = Math.Clamp(pageSize, 1, 512);

        IReadOnlyList<SessionAttachmentRecord> rows = await ListBoundAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);

        for (int offset = 0; offset < rows.Count; offset += effectivePageSize)
        {

            cancellationToken.ThrowIfCancellationRequested();

            yield return rows
                .Skip(offset)
                .Take(effectivePageSize)
                .ToArray();

        }

    }

    /// <summary>
    /// Returns only the newest bound row for each exact logical key.
    /// </summary>
    async Task<IReadOnlyList<SessionAttachmentRecord>> ListLatestBoundAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        (await ListBoundAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .GroupBy(static row => row.LogicalKey, StringComparer.Ordinal)
            .Select(static group => group.MaxBy(static row => row.Version)!)
            .OrderBy(static row => row.LogicalKey, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Returns the newest bound row only for the requested logical keys, preserving the
    /// caller's first-occurrence order. Production stores should push selection into bounded
    /// database-query pages; this compatibility implementation is for lightweight stores.
    /// </summary>
    async Task<IReadOnlyList<SessionAttachmentRecord>> ListLatestBoundByLogicalKeysAsync(
        Guid sessionId,
        IReadOnlyList<string> logicalKeys,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(logicalKeys);

        if (logicalKeys.Count == 0)
        {

            return [];
        }

        IReadOnlyList<SessionAttachmentRecord> latest =
            await ListLatestBoundAsync(
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);
        Dictionary<string, SessionAttachmentRecord> byLogicalKey =
            latest.ToDictionary(
                static row => row.LogicalKey,
                StringComparer.Ordinal);
        HashSet<string> emitted = new(StringComparer.Ordinal);
        List<SessionAttachmentRecord> selected = [];

        foreach (string logicalKey in logicalKeys)
        {

            if (emitted.Add(logicalKey)
                && byLogicalKey.TryGetValue(
                    logicalKey,
                    out SessionAttachmentRecord? row))
            {

                selected.Add(row);
            }
        }

        return selected;
    }

    Task<IReadOnlyList<SessionAttachmentRecord>> RevalidateBoundSourcesAsync(

        Guid sessionId,

        CancellationToken cancellationToken = default) =>

        ListBoundAsync(sessionId, cancellationToken);

    Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(Guid sessionId, int maxItems, CancellationToken cancellationToken = default);

    Task<ReadOnlyMemory<byte>> ReadBytesAsync(SessionAttachmentRecord record, CancellationToken cancellationToken = default);

    async Task<Stream> OpenReadAsync(
        SessionAttachmentRecord record,
        CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> bytes = await ReadBytesAsync(record, cancellationToken).ConfigureAwait(false);

        return new MemoryStream(bytes.ToArray(), writable: false);
    }

    Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Startup / periodic reconciliation: stale pending GC, bound orphans (missing sessions / missing files),
    /// unreferenced temp/final files. Serializes with persist/promote/fork/purge via session and pending gates.
    /// </summary>
    Task ReconcileAsync(TimeSpan pendingOlderThan, CancellationToken cancellationToken = default);

    Task ValidateReferencesAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires the per-session attachment gate used by purge, fork, and bound reconciliation.
    /// Caller must dispose the returned handle.
    /// </summary>
    Task<IDisposable> AcquireSessionGateAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all <c>SessionAttachments</c> rows for <paramref name="sessionId"/>.
    /// Must be called while <see cref="AcquireSessionGateAsync"/> is held and an EF ambient
    /// transaction is open on the shared <c>ArcanumDbContext</c> connection (raw SQL enlists).
    /// </summary>
    Task DeleteRowsForSessionInAmbientTransactionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort delete of <c>attachments/{sessionId}/</c> using <see cref="CancellationToken.None"/>.
    /// Returns <c>false</c> when the directory could not be removed (logged by caller / recovered by reconcile).
    /// </summary>
    bool TryDeleteSessionDirectory(Guid sessionId);

    /// <summary>
    /// Sets <c>EntryId = NULL</c> for the given entry ids. Must run under the session gate and an ambient EF transaction.
    /// </summary>
    Task ClearEntryIdsInAmbientTransactionAsync(
        Guid sessionId,
        IReadOnlyList<Guid> entryIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bound rows to copy for a fork. Full fork: every Bound row (incl. EntryId-null).
    /// Cutoff: only rows whose non-null EntryId is in <paramref name="copiedSourceEntryIds"/>.
    /// </summary>
    Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundForForkAsync(
        Guid sourceSessionId,
        IReadOnlySet<Guid>? copiedSourceEntryIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams bounded pages of source attachment metadata for a fork. A full fork includes
    /// entryless rows; a cutoff fork excludes them and selects only rows whose source entry
    /// sequence is at or before <paramref name="maximumSourceEntrySequence"/>.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<SessionAttachmentRecord>> ReadBoundForForkPagesAsync(
        Guid sourceSessionId,
        long maximumSourceEntrySequence,
        bool includeEntrylessAttachments,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Paged fork attachment enumeration is not supported by this store.");

    /// <summary>
    /// Copies and hash-verifies attachment bytes into the fork session tree before the DB transaction.
    /// On failure, deletes any partially written fork tree.
    /// </summary>
    Task CopyBytesForForkAsync(
        Guid forkSessionId,
        IReadOnlyList<SessionAttachmentForkCopyPlan> plans,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts fork attachment rows. Must run under session gates and an ambient EF transaction.
    /// </summary>
    Task InsertForkRowsInAmbientTransactionAsync(
        Guid forkSessionId,
        IReadOnlyList<SessionAttachmentForkCopyPlan> plans,
        CancellationToken cancellationToken = default);

}
