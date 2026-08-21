using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The process-wide publisher of <see cref="CovenantAvailabilitySnapshot"/>.
/// </summary>
/// <remarks>
/// Publication asks the composite runtime holder to replace one complete immutable snapshot and
/// advance its monotonic availability generation while preserving the exact runtime authority, key,
/// and authority references. Readers never take a lock or see a half-updated tuple.
///
/// <para>Each publication method copies only the fields its publisher actually owns. A mutation
/// commit knows the canonical sequence but not the accelerator's applied tuple, and letting it write
/// both would let an unrelated writer silently mark search current.</para>
/// </remarks>
internal sealed class CovenantAvailability : ICovenantAvailability
{

    private readonly CovenantRuntimeGenerationProvider _runtime;

    internal CovenantAvailability()
        : this(new CovenantRuntimeGenerationProvider())
    {
    }

    internal CovenantAvailability(CovenantRuntimeGenerationProvider runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public CovenantAvailabilitySnapshot Current => _runtime.Current.Availability;

    internal Result<CovenantAvailabilitySnapshot> BuildCommittedTransition(
        CovenantAvailabilitySnapshot expected,
        CovenantCommittedCapabilityTransition transition,
        CovenantHealthTransition healthTransition)
    {

        ArgumentNullException.ThrowIfNull(expected);

        ArgumentNullException.ThrowIfNull(transition);

        if (!Enum.IsDefined(healthTransition))
        {
            throw new ArgumentOutOfRangeException(nameof(healthTransition));
        }

        if (!ReferenceEquals(Current, expected)
            || transition.ExpectedGeneration != expected.Generation
            || transition.Generation != expected.Generation + 1)
        {
            return Result<CovenantAvailabilitySnapshot>.Failure(
                new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "Covenant availability changed before the committed transition was built."));
        }

        return Result<CovenantAvailabilitySnapshot>.Success(
            new CovenantAvailabilitySnapshot(
                transition.Generation,
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
                transition.AcceleratorDiagnosticCode));

    }

    /// <summary>
    /// Publishes the outcome of a schema installation. Copies only the installed schema versions,
    /// normalized installed-catalog fingerprints, and the derived capability states.
    /// </summary>
    internal CovenantAvailabilitySnapshot PublishSchema(
        GrimoireSchemaInstallResult result,
        CovenantHealthTransition transition)
    {

        ArgumentNullException.ThrowIfNull(result);

        return Publish(current => current with
        {

            Canonical = ToCapabilityState(result.CovenantCanonical, degradeOnFailure: false),

            CanonicalSchemaVersion = result.CovenantCanonical.IsHealthy
                ? result.CovenantCanonical.SchemaVersion
                : null,

            CanonicalInstalledFingerprint = result.CovenantCanonical.InstalledCatalogFingerprint,

            // Accelerator failure degrades rather than disables: the canonical fallback still
            // answers every inspection query, only more slowly.
            Accelerator = ToCapabilityState(result.CovenantAccelerator, degradeOnFailure: true),

            AcceleratorSchemaVersion = result.CovenantAccelerator.IsHealthy
                ? result.CovenantAccelerator.SchemaVersion
                : null,

            AcceleratorInstalledFingerprint = result.CovenantAccelerator.InstalledCatalogFingerprint,

            FtsSynchronization = result.CovenantAccelerator.IsHealthy
                ? CovenantFtsSynchronizationState.Dirty
                : CovenantFtsSynchronizationState.Unavailable,

            CanonicalDiagnosticCode = result.CovenantCanonical.DiagnosticCode,

            AcceleratorDiagnosticCode = result.CovenantAccelerator.DiagnosticCode,

            LastHealthTransition = transition,

        });

    }

    /// <summary>
    /// Publishes committed canonical facts: the dataset generation every turn snapshot binds, the
    /// canonical and core Campaign-deletion sequences, and whether a rebuild is owed.
    /// </summary>
    internal CovenantAvailabilitySnapshot PublishCanonicalState(
        Guid datasetGeneration,
        long canonicalSequence,
        long coreCampaignDeletionSequence,
        bool rebuildRequired,
        CovenantHealthTransition transition) =>
        Publish(current => current with
        {

            DatasetGeneration = datasetGeneration,

            CanonicalSequence = canonicalSequence,

            CoreCampaignDeletionSequence = coreCampaignDeletionSequence,

            RebuildRequired = rebuildRequired,

            LastHealthTransition = transition,

        });

    /// <summary>
    /// Publishes the accelerator's applied position atomically. The applied tuple, epoch,
    /// synchronization state, and rebuild flag move together or not at all.
    /// </summary>
    internal CovenantAvailabilitySnapshot PublishAcceleratorState(
        Guid? appliedDatasetGeneration,
        long? appliedSequence,
        long? appliedCampaignDeletionSequence,
        ulong acceleratorEpoch,
        CovenantFtsSynchronizationState synchronization,
        bool rebuildRequired,
        CovenantHealthTransition transition) =>
        Publish(current => current with
        {

            AppliedDatasetGeneration = appliedDatasetGeneration,

            AppliedSequence = appliedSequence,

            AppliedCampaignDeletionSequence = appliedCampaignDeletionSequence,

            AcceleratorEpoch = acceleratorEpoch,

            FtsSynchronization = synchronization,

            RebuildRequired = rebuildRequired,

            LastHealthTransition = transition,

        });

    internal CovenantAvailabilitySnapshot PublishPersistedState(
        Guid datasetGeneration,
        long canonicalSequence,
        long coreCampaignDeletionSequence,
        Guid? appliedDatasetGeneration,
        long? appliedSequence,
        long? appliedCampaignDeletionSequence,
        ulong acceleratorEpoch,
        CovenantFtsSynchronizationState synchronization,
        bool rebuildRequired,
        CovenantHealthTransition transition) =>
        Publish(current => current with
        {

            DatasetGeneration = datasetGeneration,

            CanonicalSequence = canonicalSequence,

            CoreCampaignDeletionSequence = coreCampaignDeletionSequence,

            AppliedDatasetGeneration = appliedDatasetGeneration,

            AppliedSequence = appliedSequence,

            AppliedCampaignDeletionSequence = appliedCampaignDeletionSequence,

            AcceleratorEpoch = acceleratorEpoch,

            FtsSynchronization = synchronization,

            RebuildRequired = rebuildRequired,

            LastHealthTransition = transition,

        });

    /// <summary>
    /// Publishes a live feature switch. Changes only the flag and the generation, so flipping the
    /// feature cannot disturb a cursor a turn already captured.
    /// </summary>
    internal CovenantAvailabilitySnapshot PublishFeatureEnabled(bool featureEnabled) =>
        Publish(current => current with
        {

            FeatureEnabled = featureEnabled,

            LastHealthTransition = CovenantHealthTransition.FeatureConfiguration,

        });

    /// <summary>
    /// One mapping from tier health to public capability state, so no caller invents its own.
    /// </summary>
    internal static CovenantCapabilityState ToCapabilityState(
        GrimoireSchemaTierInstallResult result,
        bool degradeOnFailure)
    {

        ArgumentNullException.ThrowIfNull(result);

        if (result.IsHealthy)
        {

            return CovenantCapabilityState.Healthy;

        }

        return degradeOnFailure
            ? CovenantCapabilityState.Degraded
            : CovenantCapabilityState.Unavailable;

    }

    private CovenantAvailabilitySnapshot Publish(
        Func<CovenantAvailabilitySnapshot, CovenantAvailabilitySnapshot> mutate)
    {

        return _runtime.PublishAvailability(mutate);

    }

}
