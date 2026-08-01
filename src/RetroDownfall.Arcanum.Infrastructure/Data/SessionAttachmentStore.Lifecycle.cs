using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class SessionAttachmentStore
{

    public async Task DeleteRowsForSessionInAmbientTransactionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {

        if (_db.Database.CurrentTransaction is null)
        {

            throw new InvalidOperationException(
                "DeleteRowsForSessionInAmbientTransactionAsync requires an ambient EF transaction.");

        }

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        EnlistAmbientTransaction(cmd);

        cmd.CommandText =
            """
            DELETE FROM "SessionAttachments"
            WHERE "SessionId" = @sessionId
            """;

        AddParameter(cmd, "@sessionId", sessionId.ToString());

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    public bool TryDeleteSessionDirectory(Guid sessionId)
    {

        string sessionSegment = sessionId.ToString("N");

        string dir = Path.GetFullPath(Path.Combine(_attachmentsRoot, sessionSegment));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_attachmentsRoot, dir, out _))
        {

            _logger.LogWarning(
                "Refusing to delete session attachment directory that escapes root: {SessionId}",
                sessionId);

            return false;

        }

        if (!Directory.Exists(dir))
        {

            return true;

        }

        try
        {

            Directory.Delete(dir, recursive: true);

            return true;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            _logger.LogWarning(
                ex,
                "Failed to delete session attachment directory for {SessionId}; reconcile will retry.",
                sessionId);

            return false;

        }

    }

    public async Task ClearEntryIdsInAmbientTransactionAsync(
        Guid sessionId,
        IReadOnlyList<Guid> entryIds,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(entryIds);

        if (entryIds.Count == 0)
        {

            return;

        }

        if (_db.Database.CurrentTransaction is null)
        {

            throw new InvalidOperationException(
                "ClearEntryIdsInAmbientTransactionAsync requires an ambient EF transaction.");

        }

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        foreach (Guid entryId in entryIds)
        {

            await using DbCommand cmd = connection.CreateCommand();

            EnlistAmbientTransaction(cmd);

            cmd.CommandText =
                """
                UPDATE "SessionAttachments"
                SET "EntryId" = NULL
                WHERE "SessionId" = @sessionId
                  AND "EntryId" = @entryId
                """;

            AddParameter(cmd, "@sessionId", sessionId.ToString());

            AddParameter(cmd, "@entryId", entryId.ToString());

            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

    }

    public async Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundForForkAsync(
        Guid sourceSessionId,
        IReadOnlySet<Guid>? copiedSourceEntryIds,
        CancellationToken cancellationToken = default)
    {

        IReadOnlyList<SessionAttachmentRecord> bound = await ListBoundAsync(sourceSessionId, cancellationToken)
            .ConfigureAwait(false);

        if (copiedSourceEntryIds is null)
        {

            return bound;

        }

        List<SessionAttachmentRecord> selected = [];

        foreach (SessionAttachmentRecord row in bound)
        {

            if (row.EntryId is Guid entryId && copiedSourceEntryIds.Contains(entryId))
            {

                selected.Add(row);

            }

        }

        return selected;

    }

    public async Task CopyBytesForForkAsync(
        Guid forkSessionId,
        IReadOnlyList<SessionAttachmentForkCopyPlan> plans,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(plans);

        if (plans.Count == 0)
        {

            return;

        }

        try
        {

            foreach (SessionAttachmentForkCopyPlan plan in plans)
            {

                cancellationToken.ThrowIfCancellationRequested();

                string sourceAbsolute = ResolveUnderRoot(plan.Source.RelativePath);

                if (!File.Exists(sourceAbsolute))
                {

                    throw new FileNotFoundException(
                        "Source attachment bytes missing for fork copy.",
                        sourceAbsolute);

                }

                string newRelative = BuildRelativePath(
                    forkSessionId,
                    pendingTurnId: null,
                    plan.Source.LogicalKey,
                    plan.Source.Version,
                    plan.Source.OriginalFileName);

                string destAbsolute = ResolveUnderRoot(newRelative);

                await AtomicCopyFileAsync(sourceAbsolute, destAbsolute, cancellationToken).ConfigureAwait(false);

                await VerifyCopiedFileAsync(destAbsolute, plan.Source, cancellationToken).ConfigureAwait(false);

            }

        }
        catch
        {

            _ = TryDeleteSessionDirectory(forkSessionId);

            throw;

        }

    }

    public async Task InsertForkRowsInAmbientTransactionAsync(
        Guid forkSessionId,
        IReadOnlyList<SessionAttachmentForkCopyPlan> plans,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(plans);

        if (_db.Database.CurrentTransaction is null)
        {

            throw new InvalidOperationException(
                "InsertForkRowsInAmbientTransactionAsync requires an ambient EF transaction.");

        }

        foreach (SessionAttachmentForkCopyPlan plan in plans)
        {

            string relativePath = NormalizeRelativePath(BuildRelativePath(
                forkSessionId,
                pendingTurnId: null,
                plan.Source.LogicalKey,
                plan.Source.Version,
                plan.Source.OriginalFileName));

            SessionAttachmentRecord row = new(
                plan.NewAttachmentId,
                forkSessionId,
                plan.NewEntryId,
                PendingTurnId: null,
                SessionAttachmentState.Bound,
                plan.Source.LogicalKey,
                plan.Source.OriginalFileName,
                plan.Source.Version,
                relativePath,
                plan.Source.ContentSha256,
                plan.Source.MimeType,
                plan.Source.ByteLength,
                plan.Source.Kind,
                plan.Source.CreatedAt,
                plan.Source.Source,
                plan.Source.EncryptionVersion,
                plan.Source.EncryptionKeyId);

            await InsertRowAsync(row, cancellationToken).ConfigureAwait(false);

        }

    }

    private async Task ReconcileCoreAsync(TimeSpan pendingOlderThan, CancellationToken cancellationToken)
    {

        await DeleteStalePendingAsync(pendingOlderThan, cancellationToken).ConfigureAwait(false);

        await SweepMissingSessionRowsAndDirectoriesAsync(cancellationToken).ConfigureAwait(false);

        await SweepMissingFileRowsAsync(cancellationToken).ConfigureAwait(false);

        await ValidateEncryptedFilesAsync(cancellationToken).ConfigureAwait(false);

        await RevalidateAttachmentSourcesAsync(cancellationToken).ConfigureAwait(false);

        await SweepOrphanAttachmentFilesAsync(cancellationToken).ConfigureAwait(false);

    }

    private async Task ValidateEncryptedFilesAsync(CancellationToken cancellationToken)
    {
        List<SessionAttachmentRecord> rows = await ListAllRowsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (SessionAttachmentRecord row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string absolute;
            try
            {
                absolute = ResolveUnderRoot(row.RelativePath);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (!File.Exists(absolute))
            {
                continue;
            }

            if (!_blobStore.HasEnvelope(absolute))
            {
                _logger.LogError(
                    "Legacy plaintext attachment requires migration: {AttachmentId} ({RelativePath}).",
                    row.Id,
                    row.RelativePath);
                continue;
            }

            try
            {
                _ = await _blobStore.InspectAsync(
                        absolute,
                        EncryptedBlobPurpose.SessionAttachment,
                        verifyAllChunks: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
            {
                _logger.LogError(
                    ex,
                    "Corrupt encrypted attachment detected: {AttachmentId} ({RelativePath}).",
                    row.Id,
                    row.RelativePath);
            }
        }
    }

    private async Task RevalidateAttachmentSourcesAsync(CancellationToken cancellationToken)
    {
        if (_sourceResolver is null)
        {
            return;
        }

        List<SessionAttachmentRecord> rows = await ListAllRowsAsync(cancellationToken).ConfigureAwait(false);
        foreach (SessionAttachmentRecord row in rows)
        {
            if (row.Source is not { Kind: AttachmentSourceKind.WorkspaceFile } source)
            {
                continue;
            }

            AttachmentSourceMetadata revalidated;
            try
            {
                revalidated = await _sourceResolver.RevalidateAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                revalidated = source with
                {
                    Status = AttachmentSourceStatus.CorruptMetadata,
                    DiagnosticReason = "Source metadata could not be safely revalidated.",
                };
            }

            await UpdateSourceAsync(row.Id, revalidated, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SessionAttachmentRecord>> RevalidateBoundSourcesAsync(

        Guid sessionId,

        CancellationToken cancellationToken = default)

    {

        IReadOnlyList<SessionAttachmentRecord> rows = await ListBoundAsync(sessionId, cancellationToken)

            .ConfigureAwait(false);

        if (_sourceResolver is null)

        {

            return rows;

        }

        List<SessionAttachmentRecord> revalidated = new(rows.Count);

        foreach (SessionAttachmentRecord row in rows)

        {

            cancellationToken.ThrowIfCancellationRequested();

            if (row.Source is not { Kind: AttachmentSourceKind.WorkspaceFile } source)

            {

                revalidated.Add(row);

                continue;

            }

            AttachmentSourceMetadata current;

            try

            {

                current = await _sourceResolver

                    .RevalidateAsync(source, cancellationToken)

                    .ConfigureAwait(false);

            }

            catch (OperationCanceledException)

            {

                throw;

            }

            catch (Exception)

            {

                current = source with

                {

                    Status = AttachmentSourceStatus.CorruptMetadata,

                    DiagnosticReason = "Source metadata could not be safely revalidated.",

                };

            }

            await UpdateSourceAsync(row.Id, current, cancellationToken).ConfigureAwait(false);

            revalidated.Add(row with { Source = current });

        }

        return revalidated;

    }

    private async Task SweepMissingSessionRowsAndDirectoriesAsync(CancellationToken cancellationToken)
    {

        HashSet<Guid> liveSessions = await ListLiveSessionIdsAsync(cancellationToken).ConfigureAwait(false);

        List<Guid> orphanSessionIds = await ListDistinctBoundSessionIdsAsync(cancellationToken).ConfigureAwait(false);

        foreach (Guid sessionId in orphanSessionIds)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (liveSessions.Contains(sessionId))
            {

                continue;

            }

            using IDisposable gate = await AttachmentGates
                .AcquireAsync(SessionGateKey(sessionId), cancellationToken)
                .ConfigureAwait(false);

            if (liveSessions.Contains(sessionId)
                || await SessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {

                continue;

            }

            await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {

                    DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                        .ConfigureAwait(false);

                    await using DbCommand cmd = connection.CreateCommand();

                    cmd.Transaction = transaction;

                    cmd.CommandText =
                        """
                        DELETE FROM "SessionAttachments"
                        WHERE "SessionId" = @sessionId
                        """;

                    AddParameter(cmd, "@sessionId", sessionId.ToString());

                    _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                },
                cancellationToken).ConfigureAwait(false);

            _ = TryDeleteSessionDirectory(sessionId);

        }

        if (!Directory.Exists(_attachmentsRoot))
        {

            return;

        }

        foreach (string dir in Directory.EnumerateDirectories(_attachmentsRoot))
        {

            cancellationToken.ThrowIfCancellationRequested();

            string name = Path.GetFileName(dir);

            if (string.Equals(name, "_pending", StringComparison.Ordinal))
            {

                continue;

            }

            if (!Guid.TryParseExact(name, "N", out Guid sessionId))
            {

                continue;

            }

            string absoluteDir = Path.GetFullPath(dir);

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_attachmentsRoot, absoluteDir, out _))
            {

                continue;

            }

            if (liveSessions.Contains(sessionId)
                || await SessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {

                continue;

            }

            using IDisposable gate = await AttachmentGates
                .AcquireAsync(SessionGateKey(sessionId), cancellationToken)
                .ConfigureAwait(false);

            if (await SessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {

                continue;

            }

            _ = TryDeleteSessionDirectory(sessionId);

        }

    }

    private async Task SweepMissingFileRowsAsync(CancellationToken cancellationToken)
    {

        List<SessionAttachmentRecord> all = await ListAllRowsAsync(cancellationToken).ConfigureAwait(false);

        foreach (SessionAttachmentRecord row in all)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string absolute;

            try
            {

                absolute = ResolveUnderRoot(row.RelativePath);

            }
            catch (InvalidOperationException)
            {

                await DeleteRowByIdAsync(row, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "Deleted SessionAttachments row {AttachmentId} with escaping RelativePath.",
                    row.Id);

                continue;

            }

            if (File.Exists(absolute))
            {

                continue;

            }

            await DeleteRowByIdAsync(row, cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                "Deleted SessionAttachments row {AttachmentId} whose file is missing ({RelativePath}).",
                row.Id,
                row.RelativePath);

        }

    }

    private async Task DeleteRowByIdAsync(SessionAttachmentRecord row, CancellationToken cancellationToken)
    {

        string? gateKey = row.SessionId is Guid sessionId
            ? SessionGateKey(sessionId)
            : row.PendingTurnId is { } turn
                && SessionAttachmentPathSanitizer.TryValidatePendingTurnId(turn, out string safeTurn, out _)
                    ? PendingTurnGateKey(safeTurn)
                    : null;

        IDisposable? gate = gateKey is null
            ? null
            : await AttachmentGates.AcquireAsync(gateKey, cancellationToken).ConfigureAwait(false);

        using IDisposable? gateLease = gate;

        await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    DELETE FROM "SessionAttachments"
                    WHERE "Id" = @id
                    """;

                AddParameter(cmd, "@id", row.Id.ToString());

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken).ConfigureAwait(false);

    }

    private async Task VerifyCopiedFileAsync(
        string absolutePath,
        SessionAttachmentRecord source,
        CancellationToken cancellationToken)
    {
        await using Stream plaintext = await _blobStore
            .OpenReadAsync(
                absolutePath,
                EncryptedBlobPurpose.SessionAttachment,
                cancellationToken)
            .ConfigureAwait(false);
        if (plaintext.Length != source.ByteLength)
        {
            throw new InvalidOperationException(
                $"Fork copy length mismatch for '{source.Id}' "
                + $"(expected {source.ByteLength}, found {plaintext.Length}).");
        }

        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await plaintext.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            hasher.AppendData(buffer, 0, read);
        }

        string hash = Convert.ToHexString(hasher.GetHashAndReset());

        if (!string.Equals(hash, source.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Fork copy hash mismatch for '{source.Id}'.");
        }
    }

    private async Task<HashSet<Guid>> ListLiveSessionIdsAsync(CancellationToken cancellationToken)
    {

        HashSet<Guid> ids = [];

        List<Guid> listed = await _db.Sessions
            .AsNoTracking()
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (Guid id in listed)
        {

            ids.Add(id);

        }

        return ids;

    }

    private async Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await _db.Sessions
            .AsNoTracking()
            .AnyAsync(s => s.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

    private async Task<List<Guid>> ListDistinctBoundSessionIdsAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            SELECT DISTINCT "SessionId"
            FROM "SessionAttachments"
            WHERE "SessionId" IS NOT NULL
            """;

        List<Guid> ids = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            ids.Add(Guid.Parse(reader.GetString(0)));

        }

        return ids;

    }

    private async Task<List<SessionAttachmentRecord>> ListAllRowsAsync(CancellationToken cancellationToken)
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
            """;

        List<SessionAttachmentRecord> rows = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            rows.Add(ReadRecord(reader));

        }

        return rows;

    }

}
