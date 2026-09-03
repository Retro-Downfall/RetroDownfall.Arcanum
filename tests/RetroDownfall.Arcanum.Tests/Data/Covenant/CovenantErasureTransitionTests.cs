using System.Reflection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The production handoff from committed storage evidence to one atomic runtime generation.
/// </summary>
public sealed class CovenantErasureTransitionTests
{

    [Fact]
    public void Storage_transition_requires_exact_v3_capabilities()
    {

        Assert.Equal(
            [
                typeof(CovenantExclusiveOperation),
                typeof(CovenantCanonicalDatasetTransition),
                typeof(CovenantV3MaintenanceCapability),
                typeof(CancellationToken),
            ],
            typeof(ICovenantErasureTransition).GetMethod(nameof(ICovenantErasureTransition.ApplyCanonicalErasureAsync))!
                .GetParameters()
                .Select(static parameter => parameter.ParameterType));

        Assert.Equal(
            [typeof(CovenantV3MaintenanceCapability), typeof(CancellationToken)],
            typeof(ICovenantErasureTransition).GetMethod(nameof(ICovenantErasureTransition.TruncateWalAsync))!
                .GetParameters()
                .Select(static parameter => parameter.ParameterType));

        Assert.Equal(
            [typeof(CovenantV3CompactionCapabilities), typeof(CancellationToken)],
            typeof(ICovenantErasureTransition).GetMethod(nameof(ICovenantErasureTransition.CompactAsync))!
                .GetParameters()
                .Select(static parameter => parameter.ParameterType));

    }

    [Fact]
    public async Task Storage_operations_delegate_once_and_verification_returns_the_exact_candidate()
    {

        using TransitionHarness harness = new();

        Assert.True((await harness.Subject.ApplyCanonicalErasureAsync(
            CovenantExclusiveOperation.CovenantReset,
            new CovenantCanonicalDatasetTransition(
                Guid.NewGuid(),
                new CovenantOfflineTransitionEpochsV1(1, 1, 1),
                Guid.NewGuid(),
                new CovenantOfflineTransitionEpochsV1(2, 2, 2)),
            harness.Mint(CovenantV3MaintenancePurpose.CanonicalErasure),
            CancellationToken.None)).IsSuccess);

        Assert.True((await harness.Subject.CloseHandlesAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await harness.Subject.TruncateWalAsync(
            harness.Mint(CovenantV3MaintenancePurpose.WalTruncation),
            CancellationToken.None)).IsSuccess);

        Assert.True((await harness.Subject.CompactAsync(
            new CovenantV3CompactionCapabilities(
                harness.Mint(CovenantV3MaintenancePurpose.CompactionVacuum),
                harness.Mint(CovenantV3MaintenancePurpose.CompactionExport),
                harness.Mint(CovenantV3MaintenancePurpose.CompactionExportVerification),
                harness.Mint(CovenantV3MaintenancePurpose.CompactionPostReplaceJournalRestore)),
            CancellationToken.None)).IsSuccess);

        Assert.True((await harness.Subject.InitializeAcceleratorAsync(
            harness.Mint(CovenantV3MaintenancePurpose.AcceleratorInitialization),
            CancellationToken.None)).IsSuccess);

        Assert.True((await harness.Subject.VerifySidecarAbsenceAsync(CancellationToken.None)).IsSuccess);

        Result<CovenantVerifiedCandidateState> verified =
            await harness.Subject.VerifyReopenAsync(
                harness.Mint(CovenantV3MaintenancePurpose.CandidateReopenVerification),
                CancellationToken.None);

        Assert.True(verified.IsSuccess);

        Assert.Same(harness.Candidate, verified.Value);

        Assert.Equal(1, harness.Canonical.ApplyCalls);

        Assert.Equal(1, harness.Storage.CloseCalls);

        Assert.Equal(1, harness.Storage.TruncateCalls);

        Assert.Equal(1, harness.Storage.CompactCalls);

        Assert.Equal(1, harness.Storage.InitializeCalls);

        Assert.Equal(1, harness.Storage.AbsenceCalls);

        Assert.Equal(1, harness.Storage.ReopenCalls);

    }

    [Fact]
    public void The_canonical_erasure_owner_exposes_no_ordinary_candidate_generation_reread()
    {

        Assert.DoesNotContain(
            typeof(ICovenantCanonicalErasure).GetMethods(),
            static method => string.Equals(
                method.Name,
                "ReadCandidateDatasetGenerationAsync",
                StringComparison.Ordinal));

        Assert.DoesNotContain(
            typeof(CovenantCanonicalErasureTransaction).GetMethods(),
            static method => string.Equals(
                method.Name,
                "ReadCandidateDatasetGenerationAsync",
                StringComparison.Ordinal));

    }

    [Fact]
    public async Task Publish_projects_only_the_verified_candidate_and_one_captured_runtime_state()
    {

        using TransitionHarness harness = new();

        CovenantRuntimeGenerationState expected = harness.Runtime.Current;

        Result published = await harness.Subject.PublishCommittedAsync(
            harness.Lease,
            harness.Candidate,
            CancellationToken.None);

        Assert.True(published.IsSuccess);

        Assert.Same(expected, harness.Publisher.Expected);

        CovenantCommittedAuthorityTransition transition = Assert.IsType<CovenantCommittedAuthorityTransition>(
            harness.Publisher.Transition);

        CovenantAvailabilitySnapshot availability = expected.Availability;

        Assert.Equal(harness.Candidate.Authority.InstallationIdentity, transition.InstallationIdentity);

        Assert.Equal(harness.Candidate.Authority.AuthorityEpoch, transition.AuthorityEpoch);

        Assert.Equal((uint)harness.Candidate.Authority.CurrentMasterKeyVersion, transition.MasterKeyVersion);

        Assert.Equal(harness.Candidate.Dataset.EnvelopeKeyEpoch, transition.CanonicalEnvelopeEpoch);

        Assert.Equal(harness.Candidate.Authority.RecoveryEnvelopeEpoch, transition.RecoveryEnvelopeEpoch);

        Assert.Equal(harness.Candidate.Authority.HostToolsState, transition.HostToolsState);

        Assert.Equal(harness.Candidate.Authority.TransitionId, transition.TransitionId);

        Assert.Equal(availability.Generation, transition.Capability.ExpectedGeneration);

        Assert.Equal(availability.Generation + 1, transition.Capability.Generation);

        Assert.Equal(availability.FeatureEnabled, transition.Capability.FeatureEnabled);

        Assert.Equal(availability.Canonical, transition.Capability.Canonical);

        Assert.Equal(availability.CanonicalSchemaVersion, transition.Capability.CanonicalSchemaVersion);

        Assert.Equal(
            availability.CanonicalInstalledFingerprint,
            transition.Capability.CanonicalInstalledFingerprint);

        Assert.Equal(availability.Accelerator, transition.Capability.Accelerator);

        Assert.Equal(availability.AcceleratorSchemaVersion, transition.Capability.AcceleratorSchemaVersion);

        Assert.Equal(
            availability.AcceleratorInstalledFingerprint,
            transition.Capability.AcceleratorInstalledFingerprint);

        Assert.Equal(harness.Candidate.Dataset.DatasetGeneration, transition.Capability.DatasetGeneration);

        Assert.Equal(harness.Candidate.Dataset.CanonicalSearchSequence, transition.Capability.CanonicalSequence);

        Assert.Equal(
            harness.Candidate.Dataset.CoreCampaignDeletionSequence,
            transition.Capability.CoreCampaignDeletionSequence);

        Assert.Equal(
            harness.Candidate.Dataset.AppliedCampaignDeletionSequence,
            transition.Capability.CanonicalAppliedCampaignDeletionSequence);

        Assert.Equal(
            harness.Candidate.Dataset.AppliedSessionDeletionSequence,
            transition.Capability.CanonicalAppliedSessionDeletionSequence);

        Assert.Equal(
            harness.Candidate.Dataset.AppliedDatasetGeneration,
            transition.Capability.AppliedDatasetGeneration);

        Assert.Equal(harness.Candidate.Dataset.AppliedSearchSequence, transition.Capability.AppliedSequence);

        Assert.Equal(
            harness.Candidate.Dataset.AppliedCampaignDeletionSequence,
            transition.Capability.AppliedCampaignDeletionSequence);

        Assert.Equal(harness.Candidate.Dataset.AcceleratorEpoch, transition.Capability.AcceleratorEpoch);

        Assert.Equal(CovenantFtsSynchronizationState.Dirty, transition.Capability.FtsSynchronization);

        Assert.True(transition.Capability.RebuildRequired);

        Assert.Equal(
            harness.Candidate.Capability.AppliedCampaignSequence,
            transition.Capability.CleanupAppliedCampaignSequence);

        Assert.Equal(
            harness.Candidate.Capability.AppliedSessionSequence,
            transition.Capability.CleanupAppliedSessionSequence);

        Assert.Equal(
            harness.Candidate.Capability.FullSweepRequired,
            transition.Capability.CleanupFullSweepRequired);

        Assert.Equal(availability.CanonicalDiagnosticCode, transition.Capability.CanonicalDiagnosticCode);

        Assert.Equal(availability.AcceleratorDiagnosticCode, transition.Capability.AcceleratorDiagnosticCode);

    }

    [Theory]
    [InlineData(CovenantCapabilityState.Degraded)]
    [InlineData(CovenantCapabilityState.Unavailable)]
    public async Task An_unhealthy_or_absent_accelerator_publishes_unavailable_synchronization(
        CovenantCapabilityState accelerator)
    {

        using TransitionHarness harness = new(accelerator);

        Result published = await harness.Subject.PublishCommittedAsync(
            harness.Lease,
            harness.Candidate,
            CancellationToken.None);

        Assert.True(published.IsSuccess);

        Assert.Equal(
            CovenantFtsSynchronizationState.Unavailable,
            harness.Publisher.Transition!.Capability.FtsSynchronization);

    }

    [Fact]
    public async Task Projection_failure_after_committed_erasure_retires_the_captured_runtime_and_all_issuance()
    {

        CovenantVerifiedCandidateState candidate = TransitionHarness.CandidateState();

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
            Enumerable.Repeat((byte)0x5A, 32).ToArray(),
            new CovenantEnvelopeBootstrapKeyInput(
                candidate.Authority.InstallationIdentity,
                checked((uint)candidate.Authority.CurrentMasterKeyVersion),
                candidate.Dataset.EnvelopeKeyEpoch,
                candidate.Authority.RecoveryEnvelopeEpoch,
                candidate.Dataset.DatasetGeneration));

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        CovenantAvailabilitySnapshot bootAvailability = runtime.PublishAvailability(
            _ => SaturatedAvailability(candidate) with { Generation = 1 });

        Assert.True(runtime.Initialize(
            runtime.Current,
            owned,
            new CovenantAuthoritySnapshot(
                RuntimeAuthorityGeneration: 1,
                candidate.Authority.InstallationIdentity,
                candidate.Authority.AuthorityEpoch,
                checked((uint)candidate.Authority.CurrentMasterKeyVersion),
                candidate.Authority.RecoveryEnvelopeEpoch,
                CovenantHostToolsState.Clean,
                TransitionId: null),
            bootAvailability).IsSuccess);

        CovenantRuntimeGenerationState initialized = runtime.Current;

        FieldInfo currentField = Assert.IsAssignableFrom<FieldInfo>(
            typeof(CovenantRuntimeGenerationProvider).GetField(
                "_current",
                BindingFlags.Instance | BindingFlags.NonPublic));

        currentField.SetValue(
            runtime,
            initialized with
            {

                Availability = SaturatedAvailability(candidate),

            });

        CovenantAuthoritySnapshotProvider authority = new(runtime);

        CovenantEnvelopeCodec codec = new(keys, TimeProvider.System);

        OperatorAuthorityContextIssuer issuer = new(authority);

        IReadOnlyDictionary<CovenantEnvelopePurpose, string> oldTokens =
            Enum.GetValues<CovenantEnvelopePurpose>()
                .ToDictionary(
                    static purpose => purpose,
                    purpose => codec.Encode(
                        purpose,
                        [(byte)(0x40 + (byte)purpose)],
                        TimeSpan.FromMinutes(5)).Value);

        OperatorAuthorityContext oldContext =
            issuer.Issue(CovenantAuthorityRequirement.CovenantManage).Value;

        CovenantExclusiveRecoveryOwner owner = new(
            Guid.Parse("22222222-3333-4444-8555-666666666666"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest([.. Enumerable.Repeat((byte)0x7B, CovenantLimits.DigestBytes)]));

        CovenantOperationGate gate = new(runtime, new NoCampaignProbe());

        await using CovenantExclusiveLease lease =
            (await gate.AcquireExclusiveAsync(owner, CancellationToken.None)).Value;

        CovenantAuthorityTransitionPublisher publisher = new(
            runtime,
            keys,
            new CovenantAvailability(runtime));

        CovenantErasureTransition subject = new(
            new RecordingCanonical(candidate.Dataset.DatasetGeneration),
            new RecordingStorage(candidate),
            runtime,
            publisher);

        Result published = await subject.PublishCommittedAsync(
            lease,
            candidate,
            CancellationToken.None);

        Assert.True(published.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, published.Error.Code);

        Assert.True(runtime.Current.AuthorityRetired);

        Assert.Null(runtime.Current.Keys);

        Assert.Null(authority.Current);

        Assert.Equal(owner, runtime.Current.RecoveryOwner);

        Assert.All(
            oldTokens,
            token => Assert.True(codec.Decode(token.Key, token.Value).IsFailure));

        Assert.True(issuer.Revalidate(oldContext).IsFailure);

        Assert.All(
            Enum.GetValues<CovenantEnvelopePurpose>(),
            purpose => Assert.True(codec.Encode(purpose, [0x42], TimeSpan.FromMinutes(5)).IsFailure));

        Assert.True(issuer.Issue(CovenantAuthorityRequirement.CovenantManage).IsFailure);

    }

    [Fact]
    public async Task Projection_failure_after_an_intervening_committed_winner_preserves_the_winner()
    {

        CovenantVerifiedCandidateState candidate = TransitionHarness.CandidateState();

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
            Enumerable.Repeat((byte)0x5A, 32).ToArray(),
            new CovenantEnvelopeBootstrapKeyInput(
                candidate.Authority.InstallationIdentity,
                checked((uint)candidate.Authority.CurrentMasterKeyVersion),
                candidate.Dataset.EnvelopeKeyEpoch,
                candidate.Authority.RecoveryEnvelopeEpoch,
                candidate.Dataset.DatasetGeneration));

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        CovenantAvailabilitySnapshot bootAvailability = runtime.PublishAvailability(
            _ => SaturatedAvailability(candidate) with { Generation = 1 });

        Assert.True(runtime.Initialize(
            runtime.Current,
            owned,
            new CovenantAuthoritySnapshot(
                RuntimeAuthorityGeneration: 1,
                candidate.Authority.InstallationIdentity,
                candidate.Authority.AuthorityEpoch,
                checked((uint)candidate.Authority.CurrentMasterKeyVersion),
                candidate.Authority.RecoveryEnvelopeEpoch,
                CovenantHostToolsState.Clean,
                TransitionId: null),
            bootAvailability).IsSuccess);

        CovenantExclusiveRecoveryOwner owner = new(
            Guid.Parse("33333333-4444-4555-8666-777777777777"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest([.. Enumerable.Repeat((byte)0x6A, CovenantLimits.DigestBytes)]));

        CovenantOperationGate gate = new(runtime, new NoCampaignProbe());

        await using CovenantExclusiveLease lease =
            (await gate.AcquireExclusiveAsync(owner, CancellationToken.None)).Value;

        CovenantAuthorityTransitionPublisher publisher = new(
            runtime,
            keys,
            new CovenantAvailability(runtime));

        CovenantErasureTransition winner = new(
            new RecordingCanonical(candidate.Dataset.DatasetGeneration),
            new RecordingStorage(candidate),
            runtime,
            publisher);

        CommitWinnerBeforeFailurePublisher interposed = new(
            winner,
            publisher,
            runtime,
            candidate);

        CovenantErasureTransition subject = new(
            new RecordingCanonical(candidate.Dataset.DatasetGeneration),
            new RecordingStorage(candidate),
            runtime,
            interposed);

        CovenantVerifiedCandidateState malformed = candidate with
        {

            Authority = candidate.Authority with { CurrentMasterKeyVersion = long.MaxValue },

        };

        Result published = await subject.PublishCommittedAsync(
            lease,
            malformed,
            CancellationToken.None);

        Assert.True(published.IsFailure);

        Assert.True(interposed.WinnerResult?.IsSuccess);

        Assert.Same(interposed.Winner, runtime.Current);

        Assert.Equal(2, runtime.Current.RuntimeAuthorityGeneration);

        Assert.False(runtime.Current.AuthorityRetired);

        Assert.NotNull(runtime.Current.Keys);

        Assert.NotNull(runtime.Current.ActiveAuthority);

        Assert.Null(runtime.Current.RecoveryOwner);

    }

    private static CovenantAvailabilitySnapshot SaturatedAvailability(
        CovenantVerifiedCandidateState candidate) =>
        new(
            Generation: long.MaxValue,
            FeatureEnabled: true,
            Canonical: CovenantCapabilityState.Healthy,
            CanonicalSchemaVersion: 11,
            CanonicalInstalledFingerprint: "canonical-fingerprint",
            Accelerator: CovenantCapabilityState.Healthy,
            AcceleratorSchemaVersion: 12,
            AcceleratorInstalledFingerprint: "accelerator-fingerprint",
            candidate.Dataset.DatasetGeneration,
            candidate.Dataset.CanonicalSearchSequence,
            candidate.Dataset.CoreCampaignDeletionSequence,
            candidate.Dataset.AppliedDatasetGeneration,
            candidate.Dataset.AppliedSearchSequence,
            candidate.Dataset.AppliedCampaignDeletionSequence,
            candidate.Dataset.AcceleratorEpoch,
            CovenantFtsSynchronizationState.Synchronized,
            RebuildRequired: false,
            CovenantHealthTransition.Bootstrap,
            CanonicalDiagnosticCode: null,
            AcceleratorDiagnosticCode: null);

    private sealed class TransitionHarness : IDisposable
    {

        internal TransitionHarness(
            CovenantCapabilityState accelerator = CovenantCapabilityState.Healthy)
        {

            Candidate = CandidateState();

            Canonical = new RecordingCanonical(Candidate.Dataset.DatasetGeneration);

            Storage = new RecordingStorage(Candidate);

            Runtime = new CovenantRuntimeGenerationProvider();

            _ = Runtime.PublishAvailability(_ => Availability(accelerator));

            Publisher = new RecordingPublisher();

            Subject = new CovenantErasureTransition(Canonical, Storage, Runtime, Publisher);

        }

        internal CovenantVerifiedCandidateState Candidate { get; }

        internal RecordingCanonical Canonical { get; }

        internal RecordingStorage Storage { get; }

        internal CovenantRuntimeGenerationProvider Runtime { get; }

        internal RecordingPublisher Publisher { get; }

        internal CovenantErasureTransition Subject { get; }

        internal ICovenantExclusiveOperationLease Lease { get; } = new NullExclusiveLease();

        internal CovenantV3MaintenanceCapability Mint(CovenantV3MaintenancePurpose purpose) =>
            CovenantV3MaintenanceCapability.MintAsync(Lease, purpose, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().Value;

        public void Dispose() => Runtime.Dispose();

        internal static CovenantVerifiedCandidateState CandidateState() =>
            new(
                new CovenantCandidateDatasetState(
                    Guid.Parse("00112233-4455-4677-8899-AABBCCDDEEFF"),
                    CanonicalSearchSequence: 17,
                    CoreCampaignDeletionSequence: 4,
                    Guid.Parse("FFEEDDCC-BBAA-4988-8776-554433221100"),
                    AppliedSearchSequence: 13,
                    AppliedCampaignDeletionSequence: 4,
                    AppliedSessionDeletionSequence: 2,
                    AcceleratorEpoch: 29,
                    CovenantFtsRebuildState.Rebuilding,
                    EnvelopeMasterKeyVersion: 7,
                    Enumerable.Repeat((byte)0xC1, 32).ToArray(),
                    EnvelopeKeyEpoch: 31,
                    KeyReclamationEpoch: 37),
                new CovenantCandidateAuthorityState(
                    InstallationIdentity: "verified-installation",
                    AuthorityEpoch: 23,
                    CurrentMasterKeyVersion: 7,
                    Enumerable.Repeat((byte)0xC1, 32).ToArray(),
                    RecoveryEnvelopeEpoch: 37,
                    CovenantHostToolsState.HostToolsTainted,
                    TransitionId: "11111111-2222-4333-8444-555555555555"),
                new CovenantCandidateCapabilityState(
                    AppliedCampaignSequence: 4,
                    AppliedSessionSequence: 2,
                    FullSweepRequired: true));

        private static CovenantAvailabilitySnapshot Availability(CovenantCapabilityState accelerator) =>
            new(
                Generation: 41,
                FeatureEnabled: true,
                Canonical: CovenantCapabilityState.Healthy,
                CanonicalSchemaVersion: 11,
                CanonicalInstalledFingerprint: "canonical-fingerprint",
                Accelerator: accelerator,
                AcceleratorSchemaVersion: accelerator == CovenantCapabilityState.Healthy ? 12 : null,
                AcceleratorInstalledFingerprint: accelerator == CovenantCapabilityState.Healthy
                    ? "accelerator-fingerprint"
                    : null,
                DatasetGeneration: Guid.Parse("AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE"),
                CanonicalSequence: 101,
                CoreCampaignDeletionSequence: 102,
                AppliedDatasetGeneration: null,
                AppliedSequence: null,
                AppliedCampaignDeletionSequence: null,
                AcceleratorEpoch: 103,
                FtsSynchronization: CovenantFtsSynchronizationState.Synchronized,
                RebuildRequired: false,
                LastHealthTransition: CovenantHealthTransition.Bootstrap,
                CanonicalDiagnosticCode: null,
                AcceleratorDiagnosticCode: accelerator == CovenantCapabilityState.Healthy
                    ? null
                    : accelerator == CovenantCapabilityState.Degraded
                        ? "accelerator-degraded"
                        : "accelerator-unavailable");

    }

    private sealed class RecordingCanonical(Guid generation) : ICovenantCanonicalErasure
    {

        internal int ApplyCalls { get; private set; }

        public Task<Result<Guid>> ApplyAsync(
            CovenantExclusiveOperation operation,
            CovenantCanonicalDatasetTransition dataset,
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken)
        {

            ApplyCalls++;

            return Task.FromResult(Result<Guid>.Success(generation));

        }

    }

    private sealed class RecordingStorage(CovenantVerifiedCandidateState candidate)
        : ICovenantLocalErasureStorageHealth
    {

        internal int CloseCalls { get; private set; }

        internal int TruncateCalls { get; private set; }

        internal int CompactCalls { get; private set; }

        internal int InitializeCalls { get; private set; }

        internal int AbsenceCalls { get; private set; }

        internal int ReopenCalls { get; private set; }

        public Task<Result> CloseHandlesAsync(CancellationToken cancellationToken) =>
            Record(() => CloseCalls++);

        public Task<Result> TruncateWalAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            Record(() => TruncateCalls++);

        public Task<Result> CompactAsync(CovenantV3CompactionCapabilities capabilities, CancellationToken cancellationToken) =>
            Record(() => CompactCalls++);

        public Task<Result> InitializeAcceleratorAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            Record(() => InitializeCalls++);

        public Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken) =>
            Record(() => AbsenceCalls++);

        public Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken)
        {

            ReopenCalls++;

            return Task.FromResult(Result<CovenantVerifiedCandidateState>.Success(candidate));

        }

        private static Task<Result> Record(Action increment)
        {

            increment();

            return Task.FromResult(Result.Success());

        }

    }

    private sealed class RecordingPublisher : ICovenantCommittedTransitionPublisher
    {

        internal CovenantRuntimeGenerationState? Expected { get; private set; }

        internal CovenantCommittedAuthorityTransition? Transition { get; private set; }

        public ValueTask<Result> PublishCommittedAsync(
            Result<CovenantCommittedAuthorityTransition> transition,
            ICovenantExclusiveOperationLease lease,
            CovenantRuntimeGenerationState expected,
            CancellationToken cancellationToken)
        {

            Transition = transition.Value;

            Expected = expected;

            return ValueTask.FromResult(Result.Success());

        }

    }

    private sealed class CommitWinnerBeforeFailurePublisher(
        CovenantErasureTransition winner,
        CovenantAuthorityTransitionPublisher publisher,
        CovenantRuntimeGenerationProvider runtime,
        CovenantVerifiedCandidateState candidate) : ICovenantCommittedTransitionPublisher
    {

        internal Result? WinnerResult { get; private set; }

        internal CovenantRuntimeGenerationState? Winner { get; private set; }

        public async ValueTask<Result> PublishCommittedAsync(
            Result<CovenantCommittedAuthorityTransition> transition,
            ICovenantExclusiveOperationLease lease,
            CovenantRuntimeGenerationState expected,
            CancellationToken cancellationToken)
        {

            WinnerResult = await winner.PublishCommittedAsync(
                lease,
                candidate,
                cancellationToken);

            Winner = runtime.Current;

            return await publisher.PublishCommittedAsync(
                transition,
                lease,
                expected,
                cancellationToken);

        }

    }

    private sealed class NullExclusiveLease : ICovenantExclusiveOperationLease
    {

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.NewGuid(),
            1,
            CovenantLeaseKind.Exclusive,
            CovenantLeaseCoverage.Installation,
            null,
            Guid.NewGuid(),
            1,
            1,
            0,
            null,
            null,
            null,
            null,
            new CovenantExclusiveRecoveryOwner(
                Guid.NewGuid(),
                CovenantExclusiveOperation.CovenantReset,
                new CovenantDigest([.. Enumerable.Repeat((byte)0x31, CovenantLimits.DigestBytes)])),
            false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public Result ExecuteWhileHeld(Func<Result> callback) => callback();

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class NoCampaignProbe : ICovenantCampaignScopeProbe
    {

        public ValueTask<Result<CovenantCampaignScopeState>> ResolveAsync(
            Guid campaignId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Result<CovenantCampaignScopeState>>(CovenantCampaignScopeState.Live);

    }

}
