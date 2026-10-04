using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>The archived rows a staged generation holds of identities this installation erased.</summary>
/// <param name="SagaIds">Saga memory ids, each as the archive stores it.</param>
/// <param name="LexiconIds">Lexicon entry ids, each as the archive stores it.</param>
/// <param name="CovenantEntryIds">Covenant entry ids.</param>
internal sealed record BackupRestoreErasureMatches(
    IReadOnlyList<string> SagaIds,
    IReadOnlyList<string> LexiconIds,
    IReadOnlyList<Guid> CovenantEntryIds)
{
    /// <summary>No archived row matches.</summary>
    internal static BackupRestoreErasureMatches Empty { get; } = new([], [], []);

    internal bool IsEmpty => SagaIds.Count == 0 && LexiconIds.Count == 0 && CovenantEntryIds.Count == 0;
}

/// <summary>What one staged evidence step did, in counts and verdicts only.</summary>
/// <param name="SagaMemoriesRemoved">Saga memories purged, as the plan measured them before deleting.</param>
/// <param name="LexiconEntriesRemoved">Lexicon entries purged, as the plan measured them before deleting.</param>
/// <param name="CovenantEntriesRemoved">Covenant entries purged, as the entry plan measured them.</param>
/// <param name="RetirementPairsRemoved">Retirement suppression rows of the purged Saga memories.</param>
/// <param name="FingerprintsJoined">The destination's fingerprints, now the staged generation's.</param>
/// <param name="ReceiptsJoined">The destination's receipts, now the staged generation's.</param>
/// <param name="ArchiveRowsDropped">Archived fingerprints and receipts the destination does not hold.</param>
/// <param name="FullTextVerified">
/// Whether every full-text index read back <c>secure-delete = 1</c> before the purge, and every purged
/// Covenant entry's index did too.
/// </param>
/// <param name="CheckpointTruncated">Whether the staged write-ahead log was truncated after the commit.</param>
/// <param name="VectorMirrorsVerified">
/// Whether every vector mirror a purge reached was absent or a plain table. A legacy virtual-table mirror
/// is skipped and stays unreachable residue, which a live erase records as a scrub that never verifies.
/// </param>
internal sealed record BackupRestoreErasureApplicationReceipt(
    long SagaMemoriesRemoved,
    long LexiconEntriesRemoved,
    long CovenantEntriesRemoved,
    long RetirementPairsRemoved,
    long FingerprintsJoined,
    long ReceiptsJoined,
    long ArchiveRowsDropped,
    bool FullTextVerified,
    bool CheckpointTruncated,
    bool VectorMirrorsVerified = true)
{
    /// <summary>A staged generation that had nothing to reconcile, because the archive carried no Grimoire.</summary>
    internal static BackupRestoreErasureApplicationReceipt None { get; } = new(0, 0, 0, 0, 0, 0, 0, true, true);

    /// <summary>Whether anything was purged, joined or dropped.</summary>
    internal bool Touched =>
        SagaMemoriesRemoved != 0
        || LexiconEntriesRemoved != 0
        || CovenantEntriesRemoved != 0
        || RetirementPairsRemoved != 0
        || FingerprintsJoined != 0
        || ReceiptsJoined != 0
        || ArchiveRowsDropped != 0;
}

/// <summary>
/// Applies this installation's erasure evidence to a staged generation: everything archived that this
/// installation erased is purged, the evidence becomes this installation's exactly, and both are proven
/// before the caller may commit (§10.19.9).
/// </summary>
/// <remarks>
/// <para><b>Inside the caller's one <c>BEGIN IMMEDIATE</c>, in a fixed order.</b> Full-text secure delete
/// is enabled first, so every later delete scrubs. The archived disclosure tails are folded and the
/// destination's buckets joined. Every candidate row is read and fingerprinted before anything is
/// deleted, so a purge never changes what the match sees. The purge then follows the live erases' plans:
/// Saga and Lexicon rows through the shared artifact plan with their labels and a Saga memory's retirement
/// pair, Covenant entries through the shared entry plan, key reclamation and the key's curation included.
/// It takes one step the live erases do not: each owning Session's taint is recounted from the labels that
/// remain. The evidence tables are then replaced by the destination's, not joined. Last come the
/// post-conditions, and any that fails rolls the whole transaction back.</para>
///
/// <para><b>The same identity everywhere.</b> Each candidate is fingerprinted with the destination's key
/// exactly as the chokepoints and the erase routes fingerprint it: Saga by exact content in its scope,
/// Lexicon by <c>Name.Trim().ToUpperInvariant()</c> computed from the name in its scope, Covenant by
/// normalized key in its scope, and every Campaign by its GUID bytes, so every spelling of one Campaign
/// names one fingerprint. A store the destination holds no fingerprint for is not read at all.</para>
///
/// <para><b>Fail closed.</b> A candidate in a fingerprinted store whose stored scope or Campaign no
/// fingerprint can describe, an archive whose catalog makes a purge statement fail, and residue any
/// post-condition finds are all <see cref="BackupRestoreErasureCodes.VerificationFailed"/>, after a
/// rollback, so nothing the step did survives. A drifted catalog the drain left at head is handled the same
/// way: either every purged row is proven absent, or the restore refuses.</para>
///
/// <para>Content-free: no message names content, a name, a key, an identity or a fingerprint, and nothing
/// here logs. Nothing here reads the keychain; the caller hands in the key it already holds.</para>
/// </remarks>
internal static class BackupRestoreErasureEvidenceApplier
{
    private const string CovenantEntries = "covenant_entries";

    private const int FullRebuildRequired = (int)CovenantFtsRebuildState.FullRebuildRequired;

    /// <summary>How many content rowids one full-text count statement binds, far below any build's variable limit.</summary>
    private const int FullTextCountChunkSize = 500;

    private const string StagedProofFailed =
        "The restore could not prove that the staged archive no longer holds the items this installation erased, "
        + "so it stopped before committing anything and the current installation is unchanged. Restore a "
        + "different archive, or repair the installation the archive came from and archive it again. Diagnostics: ";

    /// <summary>
    /// Purges every archived item the destination erased, replaces the staged evidence with the
    /// destination's, and proves both, inside <paramref name="transaction"/>.
    /// </summary>
    /// <param name="destination">The destination read; anything but <c>Present</c> applies an empty evidence set.</param>
    /// <param name="key">The destination's key, which a <c>Present</c> destination's fingerprints require.</param>
    /// <param name="afterPurgeForTests">Runs after the evidence is replaced and before any post-condition.</param>
    /// <returns>
    /// The receipt, with <see cref="BackupRestoreErasureApplicationReceipt.CheckpointTruncated"/> false for the
    /// caller to settle after its commit; or a refusal, after the transaction was rolled back.
    /// </returns>
    internal static async Task<Result<BackupRestoreErasureApplicationReceipt>> ApplyAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        BackupRestoreErasureEvidence destination,
        MemoryErasureKey? key,
        IReadOnlyList<CovenantDisclosureState> destinationDisclosure,
        TimeProvider timeProvider,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterPurgeForTests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(destination);

        ArgumentNullException.ThrowIfNull(destinationDisclosure);

        ArgumentNullException.ThrowIfNull(timeProvider);

        MemoryErasureEvidenceSnapshot rows = destination.Kind is BackupRestoreErasureEvidenceKind.Present
            ? destination.Rows
            : MemoryErasureEvidenceSnapshot.Empty;

        if (rows.Fingerprints.Count > 0 && key is null)
        {
            return await RefuseAsync(transaction, "the destination's erasure key is not in hand").ConfigureAwait(false);
        }

        try
        {
            bool fullText = await EnableSecureDeleteAsync(staged, transaction, cancellationToken).ConfigureAwait(false);

            Result disclosure = await FoldAndJoinDisclosureAsync(
                staged,
                transaction,
                destinationDisclosure,
                timeProvider,
                cancellationToken).ConfigureAwait(false);

            if (disclosure.IsFailure)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);

                return disclosure.Error;
            }

            Result<BackupRestoreErasureMatches> found = rows.Fingerprints.Count == 0
                ? BackupRestoreErasureMatches.Empty
                : await FindMatchesAsync(staged, transaction, key!, rows, cancellationToken).ConfigureAwait(false);

            if (found.IsFailure)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);

                return found.Error;
            }

            Result<Purge> saga = await PurgeArtifactsAsync(
                staged,
                transaction,
                SensitiveArtifactKind.Saga,
                found.Value.SagaIds,
                timeProvider,
                cancellationToken).ConfigureAwait(false);

            if (saga.IsFailure)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);

                return saga.Error;
            }

            Result<Purge> lexicon = await PurgeArtifactsAsync(
                staged,
                transaction,
                SensitiveArtifactKind.Lexicon,
                found.Value.LexiconIds,
                timeProvider,
                cancellationToken).ConfigureAwait(false);

            if (lexicon.IsFailure)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);

                return lexicon.Error;
            }

            Result<CovenantPurge> covenant = await PurgeCovenantAsync(
                staged,
                transaction,
                found.Value.CovenantEntryIds,
                timeProvider,
                cancellationToken).ConfigureAwait(false);

            if (covenant.IsFailure)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);

                return covenant.Error;
            }

            long dropped = await MemoryErasureEvidence
                .ReplaceAllAsync(staged, transaction, rows, cancellationToken)
                .ConfigureAwait(false);

            if (afterPurgeForTests is not null)
            {
                await afterPurgeForTests(staged, transaction, cancellationToken).ConfigureAwait(false);
            }

            string? residue = await FindResidueAsync(
                staged,
                transaction,
                key,
                rows,
                saga.Value,
                lexicon.Value,
                covenant.Value,
                cancellationToken).ConfigureAwait(false);

            if (residue is not null)
            {
                return await RefuseAsync(transaction, residue).ConfigureAwait(false);
            }

            return new BackupRestoreErasureApplicationReceipt(
                saga.Value.Removed,
                lexicon.Value.Removed,
                covenant.Value.Removed,
                saga.Value.RetirementPairs,
                rows.Fingerprints.Count,
                rows.Receipts.Count,
                dropped,
                fullText && covenant.Value.FullTextVerified,
                CheckpointTruncated: false,
                VectorMirrorsVerified: saga.Value.VectorMirrorsVerified && lexicon.Value.VectorMirrorsVerified);
        }
        // A catalog the drain left drifted, an identity the archive cannot name, or a plan statement its
        // schema refuses: none of them is a purge that can be proven, so each refuses, naming its type.
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or InvalidDataException
                or FormatException
                or ArgumentException)
        {
            return await RefuseAsync(transaction, exception.GetType().Name).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fingerprints every candidate row of each store the destination holds fingerprints for, and keeps
    /// the ones that match.
    /// </summary>
    /// <remarks>
    /// Each candidate table is streamed once and only matching ids are kept. A row whose stored scope or
    /// Campaign no fingerprint can describe could be any identity at all, so in a fingerprinted store it
    /// refuses rather than being read as unmatched. A row whose identity value is empty cannot be one an
    /// erase recorded, because no erase records an empty identity, so it is passed over.
    /// </remarks>
    internal static async Task<Result<BackupRestoreErasureMatches>> FindMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryErasureKey key,
        MemoryErasureEvidenceSnapshot destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(key);

        ArgumentNullException.ThrowIfNull(destination);

        HashSet<string> saga = Fingerprints(destination, MemoryReviewStore.Saga);

        HashSet<string> lexicon = Fingerprints(destination, MemoryReviewStore.Lexicon);

        HashSet<string> covenant = Fingerprints(destination, MemoryReviewStore.Covenant);

        List<string> sagaIds = [];

        List<string> lexiconIds = [];

        List<Guid> covenantIds = [];

        if (saga.Count > 0
            && await BackupRestoreDatabaseWorker.TableExistsAsync(connection, "saga_memories", cancellationToken, transaction).ConfigureAwait(false))
        {
            await using SqliteCommand command = Command(
                connection,
                transaction,
                "SELECT Id, ScopeKindCode, CampaignId, Content FROM saga_memories;");

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string content = reader.GetString(3);

                if (content.Length == 0)
                {
                    continue;
                }

                MemoryErasureIdentity identity;

                try
                {
                    identity = SagaErasureWriteGate.SagaIdentity(
                        (SagaMemoryScopeKind)reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        content);
                }
                catch (FormatException)
                {
                    return Unnameable("an archived Saga memory's scope or Campaign");
                }

                if (Matches(saga, key, identity))
                {
                    if (reader.IsDBNull(0))
                    {
                        return Unnameable("an archived Saga memory's id");
                    }

                    sagaIds.Add(reader.GetString(0));
                }
            }
        }

        if (lexicon.Count > 0
            && await BackupRestoreDatabaseWorker.TableExistsAsync(connection, "lexicon_entries", cancellationToken, transaction).ConfigureAwait(false))
        {
            await using SqliteCommand command = Command(
                connection,
                transaction,
                "SELECT Id, Name, ScopeCampaignId FROM lexicon_entries;");

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string name = reader.GetString(1);

                if (name.Trim().Length == 0)
                {
                    continue;
                }

                // The empty string is the Global scope; anything else must be a Campaign.
                string scope = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);

                Guid? campaign = null;

                if (scope.Length > 0)
                {
                    if (!Guid.TryParse(scope, CultureInfo.InvariantCulture, out Guid parsed))
                    {
                        return Unnameable("an archived Lexicon entry's Campaign");
                    }

                    campaign = parsed;
                }

                MemoryErasureIdentity identity;

                try
                {
                    identity = MemoryErasureIdentity.ForLexicon(campaign, name);
                }
                catch (ArgumentException)
                {
                    // An empty Campaign GUID parses, and no erasure identity can name it.
                    return Unnameable("an archived Lexicon entry's Campaign");
                }

                if (Matches(lexicon, key, identity))
                {
                    if (reader.IsDBNull(0))
                    {
                        return Unnameable("an archived Lexicon entry's id");
                    }

                    lexiconIds.Add(reader.GetString(0));
                }
            }
        }

        if (covenant.Count > 0
            && await BackupRestoreDatabaseWorker.TableExistsAsync(connection, CovenantEntries, cancellationToken, transaction).ConfigureAwait(false))
        {
            await using SqliteCommand command = Command(
                connection,
                transaction,
                "SELECT EntryId, ScopeCode, CampaignId, NormalizedKey FROM covenant_entries;");

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string normalizedKey = reader.GetString(3);

                if (normalizedKey.Length == 0)
                {
                    continue;
                }

                MemoryErasureIdentity identity;

                try
                {
                    Guid? campaign = reader.IsDBNull(2)
                        ? null
                        : Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture);

                    identity = MemoryErasureIdentity.ForCovenant((CovenantScope)reader.GetInt64(1), campaign, normalizedKey);
                }
                catch (Exception exception) when (exception is FormatException or ArgumentException)
                {
                    return Unnameable("an archived Covenant entry's scope or Campaign");
                }

                if (Matches(covenant, key, identity))
                {
                    if (reader.IsDBNull(0) || !Guid.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, out Guid entryId))
                    {
                        return Unnameable("an archived Covenant entry's id");
                    }

                    covenantIds.Add(entryId);
                }
            }
        }

        return new BackupRestoreErasureMatches(sagaIds, lexiconIds, covenantIds);
    }

    /// <summary>
    /// Enables secure delete on each full-text index the catalog has and reads it back, so every delete
    /// after it scrubs the index rather than leaving tokens behind.
    /// </summary>
    /// <returns>Whether every index present reads back <c>secure-delete = 1</c>.</returns>
    private static async Task<bool> EnableSecureDeleteAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        bool verified = true;

        foreach (string index in (string[])["lexicon_fts", "covenant_fts"])
        {
            if (!await BackupRestoreDatabaseWorker.TableExistsAsync(staged, index, cancellationToken, transaction).ConfigureAwait(false))
            {
                continue;
            }

            await using (SqliteCommand enable = Command(
                staged,
                transaction,
                $"INSERT INTO {index}({index}, rank) VALUES('secure-delete', 1);"))
            {
                _ = await enable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using SqliteCommand read = Command(staged, transaction, $"SELECT v FROM {index}_config WHERE k = 'secure-delete';");

            verified &= await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long secureDelete
                && secureDelete == 1;
        }

        return verified;
    }

    /// <summary>
    /// Folds the archive's unfolded disclosure tails, as lower bounds, then joins the destination's
    /// effective buckets into the staged ones: the join this restore used to run only with the gate on.
    /// </summary>
    private static async Task<Result> FoldAndJoinDisclosureAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        IReadOnlyList<CovenantDisclosureState> destinationDisclosure,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await BackupRestoreDatabaseWorker.TableExistsAsync(staged, "external_disclosure_state", cancellationToken, transaction).ConfigureAwait(false))
        {
            return Result.Success();
        }

        if (await BackupRestoreDatabaseWorker.TableExistsAsync(staged, "disclosure_subject_state", cancellationToken, transaction).ConfigureAwait(false)
            && await BackupRestoreDatabaseWorker.TableExistsAsync(staged, "external_disclosure_receipts", cancellationToken, transaction).ConfigureAwait(false))
        {
            _ = await ExternalDisclosureStateFold
                .FoldAllUnfoldedAsync(staged, transaction, ExternalDisclosureFoldOrigin.RestoreStaging, cancellationToken)
                .ConfigureAwait(false);
        }

        if (destinationDisclosure.Count == 0)
        {
            return Result.Success();
        }

        Result<int> joined = await CovenantDisclosureStateJoiner
            .JoinIntoStagedAsync(staged, transaction, destinationDisclosure, timeProvider, cancellationToken)
            .ConfigureAwait(false);

        return joined.IsFailure ? joined.Error : Result.Success();
    }

    /// <summary>
    /// Removes each matched Saga or Lexicon artifact through the shared plan, then its labels and its Saga
    /// retirement pair, then recounts every Session that owned one of those labels.
    /// </summary>
    /// <remarks>
    /// <para>The plan deletes by an id's normalised spelling, and an archive can hold two rows whose ids
    /// differ only in case or dashes. Each normalised id is therefore counted first, and a count that is
    /// not exactly the matched rows that share it refuses, as the live erases refuse that shape: deleting
    /// the other row would purge something nothing erased.</para>
    ///
    /// <para>A Lexicon entry's full-text row is keyed by its content rowid and removed only by a trigger
    /// that skips retired rows, so each matched entry's rowid is read before the delete for the absence
    /// proof to look for.</para>
    /// </remarks>
    private static async Task<Result<Purge>> PurgeArtifactsAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        SensitiveArtifactKind kind,
        IReadOnlyList<string> ids,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        long removed = 0;

        long pairs = 0;

        bool vectorMirrorsVerified = true;

        List<string> keys = [];

        Dictionary<string, long> matched = new(StringComparer.Ordinal);

        List<SagaRow> sagaRows = [];

        List<long> rowIds = [];

        HashSet<string> sessions = new(StringComparer.Ordinal);

        foreach (string id in ids)
        {
            string artifactKey = CovenantIdentitySql.Key(id);

            if (artifactKey.Length == 0)
            {
                // A key with nothing left once normalised would match every blank-keyed row.
                throw new InvalidDataException("A matched artifact has an empty identity.");
            }

            if (matched.TryGetValue(artifactKey, out long count))
            {
                matched[artifactKey] = count + 1;
            }
            else
            {
                matched[artifactKey] = 1;

                keys.Add(artifactKey);
            }
        }

        bool labelled = await BackupRestoreDatabaseWorker
            .TableExistsAsync(staged, "artifact_sensitivity", cancellationToken, transaction)
            .ConfigureAwait(false);

        foreach (string artifactKey in keys)
        {
            // The count is measured before the delete, with the predicates the delete removes by. A delete's
            // own tally cannot say it: SQLite does not count a row a foreign key removes as its change.
            CovenantArtifactPlanTally measured = await CovenantArtifactPlanRunner
                .RunAsync(staged, transaction, kind, artifactKey, CovenantArtifactPlanMode.Count, cancellationToken)
                .ConfigureAwait(false);

            if (measured.ArtifactRows != matched[artifactKey])
            {
                return Refusal("an archived row shares the identity of a matched row without matching it");
            }

            removed += measured.ArtifactRows;

            if (kind is SensitiveArtifactKind.Saga)
            {
                sagaRows.AddRange(await ReadSagaRowsAsync(staged, transaction, artifactKey, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                rowIds.AddRange(await ReadLexiconRowIdsAsync(staged, transaction, artifactKey, cancellationToken).ConfigureAwait(false));
            }

            CovenantArtifactPlanTally deleted = await CovenantArtifactPlanRunner
                .RunAsync(staged, transaction, kind, artifactKey, CovenantArtifactPlanMode.Delete, cancellationToken)
                .ConfigureAwait(false);

            vectorMirrorsVerified &= deleted.VectorMirror is not SagaVectorMirrorKind.LegacyVirtualTable;

            if (!labelled)
            {
                continue;
            }

            await using (SqliteCommand owners = LabelCommand(staged, transaction, "SELECT DISTINCT SessionId", kind, artifactKey))
            {
                await using SqliteDataReader reader = await owners.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0))
                    {
                        _ = sessions.Add(CovenantIdentitySql.Key(reader.GetString(0)));
                    }
                }
            }

            await using SqliteCommand delete = LabelCommand(staged, transaction, "DELETE", kind, artifactKey);

            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (SagaRow row in sagaRows)
        {
            pairs += await SagaRetirementSuppression
                .CountPairAsync(staged, transaction, row.Scope, row.CampaignId, row.Content, cancellationToken)
                .ConfigureAwait(false);

            _ = await SagaRetirementSuppression
                .DeletePairAsync(staged, transaction, row.Scope, row.CampaignId, row.Content, cancellationToken)
                .ConfigureAwait(false);
        }

        await RecountSessionsAsync(staged, transaction, sessions, timeProvider, cancellationToken).ConfigureAwait(false);

        return new Purge(kind, removed, pairs, keys, sagaRows, rowIds, vectorMirrorsVerified);
    }

    /// <summary>
    /// Recounts each Session's tainted artifacts from the labels that remain, exactly as the erasure
    /// kernel does, over normalised spellings because an archive's are its source's.
    /// </summary>
    /// <remarks>
    /// Recounted, not folded to zero: the Session may own other labelled artifacts the erase did not touch,
    /// and a zero would tell it that it holds none.
    /// </remarks>
    private static async Task RecountSessionsAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sessions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (sessions.Count == 0
            || !await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "session_sensitivity_state", cancellationToken, transaction)
                .ConfigureAwait(false))
        {
            return;
        }

        await using SqliteCommand command = Command(
            staged,
            transaction,
            $"""
            UPDATE session_sensitivity_state
            SET TaintedArtifactCount = (
                    SELECT COUNT(*) FROM artifact_sensitivity WHERE {CovenantIdentitySql.Keyed("SessionId", "$session")}
                ),
                Revision = Revision + 1,
                UpdatedAtUtc = $now
            WHERE {CovenantIdentitySql.Keyed("SessionId", "$session")};
            """);

        SqliteParameter session = command.Parameters.Add("$session", SqliteType.Text);

        _ = command.Parameters.AddWithValue("$now", UtcInstantText.Format(timeProvider.GetUtcNow()));

        foreach (string key in sessions)
        {
            session.Value = key;

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes each matched Covenant entry through the shared entry plan in staged mode, its subject read
    /// immediately before its own delete because one entry's reclamation changes the next one's.
    /// </summary>
    private static async Task<Result<CovenantPurge>> PurgeCovenantAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> entryIds,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        long removed = 0;

        bool fullText = true;

        List<(CovenantEntryErasureSubject Subject, IReadOnlyList<Guid> VersionIds)> purged = [];

        foreach (Guid entryId in entryIds)
        {
            CovenantEntryErasureSubject? subject = await CovenantEntryErasurePlan
                .ReadSubjectAsync(staged, transaction, entryId, cancellationToken)
                .ConfigureAwait(false);

            if (subject is null)
            {
                return Refusal("a matched Covenant entry could not be read back");
            }

            CovenantEntryErasureTally tally = await CovenantEntryErasurePlan
                .RunAsync(staged, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Staged, cancellationToken)
                .ConfigureAwait(false);

            if (tally.KeyReclaimed != subject.ReclaimsKey)
            {
                return Refusal("a Covenant key's reclamation did not match the entry it was read from");
            }

            removed += tally.Targets.Single(static target => target.Table == CovenantEntries).Rows;

            fullText &= tally.FullTextSecureDeleteVerified;

            purged.Add((subject, tally.VersionIds));
        }

        if (purged.Count > 0
            && await BackupRestoreDatabaseWorker.TableExistsAsync(staged, "covenant_state", cancellationToken, transaction).ConfigureAwait(false))
        {
            // The applied search tuple described a projection that held what was just purged.
            await using SqliteCommand rebuild = Command(
                staged,
                transaction,
                """
                UPDATE covenant_state
                SET AppliedDatasetGeneration = NULL,
                    AppliedSearchSequence = NULL,
                    RebuildStateCode = $rebuild,
                    RebuildTargetSequence = NULL,
                    RebuildCursor = NULL,
                    UpdatedAtUtc = $now
                WHERE StateKey = 1;
                """);

            _ = rebuild.Parameters.AddWithValue("$rebuild", FullRebuildRequired);

            _ = rebuild.Parameters.AddWithValue("$now", UtcInstantText.Format(timeProvider.GetUtcNow()));

            _ = await rebuild.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new CovenantPurge(removed, fullText, purged);
    }

    /// <summary>
    /// Every post-condition, in the caller's transaction: no staged row matches any more, every purged
    /// identity counts zero in every target, the evidence is the destination's exactly, and a purged
    /// Covenant tier no longer claims a current search projection.
    /// </summary>
    /// <returns>The first post-condition that fails, named content-free; or null when every one holds.</returns>
    private static async Task<string?> FindResidueAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        MemoryErasureKey? key,
        MemoryErasureEvidenceSnapshot rows,
        Purge saga,
        Purge lexicon,
        CovenantPurge covenant,
        CancellationToken cancellationToken)
    {
        if (rows.Fingerprints.Count > 0)
        {
            Result<BackupRestoreErasureMatches> rescan = await FindMatchesAsync(staged, transaction, key!, rows, cancellationToken)
                .ConfigureAwait(false);

            if (rescan.IsFailure || !rescan.Value.IsEmpty)
            {
                return "a staged row still matches an erasure fingerprint";
            }
        }

        if (lexicon.RowIds.Count > 0
            && await BackupRestoreDatabaseWorker.TableExistsAsync(staged, "lexicon_fts", cancellationToken, transaction).ConfigureAwait(false)
            && await FullTextRowCountAsync(staged, transaction, lexicon.RowIds, cancellationToken).ConfigureAwait(false) != 0)
        {
            return "residual rows in lexicon_fts";
        }

        foreach (Purge purge in (Purge[])[saga, lexicon])
        {
            foreach (string artifactKey in purge.Keys)
            {
                CovenantArtifactPlanTally left = await CovenantArtifactPlanRunner
                    .RunAsync(staged, transaction, purge.Kind, artifactKey, CovenantArtifactPlanMode.Count, cancellationToken)
                    .ConfigureAwait(false);

                if (left.Targets.FirstOrDefault(static target => target.Rows != 0) is { } target)
                {
                    return $"residual rows in {target.Table}";
                }

                if (await BackupRestoreDatabaseWorker.TableExistsAsync(staged, "artifact_sensitivity", cancellationToken, transaction).ConfigureAwait(false))
                {
                    await using SqliteCommand labels = LabelCommand(staged, transaction, "SELECT COUNT(*)", purge.Kind, artifactKey);

                    if (await labels.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 0L)
                    {
                        return "residual rows in artifact_sensitivity";
                    }
                }
            }

            foreach (SagaRow row in purge.SagaRows)
            {
                if (await SagaRetirementSuppression
                        .CountPairAsync(staged, transaction, row.Scope, row.CampaignId, row.Content, cancellationToken)
                        .ConfigureAwait(false) != 0)
                {
                    return "residual rows in saga_retirement_suppressions";
                }
            }
        }

        foreach ((CovenantEntryErasureSubject subject, IReadOnlyList<Guid> versionIds) in covenant.Entries)
        {
            IReadOnlyList<MemoryErasureTableCount> remaining = await CovenantEntryErasurePlan
                .ProveAbsentAsync(staged, transaction, subject, versionIds, cancellationToken)
                .ConfigureAwait(false);

            if (remaining.Count > 0)
            {
                return "residual rows in " + string.Join(", ", remaining.Select(static table => table.Table));
            }
        }

        MemoryErasureEvidenceSnapshot evidence = await MemoryErasureEvidence
            .ReadSnapshotAsync(staged, transaction, cancellationToken)
            .ConfigureAwait(false) ?? MemoryErasureEvidenceSnapshot.Empty;

        if (!SameEvidence(evidence, rows))
        {
            return "the staged erasure evidence is not the destination's";
        }

        if (covenant.Entries.Count > 0
            && await BackupRestoreDatabaseWorker.TableExistsAsync(staged, "covenant_state", cancellationToken, transaction).ConfigureAwait(false))
        {
            await using SqliteCommand tuple = Command(
                staged,
                transaction,
                """
                SELECT COUNT(*) FROM covenant_state
                WHERE StateKey = 1
                  AND (AppliedDatasetGeneration IS NOT NULL OR AppliedSearchSequence IS NOT NULL OR RebuildStateCode <> $rebuild);
                """);

            _ = tuple.Parameters.AddWithValue("$rebuild", FullRebuildRequired);

            if (await tuple.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 0L)
            {
                return "the Covenant search projection still claims to be current";
            }
        }

        return null;
    }

    /// <summary>Whether two evidence sets hold exactly the same rows, column for column.</summary>
    private static bool SameEvidence(MemoryErasureEvidenceSnapshot left, MemoryErasureEvidenceSnapshot right) =>
        Rendered(left).SequenceEqual(Rendered(right), StringComparer.Ordinal);

    private static IEnumerable<string> Rendered(MemoryErasureEvidenceSnapshot snapshot) =>
        snapshot.Fingerprints
            .Select(static row => $"F|{Convert.ToHexString(row.Fingerprint)}|{(int)row.Store}|{Convert.ToHexString(row.KeyId)}")
            .Concat(snapshot.Receipts.Select(static row => string.Join(
                '|',
                "R",
                row.MutationId.ToString("D"),
                ((int)row.Store).ToString(CultureInfo.InvariantCulture),
                Convert.ToHexString(row.KeyId),
                Convert.ToHexString(row.RequestDigest),
                Convert.ToHexString(row.EffectDigest),
                row.ErasedItemCount.ToString(CultureInfo.InvariantCulture),
                row.RemovedRowCount.ToString(CultureInfo.InvariantCulture),
                row.RemovedLabelCount.ToString(CultureInfo.InvariantCulture),
                row.RemovedRetirementSuppressionCount.ToString(CultureInfo.InvariantCulture),
                ((int)row.Authorship).ToString(CultureInfo.InvariantCulture),
                ((int)row.Context).ToString(CultureInfo.InvariantCulture),
                ((int)row.Embedding).ToString(CultureInfo.InvariantCulture),
                ((int)row.Backup).ToString(CultureInfo.InvariantCulture),
                ((int)row.OtherExternal).ToString(CultureInfo.InvariantCulture),
                row.RetainedCopiesMask.ToString(CultureInfo.InvariantCulture),
                row.ScrubStateCode.ToString(CultureInfo.InvariantCulture),
                row.ScrubPendingReasonMask.ToString(CultureInfo.InvariantCulture))))
            .Concat(snapshot.Subjects.Select(static row => $"S|{row.MutationId:D}|{Convert.ToHexString(row.SubjectDigest)}"))
            .Order(StringComparer.Ordinal);

    /// <summary>A Saga memory's scope, Campaign and content, read before it is deleted, for its retirement pair.</summary>
    private static async Task<List<SagaRow>> ReadSagaRowsAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        string artifactKey,
        CancellationToken cancellationToken)
    {
        List<SagaRow> rows = [];

        await using SqliteCommand command = Command(
            staged,
            transaction,
            $"SELECT ScopeKindCode, CampaignId, Content FROM saga_memories WHERE {CovenantIdentitySql.Keyed("Id", "$id")};");

        _ = command.Parameters.AddWithValue("$id", artifactKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SagaRow(
                (SagaMemoryScopeKind)reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>A Lexicon entry's content rowid, which keys its full-text row, read before it is deleted.</summary>
    private static async Task<List<long>> ReadLexiconRowIdsAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        string artifactKey,
        CancellationToken cancellationToken)
    {
        List<long> rowIds = [];

        await using SqliteCommand command = Command(
            staged,
            transaction,
            $"SELECT rowid FROM lexicon_entries WHERE {CovenantIdentitySql.Keyed("Id", "$id")};");

        _ = command.Parameters.AddWithValue("$id", artifactKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rowIds.Add(reader.GetInt64(0));
        }

        return rowIds;
    }

    /// <summary>
    /// How many of these content rowids <c>lexicon_fts</c> still indexes. FTS5 keeps one
    /// <c>lexicon_fts_docsize</c> row for every row it indexes, keyed by the content rowid, so that row's
    /// absence is the exact proof the live Lexicon erase rests on too.
    /// </summary>
    /// <remarks>
    /// Counted in chunks, because each rowid is one bound parameter and one statement binds a bounded
    /// number: an archive that purged more entries than that would otherwise make the proof throw and the
    /// restore refuse a purge that was fine. The chunks are disjoint and every one is read, so the sum is
    /// the count a single statement would give, and a survivor in any chunk still fails the proof.
    /// </remarks>
    internal static async Task<long> FullTextRowCountAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        IReadOnlyList<long> rowIds,
        CancellationToken cancellationToken)
    {
        long indexed = 0;

        for (int start = 0; start < rowIds.Count; start += FullTextCountChunkSize)
        {
            int length = Math.Min(FullTextCountChunkSize, rowIds.Count - start);

            await using SqliteCommand command = Command(staged, transaction, string.Empty);

            List<string> parameters = new(length);

            for (int offset = 0; offset < length; offset++)
            {
                string name = $"$r{offset}";

                parameters.Add(name);

                _ = command.Parameters.AddWithValue(name, rowIds[start + offset]);
            }

            command.CommandText = $"SELECT COUNT(*) FROM lexicon_fts_docsize WHERE id IN ({string.Join(", ", parameters)});";

            indexed = checked(
                indexed
                + (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long rows
                    ? rows
                    : throw new InvalidDataException("The full-text row count did not return an integer.")));
        }

        return indexed;
    }

    /// <summary>One statement over an artifact's labels, matched by kind and normalised artifact identity.</summary>
    private static SqliteCommand LabelCommand(
        SqliteConnection staged,
        SqliteTransaction transaction,
        string verb,
        SensitiveArtifactKind kind,
        string artifactKey)
    {
        SqliteCommand command = Command(
            staged,
            transaction,
            $"{verb} FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND {CovenantIdentitySql.Keyed("ArtifactId", "$artifact")};");

        _ = command.Parameters.AddWithValue("$kind", (long)kind);

        _ = command.Parameters.AddWithValue("$artifact", artifactKey);

        return command;
    }

    private static HashSet<string> Fingerprints(MemoryErasureEvidenceSnapshot destination, MemoryReviewStore store) =>
        [.. destination.Fingerprints.Where(row => row.Store == store).Select(static row => Convert.ToHexString(row.Fingerprint))];

    private static bool Matches(HashSet<string> fingerprints, MemoryErasureKey key, MemoryErasureIdentity identity) =>
        fingerprints.Contains(Convert.ToHexString(key.Fingerprint(identity)));

    private static Error Refusal(string diagnostics) =>
        new(BackupRestoreErasureCodes.VerificationFailed, StagedProofFailed + diagnostics + ".");

    private static Result<BackupRestoreErasureMatches> Unnameable(string what) =>
        Refusal(what + " cannot be read as an identity an erasure fingerprint can name");

    private static async Task<Result<BackupRestoreErasureApplicationReceipt>> RefuseAsync(
        SqliteTransaction transaction,
        string diagnostics)
    {
        await RollbackAsync(transaction).ConfigureAwait(false);

        return Refusal(diagnostics);
    }

    /// <summary>
    /// Rolls the transaction back on no token, as compensation. A transaction SQLite already ended is
    /// left to its disposal; either way nothing the step wrote survives.
    /// </summary>
    private static async Task RollbackAsync(SqliteTransaction transaction)
    {
        if (transaction.Connection is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return command;
    }

    /// <summary>One Saga memory's identity parts, as its retirement pair is computed from them.</summary>
    private sealed record SagaRow(SagaMemoryScopeKind Scope, string? CampaignId, string Content);

    /// <summary>What one store's artifact purge removed, and what its post-conditions look for.</summary>
    /// <param name="RowIds">The purged Lexicon entries' content rowids, which key their full-text rows.</param>
    private sealed record Purge(
        SensitiveArtifactKind Kind,
        long Removed,
        long RetirementPairs,
        IReadOnlyList<string> Keys,
        IReadOnlyList<SagaRow> SagaRows,
        IReadOnlyList<long> RowIds,
        bool VectorMirrorsVerified);

    /// <summary>What the Covenant purge removed, and each entry its absence proof needs.</summary>
    private sealed record CovenantPurge(
        long Removed,
        bool FullTextVerified,
        IReadOnlyList<(CovenantEntryErasureSubject Subject, IReadOnlyList<Guid> VersionIds)> Entries);
}
