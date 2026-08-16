using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The transaction-bound guard for canonical Covenant quotas and turn-capacity reservations.
/// </summary>
/// <remarks>
/// Every method requires the caller's live transaction, performs no commit and no retry, and borrows
/// one <see cref="CovenantSqliteAuthorizationKind.TurnCapacityMutation"/> scope across its complete
/// multi-statement sequence before disposing it. It never lends that authorization to a caller: the
/// scope is what stops direct SQL from minting capacity, and handing it out would make that
/// guarantee decorative.
///
/// <para>The claim and finalization methods touch only always-present core tables. That is what lets
/// the disabled, Covenant-free path use them without probing an optional Covenant table it may not
/// have.</para>
/// </remarks>
internal sealed class CovenantQuotaGuard(ICovenantSqliteConnectionInitializer initializer)
{

    /// <summary>
    /// A guard for callers that already hold the process-wide initializer singleton.
    /// </summary>
    internal CovenantQuotaGuard()
        : this(CovenantSqliteConnectionInitializer.Instance)
    {
    }

    public async ValueTask<Result<AssistantFinalizationCapacityReservation>> ReserveClaimAndFinalizationAsync(
        SessionTurnCapacityReservationRequest request,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(transaction);

        using CovenantSqliteAuthorizationScope authorization = Authorize(transaction);

        AssistantFinalizationCapacityReservation? existing =
            await ReadReservationAsync(transaction, request.Identity.ReservationId, cancellationToken)
                .ConfigureAwait(false);

        if (existing is not null)
        {

            return SameIdentity(existing, request.Identity) && existing.ClaimId == request.ClaimId
                ? existing with { Replayed = true }
                : new Error(
                    "Security.IdempotencyConflict",
                    "This finalization capacity reservation identity is already bound to different facts.");

        }

        Result counters = await MoveCountersAsync(
                transaction,
                request.Identity.SessionId,
                claimDelta: 1,
                reservedDelta: 1,
                consumedDelta: 0,
                cancellationToken)
            .ConfigureAwait(false);

        if (counters.IsFailure)
        {

            return counters.Error;

        }

        await InsertReservationAsync(
                transaction,
                request.Identity,
                AssistantFinalizationCapacityOrigin.PublicClaim,
                request.ClaimId,
                AssistantFinalizationCapacityState.Reserved,
                cancellationToken)
            .ConfigureAwait(false);

        return new AssistantFinalizationCapacityReservation(
            request.Identity,
            AssistantFinalizationCapacityOrigin.PublicClaim,
            request.ClaimId,
            AssistantFinalizationCapacityState.Reserved,
            Replayed: false);

    }

    public ValueTask<Result<AssistantFinalizationCapacityReservation>> ConsumeReservedFinalizationAsync(
        AssistantFinalizationCapacityIdentity identity,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            identity,
            AssistantFinalizationCapacityState.Consumed,
            reservedDelta: -1,
            consumedDelta: 1,
            transaction,
            cancellationToken);

    public ValueTask<Result<AssistantFinalizationCapacityReservation>> ReleaseReservedFinalizationAsync(
        AssistantFinalizationCapacityIdentity identity,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            identity,
            AssistantFinalizationCapacityState.Released,
            reservedDelta: -1,
            consumedDelta: 0,
            transaction,
            cancellationToken);

    public async ValueTask<Result<AssistantFinalizationCapacityReservation>> AllocateDirectFinalizationAsync(
        DirectFinalizationCapacityRequest request,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(transaction);

        using CovenantSqliteAuthorizationScope authorization = Authorize(transaction);

        AssistantFinalizationCapacityReservation? existing =
            await ReadReservationAsync(transaction, request.Identity.ReservationId, cancellationToken)
                .ConfigureAwait(false);

        if (existing is not null)
        {

            return SameIdentity(existing, request.Identity) && existing.Origin == request.Origin
                ? existing with { Replayed = true }
                : new Error(
                    "Security.IdempotencyConflict",
                    "This finalization capacity reservation identity is already bound to different facts.");

        }

        Result counters = await MoveCountersAsync(
                transaction,
                request.Identity.SessionId,
                claimDelta: 0,
                reservedDelta: 0,
                consumedDelta: 1,
                cancellationToken)
            .ConfigureAwait(false);

        if (counters.IsFailure)
        {

            return counters.Error;

        }

        await InsertReservationAsync(
                transaction,
                request.Identity,
                request.Origin,
                claimId: null,
                AssistantFinalizationCapacityState.Consumed,
                cancellationToken)
            .ConfigureAwait(false);

        return new AssistantFinalizationCapacityReservation(
            request.Identity,
            request.Origin,
            ClaimId: null,
            AssistantFinalizationCapacityState.Consumed,
            Replayed: false);

    }

    /// <summary>
    /// Whole-Session retention decrementing installation totals from the exact locked Session row.
    /// </summary>
    public async ValueTask<Result> ReleaseSessionCapacityAsync(
        SessionTurnCapacityReleaseRequest request,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(transaction);

        using CovenantSqliteAuthorizationScope authorization = Authorize(transaction);

        await using SqliteCommand command = transaction.CreateCommand();

        // The compare is against the Session row's own counts, so a concurrent change to that row
        // between the caller locking it and this statement running gives back a different number
        // than it took and is refused rather than applied.
        command.CommandText = """
            UPDATE installation_turn_quota_state
            SET ClaimCount = ClaimCount - $claims,
                ReservedFinalizationCount = ReservedFinalizationCount - $reserved,
                ConsumedFinalizationCount = ConsumedFinalizationCount - $consumed
            WHERE StateKey = 1
              AND EXISTS (
                  SELECT 1 FROM session_turn_quota_state
                  WHERE SessionId = $session
                    AND ClaimCount = $claims
                    AND ReservedFinalizationCount = $reserved
                    AND ConsumedFinalizationCount = $consumed
              );
            """;

        BindIdentity(command, "$session", request.SessionId);

        Bind(command, "$claims", request.ExpectedClaimCount);

        Bind(command, "$reserved", request.ExpectedReservedCount);

        Bind(command, "$consumed", request.ExpectedConsumedCount);

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return affected == 1
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The Session turn-capacity counters changed before retention could return them.");

    }

    /// <summary>
    /// Checks one prospective canonical batch against every scope and Campaign quota at once.
    /// </summary>
    public async ValueTask<Result<CovenantQuotaSnapshot>> CheckCanonicalCapacityAsync(
        CovenantOperationScope scope,
        CovenantQuotaDemand demand,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(demand);

        ArgumentNullException.ThrowIfNull(transaction);

        CovenantQuotaSnapshot snapshot = await ReadSnapshotAsync(scope, transaction, cancellationToken)
            .ConfigureAwait(false);

        Error? refusal =
            Exceeds(snapshot.ActiveEntriesInScope, demand.NewEntries, CovenantLimits.MaxStableEntriesPerScope, "stable entries in this scope")
            ?? Exceeds(snapshot.VersionsInScope, demand.NewVersions, CovenantLimits.MaxVersionsPerScope, "versions in this scope")
            ?? Exceeds(snapshot.SetVersionsInScope, demand.NewSetVersions, CovenantLimits.MaxSetVersionsPerScope, "content versions in this scope")
            ?? Exceeds(snapshot.CanonicalBytesInScope, demand.NewCanonicalBytes, CovenantLimits.MaxCanonicalBytesPerScope, "canonical bytes in this scope")
            ?? Exceeds(snapshot.AgentVersionsInCampaign, demand.NewAgentVersions, CovenantLimits.MaxAgentVersionsPerCampaign, "agent versions in this Campaign")
            ?? Exceeds(snapshot.AgentBytesInCampaign, demand.NewAgentBytes, CovenantLimits.MaxAgentBytesPerCampaign, "agent bytes in this Campaign")
            ?? Exceeds(snapshot.MutationReceiptsInScope, demand.NewMutationReceipts, CovenantLimits.MaxMutationReceiptsPerScope, "mutation receipts in this scope")
            ?? Exceeds(snapshot.ProvenanceRowsInCampaign, demand.NewProvenanceRows, CovenantLimits.MaxAttachmentProvenanceRowsPerCampaign, "attachment provenance rows in this Campaign")
            ?? Exceeds(snapshot.PendingOutboxRows, demand.NewOutboxRows, CovenantLimits.MaxPendingSearchOutboxRows, "pending search-outbox rows");

        return refusal is { } error
            ? error
            : Result<CovenantQuotaSnapshot>.Success(snapshot);

    }

    private static Error? Exceeds(long current, long added, long ceiling, string what) =>
        checked(current + added) > ceiling
            ? new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                $"This mutation would exceed the bound on {what}.")
            : null;

    private async ValueTask<Result<AssistantFinalizationCapacityReservation>> TransitionAsync(
        AssistantFinalizationCapacityIdentity identity,
        AssistantFinalizationCapacityState target,
        long reservedDelta,
        long consumedDelta,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(transaction);

        using CovenantSqliteAuthorizationScope authorization = Authorize(transaction);

        AssistantFinalizationCapacityReservation? existing =
            await ReadReservationAsync(transaction, identity.ReservationId, cancellationToken)
                .ConfigureAwait(false);

        if (existing is null)
        {

            return new Error(
                ErrorCodes.Covenant.NotFound,
                "There is no finalization capacity reservation with that identity.");

        }

        if (!SameIdentity(existing, identity))
        {

            return new Error(
                "Security.IdempotencyConflict",
                "This finalization capacity reservation belongs to a different Session or assistant entry.");

        }

        // Both non-reserved states are terminal, so a replayed transition is the same answer rather
        // than a second counter move.
        if (existing.State == target)
        {

            return existing with { Replayed = true };

        }

        if (existing.State != AssistantFinalizationCapacityState.Reserved)
        {

            return new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This finalization capacity reservation has already reached a terminal state.");

        }

        Result counters = await MoveCountersAsync(
                transaction,
                identity.SessionId,
                claimDelta: 0,
                reservedDelta,
                consumedDelta,
                cancellationToken)
            .ConfigureAwait(false);

        if (counters.IsFailure)
        {

            return counters.Error;

        }

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            UPDATE assistant_finalization_capacity_reservations
            SET StateCode = $state, StateChangedAtUtc = $changed
            WHERE ReservationId = $reservation AND StateCode = 1;
            """;

        Bind(command, "$state", (int)target);

        Bind(command, "$changed", Iso(DateTimeOffset.UtcNow));

        BindIdentity(command, "$reservation", identity.ReservationId);

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {

            return new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This finalization capacity reservation changed state before the transition could apply.");

        }

        return existing with { State = target, Replayed = false };

    }

    private CovenantSqliteAuthorizationScope Authorize(CovenantMutationTransaction transaction) =>
        initializer.Authorize(transaction.Connection, CovenantSqliteAuthorizationKind.TurnCapacityMutation);

    private static bool SameIdentity(
        AssistantFinalizationCapacityReservation reservation,
        AssistantFinalizationCapacityIdentity identity) =>
        reservation.Identity.SessionId == identity.SessionId
        && reservation.Identity.AssistantEntryId == identity.AssistantEntryId;

    private static async ValueTask<AssistantFinalizationCapacityReservation?> ReadReservationAsync(
        CovenantMutationTransaction transaction,
        Guid reservationId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT SessionId, AssistantEntryId, OriginCode, ClaimId, StateCode
            FROM assistant_finalization_capacity_reservations
            WHERE ReservationId = $reservation;
            """;

        BindIdentity(command, "$reservation", reservationId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return null;

        }

        return new AssistantFinalizationCapacityReservation(
            new AssistantFinalizationCapacityIdentity(
                reservationId,
                Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture)),
            (AssistantFinalizationCapacityOrigin)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            (AssistantFinalizationCapacityState)reader.GetInt32(4),
            Replayed: false);

    }

    private static async ValueTask InsertReservationAsync(
        CovenantMutationTransaction transaction,
        AssistantFinalizationCapacityIdentity identity,
        AssistantFinalizationCapacityOrigin origin,
        Guid? claimId,
        AssistantFinalizationCapacityState state,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            INSERT INTO assistant_finalization_capacity_reservations (
                ReservationId, SessionId, AssistantEntryId, OriginCode, ClaimId, StateCode,
                CreatedAtUtc, StateChangedAtUtc)
            VALUES ($reservation, $session, $assistant, $origin, $claim, $state, $created, $created);
            """;

        BindIdentity(command, "$reservation", identity.ReservationId);

        BindIdentity(command, "$session", identity.SessionId);

        BindIdentity(command, "$assistant", identity.AssistantEntryId);

        Bind(command, "$origin", (int)origin);

        Bind(command, "$claim", claimId is { } claim ? claim : DBNull.Value);

        Bind(command, "$state", (int)state);

        Bind(command, "$created", Iso(DateTimeOffset.UtcNow));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Moves the per-Session and installation counters as one compare-and-swap unit.
    /// </summary>
    private static async ValueTask<Result> MoveCountersAsync(
        CovenantMutationTransaction transaction,
        Guid sessionId,
        long claimDelta,
        long reservedDelta,
        long consumedDelta,
        CancellationToken cancellationToken)
    {

        await using (SqliteCommand session = transaction.CreateCommand())
        {

            session.CommandText = """
                UPDATE session_turn_quota_state
                SET ClaimCount = ClaimCount + $claims,
                    ReservedFinalizationCount = ReservedFinalizationCount + $reserved,
                    ConsumedFinalizationCount = ConsumedFinalizationCount + $consumed
                WHERE SessionId = $session;
                """;

            Bind(session, "$claims", claimDelta);

            Bind(session, "$reserved", reservedDelta);

            Bind(session, "$consumed", consumedDelta);

            BindIdentity(session, "$session", sessionId);

            int affected;

            try
            {

                affected = await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {

                // A CHECK refusal here is the ceiling doing its job, not a defect.
                return new Error(
                    ErrorCodes.Covenant.CapacityExceeded,
                    "This Session has exhausted its turn-claim or finalization-guard capacity.");

            }

            if (affected != 1)
            {

                return new Error(
                    ErrorCodes.Covenant.NotFound,
                    "This Session has no turn-capacity counter row.");

            }

        }

        await using SqliteCommand installation = transaction.CreateCommand();

        installation.CommandText = """
            UPDATE installation_turn_quota_state
            SET ClaimCount = ClaimCount + $claims,
                ReservedFinalizationCount = ReservedFinalizationCount + $reserved,
                ConsumedFinalizationCount = ConsumedFinalizationCount + $consumed
            WHERE StateKey = 1;
            """;

        Bind(installation, "$claims", claimDelta);

        Bind(installation, "$reserved", reservedDelta);

        Bind(installation, "$consumed", consumedDelta);

        try
        {

            return await installation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
                ? Result.Success()
                : new Error(ErrorCodes.Covenant.NotFound, "The installation turn-capacity counter row is missing.");

        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {

            return new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                "This installation has exhausted its turn-claim or finalization-guard capacity.");

        }

    }

    private static async ValueTask<CovenantQuotaSnapshot> ReadSnapshotAsync(
        CovenantOperationScope scope,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        bool campaignScoped = scope.Kind == CovenantScope.Campaign;

        string scopePredicate = campaignScoped ? "CampaignId = $campaign" : "CampaignId IS NULL";

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = $"""
            SELECT
                (SELECT COUNT(*) FROM covenant_heads WHERE {scopePredicate} AND CurrentOperationCode = 1),
                (SELECT COUNT(*) FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate}),
                (SELECT COUNT(*) FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate} AND v.OperationCode = 1),
                (SELECT COALESCE(SUM(v.CompiledByteCost), 0) FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate}),
                (SELECT COUNT(*) FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate} AND v.OriginCode IN (2, 3)),
                (SELECT COALESCE(SUM(v.CompiledByteCost), 0) FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate} AND v.OriginCode IN (2, 3)),
                (SELECT COUNT(*) FROM covenant_mutation_receipts WHERE {scopePredicate}),
                (SELECT COUNT(*) FROM covenant_version_attachment_provenance p
                    JOIN covenant_versions v ON v.VersionId = p.VersionId
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate}),
                (SELECT COUNT(*) FROM covenant_search_outbox);
            """;

        if (campaignScoped)
        {

            Bind(command, "$campaign", scope.CampaignId!.Value.ToString("D"));

        }

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new CovenantQuotaSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8));

    }

    private static void Bind(SqliteCommand command, string name, object value) =>
        _ = command.Parameters.AddWithValue(name, value);

    /// <summary>
    /// Binds a row identity in the same representation EF writes.
    /// </summary>
    /// <remarks>
    /// The value is bound as a <see cref="Guid"/> rather than as a formatted string so the provider
    /// produces exactly the text EF stored. These tables carry real foreign keys to EF-owned
    /// <c>Sessions</c> rows, and EF's SQLite mapping writes an uppercase <c>D</c>-format literal, so
    /// a lowercase parameter matched nothing: every reservation and guard would have failed its
    /// foreign key in a real host while passing against a suite that seeded its own lowercase rows.
    /// </remarks>
    private static void BindIdentity(SqliteCommand command, string name, Guid value) =>
        _ = command.Parameters.AddWithValue(name, value);

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

}
