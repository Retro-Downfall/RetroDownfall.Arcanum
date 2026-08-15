using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The process-wide publisher of <see cref="CovenantAvailabilitySnapshot"/>.
/// </summary>
/// <remarks>
/// Publication replaces one complete immutable snapshot with <see cref="Interlocked.Exchange{T}"/>
/// and advances a monotonic generation. That is the whole concurrency design: readers never take a
/// lock, never see a half-updated view, and can capture a generation early in a turn and prove at
/// dispatch time that availability has not changed underneath them.
///
/// <para>Each publication method copies only the fields its publisher actually owns. A mutation
/// commit knows the canonical sequence but not the accelerator's applied tuple, and letting it write
/// both would let an unrelated writer silently mark search current.</para>
/// </remarks>
internal sealed class CovenantAvailability : ICovenantAvailability
{

    private CovenantAvailabilitySnapshot _current = new(
        Generation: 1,
        FeatureEnabled: false,
        Canonical: CovenantCapabilityState.Unavailable,
        CanonicalSchemaVersion: null,
        CanonicalInstalledFingerprint: null,
        Accelerator: CovenantCapabilityState.Unavailable,
        AcceleratorSchemaVersion: null,
        AcceleratorInstalledFingerprint: null,
        DatasetGeneration: null,
        CanonicalSequence: 0,
        CoreCampaignDeletionSequence: 0,
        AppliedDatasetGeneration: null,
        AppliedSequence: null,
        AppliedCampaignDeletionSequence: null,
        AcceleratorEpoch: 0,
        FtsSynchronization: CovenantFtsSynchronizationState.Unavailable,
        RebuildRequired: true,
        LastHealthTransition: CovenantHealthTransition.Bootstrap,
        CanonicalDiagnosticCode: null,
        AcceleratorDiagnosticCode: null);

    public CovenantAvailabilitySnapshot Current => Volatile.Read(ref _current);

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

        while (true)
        {

            CovenantAvailabilitySnapshot current = Volatile.Read(ref _current);

            CovenantAvailabilitySnapshot next = mutate(current) with
            {

                Generation = current.Generation + 1,

            };

            // Compare-exchange rather than a plain swap: two publishers committing at once must
            // produce two distinct generations, never one that silently overwrites the other.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, next, current), current))
            {

                return next;

            }

        }

    }

}
