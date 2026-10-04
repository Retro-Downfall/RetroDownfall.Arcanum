using System.Collections.Immutable;
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

    /// <summary>
    /// Disposal cancels the closing token and then waits for every outstanding use to drain. A probe
    /// that was handed only its caller's token never observes that cancellation, so a probe that
    /// blocks until cancelled stalls disposal -- and with it the turn's teardown -- for as long as the
    /// caller's own token lives. Each probe has to be bound to the capability's closing token too.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Disposal_cancels_a_pending_probe(int probeKind)
    {
        BlockingHeadProbe probe = new();

        using CapabilityFixture fixture = new(probe);

        _ = fixture.Context.TryTake(fixture.Nonce);

        // The caller's own token is never cancelled: only the capability closing can release the probe.
        using CancellationTokenSource caller = new();

        try
        {
            Task pending = probeKind switch
            {
                0 => fixture.Context.ProbeLaneHeadAsync(
                    fixture.Nonce,
                    CovenantLane.Proposed,
                    "some.key",
                    caller.Token).AsTask(),
                1 => fixture.Context.ProbeSectionAsync(
                    fixture.Nonce,
                    CovenantLane.Proposed,
                    [],
                    caller.Token).AsTask(),
                _ => fixture.Context.ProbeScopeAsync(fixture.Nonce, [], caller.Token).AsTask()
            };

            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await fixture.Context.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            // Releases the probe if the assertion above failed, so fixture cleanup cannot hang.
            await caller.CancelAsync();

            probe.Release();
        }
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
    public async Task A_retirement_capability_requires_exactly_its_preflight()
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();
        CovenantMutationCollector collector = new(TurnId, plan.Digest, CovenantTask6Fixture.BranchId);
        CovenantAdmissionReceipt admission = CovenantCapabilityFixtures.Admission(plan);

        CovenantToolInvocationContext retirement = new(
            collector,
            CovenantCapabilityFixtures.Campaign(),
            admission,
            CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
            CovenantToolCapabilityNonce.Create(),
            CovenantToolNames.RetireCovenant,
            "call-1",
            CovenantCapabilityFixtures.RetirementPreflight(),
            CancellationToken.None);

        Assert.NotNull(retirement.RetirementPreflight);

        await retirement.DisposeAsync();

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
            CancellationToken.None));
    }

    [Fact]
    public async Task A_proposal_capability_carries_no_retirement_target()
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
            CancellationToken.None));

        CovenantToolInvocationContext proposal = new(
            collector,
            CovenantCapabilityFixtures.Campaign(),
            CovenantCapabilityFixtures.Admission(plan),
            CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
            CovenantToolCapabilityNonce.Create(),
            CovenantToolNames.ProposeCovenant,
            "call-1",
            retirementPreflight: null,
            CancellationToken.None);

        Assert.Null(proposal.RetirementPreflight);

        await proposal.DisposeAsync();
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

    /// <summary>A probe that does not answer until its token is cancelled or it is released.</summary>
    private sealed class BlockingHeadProbe : ICovenantTurnHeadProbe
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask<Result<CovenantLaneHeadProbe>> ProbeAsync(
            CovenantLane lane,
            string normalizedKey,
            CancellationToken cancellationToken)
        {
            await BlockAsync(cancellationToken);

            return Result<CovenantLaneHeadProbe>.Failure(Released());
        }

        public async ValueTask<Result<CovenantSectionOccupancy>> ProbeSectionAsync(
            CovenantLane lane,
            ImmutableArray<string> excludedKeys,
            CancellationToken cancellationToken)
        {
            await BlockAsync(cancellationToken);

            return Result<CovenantSectionOccupancy>.Failure(Released());
        }

        public async ValueTask<Result<CovenantQuotaSnapshot>> ProbeScopeAsync(
            ImmutableArray<string> excludedKeys,
            CancellationToken cancellationToken)
        {
            await BlockAsync(cancellationToken);

            return Result<CovenantQuotaSnapshot>.Failure(Released());
        }

        public ValueTask<Result<CovenantRetirementPreflight>> ResolveRetirementPreflightAsync(
            CovenantLane lane,
            string normalizedKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private async Task BlockAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();

            await _release.Task.WaitAsync(cancellationToken);
        }

        private static Error Released() =>
            new(ErrorCodes.Covenant.LifecycleConflict, "The blocking probe was released.");
    }

    private sealed class CapabilityFixture : IDisposable
    {
        private readonly CancellationTokenSource _turn = new();

        public CapabilityFixture(ICovenantTurnHeadProbe? headProbe = null)
        {
            CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

            Collector = new CovenantMutationCollector(TurnId, plan.Digest, CovenantTask6Fixture.BranchId);

            Nonce = CovenantToolCapabilityNonce.Create();

            Context = new CovenantToolInvocationContext(
                Collector,
                CovenantCapabilityFixtures.Campaign(),
                CovenantCapabilityFixtures.Admission(plan),
                CovenantCapabilityFixtures.Materialization(),
            headProbe ?? new CovenantCapabilityFixtures.StubHeadProbe(),
                Nonce,
                CovenantToolNames.ProposeCovenant,
                "call-1",
                retirementPreflight: null,
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
