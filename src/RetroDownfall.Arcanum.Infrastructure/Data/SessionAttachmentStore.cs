using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
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

    /// <summary>
    /// Test seam: runs after bytes are on disk at the destination, before the DB write that
    /// records them. Used to simulate exhausted DB failure without holding FS work inside
    /// <see cref="SqliteBusyRetry"/>.
    /// </summary>
    internal Func<CancellationToken, Task>? AfterBytesCommittedBeforeDbForTesting { get; set; }

    public SessionAttachmentStore(
        ArcanumDbContext db,
        IOptions<ArcanumSettings> options,
        string? attachmentsRoot = null,
        ILogger<SessionAttachmentStore>? logger = null)
    {

        _db = db;

        _options = options;

        _logger = logger ?? NullLogger<SessionAttachmentStore>.Instance;

        _attachmentsRoot = Path.GetFullPath(attachmentsRoot ?? ArcanumPaths.AttachmentsDirectory);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(_attachmentsRoot);

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

        string contentSha256 = Convert.ToHexString(SHA256.HashData(bytes.Span));

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

        AttachmentsSettings attachments = _options.Value.Attachments ?? new AttachmentsSettings();

        int maxVersions = ArcanumSettingClamps.AttachmentsMaxVersionsPerLogicalKey(attachments.MaxVersionsPerLogicalKey);

        long maxBytes = ArcanumSettingClamps.AttachmentsMaxBytesPerSession(attachments.MaxBytesPerSession);

        SessionAttachmentRecord? latest = await FindLatestAsync(sessionId, validatedPendingTurnId, logicalKey, cancellationToken)
            .ConfigureAwait(false);

        if (latest is not null && string.Equals(latest.ContentSha256, contentSha256, StringComparison.OrdinalIgnoreCase))
        {

            return latest;

        }

        int nextVersion = latest is null ? 1 : latest.Version + 1;

        if (nextVersion > maxVersions)
        {

            throw new InvalidOperationException(
                $"Attachment version cap exceeded for logical key '{logicalKey}' (max {maxVersions}).");

        }

        long existingBytes = await SumByteLengthAsync(sessionId, validatedPendingTurnId, cancellationToken).ConfigureAwait(false);

        if (existingBytes + bytes.Length > maxBytes)
        {

            throw new InvalidOperationException(
                $"Attachment byte budget exceeded (max {maxBytes} bytes for this session/pending turn).");

        }

        string relativePath = BuildRelativePath(sessionId, validatedPendingTurnId, logicalKey, nextVersion, safeFileName);

        string absolutePath = ResolveUnderRoot(relativePath);

        await AtomicWriteBytesAsync(absolutePath, bytes, cancellationToken).ConfigureAwait(false);

        Guid id = Guid.NewGuid();

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

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
            createdAt);

        try
        {

            if (AfterBytesCommittedBeforeDbForTesting is not null)
            {

                await AfterBytesCommittedBeforeDbForTesting(cancellationToken).ConfigureAwait(false);

            }

            await SqliteBusyRetry.ExecuteAsync(
                () => InsertRowAsync(record, cancellationToken),
                cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            TryDeleteFile(absolutePath);

            throw;

        }

        return record;

    }

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
                           "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt"
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
                               "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt"
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
                               "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt"
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

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                           "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt"
                    FROM "SessionAttachments"
                    WHERE "SessionId" = @sessionId
                      AND "State" = @state
                    ORDER BY "LogicalKey" ASC, "Version" ASC
                    """;

                AddParameter(cmd, "@sessionId", sessionId.ToString());

                AddParameter(cmd, "@state", nameof(SessionAttachmentState.Bound));

                List<SessionAttachmentRecord> rows = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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

        IReadOnlyList<SessionAttachmentRecord> bound = await ListBoundAsync(sessionId, cancellationToken).ConfigureAwait(false);

        List<SessionAttachmentIndexItem> items = [];

        foreach (IGrouping<string, SessionAttachmentRecord> group in bound.GroupBy(r => r.LogicalKey, StringComparer.Ordinal))
        {

            if (items.Count >= cap)
            {

                break;

            }

            List<SessionAttachmentRecord> versions = group.OrderBy(r => r.Version).ToList();

            SessionAttachmentRecord latest = versions[^1];

            items.Add(new SessionAttachmentIndexItem(
                latest.LogicalKey,
                latest.OriginalFileName,
                versions.Select(v => v.Version).ToList(),
                latest.Kind,
                latest.ByteLength));

        }

        return items;

    }

    public async Task<ReadOnlyMemory<byte>> ReadBytesAsync(
        SessionAttachmentRecord record,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(record);

        string absolutePath = ResolveUnderRoot(record.RelativePath);

        if (!File.Exists(absolutePath))
        {

            throw new FileNotFoundException("Attachment bytes not found on disk.", absolutePath);

        }

        byte[] bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken).ConfigureAwait(false);

        return bytes;

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
        int maxReferences,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(attachmentIds);

        int clampedMax = ArcanumSettingClamps.AttachmentsMaxReferencesPerTurn(maxReferences);

        if (attachmentIds.Count > clampedMax)
        {

            throw new InvalidOperationException(
                $"Too many attachment references ({attachmentIds.Count}); max is {clampedMax}.");

        }

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

    private async Task InsertRowAsync(SessionAttachmentRecord record, CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        EnlistAmbientTransaction(cmd);

        cmd.CommandText =
            """
            INSERT INTO "SessionAttachments"
                ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                 "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt")
            VALUES
                (@id, @sessionId, @entryId, @pendingTurnId, @state, @logicalKey, @originalFileName,
                 @version, @relativePath, @contentSha256, @mimeType, @byteLength, @kind, @createdAt)
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

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

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
                       "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt"
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
                       "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt"
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
                   "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt"
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

    private async Task SweepOrphanAttachmentFilesAsync(CancellationToken cancellationToken)
    {

        HashSet<string> knownPaths = await ListAllRelativePathsAsync(cancellationToken).ConfigureAwait(false);

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

            if (absolute.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {

                TryDeleteFile(absolute);

                continue;

            }

            string relative = NormalizeRelativePath(Path.GetRelativePath(_attachmentsRoot, absolute));

            if (knownPaths.Contains(relative))
            {

                continue;

            }

            TryDeleteFile(absolute);

        }

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

    private static async Task AtomicWriteBytesAsync(
        string destination,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {

        string? parentDir = Path.GetDirectoryName(destination);

        if (string.IsNullOrEmpty(parentDir))
        {

            throw new InvalidOperationException("Could not resolve attachment parent directory.");

        }

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(parentDir);

        string tempPath = destination + ".tmp";

        if (File.Exists(tempPath))
        {

            File.Delete(tempPath);

        }

        await using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath))
        {

            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        }

        SecureFilePermissions.ApplyOwnerOnlyFile(tempPath);

        if (File.Exists(destination))
        {

            File.Delete(destination);

        }

        File.Move(tempPath, destination);

        SecureFilePermissions.ApplyOwnerOnlyFile(destination);

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
            createdAt);

    }

    private readonly record struct PromotionPlan(
        SessionAttachmentRecord Row,
        string OldAbsolutePath,
        string NewAbsolutePath,
        string NewRelativePath);

}
