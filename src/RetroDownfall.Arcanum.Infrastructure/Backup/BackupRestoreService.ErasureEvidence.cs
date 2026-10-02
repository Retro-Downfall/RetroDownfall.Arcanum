using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The restore's read of this installation's erasure evidence: what was erased here must not come back
/// with an archive restored over it.
/// </summary>
internal sealed partial class BackupRestoreService
{
    private const string NewProfileRootErasureWarning =
        "This installation's erasure evidence is not applied to a new profile root; items erased here can "
        + "reappear there.";

    private const string NewProfileRootUndeterminedErasureWarning =
        "Whether this installation holds an erasure key could not be determined; its erasure evidence is not "
        + "applied to a new profile root, so items erased here can reappear there.";

    /// <summary>
    /// Reads the destination's fingerprints, receipts and receipt subjects as values, and proves them
    /// against the erasure key.
    /// </summary>
    /// <remarks>
    /// <para>The Grimoire is read first, by <see cref="ReadDestinationErasureRowsAsync"/>, which owns the
    /// handle and has closed it before it returns. So a destination that never erased anything is
    /// planned without a single credential call, and no keychain call can run inside the snapshot. The
    /// key is consulted only when the Grimoire holds rows, or when it cannot say whether it does.</para>
    ///
    /// <para>The key is the anchor. With rows, only the key that recorded every one of them proves them; a
    /// missing or replaced key, or a keychain that cannot answer, refuses. Without a readable Grimoire,
    /// only a keychain that proves the key absent proves there is nothing to lose, because evidence is
    /// committed only under a key; anything else refuses. No branch reads an unanswerable question as
    /// "nothing was erased".</para>
    ///
    /// <para>The key is re-probed unless it is already latched Present. A Present latch is kept for the
    /// life of the process, so the key a plan proved is the key a later read in the same restore holds.
    /// The key is always disposed here; only its identifier travels on.</para>
    /// </remarks>
    private async Task<BackupRestoreErasureEvidence> ReadDestinationErasureEvidenceAsync(
        CancellationToken cancellationToken) =>
        await ReadDestinationErasureRowsAsync(cancellationToken).ConfigureAwait(false) is { } rows
            ? ProveRows(rows)
            : ProveNothingCommitted();

    /// <summary>
    /// The destination's evidence rows, read in one <c>BEGIN DEFERRED</c> on a read-only handle that is
    /// closed before this returns.
    /// </summary>
    /// <returns>
    /// The rows, which are empty for a catalog that cannot hold evidence; or null when the Grimoire is
    /// absent or cannot be read.
    /// </returns>
    private async Task<MemoryErasureEvidenceSnapshot?> ReadDestinationErasureRowsAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            return null;
        }

        try
        {
            SecretStoreReadResult secret = await _secretStore
                .GetGrimoireEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);

            if (secret.Status != SecretStoreReadStatus.Ok || string.IsNullOrEmpty(secret.Value))
            {
                return null;
            }

            try
            {
                await using SqliteConnection connection = await BackupRestoreDatabaseWorker
                    .OpenAsync(_paths.DatabasePath, secret.Value, readOnly: true, cancellationToken)
                    .ConfigureAwait(false);

                _options.DestinationEvidenceHandleForTests?.Invoke(true);

                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

                return await MemoryErasureEvidence
                    .ReadSnapshotAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false)
                    ?? MemoryErasureEvidenceSnapshot.Empty;
            }
            finally
            {
                // After the transaction and the connection are disposed, at the end of the block above.
                _options.DestinationEvidenceHandleForTests?.Invoke(false);
            }
        }
        // The same list as the Campaign read: opening the destination starts at its key-derivation
        // sidecar, so a missing, malformed or unsupported sidecar is "this Grimoire cannot answer" too.
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or FormatException
                or System.Text.Json.JsonException
                or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>A readable Grimoire's rows, proven by the key that recorded them or refused.</summary>
    /// <remarks>
    /// A receipt with no fingerprint is still evidence: release removes a fingerprint and keeps the
    /// record of the erasure.
    /// </remarks>
    private BackupRestoreErasureEvidence ProveRows(MemoryErasureEvidenceSnapshot rows)
    {
        if (!rows.HasRows)
        {
            return BackupRestoreErasureEvidence.None;
        }

        MemoryErasureKeyOpenResult opened = _erasureKeys.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        using MemoryErasureKey? key = opened.Key;

        return opened.State switch
        {
            MemoryErasureKeyState.Present when key is not null && RecordedBy(rows, key) =>
                BackupRestoreErasureEvidence.Present(key.KeyId.ToArray(), rows),
            MemoryErasureKeyState.Present or MemoryErasureKeyState.Absent =>
                BackupRestoreErasureEvidence.Refused(BackupRestoreErasureCodes.KeyMissing, rows),
            _ => BackupRestoreErasureEvidence.Refused(BackupRestoreErasureCodes.KeyUnavailable, rows, opened.State),
        };
    }

    /// <summary>
    /// A Grimoire that is absent or cannot be read: only a keychain that proves the key absent proves no
    /// evidence was ever committed.
    /// </summary>
    private BackupRestoreErasureEvidence ProveNothingCommitted()
    {
        MemoryErasureKeyOpenResult opened = _erasureKeys.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        opened.Key?.Dispose();

        return opened.State is MemoryErasureKeyState.Absent
            ? BackupRestoreErasureEvidence.None
            : BackupRestoreErasureEvidence.Refused(BackupRestoreErasureCodes.EvidenceUnavailable, null);
    }

    /// <summary>
    /// What a new-profile-root plan says about erasures it does not apply: a warning when this
    /// installation holds an erasure key, which it has once anything was erased here; a different one
    /// when the keychain cannot say whether it does; nothing when the key is proven absent.
    /// </summary>
    private string? NewProfileRootErasureWarningFor()
    {
        MemoryErasureKeyOpenResult opened = _erasureKeys.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        opened.Key?.Dispose();

        return opened.State switch
        {
            MemoryErasureKeyState.Present => NewProfileRootErasureWarning,
            MemoryErasureKeyState.Absent => null,
            _ => NewProfileRootUndeterminedErasureWarning,
        };
    }

    /// <summary>The issue a post-commit match adds to the reconciliation, which makes it require an operator.</summary>
    private const string CommittedMatchIssue =
        BackupRestoreErasureCodes.VerificationFailed + ": an erased item is present in the committed generation. "
        + "It stays retrievable until it is removed: erase it again with its store's erase command, or run the same "
        + "restore again, which removes it in staging.";

    /// <summary>
    /// The issue the post-commit proof adds when it could not run to an answer: no key in hand, or a committed
    /// row whose identity it cannot read. That is not a presence, and it says so.
    /// </summary>
    private const string CommittedUnprovenIssue =
        BackupRestoreErasureCodes.VerificationFailed + ": the committed generation could not be proven free of the "
        + "items this installation erased. With the erasure key readable, run the same restore again, which proves "
        + "it in staging, or erase any item that reappears with its store's erase command.";

    /// <summary>
    /// Applies this installation's erasure evidence to the staged generation in one <c>BEGIN IMMEDIATE</c>,
    /// commits it, and truncates the staged write-ahead log (§10.19.9).
    /// </summary>
    /// <remarks>
    /// <para>Runs for every replace-installation restore with a staged Grimoire, with the Covenant gate on or
    /// off, after the drain and before the Covenant arm, so what the arm then preserves or purges is already
    /// free of everything this installation erased.</para>
    ///
    /// <para>The key is the one this restore's destination read already latched in this provider, so taking
    /// a copy is no keychain I/O and happens before <c>BEGIN</c>; a latched key that is not the one that
    /// recorded the evidence refuses. The transaction runs under family maintenance and the retention-purge
    /// authorization, never the entry-erasure kind, which an older staged canonical tier does not know.</para>
    /// </remarks>
    private async Task<Result<BackupRestoreErasureApplicationReceipt>> ReconcileStagedMemoryEvidenceAsync(
        SqliteConnection staged,
        BackupRestoreErasureEvidence destination,
        IReadOnlyList<CovenantDisclosureState> destinationDisclosure,
        CancellationToken cancellationToken)
    {
        bool present = destination.Kind is BackupRestoreErasureEvidenceKind.Present;

        using MemoryErasureKey? key = present ? _erasureKeys.TryCopyLatched() : null;

        if (present && (key is null || !key.HasKeyId(destination.KeyId)))
        {
            BackupVerifyIssue missing = BackupRestoreErasureEvidence.Refused(BackupRestoreErasureCodes.KeyMissing, null).Refusal!;

            return new Error(missing.Code, missing.Message);
        }

        Result<BackupRestoreErasureApplicationReceipt> applied;

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(staged, CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance))
        using (CovenantSqliteConnectionInitializer.Instance.Authorize(staged, CovenantSqliteAuthorizationKind.SensitivityRetentionPurge))
        {
            await using SqliteTransaction transaction = staged.BeginTransaction(deferred: false);

            applied = await BackupRestoreErasureEvidenceApplier
                .ApplyAsync(
                    staged,
                    transaction,
                    destination,
                    key,
                    destinationDisclosure,
                    _timeProvider,
                    _options.AfterErasurePurgeForTests,
                    cancellationToken)
                .ConfigureAwait(false);

            if (applied.IsFailure)
            {
                return applied.Error;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // After the commit the purge is proven, so a checkpoint that fails is a scrub that is not, never a
        // restore that is: it is reported as pending, exactly as a busy one is.
        bool truncated;

        try
        {
            Result<CovenantWalCheckpointOutcome> checkpoint = _options.StagedCheckpointForTests is { } seam
                ? await seam(staged, cancellationToken).ConfigureAwait(false)
                : await GrimoireWalCheckpoint.TruncateAsync(staged, cancellationToken).ConfigureAwait(false);

            truncated = checkpoint.IsSuccess && checkpoint.Value.IsTruncated;
        }
        catch (SqliteException)
        {
            truncated = false;
        }

        return applied.Value with { CheckpointTruncated = truncated };
    }

    /// <summary>
    /// Deletes the archive's extracted database, and its write-ahead log and shared memory, the moment the
    /// staged tree no longer needs them, and checks that none of the three remains.
    /// </summary>
    /// <remarks>
    /// The extraction holds the archive exactly as it was, erased items included, and nothing after the
    /// staged tree is composed reads it; only the recovery material beside it is still read, after commit.
    /// A file that cannot be removed is not a refusal: the result's scrub status reports it instead.
    /// </remarks>
    /// <returns>Whether all three files are gone.</returns>
    private static bool DeleteExtractedDatabase(string extractRoot)
    {
        string database = Path.Combine(
            extractRoot,
            BackupArchivePaths.GrimoireDatabase.Replace('/', Path.DirectorySeparatorChar));

        string[] files = [database, database + "-wal", database + "-shm"];

        foreach (string file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return files.All(static file => !File.Exists(file));
    }

    /// <summary>
    /// Proves the committed generation holds nothing this installation erased, on a connection the caller
    /// already has open, with the key this restore latched.
    /// </summary>
    /// <remarks>
    /// Total: every way it can fail to answer is an answer. A key no longer in hand or not the evidence's,
    /// a committed identity the match cannot read, or a match that throws all report that absence could not
    /// be proven, so a restore that has already committed is never left by an exception.
    /// </remarks>
    /// <returns>Null when the committed generation is proven clean; otherwise the issue that says why not.</returns>
    private async Task<string?> ProveCommittedAbsenceAsync(
        SqliteConnection committed,
        BackupRestoreErasureEvidence destination,
        CancellationToken cancellationToken)
    {
        using MemoryErasureKey? key = _erasureKeys.TryCopyLatched();

        if (key is null || !key.HasKeyId(destination.KeyId))
        {
            return CommittedUnprovenIssue;
        }

        Result<BackupRestoreErasureMatches> matches;

        try
        {
            matches = await BackupRestoreErasureEvidenceApplier
                .FindMatchesAsync(committed, null, key, destination.Rows, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or InvalidDataException
                or FormatException)
        {
            return CommittedUnprovenIssue;
        }

        return matches.IsFailure
            ? CommittedUnprovenIssue
            : matches.Value.IsEmpty ? null : CommittedMatchIssue;
    }

    /// <summary>What a restore reports it did with the destination's erasure evidence, scrub status included.</summary>
    /// <param name="committedClean">Whether the post-commit proof held, or had nothing to prove.</param>
    private static BackupRestoreErasureApplication ErasureApplication(StagedErasure erasure, bool committedClean)
    {
        BackupRestoreErasureApplicationReceipt receipt = erasure.Receipt;

        BackupRestoreErasureScrubStatus scrub = !receipt.Touched
            ? BackupRestoreErasureScrubStatus.NotApplicable
            : receipt.FullTextVerified
                && receipt.VectorMirrorsVerified
                && receipt.CheckpointTruncated
                && erasure.ExtractedDatabaseDeleted
                && committedClean
                ? BackupRestoreErasureScrubStatus.Verified
                : BackupRestoreErasureScrubStatus.ScrubPending;

        return new BackupRestoreErasureApplication(
            receipt.SagaMemoriesRemoved,
            receipt.LexiconEntriesRemoved,
            receipt.CovenantEntriesRemoved,
            receipt.RetirementPairsRemoved,
            receipt.FingerprintsJoined,
            receipt.ReceiptsJoined,
            receipt.ArchiveRowsDropped,
            scrub);
    }

    private static bool RecordedBy(MemoryErasureEvidenceSnapshot rows, MemoryErasureKey key) =>
        rows.Fingerprints.All(row => key.HasKeyId(row.KeyId))
        && rows.Receipts.All(row => key.HasKeyId(row.KeyId));

    /// <summary>
    /// The staged evidence step as the post-commit reconciliation needs it: the destination read it applied,
    /// its receipt, and whether the extracted archive database was removed.
    /// </summary>
    private sealed record StagedErasure(
        BackupRestoreErasureEvidence Destination,
        BackupRestoreErasureApplicationReceipt Receipt,
        bool ExtractedDatabaseDeleted);
}
