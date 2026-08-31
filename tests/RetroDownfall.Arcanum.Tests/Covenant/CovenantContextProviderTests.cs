using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The gate order between an invocation and a turn plan (§10.13).
/// </summary>
/// <remarks>
/// Every case here asserts on a strict gate that throws if it is reached. That is the property under
/// test: an ineligible caller, a disabled feature, or an unhealthy tier has to be answered before
/// anything takes a lease or opens a read, because even the shape of a later failure is an answer
/// about Covenant state.
/// </remarks>
public sealed class CovenantContextProviderTests
{

    private static readonly Guid TurnId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task BeginTurnAsync_RefusesEveryInvocationThatCannotReadCovenant()
    {
        CovenantContextProvider provider = Provider(Availability());

        CovenantTurnContext context = await Begin(provider, ArcanumInvocationContext.None);

        Assert.False(context.HasPlan);
        Assert.Equal(CovenantTurnAbsence.NotEligible, context.Absence);
        Assert.Null(context.Collector);
    }

    [Fact]
    public async Task BeginTurnAsync_ReportsADisabledFeatureBeforeTakingALease()
    {
        CovenantContextProvider provider = Provider(Availability(featureEnabled: false));

        CovenantTurnContext context = await Begin(provider, InvocationContexts.AttendedSession());

        Assert.Equal(CovenantTurnAbsence.FeatureDisabled, context.Absence);
        Assert.Equal(CovenantPromptContent.None, context.PlanContent);
    }

    [Fact]
    public async Task BeginTurnAsync_ReportsAnUnhealthyCanonicalTierBeforeTakingALease()
    {
        CovenantContextProvider provider = Provider(
            Availability(canonical: CovenantCapabilityState.Degraded));

        CovenantTurnContext context = await Begin(provider, InvocationContexts.AttendedSession());

        Assert.Equal(CovenantTurnAbsence.CapabilityUnavailable, context.Absence);
    }

    [Fact]
    public async Task PlanContent_OfAnAbsentContextIsExactlyTheEmptyRendering()
    {
        CovenantTurnContext context = CovenantTurnContext.Absent(CovenantTurnAbsence.Empty);

        Assert.True(context.PlanContent.IsEmpty);
        Assert.Equal(string.Empty, context.PlanContent.GlobalConfirmed);
        Assert.Equal(string.Empty, context.PlanContent.CampaignConfirmed);
        Assert.Equal(string.Empty, context.PlanContent.CampaignProposed);

        await context.DisposeAsync();
    }

    [Fact]
    public void Absent_RefusesToClaimAPlanItDoesNotHave() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            static () => CovenantTurnContext.Absent(CovenantTurnAbsence.None));

    [Fact]
    public async Task A_turn_epoch_from_an_older_runtime_generation_is_refused_before_store_access()
    {

        RecordingLeaseRegistration registration = new(runtimeAuthorityGeneration: 2);

        RecordingStore store = new();

        CovenantContextProvider provider = new(
            new StubAvailability(Availability()),
            new UnreachableGate(registration),
            store,
            new CovenantLinker());

        Result<CovenantTurnContext> result = await provider.BeginTurnAsync(
            InvocationContexts.AttendedSession(),
            TurnId,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);

        Assert.Equal(0, store.Reads);

        Assert.Equal(1, registration.Releases);

    }

    [Fact]
    public async Task A_matching_turn_epoch_retains_the_real_lease_until_the_turn_context_is_disposed()
    {

        FakeCovenantAvailability availability = new();

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            availability,
            authority);

        CovenantAuthoritySnapshot currentAuthority = authority.Current!;

        CovenantReadAuthorityEpoch epoch = CovenantReadAuthorityEpoch.CreateForTests(
            Guid.Parse(currentAuthority.InstallationIdentity),
            currentAuthority.RuntimeAuthorityGeneration,
            currentAuthority.AuthorityEpoch);

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne),
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            epoch).Value;

        CovenantContextProvider provider = new(
            availability,
            gate,
            new SuccessfulStore(),
            new CovenantLinker());

        Result<CovenantTurnContext> begun = await provider.BeginTurnAsync(
            invocation,
            TurnId,
            CancellationToken.None);

        Assert.True(begun.IsSuccess);

        CovenantTurnContext context = begun.Value;

        Assert.True(context.HasPlan);

        Assert.Equal(1, gate.LiveRegistrationCount);

        Task<Result<CovenantExclusiveLease>> closing = gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            CancellationToken.None).AsTask();

        try
        {

            Assert.False(closing.IsCompleted);

            Assert.True((await context.RevalidateAsync(CancellationToken.None)).IsFailure);

            Assert.Equal(1, gate.LiveRegistrationCount);

        }
        finally
        {

            await context.DisposeAsync();

        }

        Result<CovenantExclusiveLease> closed = await closing;

        Assert.True(closed.IsSuccess);

        Assert.Equal(0, gate.LiveRegistrationCount);

        await using CovenantExclusiveLease lease = closed.Value;

        Assert.True((await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

    }

    [Fact]
    public async Task An_eligible_unattended_turn_retains_retirement_preparation_material()
    {

        FakeCovenantAvailability availability = new();

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            availability,
            authority);

        CovenantAuthoritySnapshot currentAuthority = authority.Current!;

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne),
            InvocationAttendance.Unattended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            CovenantReadAuthorityEpoch.CreateForTests(
                Guid.Parse(currentAuthority.InstallationIdentity),
                currentAuthority.RuntimeAuthorityGeneration,
                currentAuthority.AuthorityEpoch)).Value;

        Assert.False(invocation.CanStageCovenantMutation);

        Assert.True(invocation.CanPrepareCovenantRetirement);

        CovenantContextProvider provider = new(
            availability,
            gate,
            new SuccessfulStore(),
            new CovenantLinker());

        Result<CovenantTurnContext> begun = await provider.BeginTurnAsync(
            invocation,
            TurnId,
            CancellationToken.None);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : string.Empty);

        await using CovenantTurnContext context = begun.Value;

        Assert.True(context.HasPlan);

        Assert.NotNull(context.Collector);

        Assert.NotNull(context.HeadProbe);

    }

    private static Task<CovenantTurnContext> Begin(
        CovenantContextProvider provider,
        ArcanumInvocationContext invocation) =>
        BeginCoreAsync(provider, invocation);

    private static async Task<CovenantTurnContext> BeginCoreAsync(
        CovenantContextProvider provider,
        ArcanumInvocationContext invocation)
    {
        Result<CovenantTurnContext> result = await provider.BeginTurnAsync(
            invocation,
            TurnId,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;
    }

    private static CovenantContextProvider Provider(CovenantAvailabilitySnapshot snapshot) =>
        new(
            new StubAvailability(snapshot),
            new UnreachableGate(),
            new UnreachableStore(),
            new CovenantLinker());

    private static CovenantAvailabilitySnapshot Availability(
        bool featureEnabled = true,
        CovenantCapabilityState canonical = CovenantCapabilityState.Healthy) =>
        new(
            Generation: 1,
            FeatureEnabled: featureEnabled,
            Canonical: canonical,
            CanonicalSchemaVersion: 1,
            CanonicalInstalledFingerprint: "fingerprint",
            Accelerator: CovenantCapabilityState.Healthy,
            AcceleratorSchemaVersion: 1,
            AcceleratorInstalledFingerprint: "fingerprint",
            DatasetGeneration: CovenantTask6Fixture.DatasetGeneration,
            CanonicalSequence: 0,
            CoreCampaignDeletionSequence: 0,
            AppliedDatasetGeneration: CovenantTask6Fixture.DatasetGeneration,
            AppliedSequence: 0,
            AppliedCampaignDeletionSequence: 0,
            AcceleratorEpoch: 1,
            FtsSynchronization: CovenantFtsSynchronizationState.Synchronized,
            RebuildRequired: false,
            LastHealthTransition: CovenantHealthTransition.Bootstrap,
            CanonicalDiagnosticCode: null,
            AcceleratorDiagnosticCode: null);

    private sealed class StubAvailability(CovenantAvailabilitySnapshot snapshot) : ICovenantAvailability
    {

        public CovenantAvailabilitySnapshot Current => snapshot;

    }

    private sealed class UnreachableGate(ICovenantLeaseRegistration? turn = null) : ICovenantOperationGate
    {

        public ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantReadLease>> AcquireReadAsync(CovenantOperationScope scope, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(CovenantOperationScope scope, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(CanonicalCampaignContext campaign, CancellationToken cancellationToken) =>
            turn is null
                ? throw new UnreachableException()
                : ValueTask.FromResult(Result<CovenantTurnLease>.Success(new CovenantTurnLease(turn)));

        public ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(CovenantOperationScope scope, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(CovenantOperationScope scope, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(Guid campaignId, CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(ProtectedTransferScope scope, CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantExclusiveLease>> ResumeOrAcquireExclusiveAsync(CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(Guid campaignId, CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(ProtectedTransferScope scope, CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(CovenantExclusiveRecoveryOwner owner, CancellationToken cancellationToken) =>
            throw new UnreachableException();

    }

    private sealed class RecordingLeaseRegistration(long runtimeAuthorityGeneration) : ICovenantLeaseRegistration
    {

        public int Releases { get; private set; }

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            RegistrationId: Guid.Parse("11111111-2222-4333-8444-555555555555"),
            runtimeAuthorityGeneration,
            CovenantLeaseKind.Turn,
            CovenantLeaseCoverage.Scoped,
            CovenantOperationScope.Global,
            CovenantTask6Fixture.DatasetGeneration,
            CapabilityGeneration: 1,
            AuthorityEpoch: 11,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: 1,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync()
        {

            Releases++;

            return ValueTask.CompletedTask;

        }

    }

    private sealed class RecordingStore : ICovenantStore
    {

        public ValueTask<Result<CovenantScopeCensus>> ReadScopeCensusAsync(
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public int Reads { get; private set; }

        public ValueTask<Result<CovenantTurnSnapshot>> ReadTurnSnapshotAsync(
            CanonicalCampaignContext campaign,
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken)
        {

            Reads++;

            throw new UnreachableException();

        }

        public ValueTask<Result<CovenantLaneHeadProbe>> ProbeLaneHeadAsync(CanonicalCampaignContext campaign, CovenantLane lane, string normalizedKey, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantListPage>> ReadListPageAsync(CovenantListQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantDetail>> ReadDetailAsync(CovenantDetailQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantVersionPage>> ReadVersionPageAsync(CovenantVersionQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantSourcePage>> ReadSourcePageAsync(CovenantSourceQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantRetirementTarget>> ReadRetirementTargetAsync(CanonicalCampaignContext campaign, CovenantLane lane, string normalizedKey, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantCurationEffectSnapshot>> ReadCurationEffectSnapshotAsync(CovenantCurationEffectQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantMutationEffectSnapshot>> ReadMutationEffectSnapshotAsync(CovenantMutationEffectQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantSectionOccupancy>> ReadSectionOccupancyAsync(CovenantSectionOccupancyQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantQuotaSnapshot>> ReadQuotaSnapshotAsync(CovenantOperationScope scope, ImmutableArray<string> excludedKeys, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

    }

    private sealed class SuccessfulStore : ICovenantStore
    {

        public ValueTask<Result<CovenantScopeCensus>> ReadScopeCensusAsync(
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantTurnSnapshot>> ReadTurnSnapshotAsync(
            CanonicalCampaignContext campaign,
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantTurnSnapshot>.Success(
                new CovenantTurnSnapshot(
                    new CovenantGenerationId(CovenantOperationGateFixture.DatasetGeneration),
                    CovenantTask6Fixture.KeyReclamationEpoch,
                    CovenantOperationGateFixture.CampaignOne,
                    canonicalSearchSequence: 12,
                    [])));

        public ValueTask<Result<CovenantLaneHeadProbe>> ProbeLaneHeadAsync(CanonicalCampaignContext campaign, CovenantLane lane, string normalizedKey, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantListPage>> ReadListPageAsync(CovenantListQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantDetail>> ReadDetailAsync(CovenantDetailQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantVersionPage>> ReadVersionPageAsync(CovenantVersionQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantSourcePage>> ReadSourcePageAsync(CovenantSourceQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantRetirementTarget>> ReadRetirementTargetAsync(CanonicalCampaignContext campaign, CovenantLane lane, string normalizedKey, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantCurationEffectSnapshot>> ReadCurationEffectSnapshotAsync(CovenantCurationEffectQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantMutationEffectSnapshot>> ReadMutationEffectSnapshotAsync(CovenantMutationEffectQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantSectionOccupancy>> ReadSectionOccupancyAsync(CovenantSectionOccupancyQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantQuotaSnapshot>> ReadQuotaSnapshotAsync(CovenantOperationScope scope, ImmutableArray<string> excludedKeys, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

    }

    private sealed class UnreachableStore : ICovenantStore
    {

        public ValueTask<Result<CovenantScopeCensus>> ReadScopeCensusAsync(
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantTurnSnapshot>> ReadTurnSnapshotAsync(CanonicalCampaignContext campaign, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantLaneHeadProbe>> ProbeLaneHeadAsync(CanonicalCampaignContext campaign, CovenantLane lane, string normalizedKey, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantListPage>> ReadListPageAsync(CovenantListQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantDetail>> ReadDetailAsync(CovenantDetailQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantVersionPage>> ReadVersionPageAsync(CovenantVersionQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantSourcePage>> ReadSourcePageAsync(CovenantSourceQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantRetirementTarget>> ReadRetirementTargetAsync(CanonicalCampaignContext campaign, CovenantLane lane, string normalizedKey, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantCurationEffectSnapshot>> ReadCurationEffectSnapshotAsync(CovenantCurationEffectQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) => throw new UnreachableException();

        public ValueTask<Result<CovenantMutationEffectSnapshot>> ReadMutationEffectSnapshotAsync(CovenantMutationEffectQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantSectionOccupancy>> ReadSectionOccupancyAsync(CovenantSectionOccupancyQuery query, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public ValueTask<Result<CovenantQuotaSnapshot>> ReadQuotaSnapshotAsync(CovenantOperationScope scope, ImmutableArray<string> excludedKeys, ICovenantSnapshotReadLease readLease, CancellationToken cancellationToken) =>
            throw new UnreachableException();

    }

    private sealed class UnreachableException()
        : InvalidOperationException("The provider reached a dependency it must answer before.");

}
