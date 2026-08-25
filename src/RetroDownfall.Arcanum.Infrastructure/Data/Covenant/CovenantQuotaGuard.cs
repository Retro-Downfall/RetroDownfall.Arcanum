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

        Error? refusal = CovenantScopeCapacity.Refusal(snapshot, demand);

        if (refusal is { } scopeError)
        {

            return scopeError;

        }

        // The scope-wide ceilings are orders of magnitude looser than the Section ceilings the
        // renderer enforces, so a batch can satisfy every one of them and still assemble a Section
        // that no longer renders. That failure is not confined to the mutation: it is the whole
        // placement, for every turn afterwards, which is why it has to be refused here rather than
        // discovered at render time.
        foreach (CovenantSectionDemand section in demand.Sections)
        {

            Error? sectionRefusal = await CheckSectionAsync(
                    scope,
                    section,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);

            if (sectionRefusal is { } sectionError)
            {

                return sectionError;

            }

        }

        return Result<CovenantQuotaSnapshot>.Success(snapshot);

    }

    private static async ValueTask<Error?> CheckSectionAsync(
        CovenantOperationScope scope,
        CovenantSectionDemand section,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        CovenantSectionOccupancy retained = await ReadRetainedSectionAsync(
                scope,
                section,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        // The arithmetic lives with the ceilings rather than here, because the staging preflight has
        // to reach the same verdict from a read lease that this reaches from a write transaction. A
        // second copy of the comparison is how a proposal comes to be accepted at the tool and refused
        // at the commit, and a refused commit discards the operator's answer along with the batch.
        return CovenantSectionCapacity.Refusal(
            CovenantSectionCapacity.Placement(scope.Kind, section.Lane),
            retained,
            section);

    }

    /// <summary>
    /// What the Section already holds that this batch will not replace.
    /// </summary>
    /// <remarks>
    /// The touched keys are excluded from all three measures rather than only from the counts. A
    /// <c>Set</c> supersedes the active version of its key, so leaving the old version's bytes in the
    /// total would charge one entry twice, and leaving its fence requirement in would size the
    /// Section around backticks the batch is about to remove.
    /// </remarks>
    private static async ValueTask<CovenantSectionOccupancy> ReadRetainedSectionAsync(
        CovenantOperationScope scope,
        CovenantSectionDemand section,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        // The shared builder rather than a second copy of the statement. The preflight a proposal runs
        // under a read lease issues this exact text, and a Section measured by two statements is a
        // Section two callers can disagree about.
        command.CommandText = CovenantStoreSql.SectionOccupancy(
            scope.Kind == CovenantScope.Campaign,
            section.TouchedKeys.Length);

        if (scope.Kind == CovenantScope.Campaign)
        {

            Bind(command, "$campaign", scope.CampaignId!.Value.ToString("D"));

        }

        Bind(command, "$lane", (int)section.Lane);

        for (int index = 0; index < section.TouchedKeys.Length; index++)
        {

            Bind(command, $"$key{index}", section.TouchedKeys[index]);

        }

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CovenantSectionOccupancy(
                reader.GetInt64(0),
                reader.GetInt64(1),
                (int)reader.GetInt64(2))
            : CovenantSectionOccupancy.Empty;

    }

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

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = CovenantStoreSql.QuotaSnapshot(campaignScoped);

        if (campaignScoped)
        {

            Bind(command, "$campaign", scope.CampaignId!.Value.ToString("D"));

        }

        return await CovenantQuotaSnapshotReader.ReadAsync(command, cancellationToken).ConfigureAwait(false);

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
