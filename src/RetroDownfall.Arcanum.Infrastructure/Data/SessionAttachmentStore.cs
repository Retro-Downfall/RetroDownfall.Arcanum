using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Caching;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL + disk persistence for session attachments. Bytes live under
/// <see cref="ArcanumPaths.AttachmentsDirectory"/> (or a test root); metadata in
/// <c>SessionAttachments</c> via the scoped <see cref="ArcanumDbContext"/> connection.
/// </summary>
internal sealed partial class SessionAttachmentStore : ISessionAttachmentStore
{

    private const int AttachmentIoPageSize = 64 * 1024;

    private const int DefaultBoundRecordPageSize = 128;

    private const int MaximumBoundRecordPageSize = 512;

    private const int IndexVersionPageSize = 256;

    private const int LatestLogicalKeyQueryPageSize = 128;

    /// <summary>
    /// Named gates shared by PersistNew, PromotePending, fork, purge, and GC.
    /// Session: <c>session|{sessionId:N}</c>; bound: <c>bound|{sessionId:N}|{logicalKey}</c>;
    /// pending turn: <c>pending|{turnId}</c>.
    /// </summary>
    internal static readonly KeyedLock<string> AttachmentGates = new(StringComparer.Ordinal);

    private readonly ArcanumDbContext _db;

    private readonly IOptions<ArcanumSettings> _options;

    private readonly ILogger _logger;

    private readonly string _attachmentsRoot;

    private readonly IAttachmentSourceResolver? _sourceResolver;

    private readonly IEncryptedBlobStore _blobStore;

    private readonly ISessionAttachmentIndexQueue? _indexQueue;

    private readonly ConditionalWeakTable<SessionAttachmentForkCopyPlan, ForkBlobAuthority>
        _forkBlobAuthorities = new();

    private int _boundRecordPageReadCountForTesting;

    private int _latestLogicalKeyQueryPageCountForTesting;

    /// <summary>
    /// Test seam: runs after bytes are on disk at the destination, before the DB write that
    /// records them. Used to simulate exhausted DB failure without holding FS work inside
    /// <see cref="SqliteBusyRetry"/>.
    /// </summary>
    internal Func<CancellationToken, Task>? AfterBytesCommittedBeforeDbForTesting { get; set; }

    internal Func<CancellationToken, Task>?
        AfterWriterLockBeforeBlobValidationForTesting
    { get; set; }

    /// <summary>
    /// Test seam: runs inside the missing-file sweep, after it has snapshotted the rows but before it
    /// evaluates any of them. Used to drive the promote-versus-sweep race deterministically.
    /// </summary>
    internal Func<CancellationToken, Task>? AfterMissingFileSnapshotForTesting { get; set; }

    /// <summary>
    /// Test seam: runs inside the orphan-file sweep, after it has snapshotted the referenced paths but
    /// before it walks the attachment tree. Used to drive a concurrent attachment write deterministically.
    /// </summary>
    internal Func<CancellationToken, Task>? AfterOrphanPathSnapshotForTesting { get; set; }

    internal int BoundRecordPageReadCountForTesting =>
        Volatile.Read(ref _boundRecordPageReadCountForTesting);

    internal int LatestLogicalKeyQueryPageCountForTesting =>
        Volatile.Read(ref _latestLogicalKeyQueryPageCountForTesting);

    public SessionAttachmentStore(
        ArcanumDbContext db,
        IOptions<ArcanumSettings> options,
        string? attachmentsRoot = null,
        IEncryptedBlobStore? blobStore = null,
        ILogger<SessionAttachmentStore>? logger = null,
        IAttachmentSourceResolver? sourceResolver = null,
        ISessionAttachmentIndexQueue? indexQueue = null)
    {

        _db = db;

        _options = options;

        _logger = logger ?? NullLogger<SessionAttachmentStore>.Instance;
        _sourceResolver = sourceResolver;
        _indexQueue = indexQueue;
        _blobStore = blobStore
            ?? throw new ArgumentNullException(
                nameof(blobStore),
                "Session attachment storage requires encrypted blob storage.");

        _attachmentsRoot = Path.GetFullPath(attachmentsRoot ?? ArcanumPaths.AttachmentsDirectory);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(_attachmentsRoot);

    }

    public async Task<SessionAttachmentRecord> PersistNewFromSourceAsync(
        Guid? sessionId,
        string? pendingTurnId,
        Guid? entryId,
        string logicalNameHint,
        string originalFileName,
        ReadOnlyMemory<byte> bytes,
        string mimeType,
        SessionAttachmentKind kind,
        AttachmentSourceClaim source,
        CancellationToken cancellationToken = default)
    {
        AttachmentSourceResolution resolution = _sourceResolver is null
            ? new(AttachmentSourceMetadata.SnapshotOnly with
                {
                    Status = AttachmentSourceStatus.WorkspaceUnavailable,
                    DiagnosticReason = "No host attachment source resolver is available.",
                }, ReadOnlyMemory<byte>.Empty)
            : await _sourceResolver.ResolveForPersistenceAsync(source, bytes, cancellationToken).ConfigureAwait(false);

        PersistNewCoreResult persisted = await PersistNewCoreAsync(
            sessionId, pendingTurnId, entryId, logicalNameHint, originalFileName,
            bytes, mimeType, kind, resolution.Metadata, cancellationToken).ConfigureAwait(false);
        return persisted.Record;
    }

    public async Task<SessionAttachmentRecord> PersistNewAsync(
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
        PersistNewCoreResult persisted = await PersistNewCoreAsync(
            sessionId,
            pendingTurnId,
            entryId,
            logicalNameHint,
            originalFileName,
            bytes,
            mimeType,
            kind,
            source: null,
            cancellationToken).ConfigureAwait(false);
        return persisted.Record;
    }

    public async Task<SessionAttachmentRecord> PersistNewResolvedSourceAsync(
        Guid? sessionId,
        string? pendingTurnId,
        Guid? entryId,
        string logicalNameHint,
        string originalFileName,
        SessionAttachmentKind kind,
        AttachmentSourceResolution source,
        CancellationToken cancellationToken = default)
    {
        AttachmentSourceMetadata persistedSource = ValidateResolvedSource(
            source,
            allowPriorVersion: false);

        PersistNewCoreResult persisted = await PersistNewCoreAsync(
            sessionId,
            pendingTurnId,
            entryId,
            logicalNameHint,
            originalFileName,
            source.VerifiedBytes,
            source.DetectedMimeType!,
            kind,
            persistedSource,
            cancellationToken).ConfigureAwait(false);

        return persisted.Record;
    }

    private static AttachmentSourceMetadata ValidateResolvedSource(
        AttachmentSourceResolution source,
        bool allowPriorVersion)
    {
        ArgumentNullException.ThrowIfNull(source);

        bool validStatus = source.Metadata.Status == AttachmentSourceStatus.Refreshable
            || (allowPriorVersion && source.Metadata.Status == AttachmentSourceStatus.PriorVersion);

        if (source.Metadata.Kind != AttachmentSourceKind.WorkspaceFile
            || !validStatus
            || string.IsNullOrWhiteSpace(source.Metadata.WorkspaceIdentity)
            || string.IsNullOrWhiteSpace(source.Metadata.WorkspaceRelativePath)
            || string.IsNullOrWhiteSpace(source.Metadata.LastKnownCanonicalPath)
            || string.IsNullOrWhiteSpace(source.Metadata.LastObservedContentSha256)
            || string.IsNullOrWhiteSpace(source.Metadata.LastObservedFileIdentity)
            || source.Metadata.LastObservedWriteTime is null
            || string.IsNullOrWhiteSpace(source.DetectedMimeType)
            || source.Metadata.LastObservedByteLength != source.VerifiedBytes.Length)
        {
            throw new InvalidOperationException(
                "The attachment source was not securely resolved with complete provenance and a MIME type.");
        }

        string verifiedHash = Convert.ToHexString(SHA256.HashData(source.VerifiedBytes.Span));

        if (!verifiedHash.Equals(
                source.Metadata.LastObservedContentSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The attachment source hash does not match the verified bytes.");
        }

        return source.Metadata with
        {
            Status = AttachmentSourceStatus.Refreshable,
            DiagnosticReason = null,
        };
    }

    public async Task<SessionAttachmentRefreshPersistence> PersistRefreshedAsync(
        SessionAttachmentRecord latest,
        Guid? entryId,
        AttachmentSourceResolution current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(latest);

        if (latest.State != SessionAttachmentState.Bound || latest.SessionId is not { } sessionId)
        {
            throw new InvalidOperationException("Only a bound session attachment can be refreshed.");
        }

        AttachmentSourceMetadata persistedSource = ValidateResolvedSource(
            current,
            allowPriorVersion: true);

        SessionAttachmentKind refreshedKind = SessionAttachmentContentPolicy.Classify(
            current.DetectedMimeType!);

        PersistNewCoreResult persisted = await PersistNewCoreAsync(
            sessionId,
            pendingTurnId: null,
            entryId,
            latest.LogicalKey,
            latest.OriginalFileName,
            current.VerifiedBytes,
            current.DetectedMimeType!,
            refreshedKind,
            persistedSource,
            cancellationToken).ConfigureAwait(false);

        return new SessionAttachmentRefreshPersistence(
            persisted.Record,
            persisted.NewVersionCreated);
    }

    private async Task<PersistNewCoreResult> PersistNewCoreAsync(
        Guid? sessionId,
        string? pendingTurnId,
        Guid? entryId,
        string logicalNameHint,
        string originalFileName,
        ReadOnlyMemory<byte> bytes,
        string mimeType,
        SessionAttachmentKind kind,
        AttachmentSourceMetadata? source,
        CancellationToken cancellationToken)
    {

        if (sessionId is null && string.IsNullOrWhiteSpace(pendingTurnId))
        {

            throw new ArgumentException("Either sessionId or pendingTurnId is required.");

        }

        if (sessionId is not null && !string.IsNullOrWhiteSpace(pendingTurnId))
        {

            throw new ArgumentException("Provide either sessionId or pendingTurnId, not both.");

        }

        string? validatedPendingTurnId = null;

        if (sessionId is null)
        {

            if (!SessionAttachmentPathSanitizer.TryValidatePendingTurnId(
                    pendingTurnId,
                    out validatedPendingTurnId,
                    out string turnError))
            {

                throw new ArgumentException($"Unsafe pending turn id: {turnError}", nameof(pendingTurnId));

            }

        }

        if (!SessionAttachmentPathSanitizer.TrySanitize(logicalNameHint, out string logicalKey, out string logicalError))
        {

            throw new ArgumentException($"Unsafe logical name: {logicalError}", nameof(logicalNameHint));

        }

        if (!SessionAttachmentPathSanitizer.TrySanitize(originalFileName, out string safeFileName, out string fileError))
        {

            throw new ArgumentException($"Unsafe original file name: {fileError}", nameof(originalFileName));

        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        string contentSha256 = ComputeContentSha256(bytes);

        IDisposable? sessionGate = null;

        if (sessionId is not null)
        {

            sessionGate = await AttachmentGates
                .AcquireAsync(SessionGateKey(sessionId.Value), cancellationToken)
                .ConfigureAwait(false);

        }

        using IDisposable? sessionGateLease = sessionGate;

        string gateKey = sessionId is not null
            ? BoundGateKey(sessionId.Value, logicalKey)
            : PendingTurnGateKey(validatedPendingTurnId!);

        using IDisposable gate = await AttachmentGates.AcquireAsync(gateKey, cancellationToken).ConfigureAwait(false);

        AttachmentsSettings attachments = _options.Value.ResolveAttachments();

        long maxBytes = ArcanumSettingClamps.AttachmentsMaxBytesPerSession(attachments.MaxBytesPerSession);

        SessionAttachmentRecord? latest = await FindLatestAsync(sessionId, validatedPendingTurnId, logicalKey, cancellationToken)
            .ConfigureAwait(false);

        if (latest is not null

            && string.Equals(

                latest.ContentSha256,

                contentSha256,

                StringComparison.OrdinalIgnoreCase)

            && HasSameSourceIdentity(latest.Source, source))
        {
            if (source is null)
            {
                return new PersistNewCoreResult(latest, NewVersionCreated: false);
            }

            await UpdateSourceAsync(latest.Id, source, cancellationToken).ConfigureAwait(false);
            return new PersistNewCoreResult(
                latest with { Source = source },
                NewVersionCreated: false);

        }

        if (latest?.Version == int.MaxValue)
        {

            throw new InvalidOperationException(
                $"Attachment version protocol boundary reached for logical key '{logicalKey}': "
                + $"measured version {latest.Version}; limit {int.MaxValue}. Existing versions remain saved. "
                + "Use a new logical attachment name to continue.");

        }

        int nextVersion = latest is null ? 1 : latest.Version + 1;

        long existingBytes = await SumByteLengthAsync(sessionId, validatedPendingTurnId, cancellationToken).ConfigureAwait(false);

        if (existingBytes + bytes.Length > maxBytes)
        {

            throw new InvalidOperationException(
                "Physical session-attachment storage boundary reached: "
                + $"measured {existingBytes + bytes.Length} bytes; limit {maxBytes} bytes. "
                + "Existing attachment versions remain saved; delete unneeded versions, use a new session, "
                + "or retry with a smaller attachment.");

        }

        string relativePath = BuildRelativePath(sessionId, validatedPendingTurnId, logicalKey, nextVersion, safeFileName);

        string absolutePath = ResolveUnderRoot(relativePath);

        Guid id = Guid.NewGuid();

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        EncryptedBlobDescriptor descriptor;

        await using (EncryptedBlobWriter writer = await _blobStore
            .CreateWriterAsync(
                absolutePath,
                EncryptedBlobPurpose.SessionAttachment,
                id.ToByteArray(),
                cancellationToken)
            .ConfigureAwait(false))
        {

            for (int offset = 0; offset < bytes.Length; offset += AttachmentIoPageSize)
            {

                cancellationToken.ThrowIfCancellationRequested();

                int count = Math.Min(AttachmentIoPageSize, bytes.Length - offset);

                await writer
                    .WriteAsync(bytes.Slice(offset, count), cancellationToken)
                    .ConfigureAwait(false);

            }

            descriptor = await writer
                .CompleteAsync(cancellationToken)
                .ConfigureAwait(false);

        }

        if (descriptor.PlaintextLength != bytes.Length)
        {

            throw new InvalidDataException(
                "Encrypted attachment length did not match the supplied plaintext length.");

        }

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                absolutePath,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact blobAuthority))
        {

            throw new IOException(
                "Attachment persistence could not capture the owned blob identity.");

        }

        SessionAttachmentState state = sessionId is null
            ? SessionAttachmentState.Pending
            : SessionAttachmentState.Bound;

        SessionAttachmentRecord record = new(
            id,
            sessionId,
            sessionId is null ? null : entryId,
            sessionId is null ? validatedPendingTurnId : null,
            state,
            logicalKey,
            safeFileName,
            nextVersion,
            NormalizeRelativePath(relativePath),
            contentSha256,
            mimeType,
            bytes.Length,
            kind,
            createdAt,
            Source: source,
            EncryptionVersion: descriptor.Version,
            EncryptionKeyId: descriptor.KeyId);

        try
        {

            if (AfterBytesCommittedBeforeDbForTesting is not null)
            {

                await AfterBytesCommittedBeforeDbForTesting(cancellationToken).ConfigureAwait(false);

            }

            await SqliteBusyRetry.ExecuteAsync(
                () => InsertRowAsync(
                    record,
                    blobAuthority,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            _ = IdentityOwnedFileSystemCleanup.TryDelete(blobAuthority);

            throw;

        }

        if (sessionId is { } boundSessionId)
        {

            _ = _indexQueue?.TryEnqueue(new SessionAttachmentIndexRequest(id, boundSessionId));

        }

        return new PersistNewCoreResult(record, NewVersionCreated: true);

    }

    private sealed record PersistNewCoreResult(
        SessionAttachmentRecord Record,
        bool NewVersionCreated);

    private sealed record ForkBlobAuthority(
        IdentityOwnedFileSystemArtifact Artifact);

    public async Task PromotePendingAsync(
        string pendingTurnId,
        Guid sessionId,
        Guid? entryId,
        CancellationToken cancellationToken = default)
    {

        if (!SessionAttachmentPathSanitizer.TryValidatePendingTurnId(
                pendingTurnId,
                out string validatedPendingTurnId,
                out string turnError))
        {

            throw new ArgumentException($"Unsafe pending turn id: {turnError}", nameof(pendingTurnId));

        }

        using IDisposable sessionGate = await AttachmentGates
            .AcquireAsync(SessionGateKey(sessionId), cancellationToken)
            .ConfigureAwait(false);

        using IDisposable gate = await AttachmentGates
            .AcquireAsync(PendingTurnGateKey(validatedPendingTurnId), cancellationToken)
            .ConfigureAwait(false);

        List<SessionAttachmentRecord> pending = await ListPendingByTurnAsync(validatedPendingTurnId, cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {

            return;

        }

        List<PromotionPlan> plans = [];

        try
        {

            foreach (SessionAttachmentRecord row in pending)
            {

                string oldAbsolute = ResolveUnderRoot(row.RelativePath);

                string newRelative = BuildRelativePath(
                    sessionId,
                    pendingTurnId: null,
                    row.LogicalKey,
                    row.Version,
                    row.OriginalFileName);

                string newAbsolute = ResolveUnderRoot(newRelative);

                if (!File.Exists(oldAbsolute))
                {

                    throw new InvalidOperationException($"Pending attachment file missing: {row.RelativePath}");

                }

                await AtomicCopyFileAsync(oldAbsolute, newAbsolute, cancellationToken).ConfigureAwait(false);

                plans.Add(new PromotionPlan(row, oldAbsolute, newAbsolute, NormalizeRelativePath(newRelative)));

            }

            if (AfterBytesCommittedBeforeDbForTesting is not null)
            {

                await AfterBytesCommittedBeforeDbForTesting(cancellationToken).ConfigureAwait(false);

            }

            await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {

                    DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                        .ConfigureAwait(false);

                    foreach (PromotionPlan plan in plans)
                    {

                        await using DbCommand cmd = connection.CreateCommand();

                        cmd.Transaction = transaction;

                        cmd.CommandText =
                            """
                            UPDATE "SessionAttachments"
                            SET "SessionId" = @sessionId,
                                "EntryId" = @entryId,
                                "PendingTurnId" = NULL,
                                "State" = @state,
                                "RelativePath" = @relativePath
                            WHERE "Id" = @id
                              AND "State" = @pendingState
                            """;

                        AddParameter(cmd, "@sessionId", sessionId.ToString());

                        AddParameter(cmd, "@entryId", entryId is null ? DBNull.Value : entryId.Value.ToString());

                        AddParameter(cmd, "@state", nameof(SessionAttachmentState.Bound));

                        AddParameter(cmd, "@relativePath", plan.NewRelativePath);

                        AddParameter(cmd, "@id", plan.Row.Id.ToString());

                        AddParameter(cmd, "@pendingState", nameof(SessionAttachmentState.Pending));

                        int updated = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                        if (updated != 1)
                        {

                            throw new InvalidOperationException(
                                $"Pending attachment '{plan.Row.Id}' could not be promoted (missing or already bound).");

                        }

                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                },
                cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            foreach (PromotionPlan plan in plans)
            {

                TryDeleteFile(plan.NewAbsolutePath);

            }

            throw;

        }

        foreach (PromotionPlan plan in plans)
        {

            TryDeleteFile(plan.OldAbsolutePath);

        }

        TryDeletePendingTurnDirectory(validatedPendingTurnId);

        foreach (PromotionPlan plan in plans)
        {

            _ = _indexQueue?.TryEnqueue(new SessionAttachmentIndexRequest(plan.Row.Id, sessionId));

        }

    }

    public async Task<SessionAttachmentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                           "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                           "SourceKind", "SourceWorkspaceIdentity", "SourceRelativePath", "SourceCanonicalPath",
                           "SourceContentSha256", "SourceFileIdentity", "SourceLastWriteAt", "SourceByteLength",
                           "SourceStatus", "SourceDiagnosticReason", "EncryptionVersion", "EncryptionKeyId"
                    FROM "SessionAttachments"
                    WHERE "Id" = @id
                    LIMIT 1
                    """;

                AddParameter(cmd, "@id", id.ToString());

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    return null;

                }

                return ReadRecord(reader);

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<SessionAttachmentRecord?> GetByLogicalAsync(
        Guid sessionId,
        string logicalKey,
        int? version,
        CancellationToken cancellationToken = default)
    {

        if (!SessionAttachmentPathSanitizer.TrySanitize(logicalKey, out string sanitizedKey, out _))
        {

            return null;

        }

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                if (version is null)
                {

                    cmd.CommandText =
                        """
                        SELECT "Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                               "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                               "SourceKind", "SourceWorkspaceIdentity", "SourceRelativePath", "SourceCanonicalPath",
                               "SourceContentSha256", "SourceFileIdentity", "SourceLastWriteAt", "SourceByteLength",
                               "SourceStatus", "SourceDiagnosticReason", "EncryptionVersion", "EncryptionKeyId"
                        FROM "SessionAttachments"
                        WHERE "SessionId" = @sessionId
                          AND "LogicalKey" = @logicalKey
                          AND "State" = @state
                        ORDER BY "Version" DESC
                        LIMIT 1
                        """;

                }
                else
                {

                    cmd.CommandText =
                        """
                        SELECT "Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                               "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                               "SourceKind", "SourceWorkspaceIdentity", "SourceRelativePath", "SourceCanonicalPath",
                               "SourceContentSha256", "SourceFileIdentity", "SourceLastWriteAt", "SourceByteLength",
                               "SourceStatus", "SourceDiagnosticReason", "EncryptionVersion", "EncryptionKeyId"
                        FROM "SessionAttachments"
                        WHERE "SessionId" = @sessionId
                          AND "LogicalKey" = @logicalKey
                          AND "Version" = @version
                          AND "State" = @state
                        LIMIT 1
                        """;

                    AddParameter(cmd, "@version", version.Value);

                }

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                AddParameter(cmd, "@logicalKey", sanitizedKey);

                AddParameter(cmd, "@state", nameof(SessionAttachmentState.Bound));

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    return null;

                }

                return ReadRecord(reader);

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {

        List<SessionAttachmentRecord> rows = [];

        await foreach (IReadOnlyList<SessionAttachmentRecord> page in ReadBoundPagesAsync(
            sessionId,
            DefaultBoundRecordPageSize,
            cancellationToken))
        {

            rows.AddRange(page);

        }

        return rows;

    }

    public async IAsyncEnumerable<IReadOnlyList<SessionAttachmentRecord>> ReadBoundPagesAsync(
        Guid sessionId,
        int pageSize = DefaultBoundRecordPageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        int effectivePageSize = Math.Clamp(
            pageSize,
            1,
            MaximumBoundRecordPageSize);

        string? afterLogicalKey = null;

        int afterVersion = 0;

        while (true)
        {

            IReadOnlyList<SessionAttachmentRecord> page = await ReadBoundPageAsync(
                    sessionId,
                    afterLogicalKey,
                    afterVersion,
                    effectivePageSize,
                    cancellationToken)
                .ConfigureAwait(false);

            if (page.Count == 0)
            {

                yield break;

            }

            yield return page;

            SessionAttachmentRecord last = page[^1];

            afterLogicalKey = last.LogicalKey;

            afterVersion = last.Version;

        }

    }

    private async Task<IReadOnlyList<SessionAttachmentRecord>> ReadBoundPageAsync(
        Guid sessionId,
        string? afterLogicalKey,
        int afterVersion,
        int pageSize,
        CancellationToken cancellationToken)
    {

        _ = Interlocked.Increment(ref _boundRecordPageReadCountForTesting);

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                           "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                           "SourceKind", "SourceWorkspaceIdentity", "SourceRelativePath", "SourceCanonicalPath",
                           "SourceContentSha256", "SourceFileIdentity", "SourceLastWriteAt", "SourceByteLength",
                           "SourceStatus", "SourceDiagnosticReason", "EncryptionVersion", "EncryptionKeyId"
                    FROM "SessionAttachments"
                    WHERE "SessionId" = @sessionId
                      AND "State" = @state
                      AND (
                          @afterLogicalKey IS NULL
                          OR "LogicalKey" > @afterLogicalKey
                          OR ("LogicalKey" = @afterLogicalKey AND "Version" > @afterVersion))
                    ORDER BY "LogicalKey" ASC, "Version" ASC
                    LIMIT @pageSize
                    """;

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                AddParameter(cmd, "@state", nameof(SessionAttachmentState.Bound));

                AddParameter(cmd, "@afterLogicalKey", (object?)afterLogicalKey ?? DBNull.Value);

                AddParameter(cmd, "@afterVersion", afterVersion);

                AddParameter(cmd, "@pageSize", pageSize);

                List<SessionAttachmentRecord> rows = new(pageSize);

                await using DbDataReader reader = await cmd
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    rows.Add(ReadRecord(reader));

                }

                return (IReadOnlyList<SessionAttachmentRecord>)rows;

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<SessionAttachmentRecord>> ListLatestBoundAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT current."Id", current."SessionId", current."EntryId", current."PendingTurnId",
                           current."State", current."LogicalKey", current."OriginalFileName", current."Version",
                           current."RelativePath", current."ContentSha256", current."MimeType", current."ByteLength",
                           current."Kind", current."CreatedAt", current."SourceKind",
                           current."SourceWorkspaceIdentity", current."SourceRelativePath", current."SourceCanonicalPath",
                           current."SourceContentSha256", current."SourceFileIdentity", current."SourceLastWriteAt",
                           current."SourceByteLength", current."SourceStatus", current."SourceDiagnosticReason",
                           current."EncryptionVersion", current."EncryptionKeyId"
                    FROM "SessionAttachments" AS current
                    WHERE current."SessionId" = @sessionId
                      AND current."State" = @state
                      AND NOT EXISTS (
                          SELECT 1
                          FROM "SessionAttachments" AS newer
                          WHERE newer."SessionId" = current."SessionId"
                            AND newer."State" = current."State"
                            AND newer."LogicalKey" = current."LogicalKey"
                            AND newer."Version" > current."Version")
                    ORDER BY current."LogicalKey" ASC
                    """;

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                AddParameter(cmd, "@state", nameof(SessionAttachmentState.Bound));

                List<SessionAttachmentRecord> rows = [];

                await using DbDataReader reader = await cmd
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    rows.Add(ReadRecord(reader));

                }

                return (IReadOnlyList<SessionAttachmentRecord>)rows;

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<SessionAttachmentRecord>>
        ListLatestBoundByLogicalKeysAsync(
            Guid sessionId,
            IReadOnlyList<string> logicalKeys,
            CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(logicalKeys);

        if (logicalKeys.Count == 0)
        {

            return [];
        }

        List<string> orderedKeys = new(logicalKeys.Count);
        HashSet<string> seenKeys = new(StringComparer.Ordinal);

        foreach (string logicalKey in logicalKeys)
        {

            if (!string.IsNullOrWhiteSpace(logicalKey)
                && seenKeys.Add(logicalKey))
            {

                orderedKeys.Add(logicalKey);
            }
        }

        Dictionary<string, SessionAttachmentRecord> rowsByLogicalKey =
            new(StringComparer.Ordinal);

        for (int offset = 0;
             offset < orderedKeys.Count;
             offset += LatestLogicalKeyQueryPageSize)
        {

            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<string> pageKeys = orderedKeys
                .Skip(offset)
                .Take(LatestLogicalKeyQueryPageSize)
                .ToArray();
            IReadOnlyList<SessionAttachmentRecord> page =
                await ReadLatestBoundByLogicalKeyPageAsync(
                        sessionId,
                        pageKeys,
                        cancellationToken)
                    .ConfigureAwait(false);

            foreach (SessionAttachmentRecord row in page)
            {

                rowsByLogicalKey[row.LogicalKey] = row;
            }
        }

        List<SessionAttachmentRecord> orderedRows =
            new(rowsByLogicalKey.Count);

        foreach (string logicalKey in orderedKeys)
        {

            if (rowsByLogicalKey.TryGetValue(
                    logicalKey,
                    out SessionAttachmentRecord? row))
            {

                orderedRows.Add(row);
            }
        }

        return orderedRows;
    }

    private async Task<IReadOnlyList<SessionAttachmentRecord>>
        ReadLatestBoundByLogicalKeyPageAsync(
            Guid sessionId,
        IReadOnlyList<string> logicalKeys,
        CancellationToken cancellationToken)
    {

        _ = Interlocked.Increment(
            ref _latestLogicalKeyQueryPageCountForTesting);

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection =
                    await OpenConnectionAsync(cancellationToken)
                        .ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();
                string[] keyParameters = new string[logicalKeys.Count];

                for (int index = 0; index < logicalKeys.Count; index++)
                {

                    string parameterName = $"@logicalKey{index}";
                    keyParameters[index] = parameterName;
                    AddParameter(
                        cmd,
                        parameterName,
                        logicalKeys[index]);
                }

                cmd.CommandText =
                    $"""
                     SELECT current."Id", current."SessionId", current."EntryId", current."PendingTurnId",
                            current."State", current."LogicalKey", current."OriginalFileName", current."Version",
                            current."RelativePath", current."ContentSha256", current."MimeType", current."ByteLength",
                            current."Kind", current."CreatedAt", current."SourceKind",
                            current."SourceWorkspaceIdentity", current."SourceRelativePath", current."SourceCanonicalPath",
                            current."SourceContentSha256", current."SourceFileIdentity", current."SourceLastWriteAt",
                            current."SourceByteLength", current."SourceStatus", current."SourceDiagnosticReason",
                            current."EncryptionVersion", current."EncryptionKeyId"
                     FROM "SessionAttachments" AS current
                     WHERE current."SessionId" = @sessionId
                       AND current."State" = @state
                       AND current."LogicalKey" IN ({string.Join(", ", keyParameters)})
                       AND NOT EXISTS (
                           SELECT 1
                           FROM "SessionAttachments" AS newer
                           WHERE newer."SessionId" = current."SessionId"
                             AND newer."State" = current."State"
                             AND newer."LogicalKey" = current."LogicalKey"
                             AND newer."Version" > current."Version")
                     """;

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                AddParameter(
                    cmd,
                    "@state",
                    nameof(SessionAttachmentState.Bound));

                List<SessionAttachmentRecord> rows =
                    new(logicalKeys.Count);

                await using DbDataReader reader = await cmd
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await reader
                           .ReadAsync(cancellationToken)
                           .ConfigureAwait(false))
                {

                    rows.Add(ReadRecord(reader));
                }

                return (IReadOnlyList<SessionAttachmentRecord>)rows;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(
        Guid sessionId,
        int maxItems,
        CancellationToken cancellationToken = default)
    {

        int cap = Math.Max(1, maxItems);

        List<SessionAttachmentIndexItem> items = [];

        IReadOnlyList<AttachmentIndexLatestProjection> latestItems = await ReadIndexLatestAsync(
            sessionId,
            cap,
            cancellationToken).ConfigureAwait(false);

        foreach (AttachmentIndexLatestProjection latest in latestItems)
        {

            IReadOnlyList<int> versionPage =
                await ReadIndexVersionPageAsync(
                    sessionId,
                    latest.LogicalKey,
                    latest.Version,
                    afterVersion: 0,
                    pageSize: IndexVersionPageSize + 1,
                    cancellationToken).ConfigureAwait(false);
            bool hasMoreVersions =
                versionPage.Count > IndexVersionPageSize;
            int? nextVersion = hasMoreVersions
                ? versionPage[IndexVersionPageSize]
                : null;
            IReadOnlyList<int> versions = hasMoreVersions
                ? versionPage.Take(IndexVersionPageSize).ToArray()
                : versionPage;

            items.Add(new SessionAttachmentIndexItem(
                latest.LogicalKey,
                latest.OriginalFileName,
                versions,
                latest.Kind,
                latest.ByteLength,
                hasMoreVersions,
                nextVersion));

        }

        return items;

    }

    private async Task<IReadOnlyList<AttachmentIndexLatestProjection>> ReadIndexLatestAsync(
        Guid sessionId,
        int maxItems,
        CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT current."LogicalKey", current."OriginalFileName", current."Version",
                           current."Kind", current."ByteLength"
                    FROM "SessionAttachments" AS current
                    WHERE current."SessionId" = @sessionId
                      AND current."State" = @state
                      AND NOT EXISTS (
                          SELECT 1
                          FROM "SessionAttachments" AS newer
                          WHERE newer."SessionId" = current."SessionId"
                            AND newer."State" = current."State"
                            AND newer."LogicalKey" = current."LogicalKey"
                            AND newer."Version" > current."Version")
                    ORDER BY current."LogicalKey" ASC
                    LIMIT @maxItems
                    """;

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                AddParameter(cmd, "@state", nameof(SessionAttachmentState.Bound));

                AddParameter(cmd, "@maxItems", maxItems);

                List<AttachmentIndexLatestProjection> rows = new(maxItems);

                await using DbDataReader reader = await cmd
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    rows.Add(new AttachmentIndexLatestProjection(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        Enum.Parse<SessionAttachmentKind>(reader.GetString(3), ignoreCase: false),
                        reader.GetInt64(4)));

                }

                return (IReadOnlyList<AttachmentIndexLatestProjection>)rows;

            },
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<IReadOnlyList<int>> ReadIndexVersionPageAsync(
        Guid sessionId,
        string logicalKey,
        int maximumVersion,
        int afterVersion,
        int pageSize,
        CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Version"
                    FROM "SessionAttachments"
                    WHERE "SessionId" = @sessionId
                      AND "State" = @state
                      AND "LogicalKey" = @logicalKey
                      AND "Version" > @afterVersion
                      AND "Version" <= @maximumVersion
                    ORDER BY "Version" ASC
                    LIMIT @pageSize
                    """;

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                AddParameter(cmd, "@state", nameof(SessionAttachmentState.Bound));

                AddParameter(cmd, "@logicalKey", logicalKey);

                AddParameter(cmd, "@afterVersion", afterVersion);

                AddParameter(cmd, "@maximumVersion", maximumVersion);

                AddParameter(cmd, "@pageSize", pageSize);

                List<int> rows = new(pageSize);

                await using DbDataReader reader = await cmd
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    rows.Add(reader.GetInt32(0));

                }

                return (IReadOnlyList<int>)rows;

            },
            cancellationToken).ConfigureAwait(false);

    }

    private sealed record AttachmentIndexLatestProjection(
        string LogicalKey,
        string OriginalFileName,
        int Version,
        SessionAttachmentKind Kind,
        long ByteLength);

    public async Task<Stream> OpenReadAsync(
        SessionAttachmentRecord record,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(record);

        string absolutePath = ResolveUnderRoot(record.RelativePath);

        if (!File.Exists(absolutePath))
        {

            throw new FileNotFoundException("Attachment bytes not found on disk.", absolutePath);

        }

        Stream decrypted = await _blobStore
            .OpenCompatibleReadAsync(
                absolutePath,
                EncryptedBlobPurpose.SessionAttachment,
                record.EncryptionVersion,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (decrypted.Length != record.ByteLength)
            {
                throw new InvalidDataException(
                    $"Attachment plaintext length mismatch for '{record.Id}'.");
            }

            return decrypted;
        }
        catch
        {
            await decrypted.DisposeAsync().ConfigureAwait(false);

            throw;
        }

    }

    public async Task<ReadOnlyMemory<byte>> ReadBytesAsync(
        SessionAttachmentRecord record,
        CancellationToken cancellationToken = default)
    {

        await using Stream decrypted = await OpenReadAsync(record, cancellationToken).ConfigureAwait(false);

        if (record.ByteLength < 0 || record.ByteLength > int.MaxValue)
        {

            throw new InvalidDataException(
                "Attachment exceeds the single-memory read protocol boundary; use OpenReadAsync.");

        }

        byte[] output = GC.AllocateUninitializedArray<byte>((int)record.ByteLength);

        try
        {

            int offset = 0;

            while (offset < output.Length)
            {

                int count = Math.Min(AttachmentIoPageSize, output.Length - offset);

                int read = await decrypted
                    .ReadAsync(output.AsMemory(offset, count), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {

                    throw new InvalidDataException(
                        $"Attachment plaintext ended before its declared length for '{record.Id}'.");

                }

                offset += read;

            }

            byte[] sentinel = new byte[1];

            if (await decrypted
                .ReadAsync(sentinel, cancellationToken)
                .ConfigureAwait(false) != 0)
            {

                throw new InvalidDataException(
                    $"Attachment plaintext exceeded its declared length for '{record.Id}'.");

            }

            return output;

        }
        catch
        {

            CryptographicOperations.ZeroMemory(output);

            throw;

        }

    }

    private static string ComputeContentSha256(ReadOnlyMemory<byte> bytes)
    {

        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (int offset = 0; offset < bytes.Length; offset += AttachmentIoPageSize)
        {

            int count = Math.Min(AttachmentIoPageSize, bytes.Length - offset);

            hasher.AppendData(bytes.Span.Slice(offset, count));

        }

        byte[] digest = hasher.GetHashAndReset();

        try
        {

            return Convert.ToHexString(digest);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(digest);

        }

    }

    public async Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {

        DateTimeOffset threshold = DateTimeOffset.UtcNow - olderThan;

        List<(Guid Id, string PendingTurnId, string RelativePath)> stale =
            await ListStalePendingAsync(threshold, cancellationToken).ConfigureAwait(false);

        foreach (IGrouping<string, (Guid Id, string PendingTurnId, string RelativePath)> group in stale
                     .GroupBy(s => s.PendingTurnId, StringComparer.Ordinal))
        {

            if (!SessionAttachmentPathSanitizer.TryValidatePendingTurnId(group.Key, out string safeTurnId, out _))
            {

                continue;

            }

            using IDisposable gate = await AttachmentGates
                .AcquireAsync(PendingTurnGateKey(safeTurnId), cancellationToken)
                .ConfigureAwait(false);

            List<(Guid Id, string PendingTurnId, string RelativePath)> stillStale =
                await ListStalePendingForTurnAsync(safeTurnId, threshold, cancellationToken).ConfigureAwait(false);

            if (stillStale.Count == 0)
            {

                continue;

            }

            await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {

                    DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                        .ConfigureAwait(false);

                    foreach ((Guid id, _, _) in stillStale)
                    {

                        await using DbCommand delete = connection.CreateCommand();

                        delete.Transaction = transaction;

                        delete.CommandText =
                            """
                            DELETE FROM "SessionAttachments"
                            WHERE "Id" = @id
                              AND "State" = @state
                            """;

                        AddParameter(delete, "@id", id.ToString());

                        AddParameter(delete, "@state", nameof(SessionAttachmentState.Pending));

                        _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                },
                cancellationToken).ConfigureAwait(false);

            foreach ((_, _, string relativePath) in stillStale)
            {

                TryDeleteFile(ResolveUnderRoot(relativePath));

            }

            TryDeletePendingTurnDirectory(safeTurnId);

        }

        await SweepOrphanPendingDirectoriesAsync(threshold, cancellationToken).ConfigureAwait(false);

        await SweepOrphanAttachmentFilesAsync(cancellationToken).ConfigureAwait(false);

    }

    public Task ReconcileAsync(TimeSpan pendingOlderThan, CancellationToken cancellationToken = default) =>
        ReconcileCoreAsync(pendingOlderThan, cancellationToken);

    public Task<IDisposable> AcquireSessionGateAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        AttachmentGates.AcquireAsync(SessionGateKey(sessionId), cancellationToken);

    public async Task ValidateReferencesAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(attachmentIds);

        foreach (Guid attachmentId in attachmentIds)
        {

            SessionAttachmentRecord? record = await GetByIdAsync(attachmentId, cancellationToken).ConfigureAwait(false);

            if (record is null)
            {

                throw new InvalidOperationException($"Attachment '{attachmentId}' was not found.");

            }

            if (record.State != SessionAttachmentState.Bound)
            {

                throw new InvalidOperationException($"Attachment '{attachmentId}' is not bound.");

            }

            if (record.SessionId != sessionId)
            {

                throw new InvalidOperationException(
                    $"Attachment '{attachmentId}' does not belong to session '{sessionId}'.");

            }

        }

    }

    internal static string SessionGateKey(Guid sessionId) =>
        "session|" + sessionId.ToString("N");

    internal static string BoundGateKey(Guid sessionId, string logicalKey) =>
        "bound|" + sessionId.ToString("N") + "|" + logicalKey;

    internal static string PendingTurnGateKey(string pendingTurnId) =>
        "pending|" + pendingTurnId;

    private void EnlistAmbientTransaction(DbCommand cmd)
    {

        IDbContextTransaction? ambient = _db.Database.CurrentTransaction;

        if (ambient is not null)
        {

            cmd.Transaction = ambient.GetDbTransaction();

        }

    }

    private async Task InsertRowAsync(
        SessionAttachmentRecord record,
        IdentityOwnedFileSystemArtifact expectedBlob,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IDbContextTransaction? ambient = _db.Database.CurrentTransaction;

        DbTransaction? ownedTransaction = null;

        DbTransaction transaction = ambient?.GetDbTransaction()
            ?? (ownedTransaction = await BeginImmediateAttachmentTransactionAsync(
                connection,
                cancellationToken).ConfigureAwait(false));

        try
        {

            if (ambient is not null && connection is SqliteConnection)
            {

                await AcquireAttachmentWriterLockAsync(
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false);

            }

            if (AfterWriterLockBeforeBlobValidationForTesting is not null)
            {

                await AfterWriterLockBeforeBlobValidationForTesting(
                    cancellationToken).ConfigureAwait(false);

            }

            ValidateOwnedBlob(record, expectedBlob);

            await using DbCommand cmd = connection.CreateCommand();

            cmd.Transaction = transaction;

            cmd.CommandText =
                """
                INSERT INTO "SessionAttachments"
                    ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                     "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                     "SourceKind", "SourceWorkspaceIdentity", "SourceRelativePath", "SourceCanonicalPath",
                     "SourceContentSha256", "SourceFileIdentity", "SourceLastWriteAt", "SourceByteLength",
                     "SourceStatus", "SourceDiagnosticReason", "EncryptionVersion", "EncryptionKeyId")
                VALUES
                    (@id, @sessionId, @entryId, @pendingTurnId, @state, @logicalKey, @originalFileName,
                     @version, @relativePath, @contentSha256, @mimeType, @byteLength, @kind, @createdAt,
                     @sourceKind, @sourceWorkspaceIdentity, @sourceRelativePath, @sourceCanonicalPath,
                     @sourceContentSha256, @sourceFileIdentity, @sourceLastWriteAt, @sourceByteLength,
                     @sourceStatus, @sourceDiagnosticReason, @encryptionVersion, @encryptionKeyId)
                """;

            AddParameter(cmd, "@id", record.Id.ToString());

            AddParameter(cmd, "@sessionId", record.SessionId is null ? DBNull.Value : record.SessionId.Value.ToString());

            AddParameter(cmd, "@entryId", record.EntryId is null ? DBNull.Value : record.EntryId.Value.ToString());

            AddParameter(cmd, "@pendingTurnId", (object?)record.PendingTurnId ?? DBNull.Value);

            AddParameter(cmd, "@state", record.State.ToString());

            AddParameter(cmd, "@logicalKey", record.LogicalKey);

            AddParameter(cmd, "@originalFileName", record.OriginalFileName);

            AddParameter(cmd, "@version", record.Version);

            AddParameter(cmd, "@relativePath", record.RelativePath);

            AddParameter(cmd, "@contentSha256", record.ContentSha256);

            AddParameter(cmd, "@mimeType", record.MimeType);

            AddParameter(cmd, "@byteLength", record.ByteLength);

            AddParameter(cmd, "@kind", record.Kind.ToString());

            AddParameter(cmd, "@createdAt", record.CreatedAt.ToString("o", CultureInfo.InvariantCulture));

            AddSourceParameters(cmd, record.Source ?? AttachmentSourceMetadata.SnapshotOnly);

            AddParameter(cmd, "@encryptionVersion", record.EncryptionVersion);

            AddParameter(
                cmd,
                "@encryptionKeyId",
                (object?)record.EncryptionKeyId ?? DBNull.Value);

            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (ownedTransaction is not null)
            {

                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            }

        }
        catch
        {

            if (ownedTransaction is not null)
            {

                await ownedTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            }

            throw;

        }
        finally
        {

            if (ownedTransaction is not null)
            {

                await ownedTransaction.DisposeAsync().ConfigureAwait(false);

            }

        }

    }

    private void ValidateOwnedBlob(
        SessionAttachmentRecord record,
        IdentityOwnedFileSystemArtifact expectedBlob)
    {

        string absolutePath = ResolveUnderRoot(record.RelativePath);

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(
                Path.GetFullPath(absolutePath),
                Path.GetFullPath(expectedBlob.Path),
                comparison)
            || !IdentityOwnedFileSystemCleanup.TryCapturePath(
                absolutePath,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact current)
            || current.Metadata != expectedBlob.Metadata)
        {

            throw new IOException(
                "Attachment persistence refused to publish metadata for missing or replaced bytes.");

        }

    }

    private static async Task<DbTransaction> BeginImmediateAttachmentTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (connection is SqliteConnection sqliteConnection)
        {

            return sqliteConnection.BeginTransaction(deferred: false);

        }

        return await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private static async Task AcquireAttachmentWriterLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            UPDATE "SessionAttachments"
            SET "Id" = "Id"
            WHERE 0
            """;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private async Task UpdateSourceAsync(
        Guid id,
        AttachmentSourceMetadata source,
        CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE "SessionAttachments"
            SET "SourceKind" = @sourceKind,
                "SourceWorkspaceIdentity" = @sourceWorkspaceIdentity,
                "SourceRelativePath" = @sourceRelativePath,
                "SourceCanonicalPath" = @sourceCanonicalPath,
                "SourceContentSha256" = @sourceContentSha256,
                "SourceFileIdentity" = @sourceFileIdentity,
                "SourceLastWriteAt" = @sourceLastWriteAt,
                "SourceByteLength" = @sourceByteLength,
                "SourceStatus" = @sourceStatus,
                "SourceDiagnosticReason" = @sourceDiagnosticReason
            WHERE "Id" = @id
            """;
        AddParameter(cmd, "@id", id.ToString());
        AddSourceParameters(cmd, source);
        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddSourceParameters(DbCommand cmd, AttachmentSourceMetadata source)
    {
        AddParameter(cmd, "@sourceKind", source.Kind.ToString());
        AddParameter(cmd, "@sourceWorkspaceIdentity", (object?)source.WorkspaceIdentity ?? DBNull.Value);
        AddParameter(cmd, "@sourceRelativePath", (object?)source.WorkspaceRelativePath ?? DBNull.Value);
        AddParameter(cmd, "@sourceCanonicalPath", (object?)source.LastKnownCanonicalPath ?? DBNull.Value);
        AddParameter(cmd, "@sourceContentSha256", (object?)source.LastObservedContentSha256 ?? DBNull.Value);
        AddParameter(cmd, "@sourceFileIdentity", (object?)source.LastObservedFileIdentity ?? DBNull.Value);
        AddParameter(cmd, "@sourceLastWriteAt", source.LastObservedWriteTime is null
            ? DBNull.Value
            : source.LastObservedWriteTime.Value.ToString("o", CultureInfo.InvariantCulture));
        AddParameter(cmd, "@sourceByteLength", source.LastObservedByteLength is null
            ? DBNull.Value
            : source.LastObservedByteLength.Value);
        AddParameter(cmd, "@sourceStatus", source.Status.ToString());
        AddParameter(cmd, "@sourceDiagnosticReason", (object?)source.DiagnosticReason ?? DBNull.Value);
    }

    private async Task<SessionAttachmentRecord?> FindLatestAsync(
        Guid? sessionId,
        string? pendingTurnId,
        string logicalKey,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        if (sessionId is not null)
        {

            cmd.CommandText =
                """
                SELECT "Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                       "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                       "SourceKind", "SourceWorkspaceIdentity", "SourceRelativePath", "SourceCanonicalPath",
                       "SourceContentSha256", "SourceFileIdentity", "SourceLastWriteAt", "SourceByteLength",
                       "SourceStatus", "SourceDiagnosticReason", "EncryptionVersion", "EncryptionKeyId"
                FROM "SessionAttachments"
                WHERE "SessionId" = @sessionId
                  AND "LogicalKey" = @logicalKey
                ORDER BY "Version" DESC
                LIMIT 1
                """;

            AddParameter(cmd, "@sessionId", sessionId.Value.ToString());

        }
        else
        {

            cmd.CommandText =
                """
                SELECT "Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                       "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                       "SourceKind", "SourceWorkspaceIdentity", "SourceRelativePath", "SourceCanonicalPath",
                       "SourceContentSha256", "SourceFileIdentity", "SourceLastWriteAt", "SourceByteLength",
                       "SourceStatus", "SourceDiagnosticReason", "EncryptionVersion", "EncryptionKeyId"
                FROM "SessionAttachments"
                WHERE "PendingTurnId" = @pendingTurnId
                  AND "LogicalKey" = @logicalKey
                ORDER BY "Version" DESC
                LIMIT 1
                """;

            AddParameter(cmd, "@pendingTurnId", pendingTurnId!);

        }

        AddParameter(cmd, "@logicalKey", logicalKey);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return null;

        }

        return ReadRecord(reader);

    }

    private async Task<long> SumByteLengthAsync(
        Guid? sessionId,
        string? pendingTurnId,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        if (sessionId is not null)
        {

            cmd.CommandText =
                """
                SELECT COALESCE(SUM("ByteLength"), 0)
                FROM "SessionAttachments"
                WHERE "SessionId" = @sessionId
                """;

            AddParameter(cmd, "@sessionId", sessionId.Value.ToString());

        }
        else
        {

            cmd.CommandText =
                """
                SELECT COALESCE(SUM("ByteLength"), 0)
                FROM "SessionAttachments"
                WHERE "PendingTurnId" = @pendingTurnId
                """;

            AddParameter(cmd, "@pendingTurnId", pendingTurnId!);

        }

        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull ? 0L : Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

    private async Task<List<SessionAttachmentRecord>> ListPendingByTurnAsync(
        string pendingTurnId,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            SELECT "Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                   "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                   "SourceKind", "SourceWorkspaceIdentity", "SourceRelativePath", "SourceCanonicalPath",
                   "SourceContentSha256", "SourceFileIdentity", "SourceLastWriteAt", "SourceByteLength",
                   "SourceStatus", "SourceDiagnosticReason", "EncryptionVersion", "EncryptionKeyId"
            FROM "SessionAttachments"
            WHERE "PendingTurnId" = @pendingTurnId
              AND "State" = @state
            """;

        AddParameter(cmd, "@pendingTurnId", pendingTurnId);

        AddParameter(cmd, "@state", nameof(SessionAttachmentState.Pending));

        List<SessionAttachmentRecord> rows = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            rows.Add(ReadRecord(reader));

        }

        return rows;

    }

    private async Task<List<(Guid Id, string PendingTurnId, string RelativePath)>> ListStalePendingAsync(
        DateTimeOffset threshold,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand select = connection.CreateCommand();

        select.CommandText =
            """
            SELECT "Id", "PendingTurnId", "RelativePath"
            FROM "SessionAttachments"
            WHERE "State" = @state
              AND "CreatedAt" < @threshold
              AND "PendingTurnId" IS NOT NULL
            """;

        AddParameter(select, "@state", nameof(SessionAttachmentState.Pending));

        AddParameter(select, "@threshold", threshold.ToString("o", CultureInfo.InvariantCulture));

        List<(Guid Id, string PendingTurnId, string RelativePath)> stale = [];

        await using DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            Guid id = Guid.Parse(reader.GetString(0));

            string turnId = reader.GetString(1);

            string relativePath = reader.GetString(2);

            stale.Add((id, turnId, relativePath));

        }

        return stale;

    }

    private async Task<List<(Guid Id, string PendingTurnId, string RelativePath)>> ListStalePendingForTurnAsync(
        string pendingTurnId,
        DateTimeOffset threshold,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand select = connection.CreateCommand();

        select.CommandText =
            """
            SELECT "Id", "PendingTurnId", "RelativePath"
            FROM "SessionAttachments"
            WHERE "State" = @state
              AND "PendingTurnId" = @pendingTurnId
              AND "CreatedAt" < @threshold
            """;

        AddParameter(select, "@state", nameof(SessionAttachmentState.Pending));

        AddParameter(select, "@pendingTurnId", pendingTurnId);

        AddParameter(select, "@threshold", threshold.ToString("o", CultureInfo.InvariantCulture));

        List<(Guid Id, string PendingTurnId, string RelativePath)> stale = [];

        await using DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            Guid id = Guid.Parse(reader.GetString(0));

            string turnId = reader.GetString(1);

            string relativePath = reader.GetString(2);

            stale.Add((id, turnId, relativePath));

        }

        return stale;

    }

    private async Task SweepOrphanPendingDirectoriesAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {

        string pendingRoot = Path.Combine(_attachmentsRoot, "_pending");

        if (!Directory.Exists(pendingRoot))
        {

            return;

        }

        foreach (string dir in Directory.EnumerateDirectories(pendingRoot))
        {

            cancellationToken.ThrowIfCancellationRequested();

            string turnId = Path.GetFileName(dir);

            if (!SessionAttachmentPathSanitizer.TryValidatePendingTurnId(turnId, out string safeTurnId, out _))
            {

                _logger.LogWarning(
                    "Leaving invalid _pending child name alone (no identity-checked delete): {TurnDir}",
                    turnId);

                continue;

            }

            string absoluteDir = Path.GetFullPath(dir);

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_attachmentsRoot, absoluteDir, out _))
            {

                continue;

            }

            DateTime lastWriteUtc = Directory.GetLastWriteTimeUtc(absoluteDir);

            if (lastWriteUtc > threshold.UtcDateTime)
            {

                continue;

            }

            using IDisposable gate = await AttachmentGates
                .AcquireAsync(PendingTurnGateKey(safeTurnId), cancellationToken)
                .ConfigureAwait(false);

            List<SessionAttachmentRecord> remaining = await ListPendingByTurnAsync(safeTurnId, cancellationToken)
                .ConfigureAwait(false);

            if (remaining.Count > 0)
            {

                continue;

            }

            lastWriteUtc = Directory.GetLastWriteTimeUtc(absoluteDir);

            if (lastWriteUtc > threshold.UtcDateTime)
            {

                continue;

            }

            TryDeletePendingTurnDirectory(safeTurnId);

        }

    }

    /// <summary>
    /// Unlinks files under the attachment tree that no row claims.
    /// </summary>
    /// <remarks>
    /// This runs while the host is serving — at startup Kestrel is already accepting requests, and
    /// <c>POST /api/operations/reconcile</c> can drive it at any time — so "unreferenced" has to be
    /// evaluated against a moving target. Two guards make that safe without serialising every attachment
    /// write behind the sweep. First, nothing modified at or after the moment the row snapshot was taken
    /// is touched: an attachment write lands its ciphertext before it inserts its row, so a file missing
    /// from the snapshot but newer than it belongs to a write still in flight, and that also spares
    /// <c>EncryptedBlobStore</c>'s staging temporaries. Second, a file that is about to be unlinked is
    /// re-checked against the live table, so a row committed since the snapshot keeps its bytes.
    /// </remarks>
    private async Task SweepOrphanAttachmentFilesAsync(CancellationToken cancellationToken)
    {

        DateTime snapshotTakenUtc = DateTime.UtcNow;

        HashSet<string> knownPaths = await ListAllRelativePathsAsync(cancellationToken).ConfigureAwait(false);

        if (AfterOrphanPathSnapshotForTesting is not null)
        {

            await AfterOrphanPathSnapshotForTesting(cancellationToken).ConfigureAwait(false);

        }

        if (!Directory.Exists(_attachmentsRoot))
        {

            return;

        }

        foreach (string filePath in Directory.EnumerateFiles(_attachmentsRoot, "*", SearchOption.AllDirectories))
        {

            cancellationToken.ThrowIfCancellationRequested();

            string absolute = Path.GetFullPath(filePath);

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_attachmentsRoot, absolute, out _))
            {

                continue;

            }

            if (IsModifiedAtOrAfter(absolute, snapshotTakenUtc))
            {

                continue;

            }

            // Promotion stages as `destination + ".tmp"`, but EncryptedBlobStore stages as
            // `"." + fileName + ".tmp." + guid`, which no suffix test can match — hence the infix check.
            string fileName = Path.GetFileName(absolute);

            if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
            {

                TryDeleteFile(absolute);

                continue;

            }

            string relative = NormalizeRelativePath(Path.GetRelativePath(_attachmentsRoot, absolute));

            if (knownPaths.Contains(relative))
            {

                continue;

            }

            if (await RelativePathIsClaimedAsync(relative, cancellationToken).ConfigureAwait(false))
            {

                continue;

            }

            TryDeleteFile(absolute);

        }

    }

    /// <summary>
    /// Whether <paramref name="absolutePath"/> was last written at or after <paramref name="threshold"/>.
    /// An unreadable timestamp is reported as "yes", so an unexpected filesystem error can never be the
    /// reason a live attachment's ciphertext is unlinked.
    /// </summary>
    private static bool IsModifiedAtOrAfter(string absolutePath, DateTime threshold)
    {

        try
        {

            return File.GetLastWriteTimeUtc(absolutePath) >= threshold;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            return true;

        }

    }

    /// <summary>
    /// Re-reads the live table for one relative path, so a row committed since the sweep's snapshot
    /// still protects its bytes.
    /// </summary>
    private async Task<bool> RelativePathIsClaimedAsync(string relativePath, CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            SELECT 1 FROM "SessionAttachments" WHERE "RelativePath" = @relativePath LIMIT 1
            """;

        AddParameter(cmd, "@relativePath", relativePath);

        object? found = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return found is not null and not DBNull;

    }

    private async Task<HashSet<string>> ListAllRelativePathsAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = """"
            SELECT "RelativePath" FROM "SessionAttachments"
            """";

        HashSet<string> paths = new(StringComparer.Ordinal);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            paths.Add(NormalizeRelativePath(reader.GetString(0)));

        }

        return paths;

    }

    private void TryDeletePendingTurnDirectory(string pendingTurnId)
    {

        if (!SessionAttachmentPathSanitizer.TryValidatePendingTurnId(pendingTurnId, out string safeTurnId, out _))
        {

            return;

        }

        string dir = Path.GetFullPath(Path.Combine(_attachmentsRoot, "_pending", safeTurnId));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_attachmentsRoot, dir, out _))
        {

            return;

        }

        if (!Directory.Exists(dir))
        {

            return;

        }

        try
        {

            Directory.Delete(dir, recursive: true);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            // Best effort — leftover empty trees are cleaned on a later GC pass.

        }

    }

    private static async Task AtomicCopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {

        string? parentDir = Path.GetDirectoryName(destination);

        if (string.IsNullOrEmpty(parentDir))
        {

            throw new InvalidOperationException("Could not resolve promoted attachment directory.");

        }

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(parentDir);

        string tempPath = destination + ".tmp";

        if (File.Exists(tempPath))
        {

            File.Delete(tempPath);

        }

        await using (FileStream destStream = SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath))
        await using (FileStream srcStream = new(
                         source,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 4096,
                         useAsync: true))
        {

            await srcStream.CopyToAsync(destStream, cancellationToken).ConfigureAwait(false);

            await destStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        }

        SecureFilePermissions.ApplyOwnerOnlyFile(tempPath);

        if (File.Exists(destination))
        {

            File.Delete(destination);

        }

        File.Move(tempPath, destination);

        SecureFilePermissions.ApplyOwnerOnlyFile(destination);

    }

    private static void TryDeleteFile(string absolutePath)
    {

        try
        {

            if (File.Exists(absolutePath))
            {

                File.Delete(absolutePath);

            }

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            // Best effort compensating delete.

        }

    }

    private string ResolveUnderRoot(string relativePath)
    {

        string combined = Path.GetFullPath(Path.Combine(_attachmentsRoot, relativePath));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_attachmentsRoot, combined, out _))
        {

            throw new InvalidOperationException("Attachment path escapes the attachments root.");

        }

        return combined;

    }

    private static string BuildRelativePath(
        Guid? sessionId,
        string? pendingTurnId,
        string logicalKey,
        int version,
        string originalFileName)
    {

        string ownerSegment = sessionId is not null
            ? sessionId.Value.ToString("N")
            : Path.Combine("_pending", pendingTurnId!);

        return Path.Combine(ownerSegment, logicalKey, "v" + version.ToString(CultureInfo.InvariantCulture), originalFileName);

    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static bool HasSameSourceIdentity(

        AttachmentSourceMetadata? existing,

        AttachmentSourceMetadata? requested)

    {

        AttachmentSourceMetadata existingSource = existing ?? AttachmentSourceMetadata.SnapshotOnly;

        AttachmentSourceMetadata requestedSource = requested ?? AttachmentSourceMetadata.SnapshotOnly;

        if (existingSource.Kind != requestedSource.Kind)

        {

            return false;

        }

        if (existingSource.Kind == AttachmentSourceKind.SnapshotOnly)

        {

            return true;

        }

        StringComparison pathComparison = OperatingSystem.IsWindows()

            ? StringComparison.OrdinalIgnoreCase

            : StringComparison.Ordinal;

        return string.Equals(

                existingSource.WorkspaceIdentity,

                requestedSource.WorkspaceIdentity,

                StringComparison.Ordinal)

            && string.Equals(

                NormalizeSourceRelativePath(existingSource.WorkspaceRelativePath),

                NormalizeSourceRelativePath(requestedSource.WorkspaceRelativePath),

                pathComparison)

            && SourcePathEquals(

                existingSource.LastKnownCanonicalPath,

                requestedSource.LastKnownCanonicalPath,

                pathComparison);

    }

    private static string? NormalizeSourceRelativePath(string? relativePath) =>

        relativePath?.Replace('\\', '/');

    private static bool SourcePathEquals(

        string? left,

        string? right,

        StringComparison comparison)

    {

        if (left is null || right is null)

        {

            return left is null && right is null;

        }

        try

        {

            return string.Equals(

                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),

                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),

                comparison);

        }
        catch (Exception exception) when (

            exception is ArgumentException

                or NotSupportedException

                or PathTooLongException)

        {

            return false;

        }

    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

    private static SessionAttachmentRecord ReadRecord(DbDataReader reader)
    {

        Guid id = Guid.Parse(reader.GetString(0));

        Guid? sessionId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));

        Guid? entryId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));

        string? pendingTurnId = reader.IsDBNull(3) ? null : reader.GetString(3);

        SessionAttachmentState state = Enum.Parse<SessionAttachmentState>(reader.GetString(4));

        string logicalKey = reader.GetString(5);

        string originalFileName = reader.GetString(6);

        int version = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture);

        string relativePath = reader.GetString(8);

        string contentSha256 = reader.GetString(9);

        string mimeType = reader.GetString(10);

        long byteLength = reader.GetInt64(11);

        SessionAttachmentKind kind = Enum.Parse<SessionAttachmentKind>(reader.GetString(12));

        DateTimeOffset createdAt = DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture);

        AttachmentSourceMetadata source = AttachmentSourceMetadata.SnapshotOnly;
        if (reader.FieldCount >= 24)
        {
            AttachmentSourceKind sourceKind = Enum.TryParse(reader.GetString(14), out AttachmentSourceKind parsedKind)
                ? parsedKind
                : AttachmentSourceKind.SnapshotOnly;
            AttachmentSourceStatus sourceStatus = Enum.TryParse(reader.GetString(22), out AttachmentSourceStatus parsedStatus)
                ? parsedStatus
                : AttachmentSourceStatus.CorruptMetadata;
            source = new AttachmentSourceMetadata(
                sourceKind,
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.IsDBNull(20)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(20), CultureInfo.InvariantCulture),
                reader.IsDBNull(21) ? null : reader.GetInt64(21),
                sourceStatus,
                reader.IsDBNull(23) ? null : reader.GetString(23));
        }

        int encryptionVersion = reader.FieldCount >= 26
            ? Convert.ToInt32(reader.GetValue(24), CultureInfo.InvariantCulture)
            : 0;
        string? encryptionKeyId = reader.FieldCount >= 26 && !reader.IsDBNull(25)
            ? reader.GetString(25)
            : null;

        return new SessionAttachmentRecord(
            id,
            sessionId,
            entryId,
            pendingTurnId,
            state,
            logicalKey,
            originalFileName,
            version,
            relativePath,
            contentSha256,
            mimeType,
            byteLength,
            kind,
            createdAt,
            source,
            encryptionVersion,
            encryptionKeyId);

    }

    private readonly record struct PromotionPlan(
        SessionAttachmentRecord Row,
        string OldAbsolutePath,
        string NewAbsolutePath,
        string NewRelativePath);

}
