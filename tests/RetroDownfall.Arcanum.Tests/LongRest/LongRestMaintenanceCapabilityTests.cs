using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestMaintenanceCapabilityTests
{
    private static readonly Guid Operation = Guid.Parse("44444444-4444-4444-8444-444444444444");

    private static readonly Guid Boot = Guid.Parse("55555555-5555-4555-8555-555555555555");

    private static readonly LongRestConfigurationStamp Configuration = new(Boot, 7, Digest(1));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_and_child_capabilities_are_single_use_and_exact_scope(bool campaignBound)
    {
        CanonicalCampaignContext context = campaignBound ? CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne) : CanonicalCampaignContext.GlobalOnly;

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(context, CancellationToken.None)).Value;

        LongRestMaintenanceCapability capability = (await CreateAsync(context, authority)).Value;

        LongRestSweepScope scope = Scope(context);

        Assert.True((await capability.ConsumeAsync(Operation, scope, Configuration, CancellationToken.None)).IsSuccess);

        Assert.True((await capability.ConsumeAsync(Operation, scope, Configuration, CancellationToken.None)).IsFailure);

        LongRestWorkIdentity work = Work(context, authority);

        LongRestDispatchCapability child = (await capability.CreateDispatchAsync(work, Digest(3), Digest(4), 1, Configuration, CancellationToken.None)).Value;

        Assert.True((await child.ConsumeAsync(work, Digest(3), Digest(4), 1, Configuration, CancellationToken.None)).IsSuccess);

        Assert.True((await child.ConsumeAsync(work, Digest(3), Digest(4), 1, Configuration, CancellationToken.None)).IsFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scope_cross_use_and_changed_owner_facts_are_refused(bool campaignBound)
    {
        CanonicalCampaignContext context = campaignBound ? CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne) : CanonicalCampaignContext.GlobalOnly;

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(context, CancellationToken.None)).Value;

        LongRestMaintenanceCapability capability = (await CreateAsync(context, authority)).Value;

        LongRestSweepScope wrong = Scope(campaignBound ? CanonicalCampaignContext.GlobalOnly : CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne));

        Assert.True((await capability.ConsumeAsync(Operation, wrong, Configuration, CancellationToken.None)).IsFailure);

        Assert.True((await capability.ConsumeAsync(Operation, Scope(context), Configuration, CancellationToken.None)).IsSuccess);

        LongRestWorkIdentity work = Work(context, authority);

        Assert.True((await capability.CreateDispatchAsync(work with { Scope = wrong }, Digest(3), Digest(4), 1, Configuration, CancellationToken.None)).IsFailure);

        Assert.True((await capability.CreateDispatchAsync(work, Digest(3), Digest(4), 1, Configuration with { Epoch = 8 }, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Forged_canonical_owner_and_released_lease_cannot_issue_or_dispatch()
    {
        CanonicalCampaignContext real = CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne);

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(real, CancellationToken.None)).Value;

        CanonicalCampaignContext forged = CanonicalCampaignContext.Create(real.Binding, 6, 1, 9, Digest(21));

        Assert.True((await CreateAsync(forged, authority)).IsFailure);

        LongRestMaintenanceCapability capability = (await CreateAsync(real, authority)).Value;

        Assert.True((await capability.ConsumeAsync(Operation, Scope(real), Configuration, CancellationToken.None)).IsSuccess);

        LongRestWorkIdentity work = Work(real, authority);

        LongRestDispatchCapability child = (await capability.CreateDispatchAsync(work, Digest(3), Digest(4), 1, Configuration, CancellationToken.None)).Value;

        Assert.True((await child.ConsumeAsync(work, Digest(3), Digest(4), 2, Configuration, CancellationToken.None)).IsFailure);

        await authority.DisposeAsync();

        Assert.True((await CreateAsync(real, authority)).IsFailure);

        Assert.True((await child.ConsumeAsync(work, Digest(3), Digest(4), 1, Configuration, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Preview_background_and_expired_claim_cannot_issue_capabilities()
    {
        CanonicalCampaignContext context = CanonicalCampaignContext.GlobalOnly;

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(context, CancellationToken.None)).Value;

        Assert.True((await LongRestMaintenanceCapability.CreateAsync(Operation, ArcanumInvocationContext.None, Claim(), context, authority, Configuration, CancellationToken.None)).IsFailure);

        ArcanumInvocationContext preview = Invocation(context, authority, ArcanumExecutionSurface.ContextInspection);

        Assert.True((await LongRestMaintenanceCapability.CreateAsync(Operation, preview, Claim(), context, authority, Configuration, CancellationToken.None)).IsFailure);

        Assert.True((await LongRestMaintenanceCapability.CreateAsync(Operation, Invocation(context, authority), Claim() with { LeaseDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, context, authority, Configuration, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Durable_renewal_preserves_owner_and_allows_work_past_the_initial_deadline()
    {
        CanonicalCampaignContext context = CanonicalCampaignContext.GlobalOnly;

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(context, CancellationToken.None)).Value;

        ControlledTime clock = new(DateTimeOffset.UtcNow);

        SessionTurnClaimLease original = Claim() with { LeaseDeadlineUtc = clock.GetUtcNow().AddMinutes(1) };

        original = original with { Claim = original.Claim with { CheckpointRevision = 2, ExpectedCurrentSensitivityRevision = 3 } };

        LongRestMaintenanceCapability capability = (await LongRestMaintenanceCapability.CreateAsync(Operation, Invocation(context, authority), original, context, authority, Configuration, CancellationToken.None, clock)).Value;

        SessionTurnClaimLease renewed = original with { LeaseDeadlineUtc = clock.GetUtcNow().AddMinutes(5), Claim = original.Claim with { CheckpointRevision = 3, ExpectedCurrentSensitivityRevision = 4 } };

        Assert.True((await capability.RefreshClaimAsync(renewed, Configuration, CancellationToken.None)).IsSuccess);

        Assert.Equal(renewed.LeaseDeadlineUtc, capability.Claim.LeaseDeadlineUtc);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.True((await capability.ConsumeAsync(Operation, Scope(context), Configuration, CancellationToken.None)).IsSuccess);

        Assert.True((await capability.RefreshClaimAsync(renewed with { ExecutorId = Guid.NewGuid() }, Configuration, CancellationToken.None)).IsFailure);

        Assert.True((await capability.RefreshClaimAsync(renewed with { Claim = renewed.Claim with { ClaimId = Guid.NewGuid() } }, Configuration, CancellationToken.None)).IsFailure);

        Assert.True((await capability.RefreshClaimAsync(renewed with { Claim = renewed.Claim with { CheckpointRevision = 2 } }, Configuration, CancellationToken.None)).IsFailure);

        Assert.True((await capability.RefreshClaimAsync(renewed with { Claim = renewed.Claim with { ExpectedCurrentSensitivityRevision = 3 } }, Configuration, CancellationToken.None)).IsFailure);

        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.True((await capability.RefreshClaimAsync(renewed with { LeaseDeadlineUtc = clock.GetUtcNow().AddMinutes(1) }, Configuration, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Revoked_registration_refuses_claim_refresh_and_new_children()
    {
        CanonicalCampaignContext context = CanonicalCampaignContext.GlobalOnly;

        FakeCovenantAuthorityProvider published = new();

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate(authority: published).AcquireTurnAsync(context, CancellationToken.None)).Value;

        LongRestMaintenanceCapability capability = (await CreateAsync(context, authority)).Value;

        Assert.True((await capability.ConsumeAsync(Operation, Scope(context), Configuration, CancellationToken.None)).IsSuccess);

        published.Advance();

        Assert.True((await capability.RefreshClaimAsync(capability.Claim with { LeaseDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, Configuration, CancellationToken.None)).IsFailure);

        Assert.True((await capability.CreateDispatchAsync(Work(context, authority), Digest(3), Digest(4), 1, Configuration, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Validation_uses_the_renewal_published_before_the_original_deadline()
    {
        CanonicalCampaignContext context = CanonicalCampaignContext.GlobalOnly;

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(context, CancellationToken.None)).Value;

        DateTimeOffset started = DateTimeOffset.UtcNow;

        InterleavedTime clock = new(started);

        SessionTurnClaimLease original = Claim() with { LeaseDeadlineUtc = started.AddMinutes(1) };

        LongRestMaintenanceCapability capability = (await LongRestMaintenanceCapability.CreateAsync(Operation, Invocation(context, authority), original, context, authority, Configuration, CancellationToken.None, clock)).Value;

        SessionTurnClaimLease renewed = original with { LeaseDeadlineUtc = started.AddMinutes(5) };

        clock.BeforeNextRead = () =>
        {
            clock.Now = started.AddSeconds(30);

            Assert.True(capability.RefreshClaimAsync(renewed, Configuration, CancellationToken.None).GetAwaiter().GetResult().IsSuccess);

            clock.Now = started.AddMinutes(2);
        };

        Assert.True((await capability.ValidateAsync(Configuration, CancellationToken.None)).IsSuccess);

        Assert.Equal(renewed, capability.Claim);

        clock.Now = started.AddMinutes(6);

        Assert.True((await capability.ValidateAsync(Configuration, CancellationToken.None)).IsFailure);
    }

    private sealed class InterleavedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public Action? BeforeNextRead;

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref BeforeNextRead, null)?.Invoke();

            return Now;
        }
    }

    private sealed class ControlledTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private static Task<RetroDownfall.Arcanum.Core.Primitives.Result<LongRestMaintenanceCapability>> CreateAsync(CanonicalCampaignContext context, CovenantTurnLease authority) =>
        LongRestMaintenanceCapability.CreateAsync(Operation, Invocation(context, authority), Claim(), context, authority, Configuration, CancellationToken.None);

    private static ArcanumInvocationContext Invocation(CanonicalCampaignContext context, CovenantTurnLease authority, ArcanumExecutionSurface surface = ArcanumExecutionSurface.SessionBackedOperatorTurn) =>
        ArcanumInvocationContext.Create(surface, context, InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools, CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), authority.Snapshot.RuntimeAuthorityGeneration, authority.Snapshot.AuthorityEpoch)).Value;

    private static SessionTurnClaimLease Claim()
    {
        SessionTurnClaim claim = new(Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), Guid.NewGuid(), SessionTurnSurface.Intelligence, Digest(1), Digest(2), SessionTurnClaimState.PendingMaintenance, null, 17, 0, 0, Guid.NewGuid(), null, null, Boot, 0, 0, null, DateTimeOffset.UtcNow);

        return new(claim, SessionTurnClaimDisposition.Created, Guid.NewGuid(), Guid.NewGuid(), Boot, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static LongRestSweepScope Scope(CanonicalCampaignContext context) =>
        LongRestSweepScope.Create(new(context.IsCampaignBound ? LongRestSweepScopeKind.Campaign : LongRestSweepScopeKind.Global, context.CampaignId)).Value;

    private static LongRestWorkIdentity Work(CanonicalCampaignContext context, CovenantTurnLease authority) =>
        new(Guid.Parse("66666666-6666-4666-8666-666666666666"), Operation, "fold-1", LongRestWorkKind.HierarchyFold, Scope(context), new(context, 128, authority.Snapshot.DatasetGeneration!.Value, 1, authority.Snapshot.RuntimeAuthorityGeneration, authority.Snapshot.CapabilityGeneration, Configuration), Digest(1), Digest(2));

    private static CovenantDigest Digest(byte seed) => CovenantOperationGateFixture.Digest(seed);
}
