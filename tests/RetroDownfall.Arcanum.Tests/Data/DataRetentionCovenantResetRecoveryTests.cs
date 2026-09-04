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

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Issue #118 — how the shipped recovery handler treats each retention checkpoint version.
/// </summary>
/// <remarks>
/// Three arms, and each one has a different right answer. A version-0 row never reached its durable
/// journal and must close, because a parked retention row blocks every later retention operation. A
/// version-2 row is an ordinary mutation and reconciles exactly as it always did — this build
/// changed its priority, not its payload. A version-4 row is an offline-transition launch, and so an
/// interrupted erasure: its owner is rebuilt from the launch alone and it parks, because resuming it
/// needs the exclusive erasure coordinator this build does not have, and restarting it would run a
/// second dataset replacement over a family whose canonical arm may already be gone (§10.20.3).
///
/// <para>Every version other than those three is an ordinary mutation as far as this handler is
/// concerned. The row's own version decides that, rather than whether its payload happens to decode,
/// so a payload this build cannot read never turns an ordinary mutation into a Covenant
/// escalation.</para>
/// </remarks>
public sealed partial class DataRetentionServiceTests
{

    private static readonly string CovenantResetEffect = new('a', 64);

    /// <summary>
    /// The generation the launches below bind to, and the one they preselect as their target.
    /// </summary>
    /// <remarks>
    /// Fixed rather than freshly minted per call, because a launch is only launchable when the two
    /// differ, and a helper that generated both would let a future edit produce a pair that happened
    /// to satisfy the decoder for a reason no test states.
    /// </remarks>
    private static readonly Guid CovenantResetSourceGeneration =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid CovenantResetTargetGeneration =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>
    /// Recovery stops renewing the durable lease once the journal is its authority.
    /// </summary>
    /// <remarks>
    /// It used to heartbeat for the whole recovery, and the revision that heartbeat advanced is now
    /// the one the authenticated journal binds itself to — so a renewal would make the terminal
    /// compare-exchange refuse the very row the transition exists to terminalize.
    ///
    /// <para>What the heartbeat was guarding is guarded differently. Another process cannot be running
    /// an erasure at all, because the installation maintenance lock admits one; and generic
    /// reconciliation skips an operation this process has claimed, which is what stops a background
    /// pass adopting a row whose lease was deliberately allowed to lapse.</para>
    /// </remarks>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_stops_renewing_the_durable_lease_while_the_coordinator_runs(
        bool factoryReset)
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedRecoveryCheckpointAsync(
            operations,
            factoryReset,
            startedAt: DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(5)),
            leaseDuration: TimeSpan.FromMinutes(1));

        RecoveryPause pause = new();

        LongRunningOperationOwnership ownership = new();

        DataRetentionService service = CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhase.InventoryPrepared,
                pause: pause,
                ownership: ownership));

        LongRunningOperation snapshot = (await operations.GetAsync(operation.Id))!;

        Task<LongRunningOperationRecoveryResult> recovering = factoryReset
            ? service.RecoverFactoryResetAsync(snapshot, CancellationToken.None)
            : service.RecoverMutationAsync(snapshot, CancellationToken.None);

        await pause.WaitUntilPausedAsync();

        LongRunningOperation duringPause;

        try
        {

            // Given time to renew, and asserted not to have.
            await Task.Delay(TimeSpan.FromMilliseconds(400), TimeProvider.System);

            duringPause = (await operations.GetAsync(operation.Id))!;

            Assert.Equal(snapshot.Revision, duringPause.Revision);

            Assert.Equal(snapshot.LeaseExpiresAt, duringPause.LeaseExpiresAt);

            // And claimed, so a background reconciliation pass would leave the row alone rather than
            // reading its lapsed lease as an invitation to start a second recovery beside this one.
            Assert.True(ownership.IsClaimed(operation.Id));

        }
        finally
        {

            pause.Release();

        }

        LongRunningOperationRecoveryResult result = await recovering.WaitAsync(
            TimeSpan.FromSeconds(30));

        Assert.Equal(LongRunningOperationState.Completed, result.State);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        Assert.Equal(LongRunningOperationState.Completed, after.State);

        Assert.False(ownership.IsClaimed(operation.Id));

    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_owner_loss_leaves_the_new_owners_row_exactly_as_it_found_it(bool factoryReset)
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedRecoveryCheckpointAsync(
            operations,
            factoryReset);

        RecoveryPause pause = new();

        DataRetentionService service = CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhase.InventoryPrepared,
                pause: pause));

        LongRunningOperation snapshot = (await operations.GetAsync(operation.Id))!;

        Task<LongRunningOperationRecoveryResult> recovering = factoryReset
            ? service.RecoverFactoryResetAsync(snapshot, CancellationToken.None)
            : service.RecoverMutationAsync(snapshot, CancellationToken.None);

        await pause.WaitUntilPausedAsync();

        LongRunningOperation replacement;

        try
        {

            // The takeover is performed directly rather than waited for. It used to arrive through a
            // renewal callback, because the durable lease was being heartbeated for the whole run and
            // the heartbeat was what noticed; the closed period renews nothing now, so nothing notices
            // in flight and the test has to stage the takeover itself.
            Assert.True(await operations.TryTransitionAsync(
                operation.Id,
                snapshot.Revision,
                snapshot.LeaseOwner!,
                LongRunningOperationState.ReconciliationRequired,
                DateTimeOffset.UtcNow,
                ErrorCodes.Covenant.MaintenanceFailed));

            LongRunningOperationLeaseResult adopted = await operations.TryAcquireLeaseAsync(
                operation.Id,
                "replacement-recovery-owner",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5));

            Assert.True(adopted.Acquired);

            replacement = adopted.Operation;

        }
        finally
        {

            pause.Release();

        }

        LongRunningOperationRecoveryResult result = await recovering.WaitAsync(
            TimeSpan.FromSeconds(30));

        // The row is what matters, and it is untouched. A transition whose row moved under it cannot
        // name the terminal winner its journal requires, so it parks with the journal retained rather
        // than writing over an answer another owner is now responsible for.
        Assert.NotEqual(LongRunningOperationState.Completed, result.State);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        Assert.Equal(replacement.LeaseOwner, after.LeaseOwner);

        Assert.Equal(replacement.Revision, after.Revision);

        Assert.Equal(replacement.LeaseExpiresAt, after.LeaseExpiresAt);

        Assert.Equal(replacement.State, after.State);

    }

    /// <summary>
    /// A committed launch is resumed by running the coordinator, and the row still holds that exact
    /// launch when the run is over.
    /// </summary>
    /// <remarks>
    /// The launch is the one thing the row is still the authority for. It states which target this
    /// operation bound itself to before it closed anything, and a resumed run that rewrote the row
    /// would destroy the only durable record of which generation a half-replaced family is supposed
    /// to arrive at — which is exactly the question a later pass has to answer to tell its own
    /// canonical commit from somebody else's. Progress past the launch lives in the authenticated
    /// journal, so there is no phase left in the row for a resumed run to advance.
    /// </remarks>
    [SkippableFact]
    public async Task RecoverMutationAsync_FromACovenantLaunch_RunsTheCoordinatorAndCompletes()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCovenantResetCheckpointAsync(operations);

        DataRetentionService service = CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhaseMachine.First));

        LongRunningOperationRecoveryResult result = await service.RecoverMutationAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);

        Assert.Null(result.ErrorCode);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        // The recovery handler only reports Completed; the outer reconciler owns the row's terminal
        // transition, and the launch it committed to outlives the run either way.
        Assert.Equal(CovenantOfflineTransitionLaunchV4.CurrentVersion, after.CheckpointVersion);

        Assert.Equal(
            CovenantResetTargetGeneration,
            CovenantRecoveryCheckpointCodec
                .DecodeCovenantOfflineTransitionLaunch(after.CheckpointPayload!)
                .Value
                .TargetDatasetGeneration);

    }

    /// <summary>
    /// The handler rebuilds the owner itself, from the launch it decoded, and reports the phase that
    /// launch projects.
    /// </summary>
    /// <remarks>
    /// Asserted through the log rather than the result code, because every failure of a Covenant
    /// erasure deliberately carries the same single code. The reported phase is the one thing that
    /// distinguishes a decoded, adopted launch from a refused one, and without this the whole method
    /// could be replaced by a constant return. A launch always projects the first phase — it records
    /// what was committed to, not how far the transition got — so the constant this pins is the phase
    /// machine's own, never a literal repeated here.
    /// </remarks>
    [SkippableFact]
    public async Task RecoverMutationAsync_FromACovenantLaunch_AdoptsTheLaunchAndReportsItsPhase()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCovenantResetCheckpointAsync(operations);

        RecordingLogger<DataRetentionService> log = new();

        LongRunningOperationRecoveryResult result = await CreateService(
            operationStore: operations,
            logger: log,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhaseMachine.First)).RecoverMutationAsync(
                (await operations.GetAsync(operation.Id))!,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);

        string adopted = Assert.Single(
            log.Messages,
            message => message.Contains("Covenant reset was interrupted", StringComparison.Ordinal));

        Assert.Contains(
            CovenantResetPhaseMachine.First.ToString(),
            adopted,
            StringComparison.Ordinal);

        Assert.Contains(operation.Id.ToString(), adopted, StringComparison.Ordinal);

    }

    /// <summary>
    /// A launch naming another operation is refused rather than adopted. Adopting it would hand a
    /// closed scope to an operation that never closed it.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithALaunchNamingAnotherOperation_RefusesWithoutAdopting()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            payload: null,
            checkpointFactory: static _ => CovenantRecoveryCheckpointCodec.Encode(
                CovenantResetLaunch(Guid.Parse("deadbeef-dead-4eef-8eef-deadbeefdead"))));

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
    /// A launch row whose payload this build cannot read parks rather than being abandoned.
    /// Abandoning would discard the only durable record that a family is half erased.
    /// </summary>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithAnUnreadableLaunchPayload_ParksWithoutAdopting()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            "not a launch"u8.ToArray());

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
    /// A retention-mutation row that is not a launch closed no admission, so it has no exclusive
    /// scope to adopt and no erasure to attribute to it. It is an ordinary reconciliation failure
    /// rather than a Covenant escalation, and the two codes must stay distinguishable.
    /// </summary>
    /// <remarks>
    /// The row's own version answers this, rather than whether its payload happens to decode. An
    /// ordinary mutation's journal is a different shape under a different version, so a build that
    /// read "cannot decode" as "erasure" would park every ordinary mutation whose payload it could
    /// not read for any reason at all — and a parked retention row blocks every later retention
    /// operation forever, which is the one outcome an ordinary mutation must never cause.
    /// </remarks>
    [SkippableFact]
    public async Task RecoverMutationAsync_WithANonLaunchCheckpointVersion_IsAnOrdinaryReconciliationFailure()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        // The retired Covenant-arm version, which this build neither writes nor reads back as a
        // launch. Its bytes are never parsed, so the payload only has to be something.
        LongRunningOperation operation = await SeedCheckpointAsync(
            operations,
            checkpointVersion: 3,
            "not a launch"u8.ToArray());

        LongRunningOperationRecoveryResult result =
            await CreateService(operationStore: operations).RecoverMutationAsync(
                (await operations.GetAsync(operation.Id))!,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.ErrorCode);

    }

    /// <summary>
    /// The V2 arm is untouched by this slice. An ordinary reset-memory journal written before the
    /// launch shape existed still decodes, still reconciles against the database that is the commit
    /// authority, and still completes — it never falls through the launch branch and never runs a
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
    /// A version-0 row still closes. The launch branch must not capture the window between the
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

    /// <summary>
    /// A committed factory launch is resumed on the same terms, and its ordinary continuation still
    /// runs.
    /// </summary>
    /// <remarks>
    /// The surviving ordinary session is the observable half of that continuation: a factory erasure
    /// resumed from its launch has not yet reached the point where handles close, so the deletion the
    /// continuation owns is still ahead of it and must happen. A resumed run that skipped it would
    /// present a half-erased state root as a working installation.
    /// </remarks>
    [SkippableFact]
    public async Task RecoverFactoryResetAsync_FromAFactoryLaunch_RunsTheCoordinatorAndCompletes()
    {

        RequireSqlCipher();

        _ = await SeedSessionAsync(pinned: false);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedFactoryTransitionCheckpointAsync(operations);

        DataRetentionService service = CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhaseMachine.First));

        LongRunningOperationRecoveryResult result = await service.RecoverFactoryResetAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        Assert.Equal(
            CovenantResetTargetGeneration,
            CovenantRecoveryCheckpointCodec
                .DecodeDataRetentionFactoryTransitionLaunch(after.CheckpointPayload!)
                .Value
                .TargetDatasetGeneration);

        long ordinarySessions = await _db!.Sessions.LongCountAsync();

        Assert.Equal(0, ordinarySessions);

    }

    [SkippableFact]

    public async Task RecoverFactoryResetAsync_WhenOrdinaryContinuationFails_KeepsAdmissionClosedAtManagedBoundary()
    {

        RequireSqlCipher();

        _ = await SeedSessionAsync(pinned: false);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await SeedFactoryTransitionCheckpointAsync(operations);

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
                CovenantResetPhaseMachine.First));

        LongRunningOperationRecoveryResult result = await service.RecoverFactoryResetAsync(
            (await operations.GetAsync(operation.Id))!,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Data.Conflict, result.ErrorCode);

        Assert.Equal(1, await _db!.Sessions.LongCountAsync());

        LongRunningOperation after = (await operations.GetAsync(operation.Id))!;

        // Admission stays closed with the launch intact: the row is still the authority for which
        // target this erasure bound itself to, and a refused continuation may not spend that.
        Assert.Equal(
            CovenantResetTargetGeneration,
            CovenantRecoveryCheckpointCodec
                .DecodeDataRetentionFactoryTransitionLaunch(after.CheckpointPayload!)
                .Value
                .TargetDatasetGeneration);

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

        LongRunningOperation operation = await SeedCovenantResetCheckpointAsync(operations);

        LongRunningOperationRecoveryResult result = await CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhaseMachine.First,
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

        LongRunningOperation operation = await SeedFactoryTransitionCheckpointAsync(operations);

        LongRunningOperation unowned = (await operations.GetAsync(operation.Id))! with
        {

            LeaseOwner = " ",

        };

        LongRunningOperationRecoveryResult result = await CreateService(
            operationStore: operations,
            erasureCoordinator: RecoveryCoordinator(
                operations,
                operation,
                CovenantResetPhaseMachine.First)).RecoverFactoryResetAsync(
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
        TimeProvider? timeProvider = null,
        LocalOfflineTransitionPhaseAuthority? phases = null,
        LongRunningOperationOwnership? ownership = null)
    {

        Result<CovenantErasureCheckpointState> checkpoint = operation.Kind
            == LongRunningOperationKinds.DataRetentionFactoryReset
            ? CovenantErasureCheckpointState.FromFactoryResetCheckpoint(
                operation.Id,
                operation.CheckpointVersion,
                operation.CheckpointPayload!)
            : CovenantErasureCheckpointState.FromMutationCheckpoint(
                operation.Id,
                operation.CheckpointVersion,
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
            phases ?? new LocalOfflineTransitionPhaseAuthority(operations),
            new GrimoireOfflineTransitionDatabaseReconciler(operations, clock),
            ownership ?? new LongRunningOperationOwnership(),
            clock,
            NullLogger<CovenantErasureCoordinator>.Instance);

    }

    private sealed class RecoveryInventory(
        RecoveryDisposition disposition,
        RecoveryPause? pause = null)
        : ICovenantErasureInventorySource
    {

        /// <summary>
        /// The same source tuple the seeded launches bind to, so the fake and the durable rows this
        /// file writes cannot disagree about which dataset is being erased.
        /// </summary>
        public Task<Result<CovenantOfflineTransitionSourceState>> ReadOfflineTransitionSourceStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CovenantOfflineTransitionSourceState>.Success(
                    new CovenantOfflineTransitionSourceState(
                        CovenantResetSourceGeneration,
                        1,
                        1,
                        1)));

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
            CovenantCanonicalDatasetTransition dataset,
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                disposition == RecoveryDisposition.KeepClosed
                    ? Result<Guid>.Failure(
                        new Error(
                            ErrorCodes.Covenant.ErasureIncomplete,
                            "The recovery transition was refused."))
                    : Result<Guid>.Success(Guid.Parse("99999999-9999-4999-8999-999999999999")));

        public Task<Result> CloseHandlesAsync(

            CovenantV3MaintenanceCapability capability,

            CancellationToken cancellationToken) =>
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
                    1,
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
        DateTimeOffset? startedAt = null,
        TimeSpan? leaseDuration = null) =>
        factoryReset
            ? await SeedFactoryTransitionCheckpointAsync(operations, startedAt, leaseDuration)
            : await SeedCovenantResetCheckpointAsync(operations, startedAt, leaseDuration);

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
        DateTimeOffset? startedAt = null,
        TimeSpan? leaseDuration = null) =>
        await SeedCheckpointAsync(
            operations,
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            payload: null,
            checkpointFactory: static id => CovenantRecoveryCheckpointCodec.Encode(
                CovenantResetLaunch(id)),
            startedAt: startedAt,
            leaseDuration: leaseDuration);

    private async Task<LongRunningOperation> SeedFactoryTransitionCheckpointAsync(
        LongRunningOperationStore operations,
        DateTimeOffset? startedAt = null,
        TimeSpan? leaseDuration = null) =>
        await SeedCheckpointAsync(
            operations,
            DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
            payload: null,
            kind: LongRunningOperationKinds.DataRetentionFactoryReset,
            policy: LongRunningOperationRecoveryPolicy.RestartIdempotently,
            checkpointFactory: static id => CovenantRecoveryCheckpointCodec.Encode(
                FactoryTransitionLaunch(id)),
            startedAt: startedAt,
            leaseDuration: leaseDuration);

    /// <summary>
    /// The launch a Covenant reset commits, built exactly as the initiator builds it.
    /// </summary>
    /// <remarks>
    /// Spelled out member by member rather than borrowed from production, because every one of these
    /// members is a rule the decoder enforces: the ledger kind, the policy's declared name rather
    /// than its wire code, a canonical lowercase digest, two generations that differ, and three
    /// target epochs that are each the successor of their own source. A helper that copied whatever
    /// production produced would pass on the day a rule was dropped.
    /// </remarks>
    private static CovenantOfflineTransitionLaunchV4 CovenantResetLaunch(Guid operationId) =>
        new(
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            operationId,
            LongRunningOperationKinds.DataRetentionMutation,
            nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
            CovenantExclusiveOperation.CovenantReset,
            CovenantResetEffect,
            CovenantResetSourceGeneration,
            CovenantResetTargetGeneration,
            new CovenantOfflineTransitionEpochsV1(1, 1, 1),
            new CovenantOfflineTransitionEpochsV1(2, 2, 2),
            StartingRevision: 1);

    /// <summary>The launch a healthy-catalog factory erasure commits, on the same terms.</summary>
    private static DataRetentionFactoryTransitionLaunchV2 FactoryTransitionLaunch(Guid operationId) =>
        new(
            DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
            operationId,
            LongRunningOperationKinds.DataRetentionFactoryReset,
            nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            CovenantResetEffect,
            CovenantResetSourceGeneration,
            CovenantResetTargetGeneration,
            new CovenantOfflineTransitionEpochsV1(1, 1, 1),
            new CovenantOfflineTransitionEpochsV1(2, 2, 2),
            StartingRevision: 1);

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
