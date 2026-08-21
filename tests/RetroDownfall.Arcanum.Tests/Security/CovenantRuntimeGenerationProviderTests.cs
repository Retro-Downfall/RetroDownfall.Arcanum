using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class CovenantRuntimeGenerationProviderTests
{

    private static readonly Guid Dataset = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private static readonly Guid NextDataset = Guid.Parse("AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE");

    [Fact]
    public void Codec_key_captures_bind_the_runtime_authority_generation()
    {

        Assert.NotNull(typeof(CovenantEnvelopeKeyReservation).GetProperty("RuntimeAuthorityGeneration"));

        Assert.NotNull(typeof(CovenantEnvelopeKeyCapture).GetProperty("RuntimeAuthorityGeneration"));

        Type[] materializationParameters = typeof(ICovenantEnvelopeMasterKeyProvider)
            .GetMethod(nameof(ICovenantEnvelopeMasterKeyProvider.AcquireMaterializationLease))!
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal([typeof(long), typeof(CovenantEnvelopeKeyGenerationIdentity)], materializationParameters);

    }

    [Fact]
    public void Injectable_facades_expose_no_independent_live_generation_mutator()
    {

        Dictionary<Type, string[]> forbiddenByFacade = new()
        {
            [typeof(CovenantEnvelopeMasterKeyProvider)] =
            [
                "Initialize",
                "Rekey",
                "PublishPrepared",
                "RetireCurrentGeneration",
            ],

            [typeof(CovenantAuthoritySnapshotProvider)] = ["Publish", "Withdraw"],

            [typeof(CovenantAvailability)] = ["PublishCommitted", "ApplyCommittedTransition"],
        };

        foreach ((Type facade, string[] forbidden) in forbiddenByFacade)
        {

            string[] declared = facade
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .ToArray();

            Assert.Empty(declared.Intersect(forbidden, StringComparer.Ordinal));

        }

    }

    [Fact]
    public void Initialization_and_availability_publication_project_one_composite_state()
    {

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        CovenantAuthoritySnapshotProvider authority = new(runtime);

        CovenantAvailability availability = new(runtime);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
            Encoding.UTF8.GetBytes("runtime-generation-master-material"),
            new CovenantEnvelopeBootstrapKeyInput(
                "runtime-generation-installation",
                masterKeyVersion: 3,
                canonicalEnvelopeEpoch: 5,
                recoveryEnvelopeEpoch: 7,
                datasetGeneration: null));

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        CovenantRuntimeGenerationState expected = runtime.Current;

        Result initialized = runtime.Initialize(
            expected,
            owned,
            new CovenantAuthoritySnapshot(
                RuntimeAuthorityGeneration: 1,
                InstallationIdentity: "runtime-generation-installation",
                AuthorityEpoch: 11,
                MasterKeyVersion: 3,
                RecoveryEnvelopeEpoch: 7,
                HostToolsState: CovenantHostToolsState.Clean,
                TransitionId: null),
            availability.Current);

        Assert.True(initialized.IsSuccess);

        CovenantRuntimeGenerationState first = runtime.Current;

        Assert.Same(first.Keys, keys.Current);

        Assert.Same(first.ActiveAuthority, authority.Current);

        Assert.Same(first.Availability, availability.Current);

        CovenantAvailabilitySnapshot next = availability.PublishFeatureEnabled(true);

        CovenantRuntimeGenerationState second = runtime.Current;

        Assert.NotSame(first, second);

        Assert.Equal(first.RuntimeAuthorityGeneration, second.RuntimeAuthorityGeneration);

        Assert.Same(first.Keys, second.Keys);

        Assert.Same(first.ActiveAuthority, second.ActiveAuthority);

        Assert.Same(next, second.Availability);

    }

    [Fact]
    public void Initialization_rejects_prepared_keys_bound_to_different_authority()
    {

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
            Encoding.UTF8.GetBytes("runtime-generation-master-material"),
            new CovenantEnvelopeBootstrapKeyInput(
                "different-installation",
                masterKeyVersion: 3,
                canonicalEnvelopeEpoch: 5,
                recoveryEnvelopeEpoch: 7,
                datasetGeneration: null));

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        CovenantRuntimeGenerationState expected = runtime.Current;

        Result initialized = runtime.Initialize(
            expected,
            owned,
            new CovenantAuthoritySnapshot(
                RuntimeAuthorityGeneration: 1,
                InstallationIdentity: "runtime-generation-installation",
                AuthorityEpoch: 11,
                MasterKeyVersion: 3,
                RecoveryEnvelopeEpoch: 7,
                HostToolsState: CovenantHostToolsState.Clean,
                TransitionId: null),
            expected.Availability);

        Assert.True(initialized.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, initialized.Error.Code);

        Assert.Same(expected, runtime.Current);

    }

    [Fact]
    public void Initialization_rejects_dataset_keys_bound_to_different_availability()
    {

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
            Encoding.UTF8.GetBytes("runtime-generation-master-material"),
            new CovenantEnvelopeBootstrapKeyInput(
                "runtime-generation-installation",
                masterKeyVersion: 3,
                canonicalEnvelopeEpoch: 5,
                recoveryEnvelopeEpoch: 7,
                Dataset));

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        _ = runtime.PublishAvailability(current => current with
        {

            DatasetGeneration = NextDataset,

        });

        CovenantRuntimeGenerationState expected = runtime.Current;

        Result initialized = runtime.Initialize(
            expected,
            owned,
            new CovenantAuthoritySnapshot(
                RuntimeAuthorityGeneration: 1,
                InstallationIdentity: "runtime-generation-installation",
                AuthorityEpoch: 11,
                MasterKeyVersion: 3,
                RecoveryEnvelopeEpoch: 7,
                HostToolsState: CovenantHostToolsState.Clean,
                TransitionId: null),
            expected.Availability);

        Assert.True(initialized.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, initialized.Error.Code);

        Assert.Same(expected, runtime.Current);

    }

    [Fact]
    public async Task Availability_only_publication_preserves_runtime_bound_contexts_epochs_and_leases()
    {

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        CovenantAvailability availability = new(runtime);

        Initialize(runtime, keys, availability.Current);

        CovenantAuthoritySnapshotProvider authority = new(runtime);

        OperatorAuthorityContextIssuer issuer = new(authority);

        CovenantOperationGate gate = new(runtime, new NoCampaignProbe());

        OperatorAuthorityContext context = issuer.Issue(
            CovenantAuthorityRequirement.CovenantManage).Value;

        CovenantReadAuthorityEpoch epoch = issuer.IssueReadEpoch().Value;

        await using CovenantReadLease lease = (await gate.AcquireReadAsync(
            CovenantOperationScope.Global,
            CancellationToken.None)).Value;

        long runtimeGeneration = runtime.Current.RuntimeAuthorityGeneration;

        _ = availability.PublishFeatureEnabled(featureEnabled: false);

        Assert.Equal(runtimeGeneration, runtime.Current.RuntimeAuthorityGeneration);

        Assert.True(context.Matches(authority.Current));

        Assert.True(epoch.Matches(authority.Current));

        Assert.True((await lease.RevalidateAsync(CancellationToken.None)).IsSuccess);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dataset_publication_is_forbidden_after_authority_initialization(bool retireAuthority)
    {

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        CovenantAvailability availability = new(runtime);

        Initialize(runtime, keys, availability.Current);

        if (retireAuthority)
        {

            _ = runtime.RetireAuthorityGeneration(
                runtime.Current.RuntimeAuthorityGeneration,
                new CovenantExclusiveRecoveryOwner(
                    Guid.Parse("EEEEEEEE-1111-4222-8333-444444444444"),
                    CovenantExclusiveOperation.SchemaRepair,
                    new CovenantDigest([.. Enumerable.Repeat((byte)0x5A, CovenantLimits.DigestBytes)])));

        }

        CovenantRuntimeGenerationState expected = runtime.Current;

        _ = Assert.Throws<InvalidOperationException>(() => availability.PublishPersistedState(
            NextDataset,
            expected.Availability.CanonicalSequence,
            expected.Availability.CoreCampaignDeletionSequence,
            expected.Availability.AppliedDatasetGeneration,
            expected.Availability.AppliedSequence,
            expected.Availability.AppliedCampaignDeletionSequence,
            expected.Availability.AcceleratorEpoch,
            expected.Availability.FtsSynchronization,
            expected.Availability.RebuildRequired,
            CovenantHealthTransition.Restore));

        Assert.Same(expected, runtime.Current);

        Assert.Same(expected.Keys, runtime.Current.Keys);

        Assert.Same(expected.AuthoritySlot, runtime.Current.AuthoritySlot);

        Assert.Same(expected.Availability, runtime.Current.Availability);

        Assert.Equal(expected.RecoveryOwner, runtime.Current.RecoveryOwner);

    }

    [Fact]
    public void Pre_initialization_persisted_publication_can_establish_the_dataset()
    {

        using CovenantRuntimeGenerationProvider runtime = new();

        CovenantAvailability availability = new(runtime);

        long runtimeGeneration = runtime.Current.RuntimeAuthorityGeneration;

        CovenantAvailabilitySnapshot published = availability.PublishPersistedState(
            NextDataset,
            canonicalSequence: 3,
            coreCampaignDeletionSequence: 4,
            appliedDatasetGeneration: null,
            appliedSequence: null,
            appliedCampaignDeletionSequence: null,
            acceleratorEpoch: 5,
            CovenantFtsSynchronizationState.Unavailable,
            rebuildRequired: true,
            CovenantHealthTransition.Bootstrap);

        Assert.Same(published, runtime.Current.Availability);

        Assert.Equal(NextDataset, runtime.Current.Availability.DatasetGeneration);

        Assert.Equal(runtimeGeneration, runtime.Current.RuntimeAuthorityGeneration);

        Assert.Null(runtime.Current.Keys);

        Assert.Null(runtime.Current.AuthoritySlot);

    }

    [Fact]
    public void Final_publication_rejects_an_availability_tuple_that_disagrees_with_capability()
    {

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        CovenantAvailability availability = new(runtime);

        Initialize(runtime, keys, availability.Current);

        CovenantRuntimeGenerationState expected = runtime.Current;

        CovenantCommittedAuthorityTransition transition = Transition(expected.Availability);

        Result<CovenantAvailabilitySnapshot> built = availability.BuildCommittedTransition(
            expected.Availability,
            transition.Capability,
            CovenantHealthTransition.Reset);

        Assert.True(built.IsSuccess);

        CovenantAvailabilitySnapshot disagreeing = built.Value with
        {

            FeatureEnabled = !transition.Capability.FeatureEnabled,

            CanonicalDiagnosticCode = "covenant.synthetic_mismatch",

        };

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareRekey(transition);

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        Result published = runtime.PublishCommitted(expected, owned, transition, disagreeing);

        Assert.True(published.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, published.Error.Code);

        Assert.Same(expected, runtime.Current);

    }

    [Fact]
    public void Final_publication_rejects_prepared_keys_from_a_different_capability()
    {

        using CovenantRuntimeGenerationProvider runtime = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        CovenantAvailability availability = new(runtime);

        Initialize(runtime, keys, availability.Current);

        CovenantRuntimeGenerationState expected = runtime.Current;

        CovenantCommittedAuthorityTransition transition = Transition(expected.Availability);

        CovenantCommittedAuthorityTransition different = new(
            transition.InstallationIdentity,
            transition.AuthorityEpoch,
            transition.MasterKeyVersion,
            transition.CanonicalEnvelopeEpoch,
            transition.RecoveryEnvelopeEpoch,
            transition.HostToolsState,
            transition.TransitionId,
            CopyCapability(transition.Capability, Guid.NewGuid()));

        Result<CovenantAvailabilitySnapshot> built = availability.BuildCommittedTransition(
            expected.Availability,
            transition.Capability,
            CovenantHealthTransition.Reset);

        Assert.True(built.IsSuccess);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareRekey(different);

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        Result published = runtime.PublishCommitted(expected, owned, transition, built.Value);

        Assert.True(published.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, published.Error.Code);

        Assert.Same(expected, runtime.Current);

    }

    [Theory]
    [InlineData((byte)CovenantRuntimePublicationStep.CommittedBeforeSwap)]
    [InlineData((byte)CovenantRuntimePublicationStep.CommittedAfterSwap)]
    public void Committed_publication_observer_failure_is_non_authoritative(byte faultStepValue)
    {

        CovenantRuntimePublicationStep faultStep = (CovenantRuntimePublicationStep)faultStepValue;

        ThrowingPublicationCheckpoint checkpoint = new(faultStep);

        using CovenantRuntimeGenerationProvider runtime = new(checkpoint);

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        CovenantAvailability availability = new(runtime);

        Initialize(runtime, keys, availability.Current);

        CovenantEnvelopeCodec codec = new(keys, TimeProvider.System);

        string predecessorToken = codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [0x31],
            TimeSpan.FromMinutes(30)).Value;

        CovenantRuntimeGenerationState predecessor = runtime.Current;

        CovenantEnvelopeKeyGeneration predecessorKeys = Assert.IsType<CovenantEnvelopeKeyGeneration>(
            predecessor.Keys);

        CovenantCommittedAuthorityTransition transition = Transition(predecessor.Availability);

        Result<CovenantAvailabilitySnapshot> built = availability.BuildCommittedTransition(
            predecessor.Availability,
            transition.Capability,
            CovenantHealthTransition.Reset);

        Assert.True(built.IsSuccess);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareRekey(transition);

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        checkpoint.Arm();

        Result published = runtime.PublishCommitted(
            predecessor,
            owned,
            transition,
            built.Value);

        Assert.True(published.IsSuccess);

        Assert.Equal(1, checkpoint.FaultCount);

        Assert.False(owned.Matches(transition));

        CovenantRuntimeGenerationState successor = runtime.Current;

        Assert.NotSame(predecessor, successor);

        Assert.Equal(
            predecessor.RuntimeAuthorityGeneration + 1,
            successor.RuntimeAuthorityGeneration);

        Assert.NotSame(predecessorKeys, successor.Keys);

        Assert.Same(built.Value, successor.Availability);

        Assert.NotNull(successor.ActiveAuthority);

        Assert.False(successor.AuthorityRetired);

        Span<byte> disposedKey = stackalloc byte[32];

        try
        {

            Assert.Equal(
                CovenantEnvelopeKeyCopyStatus.NoGeneration,
                predecessorKeys.TryCopyPurposeKey(
                    CovenantEnvelopePurpose.Cursor,
                    disposedKey,
                    predecessor.RuntimeAuthorityGeneration,
                    out _));

        }
        finally
        {

            CryptographicOperations.ZeroMemory(disposedKey);

        }

        Assert.True(codec.Decode(
            CovenantEnvelopePurpose.Cursor,
            predecessorToken).IsFailure);

        string successorToken = codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [0x32],
            TimeSpan.FromMinutes(30)).Value;

        Assert.True(codec.Decode(
            CovenantEnvelopePurpose.Cursor,
            successorToken).IsSuccess);

    }

    [Theory]
    [InlineData((byte)CovenantRuntimePublicationStep.AvailabilityBeforeSwap)]
    [InlineData((byte)CovenantRuntimePublicationStep.AvailabilityAfterSwap)]
    public void Availability_publication_observer_failure_is_non_authoritative(byte faultStepValue)
    {

        CovenantRuntimePublicationStep faultStep = (CovenantRuntimePublicationStep)faultStepValue;

        ThrowingPublicationCheckpoint checkpoint = new(faultStep);

        using CovenantRuntimeGenerationProvider runtime = new(checkpoint);

        using CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        CovenantAvailability availability = new(runtime);

        Initialize(runtime, keys, availability.Current);

        CovenantRuntimeGenerationState predecessor = runtime.Current;

        checkpoint.Arm();

        CovenantAvailabilitySnapshot published = availability.PublishFeatureEnabled(featureEnabled: false);

        Assert.Equal(1, checkpoint.FaultCount);

        CovenantRuntimeGenerationState successor = runtime.Current;

        Assert.NotSame(predecessor, successor);

        Assert.Same(published, successor.Availability);

        Assert.Equal(predecessor.Availability.Generation + 1, published.Generation);

        Assert.False(published.FeatureEnabled);

        Assert.Equal(
            predecessor.RuntimeAuthorityGeneration,
            successor.RuntimeAuthorityGeneration);

        Assert.Same(predecessor.Keys, successor.Keys);

        Assert.Same(predecessor.AuthoritySlot, successor.AuthoritySlot);

        Assert.Equal(predecessor.CanonicalEnvelopeEpoch, successor.CanonicalEnvelopeEpoch);

        Assert.Equal(predecessor.AuthorityRetired, successor.AuthorityRetired);

        Assert.Equal(predecessor.RecoveryOwner, successor.RecoveryOwner);

    }

    private static void Initialize(
        CovenantRuntimeGenerationProvider runtime,
        CovenantEnvelopeMasterKeyProvider keys,
        CovenantAvailabilitySnapshot availability)
    {

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
            Encoding.UTF8.GetBytes("runtime-generation-master-material"),
            new CovenantEnvelopeBootstrapKeyInput(
                "runtime-generation-installation",
                masterKeyVersion: 3,
                canonicalEnvelopeEpoch: 5,
                recoveryEnvelopeEpoch: 7,
                Dataset));

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        CovenantAvailabilitySnapshot bootAvailability = runtime.PublishAvailability(_ => availability with
        {

            FeatureEnabled = true,

            Canonical = CovenantCapabilityState.Healthy,

            CanonicalSchemaVersion = 1,

            CanonicalInstalledFingerprint = "sha256-canonical",

            Accelerator = CovenantCapabilityState.Healthy,

            AcceleratorSchemaVersion = 1,

            AcceleratorInstalledFingerprint = "sha256-accelerator",

            DatasetGeneration = Dataset,

            FtsSynchronization = CovenantFtsSynchronizationState.Synchronized,

            RebuildRequired = false,

        });

        CovenantRuntimeGenerationState expected = runtime.Current;

        Assert.True(runtime.Initialize(
            expected,
            owned,
            new CovenantAuthoritySnapshot(
                RuntimeAuthorityGeneration: 1,
                InstallationIdentity: "runtime-generation-installation",
                AuthorityEpoch: 11,
                MasterKeyVersion: 3,
                RecoveryEnvelopeEpoch: 7,
                HostToolsState: CovenantHostToolsState.Clean,
                TransitionId: null),
            bootAvailability).IsSuccess);

    }

    private static CovenantCommittedAuthorityTransition Transition(
        CovenantAvailabilitySnapshot expected) =>
        new(
            "runtime-generation-installation",
            authorityEpoch: 11,
            masterKeyVersion: 3,
            canonicalEnvelopeEpoch: 6,
            recoveryEnvelopeEpoch: 7,
            CovenantHostToolsState.Clean,
            transitionId: null,
            new CovenantCommittedCapabilityTransition(
                expected.Generation,
                expected.Generation + 1,
                FeatureEnabled: true,
                CovenantCapabilityState.Healthy,
                CanonicalSchemaVersion: 2,
                CanonicalInstalledFingerprint: "sha256-next-canonical",
                CovenantCapabilityState.Degraded,
                AcceleratorSchemaVersion: null,
                AcceleratorInstalledFingerprint: "sha256-next-accelerator",
                NextDataset,
                CanonicalSequence: 3,
                CoreCampaignDeletionSequence: 4,
                CanonicalAppliedCampaignDeletionSequence: 4,
                CanonicalAppliedSessionDeletionSequence: 5,
                AppliedDatasetGeneration: null,
                AppliedSequence: null,
                AppliedCampaignDeletionSequence: null,
                AcceleratorEpoch: 8,
                CovenantFtsSynchronizationState.Unavailable,
                RebuildRequired: true,
                CleanupAppliedCampaignSequence: 4,
                CleanupAppliedSessionSequence: 5,
                CleanupFullSweepRequired: false,
                CanonicalDiagnosticCode: null,
                AcceleratorDiagnosticCode: "covenant.accelerator_degraded"));

    private static CovenantCommittedCapabilityTransition CopyCapability(
        CovenantCommittedCapabilityTransition source,
        Guid datasetGeneration) =>
        new(
            source.ExpectedGeneration,
            source.Generation,
            source.FeatureEnabled,
            source.Canonical,
            source.CanonicalSchemaVersion,
            source.CanonicalInstalledFingerprint,
            source.Accelerator,
            source.AcceleratorSchemaVersion,
            source.AcceleratorInstalledFingerprint,
            datasetGeneration,
            source.CanonicalSequence,
            source.CoreCampaignDeletionSequence,
            source.CanonicalAppliedCampaignDeletionSequence,
            source.CanonicalAppliedSessionDeletionSequence,
            source.AppliedDatasetGeneration,
            source.AppliedSequence,
            source.AppliedCampaignDeletionSequence,
            source.AcceleratorEpoch,
            source.FtsSynchronization,
            source.RebuildRequired,
            source.CleanupAppliedCampaignSequence,
            source.CleanupAppliedSessionSequence,
            source.CleanupFullSweepRequired,
            source.CanonicalDiagnosticCode,
            source.AcceleratorDiagnosticCode);

    private sealed class NoCampaignProbe : ICovenantCampaignScopeProbe
    {

        public ValueTask<Result<CovenantCampaignScopeState>> ResolveAsync(
            Guid campaignId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Result<CovenantCampaignScopeState>>(CovenantCampaignScopeState.Live);

    }

    private sealed class ThrowingPublicationCheckpoint(
        CovenantRuntimePublicationStep faultStep) : ICovenantRuntimePublicationCheckpoint
    {

        private int _armed;

        private int _faultCount;

        internal int FaultCount => Volatile.Read(ref _faultCount);

        internal void Arm() => Volatile.Write(ref _armed, 1);

        public void Reached(CovenantRuntimePublicationStep step)
        {

            if (Volatile.Read(ref _armed) == 0 || step != faultStep)
            {

                return;

            }

            _ = Interlocked.Increment(ref _faultCount);

            throw new InvalidOperationException("Injected runtime publication observer failure.");

        }

    }

}
