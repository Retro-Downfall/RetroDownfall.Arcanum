using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Arcanum.Tests.Coordination;

[Collection("WorkspacePathPolicy")]
public sealed class InstallationMaintenanceCoordinationTests : IDisposable
{

    private readonly string _container;

    private readonly string _guardedRoot;

    public InstallationMaintenanceCoordinationTests()
    {

        _container = Path.Combine(
            Path.GetTempPath(),
            "arcanum-installation-coordination-" + Guid.NewGuid().ToString("N"));

        _guardedRoot = Path.Combine(_container, "arcanum");

        Directory.CreateDirectory(_container);

    }

    public void Dispose()
    {

        if (Directory.Exists(_container))
        {

            Directory.Delete(_container, recursive: true);

        }

    }

    [Fact]
    public async Task Reset_owner_publishes_the_durable_blocker_under_the_retained_mutex()
    {

        InstallationMaintenanceCoordination coordinator = Coordinator();

        InstallationMaintenanceCoordinationResult acquired = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.All,
                "accepted-plan",
                operationId: null,
                CancellationToken.None);

        Assert.Equal(
            InstallationMaintenanceCoordinationDisposition.Acquired,
            acquired.Disposition);

        await using InstallationMaintenanceCoordinationLease held =
            acquired.BorrowAcquiredLease();

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Contended,
            ArcanumClientMutationLock.AcquireDetailed(_guardedRoot).Disposition);

        ClientMutationBlockerPublication publication = Assert.IsType<
            ClientMutationBlockerPublication>(
            (await new ClientMutationBlockerStore(_guardedRoot)
                .InspectAsync()).Value);

        Assert.Equal(
            ClientMutationBlockerKind.InstallationReset,
            publication.Record.Kind);

        Assert.Equal("accepted-plan", publication.Record.PlanId);

    }

    [Fact]
    public async Task Matching_durable_reset_blocker_is_reauthenticated_and_adopted_on_resume()
    {

        MutableResetProbe reset = new(active: null);

        InstallationMaintenanceCoordination coordinator = Coordinator(
            reset,
            new MutableRestoreProbe(active: false));

        InstallationMaintenanceCoordinationResult first = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.All,
                "accepted-plan",
                operationId: null,
                CancellationToken.None);

        ClientMutationBlockerRecord expected;

        await using (InstallationMaintenanceCoordinationLease lease =
                     first.BorrowAcquiredLease())
        {

            expected = lease.Publication.Record;

        }

        Guid operationId = Guid.NewGuid();

        reset.Active = new ActiveInstallationReset(
            Scope: InstallationResetScope.All,
            WorkspaceRoot: null,
            PlanId: "accepted-plan",
            OperationId: operationId);

        InstallationMaintenanceCoordinationResult resumed = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.All,
                "accepted-plan",
                operationId,
                CancellationToken.None);

        Assert.Equal(
            InstallationMaintenanceCoordinationDisposition.Acquired,
            resumed.Disposition);

        await using InstallationMaintenanceCoordinationLease resumedLease =
            resumed.BorrowAcquiredLease();

        Assert.Equal(expected, resumedLease.Publication.Record);

    }

    [Fact]
    public async Task Conflicting_blocker_is_never_replaced_or_adopted()
    {

        InstallationMaintenanceCoordination coordinator = Coordinator();

        InstallationMaintenanceCoordinationResult first = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.All,
                "first-plan",
                operationId: null,
                CancellationToken.None);

        ClientMutationBlockerRecord expected;

        await using (InstallationMaintenanceCoordinationLease lease =
                     first.BorrowAcquiredLease())
        {

            expected = lease.Publication.Record;

        }

        InstallationMaintenanceCoordinationResult conflicting = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.All,
                "different-plan",
                operationId: null,
                CancellationToken.None);

        Assert.Equal(
            InstallationMaintenanceCoordinationDisposition.Contended,
            conflicting.Disposition);

        Assert.Equal(
            expected,
            (await new ClientMutationBlockerStore(_guardedRoot)
                .InspectAsync()).Value!.Record);

    }

    [Fact]
    public async Task Blocker_is_removed_only_after_both_reset_and_restore_evidence_are_clear()
    {

        MutableResetProbe reset = new(active: null);

        MutableRestoreProbe restore = new(active: false);

        InstallationMaintenanceCoordination coordinator = Coordinator(
            reset,
            restore);

        InstallationMaintenanceCoordinationResult acquired = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.All,
                "accepted-plan",
                operationId: null,
                CancellationToken.None);

        await using InstallationMaintenanceCoordinationLease held =
            acquired.BorrowAcquiredLease();

        reset.Active = new ActiveInstallationReset(
            Scope: InstallationResetScope.All,
            WorkspaceRoot: null,
            PlanId: "accepted-plan",
            OperationId: Guid.NewGuid());

        Result refused = await held.RemoveBlockerIfSafeAsync(
            CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.NotNull((await new ClientMutationBlockerStore(_guardedRoot)
            .InspectAsync()).Value);

        reset.Active = null;

        Assert.True((await held.RemoveBlockerIfSafeAsync(
            CancellationToken.None)).IsSuccess);

        Assert.Null((await new ClientMutationBlockerStore(_guardedRoot)
            .InspectAsync()).Value);

    }

    [Fact]
    public async Task Missing_blocker_is_synthesized_only_for_the_exact_active_reset_identity()
    {

        Guid operationId = Guid.NewGuid();

        MutableResetProbe reset = new(
            new ActiveInstallationReset(
                Scope: InstallationResetScope.All,
                WorkspaceRoot: null,
                PlanId: "plan-a",
                OperationId: operationId));

        InstallationMaintenanceCoordination coordinator = Coordinator(
            reset,
            new MutableRestoreProbe(active: false));

        InstallationMaintenanceCoordinationResult mismatch = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.All,
                "plan-b",
                Guid.NewGuid(),
                CancellationToken.None);

        Assert.Equal(
            InstallationMaintenanceCoordinationDisposition.Contended,
            mismatch.Disposition);

        Assert.Null((await new ClientMutationBlockerStore(_guardedRoot)
            .InspectAsync()).Value);

        InstallationMaintenanceCoordinationResult exact = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.All,
                "plan-a",
                operationId,
                CancellationToken.None);

        Assert.Equal(
            InstallationMaintenanceCoordinationDisposition.Acquired,
            exact.Disposition);

        await using InstallationMaintenanceCoordinationLease held =
            exact.BorrowAcquiredLease();

        Assert.Equal(operationId, held.Publication.Record.OperationId);

    }

    [Fact]
    public async Task Transitional_null_operation_blocker_requires_exact_active_identity_before_adoption()
    {

        MutableResetProbe reset = new(active: null);

        InstallationMaintenanceCoordination coordinator = Coordinator(
            reset,
            new MutableRestoreProbe(active: false));

        InstallationMaintenanceCoordinationResult opening = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.Global,
                "accepted-plan",
                operationId: null,
                CancellationToken.None);

        await opening.BorrowAcquiredLease().DisposeAsync();

        Guid activeOperation = Guid.NewGuid();

        reset.Active = new ActiveInstallationReset(
            Scope: InstallationResetScope.Global,
            WorkspaceRoot: null,
            PlanId: "accepted-plan",
            OperationId: activeOperation);

        InstallationMaintenanceCoordinationResult mismatch = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.Global,
                "accepted-plan",
                Guid.NewGuid(),
                CancellationToken.None);

        Assert.Equal(
            InstallationMaintenanceCoordinationDisposition.Contended,
            mismatch.Disposition);

        InstallationMaintenanceCoordinationResult exact = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.Global,
                "accepted-plan",
                activeOperation,
                CancellationToken.None);

        Assert.Equal(
            InstallationMaintenanceCoordinationDisposition.Acquired,
            exact.Disposition);

        await exact.BorrowAcquiredLease().DisposeAsync();

    }

    [Fact]
    public async Task Host_startup_removes_blocker_left_before_active_reset_publication_under_both_locks()
    {

        MutableResetProbe reset = new(active: null);

        InstallationMaintenanceCoordination coordinator = Coordinator(
            reset,
            new MutableRestoreProbe(active: false));

        InstallationMaintenanceCoordinationResult opening = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.All,
                "accepted-plan",
                operationId: null,
                CancellationToken.None);

        await opening.BorrowAcquiredLease().DisposeAsync();

        using ArcanumMaintenanceLock maintenance = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_guardedRoot));

        InstallationStartupCoordinationResult startup = await coordinator
            .AcquireHostStartupAsync(
                maintenance,
                CancellationToken.None);

        Assert.Equal(
            InstallationStartupCoordinationDisposition.Acquired,
            startup.Disposition);

        await using InstallationStartupCoordinationLease lease =
            startup.BorrowAcquiredLease();

        Assert.False(lease.RequiresRecovery);

        Assert.Null((await new ClientMutationBlockerStore(_guardedRoot)
            .InspectAsync()).Value);

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Contended,
            ArcanumClientMutationLock.AcquireDetailed(_guardedRoot).Disposition);

    }

    [Fact]
    public async Task Host_startup_removes_blocker_left_after_active_reset_retirement_under_both_locks()
    {

        Guid operationId = Guid.NewGuid();

        MutableResetProbe reset = new(
            new ActiveInstallationReset(
                Scope: InstallationResetScope.Global,
                WorkspaceRoot: null,
                PlanId: "accepted-plan",
                OperationId: operationId));

        InstallationMaintenanceCoordination coordinator = Coordinator(
            reset,
            new MutableRestoreProbe(active: false));

        InstallationMaintenanceCoordinationResult active = await coordinator
            .AcquireInstallationResetAsync(
                InstallationResetScope.Global,
                "accepted-plan",
                operationId,
                CancellationToken.None);

        await active.BorrowAcquiredLease().DisposeAsync();

        reset.Active = null;

        using ArcanumMaintenanceLock maintenance = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_guardedRoot));

        InstallationStartupCoordinationResult startup = await coordinator
            .AcquireHostStartupAsync(
                maintenance,
                CancellationToken.None);

        Assert.Equal(
            InstallationStartupCoordinationDisposition.Acquired,
            startup.Disposition);

        await using InstallationStartupCoordinationLease lease =
            startup.BorrowAcquiredLease();

        Assert.False(lease.RequiresRecovery);

        Assert.Null((await new ClientMutationBlockerStore(_guardedRoot)
            .InspectAsync()).Value);

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Contended,
            ArcanumClientMutationLock.AcquireDetailed(_guardedRoot).Disposition);

    }

    [Fact]
    public async Task Host_startup_refuses_a_restore_blocker_for_a_different_active_operation()
    {

        Guid blockerOperation = Guid.NewGuid();

        MutableRestoreProbe restore = new(operationId: null);

        InstallationMaintenanceCoordination coordinator = Coordinator(
            new MutableResetProbe(active: null),
            restore);

        using ArcanumMaintenanceLock maintenance = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_guardedRoot));

        InstallationMaintenanceCoordinationResult opening = await coordinator
            .AcquireReplacementRestoreAsync(
                maintenance,
                blockerOperation,
                CancellationToken.None);

        await opening.BorrowAcquiredLease().DisposeAsync();

        restore.OperationId = Guid.NewGuid();

        InstallationStartupCoordinationResult startup = await coordinator
            .AcquireHostStartupAsync(
                maintenance,
                CancellationToken.None);

        Assert.Equal(
            InstallationStartupCoordinationDisposition.Unsafe,
            startup.Disposition);

        Assert.Equal(
            blockerOperation,
            (await new ClientMutationBlockerStore(_guardedRoot)
                .InspectAsync()).Value!.Record.OperationId);

    }

    [Fact]
    public async Task Host_startup_reauthenticates_the_exact_restore_operation_identity()
    {

        Guid operationId = Guid.NewGuid();

        MutableRestoreProbe restore = new(operationId: null);

        InstallationMaintenanceCoordination coordinator = Coordinator(
            new MutableResetProbe(active: null),
            restore);

        using ArcanumMaintenanceLock maintenance = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_guardedRoot));

        InstallationMaintenanceCoordinationResult opening = await coordinator
            .AcquireReplacementRestoreAsync(
                maintenance,
                operationId,
                CancellationToken.None);

        await opening.BorrowAcquiredLease().DisposeAsync();

        restore.OperationId = operationId;

        InstallationStartupCoordinationResult startup = await coordinator
            .AcquireHostStartupAsync(
                maintenance,
                CancellationToken.None);

        Assert.Equal(
            InstallationStartupCoordinationDisposition.Acquired,
            startup.Disposition);

        await using InstallationStartupCoordinationLease lease =
            startup.BorrowAcquiredLease();

        Assert.True(lease.RequiresRecovery);

        Assert.Equal(operationId, lease.Publication!.Record.OperationId);

    }

    private InstallationMaintenanceCoordination Coordinator(
        MutableResetProbe? reset = null,
        MutableRestoreProbe? restore = null) =>
        new(
            _guardedRoot,
            new ClientMutationBlockerStore(_guardedRoot),
            reset ?? new MutableResetProbe(active: null),
            restore ?? new MutableRestoreProbe(active: false));

    private sealed class MutableResetProbe(ActiveInstallationReset? active) :
        IClientMutationResetEvidenceProbe
    {

        internal ActiveInstallationReset? Active { get; set; } = active;

        public Task<Result<ActiveInstallationReset?>> InspectAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                Result<ActiveInstallationReset?>.Success(Active));

        }

    }

    private sealed class MutableRestoreProbe(Guid? operationId) :
        IClientMutationRestoreEvidenceProbe
    {

        private static readonly Guid LegacyTestOperationId = new(
            "c8318cb9-583a-4a73-b293-5fd3f5ff9b7f");

        internal MutableRestoreProbe(bool active)
            : this(active ? LegacyTestOperationId : null)
        {
        }

        internal Guid? OperationId { get; set; } = operationId;

        public Task<Result<ActiveReplacementRestore?>> InspectAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                Result<ActiveReplacementRestore?>.Success(
                    OperationId is { } active
                        ? new ActiveReplacementRestore(active)
                        : null));

        }

    }

}
