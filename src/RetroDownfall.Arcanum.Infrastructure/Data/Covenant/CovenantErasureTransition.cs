using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Publishes one committed transition against the exact runtime state its caller captured.
/// </summary>
internal interface ICovenantCommittedTransitionPublisher
{

    ValueTask<Result> PublishCommittedAsync(
        Result<CovenantCommittedAuthorityTransition> transition,
        ICovenantExclusiveOperationLease lease,
        CovenantRuntimeGenerationState expected,
        CancellationToken cancellationToken);

}

/// <summary>
/// Joins the canonical transaction, storage proof, and atomic runtime publication for one erasure.
/// </summary>
/// <remarks>
/// This adapter opens no connection of its own. The canonical and storage owners retain their one
/// connection policies, while publication consumes only the immutable candidate the sidecar-free
/// reopen returned and one process runtime-state reference captured here.
/// </remarks>
internal sealed class CovenantErasureTransition(
    ICovenantCanonicalErasure canonical,
    ICovenantLocalErasureStorageHealth storage,
    CovenantRuntimeGenerationProvider runtime,
    ICovenantCommittedTransitionPublisher publisher) : ICovenantErasureTransition
{

    private readonly ICovenantCanonicalErasure _canonical =
        canonical ?? throw new ArgumentNullException(nameof(canonical));

    private readonly ICovenantLocalErasureStorageHealth _storage =
        storage ?? throw new ArgumentNullException(nameof(storage));

    private readonly CovenantRuntimeGenerationProvider _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));

    private readonly ICovenantCommittedTransitionPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));

    public Task<Result<Guid>> ApplyCanonicalErasureAsync(
        CovenantExclusiveOperation operation,
        CovenantCanonicalDatasetTransition dataset,
        CovenantV3MaintenanceCapability capability,
        CancellationToken cancellationToken) =>
        _canonical.ApplyAsync(operation, dataset, capability, cancellationToken);

    public Task<Result> CloseHandlesAsync(CancellationToken cancellationToken) =>
        _storage.CloseHandlesAsync(cancellationToken);

    public Task<Result> TruncateWalAsync(
        CovenantV3MaintenanceCapability capability,
        CancellationToken cancellationToken) =>
        _storage.TruncateWalAsync(capability, cancellationToken);

    public Task<Result> CompactAsync(
        CovenantV3CompactionCapabilities capabilities,
        CancellationToken cancellationToken) =>
        _storage.CompactAsync(capabilities, cancellationToken);

    public Task<Result> InitializeAcceleratorAsync(
        CovenantV3MaintenanceCapability capability,
        CancellationToken cancellationToken) =>
        _storage.InitializeAcceleratorAsync(capability, cancellationToken);

    public Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken) =>
        _storage.VerifySidecarAbsenceAsync(cancellationToken);

    public Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
        CovenantV3MaintenanceCapability capability,
        CancellationToken cancellationToken) =>
        _storage.VerifyReopenAsync(capability, cancellationToken);

    public async Task<Result> PublishCommittedAsync(
        ICovenantExclusiveOperationLease lease,
        CovenantVerifiedCandidateState candidate,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(lease);

        ArgumentNullException.ThrowIfNull(candidate);

        CovenantRuntimeGenerationState expected = _runtime.Current;

        Result<CovenantCommittedAuthorityTransition> projected = Project(candidate, expected);

        return await _publisher.PublishCommittedAsync(
                projected,
                lease,
                expected,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private static Result<CovenantCommittedAuthorityTransition> Project(
        CovenantVerifiedCandidateState candidate,
        CovenantRuntimeGenerationState expected)
    {

        CovenantAvailabilitySnapshot availability = expected.Availability;

        bool acceleratorHealthy = availability.Accelerator == CovenantCapabilityState.Healthy;

        long? appliedCampaignDeletionSequence = candidate.Dataset.AppliedDatasetGeneration is null
            ? null
            : candidate.Dataset.AppliedCampaignDeletionSequence;

        try
        {

            CovenantCommittedCapabilityTransition capability = new(
                ExpectedGeneration: availability.Generation,
                Generation: checked(availability.Generation + 1),
                FeatureEnabled: availability.FeatureEnabled,
                Canonical: availability.Canonical,
                CanonicalSchemaVersion: availability.CanonicalSchemaVersion,
                CanonicalInstalledFingerprint: availability.CanonicalInstalledFingerprint,
                Accelerator: availability.Accelerator,
                AcceleratorSchemaVersion: availability.AcceleratorSchemaVersion,
                AcceleratorInstalledFingerprint: availability.AcceleratorInstalledFingerprint,
                DatasetGeneration: candidate.Dataset.DatasetGeneration,
                CanonicalSequence: candidate.Dataset.CanonicalSearchSequence,
                CoreCampaignDeletionSequence: candidate.Dataset.CoreCampaignDeletionSequence,
                CanonicalAppliedCampaignDeletionSequence:
                    candidate.Dataset.AppliedCampaignDeletionSequence,
                CanonicalAppliedSessionDeletionSequence:
                    candidate.Dataset.AppliedSessionDeletionSequence,
                AppliedDatasetGeneration: candidate.Dataset.AppliedDatasetGeneration,
                AppliedSequence: candidate.Dataset.AppliedSearchSequence,
                AppliedCampaignDeletionSequence: appliedCampaignDeletionSequence,
                AcceleratorEpoch: candidate.Dataset.AcceleratorEpoch,
                FtsSynchronization: acceleratorHealthy
                    ? CovenantFtsSynchronizationState.Dirty
                    : CovenantFtsSynchronizationState.Unavailable,
                RebuildRequired: candidate.Dataset.RebuildState != CovenantFtsRebuildState.Idle,
                CleanupAppliedCampaignSequence: candidate.Capability.AppliedCampaignSequence,
                CleanupAppliedSessionSequence: candidate.Capability.AppliedSessionSequence,
                CleanupFullSweepRequired: candidate.Capability.FullSweepRequired,
                CanonicalDiagnosticCode: availability.CanonicalDiagnosticCode,
                AcceleratorDiagnosticCode: availability.AcceleratorDiagnosticCode);

            return Result<CovenantCommittedAuthorityTransition>.Success(
                new CovenantCommittedAuthorityTransition(
                    candidate.Authority.InstallationIdentity,
                    candidate.Authority.AuthorityEpoch,
                    checked((uint)candidate.Authority.CurrentMasterKeyVersion),
                    candidate.Dataset.EnvelopeKeyEpoch,
                    candidate.Authority.RecoveryEnvelopeEpoch,
                    candidate.Authority.HostToolsState,
                    candidate.Authority.TransitionId,
                    capability));

        }
        catch (Exception failed) when (
            failed is ArgumentException or OverflowException or InvalidOperationException)
        {

            return Result<CovenantCommittedAuthorityTransition>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The verified Covenant candidate cannot form a committed runtime transition."));

        }

    }

}
