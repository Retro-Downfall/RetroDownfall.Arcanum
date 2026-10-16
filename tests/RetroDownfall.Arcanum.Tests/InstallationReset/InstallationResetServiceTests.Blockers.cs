using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// What a stopped host cannot drain, listed as a blocker before the point of no return with the
/// remedy that is true for its kind.
/// </summary>
public sealed partial class InstallationResetServiceTests
{
    private static readonly DataRetentionCovenantInventory EmptyFamily = new(
        Rows: 0,
        ManagedFiles: 0,
        LocalArtifacts: 0,
        AffectedSessions: 0,
        PossibleDisclosures: 0,
        DisclosureCountKind: CovenantDisclosureCountKind.Exact);

    [Theory]
    [InlineData("Data.ActiveOperation", "StartHostOnceRemedy")]
    [InlineData("Data.BatchInProgress", "StartHostOnceRemedy")]
    [InlineData("Data.InferenceRunActive", "InterruptedWorkRemedy")]
    [InlineData("Data.BudgetReservationOutstanding", "InterruptedWorkRemedy")]
    [InlineData("Data.IdempotencyLeaseActive", "LeaseExpiryRemedy")]
    public async Task A_stopped_host_global_plan_lists_each_undrainable_conflict_with_the_remedy_for_its_kind(
        string code,
        string remedyName)
    {
        string expected = remedyName switch
        {
            "StartHostOnceRemedy" => InstallationResetService.StartHostOnceRemedy,
            "InterruptedWorkRemedy" => InstallationResetService.InterruptedWorkRemedy,
            _ => InstallationResetService.LeaseExpiryRemedy,
        };

        DataRetentionPlan dataPlan = CreateDataPlan("conflicted-data") with
        {
            Covenant = EmptyFamily,
            Conflicts = [new DataRetentionConflict(code, "resource-1", "Something conflicts with this global reset.")],
        };

        InstallationResetService service = CreateService(
            new FakeDataService(dataPlan),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            new FakeOfflineCleanup(),
            stateRoots: new FixedStateRoots(["/state"]));

        Result<StoppedHostInstallationResetPlan> planned = await PlanUnderTestLockAsync(
            service,
            service,
            new InstallationResetPlanRequest(InstallationResetScope.Global, "/invocation"));

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        InstallationResetIssueSummary blocker = Assert.Single(
            planned.Value.Plan.Blockers,
            candidate => candidate.Code == code);

        Assert.Equal("resource-1", blocker.ResourceId);

        Assert.Equal("Something conflicts with this global reset. " + expected, blocker.Message);
    }

    [Theory]
    [InlineData("StandaloneTransition", true)]
    [InlineData("NestedBound", true)]
    [InlineData("NestedReceiptStoredRetirementSuffix", true)]
    [InlineData("NeitherActive", false)]
    [InlineData("NestedNotStarted", false)]
    [InlineData("NestedRetired", false)]
    public async Task A_stopped_host_global_plan_lists_an_unfinished_offline_transition_as_a_blocker(
        string evidenceName,
        bool blocked)
    {
        InstallationResetNestedTransitionEvidenceOutcome evidence =
            Enum.Parse<InstallationResetNestedTransitionEvidenceOutcome>(evidenceName);

        DataRetentionPlan dataPlan = CreateDataPlan("journal-data") with
        {
            Covenant = EmptyFamily,
        };

        FakeActiveStore active = new();

        InstallationResetMaintenanceLockAccessor accessor = new();

        BorrowRecordingStartupRecovery recovery = new(accessor, active.GuardedRoot, evidence);

        FakeDataService data = new(dataPlan);

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            null,
            null,
            new FixedStateRoots(["/state"]),
            null,
            null,
            null,
            CleanPairReader(),
            null,
            new TransitionDeferredServices(recovery, accessor),
            new TestStoppedHostDataService(data, null, active),
            new TestStoppedHostPairReader(CleanPairReader()),
            Path.Combine(active.GuardedRoot, "arcanum.db"));

        Result<StoppedHostInstallationResetPlan> planned = await PlanUnderTestLockAsync(
            service,
            service,
            new InstallationResetPlanRequest(InstallationResetScope.Global, "/invocation"));

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        Assert.Equal(1, recovery.Reads);

        // The held lock was lent to the journal for the length of the read and not a moment longer.
        Assert.True(recovery.LockWasBorrowableDuringTheRead);

        Assert.True(accessor.BorrowHeldLock(active.GuardedRoot).IsFailure);

        InstallationResetIssueSummary[] transitionBlockers =
        [
            .. planned.Value.Plan.Blockers.Where(static blocker =>
                blocker.Code == ErrorCodes.Covenant.LifecycleConflict),
        ];

        if (!blocked)
        {
            Assert.Empty(transitionBlockers);

            return;
        }

        InstallationResetIssueSummary only = Assert.Single(transitionBlockers);

        Assert.EndsWith(InstallationResetService.StartHostOnceRemedy, only.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_conflict_kind_has_exactly_one_remedy_and_no_other_kind_borrows_one()
    {
        Assert.Equal(InstallationResetService.StartHostOnceRemedy, InstallationResetService.RemedyFor("Data.ActiveOperation"));

        Assert.Equal(InstallationResetService.StartHostOnceRemedy, InstallationResetService.RemedyFor("Data.BatchInProgress"));

        Assert.Equal(InstallationResetService.InterruptedWorkRemedy, InstallationResetService.RemedyFor("Data.InferenceRunActive"));

        Assert.Equal(InstallationResetService.InterruptedWorkRemedy, InstallationResetService.RemedyFor("Data.BudgetReservationOutstanding"));

        Assert.Equal(InstallationResetService.LeaseExpiryRemedy, InstallationResetService.RemedyFor("Data.IdempotencyLeaseActive"));

        // What a host start does not settle is said so, and names the procedure that remains.
        Assert.Contains("not settled by a host start", InstallationResetService.InterruptedWorkRemedy, StringComparison.Ordinal);

        Assert.Contains("Local Grimoire reinstall", InstallationResetService.InterruptedWorkRemedy, StringComparison.Ordinal);

        Assert.Contains("does not shorten", InstallationResetService.LeaseExpiryRemedy, StringComparison.Ordinal);
    }

    private sealed class TransitionDeferredServices(
        IInstallationResetStartupRecovery recovery,
        InstallationResetMaintenanceLockAccessor accessor)
        : IInstallationResetDeferredServices
    {
        public IHostToolsMarkerPairResetCoordinator ResolveMarkerPairResetCoordinator() =>
            throw new NotSupportedException();

        public IFullInstallationResetTerminalContinuation ResolveTerminalContinuation() =>
            throw new NotSupportedException();

        public IInstallationResetStartupRecovery? ResolveStartupRecovery() => recovery;

        public InstallationResetMaintenanceLockAccessor? ResolveLockAccessor() => accessor;
    }

    private sealed class BorrowRecordingStartupRecovery(
        InstallationResetMaintenanceLockAccessor accessor,
        string guardedRoot,
        InstallationResetNestedTransitionEvidenceOutcome evidence)
        : IInstallationResetStartupRecovery
    {
        public int Reads { get; private set; }

        public bool LockWasBorrowableDuringTheRead { get; private set; }

        public Task<Result<InstallationResetStartupRecoveryState>> RecoverBeforeBootstrapAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {
            Reads++;

            LockWasBorrowableDuringTheRead = accessor.BorrowHeldLock(guardedRoot).IsSuccess;

            return Task.FromResult(
                Result<InstallationResetStartupRecoveryState>.Success(
                    new InstallationResetStartupRecoveryState(
                        ActiveReset: null,
                        ExpectedInstallationId: null,
                        IsLegacyV1: false,
                        evidence)));
        }
    }
}
