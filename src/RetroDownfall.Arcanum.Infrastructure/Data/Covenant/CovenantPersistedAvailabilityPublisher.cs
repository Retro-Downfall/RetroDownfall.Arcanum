using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

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
/// <para>The callers, each after the transaction that wrote the tuple has committed:</para>
/// <list type="bullet">
/// <item><c>GrimoireDatabaseBootstrapper</c>, before readiness, with the transition
/// <see cref="CovenantHealthTransition.Bootstrap"/>.</item>
/// <item><c>GrimoireSchemaTransitionCoordinator</c>, after a background schema step converges, with
/// <see cref="CovenantHealthTransition.SchemaEvolution"/>.</item>
/// <item><c>CovenantRecoveryAuthorityBootstrapper</c>, before pre-readiness recovery takes its lease,
/// reporting the accelerator unhealthy.</item>
/// <item><see cref="CovenantAvailabilityRepublisher"/>, for every writer that changes the tuple inside a
/// serving host: the outbox coordinator (<see cref="CovenantHealthTransition.AcceleratorSynchronization"/>),
/// the owner-cleanup coordinator (<see cref="CovenantHealthTransition.OwnerCleanup"/>), the index rebuilder
/// (<see cref="CovenantHealthTransition.AcceleratorRebuild"/>), and the operator mutation, review apply,
/// turn commit and entry erasure (<see cref="CovenantHealthTransition.CanonicalMutation"/>).</item>
/// </list>
/// <para>Three writers deliberately do not call here. An installation-level reset or erasure stamps a new
/// dataset under an exclusive lease and publishes its successor by reference through
/// <c>CovenantAuthorityTransitionPublisher</c>, because the dataset generation can only change together
/// with authority. <c>BackupCovenantRestoreReconciler</c> and <c>BackupRestoreErasureEvidenceApplier</c>
/// rewrite a staged database in the CLI's own restore process: <c>GrimoireDatabaseHostedService</c> holds
/// <c>ArcanumMaintenanceLock</c> for the host's lifetime, the restore refuses with
/// <c>backup.restore_maintenance_unavailable</c> without that lock, and the next <c>arcanum serve</c>
/// republishes here before readiness. A core Campaign delete appends the owner-deletion event that moves
/// the core Campaign-deletion sequence, but it holds no Covenant gate lease, so a publication from it
/// could land inside an exclusive transition's reference swap; the next maintenance pass republishes it
/// instead. <c>CovenantFamilyReinitializeCoordinator</c> is unregistered in this release and constructed
/// nowhere, so it writes no tuple to republish.</para>
/// <para>Any new writer of the tuple inside a serving host republishes through here too, rather than
/// growing its own derivation: which rebuild states count as owed, and when the applied tuple counts as
/// synchronized, are decided once in this file so two callers cannot drift apart on them.</para>
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

/// <summary>
/// Republishes the persisted tuple after a writer inside a serving host commits a change to it.
/// </summary>
/// <remarks>
/// <para>Every caller republishes after its own commit, on the connection that committed, and while it
/// still holds the gate lease that protected the write. Reading inside the transaction would publish a
/// position that a rollback could still undo. Holding the lease keeps the publication out of an
/// exclusive transition: the gate drains every live lease before such a transition commits, and the
/// transition then publishes its successor by reference against the snapshot it captured.</para>
/// <para>Whether the accelerator is healthy is read from the published capability state, which
/// bootstrap derived from the same installation result it handed to
/// <see cref="CovenantPersistedAvailabilityPublisher.PublishAsync"/>. What counts as owed or
/// synchronized is still decided only there.</para>
/// <para>A failure is never the writer's failure, because its rows are already durable. It is logged
/// without content and swallowed, and that includes a SQLite failure the publisher's own read absorbs
/// and reports only as nothing published. The snapshot it left stale is corrected by the next maintenance pass,
/// whose cleanup and outbox batches republish after every commit even when they apply nothing, or by
/// bootstrap on restart. Two republications that race land in either order, and the same pass corrects
/// the older one.</para>
/// </remarks>
internal sealed class CovenantAvailabilityRepublisher(
    CovenantAvailability availability,
    ILogger<CovenantAvailabilityRepublisher> logger)
{

    /// <summary>The fixed failure name for a state read the publisher absorbed.</summary>
    private const string UnreadableState = "CovenantStateUnreadable";

    internal async Task RepublishAsync(SqliteConnection connection, CovenantHealthTransition transition)
    {

        ArgumentNullException.ThrowIfNull(connection);

        try
        {

            // Not the caller's token: a caller that stopped waiting has not undone the commit, and the
            // whole cost is one read of a singleton row.
            bool published = await CovenantPersistedAvailabilityPublisher.PublishAsync(
                availability,
                connection,
                availability.Current.Accelerator is CovenantCapabilityState.Healthy,
                transition,
                CancellationToken.None).ConfigureAwait(false);

            // The publisher's read absorbs a SQLite failure and reports it as nothing to publish. Every
            // caller has just committed a Covenant write on a healthy canonical tier, whose state row
            // therefore exists, so nothing to publish here means the read failed.
            if (!published)
            {

                LogNotRepublished(transition, UnreadableState);

            }

        }
        catch (Exception failure)
        {

            // The type names the failure; its message is not repeated, so nothing it carries is logged.
            LogNotRepublished(transition, failure.GetType().Name);

        }

    }

    private void LogNotRepublished(CovenantHealthTransition transition, string failure) =>
        logger.LogWarning(
            "Covenant availability was not republished after a committed {Transition} ({FailureType}); the next maintenance pass or a restart republishes it.",
            transition,
            failure);

}
