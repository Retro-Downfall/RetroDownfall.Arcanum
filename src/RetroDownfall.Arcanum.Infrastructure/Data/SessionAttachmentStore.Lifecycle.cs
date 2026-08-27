using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class SessionAttachmentStore
{

    private const int ForkAttachmentPageSize = 128;

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

        AddParameter(cmd, "@sessionId", sessionId.ToString().ToUpperInvariant());

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

            AddParameter(cmd, "@sessionId", sessionId.ToString().ToUpperInvariant());

            AddParameter(cmd, "@entryId", entryId.ToString().ToUpperInvariant());

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

    public async IAsyncEnumerable<IReadOnlyList<SessionAttachmentRecord>> ReadBoundForForkPagesAsync(
        Guid sourceSessionId,
        long maximumSourceEntrySequence,
        bool includeEntrylessAttachments,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        Guid? afterAttachmentId = null;

        while (true)
        {

            IReadOnlyList<SessionAttachmentRecord> page = await ReadBoundForForkPageAsync(
                    sourceSessionId,
                    maximumSourceEntrySequence,
                    includeEntrylessAttachments,
                    afterAttachmentId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (page.Count == 0)
            {

                yield break;

            }

            yield return page;

            afterAttachmentId = page[^1].Id;

        }

    }

    private async Task<IReadOnlyList<SessionAttachmentRecord>> ReadBoundForForkPageAsync(
        Guid sourceSessionId,
        long maximumSourceEntrySequence,
        bool includeEntrylessAttachments,
        Guid? afterAttachmentId,
        CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                EnlistAmbientTransaction(cmd);

                string cursorClause = afterAttachmentId is null
                    ? string.Empty
                    : "AND attachment.\"Id\" > @afterAttachmentId";

                cmd.CommandText =
                    $$"""
                    SELECT attachment."Id", attachment."SessionId", attachment."EntryId",
                           attachment."PendingTurnId", attachment."State", attachment."LogicalKey",
                           attachment."OriginalFileName", attachment."Version", attachment."RelativePath",
                           attachment."ContentSha256", attachment."MimeType", attachment."ByteLength",
                           attachment."Kind", attachment."CreatedAt", attachment."SourceKind",
                           attachment."SourceWorkspaceIdentity", attachment."SourceRelativePath",
                           attachment."SourceCanonicalPath", attachment."SourceContentSha256",
                           attachment."SourceFileIdentity", attachment."SourceLastWriteAt",
                           attachment."SourceByteLength", attachment."SourceStatus",
                           attachment."SourceDiagnosticReason", attachment."EncryptionVersion",
                           attachment."EncryptionKeyId"
                    FROM "SessionAttachments" AS attachment
                    WHERE attachment."SessionId" = @sessionId
                      AND attachment."State" = @state
                      {{cursorClause}}
                      -- Exact, not COLLATE NOCASE. Both sides of both comparisons now hold the one
                      -- canonical spelling - Entries' two columns have always held it, and this table's
                      -- EntryId does from the schema step that moved this family - so the case-insensitive
                      -- collation this predicate used to carry bought nothing and cost the index behind
                      -- Entries' primary key, once per attachment row, on a fork that pages them.
                      AND (
                          @includeAllBound = 1
                          OR EXISTS (
                              SELECT 1
                              FROM "Entries" AS sourceEntry
                              WHERE sourceEntry."Id" = attachment."EntryId"
                                AND sourceEntry."SessionId" = @sessionId
                                AND sourceEntry."Sequence" <= @maximumSourceEntrySequence
                          )
                      )
                    ORDER BY attachment."Id" ASC
                    LIMIT @pageSize
                    """;

                AddParameter(cmd, "@sessionId", sourceSessionId.ToString().ToUpperInvariant());

                AddParameter(cmd, "@state", nameof(SessionAttachmentState.Bound));

                AddParameter(cmd, "@includeAllBound", includeEntrylessAttachments ? 1 : 0);

                AddParameter(cmd, "@maximumSourceEntrySequence", maximumSourceEntrySequence);

                AddParameter(cmd, "@pageSize", ForkAttachmentPageSize);

                if (afterAttachmentId is Guid cursor)
                {

                    AddParameter(cmd, "@afterAttachmentId", cursor.ToString().ToUpperInvariant());

                }

                List<SessionAttachmentRecord> rows = new(ForkAttachmentPageSize);

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

                if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                        destAbsolute,
                        FileSystemObjectKind.RegularFile,
                        out IdentityOwnedFileSystemArtifact blobAuthority))
                {

                    throw new IOException(
                        "Fork copy could not capture the owned attachment blob identity.");

                }

                _forkBlobAuthorities.Remove(plan);

                _forkBlobAuthorities.Add(
                    plan,
                    new ForkBlobAuthority(blobAuthority));

            }

        }
        catch
        {

            foreach (SessionAttachmentForkCopyPlan plan in plans)
            {

                _forkBlobAuthorities.Remove(plan);

            }

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

        try
        {

            foreach (SessionAttachmentForkCopyPlan plan in plans)
            {
                string relativePath = NormalizeRelativePath(BuildRelativePath(
                    forkSessionId,
                    pendingTurnId: null,
                    plan.Source.LogicalKey,
                    plan.Source.Version,
                    plan.Source.OriginalFileName));

                ForkBlobAuthority authority;

                if (_forkBlobAuthorities.TryGetValue(
                        plan,
                        out ForkBlobAuthority? retainedAuthority))
                {

                    authority = retainedAuthority;

                }
                else
                {

                    string absolutePath = ResolveUnderRoot(relativePath);

                    await VerifyCopiedFileAsync(
                        absolutePath,
                        plan.Source,
                        cancellationToken).ConfigureAwait(false);

                    if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                            absolutePath,
                            FileSystemObjectKind.RegularFile,
                            out IdentityOwnedFileSystemArtifact recapturedArtifact))
                    {

                        throw new IOException(
                            "Fork attachment row insertion could not recapture the copied blob identity.");

                    }

                    authority = new ForkBlobAuthority(recapturedArtifact);

                }

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

                await InsertRowAsync(
                    row,
                    authority.Artifact,
                    cancellationToken).ConfigureAwait(false);

            }

        }
        finally
        {

            foreach (SessionAttachmentForkCopyPlan plan in plans)
            {

                _forkBlobAuthorities.Remove(plan);

            }

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

        // ListBoundAsync returns every version of every logical key, and all versions of one key name
        // the same workspace file. Revalidation is a pure function of the row's stored source metadata
        // plus the file's current state, so rows whose stored metadata is identical must revalidate
        // identically — memoizing them collapses the repeated open/containment-check/double-SHA-256 pass
        // over one file into a single pass per distinct snapshot, for the whole request.
        Dictionary<AttachmentSourceMetadata, AttachmentSourceMetadata> observed = [];

        foreach (SessionAttachmentRecord row in rows)

        {

            cancellationToken.ThrowIfCancellationRequested();

            if (row.Source is not { Kind: AttachmentSourceKind.WorkspaceFile } source)

            {

                revalidated.Add(row);

                continue;

            }

            if (!observed.TryGetValue(source, out AttachmentSourceMetadata? current))

            {

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

                observed[source] = current;

            }

            // Persist only a real change. Revalidating an unchanged source is the overwhelmingly common
            // case, and this endpoint is driven by a debounced workspace watcher, so writing back
            // byte-identical column values once per version row per keystroke is pure write amplification.
            if (current != source)

            {

                await UpdateSourceAsync(row.Id, current, cancellationToken).ConfigureAwait(false);

            }

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

                    AddParameter(cmd, "@sessionId", sessionId.ToString().ToUpperInvariant());

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

        if (AfterMissingFileSnapshotForTesting is not null)
        {

            await AfterMissingFileSnapshotForTesting(cancellationToken).ConfigureAwait(false);

        }

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

                if (await DeleteSweptRowAsync(row, cancellationToken).ConfigureAwait(false))
                {

                    _logger.LogWarning(
                        "Deleted SessionAttachments row {AttachmentId} with escaping RelativePath.",
                        row.Id);

                }

                continue;

            }

            if (File.Exists(absolute))
            {

                continue;

            }

            if (await DeleteSweptRowAsync(row, cancellationToken).ConfigureAwait(false))
            {

                _logger.LogWarning(
                    "Deleted SessionAttachments row {AttachmentId} whose file is missing ({RelativePath}).",
                    row.Id,
                    row.RelativePath);

            }

        }

    }

    /// <summary>
    /// Deletes a row a sweep decided is dead, but only while the persisted row still names the exact
    /// <c>State</c> and <c>RelativePath</c> the sweep observed.
    /// </summary>
    /// <remarks>
    /// <see cref="SweepMissingFileRowsAsync"/> snapshots every row with no gate held, so
    /// <c>PromotePendingAsync</c> can rewrite <c>SessionId</c>/<c>State</c>/<c>RelativePath</c> and unlink
    /// the old pending file between the snapshot and the <c>File.Exists</c> probe. An unconditional
    /// <c>DELETE ... WHERE "Id" = @id</c> would then destroy the freshly promoted row, and the orphan-file
    /// sweep that runs next would unlink its ciphertext — silently losing an attachment the persisted
    /// Entry still references. The <c>State</c>/<c>RelativePath</c> predicate makes the delete an atomic
    /// no-op in that window, matching the guard every sibling sweep in this file already applies. Returns
    /// <see langword="true"/> only when a row was actually removed, so the caller never logs a phantom
    /// deletion.
    /// </remarks>
    private async Task<bool> DeleteSweptRowAsync(SessionAttachmentRecord row, CancellationToken cancellationToken)
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

        int affected = 0;

        await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    DELETE FROM "SessionAttachments"
                    WHERE "Id" = @id
                      AND "State" = @state
                      AND "RelativePath" = @relativePath
                    """;

                AddParameter(cmd, "@id", row.Id.ToString().ToUpperInvariant());

                AddParameter(cmd, "@state", row.State.ToString());

                AddParameter(cmd, "@relativePath", row.RelativePath);

                affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken).ConfigureAwait(false);

        return affected > 0;

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
