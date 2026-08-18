using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Operations;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The family-reinitialize phase machine: what it erases first, what it never repeats, and how it
/// leaves admission.
/// </summary>
/// <remarks>
/// The transition owner is faked so every crash point can be exercised without destroying a real
/// database. What is not faked is the thing under test: the gate, its exclusive lease, the erasure
/// authority the coordinator wraps that lease in, and the single disposition it ends on (§10.17).
/// </remarks>
public sealed class CovenantFamilyReinitializeCoordinatorTests
{

    private static readonly Guid OperationId = Guid.Parse("66666666-6666-4666-8666-666666666666");

    private static CancellationToken Token => CancellationToken.None;

    /// <summary>
    /// Family reinitialize keeps the same markers a Covenant reset does, asserted where they do.
    /// </summary>
    /// <remarks>
    /// This path reaches storage through <c>ICovenantFamilyReinitializeTransition</c>, which has no
    /// production implementation, so there is no run to assert a retained row against. What can be
    /// asserted is the property that makes the promise true for every path at once: no production file
    /// outside a closed list is able to issue the deletion at all (§10.20.5).
    /// </remarks>
    [Fact]
    public void Family_reinitialize_retains_the_marker_set_no_production_path_may_delete() =>
        CovenantRetainedEvidence.AssertNoProductionPathDeletesRetainedEvidence();

    [Fact]
    public async Task A_clean_run_erases_every_protected_artifact_before_the_family_is_dropped()
    {

        CoordinatorHarness harness = new();

        Result<CovenantExclusiveLeaseDisposition> disposition = await harness.RunAsync(
            CovenantFamilyReinitializePhase.Planned);

        Assert.True(disposition.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, disposition.Value);

        Assert.Equal(1, harness.Artifacts.Calls);

        Assert.Equal(1, harness.ManagedFiles.Calls);

        // The two kernels run before the drop, and the drop before the reopen proof. A drop that ran
        // first would destroy the database's own record of the files still to be erased.
        Assert.Equal(
            [
                "erase-artifacts",
                "erase-managed-files",
                "close-handles",
                "drop-family",
                "compact",
                "install-tiers",
                "truncate",
                "verify-reopen",
                "publish",
                "reopen-writer",
            ],
            harness.Steps);

    }

    [Fact]
    public async Task Both_kernels_borrow_the_coordinators_own_lease_and_never_acquire_one()
    {

        CoordinatorHarness harness = new();

        _ = await harness.RunAsync(CovenantFamilyReinitializePhase.Planned);

        Assert.NotNull(harness.Artifacts.LastAuthority);

        Assert.Equal(CovenantArtifactErasureAuthorityKind.Exclusive, harness.Artifacts.LastAuthority.Kind);

        Assert.Equal(
            CovenantExclusiveOperation.CovenantFamilyReinitialize,
            harness.Artifacts.LastAuthority.ExclusiveOperation);

        Assert.Same(harness.Artifacts.LastAuthority, harness.ManagedFiles.LastAuthority);

        Assert.Equal(CovenantLeaseCoverage.Installation, harness.Artifacts.LastAuthority.Snapshot.Coverage);

    }

    [Fact]
    public async Task A_manual_file_blocker_keeps_admission_closed_and_leaves_the_family_intact()
    {

        CoordinatorHarness harness = new();

        harness.ManagedFiles.Blocker = CovenantErasureBlocker.ManualOwnershipMismatch;

        Result<CovenantExclusiveLeaseDisposition> disposition = await harness.RunAsync(
            CovenantFamilyReinitializePhase.Planned);

        Assert.True(disposition.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, disposition.Value);

        Assert.DoesNotContain("drop-family", harness.Steps);

    }

    [Fact]
    public async Task A_failed_publication_keeps_admission_closed_after_a_durable_mutation()
    {

        CoordinatorHarness harness = new();

        harness.Transition.FailingStep = "publish";

        Result<CovenantExclusiveLeaseDisposition> disposition = await harness.RunAsync(
            CovenantFamilyReinitializePhase.Planned);

        Assert.True(disposition.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, disposition.Value);

        Assert.Contains("drop-family", harness.Steps);

        Assert.DoesNotContain("reopen-writer", harness.Steps);

    }

    [Fact]
    public async Task A_failed_step_before_any_durable_mutation_still_keeps_admission_closed()
    {

        CoordinatorHarness harness = new();

        harness.Transition.FailingStep = "close-handles";

        Result<CovenantExclusiveLeaseDisposition> disposition = await harness.RunAsync(
            CovenantFamilyReinitializePhase.Planned);

        Assert.True(disposition.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, disposition.Value);

        Assert.DoesNotContain("drop-family", harness.Steps);

    }

    [Theory]
    [InlineData(CovenantFamilyReinitializePhase.HandlesClosed, "close-handles")]
    [InlineData(CovenantFamilyReinitializePhase.FamilyDropped, "drop-family")]
    [InlineData(CovenantFamilyReinitializePhase.SidecarsVerified, "truncate")]
    [InlineData(CovenantFamilyReinitializePhase.ReopenedVerified, "verify-reopen")]
    public async Task A_resumed_run_never_repeats_a_step_its_checkpoint_already_records(
        CovenantFamilyReinitializePhase resumeFrom,
        string skippedStep)
    {

        CoordinatorHarness harness = new();

        // A phase already recorded is a step already committed, and every one of these effects is one
        // SQLite cannot roll back.
        await harness.CloseAndAdoptAsync();

        Result<CovenantExclusiveLeaseDisposition> disposition = await harness.RunAsync(resumeFrom);

        Assert.True(disposition.IsSuccess);

        Assert.DoesNotContain(skippedStep, harness.Steps);

    }

    [Fact]
    public async Task A_checkpoint_past_installation_without_its_generation_keeps_admission_closed()
    {

        CoordinatorHarness harness = new();

        await harness.CloseAndAdoptAsync();

        Result<CovenantExclusiveLeaseDisposition> disposition = await harness.RunAsync(
            CovenantFamilyReinitializePhase.SidecarsVerified,
            newDatasetGeneration: null,
            useDefaultGeneration: false);

        Assert.True(disposition.IsSuccess);

        Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, disposition.Value);

        Assert.DoesNotContain("publish", harness.Steps);

    }

    private sealed class CoordinatorHarness
    {

        private readonly CovenantOperationGate _gate = CovenantOperationGateFixture.CreateGate();

        private readonly FakeLongRunningOperationStore _store = new(TimeProvider.System);

        /// <summary>
        /// One ordered log across both kernels and the transition owner, because the ordering between
        /// them is the property under test.
        /// </summary>
        internal List<string> Steps { get; } = [];

        internal RecordingErasureKernel Artifacts { get; }

        internal RecordingManagedFileKernel ManagedFiles { get; }

        internal RecordingTransition Transition { get; }

        internal CoordinatorHarness()
        {

            Artifacts = new RecordingErasureKernel(Steps);

            ManagedFiles = new RecordingManagedFileKernel(Steps);

            Transition = new RecordingTransition(Steps);

        }

        /// <summary>
        /// Closes the scope under the exact recovery owner and releases the live registration, which is
        /// the state a crash mid-operation leaves behind.
        /// </summary>
        internal async Task CloseAndAdoptAsync()
        {

            CovenantExclusiveLease lease = (await _gate.AcquireExclusiveAsync(Owner, Token)).Value;

            _ = await lease.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token);

            await lease.DisposeAsync();

        }

        internal async Task<Result<CovenantExclusiveLeaseDisposition>> RunAsync(
            CovenantFamilyReinitializePhase phase,
            Guid? newDatasetGeneration = null,
            bool useDefaultGeneration = true)
        {

            RecordingOperationCoordinator operations = new();

            CovenantFamilyReinitializeCoordinator coordinator = new(
                new CovenantRequestedOperationStarter(operations),
                operations,
                _store,
                _gate,
                Artifacts,
                ManagedFiles,
                new StubErasureSource(),
                Transition,
                TimeProvider.System);

            return await coordinator.RunAsync(
                Operation(),
                useDefaultGeneration && newDatasetGeneration is null
                    ? Checkpoint(phase)
                    : Checkpoint(phase) with { NewDatasetGeneration = newDatasetGeneration },
                "owner",
                Token);

        }

        private static CovenantExclusiveRecoveryOwner Owner =>
            new(
                OperationId,
                CovenantExclusiveOperation.CovenantFamilyReinitialize,
                CovenantOperationGateFixture.Digest(3));

        private static LongRunningOperation Operation() =>
            new(
                OperationId,
                LongRunningOperationKinds.CovenantFamilyReinitialize,
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
                "Reinitializing.",
                null,
                1);

        private static CovenantFamilyReinitializeCheckpointV1 Checkpoint(
            CovenantFamilyReinitializePhase phase) =>
            new(
                CovenantFamilyReinitializeCheckpointV1.CurrentVersion,
                OperationId,
                "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90",
                AuthorityEpoch: 1,
                CovenantOperationGateFixture.Digest(1).ToString(),
                CovenantOperationGateFixture.Digest(2).ToString(),
                CovenantOperationGateFixture.Digest(3).ToString(),
                CovenantOperationGateFixture.DatasetGeneration,

                // A checkpoint at or past the install phase carries the generation that install
                // produced; one that does not is covered separately as durable state disagreeing with
                // itself.
                NewDatasetGeneration: phase >= CovenantFamilyReinitializePhase.AcceleratorInstalled
                    ? Guid.Parse("88888888-8888-4888-8888-888888888888")
                    : null,
                phase,
                ManagedArtifactCursor: 0,
                OldFamilyDropped: false,
                CanonicalInstalled: false,
                AcceleratorInstalled: false,
                CompactedFileIdentityDigest: null,
                RetryCount: 0,
                LastDurableErrorCode: null);

    }

    private sealed class RecordingTransition(List<string> steps) : ICovenantFamilyReinitializeTransition
    {

        internal string? FailingStep { get; set; }

        public Task<Result> CloseHandlesAsync(CancellationToken cancellationToken) => Step("close-handles");

        public Task<Result> DropFamilyAsync(CancellationToken cancellationToken) => Step("drop-family");

        public async Task<Result<CovenantDigest>> CompactAsync(CancellationToken cancellationToken)
        {

            Result stepped = await Step("compact");

            return stepped.IsFailure
                ? Result<CovenantDigest>.Failure(stepped.Error)
                : Result<CovenantDigest>.Success(CovenantOperationGateFixture.Digest(8));

        }

        public async Task<Result<Guid>> InstallTiersAsync(CancellationToken cancellationToken)
        {

            Result stepped = await Step("install-tiers");

            return stepped.IsFailure
                ? Result<Guid>.Failure(stepped.Error)
                : Result<Guid>.Success(Guid.Parse("88888888-8888-4888-8888-888888888888"));

        }

        public Task<Result> TruncateAndVerifySidecarsAsync(CancellationToken cancellationToken) =>
            Step("truncate");

        public Task<Result> VerifyReopenAsync(CancellationToken cancellationToken) => Step("verify-reopen");

        public Task<Result> PublishCommittedAsync(
            ICovenantExclusiveOperationLease lease,
            Guid newDatasetGeneration,
            CancellationToken cancellationToken) => Step("publish");

        public Task<Result> ReopenDisclosureWriterAsync(CancellationToken cancellationToken) =>
            Step("reopen-writer");

        private Task<Result> Step(string name)
        {

            steps.Add(name);

            return Task.FromResult(
                string.Equals(FailingStep, name, StringComparison.Ordinal)
                    ? Result.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, name))
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

    private sealed class StubErasureSource : ICovenantReinitializeErasureSource
    {

        public Task<Result<CovenantReinitializeErasureWork>> EnumerateAsync(
            Guid operationId,
            Guid datasetGeneration,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CovenantReinitializeErasureWork>.Success(
                    new CovenantReinitializeErasureWork(
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
                        ])));

    }

    private sealed class RecordingOperationCoordinator : ILongRunningOperationCoordinator
    {

        public Task<LongRunningOperationLeaseResult> StartAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A named Covenant operation never takes the unnamed arm.");

        public Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<LongRunningOperationRequestIdentityResult>.Success(
                    new LongRunningOperationRequestIdentityResult(
                        LongRunningOperationRequestIdentityOutcome.Created,
                        null)));

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> CheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> CompleteAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> FailAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            string errorCode,
            CancellationToken cancellationToken) => Task.FromResult(true);

    }

}
