using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

internal static class CovenantEnvelopeRuntimeTestHarness
{

    internal static Result Initialize(
        CovenantEnvelopeMasterKeyProvider keys,
        Span<byte> masterKeyMaterial,
        CovenantCommittedAuthorityTransition transition)
    {

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
            masterKeyMaterial,
            new CovenantEnvelopeBootstrapKeyInput(
                transition.InstallationIdentity,
                transition.MasterKeyVersion,
                transition.CanonicalEnvelopeEpoch,
                transition.RecoveryEnvelopeEpoch,
                transition.Capability.DatasetGeneration));

        if (prepared.IsFailure)
        {

            return prepared.Error;

        }

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        CovenantAvailabilitySnapshot bootAvailability = keys.Runtime.PublishAvailability(
            _ => Availability(transition.Capability, CovenantHealthTransition.Bootstrap));

        CovenantRuntimeGenerationState expected = keys.Runtime.Current;

        return keys.Runtime.Initialize(
            expected,
            owned,
            Authority(transition),
            bootAvailability);

    }

    internal static Result Initialize(
        CovenantEnvelopeMasterKeyProvider keys,
        Span<byte> masterKeyMaterial,
        CovenantEnvelopeBootstrapKeyInput input)
    {

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
            masterKeyMaterial,
            input);

        if (prepared.IsFailure)
        {

            return prepared.Error;

        }

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        CovenantAvailabilitySnapshot bootAvailability = keys.Runtime.PublishAvailability(current => current with
        {

            DatasetGeneration = input.DatasetGeneration,

        });

        CovenantRuntimeGenerationState expected = keys.Runtime.Current;

        return keys.Runtime.Initialize(
            expected,
            owned,
            new CovenantAuthoritySnapshot(
                RuntimeAuthorityGeneration: 1,
                input.InstallationIdentity,
                AuthorityEpoch: 1,
                input.MasterKeyVersion,
                input.RecoveryEnvelopeEpoch,
                CovenantHostToolsState.Clean,
                TransitionId: null),
            bootAvailability);

    }

    internal static Result Publish(
        CovenantEnvelopeMasterKeyProvider keys,
        CovenantCommittedAuthorityTransition transition)
    {

        CovenantRuntimeGenerationState expected = keys.Runtime.Current;

        CovenantCommittedAuthorityTransition normalized = NormalizeTransition(
            transition,
            expected.Availability);

        CovenantAvailability availability = new(keys.Runtime);

        Result<CovenantAvailabilitySnapshot> built = availability.BuildCommittedTransition(
            expected.Availability,
            normalized.Capability,
            CovenantHealthTransition.Reset);

        if (built.IsFailure)
        {

            return built.Error;

        }

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareRekey(normalized);

        if (prepared.IsFailure)
        {

            return prepared.Error;

        }

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        return keys.Runtime.PublishCommitted(expected, owned, normalized, built.Value);

    }

    internal static void PublishOwned(
        CovenantEnvelopeMasterKeyProvider keys,
        CovenantPreparedEnvelopeKeyGeneration prepared,
        CovenantCommittedAuthorityTransition transition)
    {

        CovenantRuntimeGenerationState expected = keys.Runtime.Current;

        CovenantCommittedAuthorityTransition normalized = NormalizeTransition(
            transition,
            expected.Availability);

        CovenantAvailability availability = new(keys.Runtime);

        Result<CovenantAvailabilitySnapshot> built = availability.BuildCommittedTransition(
            expected.Availability,
            normalized.Capability,
            CovenantHealthTransition.Reset);

        if (built.IsFailure)
        {

            throw new InvalidOperationException(built.Error.Message);

        }

        Result published = keys.Runtime.PublishCommitted(
            expected,
            prepared,
            normalized,
            built.Value);

        if (published.IsFailure)
        {

            throw new InvalidOperationException(published.Error.Message);

        }

    }

    internal static void Retire(CovenantEnvelopeMasterKeyProvider keys)
    {

        _ = keys.Runtime.RetireAuthorityGeneration(
            keys.Runtime.Current.RuntimeAuthorityGeneration,
            new CovenantExclusiveRecoveryOwner(
                Guid.Parse("EEEEEEEE-1111-4222-8333-444444444444"),
                CovenantExclusiveOperation.SchemaRepair,
                new CovenantDigest([.. Enumerable.Repeat((byte)0x5A, CovenantLimits.DigestBytes)])));

    }

    private static CovenantAuthoritySnapshot Authority(CovenantCommittedAuthorityTransition transition) =>
        new(
            RuntimeAuthorityGeneration: 1,
            transition.InstallationIdentity,
            transition.AuthorityEpoch,
            transition.MasterKeyVersion,
            transition.RecoveryEnvelopeEpoch,
            transition.HostToolsState,
            transition.TransitionId);

    private static CovenantAvailabilitySnapshot Availability(
        CovenantCommittedCapabilityTransition transition,
        CovenantHealthTransition healthTransition) =>
        new(
            transition.ExpectedGeneration,
            transition.FeatureEnabled,
            transition.Canonical,
            transition.CanonicalSchemaVersion,
            transition.CanonicalInstalledFingerprint,
            transition.Accelerator,
            transition.AcceleratorSchemaVersion,
            transition.AcceleratorInstalledFingerprint,
            transition.DatasetGeneration,
            transition.CanonicalSequence,
            transition.CoreCampaignDeletionSequence,
            transition.AppliedDatasetGeneration,
            transition.AppliedSequence,
            transition.AppliedCampaignDeletionSequence,
            transition.AcceleratorEpoch,
            transition.FtsSynchronization,
            transition.RebuildRequired,
            healthTransition,
            transition.CanonicalDiagnosticCode,
            transition.AcceleratorDiagnosticCode);

    private static CovenantCommittedAuthorityTransition NormalizeTransition(
        CovenantCommittedAuthorityTransition transition,
        CovenantAvailabilitySnapshot expected)
    {

        CovenantCommittedCapabilityTransition capability = transition.Capability;

        CovenantCommittedCapabilityTransition normalized = new(
            expected.Generation,
            checked(expected.Generation + 1),
            capability.FeatureEnabled,
            capability.Canonical,
            capability.CanonicalSchemaVersion,
            capability.CanonicalInstalledFingerprint,
            capability.Accelerator,
            capability.AcceleratorSchemaVersion,
            capability.AcceleratorInstalledFingerprint,
            capability.DatasetGeneration,
            capability.CanonicalSequence,
            capability.CoreCampaignDeletionSequence,
            capability.CanonicalAppliedCampaignDeletionSequence,
            capability.CanonicalAppliedSessionDeletionSequence,
            capability.AppliedDatasetGeneration,
            capability.AppliedSequence,
            capability.AppliedCampaignDeletionSequence,
            capability.AcceleratorEpoch,
            capability.FtsSynchronization,
            capability.RebuildRequired,
            capability.CleanupAppliedCampaignSequence,
            capability.CleanupAppliedSessionSequence,
            capability.CleanupFullSweepRequired,
            capability.CanonicalDiagnosticCode,
            capability.AcceleratorDiagnosticCode);

        return new CovenantCommittedAuthorityTransition(
            transition.InstallationIdentity,
            transition.AuthorityEpoch,
            transition.MasterKeyVersion,
            transition.CanonicalEnvelopeEpoch,
            transition.RecoveryEnvelopeEpoch,
            transition.HostToolsState,
            transition.TransitionId,
            normalized);

    }

}
