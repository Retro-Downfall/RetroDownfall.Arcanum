using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Operations;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The Covenant erasure phase machine: what it erases, in what order, what it never repeats, and how
/// it leaves admission.
/// </summary>
/// <remarks>
/// The storage owner is faked so every crash point can be exercised without destroying a real
/// database, exactly as the sibling family-reinitialize suite does. What is not faked is the thing
/// under test: the real gate, its real one-shot exclusive lease, the erasure authority the coordinator
/// wraps that lease in, the ordering across both shared kernels, and the admission state each failure
/// arm leaves behind (§10.20.4).
///
/// <para>Admission is asserted by actually trying to take a lease afterwards rather than by counting
/// disposition calls. A reset that reported <c>CommitAndReopen</c> while the gate stayed shut would
/// pass a call-counting assertion and fail every user.</para>
/// </remarks>
public sealed class CovenantErasureCoordinatorTests
{

    private static CancellationToken Token => CancellationToken.None;

    private static readonly Guid OperationId = new("55555555-5555-4555-8555-555555555555");

    /// <summary>
    /// The source and target pair a launch would have committed to, fixed so every case is comparable.
    /// </summary>
    /// <remarks>
    /// A coordinator suite never runs the real canonical transaction, so the values themselves are
    /// arbitrary — what has to hold is that the pair is coherent, because an incoherent one is refused
    /// before the transaction opens anything and every case here would then fail for the wrong reason.
    /// </remarks>
    private static readonly CovenantCanonicalDatasetTransition PreselectedDataset = new(
        new Guid("66666666-6666-4666-8666-666666666666"),
        new CovenantOfflineTransitionEpochsV1(4, 9, 16),
        new Guid("77777777-7777-4777-8777-777777777777"),
        new CovenantOfflineTransitionEpochsV1(5, 10, 17));

    private static readonly Guid CandidateGeneration = new("99999999-9999-4999-8999-999999999999");

    /// <summary>
    /// The epoch tuple every launch in this suite was planned against, and the successor tuple it
    /// preselected.
    /// </summary>
    /// <remarks>
    /// Each target epoch is its own source plus one, which is the only relation the codec accepts. A
    /// harness that committed a launch the decoder refuses would prove nothing at all about resuming
    /// one: every resume assertion below would pass through the same "unresumable" arm no matter what
    /// the coordinator did with it.
    /// </remarks>
    private static CovenantOfflineTransitionEpochsV1 SourceEpochs => new(1, 1, 1);

    private static CovenantOfflineTransitionEpochsV1 TargetEpochs => new(2, 2, 2);

    /// <summary>
    /// The full ordered step log of a clean erasure, and the only place the whole sequence is written
    /// down in one literal.
    /// </summary>
    private static readonly string[] CleanRunSteps =
    [
        "quiesce-writer",
        "erase-artifacts",
        "apply-canonical",
        "erase-managed-files",
        "close-handles",
        "truncate-wal",
        "compact",
        "initialize-accelerator",
        "truncate-wal",
        "verify-sidecar-absence",
        "verify-reopen",
        "publish",
        "reopen-writer",
    ];

    [Fact]
    public async Task Factory_continuation_runs_after_its_durable_boundary_and_before_handle_proof()
    {

        CoordinatorHarness harness = new(CovenantExclusiveOperation.HealthyCatalogFactoryErasure);

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess, completion.IsFailure ? completion.Error.Message : null);

        Assert.True(
            harness.Steps.IndexOf("erase-managed-files")
            < harness.Steps.IndexOf("apply-ordinary-factory-reset"));

        Assert.True(
            harness.Steps.IndexOf("apply-ordinary-factory-reset")
            < harness.Steps.IndexOf("close-handles"));

    }

    [Theory]
    [InlineData(CovenantResetPhase.ManagedArtifactsProcessed, 1)]
    [InlineData(CovenantResetPhase.HandlesClosed, 0)]
    [InlineData(CovenantResetPhase.SidecarsVerified, 0)]
    public async Task Factory_recovery_reruns_continuation_only_before_HandlesClosed_is_durable(
        CovenantResetPhase phase,
        int expectedCalls)
    {

        CoordinatorHarness harness = new(CovenantExclusiveOperation.HealthyCatalogFactoryErasure);

        await harness.CloseAndAdoptAsync();

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(phase);

        Assert.True(completion.IsSuccess, completion.IsFailure ? completion.Error.Message : null);

        Assert.Equal(expectedCalls, harness.FactoryContinuationCalls);

    }

    [Fact]
    public async Task Factory_continuation_failure_keeps_admission_closed_at_ManagedArtifactsProcessed()
    {

        CoordinatorHarness harness = new(CovenantExclusiveOperation.HealthyCatalogFactoryErasure)
        {

            FactoryContinuationFailure = new Error(
                ErrorCodes.Data.ReconciliationFailed,
                "Ordinary factory deletion did not reconcile."),

        };

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, completion.Value.Disposition);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, completion.Value.BlockingErrorCode);

        Assert.DoesNotContain("close-handles", harness.Steps);

        LongRunningOperation durable = (await harness.Store.GetAsync(OperationId))!;

        Assert.Equal(
            CovenantResetPhase.ManagedArtifactsProcessed,
            CovenantRecoveryCheckpointCodec
                .DecodeDataRetentionFactoryReset(durable.CheckpointPayload!)
                .Value
                .Phase);

        Assert.False(await harness.AdmissionIsOpenAsync());

    }

    [Fact]
    public async Task Four_argument_reset_overload_refuses_a_factory_checkpoint_before_gate_effects()
    {

        CoordinatorHarness harness = new(CovenantExclusiveOperation.HealthyCatalogFactoryErasure);

        Result<CovenantErasureCompletion> completion = await harness.RunWithoutFactoryContinuationAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, completion.Error.Code);

        Assert.Equal(0, harness.Gate.ResumeOrAcquireCount);

        Assert.Empty(harness.Steps);

    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.CovenantReset)]
    [InlineData(CovenantExclusiveOperation.HealthyCatalogFactoryErasure)]
    public async Task A_clean_run_erases_through_both_kernels_then_proves_storage_then_publishes(
        CovenantExclusiveOperation operation)
    {

        CoordinatorHarness harness = new(operation);

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, completion.Value.Disposition);

        Assert.Null(completion.Value.BlockingErrorCode);

        // The database half commits before a single managed file is touched. The managed-file kernel
        // persists its durable work item before its first external effect, and that work item is a
        // database row: a file deleted before the transaction that authorized it would be a deletion
        // no surviving row can explain.
        string[] expectedSteps = operation == CovenantExclusiveOperation.HealthyCatalogFactoryErasure
            ? [.. CleanRunSteps[..4], "apply-ordinary-factory-reset", .. CleanRunSteps[4..]]
            : CleanRunSteps;

        Assert.Equal(expectedSteps, harness.Steps);

        Assert.Equal(
            CovenantOperationGateFixture.DatasetGeneration,
            harness.Inventory.ObservedDatasetGeneration);

        Assert.True(await harness.AdmissionIsOpenAsync());

    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.CovenantReset)]
    [InlineData(CovenantExclusiveOperation.HealthyCatalogFactoryErasure)]
    public async Task Both_kernels_borrow_the_coordinators_own_lease_and_never_acquire_one(
        CovenantExclusiveOperation operation)
    {

        CoordinatorHarness harness = new(operation);

        _ = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.NotNull(harness.Artifacts.LastAuthority);

        Assert.Equal(CovenantArtifactErasureAuthorityKind.Exclusive, harness.Artifacts.LastAuthority.Kind);

        Assert.Equal(operation, harness.Artifacts.LastAuthority.ExclusiveOperation);

        // The same object, not an equal one. Two authorities would be two borrowed capabilities, and
        // only one of them is the lease this coordinator actually holds.
        Assert.Same(harness.Artifacts.LastAuthority, harness.ManagedFiles.LastAuthority);

        Assert.Equal(CovenantLeaseCoverage.Installation, harness.Artifacts.LastAuthority.Snapshot.Coverage);

    }

    [Fact]
    public async Task Caller_cancellation_after_storage_proof_cannot_cancel_checkpoint_publication_or_reopen()
    {

        CoordinatorHarness harness = new();

        using CancellationTokenSource caller = new();

        harness.Operations.HonorCancellation = true;

        harness.Transition.OnVerified = caller.Cancel;

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared,
            caller.Token);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, completion.Value.Disposition);

        Assert.False(harness.Operations.LastCheckpointToken.IsCancellationRequested);

        Assert.False(harness.Transition.PublicationToken.IsCancellationRequested);

        Assert.False(harness.DisclosureWriter.ReopenToken.IsCancellationRequested);

    }

    [Fact]
    public async Task A_database_kernel_blocker_keeps_admission_closed_and_never_applies_the_canonical_erasure()
    {

        CoordinatorHarness harness = new();

        harness.Artifacts.Blocker = CovenantErasureBlocker.ManualOwnershipMismatch;

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, completion.Value.Disposition);

        Assert.Equal(ErrorCodes.Covenant.ManualArtifactErasureRequired, completion.Value.BlockingErrorCode);

        Assert.DoesNotContain("apply-canonical", harness.Steps);

        Assert.False(completion.Value.CanonicalResetApplied);

        Assert.False(await harness.AdmissionIsOpenAsync());

    }

    [Fact]
    public async Task A_managed_file_blocker_keeps_admission_closed_after_the_canonical_erasure_committed()
    {

        CoordinatorHarness harness = new();

        harness.ManagedFiles.Blocker = CovenantErasureBlocker.ManualOwnershipMismatch;

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, completion.Value.Disposition);

        // A blocker is not an abort. The canonical erasure already committed, one managed file is
        // provably still on disk, and the operator has to resolve it — so the operation stays
        // adoptable rather than being rolled back into a state nobody can resume.
        Assert.Contains("apply-canonical", harness.Steps);

        Assert.DoesNotContain("close-handles", harness.Steps);

        Assert.True(completion.Value.CanonicalResetApplied);

        Assert.False(completion.Value.LocalSecureErasureComplete);

        Assert.False(await harness.AdmissionIsOpenAsync());

    }

    [Theory]
    [InlineData(CovenantResetPhase.CanonicalApplied, "erase-artifacts")]
    [InlineData(CovenantResetPhase.CanonicalApplied, "apply-canonical")]
    [InlineData(CovenantResetPhase.ManagedArtifactsProcessed, "erase-managed-files")]
    [InlineData(CovenantResetPhase.HandlesClosed, "close-handles")]
    [InlineData(CovenantResetPhase.DatabaseCompacted, "compact")]
    [InlineData(CovenantResetPhase.AcceleratorInitialized, "initialize-accelerator")]
    [InlineData(CovenantResetPhase.SidecarsVerified, "verify-sidecar-absence")]
    public async Task A_resumed_run_never_repeats_a_step_its_checkpoint_already_records(
        CovenantResetPhase resumeFrom,
        string skippedStep)
    {

        CoordinatorHarness harness = new();

        await harness.CloseAndAdoptAsync();

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(resumeFrom);

        Assert.True(completion.IsSuccess);

        Assert.DoesNotContain(skippedStep, harness.Steps);

    }

    [Theory]
    [InlineData(CovenantResetPhase.InventoryPrepared)]
    [InlineData(CovenantResetPhase.CanonicalApplied)]
    [InlineData(CovenantResetPhase.ManagedArtifactsProcessed)]
    [InlineData(CovenantResetPhase.HandlesClosed)]
    [InlineData(CovenantResetPhase.WalTruncated)]
    [InlineData(CovenantResetPhase.DatabaseCompacted)]
    [InlineData(CovenantResetPhase.AcceleratorInitialized)]
    [InlineData(CovenantResetPhase.FinalWalTruncated)]
    [InlineData(CovenantResetPhase.SidecarsVerified)]
    [InlineData(CovenantResetPhase.ReopenedVerified)]
    public async Task Recovery_from_every_declared_phase_reaches_the_same_committed_reopen(
        CovenantResetPhase resumeFrom)
    {

        CoordinatorHarness harness = new();

        if (resumeFrom != CovenantResetPhase.InventoryPrepared)
        {

            await harness.CloseAndAdoptAsync();

        }

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(resumeFrom);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, completion.Value.Disposition);

        // Publication and the writer restart follow the last recorded phase rather than being one of
        // them, so every resume performs both exactly once no matter where it re-entered.
        Assert.Contains("publish", harness.Steps);

        Assert.Contains("reopen-writer", harness.Steps);

        Assert.True(await harness.AdmissionIsOpenAsync());

    }

    [Fact]
    public async Task A_fresh_run_acquires_the_gate_and_a_resumed_run_resumes_it()
    {

        CoordinatorHarness fresh = new();

        _ = await fresh.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.Equal(0, fresh.Gate.AcquireCount);

        Assert.Equal(1, fresh.Gate.ResumeOrAcquireCount);

        Assert.Equal(0, fresh.Gate.ResumeCount);

        CoordinatorHarness resumed = new();

        // The setup's own closure goes straight to the inner gate, so these counters see only what
        // the coordinator itself did.
        await resumed.CloseAndAdoptAsync();

        _ = await resumed.RunAsync(CovenantResetPhase.HandlesClosed);

        // Acquiring a scope that is already closed under this owner would be a second closure of a
        // scope this operation never reopened, and the gate refuses it. Resume is the only correct
        // verb for a checkpoint past its first phase.
        Assert.Equal(0, resumed.Gate.AcquireCount);

        Assert.Equal(0, resumed.Gate.ResumeOrAcquireCount);

        Assert.Equal(1, resumed.Gate.ResumeCount);

    }

    [Fact]
    public async Task Resume_from_ReopenedVerified_reverifies_immutably_and_publishes_that_exact_candidate()
    {

        CoordinatorHarness harness = new();

        await harness.CloseAndAdoptAsync();

        Result<CovenantErasureCompletion> completion =
            await harness.RunAsync(CovenantResetPhase.ReopenedVerified);

        Assert.True(completion.IsSuccess);

        Assert.Equal(1, harness.Transition.VerifyReopenCalls);

        Assert.Same(harness.Transition.VerifiedCandidate, harness.Transition.PublishedCandidate);

        Assert.Equal(0, harness.Operations.CheckpointCalls);

    }

    [Fact]
    public async Task A_fresh_pass_checkpoints_one_live_verified_candidate_and_publishes_that_exact_object()
    {

        CoordinatorHarness harness = new();

        _ = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.Equal(1, harness.Transition.VerifyReopenCalls);

        Assert.Same(harness.Transition.VerifiedCandidate, harness.Transition.PublishedCandidate);

        Assert.Equal(CandidateGeneration, harness.Transition.PublishedGeneration);

    }

    [Fact]
    public void The_transition_seam_has_no_ordinary_candidate_generation_read()
    {

        Assert.DoesNotContain(
            typeof(ICovenantErasureTransition).GetMethods(),
            static method => string.Equals(
                method.Name,
                "ReadCandidateDatasetGenerationAsync",
                StringComparison.Ordinal));

    }

    [Fact]
    public async Task A_failed_publication_keeps_admission_closed_after_a_durable_mutation()
    {

        CoordinatorHarness harness = new();

        harness.Transition.FailingStep = "publish";

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, completion.Value.Disposition);

        Assert.Contains("apply-canonical", harness.Steps);

        Assert.DoesNotContain("reopen-writer", harness.Steps);

        // The erasure itself is still proven. Publication is what failed, and the two facts have to
        // stay separable or an operator cannot tell a half-published reset from a half-erased one.
        Assert.True(completion.Value.LocalSecureErasureComplete);

        Assert.False(await harness.AdmissionIsOpenAsync());

    }

    [Fact]
    public async Task A_failed_disclosure_writer_restart_selects_keep_closed_before_any_disposition()
    {

        CoordinatorHarness harness = new();

        harness.DisclosureWriter.ReopenFails = true;

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, completion.Value.Disposition);

        Assert.Contains("publish", harness.Steps);

        Assert.False(await harness.AdmissionIsOpenAsync());

    }

    [Theory]
    [InlineData(DispositionFailureMode.ReturnedFailure)]
    [InlineData(DispositionFailureMode.Cancelled)]
    [InlineData(DispositionFailureMode.Thrown)]
    public async Task A_failed_one_shot_commit_records_recoverability_without_a_fallback_disposition(
        DispositionFailureMode failureMode)
    {

        // The real gate refuses to hand a second live registration to a scope that already has one,
        // so a disposition can only fail underneath the lease rather than beside it. The registration
        // is faked and the lease is not: the one-shot claim under test is the real one.
        CoordinatorHarness harness = new(dispositionFailure: failureMode);

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsFailure);

        // The failure is reported as itself. A second disposition would change nothing an operator can
        // observe — the gate is already closed — while replacing the real reason with the lease's own
        // LifecycleConflict, which is the one message that explains nothing.
        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, completion.Error.Code);

        Assert.Equal(1, harness.Gate.DispositionAttempts);

        Assert.Equal(0, harness.Gate.KeepClosedAttempts);

        Assert.Equal(1, harness.Gate.DisposalCount);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, harness.Gate.LastDisposition);

        Assert.False(await harness.AdmissionIsOpenAsync());

        LongRunningOperation durable = (await harness.Store.GetAsync(OperationId))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, durable.State);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, durable.TerminalErrorCode);

    }

    public enum DispositionFailureMode
    {

        None,

        ReturnedFailure,

        Cancelled,

        Thrown,

    }

    public enum LifecycleFailureRaceMode
    {

        TransientMissing,

        WrongAttentionCode,

        ChangedCheckpoint,

        NonActiveCheckpoint,

        ValidWinner,

    }

    [Theory]
    [InlineData(LifecycleFailureRaceMode.TransientMissing)]
    [InlineData(LifecycleFailureRaceMode.WrongAttentionCode)]
    [InlineData(LifecycleFailureRaceMode.ChangedCheckpoint)]
    [InlineData(LifecycleFailureRaceMode.NonActiveCheckpoint)]
    [InlineData(LifecycleFailureRaceMode.ValidWinner)]
    public async Task A_failed_disposition_retries_until_a_fresh_read_proves_recoverability(
        LifecycleFailureRaceMode raceMode)
    {

        CoordinatorHarness harness = new(dispositionFailure: DispositionFailureMode.ReturnedFailure);

        int reads = 0;

        int invalidTransitions = 0;

        bool nonActiveReadOutstanding = false;

        harness.Gate.OnDispositionAttempt = () =>
        {

            if (raceMode == LifecycleFailureRaceMode.WrongAttentionCode)
            {

                LongRunningOperation current = Assert.Single(
                    harness.Store.Operations,
                    static operation => operation.Id == OperationId);

                harness.Store.Add(
                    current with
                    {
                        State = LongRunningOperationState.ReconciliationRequired,
                        LeaseOwner = null,
                        LeaseExpiresAt = null,
                        TerminalErrorCode = "Covenant.UnrecognizedAttention",
                        Revision = current.Revision + 1,
                    });

            }

            harness.Store.GetOverride = current =>
            {

                int read = Interlocked.Increment(ref reads);

                if (raceMode == LifecycleFailureRaceMode.TransientMissing && read == 1)
                {

                    return null;

                }

                if (raceMode == LifecycleFailureRaceMode.ChangedCheckpoint && read == 1)
                {

                    return current! with { CheckpointPayload = [0] };

                }

                if (raceMode == LifecycleFailureRaceMode.NonActiveCheckpoint && read == 1)
                {

                    nonActiveReadOutstanding = true;

                    return current! with
                    {
                        State = LongRunningOperationState.Completed,
                        CompletedAt = DateTimeOffset.UtcNow,
                        LeaseOwner = null,
                        LeaseExpiresAt = null,
                    };

                }

                nonActiveReadOutstanding = false;

                return current;

            };

            if (raceMode == LifecycleFailureRaceMode.ValidWinner)
            {

                harness.Store.TryTransitionOverride = current =>
                {

                    Assert.NotNull(current);

                    harness.Store.Add(
                        current with
                        {
                            State = LongRunningOperationState.ReconciliationRequired,
                            LeaseOwner = null,
                            LeaseExpiresAt = null,
                            TerminalErrorCode = ErrorCodes.Covenant.MaintenanceFailed,
                            Revision = current.Revision + 1,
                        });

                    return false;

                };

            }
            else if (raceMode == LifecycleFailureRaceMode.NonActiveCheckpoint)
            {

                harness.Store.TryTransitionOverride = current =>
                {

                    _ = current;

                    if (!nonActiveReadOutstanding)
                    {

                        return null;

                    }

                    _ = Interlocked.Increment(ref invalidTransitions);

                    return false;

                };

            }

        };

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsFailure);

        Assert.Equal(1, harness.Gate.DispositionAttempts);

        Assert.Equal(0, harness.Gate.KeepClosedAttempts);

        Assert.Equal(0, invalidTransitions);

        Assert.True(reads >= 2);

        LongRunningOperation durable = (await harness.Store.GetAsync(OperationId))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, durable.State);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, durable.TerminalErrorCode);

    }

    [Fact]
    public async Task A_quiesce_failure_before_any_erasure_rolls_back_and_reopens()
    {

        CoordinatorHarness harness = new();

        harness.DisclosureWriter.QuiesceFails = true;

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        // Nothing was erased and nothing was even attempted, so this is the one shape of failure that
        // may reopen: a proven pre-erasure abort. Everything from the first kernel call onwards keeps
        // the gate closed instead.
        Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, completion.Value.Disposition);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, completion.Value.BlockingErrorCode);

        Assert.Equal(0, harness.Artifacts.Calls);

        Assert.Equal(0, harness.ManagedFiles.Calls);

        Assert.False(completion.Value.CanonicalResetApplied);

        Assert.Equal(["quiesce-writer", "reopen-writer"], harness.Steps);

        Assert.True(await harness.AdmissionIsOpenAsync());

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_quiesce_throw_or_cancellation_restores_the_old_writer_before_rollback(
        bool cancellation)
    {

        CoordinatorHarness harness = new();

        harness.DisclosureWriter.QuiesceException = cancellation
            ? new OperationCanceledException()
            : new InvalidOperationException("private writer detail");

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, completion.Value.Disposition);

        Assert.Equal(["quiesce-writer", "reopen-writer"], harness.Steps);

        Assert.True(await harness.AdmissionIsOpenAsync());

    }

    [Fact]
    public async Task An_inventory_failure_before_any_erasure_rolls_back_and_reopens()
    {

        CoordinatorHarness harness = new();

        harness.Inventory.Fails = true;

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, completion.Value.Disposition);

        Assert.Equal(0, harness.Artifacts.Calls);

        Assert.Equal(["quiesce-writer", "reopen-writer"], harness.Steps);

        Assert.True(await harness.AdmissionIsOpenAsync());

    }

    [Fact]
    public async Task A_factory_catalog_refusal_restores_the_old_writer_before_rollback()
    {

        CoordinatorHarness harness = new(CovenantExclusiveOperation.HealthyCatalogFactoryErasure);

        harness.Inventory.Fails = true;

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, completion.Value.Disposition);

        Assert.Equal(["quiesce-writer", "reopen-writer"], harness.Steps);

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_pre_effect_throw_or_cancellation_restores_the_old_writer_before_rollback(
        bool cancellation)
    {

        CoordinatorHarness harness = new();

        harness.Inventory.Exception = cancellation
            ? new OperationCanceledException()
            : new InvalidOperationException("private inventory detail");

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, completion.Value.Disposition);

        Assert.Equal(["quiesce-writer", "reopen-writer"], harness.Steps);

    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_database_replay_read_failure_before_the_first_kernel_restores_then_rolls_back(
        int failureKind)
    {

        CoordinatorHarness harness = new();

        if (failureKind == 0)
        {

            harness.Inventory.DatabaseBatchFails = true;

        }
        else
        {

            harness.Inventory.DatabaseBatchException = failureKind == 1
                ? new InvalidOperationException("private page detail")
                : new OperationCanceledException();

        }

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, completion.Value.Disposition);

        Assert.Equal(0, harness.Artifacts.Calls);

        Assert.Equal(["quiesce-writer", "reopen-writer"], harness.Steps);

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Malformed_nonempty_managed_evidence_in_real_inventory_restores_then_rolls_back_before_any_kernel(
        bool corruptDurableLocation)
    {

        await using CovenantErasureInventorySourceTests.InventoryFixture fixture =
            await CovenantErasureInventorySourceTests.InventoryFixture.CreateAsync(healthyCatalog: false);

        await fixture.SeedInterleavedLabelsAsync(2, includeExistingWorkItem: false);

        await fixture.CorruptManagedProducerEvidenceAsync(corruptDurableLocation);

        CoordinatorHarness harness = new(
            inventory: fixture.CreateSource(),
            datasetGeneration: await fixture.ReadDatasetGenerationAsync());

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, completion.Value.Disposition);

        Assert.Equal(ErrorCodes.Covenant.ManualArtifactErasureRequired, completion.Value.BlockingErrorCode);

        Assert.Equal(0, harness.Artifacts.Calls);

        Assert.Equal(0, harness.ManagedFiles.Calls);

        Assert.DoesNotContain("apply-canonical", harness.Steps);

        Assert.Equal(["quiesce-writer", "reopen-writer"], harness.Steps);

    }

    [Fact]
    public async Task A_failed_old_writer_restoration_selects_one_keep_closed_disposition()
    {

        CoordinatorHarness harness = new();

        harness.Inventory.Fails = true;

        harness.DisclosureWriter.ReopenFails = true;

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, completion.Value.Disposition);

        Assert.Equal(1, harness.Gate.DispositionAttempts);

        Assert.Equal(["quiesce-writer", "reopen-writer"], harness.Steps);

        Assert.False(await harness.AdmissionIsOpenAsync());

    }

    [Fact]
    public async Task A_lease_that_cannot_drain_calls_no_kernel_and_changes_nothing()
    {

        CoordinatorHarness harness = new(drainTimeout: TimeSpan.FromMilliseconds(150));

        // A live installation read lease that never releases is exactly what a reset has to wait for,
        // and exactly what it must refuse to run past.
        CovenantInstallationReadLease reader =
            (await harness.Gate.Inner.AcquireInstallationReadAsync(Token)).Value;

        try
        {

            Result<CovenantErasureCompletion> completion =
                await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

            Assert.True(completion.IsFailure);

            Assert.Equal(0, harness.Artifacts.Calls);

            Assert.Equal(0, harness.ManagedFiles.Calls);

            Assert.Empty(harness.Steps);

        }
        finally
        {

            await reader.DisposeAsync();

        }

    }

    [Fact]
    public async Task An_empty_lease_dataset_is_refused_before_inventory()
    {

        CoordinatorHarness harness = new(emptyLeaseDataset: true);

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, completion.Error.Code);

        Assert.Null(harness.Inventory.ObservedDatasetGeneration);

        Assert.Empty(harness.Steps);

    }

    [Fact]
    public async Task The_disposition_runs_on_its_own_lifecycle_token_after_the_caller_token_is_cancelled()
    {

        CoordinatorHarness harness = new();

        using CancellationTokenSource caller = new();

        // The caller's token is an HTTP request token in production, and the gate's disposition throws
        // on a cancelled one. Cancelling after the durable mutation must not be able to strand a reset
        // with admission closed, so the reopening decision is taken on a bounded token the coordinator
        // owns.
        harness.Transition.OnStep = step =>
        {

            if (string.Equals(step, "verify-reopen", StringComparison.Ordinal))
            {

                caller.Cancel();

            }

        };

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared,
            caller.Token);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, completion.Value.Disposition);

        Assert.True(await harness.AdmissionIsOpenAsync());

    }

    [Fact]
    public async Task The_three_status_facts_are_reported_independently()
    {

        CoordinatorHarness canonicalOnly = new();

        canonicalOnly.Transition.FailingStep = "compact";

        Result<CovenantErasureCompletion> stopped =
            await canonicalOnly.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(stopped.IsSuccess);

        Assert.True(stopped.Value.CanonicalResetApplied);

        Assert.False(stopped.Value.LocalSecureErasureComplete);

        CoordinatorHarness complete = new();

        complete.Inventory.ExternalDisclosuresNotRevocable = false;

        Result<CovenantErasureCompletion> finished =
            await complete.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(finished.IsSuccess);

        Assert.True(finished.Value.CanonicalResetApplied);

        Assert.True(finished.Value.LocalSecureErasureComplete);

        // The third fact is about what local work cannot revoke, so it never follows from the other
        // two. A completed erasure with no receipt-backed disclosure reports false while both local
        // facts are true.
        Assert.False(finished.Value.ExternalDisclosuresNotRevocable);

    }

    [Theory]
    [InlineData(CovenantResetPhase.InventoryPrepared, 1, 0, 1, 1, 0)]
    [InlineData(CovenantResetPhase.CanonicalApplied, 0, 1, 0, 1, 1)]
    [InlineData(CovenantResetPhase.ManagedArtifactsProcessed, 0, 0, 0, 0, 1)]
    public async Task Each_phase_runs_only_its_required_physical_inventory_passes(
        CovenantResetPhase resumeFrom,
        int firstPreflightCalls,
        int remainingManagedPreflightCalls,
        int databaseBatchCalls,
        int managedBatchCalls,
        int exposureCalls)
    {

        CoordinatorHarness harness = new();

        if (resumeFrom != CovenantResetPhase.InventoryPrepared)
        {

            await harness.CloseAndAdoptAsync();

        }

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(resumeFrom);

        Assert.True(completion.IsSuccess);

        Assert.Equal(firstPreflightCalls, harness.Inventory.FirstPreflightCalls);

        Assert.Equal(remainingManagedPreflightCalls, harness.Inventory.RemainingManagedPreflightCalls);

        Assert.Equal(databaseBatchCalls, harness.Inventory.DatabaseBatchCalls);

        Assert.Equal(managedBatchCalls, harness.Inventory.ManagedBatchCalls);

        Assert.Equal(exposureCalls, harness.Inventory.ExposureCalls);

    }

    [Fact]
    public async Task Resumed_completion_rereads_and_preserves_the_exact_disclosure_exposure()
    {

        CoordinatorHarness harness = new();

        harness.Inventory.Exposure = new CovenantDisclosureExposure(
            7,
            CovenantDisclosureCountKind.LowerBound);

        await harness.CloseAndAdoptAsync();

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.HandlesClosed);

        Assert.True(completion.IsSuccess);

        Assert.Equal(7, completion.Value.Exposure.PossibleAttempts);

        Assert.Equal(CovenantDisclosureCountKind.LowerBound, completion.Value.Exposure.CountKind);

        Assert.True(completion.Value.ExternalDisclosuresNotRevocable);

        Assert.Equal(1, harness.Inventory.ExposureCalls);

        Assert.Equal(0, harness.Inventory.RemainingManagedPreflightCalls);

    }

    [Fact]
    public async Task A_checkpoint_whose_owner_disagrees_with_the_operation_row_refuses()
    {

        CoordinatorHarness harness = new();

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(
            CovenantResetPhase.InventoryPrepared,
            checkpointOperationId: new Guid("77777777-7777-4777-8777-777777777777"));

        Assert.True(completion.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, completion.Error.Code);

        Assert.Equal(0, harness.Gate.AcquireCount);

        Assert.Equal(0, harness.Gate.ResumeCount);

    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.CovenantFamilyReinitialize)]
    [InlineData(CovenantExclusiveOperation.BackupRestore)]
    [InlineData(CovenantExclusiveOperation.SchemaRepair)]
    [InlineData(CovenantExclusiveOperation.CampaignDelete)]
    public async Task An_operation_outside_the_two_erasure_kinds_never_reaches_the_gate(
        CovenantExclusiveOperation operation)
    {

        CoordinatorHarness harness = new(operation);

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsFailure);

        // Reinitialize already owns its own coordinator and its own effect-digest domain. Letting it
        // through here would give one destructive operation two owners, and a resume would not know
        // which one closed the scope.
        Assert.Equal(0, harness.Gate.AcquireCount);

        Assert.Equal(0, harness.Gate.ResumeCount);

    }

    [Fact]
    public void The_coordinator_holds_no_managed_file_capability_opener_or_ownership_verifier()
    {

        ConstructorInfo constructor = Assert.Single(typeof(CovenantErasureCoordinator).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        string[] dependencies =
        [
            .. constructor.GetParameters().Select(static parameter => parameter.ParameterType.Name),
        ];

        // Structural rather than a convention somebody has to remember. A coordinator that could open
        // a managed-file capability could delete a file, and then there would be two implementations
        // of "which file is Arcanum's" — only one of which can be right.
        Assert.DoesNotContain("IManagedFileCapabilityOpener", dependencies);

        Assert.DoesNotContain("IManagedFileOwnershipVerifier", dependencies);

        FieldInfo[] fields = typeof(CovenantErasureCoordinator)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.DoesNotContain(
            fields,
            static field =>
                field.FieldType.Name is "IManagedFileCapabilityOpener" or "IManagedFileOwnershipVerifier");

    }

    private sealed class CoordinatorHarness
    {

        private readonly CovenantExclusiveOperation _operation;

        private readonly ICovenantErasureInventorySource _inventory;

        internal RecordingGate Gate { get; }

        /// <summary>
        /// One ordered log across both kernels, the disclosure writer, and the storage owner, because
        /// the ordering between them is the property under test.
        /// </summary>
        internal List<string> Steps { get; } = [];

        internal RecordingErasureKernel Artifacts { get; }

        internal RecordingManagedFileKernel ManagedFiles { get; }

        internal RecordingErasureTransition Transition { get; }

        internal RecordingDisclosureWriterLifecycle DisclosureWriter { get; }

        internal StubInventorySource Inventory { get; }

        internal FakeLongRunningOperationStore Store { get; } = new(TimeProvider.System);

        internal RecordingOperationCoordinator Operations { get; }

        internal int FactoryContinuationCalls { get; private set; }

        internal Error? FactoryContinuationFailure { get; init; }

        internal CoordinatorHarness(
            CovenantExclusiveOperation operation = CovenantExclusiveOperation.CovenantReset,
            TimeSpan? drainTimeout = null,
            DispositionFailureMode dispositionFailure = DispositionFailureMode.None,
            bool emptyLeaseDataset = false,
            ICovenantErasureInventorySource? inventory = null,
            Guid? datasetGeneration = null)
        {

            _operation = operation;

            Operations = new RecordingOperationCoordinator(Store);

            FakeCovenantAvailability? availability = null;

            if (datasetGeneration is { } generation)
            {

                availability = new FakeCovenantAvailability();

                availability.Mutate(
                    current => current with
                    {
                        DatasetGeneration = generation,
                        AppliedDatasetGeneration = generation,
                    });

            }

            Gate = new RecordingGate(
                CovenantOperationGateFixture.CreateGate(
                    availability: availability,
                    drainTimeout: drainTimeout),
                dispositionFailure,
                emptyLeaseDataset);

            Artifacts = new RecordingErasureKernel(Steps);

            ManagedFiles = new RecordingManagedFileKernel(Steps);

            Transition = new RecordingErasureTransition(Steps);

            DisclosureWriter = new RecordingDisclosureWriterLifecycle(Steps);

            Inventory = new StubInventorySource();

            _inventory = inventory ?? Inventory;

        }

        /// <summary>
        /// Whether ordinary work can be admitted again — the only honest reading of "did this reset
        /// reopen the door".
        /// </summary>
        internal async Task<bool> AdmissionIsOpenAsync()
        {

            Result<CovenantInstallationReadLease> read =
                await Gate.Inner.AcquireInstallationReadAsync(Token);

            if (read.IsFailure)
            {

                return false;

            }

            await read.Value.DisposeAsync();

            return true;

        }

        /// <summary>
        /// Closes the scope under the exact recovery owner and releases the live registration, which is
        /// the state a crash mid-erasure leaves behind.
        /// </summary>
        internal async Task CloseAndAdoptAsync()
        {

            CovenantExclusiveLease lease = (await Gate.Inner.AcquireExclusiveAsync(Owner, Token)).Value;

            _ = await lease.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token);

            await lease.DisposeAsync();

        }

        internal async Task<Result<CovenantErasureCompletion>> RunAsync(
            CovenantResetPhase phase,
            CancellationToken? cancellationToken = null,
            Guid? checkpointOperationId = null)
        {

            LongRunningOperation operation = Operation();

            Store.Add(operation);

            CovenantErasureCoordinator coordinator = new(
                Operations,
                Store,
                Gate,
                Artifacts,
                ManagedFiles,
                _inventory,
                Transition,
                DisclosureWriter,
                TimeProvider.System,
                NullLogger<CovenantErasureCoordinator>.Instance);

            CovenantErasureCheckpointState checkpoint = new(
                checkpointOperationId ?? OperationId,
                _operation,
                CovenantOperationGateFixture.Digest(7),
                phase,
                PreselectedDataset);

            if (_operation == CovenantExclusiveOperation.HealthyCatalogFactoryErasure)
            {

                return await coordinator.RunAsync(
                    operation,
                    checkpoint,
                    "owner",
                    RunFactoryContinuationAsync,
                    cancellationToken ?? Token);

            }

            return await coordinator.RunAsync(
                operation,
                checkpoint,
                "owner",
                cancellationToken ?? Token);

        }

        internal async Task<Result<CovenantErasureCompletion>> RunWithoutFactoryContinuationAsync(
            CovenantResetPhase phase)
        {

            LongRunningOperation operation = Operation();

            Store.Add(operation);

            CovenantErasureCoordinator coordinator = new(
                Operations,
                Store,
                Gate,
                Artifacts,
                ManagedFiles,
                _inventory,
                Transition,
                DisclosureWriter,
                TimeProvider.System,
                NullLogger<CovenantErasureCoordinator>.Instance);

            return await coordinator.RunAsync(
                operation,
                new CovenantErasureCheckpointState(
                    OperationId,
                    _operation,
                    CovenantOperationGateFixture.Digest(7),
                    phase,
                    PreselectedDataset),
                "owner",
                Token);

        }

        private Task<Result> RunFactoryContinuationAsync(CancellationToken cancellationToken)
        {

            _ = cancellationToken;

            FactoryContinuationCalls++;

            Steps.Add("apply-ordinary-factory-reset");

            return Task.FromResult(
                FactoryContinuationFailure is { } failure
                    ? Result.Failure(failure)
                    : Result.Success());

        }

        private CovenantExclusiveRecoveryOwner Owner =>
            new(OperationId, _operation, CovenantOperationGateFixture.Digest(7));

        /// <summary>
        /// The durable row an erasure resumes from: one committed launch, and nothing about progress.
        /// </summary>
        /// <remarks>
        /// The phase a run re-enters at is handed to the coordinator rather than encoded here, because
        /// a launch records only what was committed to. A row that also carried the phase would be a
        /// second authority for a fact the authenticated journal now owns, and the coordinator would
        /// have two answers to choose between on the first occasion they disagreed.
        ///
        /// <para>The preselected target is the very generation the faked transition goes on to stamp,
        /// so the row and the run describe one plan rather than two that happen to run together.</para>
        /// </remarks>
        private LongRunningOperation Operation()
        {

            string effectDigest = CovenantRecoveryCheckpointCodec.EncodeEffectDigest(
                CovenantOperationGateFixture.Digest(7));

            (int version, byte[] payload, string kind, LongRunningOperationRecoveryPolicy policy) =
                _operation == CovenantExclusiveOperation.HealthyCatalogFactoryErasure
                    ? (
                        DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
                        CovenantRecoveryCheckpointCodec.Encode(
                            new DataRetentionFactoryTransitionLaunchV2(
                                DataRetentionFactoryTransitionLaunchV2.CurrentVersion,
                                OperationId,
                                LongRunningOperationKinds.DataRetentionFactoryReset,
                                nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
                                _operation,
                                effectDigest,
                                CovenantOperationGateFixture.DatasetGeneration,
                                CandidateGeneration,
                                SourceEpochs,
                                TargetEpochs,
                                StartingRevision: 0)),
                        LongRunningOperationKinds.DataRetentionFactoryReset,
                        LongRunningOperationRecoveryPolicy.RestartIdempotently)
                    : (
                        CovenantOfflineTransitionLaunchV4.CurrentVersion,
                        CovenantRecoveryCheckpointCodec.Encode(
                            new CovenantOfflineTransitionLaunchV4(
                                CovenantOfflineTransitionLaunchV4.CurrentVersion,
                                OperationId,
                                LongRunningOperationKinds.DataRetentionMutation,
                                nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
                                _operation,
                                effectDigest,
                                CovenantOperationGateFixture.DatasetGeneration,
                                CandidateGeneration,
                                SourceEpochs,
                                TargetEpochs,
                                StartingRevision: 0)),
                        LongRunningOperationKinds.DataRetentionMutation,
                        LongRunningOperationRecoveryPolicy.ReconcileAndComplete);

            return new(
                OperationId,
                kind,
                LongRunningOperationState.Running,
                policy,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                null,
                null,
                null,
                "owner",
                null,
                1,
                version,
                payload,
                CovenantResetCheckpointInitiator.CheckpointReference(kind, OperationId),
                "Erasing the Covenant.",
                null,
                1);

        }

    }

    /// <summary>
    /// The real gate, wrapped only to count which verb the coordinator used.
    /// </summary>
    /// <remarks>
    /// A wrapper rather than a fake, because acquire-versus-resume, lease draining, and the one-shot
    /// disposition are properties of the real gate that a stub would simply assert into existence.
    /// </remarks>
    private sealed class RecordingGate(
        CovenantOperationGate inner,
        DispositionFailureMode dispositionFailure,
        bool emptyLeaseDataset) : ICovenantOperationGate
    {

        private int _dispositionAttempts;

        private int _disposalCount;

        private readonly DispositionFailureMode _dispositionFailure = dispositionFailure;

        private readonly bool _emptyLeaseDataset = emptyLeaseDataset;

        internal CovenantOperationGate Inner { get; } = inner;

        internal int AcquireCount { get; private set; }

        internal int ResumeCount { get; private set; }

        internal int ResumeOrAcquireCount { get; private set; }

        internal int DispositionAttempts => Volatile.Read(ref _dispositionAttempts);

        internal int DisposalCount => Volatile.Read(ref _disposalCount);

        internal int KeepClosedAttempts { get; private set; }

        internal CovenantExclusiveLeaseDisposition? LastDisposition { get; private set; }

        internal Action? OnDispositionAttempt { get; set; }

        public async ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken)
        {

            AcquireCount++;

            return Observe(await Inner.AcquireExclusiveAsync(owner, cancellationToken));

        }

        public async ValueTask<Result<CovenantExclusiveLease>> ResumeOrAcquireExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken)
        {

            ResumeOrAcquireCount++;

            return Observe(await Inner.ResumeOrAcquireExclusiveAsync(owner, cancellationToken));

        }

        public async ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken)
        {

            ResumeCount++;

            return Observe(await Inner.ResumeExclusiveAsync(owner, cancellationToken));

        }

        /// <summary>
        /// Rebuilds the lease over an observing registration, keeping the real one-shot disposition
        /// logic and the real closure while making the registration's answer controllable.
        /// </summary>
        private Result<CovenantExclusiveLease> Observe(Result<CovenantExclusiveLease> acquired) =>
            acquired.IsFailure
                ? acquired
                : Result<CovenantExclusiveLease>.Success(
                    new CovenantExclusiveLease(new ObservingRegistration(this, acquired.Value)));

        private Result Record(CovenantExclusiveLeaseDisposition disposition)
        {

            _ = Interlocked.Increment(ref _dispositionAttempts);

            LastDisposition = disposition;

            OnDispositionAttempt?.Invoke();

            if (disposition == CovenantExclusiveLeaseDisposition.KeepClosed)
            {

                KeepClosedAttempts++;

            }

            return _dispositionFailure == DispositionFailureMode.ReturnedFailure
                ? Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.MaintenanceFailed,
                        "The gate could not record this reopening decision."))
                : Result.Success();

        }

        private sealed class ObservingRegistration(RecordingGate gate, CovenantExclusiveLease real)
            : ICovenantExclusiveLeaseRegistration
        {

            public CovenantOperationLeaseSnapshot Snapshot => gate._emptyLeaseDataset
                ? real.Snapshot with { DatasetGeneration = Guid.Empty }
                : real.Snapshot;

            public CancellationToken Revocation => real.Revocation;

            public Result ExecuteWhileHeld(Func<Result> callback) => real.ExecuteWhileHeld(callback);

            public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
                real.RevalidateAsync(cancellationToken);

            public async ValueTask ReleaseAsync()
            {

                _ = Interlocked.Increment(ref gate._disposalCount);

                await real.DisposeAsync();

            }

            public async ValueTask<Result> CompleteAsync(
                CovenantExclusiveLeaseDisposition disposition,
                CancellationToken cancellationToken)
            {

                if (gate._dispositionFailure == DispositionFailureMode.Cancelled)
                {

                    _ = gate.Record(disposition);

                    throw new OperationCanceledException(cancellationToken);

                }

                if (gate._dispositionFailure == DispositionFailureMode.Thrown)
                {

                    _ = gate.Record(disposition);

                    throw new InvalidOperationException("private gate detail");

                }

                Result recorded = gate.Record(disposition);

                // A refused disposition never reaches the real gate, so the scope stays closed — which
                // is precisely the state the caller then has to report rather than paper over.
                return recorded.IsFailure
                    ? recorded
                    : await real.CompleteAsync(disposition, cancellationToken);

            }

        }

        public ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(
            CancellationToken cancellationToken) => Inner.AcquireInstallationReadAsync(cancellationToken);

        public ValueTask<Result<CovenantReadLease>> AcquireReadAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) => Inner.AcquireReadAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) => Inner.AcquireWriteAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(
            CanonicalCampaignContext campaign,
            CancellationToken cancellationToken) => Inner.AcquireTurnAsync(campaign, cancellationToken);

        public ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) => Inner.AcquireMcpAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(
            CancellationToken cancellationToken) => Inner.AcquireAcceleratorAsync(cancellationToken);

        public ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) => Inner.AcquireCleanupAsync(scope, cancellationToken);

        public ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(
            Guid campaignId,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            Inner.AcquireCampaignExclusiveAsync(campaignId, owner, cancellationToken);

        public ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(
            ProtectedTransferScope scope,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            Inner.AcquireProtectedTransferAsync(scope, owner, cancellationToken);

        public ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(
            Guid campaignId,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            Inner.ResumeCampaignExclusiveAsync(campaignId, owner, cancellationToken);

        public ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(
            ProtectedTransferScope scope,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            Inner.ResumeProtectedTransferAsync(scope, owner, cancellationToken);

    }

    private sealed class RecordingErasureTransition(List<string> steps) : ICovenantErasureTransition
    {

        internal string? FailingStep { get; set; }

        internal Action<string>? OnStep { get; set; }

        internal Action? OnVerified { get; set; }

        internal int VerifyReopenCalls { get; private set; }

        internal Guid? PublishedGeneration { get; private set; }

        internal CovenantVerifiedCandidateState VerifiedCandidate { get; } = CreateCandidate();

        internal CovenantVerifiedCandidateState? PublishedCandidate { get; private set; }

        internal CancellationToken PublicationToken { get; private set; }

        public async Task<Result<Guid>> ApplyCanonicalErasureAsync(
            CovenantExclusiveOperation operation,
            CovenantCanonicalDatasetTransition dataset,
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken)
        {

            Result stepped = await Step("apply-canonical").ConfigureAwait(false);

            return stepped.IsFailure
                ? Result<Guid>.Failure(stepped.Error)
                : Result<Guid>.Success(CandidateGeneration);

        }

        public Task<Result> CloseHandlesAsync(CancellationToken cancellationToken) => Step("close-handles");

        public Task<Result> TruncateWalAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) => Step("truncate-wal");

        public Task<Result> CompactAsync(CovenantV3CompactionCapabilities capabilities, CancellationToken cancellationToken) => Step("compact");

        public Task<Result> InitializeAcceleratorAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            Step("initialize-accelerator");

        public Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken) =>
            Step("verify-sidecar-absence");

        public async Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken)
        {

            VerifyReopenCalls++;

            Result stepped = await Step("verify-reopen").ConfigureAwait(false);

            OnVerified?.Invoke();

            return stepped.IsFailure
                ? Result<CovenantVerifiedCandidateState>.Failure(stepped.Error)
                : Result<CovenantVerifiedCandidateState>.Success(VerifiedCandidate);

        }

        public Task<Result> PublishCommittedAsync(
            ICovenantExclusiveOperationLease lease,
            CovenantVerifiedCandidateState candidate,
            CancellationToken cancellationToken)
        {

            PublishedCandidate = candidate;

            PublishedGeneration = candidate.Dataset.DatasetGeneration;

            PublicationToken = cancellationToken;

            return Step("publish");

        }

        private Task<Result> Step(string name)
        {

            steps.Add(name);

            OnStep?.Invoke(name);

            return Task.FromResult(
                string.Equals(FailingStep, name, StringComparison.Ordinal)
                    ? Result.Failure(new Error(ErrorCodes.Covenant.ErasureIncomplete, name))
                    : Result.Success());

        }

        private static CovenantVerifiedCandidateState CreateCandidate() =>
            new(
                new CovenantCandidateDatasetState(
                    CandidateGeneration,
                    CanonicalSearchSequence: 0,
                    CoreCampaignDeletionSequence: 0,
                    AppliedDatasetGeneration: null,
                    AppliedSearchSequence: null,
                    AppliedCampaignDeletionSequence: 0,
                    AppliedSessionDeletionSequence: 0,
                    AcceleratorEpoch: 1,
                    CovenantFtsRebuildState.FullRebuildRequired,
                    EnvelopeMasterKeyVersion: 1,
                    new byte[32],
                    EnvelopeKeyEpoch: 1,
                    KeyReclamationEpoch: 1),
                new CovenantCandidateAuthorityState(
                    InstallationIdentity: "coordinator-test",
                    AuthorityEpoch: 1,
                    CurrentMasterKeyVersion: 1,
                    new byte[32],
                    RecoveryEnvelopeEpoch: 1,
                    CovenantHostToolsState.Clean,
                    TransitionId: null),
                new CovenantCandidateCapabilityState(
                    AppliedCampaignSequence: 0,
                    AppliedSessionSequence: 0,
                    FullSweepRequired: false));

    }

    private sealed class RecordingDisclosureWriterLifecycle(List<string> steps) : ICovenantDisclosureWriterLifecycle
    {

        internal bool QuiesceFails { get; set; }

        internal Exception? QuiesceException { get; set; }

        internal bool ReopenFails { get; set; }

        internal CancellationToken ReopenToken { get; private set; }

        public ValueTask<Result> QuiesceAsync(CancellationToken cancellationToken)
        {

            steps.Add("quiesce-writer");

            if (QuiesceException is { } exception)
            {

                throw exception;

            }

            return ValueTask.FromResult(
                QuiesceFails
                    ? Result.Failure(
                        new Error(ErrorCodes.Covenant.MaintenanceFailed, "The disclosure writer did not quiesce."))
                    : Result.Success());

        }

        public ValueTask<Result> ReopenAsync(CancellationToken cancellationToken)
        {

            steps.Add("reopen-writer");

            ReopenToken = cancellationToken;

            return ValueTask.FromResult(
                ReopenFails
                    ? Result.Failure(
                        new Error(ErrorCodes.Covenant.MaintenanceFailed, "The disclosure writer did not reopen."))
                    : Result.Success());

        }

    }

    private sealed class RecordingErasureKernel(List<string> steps) : ICovenantProtectedArtifactErasureKernel
    {

        internal int Calls { get; private set; }

        internal CovenantArtifactErasureAuthority? LastAuthority { get; private set; }

        internal CovenantErasureBlocker Blocker { get; set; } = CovenantErasureBlocker.None;

        public ValueTask<Result<CovenantArtifactErasureProgress>> ErasePageAsync(
            CovenantProtectedArtifactErasurePage page,
            CovenantArtifactErasureAuthority authority,
            CancellationToken cancellationToken = default)
        {

            Calls++;

            LastAuthority = authority;

            steps.Add("erase-artifacts");

            return ValueTask.FromResult(
                Result<CovenantArtifactErasureProgress>.Success(
                    new CovenantArtifactErasureProgress(1, 1, 0, Blocker)));

        }

    }

    private sealed class RecordingManagedFileKernel(List<string> steps) : ICovenantManagedFileErasureKernel
    {

        internal int Calls { get; private set; }

        internal CovenantArtifactErasureAuthority? LastAuthority { get; private set; }

        internal CovenantErasureBlocker Blocker { get; set; } = CovenantErasureBlocker.None;

        public ValueTask<Result<CovenantArtifactErasureProgress>> EraseAsync(
            CovenantManagedFileErasureRequest request,
            CovenantArtifactErasureAuthority authority,
            CancellationToken cancellationToken = default)
        {

            Calls++;

            LastAuthority = authority;

            steps.Add("erase-managed-files");

            return ValueTask.FromResult(
                Result<CovenantArtifactErasureProgress>.Success(
                    new CovenantArtifactErasureProgress(1, 1, 0, Blocker)));

        }

    }

    private sealed class StubInventorySource : ICovenantErasureInventorySource
    {

        internal bool Fails { get; set; }

        internal Exception? Exception { get; set; }

        internal bool DatabaseBatchFails { get; set; }

        internal Exception? DatabaseBatchException { get; set; }

        internal bool ExternalDisclosuresNotRevocable { get; set; } = true;

        internal CovenantDisclosureExposure Exposure { get; set; } =
            new(1, CovenantDisclosureCountKind.Exact);

        internal int FirstPreflightCalls { get; private set; }

        internal int RemainingManagedPreflightCalls { get; private set; }

        internal int DatabaseBatchCalls { get; private set; }

        internal int ManagedBatchCalls { get; private set; }

        internal int ExposureCalls { get; private set; }

        internal Guid? ObservedDatasetGeneration { get; private set; }

        public Task<Result<CovenantErasureInventorySummary>> PreflightBeforeCanonicalAsync(
            CovenantExclusiveOperation operation,
            Guid datasetGeneration,
            CancellationToken cancellationToken)
        {

            FirstPreflightCalls++;

            ObservedDatasetGeneration = datasetGeneration;

            if (Exception is { } exception)
            {

                throw exception;

            }

            return Task.FromResult(
                Fails
                    ? Result<CovenantErasureInventorySummary>.Failure(
                        new Error(
                            ErrorCodes.Covenant.IntegrityFailure,
                            "The erasure inventory could not be enumerated."))
                    : Result<CovenantErasureInventorySummary>.Success(
                        new CovenantErasureInventorySummary(
                            1,
                            1,
                            CurrentExposure())));

        }

        public Task<Result> PreflightRemainingManagedAsync(CancellationToken cancellationToken)
        {

            RemainingManagedPreflightCalls++;

            return Task.FromResult(Result.Success());

        }

        public Task<Result<CovenantDatabaseErasureBatch>> ReadNextDatabaseBatchAsync(
            Guid datasetGeneration,
            Guid? afterLabelId,
            CancellationToken cancellationToken)
        {

            DatabaseBatchCalls++;

            if (DatabaseBatchException is { } exception)
            {

                throw exception;

            }

            if (DatabaseBatchFails)
            {

                return Task.FromResult(
                    Result<CovenantDatabaseErasureBatch>.Failure(
                        new Error(
                            ErrorCodes.Covenant.IntegrityFailure,
                            "The bounded database inventory page could not be read.")));

            }

            return Task.FromResult(
                Result<CovenantDatabaseErasureBatch>.Success(
                    new CovenantDatabaseErasureBatch(
                        Guid.NewGuid(),
                        true,
                        new CovenantProtectedArtifactErasurePage(
                            datasetGeneration,
                            [CovenantErasureAuthorityFixture.Item(Guid.NewGuid(), Guid.NewGuid())]))));

        }

        public Task<Result<CovenantManagedFileErasureBatch>> ReadNextManagedFileBatchAsync(
            Guid operationId,
            Guid? afterLabelId,
            CancellationToken cancellationToken)
        {

            ManagedBatchCalls++;

            return Task.FromResult(
                Result<CovenantManagedFileErasureBatch>.Success(
                    new CovenantManagedFileErasureBatch(
                        Guid.NewGuid(),
                        true,
                        [
                            new CovenantManagedFileErasureRequest(
                                Guid.NewGuid(),
                                operationId,
                                Guid.NewGuid(),
                                Guid.NewGuid(),
                                Guid.NewGuid(),
                                1),
                        ])));

        }

        public Task<Result<CovenantDisclosureExposure>> ReadDisclosureExposureAsync(
            CancellationToken cancellationToken)
        {

            ExposureCalls++;

            return Task.FromResult(Result<CovenantDisclosureExposure>.Success(CurrentExposure()));

        }

        /// <summary>
        /// The canonical source tuple a launch binds to, answered as the one this harness's durable
        /// launch already names.
        /// </summary>
        /// <remarks>
        /// A double that invented a fresh generation per call would describe an installation whose
        /// canonical state moved between the plan and the row it was written into — the one condition
        /// an offline transition may never be resumed across — and every test here would then be
        /// exercising a launch no live installation could have produced.
        /// </remarks>
        public Task<Result<CovenantOfflineTransitionSourceState>> ReadOfflineTransitionSourceStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CovenantOfflineTransitionSourceState>.Success(
                    new CovenantOfflineTransitionSourceState(
                        CovenantOperationGateFixture.DatasetGeneration,
                        SourceEpochs.AcceleratorEpoch,
                        SourceEpochs.KeyReclamationEpoch,
                        SourceEpochs.EnvelopeKeyEpoch)));

        private CovenantDisclosureExposure CurrentExposure() =>
            ExternalDisclosuresNotRevocable
                ? Exposure
                : new CovenantDisclosureExposure(0, CovenantDisclosureCountKind.Exact);

    }

    private sealed class RecordingOperationCoordinator(FakeLongRunningOperationStore store)
        : ILongRunningOperationCoordinator
    {

        private readonly FakeLongRunningOperationStore _store = store;

        internal int CheckpointCalls { get; private set; }

        internal bool HonorCancellation { get; set; }

        internal CancellationToken LastCheckpointToken { get; private set; }

        public Task<LongRunningOperationLeaseResult> StartAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The erasure coordinator never starts an unnamed operation.");

        public Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The erasure coordinator never starts an operation.");

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> CheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            CancellationToken cancellationToken = default)
        {

            CheckpointCalls++;

            LastCheckpointToken = cancellationToken;

            if (HonorCancellation)
            {

                cancellationToken.ThrowIfCancellationRequested();

            }

            return _store.SaveCheckpointAsync(
                operationId,
                ownerId,
                expectedCheckpointVersion,
                checkpointVersion,
                checkpointPayload,
                checkpointReference,
                publicSummary,
                TimeProvider.System.GetUtcNow(),
                cancellationToken);

        }

        public Task<bool> CompleteAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> FailAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            string errorCode,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

    }

}
