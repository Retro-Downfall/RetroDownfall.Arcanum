using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The destination facts a staged generation has to be reconciled against.
/// </summary>
/// <remarks>
/// Read from the live installation before anything is displaced, and passed as values rather than as
/// a second open connection: the reconciliation runs inside the staged core transaction, and a
/// destination handle held open across it would be a second writer to a database this operation has
/// already closed admission on.
///
/// <para>An absent authority row is a real answer — a destination that has none is a machine with no
/// prior Covenant lineage — and it is represented as <see langword="null"/> rather than as a synthetic
/// clean row, so the join can tell "this machine was clean" from "this machine had no history".</para>
/// </remarks>
internal sealed record BackupCovenantRestoreDestinationState(
    CovenantAuthorityStateRow? Authority,
    IReadOnlyList<CovenantDisclosureState> DisclosureBuckets)
{

    /// <summary>What a destination with no readable Covenant state contributes to the join.</summary>
    internal static BackupCovenantRestoreDestinationState None { get; } = new(null, []);

    /// <summary>
    /// Reads the authority row and every disclosure bucket the destination can still prove.
    /// </summary>
    /// <remarks>
    /// Missing tables are absence rather than failure. A destination that predates the Covenant
    /// tables genuinely has nothing to carry forward, and refusing the restore over it would make an
    /// installation unable to adopt a generation it is entitled to.
    /// </remarks>
    internal static async Task<BackupCovenantRestoreDestinationState> ReadAsync(
        SqliteConnection destination,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(destination);

        CovenantAuthorityStateRow? authority =
            await BackupRestoreDatabaseWorker.TableExistsAsync(
                    destination,
                    "covenant_authority_state",
                    cancellationToken).ConfigureAwait(false)
                ? await CovenantAuthorityStateJoiner
                    .ReadAsync(destination, transaction: null, cancellationToken)
                    .ConfigureAwait(false)
                : null;

        List<CovenantDisclosureState> buckets =
            await BackupRestoreDatabaseWorker.TableExistsAsync(
                    destination,
                    "external_disclosure_state",
                    cancellationToken).ConfigureAwait(false)
                ? await CovenantDisclosureStateJoiner
                    .ReadAllAsync(destination, transaction: null, cancellationToken)
                    .ConfigureAwait(false)
                : [];

        return new BackupCovenantRestoreDestinationState(authority, buckets);

    }

}

/// <summary>
/// What one staged reconciliation changed, in counts and identities and nothing else.
/// </summary>
internal sealed record BackupCovenantRestoreReconciliationReceipt(
    bool CanonicalTierPresent,
    Guid StagedDatasetGeneration,
    long AcceleratorEpoch,
    long EnvelopeKeyEpoch,
    ulong ClearedOutboxRows,
    ulong RetainedLabels,
    ulong UnresolvedCampaignPaths,
    ulong TerminalizedTurnClaims,
    int JoinedDisclosureBuckets,
    CovenantHostToolsState HostToolsState);

/// <summary>
/// Converts a staged snapshot of somebody else's installation into one this machine may adopt.
/// </summary>
/// <remarks>
/// Everything here runs inside the caller's staged core transaction, against a candidate that has
/// never been published as live, and <em>after</em>
/// <see cref="RestoreStagingManagedAuthoritySanitizationCapability"/> has stripped managed-file
/// authority. The order is the safety property: this phase refuses outright if either authority table
/// still holds a row, because everything below it — reopening the candidate, registering a
/// destination opener, validating the staged tree — would then be running beside a delete capability
/// for a path this installation never owned (§10.19.9).
///
/// <para>Nothing here touches the filesystem, and nothing here reads or writes the live installation.
/// The destination's contribution arrives as <see cref="BackupCovenantRestoreDestinationState"/>
/// values captured before the phase began.</para>
/// </remarks>
internal static class BackupCovenantRestoreReconciler
{

    /// <summary>
    /// The parameter payload every claim this phase terminalizes carries.
    /// </summary>
    /// <remarks>
    /// A fixed domain string rather than anything from the turn. The column is required and bound by
    /// its own digest, and the turn it describes ran on a different installation — so the only honest
    /// content is the identity of the transition that ended it.
    /// </remarks>
    internal const string TurnClaimTerminalizationDomain =
        "Arcanum.BackupRestore.TurnClaimTerminalization.v1";

    /// <summary>The status a replay of a restore-interrupted claim answers with.</summary>
    internal const int TurnClaimTerminalStatus = 409;

    private const int RestoredInterruptedStateCode = 6;

    private const int FullRebuildRequiredCode = (int)CovenantFtsRebuildState.FullRebuildRequired;

    private const long EpochCeiling = long.MaxValue;

    /// <summary>
    /// Reissues this generation's identities, joins the destination's authority and disclosure
    /// evidence into it, and retires everything the source machine left in flight.
    /// </summary>
    internal static async Task<Result<BackupCovenantRestoreReconciliationReceipt>> ReconcileStagedAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        BackupCovenantRestoreDestinationState destination,
        CovenantSqliteConnectionInitializer initializer,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {

        if (staged is null
            || transaction is null
            || destination is null
            || initializer is null
            || timeProvider is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A staged Covenant reconciliation requires its connection, transaction, and destination state.");

        }

        // Before anything is rewritten. A generation that still holds managed-file authority is a
        // generation this installation must not open a destination opener beside, so the refusal has
        // to come first rather than after the identities have already been reissued.
        Result absent = await RequireAbsentSourceAuthorityAsync(staged, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (absent.IsFailure)
        {

            return absent.Error;

        }

        Result<ulong> labels = await ValidateRemainingLabelsAsync(staged, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (labels.IsFailure)
        {

            return labels.Error;

        }

        Result<CovenantHostToolsState> authority = await JoinAuthorityAsync(
            staged,
            transaction,
            destination.Authority,
            timeProvider,
            cancellationToken).ConfigureAwait(false);

        if (authority.IsFailure)
        {

            return authority.Error;

        }

        Result<int> disclosure = await JoinDisclosureAsync(
            staged,
            transaction,
            destination.DisclosureBuckets,
            timeProvider,
            cancellationToken).ConfigureAwait(false);

        if (disclosure.IsFailure)
        {

            return disclosure.Error;

        }

        Result<CanonicalReissue> canonical = await ReissueCanonicalIdentitiesAsync(
            staged,
            transaction,
            initializer,
            timeProvider,
            cancellationToken).ConfigureAwait(false);

        if (canonical.IsFailure)
        {

            return canonical.Error;

        }

        ulong unresolved = await UnresolveCampaignPathsAsync(staged, transaction, cancellationToken)
            .ConfigureAwait(false);

        ulong claims = await TerminalizeTurnClaimsAsync(
            staged,
            transaction,
            timeProvider,
            cancellationToken).ConfigureAwait(false);

        return new BackupCovenantRestoreReconciliationReceipt(
            canonical.Value.Present,
            canonical.Value.DatasetGeneration,
            canonical.Value.AcceleratorEpoch,
            canonical.Value.EnvelopeKeyEpoch,
            canonical.Value.ClearedOutboxRows,
            labels.Value,
            unresolved,
            claims,
            disclosure.Value,
            authority.Value);

    }

    /// <summary>
    /// Refuses a staged generation that still carries managed-file authority of any kind.
    /// </summary>
    /// <remarks>
    /// This is the ordering rule of §10.19.4 expressed as a precondition rather than as a comment. The
    /// sanitation capability is the only thing that can empty these tables, and a reconciliation that
    /// ran without it would leave the rows in place for the swap to publish as live authority.
    /// </remarks>
    private static async Task<Result> RequireAbsentSourceAuthorityAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        foreach (string table in (string[])["managed_file_write_intents", "local_erasure_work_items"])
        {

            if (!await BackupRestoreDatabaseWorker
                    .TableExistsAsync(staged, table, cancellationToken, transaction)
                    .ConfigureAwait(false))
            {

                continue;

            }

            if (await CountAsync(staged, transaction, $"SELECT COUNT(*) FROM {table};", cancellationToken)
                    .ConfigureAwait(false) != 0)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.ForbiddenAuthority,
                        "The staged generation still holds managed-file authority, so it cannot be "
                        + "validated or opened against this installation."));

            }

        }

        return Result.Success();

    }

    /// <summary>
    /// Counts the labels this generation keeps, and refuses one the sanitation receipt says is gone.
    /// </summary>
    /// <remarks>
    /// A tombstone recording <c>ExactLabelRemoved</c> is a claim about <em>this</em> database, not
    /// about a moment that has since passed. A label that is present anyway means either the archive
    /// was replayed over a stripped generation or the removal did not hold, and both make the
    /// tombstone's own evidence false.
    /// </remarks>
    private static async Task<Result<ulong>> ValidateRemainingLabelsAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "artifact_sensitivity", cancellationToken, transaction)
                .ConfigureAwait(false))
        {

            return 0UL;

        }

        if (await BackupRestoreDatabaseWorker
                .TableExistsAsync(
                    staged,
                    "restored_managed_file_authority_tombstones",
                    cancellationToken,
                    transaction)
                .ConfigureAwait(false))
        {

            long revived = await CountAsync(
                staged,
                transaction,
                """
                SELECT COUNT(*) FROM artifact_sensitivity label
                WHERE EXISTS (
                    SELECT 1 FROM restored_managed_file_authority_tombstones tombstone
                    WHERE tombstone.SensitivityLabelId = label.LabelId
                      AND tombstone.LabelDispositionCode = 2);
                """,
                cancellationToken).ConfigureAwait(false);

            if (revived != 0)
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A sensitivity label this restore recorded as removed is still present in staging.");

            }

        }

        return checked((ulong)await CountAsync(
            staged,
            transaction,
            "SELECT COUNT(*) FROM artifact_sensitivity;",
            cancellationToken).ConfigureAwait(false));

    }

    /// <summary>
    /// Writes the destination-monotonic authority row the staged generation becomes live under.
    /// </summary>
    private static async Task<Result<CovenantHostToolsState>> JoinAuthorityAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        CovenantAuthorityStateRow? destination,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "covenant_authority_state", cancellationToken, transaction)
                .ConfigureAwait(false))
        {

            return CovenantHostToolsState.Clean;

        }

        CovenantAuthorityStateRow? source = await CovenantAuthorityStateJoiner
            .ReadAsync(staged, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The staged generation carries the authority table without the row it exists to hold.");

        }

        // A destination with no row of its own contributes a clean lineage at epoch zero. It is not a
        // laundering path: zero loses every maximum, so the archive's taint and epoch both survive.
        CovenantAuthorityStateRow effective = destination
            ?? new CovenantAuthorityStateRow(
                source.InstallationIdentity,
                0,
                CovenantHostToolsState.Clean,
                null,
                null,
                null);

        Result<CovenantAuthorityStateRow> joined = CovenantAuthorityStateJoiner.Join(
            effective,
            source,
            effective.InstallationIdentity);

        if (joined.IsFailure)
        {

            return joined.Error;

        }

        await using SqliteCommand command = staged.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE covenant_authority_state
            SET InstallationIdentity = $identity,
                AuthorityEpoch = $epoch,
                HostToolsStateCode = $hostTools,
                TaintTimeMasterVersion = $taintVersion,
                TaintFingerprint = $taintFingerprint,
                TransitionId = $transition,
                UpdatedAtUtc = $now
            WHERE StateKey = 1;
            """;

        _ = command.Parameters.AddWithValue("$identity", joined.Value.InstallationIdentity);

        _ = command.Parameters.AddWithValue("$epoch", joined.Value.AuthorityEpoch);

        _ = command.Parameters.AddWithValue("$hostTools", (int)joined.Value.HostToolsState);

        _ = command.Parameters.AddWithValue(
            "$taintVersion",
            joined.Value.TaintTimeMasterVersion is { } version ? version : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$taintFingerprint",
            joined.Value.TaintFingerprint is { } fingerprint ? fingerprint : (object)DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$transition",
            joined.Value.TransitionId is { } transition ? transition : (object)DBNull.Value);

        _ = command.Parameters.AddWithValue("$now", Timestamp(timeProvider));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return joined.Value.HostToolsState;

    }

    private static async Task<Result<int>> JoinDisclosureAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        IReadOnlyList<CovenantDisclosureState> destinationBuckets,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {

        if (destinationBuckets.Count == 0
            || !await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "external_disclosure_state", cancellationToken, transaction)
                .ConfigureAwait(false))
        {

            return 0;

        }

        return await CovenantDisclosureStateJoiner.JoinIntoStagedAsync(
            staged,
            transaction,
            destinationBuckets,
            timeProvider,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Stamps a new dataset generation, advances both epochs, and discards every archived projection.
    /// </summary>
    /// <remarks>
    /// The generation is the identity every envelope, cursor, and turn snapshot binds, so a restored
    /// dataset that kept the archive's generation would let a token minted on the source machine
    /// validate here. The two epochs advance for the mirror-image reason: they are outside the
    /// generation's reset exception precisely so a captured epoch cannot survive the dataset it was
    /// captured against.
    ///
    /// <para>The applied FTS tuple is nulled and the rebuild state set to
    /// <see cref="CovenantFtsRebuildState.FullRebuildRequired"/> rather than trusted. An accelerator
    /// projection is a cache of canonical rows under a generation that no longer exists, and the
    /// archive is in no position to say whether it was current when the snapshot was taken. The
    /// projection rows themselves are deliberately left where they are: only the outbox worker and the
    /// rebuilder write accelerator state, and a null applied tuple already makes those rows unusable —
    /// the worker refuses a nonempty projection it never published a tuple for, and the rebuilder
    /// clears the whole table before its first batch.</para>
    /// </remarks>
    private static async Task<Result<CanonicalReissue>> ReissueCanonicalIdentitiesAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        CovenantSqliteConnectionInitializer initializer,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "covenant_state", cancellationToken, transaction)
                .ConfigureAwait(false))
        {

            return CanonicalReissue.Absent;

        }

        long accelerator;

        long envelope;

        await using (SqliteCommand read = staged.CreateCommand())
        {

            read.Transaction = transaction;

            read.CommandText = """
                SELECT AcceleratorEpoch, EnvelopeKeyEpoch FROM covenant_state WHERE StateKey = 1;
                """;

            await using SqliteDataReader reader = await read
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The staged canonical tier is installed without the singleton it exists to hold.");

            }

            accelerator = reader.GetInt64(0);

            envelope = reader.GetInt64(1);

        }

        if (accelerator >= EpochCeiling || envelope >= EpochCeiling)
        {

            // A saturated epoch cannot advance, and the schema refuses to change it at all. Adopting
            // the generation anyway would publish a dataset whose epochs no longer separate it from
            // the one it replaced.
            return new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                "A staged Covenant epoch is saturated and cannot be advanced for this restore.");

        }

        Guid generation = Guid.NewGuid();

        // The outbox is the accelerator's work queue for a dataset that is about to stop existing.
        // Draining it is family maintenance, which is the one authorization its delete guard accepts
        // besides the synchronization worker's.
        using CovenantSqliteAuthorizationScope scope = initializer.Authorize(
            staged,
            CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);

        long outbox = await ExecuteAsync(
            staged,
            transaction,
            "DELETE FROM covenant_search_outbox;",
            cancellationToken).ConfigureAwait(false);

        await using SqliteCommand update = staged.CreateCommand();

        update.Transaction = transaction;

        update.CommandText = """
            UPDATE covenant_state
            SET DatasetGeneration = $generation,
                CanonicalSearchSequence = 0,
                AppliedDatasetGeneration = NULL,
                AppliedSearchSequence = NULL,
                AcceleratorEpoch = $accelerator,
                EnvelopeKeyEpoch = $envelope,
                NextSearchRowId = 1,
                RebuildStateCode = $rebuild,
                RebuildTargetSequence = NULL,
                RebuildCursor = NULL,
                UpdatedAtUtc = $now
            WHERE StateKey = 1;
            """;

        _ = update.Parameters.AddWithValue("$generation", CanonicalGenerationBytes(generation));

        _ = update.Parameters.AddWithValue("$accelerator", checked(accelerator + 1));

        _ = update.Parameters.AddWithValue("$envelope", checked(envelope + 1));

        _ = update.Parameters.AddWithValue("$rebuild", FullRebuildRequiredCode);

        _ = update.Parameters.AddWithValue("$now", Timestamp(timeProvider));

        _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new CanonicalReissue(
            true,
            generation,
            checked(accelerator + 1),
            checked(envelope + 1),
            checked((ulong)outbox));

    }

    /// <summary>
    /// Drops every resolved Campaign root the archive carried.
    /// </summary>
    /// <remarks>
    /// A row here is the keyed identity of a directory on the machine the backup was taken from. It
    /// grants nothing on its own, but keeping it would let this installation believe a Campaign is
    /// already bound to a root it has never opened — and the marker that would have proved the
    /// binding is not restored either. Every Campaign therefore comes back unresolved and requires a
    /// fresh registration (§10.12).
    /// </remarks>
    private static async Task<ulong> UnresolveCampaignPathsAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "campaign_path_identities", cancellationToken, transaction)
                .ConfigureAwait(false))
        {

            return 0;

        }

        return checked((ulong)await ExecuteAsync(
            staged,
            transaction,
            "DELETE FROM campaign_path_identities;",
            cancellationToken).ConfigureAwait(false));

    }

    /// <summary>
    /// Retires every turn the source machine was still running.
    /// </summary>
    /// <remarks>
    /// A pending or begun claim names an executor, a lease, and a boot identity that all died with
    /// the installation the archive came from. Leaving one live would hold its Session busy against a
    /// turn nothing can finish, and adopting its lease would be this machine claiming to be the owner
    /// that made the reservation. <c>RestoredInterrupted</c> is the state the schema keeps for exactly
    /// this: terminal, with no executable lease or checkpoint authority at all.
    /// </remarks>
    private static async Task<ulong> TerminalizeTurnClaimsAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "session_turn_claims", cancellationToken, transaction)
                .ConfigureAwait(false))
        {

            return 0;

        }

        byte[] parameters = Encoding.ASCII.GetBytes(TurnClaimTerminalizationDomain);

        await using SqliteCommand command = staged.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE session_turn_claims
            SET StateCode = $state,
                ExecutorId = NULL,
                LeaseDeadlineUtc = NULL,
                TerminalErrorCode = $code,
                TerminalHttpStatus = $status,
                TerminalParameterBytes = $parameters,
                TerminalParameterDigest = $digest,
                TerminalAtUtc = $now
            WHERE StateCode IN (1, 2);
            """;

        _ = command.Parameters.AddWithValue("$state", RestoredInterruptedStateCode);

        _ = command.Parameters.AddWithValue(
            "$code",
            ErrorCodes.Hub.SessionTurnRestoredInterrupted);

        _ = command.Parameters.AddWithValue("$status", TurnClaimTerminalStatus);

        _ = command.Parameters.AddWithValue("$parameters", parameters);

        _ = command.Parameters.AddWithValue("$digest", SHA256.HashData(parameters));

        _ = command.Parameters.AddWithValue("$now", Timestamp(timeProvider));

        return checked((ulong)await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));

    }

    /// <summary>
    /// The 16 canonical policy-v1 bytes of a dataset generation, in network byte order.
    /// </summary>
    /// <remarks>
    /// Big-endian rather than <see cref="Guid.ToByteArray()"/>'s mixed endianness, because the same
    /// value is compared byte-for-byte against generations written by every other Covenant producer.
    /// </remarks>
    private static byte[] CanonicalGenerationBytes(Guid generation)
    {

        byte[] bytes = new byte[16];

        _ = generation.TryWriteBytes(bytes, bigEndian: true, out _);

        return bytes;

    }

    private static async Task<long> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static string Timestamp(TimeProvider timeProvider) =>
        timeProvider.GetUtcNow().UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);

    /// <summary>What the canonical tier looked like after its identities were reissued.</summary>
    private sealed record CanonicalReissue(
        bool Present,
        Guid DatasetGeneration,
        long AcceleratorEpoch,
        long EnvelopeKeyEpoch,
        ulong ClearedOutboxRows)
    {

        internal static CanonicalReissue Absent { get; } = new(false, Guid.Empty, 0, 0, 0);

    }

}
