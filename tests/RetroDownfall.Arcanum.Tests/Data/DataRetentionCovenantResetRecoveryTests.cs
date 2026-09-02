using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Issue #118 — how the shipped recovery handler treats each retention checkpoint version.
/// </summary>
/// <remarks>
/// Three arms, and each one has a different right answer. A version-0 row never reached its durable
/// journal and must close, because a parked retention row blocks every later retention operation. A
/// version-2 row is an ordinary mutation and reconciles exactly as it always did — this build
/// changed its priority, not its payload. A version-3 row carrying a Covenant arm is an interrupted
/// erasure: its owner is rebuilt from the checkpoint alone and it parks, because resuming it needs
/// the exclusive erasure coordinator this build does not have, and restarting it would run a second
/// dataset replacement over a family whose canonical arm may already be gone (§10.20.3).
/// </remarks>
public sealed partial class DataRetentionServiceTests
{

    private static readonly string CovenantResetEffect = new('a', 64);

    private static readonly string CovenantScopeCode =
        ((int)MemoryResetScope.Covenant).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static TheoryData<CovenantResetPhase> EveryResetPhase()
    {

        TheoryData<CovenantResetPhase> data = [];

        foreach (CovenantResetPhase phase in CovenantResetPhaseMachine.Ordered)
        {

            data.Add(phase);

        }

        return data;

    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_maintains_the_exact_adopted_owner_while_the_coordinator_is_paused(
        bool factoryReset)
    {

        RequireSqlCipher();

        TimeSpan recoveryLease = TimeSpan.FromMinutes(2);

        TimeSpan heartbeat = TimeSpan.FromSeconds(30);

        RecoveryTimeProvider clock = new(
            new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedRecoveryCheckpointAsync(
            operations,
            factoryReset,
            CovenantResetPhase.InventoryPrepared,
            startedAt: clock.GetUtcNow().Subtract(TimeSpan.FromMinutes(5)),
            leaseDuration: TimeSpan.FromMinutes(1));

        RecoveryPause pause = new();

        DataRetentionLeaseMaintainer maintainer = new(
            async (operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken) =>
            {

                return await operations.RenewLeaseAsync(
                    operationId,
                    ownerId,
                    utcNow,
                    leaseExpiresAt,
                    cancellationToken);

            },
            clock,
            leaseDuration: recoveryLease,
            heartbeatInterval: heartbeat);

        DataRetentionService service = CreateService(
            timeProvider: clock,
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhase.InventoryPrepared,
                pause: pause,
                timeProvider: clock),
            leaseMaintainer: maintainer);

        ILongRunningOperationRecoveryHandler handler = factoryReset
            ? new DataRetentionFactoryResetRecoveryHandler(service)
            : new DataRetentionMutationRecoveryHandler(service);

        LongRunningOperationReconciler reconciler = new(
            operations,
            [handler],
            clock,
            NullLogger<LongRunningOperationReconciler>.Instance);

        const string adoptedOwner = "fake-time-recovery-owner";

        DateTimeOffset adoptedAt = clock.GetUtcNow();

        Task<LongRunningOperationReconciliationSummary> recovering = reconciler.ReconcileNowAsync(
            adoptedOwner,
            maxOperations: 1,
            maxConcurrency: 1,
            CancellationToken.None);

        await pause.WaitUntilPausedAsync();

        LongRunningOperation adopted = (await operations.GetAsync(operation.Id))!;

        LongRunningOperation? maintainedWhilePaused = null;

        try
        {

            await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));

            for (int heartbeatNumber = 1; heartbeatNumber <= 6; heartbeatNumber++)
            {

                clock.Advance(heartbeat);

                await clock
                    .WaitForScheduledTimerCountAsync(heartbeatNumber + 1)
                    .WaitAsync(TimeSpan.FromSeconds(5));

            }

            maintainedWhilePaused = await operations.GetAsync(operation.Id);

        }
        finally
        {

            pause.Release();

        }

        Assert.NotNull(maintainedWhilePaused);

        LongRunningOperation duringPause = maintainedWhilePaused;

        LongRunningOperationReconciliationSummary result = await recovering.WaitAsync(
            TimeSpan.FromSeconds(10));

        Assert.Equal(1, result.Claimed);

        Assert.Equal(1, result.Completed);

        Assert.Equal(adoptedOwner, adopted.LeaseOwner);

        Assert.Equal(adoptedAt.Add(recoveryLease), adopted.LeaseExpiresAt);

        Assert.True(clock.GetUtcNow() > adopted.LeaseExpiresAt);

        Assert.Equal(adoptedOwner, duringPause.LeaseOwner);

        Assert.True(duringPause.LeaseExpiresAt > clock.GetUtcNow());

        Assert.True(duringPause.Revision >= adopted.Revision + 6);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        Assert.Equal(LongRunningOperationState.Completed, after.State);

        Assert.Null(after.LeaseOwner);

    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_owner_loss_cancels_without_overwriting_the_new_owner(bool factoryReset)
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedRecoveryCheckpointAsync(
            operations,
            factoryReset,
            CovenantResetPhase.InventoryPrepared);

        RecoveryPause pause = new();

        TaskCompletionSource<LongRunningOperation> adopted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        DataRetentionLeaseMaintainer maintainer = new(
            async (operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken) =>
            {

                LongRunningOperation current = (await operations.GetAsync(
                    operationId,
                    cancellationToken))!;

                Assert.True(await operations.TryTransitionAsync(
                    operationId,
                    current.Revision,
                    ownerId,
                    LongRunningOperationState.ReconciliationRequired,
                    utcNow,
                    ErrorCodes.Covenant.MaintenanceFailed,
                    cancellationToken));

                LongRunningOperationLeaseResult replacement = await operations.TryAcquireLeaseAsync(
                    operationId,
                    "replacement-recovery-owner",
                    utcNow,
                    leaseExpiresAt,
                    cancellationToken);

                Assert.True(replacement.Acquired);

                adopted.TrySetResult(replacement.Operation);

                return false;

            },
            TimeProvider.System,
            leaseDuration: TimeSpan.FromMinutes(10),
            heartbeatInterval: TimeSpan.FromMilliseconds(20));

        DataRetentionService service = CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhase.InventoryPrepared,
                pause: pause),
            leaseMaintainer: maintainer);

        LongRunningOperation snapshot = (await operations.GetAsync(operation.Id))!;

        Task<LongRunningOperationRecoveryResult> recovering = factoryReset
            ? service.RecoverFactoryResetAsync(snapshot, CancellationToken.None)
            : service.RecoverMutationAsync(snapshot, CancellationToken.None);

        await pause.WaitUntilPausedAsync();

        LongRunningOperation replacement;

        try
        {

            replacement = await adopted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        }
        finally
        {

            pause.Release();

        }

        LongRunningOperationRecoveryResult result = await recovering.WaitAsync(
            TimeSpan.FromSeconds(10));

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, result.ErrorCode);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        Assert.Equal(replacement.LeaseOwner, after.LeaseOwner);

        Assert.Equal(replacement.Revision, after.Revision);

        Assert.Equal(replacement.LeaseExpiresAt, after.LeaseExpiresAt);

    }

    [SkippableTheory]

    [MemberData(nameof(EveryResetPhase))]
    public async Task RecoverMutationAsync_FromEveryV3Phase_RunsTheCoordinatorAndCompletes(
        CovenantResetPhase phase)
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCovenantResetCheckpointAsync(operations, phase);

        DataRetentionService service = CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(operations, operation, phase));

        LongRunningOperationRecoveryResult result = await service.RecoverMutationAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);

        Assert.Null(result.ErrorCode);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        // The coordinator durably advances every completed storage step. The recovery handler only
        // reports Completed; the outer reconciler owns the row's terminal transition.
        Assert.Equal(DataRetentionMutationCheckpointV3.CurrentVersion, after.CheckpointVersion);

        Assert.Equal(
            CovenantResetPhase.ReopenedVerified,
            CovenantRecoveryCheckpointCodec
                .DecodeDataRetentionMutation(after.CheckpointPayload!)
                .Value
                .Covenant!
                .Phase);

    }

    /// <summary>
    /// The handler rebuilds the owner itself, from the payload it decoded, and reports the exact
    /// phase it found.
    /// </summary>
    /// <remarks>
    /// Asserted through the log rather than the result code, because every failure of a Covenant
    /// erasure deliberately carries the same single code. The recorded phase is the one thing that
    /// distinguishes a decoded, adopted checkpoint from a refused one, and without this the whole
    /// method could be replaced by a constant return.
    /// </remarks>
    [SkippableTheory]

    [MemberData(nameof(EveryResetPhase))]
    public async Task RecoverMutationAsync_FromEveryV3Phase_AdoptsTheCheckpointAndReportsItsPhase(
        CovenantResetPhase phase)
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCovenantResetCheckpointAsync(operations, phase);

        RecordingLogger<DataRetentionService> log = new();

        LongRunningOperationRecoveryResult result = await CreateService(
            operationStore: operations,
            logger: log,
            erasureCoordinator: RecoveryCoordinator(operations, operation, phase)).RecoverMutationAsync(
                (await operations.GetAsync(operation.Id))!,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);

        string adopted = Assert.Single(
            log.Messages,
            message => message.Contains("Covenant reset was interrupted", StringComparison.Ordinal));

        Assert.Contains(phase.ToString(), adopted, StringComparison.Ordinal);

        Assert.Contains(operation.Id.ToString(), adopted, StringComparison.Ordinal);

    }

    /// <summary>
    /// A V3 arm naming another operation is refused rather than adopted. Adopting it would hand a
    /// closed scope to an operation that never closed it.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithV3ArmNamingAnotherOperation_RefusesWithoutAdopting()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            DataRetentionMutationCheckpointV3.CurrentVersion,
            payload: null,
            checkpointFactory: _ => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionMutationCheckpointV3(
                    DataRetentionMutationCheckpointV3.CurrentVersion,
                    Subtype: "reset-memory",
                    Target: CovenantScopeCode,
                    new CovenantResetEffectArmV1(
                        Guid.Parse("deadbeef-dead-4eef-8eef-deadbeefdead"),
                        CovenantResetEffect,
                        CovenantExclusiveOperation.CovenantReset,
                        CovenantResetPhase.CanonicalApplied))));

        RecordingLogger<DataRetentionService> log = new();

        LongRunningOperationRecoveryResult result =
            await CreateService(operationStore: operations, logger: log).RecoverMutationAsync(
                (await operations.GetAsync(operation.Id))!,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, result.ErrorCode);

        Assert.DoesNotContain(
            log.Messages,
            message => message.Contains("Covenant reset was interrupted", StringComparison.Ordinal));

    }

    /// <summary>
    /// A V3 row whose payload this build cannot read parks rather than being abandoned. Abandoning
    /// would discard the only durable record that a family is half erased.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithUnreadableV3Payload_ParksWithoutAdopting()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            DataRetentionMutationCheckpointV3.CurrentVersion,
            "not a checkpoint"u8.ToArray());

        RecordingLogger<DataRetentionService> log = new();

        LongRunningOperationRecoveryResult result =
            await CreateService(operationStore: operations, logger: log).RecoverMutationAsync(
                (await operations.GetAsync(operation.Id))!,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, result.ErrorCode);

        Assert.DoesNotContain(
            log.Messages,
            message => message.Contains("Covenant reset was interrupted", StringComparison.Ordinal));

    }

    /// <summary>
    /// A V3 row with no Covenant arm closed no admission, so it has no exclusive scope to adopt and
    /// no erasure to attribute to it. It is an ordinary reconciliation failure rather than a
    /// Covenant escalation, and the two codes must stay distinguishable.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithV3ArmAbsent_IsAnOrdinaryReconciliationFailure()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            DataRetentionMutationCheckpointV3.CurrentVersion,
            payload: null,
            checkpointFactory: static _ => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionMutationCheckpointV3(
                    DataRetentionMutationCheckpointV3.CurrentVersion,
                    Subtype: "delete-session",
                    Target: "f0f0f0f0f0f04f0f8f0f0f0f0f0f0f0f",
                    Covenant: null)));

        LongRunningOperationRecoveryResult result =
            await CreateService(operationStore: operations).RecoverMutationAsync(
                (await operations.GetAsync(operation.Id))!,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.ErrorCode);

    }

    /// <summary>
    /// The V2 arm is untouched by this slice. An ordinary reset-memory journal written before the
    /// V3 shape existed still decodes, still reconciles against the database that is the commit
    /// authority, and still completes — it never falls through the new V3 branch and never runs a
    /// second dataset replacement.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithLegacyV2Journal_StillResumesAndCompletes()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            checkpointVersion: 2,
            LegacyResetMemoryJournal());

        DataRetentionService service = CreateService(operationStore: operations);

        LongRunningOperationRecoveryResult result = await service.RecoverMutationAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        Assert.Equal(2, after.CheckpointVersion);

    }

    /// <summary>
    /// A version-0 row still closes. The V3 branch must not capture the window between the
    /// single-flight insert and the first journal, because parking it would wedge retention.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithVersionZeroRow_StillAbandonsAsNeverStarted()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Interrupted before its journal.",
                DateTimeOffset.UtcNow));

        DataRetentionService service = CreateService(operationStore: operations);

        LongRunningOperationRecoveryResult result = await service.RecoverMutationAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, result.State);

        Assert.Equal(
            LongRunningOperationRecoveryOutcomes.RetentionMutationNeverStarted,
            result.ErrorCode);

    }

    [SkippableTheory]

    [MemberData(nameof(EveryResetPhase))]
    public async Task RecoverFactoryResetAsync_FromEveryV1Phase_RunsTheCoordinatorAndCompletes(
        CovenantResetPhase phase)
    {

        RequireSqlCipher();

        _ = await SeedSessionAsync(pinned: false);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            DataRetentionFactoryResetCheckpointV1.CurrentVersion,
            payload: null,
            LongRunningOperationKinds.DataRetentionFactoryReset,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            checkpointFactory: id => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionFactoryResetCheckpointV1(
                    DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                    id,
                    CovenantResetEffect,
                    CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    phase)));

        DataRetentionService service = CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(operations, operation, phase));

        LongRunningOperationRecoveryResult result = await service.RecoverFactoryResetAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        Assert.Equal(
            CovenantResetPhase.ReopenedVerified,
            CovenantRecoveryCheckpointCodec
                .DecodeDataRetentionFactoryReset(after.CheckpointPayload!)
                .Value
                .Phase);

        long ordinarySessions = await _db!.Sessions.LongCountAsync();

        Assert.Equal(
            phase < CovenantResetPhase.HandlesClosed ? 0 : 1,
            ordinarySessions);

    }

    [SkippableFact]

    public async Task RecoverFactoryResetAsync_WhenOrdinaryContinuationFails_KeepsAdmissionClosedAtManagedBoundary()
    {

        RequireSqlCipher();

        _ = await SeedSessionAsync(pinned: false);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            DataRetentionFactoryResetCheckpointV1.CurrentVersion,
            payload: null,
            LongRunningOperationKinds.DataRetentionFactoryReset,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            checkpointFactory: id => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionFactoryResetCheckpointV1(
                    DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                    id,
                    CovenantResetEffect,
                    CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    CovenantResetPhase.ManagedArtifactsProcessed)));

        _ = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.WorkspaceIndex,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Conflicting operation blocks factory continuation.",
                DateTimeOffset.UtcNow));

        DataRetentionService service = CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhase.ManagedArtifactsProcessed));

        LongRunningOperationRecoveryResult result = await service.RecoverFactoryResetAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Data.Conflict, result.ErrorCode);

        Assert.Equal(1, await _db!.Sessions.LongCountAsync());

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        Assert.Equal(
            CovenantResetPhase.ManagedArtifactsProcessed,
            CovenantRecoveryCheckpointCodec
                .DecodeDataRetentionFactoryReset(after.CheckpointPayload!)
                .Value
                .Phase);

    }

    [SkippableTheory]

    [InlineData(
        RecoveryDisposition.Rollback,
        LongRunningOperationState.Failed,
        ErrorCodes.Covenant.IntegrityFailure)]

    [InlineData(
        RecoveryDisposition.KeepClosed,
        LongRunningOperationState.ReconciliationRequired,
        ErrorCodes.Covenant.ErasureIncomplete)]
    public async Task RecoverMutationAsync_MapsTheCoordinatorsClosedDisposition(
        RecoveryDisposition disposition,
        LongRunningOperationState expectedState,
        string expectedError)
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCovenantResetCheckpointAsync(
            operations,
            CovenantResetPhase.InventoryPrepared);

        LongRunningOperationRecoveryResult result = await CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhase.InventoryPrepared,
                disposition)).RecoverMutationAsync(
                    (await operations.GetAsync(operation.Id))!,
                    CancellationToken.None);

        Assert.Equal(expectedState, result.State);

        Assert.Equal(expectedError, result.ErrorCode);

    }

    [SkippableFact]
    public async Task RecoverFactoryResetAsync_RequiresTheCurrentLeaseOwner()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            DataRetentionFactoryResetCheckpointV1.CurrentVersion,
            payload: null,
            LongRunningOperationKinds.DataRetentionFactoryReset,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            checkpointFactory: id => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionFactoryResetCheckpointV1(
                    DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                    id,
                    CovenantResetEffect,
                    CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    CovenantResetPhase.InventoryPrepared)));

        LongRunningOperation unowned = (await operations.GetAsync(operation.Id))! with
        {

            LeaseOwner = " ",

        };

        LongRunningOperationRecoveryResult result = await CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhase.InventoryPrepared)).RecoverFactoryResetAsync(
                    unowned,
                    CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, result.ErrorCode);

    }

    public enum RecoveryDisposition
    {

        Commit,

        Rollback,

        KeepClosed,

    }

    private static CovenantErasureCoordinator RecoveryCoordinator(
        LongRunningOperationStore operations,
        LongRunningOperation operation,
        CovenantResetPhase phase,
        RecoveryDisposition disposition = RecoveryDisposition.Commit,
        RecoveryPause? pause = null,
        TimeProvider? timeProvider = null)
    {

        Result<CovenantErasureCheckpointState> checkpoint = operation.Kind
            == LongRunningOperationKinds.DataRetentionFactoryReset
            ? CovenantErasureCheckpointState.FromFactoryResetCheckpoint(
                operation.Id,
                operation.CheckpointPayload!)
            : CovenantErasureCheckpointState.FromMutationCheckpoint(
                operation.Id,
                operation.CheckpointPayload!,
                out _);

        Assert.True(checkpoint.IsSuccess);

        Assert.Equal(phase, checkpoint.Value.Phase);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        gate.AdoptDurableRecoveryOwner(
            checkpoint.Value.Owner,
            scope: null,
            cleanupOnlyHistoricalCampaign: false);

        TimeProvider clock = timeProvider ?? TimeProvider.System;

        return new CovenantErasureCoordinator(
            new LongRunningOperationCoordinator(operations, clock),
            operations,
            gate,
            new RecoveryArtifactKernel(),
            new RecoveryManagedFileKernel(),
            new RecoveryInventory(disposition, pause),
            new RecoveryTransition(disposition),
            new RecoveryWriterLifecycle(),
            clock,
            NullLogger<CovenantErasureCoordinator>.Instance);

    }

    private sealed class RecoveryInventory(
        RecoveryDisposition disposition,
        RecoveryPause? pause = null)
        : ICovenantErasureInventorySource
    {

        public async Task<Result<CovenantErasureInventorySummary>> PreflightBeforeCanonicalAsync(
            CovenantExclusiveOperation operation,
            Guid datasetGeneration,
            CancellationToken cancellationToken)
        {

            if (pause is not null)
            {

                await pause.WaitForReleaseAsync(cancellationToken);

            }

            return disposition == RecoveryDisposition.Rollback
                ? Result<CovenantErasureInventorySummary>.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "The recovery inventory was refused."))
                : Result<CovenantErasureInventorySummary>.Success(
                    new CovenantErasureInventorySummary(
                        0,
                        0,
                        new CovenantDisclosureExposure(0, CovenantDisclosureCountKind.Exact)));

        }

        public Task<Result> PreflightRemainingManagedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<CovenantDatabaseErasureBatch>> ReadNextDatabaseBatchAsync(
            Guid datasetGeneration,
            Guid? afterLabelId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CovenantDatabaseErasureBatch>.Success(
                    new CovenantDatabaseErasureBatch(afterLabelId, true, page: null)));

        public Task<Result<CovenantManagedFileErasureBatch>> ReadNextManagedFileBatchAsync(
            Guid operationId,
            Guid? afterLabelId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CovenantManagedFileErasureBatch>.Success(
                    new CovenantManagedFileErasureBatch(afterLabelId, true, [])));

        public Task<Result<CovenantDisclosureExposure>> ReadDisclosureExposureAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CovenantDisclosureExposure>.Success(
                    new CovenantDisclosureExposure(0, CovenantDisclosureCountKind.Exact)));

    }

    private sealed class RecoveryArtifactKernel : ICovenantProtectedArtifactErasureKernel
    {

        public ValueTask<Result<CovenantArtifactErasureProgress>> ErasePageAsync(
            CovenantProtectedArtifactErasurePage page,
            CovenantArtifactErasureAuthority authority,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                Result<CovenantArtifactErasureProgress>.Success(
                    new CovenantArtifactErasureProgress(0, 0, 0, CovenantErasureBlocker.None)));

    }

    private sealed class RecoveryManagedFileKernel : ICovenantManagedFileErasureKernel
    {

        public ValueTask<Result<CovenantArtifactErasureProgress>> EraseAsync(
            CovenantManagedFileErasureRequest request,
            CovenantArtifactErasureAuthority authority,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                Result<CovenantArtifactErasureProgress>.Success(
                    new CovenantArtifactErasureProgress(0, 0, 0, CovenantErasureBlocker.None)));

    }

    private sealed class RecoveryWriterLifecycle : ICovenantDisclosureWriterLifecycle
    {

        public ValueTask<Result> QuiesceAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ReopenAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

    }

    private sealed class RecoveryTransition(RecoveryDisposition disposition)
        : ICovenantErasureTransition
    {

        public Task<Result<Guid>> ApplyCanonicalErasureAsync(
            CovenantExclusiveOperation operation,
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                disposition == RecoveryDisposition.KeepClosed
                    ? Result<Guid>.Failure(
                        new Error(
                            ErrorCodes.Covenant.ErasureIncomplete,
                            "The recovery transition was refused."))
                    : Result<Guid>.Success(Guid.Parse("99999999-9999-4999-8999-999999999999")));

        public Task<Result> CloseHandlesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> TruncateWalAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> CompactAsync(CovenantV3CompactionCapabilities capabilities, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> InitializeAcceleratorAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<CovenantVerifiedCandidateState>.Success(Candidate()));

        public Task<Result> PublishCommittedAsync(
            ICovenantExclusiveOperationLease lease,
            CovenantVerifiedCandidateState candidate,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        private static CovenantVerifiedCandidateState Candidate() =>
            new(
                new CovenantCandidateDatasetState(
                    Guid.Parse("99999999-9999-4999-8999-999999999999"),
                    0,
                    0,
                    null,
                    null,
                    0,
                    0,
                    1,
                    CovenantFtsRebuildState.FullRebuildRequired,
                    1,
                    new byte[32],
                    1),
                new CovenantCandidateAuthorityState(
                    "retention-recovery-test",
                    1,
                    1,
                    new byte[32],
                    1,
                    CovenantHostToolsState.Clean,
                    null),
                new CovenantCandidateCapabilityState(0, 0, false));

    }

    private async Task<LongRunningOperation> SeedRecoveryCheckpointAsync(
        LongRunningOperationStore operations,
        bool factoryReset,
        CovenantResetPhase phase,
        DateTimeOffset? startedAt = null,
        TimeSpan? leaseDuration = null) =>
        factoryReset
            ? await SeedCheckpointAsync(
                operations,
                DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                payload: null,
                kind: LongRunningOperationKinds.DataRetentionFactoryReset,
                policy: LongRunningOperationRecoveryPolicy.RestartIdempotently,
                checkpointFactory: id => CovenantRecoveryCheckpointCodec.Encode(
                    new DataRetentionFactoryResetCheckpointV1(
                        DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                        id,
                        CovenantResetEffect,
                        CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                        phase)),
                startedAt: startedAt,
                leaseDuration: leaseDuration)
            : await SeedCovenantResetCheckpointAsync(
                operations,
                phase,
                startedAt,
                leaseDuration);

    private sealed class RecoveryPause
    {

        private readonly TaskCompletionSource _paused = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        internal Task WaitUntilPausedAsync() => _paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

        internal async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {

            _paused.TrySetResult();

            await _release.Task.WaitAsync(cancellationToken);

        }

    }

    private sealed class RecoveryTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {

        private readonly object _gate = new();

        private readonly List<RecoveryTimer> _timers = [];

        private readonly List<(int ExpectedCount, TaskCompletionSource Completion)> _waiters = [];

        private DateTimeOffset _utcNow = initialUtcNow;

        private int _scheduledTimerCount;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {

            lock (_gate)
            {

                return _utcNow;

            }

        }

        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {

            ArgumentNullException.ThrowIfNull(callback);

            RecoveryTimer timer = new(this, callback, state);

            _ = timer.Change(dueTime, period);

            return timer;

        }

        internal void Advance(TimeSpan amount)
        {

            if (amount < TimeSpan.Zero)
            {

                throw new ArgumentOutOfRangeException(nameof(amount));

            }

            List<(TimerCallback Callback, object? State)> callbacks = [];

            lock (_gate)
            {

                _utcNow = _utcNow.Add(amount);

                foreach (RecoveryTimer timer in _timers.ToArray())
                {

                    timer.CollectDueCallbacks(_utcNow, callbacks);

                }

            }

            foreach ((TimerCallback callback, object? state) in callbacks)
            {

                callback(state);

            }

        }

        internal Task WaitForScheduledTimerCountAsync(int expectedCount)
        {

            lock (_gate)
            {

                if (_scheduledTimerCount >= expectedCount)
                {

                    return Task.CompletedTask;

                }

                TaskCompletionSource waiter = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                _waiters.Add((expectedCount, waiter));

                return waiter.Task;

            }

        }

        private void ChangeTimer(
            RecoveryTimer timer,
            TimeSpan dueTime,
            TimeSpan period)
        {

            if (dueTime < Timeout.InfiniteTimeSpan)
            {

                throw new ArgumentOutOfRangeException(nameof(dueTime));

            }

            if (period < Timeout.InfiniteTimeSpan || period == TimeSpan.Zero)
            {

                throw new ArgumentOutOfRangeException(nameof(period));

            }

            List<TaskCompletionSource> completed = [];

            lock (_gate)
            {

                if (timer.Disposed)
                {

                    throw new ObjectDisposedException(nameof(RecoveryTimer));

                }

                if (!_timers.Contains(timer))
                {

                    _timers.Add(timer);

                }

                timer.DueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : _utcNow.Add(dueTime);

                timer.Period = period;

                if (dueTime != Timeout.InfiniteTimeSpan)
                {

                    _scheduledTimerCount++;

                    for (int index = _waiters.Count - 1; index >= 0; index--)
                    {

                        if (_waiters[index].ExpectedCount > _scheduledTimerCount)
                        {

                            continue;

                        }

                        completed.Add(_waiters[index].Completion);

                        _waiters.RemoveAt(index);

                    }

                }

            }

            foreach (TaskCompletionSource waiter in completed)
            {

                waiter.TrySetResult();

            }

        }

        private void RemoveTimer(RecoveryTimer timer)
        {

            lock (_gate)
            {

                _ = _timers.Remove(timer);

            }

        }

        private sealed class RecoveryTimer(
            RecoveryTimeProvider owner,
            TimerCallback callback,
            object? state)
            : ITimer
        {

            internal bool Disposed { get; private set; }

            internal DateTimeOffset? DueAt { get; set; }

            internal TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {

                owner.ChangeTimer(this, dueTime, period);

                return true;

            }

            public void Dispose()
            {

                if (Disposed)
                {

                    return;

                }

                Disposed = true;

                owner.RemoveTimer(this);

            }

            public ValueTask DisposeAsync()
            {

                Dispose();

                return ValueTask.CompletedTask;

            }

            internal void CollectDueCallbacks(
                DateTimeOffset now,
                List<(TimerCallback Callback, object? State)> callbacks)
            {

                if (Disposed || DueAt is not DateTimeOffset dueAt || dueAt > now)
                {

                    return;

                }

                callbacks.Add((callback, state));

                if (Period == Timeout.InfiniteTimeSpan)
                {

                    DueAt = null;

                    return;

                }

                do
                {

                    dueAt = dueAt.Add(Period);

                }

                while (dueAt <= now);

                DueAt = dueAt;

            }

        }

    }

    private async Task<LongRunningOperation> SeedCovenantResetCheckpointAsync(
        LongRunningOperationStore operations,
        CovenantResetPhase phase,
        DateTimeOffset? startedAt = null,
        TimeSpan? leaseDuration = null) =>
        await SeedCheckpointAsync(
            operations,
            DataRetentionMutationCheckpointV3.CurrentVersion,
            payload: null,
            checkpointFactory: id => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionMutationCheckpointV3(
                    DataRetentionMutationCheckpointV3.CurrentVersion,
                    Subtype: "reset-memory",
                    Target: CovenantScopeCode,
                    new CovenantResetEffectArmV1(
                        id,
                        CovenantResetEffect,
                        CovenantExclusiveOperation.CovenantReset,
                        phase))),
            startedAt: startedAt,
            leaseDuration: leaseDuration);

    /// <summary>
    /// Leaves the row in the exact state a dead process leaves behind: the checkpoint it managed to
    /// write, under a lease that has already expired.
    /// </summary>
    private async Task<LongRunningOperation> SeedCheckpointAsync(
        LongRunningOperationStore operations,
        int checkpointVersion,
        byte[]? payload,
        string kind = LongRunningOperationKinds.DataRetentionMutation,
        LongRunningOperationRecoveryPolicy policy =
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
        Func<Guid, byte[]>? checkpointFactory = null,
        DateTimeOffset? startedAt = null,
        TimeSpan? leaseDuration = null)
    {

        DateTimeOffset now = startedAt ?? DateTimeOffset.UtcNow;

        TimeSpan ownedFor = leaseDuration ?? TimeSpan.FromMinutes(5);

        const string ownerId = "covenant-reset-recovery-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                kind,
                policy,
                "Interrupted Covenant erasure.",
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.Add(ownedFor));

        Assert.True(lease.Acquired);

        string reference = kind == LongRunningOperationKinds.DataRetentionMutation
            ? "retention-mutation:" + operation.Id.ToString("N")
            : "retention-factory-reset:" + operation.Id.ToString("N");

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion,
                checkpointFactory?.Invoke(operation.Id) ?? payload,
                reference,
                operation.PublicSummary,
                now));

        return (await operations.GetAsync(operation.Id))!;

    }

    /// <summary>
    /// Captures the recovery handler's own log, which is the only surface that distinguishes an
    /// adopted checkpoint from a refused one — every Covenant erasure failure deliberately carries
    /// the same single error code.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {

        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

    }

    /// <summary>
    /// The exact <c>ARCAMUT2</c> payload a pre-#118 build wrote for an entry-scope memory reset: no
    /// captured file entries, and the digest line the parser verifies.
    /// </summary>
    private static byte[] LegacyResetMemoryJournal()
    {

        string body =
            "ARCAMUT2\n"
            + "reset-memory\n"
            + Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(
                    ((int)MemoryResetScope.Entry).ToString(
                        System.Globalization.CultureInfo.InvariantCulture)))
            + "\n0\n";

        string digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(body)));

        return System.Text.Encoding.UTF8.GetBytes(body + "H:" + digest + "\n");

    }

}
