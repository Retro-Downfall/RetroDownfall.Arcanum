using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// The durable turn-claim contract against a real SQLCipher Grimoire.
/// </summary>
/// <remarks>
/// Every case runs over the installed <c>session_turn_claims</c> table and its triggers rather than a
/// fake, because the rules under test — one live claim per Session, one claim per client turn ID, a
/// closed forward state graph — are enforced by the schema as much as by the store.
/// </remarks>
public sealed class SessionTurnClaimStoreTests
{

    private static readonly Guid SessionId = new("aaaaaaaa-1111-4111-8111-111111111111");

    private static readonly Guid OtherSessionId = new("aaaaaaaa-2222-4222-8222-222222222222");

    private static readonly Guid InstallationId = new("bbbbbbbb-3333-4333-8333-333333333333");

    /// <summary>
    /// The claim tier plus every core object it depends on, in dependency order.
    /// </summary>
    private static readonly string[] ClaimObjects =
    [
        .. CovenantCapacityFixture.CoreObjects,

        "session_turn_claims",

        "session_turn_claims_validate_insert",

        "session_turn_claims_validate_update",

        "session_turn_claims_guard_delete",
    ];

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Acquiring_twice_with_one_client_turn_id_creates_exactly_one_claim()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimStore store = CreateStore(fixture, Guid.NewGuid());

        SessionTurnRequestIdentity request = Request(Guid.NewGuid());

        Result<SessionTurnClaimLease> first = await store.AcquireAsync(request, Token);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        Assert.Equal(SessionTurnClaimDisposition.Created, first.Value.Disposition);

        Assert.Equal(SessionTurnClaimState.PendingMaintenance, first.Value.Claim.State);

        Result<SessionTurnClaimLease> second = await store.AcquireAsync(request, Token);

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : null);

        // The same boot re-entering its own claim resumes it; only another boot is a takeover.
        Assert.Equal(SessionTurnClaimDisposition.Resumed, second.Value.Disposition);

        Assert.Equal(first.Value.Claim.ClaimId, second.Value.Claim.ClaimId);

        Assert.Equal(first.Value.FutureAssistantEntryId, second.Value.FutureAssistantEntryId);

        Assert.Equal(1, await CountClaimsAsync(fixture));

        Assert.Equal(1, await ScalarAsync(fixture, ClaimCountSql));

    }

    [Fact]
    public async Task A_replay_of_a_terminal_claim_returns_the_recorded_outcome()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimStore store = CreateStore(fixture, Guid.NewGuid());

        SessionTurnRequestIdentity request = Request(Guid.NewGuid());

        SessionTurnClaimLease lease = (await store.AcquireAsync(request, Token)).Value;

        SessionTurnClaimOutcome outcome = SessionTurnClaimOutcome.Discarded(
            ErrorCodes.Security.IdempotencyConflict,
            409,
            [1, 2, 3]);

        Result<SessionTurnClaim> completed = await store.CompleteAsync(lease, outcome, Token);

        Assert.True(completed.IsSuccess, completed.IsFailure ? completed.Error.Message : null);

        Result<SessionTurnClaimLease> replay = await store.AcquireAsync(request, Token);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : null);

        Assert.Equal(SessionTurnClaimDisposition.Replayed, replay.Value.Disposition);

        Assert.False(replay.Value.IsExecutable);

        Assert.Equal(SessionTurnClaimState.Discarded, replay.Value.Claim.State);

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, replay.Value.Claim.Outcome!.TerminalErrorCode);

        Assert.Equal(409, replay.Value.Claim.Outcome.TerminalHttpStatus);

    }

    [Fact]
    public async Task The_same_client_turn_id_with_a_different_request_digest_conflicts_and_claims_nothing()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimStore store = CreateStore(fixture, Guid.NewGuid());

        Guid clientTurnId = Guid.NewGuid();

        _ = await store.AcquireAsync(Request(clientTurnId), Token);

        Result<SessionTurnClaimLease> conflicted = await store.AcquireAsync(
            Request(clientTurnId, requestSeed: 99),
            Token);

        Assert.True(conflicted.IsFailure);

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, conflicted.Error.Code);

        Assert.Equal(1, await CountClaimsAsync(fixture));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM assistant_finalization_capacity_reservations;"));

    }

    [Fact]
    public async Task A_second_live_claim_on_the_same_session_is_busy()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimStore store = CreateStore(fixture, Guid.NewGuid());

        _ = await store.AcquireAsync(Request(Guid.NewGuid()), Token);

        Result<SessionTurnClaimLease> busy = await store.AcquireAsync(Request(Guid.NewGuid()), Token);

        Assert.True(busy.IsFailure);

        Assert.Equal(ErrorCodes.Hub.SessionTurnBusy, busy.Error.Code);

        Assert.Equal(1, await CountClaimsAsync(fixture));

    }

    [Fact]
    public async Task A_claim_abandoned_by_a_crashed_process_is_adopted_rather_than_duplicated()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        Guid crashedBoot = Guid.NewGuid();

        SessionTurnClaimStore crashed = CreateStore(fixture, crashedBoot);

        SessionTurnRequestIdentity request = Request(Guid.NewGuid());

        SessionTurnClaimLease before = (await crashed.AcquireAsync(request, Token)).Value;

        Assert.Equal(crashedBoot, before.Claim.OwnerBootId);

        // A crash is exactly a new boot finding a live claim it never owned.
        Guid restartedBoot = Guid.NewGuid();

        SessionTurnClaimStore restarted = CreateStore(fixture, restartedBoot);

        Result<SessionTurnClaimLease> adopted = await restarted.AcquireAsync(request, Token);

        Assert.True(adopted.IsSuccess, adopted.IsFailure ? adopted.Error.Message : null);

        Assert.Equal(SessionTurnClaimDisposition.Adopted, adopted.Value.Disposition);

        Assert.Equal(before.Claim.ClaimId, adopted.Value.Claim.ClaimId);

        Assert.Equal(restartedBoot, adopted.Value.Claim.OwnerBootId);

        Assert.NotEqual(before.ExecutorId, adopted.Value.ExecutorId);

        Assert.Equal(1, await CountClaimsAsync(fixture));

        // The adopted-away executor no longer has authority over the claim it started.
        Result<SessionTurnClaim> stale = await crashed.CompleteAsync(
            before,
            SessionTurnClaimOutcome.RestoredInterrupted("Covenant.RestoreInterrupted", 409, [7]),
            Token);

        Assert.True(stale.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, stale.Error.Code);

    }

    [Fact]
    public async Task Adoption_still_revalidates_the_request_digest()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        Guid clientTurnId = Guid.NewGuid();

        _ = await CreateStore(fixture, Guid.NewGuid()).AcquireAsync(Request(clientTurnId), Token);

        // A prior-boot claim is adoptable, but never into a request it was not accepted for.
        Result<SessionTurnClaimLease> conflicted = await CreateStore(fixture, Guid.NewGuid())
            .AcquireAsync(Request(clientTurnId, requestSeed: 42), Token);

        Assert.True(conflicted.IsFailure);

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, conflicted.Error.Code);

    }

    [Fact]
    public async Task Terminalization_is_one_shot()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimStore store = CreateStore(fixture, Guid.NewGuid());

        SessionTurnClaimLease lease = (await store.AcquireAsync(Request(Guid.NewGuid()), Token)).Value;

        Result<SessionTurnClaim> first = await store.CompleteAsync(
            lease,
            SessionTurnClaimOutcome.Discarded("Hub.TurnCancelled", 499, [4]),
            Token);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        Assert.Equal(SessionTurnClaimState.Discarded, first.Value.State);

        // Repeating the identical terminalization is the same durable answer.
        Result<SessionTurnClaim> repeated = await store.CompleteAsync(
            lease,
            SessionTurnClaimOutcome.Discarded("Hub.TurnCancelled", 499, [4]),
            Token);

        Assert.True(repeated.IsSuccess, repeated.IsFailure ? repeated.Error.Message : null);

        Assert.Equal(SessionTurnClaimState.Discarded, repeated.Value.State);

        // A different terminal answer for the same claim is refused outright.
        Result<SessionTurnClaim> contradicted = await store.CompleteAsync(
            lease,
            SessionTurnClaimOutcome.RestoredInterrupted("Covenant.RestoreInterrupted", 409, [5]),
            Token);

        Assert.True(contradicted.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, contradicted.Error.Code);

    }

    [Fact]
    public async Task Terminalizing_a_never_begun_claim_releases_exactly_its_reserved_finalization()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimStore store = CreateStore(fixture, Guid.NewGuid());

        SessionTurnClaimLease lease = (await store.AcquireAsync(Request(Guid.NewGuid()), Token)).Value;

        Assert.Equal(1, await ScalarAsync(fixture, ReservedCountSql));

        _ = await store.CompleteAsync(lease, SessionTurnClaimOutcome.Discarded("Hub.TurnCancelled", 499, [4]), Token);

        Assert.Equal(0, await ScalarAsync(fixture, ReservedCountSql));

        // The claim itself really happened, so lifetime claim capacity is not handed back.
        Assert.Equal(1, await ScalarAsync(fixture, ClaimCountSql));

    }

    [Fact]
    public async Task Begin_records_the_reserved_future_assistant_entry_and_refuses_any_other()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimStore store = CreateStore(fixture, Guid.NewGuid());

        SessionTurnClaimLease lease = (await store.AcquireAsync(Request(Guid.NewGuid()), Token)).Value;

        Assert.NotEqual(Guid.Empty, lease.FutureAssistantEntryId);

        Result<SessionTurnClaim> mismatched = await store.MarkBegunAsync(
            lease,
            new AssistantReplyBeginReceipt(
                SessionId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new SessionTurnInputPreflight(SessionId, SessionCampaignBinding.GlobalOnly, 0, 0)),
            Token);

        Assert.True(mismatched.IsFailure);

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, mismatched.Error.Code);

        Guid userEntryId = Guid.NewGuid();

        Result<SessionTurnClaim> begun = await store.MarkBegunAsync(
            lease,
            new AssistantReplyBeginReceipt(
                SessionId,
                userEntryId,
                lease.FutureAssistantEntryId,
                new SessionTurnInputPreflight(SessionId, SessionCampaignBinding.GlobalOnly, 0, 0)),
            Token);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        Assert.Equal(SessionTurnClaimState.Begun, begun.Value.State);

        Assert.Equal(userEntryId, begun.Value.UserEntryId);

        Assert.Equal(lease.FutureAssistantEntryId, begun.Value.AssistantEntryId);

    }

    [Fact]
    public async Task Only_a_committed_claim_advances_to_its_erasure_tombstone()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimStore store = CreateStore(fixture, Guid.NewGuid());

        SessionTurnClaimLease lease = (await store.AcquireAsync(Request(Guid.NewGuid()), Token)).Value;

        // A pending claim has written no Entries, so it has no answer to erase.
        Result<SessionTurnClaim> premature = await store.CompleteAsync(
            lease,
            SessionTurnClaimOutcome.Erased(),
            Token);

        Assert.True(premature.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, premature.Error.Code);

        _ = await store.MarkBegunAsync(
            lease,
            new AssistantReplyBeginReceipt(
                SessionId,
                Guid.NewGuid(),
                lease.FutureAssistantEntryId,
                new SessionTurnInputPreflight(SessionId, SessionCampaignBinding.GlobalOnly, 0, 0)),
            Token);

        Result<SessionTurnClaim> committed = await store.CompleteAsync(
            lease,
            SessionTurnClaimOutcome.Committed(),
            Token);

        Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : null);

        Result<SessionTurnClaim> erased = await store.CompleteAsync(
            lease,
            SessionTurnClaimOutcome.Erased(),
            Token);

        Assert.True(erased.IsSuccess, erased.IsFailure ? erased.Error.Message : null);

        Assert.Equal(SessionTurnClaimState.Erased, erased.Value.State);

    }

    private const string ReservedCountSql =
        "SELECT ReservedFinalizationCount FROM installation_turn_quota_state WHERE StateKey = 1;";

    private const string ClaimCountSql =
        "SELECT ClaimCount FROM installation_turn_quota_state WHERE StateKey = 1;";

    private static async Task<CovenantCanonicalFixture> CreateAsync()
    {

        CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            Token,
            coreObjects: ClaimObjects);

        await using (SqliteCommand command = fixture.Connection.CreateCommand())
        {

            command.CommandText = """
                INSERT OR IGNORE INTO installation_turn_quota_state
                    (StateKey, ClaimCount, ReservedFinalizationCount, ConsumedFinalizationCount)
                VALUES (1, 0, 0, 0);
                """;

            _ = await command.ExecuteNonQueryAsync(Token);

        }

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, OtherSessionId, Token);

        return fixture;

    }

    private static SessionTurnClaimStore CreateStore(CovenantCanonicalFixture fixture, Guid bootId) =>
        new(new FixedCovenantConnectionSource(fixture.Connection), new CovenantQuotaGuard(), bootId);

    private static SessionTurnRequestIdentity Request(Guid clientTurnId, byte requestSeed = 1) =>
        new(
            InstallationId,
            OriginRestoreEpoch: 0,
            clientTurnId,
            SessionId,
            SessionTurnSurface.Intelligence,
            CovenantOperationGateFixture.Digest(requestSeed),
            CovenantOperationGateFixture.Digest(2),
            PreRequestHistoryWatermarkUtc: null,
            PreRequestHistoryRevision: 0,
            InputSensitivityRevision: 0);

    private static Task<long> CountClaimsAsync(CovenantCanonicalFixture fixture) =>
        ScalarAsync(fixture, "SELECT COUNT(*) FROM session_turn_claims;");

    private static async Task<long> ScalarAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(Token);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

}
