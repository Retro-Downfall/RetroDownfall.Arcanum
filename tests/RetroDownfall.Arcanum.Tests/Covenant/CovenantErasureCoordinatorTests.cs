using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
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

    private static readonly Guid CandidateGeneration = new("99999999-9999-4999-8999-999999999999");

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

        // The database half commits before a single managed file is touched. The managed-file kernel
        // persists its durable work item before its first external effect, and that work item is a
        // database row: a file deleted before the transaction that authorized it would be a deletion
        // no surviving row can explain.
        Assert.Equal(CleanRunSteps, harness.Steps);

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
    public async Task A_database_kernel_blocker_keeps_admission_closed_and_never_applies_the_canonical_erasure()
    {

        CoordinatorHarness harness = new();

        harness.Artifacts.Blocker = CovenantErasureBlocker.ManualOwnershipMismatch;

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, completion.Value.Disposition);

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
    [InlineData(CovenantResetPhase.ReopenedVerified, "verify-reopen")]
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

        Assert.Equal(1, fresh.Gate.AcquireCount);

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

        Assert.Equal(1, resumed.Gate.ResumeCount);

    }

    [Fact]
    public async Task A_resumed_run_reads_the_candidate_generation_from_storage_rather_than_the_checkpoint()
    {

        CoordinatorHarness harness = new();

        await harness.CloseAndAdoptAsync();

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.HandlesClosed);

        Assert.True(completion.IsSuccess);

        // Neither durable reset checkpoint has a field for the generation the canonical erasure
        // created — both shapes were frozen by #118 — so a resumed run asks the database, which is the
        // commit authority for the dataset row it wrote.
        Assert.Equal(1, harness.Transition.CandidateGenerationReads);

        Assert.Equal(CandidateGeneration, harness.Transition.PublishedGeneration);

    }

    [Fact]
    public async Task A_first_pass_publishes_the_generation_its_own_canonical_erasure_returned()
    {

        CoordinatorHarness harness = new();

        _ = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.Equal(CandidateGeneration, harness.Transition.PublishedGeneration);

        // A first pass already holds the generation it just created. Reading it back would be a second
        // source for one fact, and the two could disagree only in the case that matters.
        Assert.Equal(0, harness.Transition.CandidateGenerationReads);

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

    [Fact]
    public async Task A_failed_one_shot_commit_and_reopen_reports_the_lifecycle_failure_and_leaves_admission_shut()
    {

        // The real gate refuses to hand a second live registration to a scope that already has one,
        // so a disposition can only fail underneath the lease rather than beside it. The registration
        // is faked and the lease is not: the one-shot claim under test is the real one.
        CoordinatorHarness harness = new(dispositionFails: true);

        Result<CovenantErasureCompletion> completion = await harness.RunAsync(CovenantResetPhase.InventoryPrepared);

        Assert.True(completion.IsFailure);

        // The failure is reported as itself. A second disposition would change nothing an operator can
        // observe — the gate is already closed — while replacing the real reason with the lease's own
        // LifecycleConflict, which is the one message that explains nothing.
        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, completion.Error.Code);

        Assert.Equal(1, harness.Gate.DispositionAttempts);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, harness.Gate.LastDisposition);

        Assert.False(await harness.AdmissionIsOpenAsync());

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

        Assert.Equal(0, harness.Artifacts.Calls);

        Assert.Equal(0, harness.ManagedFiles.Calls);

        Assert.False(completion.Value.CanonicalResetApplied);

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

        Assert.True(await harness.AdmissionIsOpenAsync());

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

        internal CoordinatorHarness(
            CovenantExclusiveOperation operation = CovenantExclusiveOperation.CovenantReset,
            TimeSpan? drainTimeout = null,
            bool dispositionFails = false)
        {

            _operation = operation;

            Gate = new RecordingGate(
                CovenantOperationGateFixture.CreateGate(drainTimeout: drainTimeout),
                dispositionFails);

            Artifacts = new RecordingErasureKernel(Steps);

            ManagedFiles = new RecordingManagedFileKernel(Steps);

            Transition = new RecordingErasureTransition(Steps);

            DisclosureWriter = new RecordingDisclosureWriterLifecycle(Steps);

            Inventory = new StubInventorySource();

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

            FakeLongRunningOperationStore store = new(TimeProvider.System);

            RecordingOperationCoordinator operations = new();

            CovenantErasureCoordinator coordinator = new(
                operations,
                store,
                Gate,
                Artifacts,
                ManagedFiles,
                Inventory,
                Transition,
                DisclosureWriter,
                NullLogger<CovenantErasureCoordinator>.Instance);

            return await coordinator.RunAsync(
                Operation(),
                new CovenantErasureCheckpointState(
                    checkpointOperationId ?? OperationId,
                    _operation,
                    CovenantOperationGateFixture.Digest(7),
                    phase),
                CovenantOperationGateFixture.DatasetGeneration,
                "owner",
                cancellationToken ?? Token);

        }

        private CovenantExclusiveRecoveryOwner Owner =>
            new(OperationId, _operation, CovenantOperationGateFixture.Digest(7));

        private static LongRunningOperation Operation() =>
            new(
                OperationId,
                LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationState.Running,
                LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
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
                1,
                null,
                null,
                "Erasing the Covenant.",
                null,
                1);

    }

    /// <summary>
    /// The real gate, wrapped only to count which verb the coordinator used.
    /// </summary>
    /// <remarks>
    /// A wrapper rather than a fake, because acquire-versus-resume, lease draining, and the one-shot
    /// disposition are properties of the real gate that a stub would simply assert into existence.
    /// </remarks>
    private sealed class RecordingGate(CovenantOperationGate inner, bool dispositionFails) : ICovenantOperationGate
    {

        private int _dispositionAttempts;

        internal CovenantOperationGate Inner { get; } = inner;

        internal int AcquireCount { get; private set; }

        internal int ResumeCount { get; private set; }

        internal int DispositionAttempts => Volatile.Read(ref _dispositionAttempts);

        internal CovenantExclusiveLeaseDisposition? LastDisposition { get; private set; }

        public async ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken)
        {

            AcquireCount++;

            return Observe(await Inner.AcquireExclusiveAsync(owner, cancellationToken));

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

            return dispositionFails
                ? Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.MaintenanceFailed,
                        "The gate could not record this reopening decision."))
                : Result.Success();

        }

        private sealed class ObservingRegistration(RecordingGate gate, CovenantExclusiveLease real)
            : ICovenantExclusiveLeaseRegistration
        {

            public CovenantOperationLeaseSnapshot Snapshot => real.Snapshot;

            public CancellationToken Revocation => real.Revocation;

            public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
                real.RevalidateAsync(cancellationToken);

            public ValueTask ReleaseAsync() => real.DisposeAsync();

            public async ValueTask<Result> CompleteAsync(
                CovenantExclusiveLeaseDisposition disposition,
                CancellationToken cancellationToken)
            {

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

        internal int CandidateGenerationReads { get; private set; }

        internal Guid? PublishedGeneration { get; private set; }

        public async Task<Result<Guid>> ApplyCanonicalErasureAsync(
            CovenantExclusiveOperation operation,
            CancellationToken cancellationToken)
        {

            Result stepped = await Step("apply-canonical").ConfigureAwait(false);

            return stepped.IsFailure
                ? Result<Guid>.Failure(stepped.Error)
                : Result<Guid>.Success(CandidateGeneration);

        }

        public Task<Result<Guid>> ReadCandidateDatasetGenerationAsync(CancellationToken cancellationToken)
        {

            CandidateGenerationReads++;

            return Task.FromResult(Result<Guid>.Success(CandidateGeneration));

        }

        public Task<Result> CloseHandlesAsync(CancellationToken cancellationToken) => Step("close-handles");

        public Task<Result> TruncateWalAsync(CancellationToken cancellationToken) => Step("truncate-wal");

        public Task<Result> CompactAsync(CancellationToken cancellationToken) => Step("compact");

        public Task<Result> InitializeAcceleratorAsync(CancellationToken cancellationToken) =>
            Step("initialize-accelerator");

        public Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken) =>
            Step("verify-sidecar-absence");

        public Task<Result> VerifyReopenAsync(CancellationToken cancellationToken) => Step("verify-reopen");

        public Task<Result> PublishCommittedAsync(
            ICovenantExclusiveOperationLease lease,
            Guid candidateDatasetGeneration,
            CancellationToken cancellationToken)
        {

            PublishedGeneration = candidateDatasetGeneration;

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

    }

    private sealed class RecordingDisclosureWriterLifecycle(List<string> steps) : ICovenantDisclosureWriterLifecycle
    {

        internal bool QuiesceFails { get; set; }

        internal bool ReopenFails { get; set; }

        public ValueTask<Result> QuiesceAsync(CancellationToken cancellationToken)
        {

            steps.Add("quiesce-writer");

            return ValueTask.FromResult(
                QuiesceFails
                    ? Result.Failure(
                        new Error(ErrorCodes.Covenant.MaintenanceFailed, "The disclosure writer did not quiesce."))
                    : Result.Success());

        }

        public ValueTask<Result> ReopenAsync(CancellationToken cancellationToken)
        {

            steps.Add("reopen-writer");

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

        internal bool ExternalDisclosuresNotRevocable { get; set; } = true;

        public Task<Result<CovenantErasureWork>> EnumerateAsync(
            Guid operationId,
            CovenantExclusiveOperation operation,
            Guid datasetGeneration,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Fails
                    ? Result<CovenantErasureWork>.Failure(
                        new Error(
                            ErrorCodes.Covenant.IntegrityFailure,
                            "The erasure inventory could not be enumerated."))
                    : Result<CovenantErasureWork>.Success(
                        new CovenantErasureWork(
                            [
                                new CovenantProtectedArtifactErasurePage(
                                    CovenantOperationGateFixture.DatasetGeneration,
                                    [CovenantErasureAuthorityFixture.Item(Guid.NewGuid(), Guid.NewGuid())]),
                            ],
                            [
                                new CovenantManagedFileErasureRequest(
                                    Guid.NewGuid(),
                                    operationId,
                                    Guid.NewGuid(),
                                    Guid.NewGuid(),
                                    Guid.NewGuid(),
                                    1),
                            ],
                            ExternalDisclosuresNotRevocable)));

    }

    private sealed class RecordingOperationCoordinator : ILongRunningOperationCoordinator
    {

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
            CancellationToken cancellationToken = default) => Task.FromResult(true);

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
