using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class SessionAttachmentStore
{
    private const int ForkAttachmentPageSize = 128;

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

            AddParameter(cmd, "@sessionId", GrimoireEntitySql.Format(sessionId));

            AddParameter(cmd, "@entryId", GrimoireEntitySql.Format(entryId));

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
                      -- canonical spelling - Entries' own columns have always held it, and this table's
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

                AddParameter(cmd, "@sessionId", GrimoireEntitySql.Format(sourceSessionId));

                AddParameter(cmd, "@state", nameof(SessionAttachmentState.Bound));

                AddParameter(cmd, "@includeAllBound", includeEntrylessAttachments ? 1 : 0);

                AddParameter(cmd, "@maximumSourceEntrySequence", maximumSourceEntrySequence);

                AddParameter(cmd, "@pageSize", ForkAttachmentPageSize);

                if (afterAttachmentId is Guid cursor)
                {
                    AddParameter(cmd, "@afterAttachmentId", GrimoireEntitySql.Format(cursor));
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

    public SessionAttachmentForkScope BeginForkCopyScope(Guid forkSessionId)
    {
        ForkCopyScope scope = new(this, forkSessionId);

        if (!_forkCopyScopes.TryAdd(forkSessionId, scope))
        {
            throw new InvalidOperationException("A fork copy scope already owns this Session.");
        }

        return scope;
    }

    private sealed class ForkCopyScope(SessionAttachmentStore store, Guid sessionId) : SessionAttachmentForkScope
    {
        private readonly List<IdentityOwnedFileSystemArtifact> _copies = [];

        private readonly Dictionary<Guid, ForkBlobAuthority> _authorities = [];

        private bool _completed;

        private int _disposed;

        public void Track(IdentityOwnedFileSystemArtifact copy)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            if (_completed)
            {
                throw new InvalidOperationException("A completed fork cannot register more copies.");
            }

            _copies.Add(copy);
        }

        public void Remember(Guid attachmentId, ForkBlobAuthority authority)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            if (_completed)
            {
                throw new InvalidOperationException("A completed fork cannot register more copies.");
            }

            _authorities.Add(attachmentId, authority);
        }

        public ForkBlobAuthority? Find(Guid attachmentId) =>
            _disposed == 0 && !_completed && _authorities.TryGetValue(attachmentId, out ForkBlobAuthority? authority)
                ? authority
                : null;

        public void Forget(Guid attachmentId) => _authorities.Remove(attachmentId);

        public override void Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            _completed = true;

            _authorities.Clear();
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _ = store._forkCopyScopes.TryRemove(new KeyValuePair<Guid, ForkCopyScope>(sessionId, this));

            if (!_completed)
            {
                foreach (IdentityOwnedFileSystemArtifact copy in _copies)
                {
                    try
                    {
                        _ = IdentityOwnedFileSystemCleanup.TryDelete(copy);
                    }
                    catch (Exception exception)
                    {
                        // Compensation must preserve the original fork failure even if filesystem
                        // cleanup or its diagnostic sink fails. Reconciliation can retry abandoned bytes.
                        try
                        {
                            store._logger.LogWarning(exception, "Owned fork copy cleanup failed for Session {SessionId}.", sessionId);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }

            _copies.Clear();

            _authorities.Clear();
        }
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

        List<IdentityOwnedFileSystemArtifact> copies = [];

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
                    plan.NewAttachmentId);

                string destAbsolute = ResolveUnderRoot(newRelative);

                if (BeforeForkCopyForTesting is not null)
                {
                    await BeforeForkCopyForTesting(destAbsolute, cancellationToken).ConfigureAwait(false);
                }

                IdentityOwnedFileSystemArtifact blobAuthority = await AtomicCopyFileAsync(
                    sourceAbsolute,
                    destAbsolute,
                    cancellationToken).ConfigureAwait(false);

                copies.Add(blobAuthority);

                if (_forkCopyScopes.TryGetValue(forkSessionId, out ForkCopyScope? scope))
                {
                    scope.Track(blobAuthority);
                }

                await VerifyCopiedFileAsync(destAbsolute, plan.Source, cancellationToken).ConfigureAwait(false);

                ForkBlobAuthority authority = new(forkSessionId, ComputeForkPlanDigest(plan), blobAuthority);

                if (scope is not null)
                {
                    scope.Remember(plan.NewAttachmentId, authority);
                }
                else
                {
                    _forkBlobAuthorities.Remove(plan);

                    _forkBlobAuthorities.Add(plan, authority);
                }
            }
        }
        catch
        {
            foreach (SessionAttachmentForkCopyPlan plan in plans)
            {
                _forkBlobAuthorities.Remove(plan);

                if (_forkCopyScopes.TryGetValue(forkSessionId, out ForkCopyScope? scope))
                {
                    scope.Forget(plan.NewAttachmentId);
                }
            }

            if (!_forkCopyScopes.ContainsKey(forkSessionId))
            {
                foreach (IdentityOwnedFileSystemArtifact copy in copies)
                {
                    _ = IdentityOwnedFileSystemCleanup.TryDelete(copy);
                }
            }

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
                    plan.NewAttachmentId));

                ForkBlobAuthority? authority = _forkCopyScopes.TryGetValue(forkSessionId, out ForkCopyScope? scope)
                    ? scope.Find(plan.NewAttachmentId)
                    : _forkBlobAuthorities.TryGetValue(plan, out ForkBlobAuthority? retainedAuthority)
                        ? retainedAuthority
                        : null;

                if (authority is null
                    || authority.ForkSessionId != forkSessionId
                    || !CryptographicOperations.FixedTimeEquals(authority.PlanDigest, ComputeForkPlanDigest(plan)))
                {
                    throw new InvalidOperationException(
                        "Fork attachment row insertion requires the verified copy-time authority for this plan and Session.");
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

                if (_forkCopyScopes.TryGetValue(forkSessionId, out ForkCopyScope? scope))
                {
                    scope.Forget(plan.NewAttachmentId);
                }
            }
        }
    }

    // Keep only a fixed-size metadata binding with each copy identity. Source records still live
    // for one page, and equivalent plans reconstructed from the second paged read retain authority.
    private static byte[] ComputeForkPlanDigest(SessionAttachmentForkCopyPlan plan) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan, GrimoireJsonContext.Default.SessionAttachmentForkCopyPlan));

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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LogSourceRevalidationFailure(row.Id, ex);

                continue;
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
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    LogSourceRevalidationFailure(row.Id, ex);

                    current = source;
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

    private void LogSourceRevalidationFailure(Guid attachmentId, Exception exception)
    {
        string kind = exception switch
        {
            SqliteException => "Storage",
            UnauthorizedAccessException => "Access",
            IOException => "IO",
            _ => "Unexpected",
        };

        SqliteException? sqlite = exception as SqliteException;

        _logger.LogWarning(
            "Attachment {AttachmentId} source revalidation did not complete ({FailureKind}; SQLite {SqliteCode}/{SqliteExtendedCode}). The last successful observation and snapshot were retained. Retry attachment listing; if this persists, check workspace registry and storage availability.",
            attachmentId,
            kind,
            sqlite?.SqliteErrorCode,
            sqlite?.SqliteExtendedErrorCode);
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

                    AddParameter(cmd, "@sessionId", GrimoireEntitySql.Format(sessionId));

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

            // Presence is only a fast path. Failure to resolve or observe a file is never absence authority.
            try
            {
                if (File.Exists(ResolveUnderRoot(row.RelativePath)))
                {
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
                // The gated native proof below refuses malformed or escaping selectors.
            }

            if (await DeleteSweptRowAsync(row, cancellationToken, requireMissingFile: true).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Deleted SessionAttachments row {AttachmentId} after proving its file is missing.",
                    row.Id);
            }
        }
    }

    /// <summary>
    /// Removes a row <see cref="PersistNewWithOutcomeAsync"/> created for a turn that then failed, and
    /// its blob. Guarded by the same <c>State</c>/<c>RelativePath</c> predicate as a sweep delete, so a
    /// row that was promoted (or otherwise rewritten) in the meantime is left alone. The derived index
    /// rows go with it through their <c>ON DELETE CASCADE</c> keys. The blob unlink is best effort: the
    /// orphan-file sweep reclaims it if it fails. It runs while the attachment gate is still held,
    /// keeping row deletion and blob cleanup serialized with subsequent versions of the logical key.
    /// New snapshots receive distinct opaque paths even when a logical version number is reused.
    /// </summary>
    public async Task<bool> DeleteCreatedAttachmentAsync(
        SessionAttachmentRecord created,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(created);

        return await DeleteSweptRowAsync(
            created,
            cancellationToken,
            whileGateHeld: async token =>
            {
                if (AfterCreatedRowDeletedForTesting is not null)
                {
                    await AfterCreatedRowDeletedForTesting(token).ConfigureAwait(false);
                }

                try
                {
                    TryDeleteFile(ResolveUnderRoot(created.RelativePath));
                }
                catch (InvalidOperationException)
                {
                    // A path that escapes the root names nothing this store owns; the row is already gone.
                }
            }).ConfigureAwait(false);
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
    /// deletion. <paramref name="whileGateHeld"/>, when given, runs after a row was removed and before
    /// the gate is released, for work that must not interleave with another persist of the same key.
    /// </remarks>
    private async Task<bool> DeleteSweptRowAsync(
        SessionAttachmentRecord row,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? whileGateHeld = null,
        bool requireMissingFile = false)
    {
        string? gateKey = row.SessionId is Guid sessionId
            ? SessionGateKey(sessionId)
            : row.PendingTurnId is { } turn
                && SessionAttachmentPathSanitizer.TryValidatePendingTurnId(turn, out string safeTurn, out _)
                    ? PendingTurnGateKey(safeTurn)
                    : null;

        if (requireMissingFile
            && (gateKey is null
                || row.State is not (SessionAttachmentState.Pending or SessionAttachmentState.Bound)
                || (row.State == SessionAttachmentState.Bound
                    ? row.SessionId is null || row.PendingTurnId is not null
                    : row.SessionId is not null
                        || row.PendingTurnId is not { } pending
                        || !SessionAttachmentPathSanitizer.TryValidatePendingTurnId(pending, out string exactPending, out _)
                        || !string.Equals(pending, exactPending, StringComparison.Ordinal))))
        {
            LogUnprovedMissingFile(row.Id);

            return false;
        }

        IDisposable? gate = gateKey is null
            ? null
            : await AttachmentGates.AcquireAsync(gateKey, cancellationToken).ConfigureAwait(false);

        using IDisposable? gateLease = gate;

        int affected = 0;

        await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                (SecureFileOpenStatus status, SafeFileHandle? root, SafeFileHandle? parent) = requireMissingFile
                    ? ProbeMissingAttachmentFile(row)
                    : (SecureFileOpenStatus.NotFound, null, null);

                using SafeFileHandle? rootLease = root;

                using SafeFileHandle? parentLease = parent;

                cancellationToken.ThrowIfCancellationRequested();

                if (status != SecureFileOpenStatus.NotFound)
                {
                    if (status != SecureFileOpenStatus.Success)
                    {
                        LogUnprovedMissingFile(row.Id);
                    }

                    return;
                }

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    DELETE FROM "SessionAttachments"
                    WHERE "Id" = @id
                      AND "State" = @state
                      AND "RelativePath" = @relativePath
                    """;

                AddParameter(cmd, "@id", GrimoireEntitySql.Format(row.Id));

                AddParameter(cmd, "@state", row.State.ToString());

                AddParameter(cmd, "@relativePath", row.RelativePath);

                affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (affected > 0 && whileGateHeld is not null)
        {
            await whileGateHeld(cancellationToken).ConfigureAwait(false);
        }

        return affected > 0;
    }

    private void LogUnprovedMissingFile(Guid attachmentId) =>
        _logger.LogWarning(
            "Retained SessionAttachments row {AttachmentId}: file absence could not be proved. Restore ordinary attachment directories and retry reconciliation.",
            attachmentId);

    private (SecureFileOpenStatus Status, SafeFileHandle? Root, SafeFileHandle? Parent) ProbeMissingAttachmentFile(SessionAttachmentRecord row)
    {
        string relativePath = row.RelativePath;

        SafeFileHandle? root = null;

        SafeFileHandle? parent = null;

        bool retainHandles = false;

        try
        {
            if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
            {
                return (SecureFileOpenStatus.Rejected, null, null);
            }

            string[] segments = relativePath.Split(OperatingSystem.IsWindows() ? ['/', '\\'] : ['/']);

            if (segments.Any(static segment => !IsOrdinaryAttachmentSegment(segment))
                || !IsAttachmentPathOwnedByRow(row, segments)
                || !WorkspacePathPolicy.TryCanonicalize(_attachmentsRoot, out string? canonicalRoot, out bool exists)
                || !exists
                || !FileHandleIdentityInterop.TryOpenDirectoryMetadata(canonicalRoot, out root, out FileHandleMetadata rootMetadata))
            {
                return (SecureFileOpenStatus.Rejected, null, null);
            }

            SafeFileHandle current = root;

            string expectedParent = canonicalRoot;

            FileHandleMetadata parentMetadata = rootMetadata;

            foreach (string segment in segments.AsSpan(0, segments.Length - 1))
            {
                if (!FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(
                        current, segment, requestReadControl: false, out SafeFileHandle next, out parentMetadata))
                {
                    next.Dispose();

                    return (SecureFileOpenStatus.Rejected, null, null);
                }

                parent?.Dispose();

                parent = next;

                current = next;

                expectedParent = Path.Combine(expectedParent, segment);
            }

            SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(
                current, segments[^1], out SafeFileHandle? leaf);

            leaf?.Dispose();

            if (status != SecureFileOpenStatus.NotFound)
            {
                return (status, null, null);
            }

            // ENOTDIR is mapped to NotFound by the shared native helper. It is absence only for a
            // strict single leaf beneath the ordinary, still-current parent just admitted above.
            if (!WorkspacePathPolicy.TryCanonicalize(_attachmentsRoot, out string? currentRoot, out bool rootExists)
                || !rootExists
                || !SameAttachmentDirectory(currentRoot, canonicalRoot)
                || !IsCurrentAttachmentDirectory(root, canonicalRoot, rootMetadata.Identity)
                || !IsCurrentAttachmentDirectory(current, expectedParent, parentMetadata.Identity))
            {
                return (SecureFileOpenStatus.Rejected, null, null);
            }

            retainHandles = true;

            return (SecureFileOpenStatus.NotFound, root, parent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (SecureFileOpenStatus.Rejected, null, null);
        }
        finally
        {
            if (!retainHandles)
            {
                parent?.Dispose();

                root?.Dispose();
            }
        }
    }

    private static bool IsAttachmentPathOwnedByRow(SessionAttachmentRecord row, string[] segments)
    {
        if (row.State == SessionAttachmentState.Bound)
        {
            return row.SessionId is Guid sessionId
                && row.PendingTurnId is null
                && segments.Length >= 2
                && segments[0].Length == 32
                && Guid.TryParseExact(segments[0], "N", out Guid pathOwner)
                && pathOwner == sessionId;
        }

        return row.State == SessionAttachmentState.Pending
            && row.SessionId is null
            && row.PendingTurnId is { } pendingTurn
            && segments.Length >= 3
            && string.Equals(segments[0], "_pending", StringComparison.Ordinal)
            && string.Equals(segments[1], pendingTurn, StringComparison.Ordinal);
    }

    private static bool IsCurrentAttachmentDirectory(SafeFileHandle handle, string expectedPath, FileHandleIdentity identity) =>
        FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? kernelPath)
        && SameAttachmentDirectory(kernelPath, expectedPath)
        && FileHandleIdentityInterop.TryGetPathMetadataNoFollow(expectedPath, out FileHandleMetadata current)
        && current.Kind == FileSystemObjectKind.Directory
        && current.Identity == identity;

    private static bool SameAttachmentDirectory(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsOrdinaryAttachmentSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)
            || segment is "." or ".."
            || segment.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string device = segment.Split('.')[0];

        return segment[^1] is not ('.' or ' ')
            && segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) < 0
            && !segment.Any(static character => character < ' ')
            && !device.Equals("CON", StringComparison.OrdinalIgnoreCase)
            && !device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            && !device.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            && !device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            && !(device.Length == 4
                && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && device[3] is >= '1' and <= '9');
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
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT "Id"
            FROM "Sessions";
            """,
            cancellationToken).ConfigureAwait(false);

        HashSet<Guid> ids = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(GrimoireEntitySql.ReadGuid(reader, 0));
        }

        return ids;
    }

    private async Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT 1
            FROM "Sessions"
            WHERE "Id" = $sessionId
            LIMIT 1;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(
            command,
            "$sessionId",
            GrimoireEntitySql.Format(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is not null and not DBNull;
    }

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
