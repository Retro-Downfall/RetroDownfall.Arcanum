using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Reservation transitions and counter movement for turn and finalization capacity.
/// </summary>
public sealed class CovenantFinalizationCapacityTests
{

    private static readonly Guid SessionId = new("dddddddd-1111-4111-8111-111111111111");

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void A_public_claim_cannot_take_the_direct_allocation_path()
    {

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DirectFinalizationCapacityRequest(
                Guid.NewGuid(),
                SessionId,
                Guid.NewGuid(),
                AssistantFinalizationCapacityOrigin.PublicClaim));

    }

    [Fact]
    public async Task Reserving_takes_one_claim_and_one_guard_slot_together()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        AssistantFinalizationCapacityReservation reservation = await ReserveAsync(fixture, Guid.NewGuid());

        Assert.Equal(AssistantFinalizationCapacityState.Reserved, reservation.State);

        Assert.False(reservation.Replayed);

        Assert.Equal(1, await CountAsync(fixture, "ClaimCount"));

        Assert.Equal(1, await CountAsync(fixture, "ReservedFinalizationCount"));

        Assert.Equal(0, await CountAsync(fixture, "ConsumedFinalizationCount"));

        Assert.Equal(
            1,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT ClaimCount FROM installation_turn_quota_state WHERE StateKey = 1;",
                Token));

    }

    [Fact]
    public async Task Consuming_moves_one_slot_from_reserved_to_consumed()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        Guid reservationId = Guid.NewGuid();

        AssistantFinalizationCapacityReservation reserved = await ReserveAsync(fixture, reservationId);

        Result<AssistantFinalizationCapacityReservation> consumed = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ConsumeReservedFinalizationAsync(reserved.Identity, transaction, Token)
                .AsTask(),
            Token);

        Assert.Equal(AssistantFinalizationCapacityState.Consumed, consumed.Value.State);

        Assert.Equal(1, await CountAsync(fixture, "ClaimCount"));

        Assert.Equal(0, await CountAsync(fixture, "ReservedFinalizationCount"));

        Assert.Equal(1, await CountAsync(fixture, "ConsumedFinalizationCount"));

        // Replay is the same answer, not a second counter move.
        Result<AssistantFinalizationCapacityReservation> replay = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ConsumeReservedFinalizationAsync(reserved.Identity, transaction, Token)
                .AsTask(),
            Token);

        Assert.True(replay.Value.Replayed);

        Assert.Equal(1, await CountAsync(fixture, "ConsumedFinalizationCount"));

    }

    [Fact]
    public async Task Releasing_returns_the_guard_slot_and_never_the_claim()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        AssistantFinalizationCapacityReservation reserved = await ReserveAsync(fixture, Guid.NewGuid());

        Result<AssistantFinalizationCapacityReservation> released = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ReleaseReservedFinalizationAsync(reserved.Identity, transaction, Token)
                .AsTask(),
            Token);

        Assert.Equal(AssistantFinalizationCapacityState.Released, released.Value.State);

        // The claim really was made, so lifetime claim capacity is not given back.
        Assert.Equal(1, await CountAsync(fixture, "ClaimCount"));

        Assert.Equal(0, await CountAsync(fixture, "ReservedFinalizationCount"));

        Assert.Equal(0, await CountAsync(fixture, "ConsumedFinalizationCount"));

    }

    [Fact]
    public async Task A_consumed_reservation_cannot_then_be_released()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        AssistantFinalizationCapacityReservation reserved = await ReserveAsync(fixture, Guid.NewGuid());

        _ = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ConsumeReservedFinalizationAsync(reserved.Identity, transaction, Token)
                .AsTask(),
            Token);

        Result<AssistantFinalizationCapacityReservation> released = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ReleaseReservedFinalizationAsync(reserved.Identity, transaction, Token)
                .AsTask(),
            Token,
            commit: false);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, released.Error.Code);

    }

    [Fact]
    public async Task A_different_identity_for_the_same_reservation_conflicts()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        Guid reservationId = Guid.NewGuid();

        _ = await ReserveAsync(fixture, reservationId);

        Result<AssistantFinalizationCapacityReservation> mismatched = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ConsumeReservedFinalizationAsync(
                    new AssistantFinalizationCapacityIdentity(reservationId, SessionId, Guid.NewGuid()),
                    transaction,
                    Token)
                .AsTask(),
            Token,
            commit: false);

        Assert.Equal("Security.IdempotencyConflict", mismatched.Error.Code);

    }

    [Fact]
    public async Task A_direct_guard_increments_consumed_alone()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        Result<AssistantFinalizationCapacityReservation> direct = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .AllocateDirectFinalizationAsync(
                    new DirectFinalizationCapacityRequest(
                        Guid.NewGuid(),
                        SessionId,
                        Guid.NewGuid(),
                        AssistantFinalizationCapacityOrigin.Forked),
                    transaction,
                    Token)
                .AsTask(),
            Token);

        Assert.Equal(AssistantFinalizationCapacityState.Consumed, direct.Value.State);

        Assert.Equal(0, await CountAsync(fixture, "ClaimCount"));

        Assert.Equal(0, await CountAsync(fixture, "ReservedFinalizationCount"));

        Assert.Equal(1, await CountAsync(fixture, "ConsumedFinalizationCount"));

    }

    [Fact]
    public async Task A_failed_reservation_leaves_no_row_and_no_counter_delta()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        // No Session row at all: the counters have no owner to move.
        Result<AssistantFinalizationCapacityReservation> refused = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ReserveClaimAndFinalizationAsync(
                    new SessionTurnCapacityReservationRequest(
                        Guid.NewGuid(),
                        SessionId,
                        Guid.NewGuid(),
                        Guid.NewGuid()),
                    transaction,
                    Token)
                .AsTask(),
            Token,
            commit: false);

        Assert.Equal(ErrorCodes.Covenant.NotFound, refused.Error.Code);

        Assert.Equal(
            0,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT COUNT(*) FROM assistant_finalization_capacity_reservations;",
                Token));

        Assert.Equal(
            0,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT ClaimCount FROM installation_turn_quota_state WHERE StateKey = 1;",
                Token));

    }

    [Fact]
    public async Task Whole_session_retention_returns_exactly_the_locked_counts()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        AssistantFinalizationCapacityReservation reserved = await ReserveAsync(fixture, Guid.NewGuid());

        _ = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ConsumeReservedFinalizationAsync(reserved.Identity, transaction, Token)
                .AsTask(),
            Token);

        Result wrongCounts = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ReleaseSessionCapacityAsync(
                    new SessionTurnCapacityReleaseRequest(SessionId, 5, 0, 1),
                    transaction,
                    Token)
                .AsTask(),
            Token,
            commit: false);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, wrongCounts.Error.Code);

        Result released = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ReleaseSessionCapacityAsync(
                    new SessionTurnCapacityReleaseRequest(SessionId, 1, 0, 1),
                    transaction,
                    Token)
                .AsTask(),
            Token);

        Assert.True(released.IsSuccess);

        Assert.Equal(
            0,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT ClaimCount + ConsumedFinalizationCount FROM installation_turn_quota_state WHERE StateKey = 1;",
                Token));

    }

    private static async Task<AssistantFinalizationCapacityReservation> ReserveAsync(
        CovenantCanonicalFixture fixture,
        Guid reservationId)
    {

        Result<AssistantFinalizationCapacityReservation> reserved = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .ReserveClaimAndFinalizationAsync(
                    new SessionTurnCapacityReservationRequest(
                        reservationId,
                        SessionId,
                        Guid.NewGuid(),
                        Guid.NewGuid()),
                    transaction,
                    Token)
                .AsTask(),
            Token);

        Assert.True(reserved.IsSuccess, reserved.IsFailure ? reserved.Error.Message : null);

        return reserved.Value;

    }

    private static Task<long> CountAsync(CovenantCanonicalFixture fixture, string column) =>
        CovenantCapacityFixture.ScalarAsync(
            fixture,
            $"SELECT {column} FROM session_turn_quota_state WHERE SessionId = '{SessionId:D}';",
            Token);

}
