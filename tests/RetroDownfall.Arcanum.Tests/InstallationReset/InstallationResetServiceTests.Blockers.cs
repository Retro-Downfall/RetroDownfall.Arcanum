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
    [InlineData("Data.OperationNeedsAttention", "OperatorSettlesRemedy")]
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
            "OperatorSettlesRemedy" => InstallationResetService.OperatorSettlesRemedy,
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

        RecordingStartupRecovery recovery = new(evidence);

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
            new TransitionDeferredServices(
                recovery,
                new StoppedHostLockLender(accessor, Path.Combine(active.GuardedRoot, "arcanum.db"))),
            new TestStoppedHostDataService(data, null, active),
            new TestStoppedHostPairReader(CleanPairReader()),
            Path.Combine(active.GuardedRoot, "arcanum.db"));

        Result<StoppedHostInstallationResetPlan> planned = await PlanUnderTestLockAsync(
            service,
            service,
            new InstallationResetPlanRequest(InstallationResetScope.Global, "/invocation"));

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        // A plan is a dry run: it asks what recovery would find and never runs recovery, which renames
        // and deletes journal files, advances anchors and removes leftover credentials.
        Assert.Equal(1, recovery.Inspections);

        Assert.Equal(0, recovery.Recoveries);

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

        Assert.Equal(
            InstallationResetService.OperatorSettlesRemedy,
            InstallationResetService.RemedyFor(ErrorCodes.Data.OperationNeedsAttention));

        Assert.Equal(InstallationResetService.StartHostOnceRemedy, InstallationResetService.RemedyFor("Data.BatchInProgress"));

        Assert.Equal(InstallationResetService.InterruptedWorkRemedy, InstallationResetService.RemedyFor("Data.InferenceRunActive"));

        Assert.Equal(InstallationResetService.InterruptedWorkRemedy, InstallationResetService.RemedyFor("Data.BudgetReservationOutstanding"));

        Assert.Equal(InstallationResetService.LeaseExpiryRemedy, InstallationResetService.RemedyFor("Data.IdempotencyLeaseActive"));

        // What a host start does not settle is said so, and names the procedure that remains.
        Assert.Contains("not settled by a host start", InstallationResetService.InterruptedWorkRemedy, StringComparison.Ordinal);

        Assert.Contains("Local Grimoire reinstall", InstallationResetService.InterruptedWorkRemedy, StringComparison.Ordinal);

        // It names what that procedure costs, and the one case it is for: a host killed mid-turn.
        Assert.Contains("killed mid-turn", InstallationResetService.InterruptedWorkRemedy, StringComparison.Ordinal);

        Assert.Contains("without the protected erasure", InstallationResetService.InterruptedWorkRemedy, StringComparison.Ordinal);

        Assert.Contains("does not shorten", InstallationResetService.LeaseExpiryRemedy, StringComparison.Ordinal);

        // An operation that waits for an operator is not cleared by starting the host alone: the remedy
        // says what does clear it, with the two commands that exist for it, and it is not the remedy of
        // an operation a host start does settle.
        Assert.NotEqual(InstallationResetService.StartHostOnceRemedy, InstallationResetService.OperatorSettlesRemedy);

        Assert.Contains("`arcanum operation cancel <id>`", InstallationResetService.OperatorSettlesRemedy, StringComparison.Ordinal);

        Assert.Contains("`arcanum operation retry <id>`", InstallationResetService.OperatorSettlesRemedy, StringComparison.Ordinal);

        Assert.Contains("`arcanum serve quit`", InstallationResetService.OperatorSettlesRemedy, StringComparison.Ordinal);
    }

    private sealed class TransitionDeferredServices(
        IInstallationResetStartupRecovery recovery,
        StoppedHostLockLender lender)
        : IInstallationResetDeferredServices
    {
        public IHostToolsMarkerPairResetCoordinator ResolveMarkerPairResetCoordinator() =>
            throw new NotSupportedException();

        public IFullInstallationResetTerminalContinuation ResolveTerminalContinuation() =>
            throw new NotSupportedException();

        public IInstallationResetStartupRecovery? ResolveStartupRecovery() => recovery;

        public StoppedHostLockLender? ResolveStoppedHostLockLender() => lender;
    }

    private sealed class RecordingStartupRecovery(
        InstallationResetNestedTransitionEvidenceOutcome evidence)
        : IInstallationResetStartupRecovery
    {
        public int Inspections { get; private set; }

        public int Recoveries { get; private set; }

        public Task<Result<InstallationResetStartupRecoveryState>> RecoverBeforeBootstrapAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {
            Recoveries++;

            return Task.FromResult(Report());
        }

        public Task<Result<InstallationResetStartupRecoveryState>> InspectPairAsync(
            CancellationToken cancellationToken = default)
        {
            Inspections++;

            return Task.FromResult(Report());
        }

        private Result<InstallationResetStartupRecoveryState> Report() =>
            Result<InstallationResetStartupRecoveryState>.Success(
                new InstallationResetStartupRecoveryState(
                    ActiveReset: null,
                    ExpectedInstallationId: null,
                    IsLegacyV1: false,
                    evidence));
    }
}
