using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Carries the canonical facts persisted in <c>covenant_state</c> into the process-wide
/// <see cref="CovenantAvailability"/> snapshot.
/// </summary>
/// <remarks>
/// <para><see cref="CovenantAvailability.PublishSchema"/> reports only which tiers installed. The
/// dataset generation every turn snapshot binds, the canonical and core Campaign deletion sequences,
/// and the accelerator's applied tuple are all persisted state, and until something reads them the
/// snapshot keeps its bootstrap defaults — <c>DatasetGeneration</c> null and every sequence zero —
/// for the whole process lifetime.</para>
/// <para>That is not a diagnostics gap. <c>CovenantOperationGate.CaptureFacts</c> refuses every
/// <c>requireCanonical: true</c> acquisition while the dataset generation is null, and
/// <c>AcquireOrdinary</c> — the ordinary lease every Covenant turn takes — always requires it. So
/// without this step Covenant fails closed on its own hot path the instant the feature is enabled,
/// and every staleness guard downstream is inert because the values it compares never move.</para>
/// <para><c>GrimoireDatabaseBootstrapper</c> is the only caller, and deliberately the only one. The
/// other writer of this tuple is <c>BackupCovenantRestoreReconciler</c>, which rewrites
/// <c>DatasetGeneration</c> and nulls the applied pair — but it cannot leave a serving snapshot stale,
/// because a restore and a host can never be running at once. <c>GrimoireDatabaseHostedService</c>
/// takes <c>ArcanumMaintenanceLock</c> for the host's whole lifetime and fails startup without it,
/// <c>BackupRestoreService</c> refuses with <c>backup.restore_maintenance_unavailable</c> when it
/// cannot take the same lock, and <c>IBackupRestoreService</c> has no API route at all — it is reached
/// only from the CLI's <c>BackupCommands</c>. The restore therefore runs in its own short-lived
/// process against a snapshot nothing serves turns from, and the next <c>arcanum serve</c> republishes
/// here before readiness. <c>CovenantFamilyReinitializeCoordinator</c> is unregistered in this release
/// and constructed nowhere, so it writes no tuple to republish.</para>
/// <para>Any future writer that changes the tuple <em>inside a serving host</em> has to republish
/// through here rather than growing its own derivation: which rebuild states count as owed, and when
/// the applied tuple counts as synchronized, are decided once in this file so two callers cannot drift
/// apart on them.</para>
/// </remarks>
internal static class CovenantPersistedAvailabilityPublisher
{

    private const string CampaignOwnerKindCode = "1";

    /// <summary>
    /// The persisted publication facts, read in one statement.
    /// </summary>
    /// <remarks>
    /// One statement, and therefore one SQLite snapshot: reading the canonical sequence separately
    /// from the applied tuple would let a publication claim an applied position that did not hold
    /// when the canonical position was read, which is exactly the torn view
    /// <see cref="CovenantAvailabilitySnapshot"/> exists to make unobservable. This is the shape
    /// <c>CovenantSearchSql.Sources</c> reads for a search page, plus the rebuild discriminant.
    /// </remarks>
    private const string SelectPersistedState = $"""
        SELECT st.DatasetGeneration,
               st.CanonicalSearchSequence,
               COALESCE((SELECT MAX(Sequence) FROM owner_deletion_events WHERE OwnerKindCode = {CampaignOwnerKindCode}), 0),
               st.AppliedDatasetGeneration,
               st.AppliedSearchSequence,
               st.AppliedCampaignDeletionSequence,
               st.AcceleratorEpoch,
               st.RebuildStateCode
        FROM covenant_state st
        WHERE st.StateKey = 1;
        """;

    /// <summary>
    /// Publishes the persisted canonical and accelerator positions, and reports whether it did.
    /// </summary>
    /// <param name="acceleratorHealthy">
    /// Whether the accelerator tier installed healthily. A degraded tier publishes
    /// <see cref="CovenantFtsSynchronizationState.Unavailable"/> however current its persisted tuple
    /// looks, because the canonical fallback is the only thing answering queries.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the canonical tier is absent, so nothing was published. An
    /// installation without the canonical tier has no dataset generation, and inventing one would
    /// defeat the gate's refusal rather than satisfy it.
    /// </returns>
    internal static async Task<bool> PublishAsync(
        CovenantAvailability availability,
        SqliteConnection connection,
        bool acceleratorHealthy,
        CovenantHealthTransition transition,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(availability);

        ArgumentNullException.ThrowIfNull(connection);

        PersistedState? state = await TryReadAsync(connection, cancellationToken).ConfigureAwait(false);

        if (state is null)
        {

            return false;

        }

        // Idle is the only state that owes nothing. A rebuild that is in progress is still owed
        // until it lands, so it keeps the flag set (see CovenantFtsRebuildState).
        bool rebuildRequired = state.RebuildState != CovenantFtsRebuildState.Idle;

        // The same comparison CovenantSearchSourceSnapshot.AcceleratorEligible makes. Publishing
        // Synchronized on a trailing tuple would let the accelerator answer from a projection that
        // is missing committed mutations.
        bool eligible = state.AppliedDatasetGeneration == state.DatasetGeneration
            && state.AppliedSequence == state.CanonicalSequence
            && state.AppliedCampaignDeletionSequence == state.CoreCampaignDeletionSequence;

        CovenantFtsSynchronizationState synchronization = !acceleratorHealthy
            ? CovenantFtsSynchronizationState.Unavailable
            : eligible
                ? CovenantFtsSynchronizationState.Synchronized
                : CovenantFtsSynchronizationState.Dirty;

        _ = availability.PublishPersistedState(
            state.DatasetGeneration,
            state.CanonicalSequence,
            state.CoreCampaignDeletionSequence,
            state.AppliedDatasetGeneration,
            state.AppliedSequence,
            state.AppliedCampaignDeletionSequence,
            state.AcceleratorEpoch,
            synchronization,
            rebuildRequired,
            transition);

        return true;

    }

    private static async Task<PersistedState?> TryReadAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        try
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = SelectPersistedState;

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                return null;

            }

            return new PersistedState(
                ReadGuid(reader, 0)!.Value,
                reader.GetInt64(1),
                reader.GetInt64(2),
                ReadGuid(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                (ulong)reader.GetInt64(6),
                (CovenantFtsRebuildState)reader.GetInt32(7));

        }
        catch (SqliteException)
        {

            // The canonical tier is absent, so covenant_state does not exist. A failed or skipped
            // canonical install is exactly the case the gate's refusal is right about; there is
            // nothing to publish and nothing to invent.
            return null;

        }

    }

    private static Guid? ReadGuid(SqliteDataReader reader, int ordinal)
    {

        if (reader.IsDBNull(ordinal))
        {

            return null;

        }

        byte[] raw = new byte[16];

        _ = reader.GetBytes(ordinal, 0, raw, 0, raw.Length);

        return new Guid(raw);

    }

    private sealed record PersistedState(
        Guid DatasetGeneration,
        long CanonicalSequence,
        long CoreCampaignDeletionSequence,
        Guid? AppliedDatasetGeneration,
        long? AppliedSequence,
        long? AppliedCampaignDeletionSequence,
        ulong AcceleratorEpoch,
        CovenantFtsRebuildState RebuildState);

}
