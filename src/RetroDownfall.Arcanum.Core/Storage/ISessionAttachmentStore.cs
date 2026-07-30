using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<SessionAttachmentKind>))]
public enum SessionAttachmentKind
{

    Text,

    Image,

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
public sealed record AttachmentSourceClaim(string AbsolutePath);

public sealed record AttachmentSourceResolution(
    AttachmentSourceMetadata Metadata,
    ReadOnlyMemory<byte> VerifiedBytes);

public interface IAttachmentSourceResolver
{
    Task<AttachmentSourceResolution> ResolveForPersistenceAsync(
        AttachmentSourceClaim claim,
        ReadOnlyMemory<byte> snapshotBytes,
        CancellationToken cancellationToken = default);

    Task<AttachmentSourceMetadata> RevalidateAsync(
        AttachmentSourceMetadata source,
        CancellationToken cancellationToken = default);
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
    AttachmentSourceMetadata? Source = null);

public sealed record SessionAttachmentIndexItem(
    string LogicalKey,
    string OriginalFileName,
    IReadOnlyList<int> Versions,
    SessionAttachmentKind Kind,
    long LatestByteLength);

/// <summary>
/// Preallocated fork attachment plan: new row identity + remapped entry, with source metadata for FS copy.
/// </summary>
public sealed record SessionAttachmentForkCopyPlan(
    SessionAttachmentRecord Source,
    Guid NewAttachmentId,
    Guid? NewEntryId);

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

    Task PromotePendingAsync(string pendingTurnId, Guid sessionId, Guid? entryId, CancellationToken cancellationToken = default);

    Task<SessionAttachmentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SessionAttachmentRecord?> GetByLogicalAsync(Guid sessionId, string logicalKey, int? version, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(Guid sessionId, int maxItems, CancellationToken cancellationToken = default);

    Task<ReadOnlyMemory<byte>> ReadBytesAsync(SessionAttachmentRecord record, CancellationToken cancellationToken = default);

    Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Startup / periodic reconciliation: stale pending GC, bound orphans (missing sessions / missing files),
    /// unreferenced temp/final files. Serializes with persist/promote/fork/purge via session and pending gates.
    /// </summary>
    Task ReconcileAsync(TimeSpan pendingOlderThan, CancellationToken cancellationToken = default);

    Task ValidateReferencesAsync(Guid sessionId, IReadOnlyList<Guid> attachmentIds, int maxReferences, CancellationToken cancellationToken = default);

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
