using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The single-use capability one Covenant MCP request runs under: take once, lease per operation,
/// recheck before anything irreversible, and drain on disposal (§10.14).
/// </summary>
public sealed class CovenantToolInvocationContextTests
{

    private static readonly Guid TurnId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public void A_fresh_capability_is_registered_and_grants_nothing_until_it_is_taken()
    {
        using CapabilityFixture fixture = new();

        Assert.Equal(CovenantToolCapabilityState.Registered, fixture.Context.State);
        Assert.Equal(
            ErrorCodes.Covenant.LifecycleConflict,
            fixture.Context.TryAcquireUse(fixture.Nonce).Error.Code);
        Assert.Equal(
            ErrorCodes.Covenant.LifecycleConflict,
            fixture.Context.RecheckBeforeIrreversibleEffect(fixture.Nonce).Error.Code);
    }

    [Fact]
    public void Take_succeeds_exactly_once_and_a_replay_is_refused()
    {
        using CapabilityFixture fixture = new();

        Result first = fixture.Context.TryTake(fixture.Nonce);
        Result replay = fixture.Context.TryTake(fixture.Nonce);

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.Equal(CovenantToolCapabilityState.Taken, fixture.Context.State);
        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, replay.Error.Code);
    }

    [Fact]
    public void A_wrong_nonce_takes_nothing_and_leases_nothing()
    {
        using CapabilityFixture fixture = new();

        CovenantToolCapabilityNonce forged = CovenantToolCapabilityNonce.Create();

        Result forgedTake = fixture.Context.TryTake(forged);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, forgedTake.Error.Code);
        Assert.Equal(CovenantToolCapabilityState.Registered, fixture.Context.State);

        _ = fixture.Context.TryTake(fixture.Nonce);

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            fixture.Context.TryAcquireUse(forged).Error.Code);
        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            fixture.Context.RecheckBeforeIrreversibleEffect(forged).Error.Code);
    }

    [Fact]
    public void A_taken_capability_leases_and_rechecks_cleanly()
    {
        using CapabilityFixture fixture = new();

        _ = fixture.Context.TryTake(fixture.Nonce);

        Result<IDisposable> lease = fixture.Context.TryAcquireUse(fixture.Nonce);

        Assert.True(lease.IsSuccess, lease.Error.Message);
        Assert.True(fixture.Context.RecheckBeforeIrreversibleEffect(fixture.Nonce).IsSuccess);

        lease.Value.Dispose();
    }

    [Fact]
    public void A_recheck_fails_once_the_collector_moved_to_another_branch()
    {
        using CapabilityFixture fixture = new();

        _ = fixture.Context.TryTake(fixture.Nonce);

        _ = fixture.Collector.OpenBranch(Guid.NewGuid(), sharedPrefixOrdinal: 0);

        Result recheck = fixture.Context.RecheckBeforeIrreversibleEffect(fixture.Nonce);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, recheck.Error.Code);
    }

    [Fact]
    public void A_recheck_fails_once_the_collector_stopped_accepting_work()
    {
        using CapabilityFixture fixture = new();

        _ = fixture.Context.TryTake(fixture.Nonce);

        fixture.Collector.Discard();

        Result recheck = fixture.Context.RecheckBeforeIrreversibleEffect(fixture.Nonce);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, recheck.Error.Code);
    }

    [Fact]
    public void A_recheck_fails_once_the_turn_is_cancelled()
    {
        using CapabilityFixture fixture = new();

        _ = fixture.Context.TryTake(fixture.Nonce);

        fixture.CancelTurn();

        Result recheck = fixture.Context.RecheckBeforeIrreversibleEffect(fixture.Nonce);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, recheck.Error.Code);
    }

    [Fact]
    public async Task Disposal_drains_an_outstanding_lease_before_it_completes()
    {
        using CapabilityFixture fixture = new();

        _ = fixture.Context.TryTake(fixture.Nonce);

        IDisposable lease = fixture.Context.TryAcquireUse(fixture.Nonce).Value;

        ValueTask disposal = fixture.Context.DisposeAsync();

        Assert.False(disposal.IsCompleted);
        Assert.Equal(CovenantToolCapabilityState.Closing, fixture.Context.State);

        // A use that crosses an await resumes into a closing capability and must not proceed.
        Assert.Equal(
            ErrorCodes.Covenant.LifecycleConflict,
            fixture.Context.RecheckBeforeIrreversibleEffect(fixture.Nonce).Error.Code);
        Assert.Equal(
            ErrorCodes.Covenant.LifecycleConflict,
            fixture.Context.TryAcquireUse(fixture.Nonce).Error.Code);

        lease.Dispose();

        await disposal;

        Assert.Equal(CovenantToolCapabilityState.Disposed, fixture.Context.State);
    }

    [Fact]
    public async Task Disposal_is_idempotent_and_leaves_a_spent_capability()
    {
        using CapabilityFixture fixture = new();

        _ = fixture.Context.TryTake(fixture.Nonce);

        await fixture.Context.DisposeAsync();
        await fixture.Context.DisposeAsync();

        Assert.Equal(CovenantToolCapabilityState.Disposed, fixture.Context.State);
        Assert.Equal(
            ErrorCodes.Covenant.LifecycleConflict,
            fixture.Context.TryTake(fixture.Nonce).Error.Code);
    }

    [Fact]
    public async Task A_capability_disposed_before_it_was_taken_can_never_be_taken()
    {
        using CapabilityFixture fixture = new();

        await fixture.Context.DisposeAsync();

        Assert.Equal(CovenantToolCapabilityState.Disposed, fixture.Context.State);
        Assert.Equal(
            ErrorCodes.Covenant.LifecycleConflict,
            fixture.Context.TryTake(fixture.Nonce).Error.Code);
    }

    [Fact]
    public void A_retirement_capability_requires_its_preflight_and_its_ward_receipt()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantMutationCollector collector = new(TurnId, plan.Digest, CovenantTask6Fixture.BranchId);
        CovenantAdmissionReceipt admission = CovenantCapabilityFixtures.Admission(plan);

        Assert.Throws<ArgumentException>(() => new CovenantToolInvocationContext(
            collector,
            CovenantCapabilityFixtures.Campaign(),
            admission,
            CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
            CovenantToolCapabilityNonce.Create(),
            CovenantToolNames.RetireCovenant,
            "call-1",
            retirementPreflight: null,
            wardReceipt: CovenantCapabilityFixtures.WardReceipt(CovenantWardDecision.Approved),
            CancellationToken.None));

        Assert.Throws<ArgumentException>(() => new CovenantToolInvocationContext(
            collector,
            CovenantCapabilityFixtures.Campaign(),
            admission,
            CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
            CovenantToolCapabilityNonce.Create(),
            CovenantToolNames.RetireCovenant,
            "call-1",
            CovenantCapabilityFixtures.RetirementPreflight(),
            wardReceipt: null,
            CancellationToken.None));
    }

    [Fact]
    public void A_proposal_capability_carries_no_ward_receipt_and_no_retirement_target()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantMutationCollector collector = new(TurnId, plan.Digest, CovenantTask6Fixture.BranchId);

        Assert.Throws<ArgumentException>(() => new CovenantToolInvocationContext(
            collector,
            CovenantCapabilityFixtures.Campaign(),
            CovenantCapabilityFixtures.Admission(plan),
            CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
            CovenantToolCapabilityNonce.Create(),
            CovenantToolNames.ProposeCovenant,
            "call-1",
            CovenantCapabilityFixtures.RetirementPreflight(),
            wardReceipt: null,
            CancellationToken.None));
    }

    [Fact]
    public void A_capability_must_bind_the_turn_plan_that_produced_its_admission()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantMutationCollector foreign = new(TurnId, CovenantTask6Fixture.D(3), CovenantTask6Fixture.BranchId);

        Assert.Throws<ArgumentException>(() => new CovenantToolInvocationContext(
            foreign,
            CovenantCapabilityFixtures.Campaign(),
            CovenantCapabilityFixtures.Admission(plan),
            CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
            CovenantToolCapabilityNonce.Create(),
            CovenantToolNames.ProposeCovenant,
            "call-1",
            retirementPreflight: null,
            wardReceipt: null,
            CancellationToken.None));
    }

    [Fact]
    public void A_capability_is_campaign_scoped_because_the_proposed_lane_has_no_global_scope()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantMutationCollector collector = new(TurnId, plan.Digest, CovenantTask6Fixture.BranchId);

        Assert.Throws<ArgumentException>(() => new CovenantToolInvocationContext(
            collector,
            CanonicalCampaignContext.GlobalOnly,
            CovenantCapabilityFixtures.Admission(plan),
            CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
            CovenantToolCapabilityNonce.Create(),
            CovenantToolNames.ProposeCovenant,
            "call-1",
            retirementPreflight: null,
            wardReceipt: null,
            CancellationToken.None));
    }

    [Fact]
    public void A_nonce_compares_by_value_and_never_prints_itself()
    {
        CovenantToolCapabilityNonce nonce = CovenantToolCapabilityNonce.Create();
        CovenantToolCapabilityNonce other = CovenantToolCapabilityNonce.Create();

        Assert.True(nonce.Equals(nonce));
        Assert.False(nonce.Equals(other));
        Assert.False(nonce.Equals(default));
        Assert.True(nonce.IsValid);
        Assert.False(default(CovenantToolCapabilityNonce).IsValid);
        Assert.DoesNotContain(
            Convert.ToHexString(nonce.ToDigest().Bytes),
            nonce.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapabilityFixture : IDisposable
    {

        private readonly CancellationTokenSource _turn = new();

        public CapabilityFixture()
        {
            CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

            Collector = new CovenantMutationCollector(TurnId, plan.Digest, CovenantTask6Fixture.BranchId);

            Nonce = CovenantToolCapabilityNonce.Create();

            Context = new CovenantToolInvocationContext(
                Collector,
                CovenantCapabilityFixtures.Campaign(),
                CovenantCapabilityFixtures.Admission(plan),
                CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
                Nonce,
                CovenantToolNames.ProposeCovenant,
                "call-1",
                retirementPreflight: null,
                wardReceipt: null,
                _turn.Token);
        }

        public CovenantMutationCollector Collector { get; }

        public CovenantToolInvocationContext Context { get; }

        public CovenantToolCapabilityNonce Nonce { get; }

        public void CancelTurn() => _turn.Cancel();

        public void Dispose()
        {
            Context.DisposeAsync().AsTask().GetAwaiter().GetResult();

            _turn.Dispose();
        }

    }

}
