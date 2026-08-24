using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Covenant.Benchmarks;

/// <summary>
/// Three of the four seams a live host supplies from its own runtime, and the whole of what this file
/// holds; the fourth is <c>CovenantWorkloadBed.FixedConnectionSource</c>.
/// </summary>
/// <remarks>
/// These are adapters, not stand-ins for anything measured. Availability and authority come from the
/// process's own runtime generation in a real host, and Campaign scope comes from the core registry;
/// a benchmark process has neither, so it states them. Nothing here decides, compiles, links, admits,
/// or stores — every one of those is the production service under measurement.
///
/// <para>The count matters because DESIGN enumerates the substituted seams, and an inventory that is
/// short by one is a reader's assurance that something is production when it is not.</para>
/// </remarks>
internal sealed class BenchmarkAvailability(Guid datasetGeneration) : ICovenantAvailability
{

    private CovenantRuntimeGenerationProvider? _runtime;

    private readonly CovenantAvailabilitySnapshot _boot = new(
        Generation: 1,
        FeatureEnabled: true,
        Canonical: CovenantCapabilityState.Healthy,
        CanonicalSchemaVersion: 1,
        CanonicalInstalledFingerprint: "benchmark",
        Accelerator: CovenantCapabilityState.Unavailable,
        AcceleratorSchemaVersion: 0,
        AcceleratorInstalledFingerprint: null,
        DatasetGeneration: datasetGeneration,
        CanonicalSequence: 1,
        CoreCampaignDeletionSequence: 0,
        AppliedDatasetGeneration: datasetGeneration,
        AppliedSequence: 1,
        AppliedCampaignDeletionSequence: 0,
        AcceleratorEpoch: 0,
        FtsSynchronization: CovenantFtsSynchronizationState.Synchronized,
        RebuildRequired: false,
        LastHealthTransition: CovenantHealthTransition.Bootstrap,
        CanonicalDiagnosticCode: null,
        AcceleratorDiagnosticCode: null);

    public CovenantAvailabilitySnapshot Current => _runtime?.Current.Availability ?? _boot;

    internal void Bind(CovenantRuntimeGenerationProvider runtime) => _runtime = runtime;

}

internal sealed class BenchmarkAuthority(Guid installationIdentity) : ICovenantAuthoritySnapshotProvider
{

    private CovenantRuntimeGenerationProvider? _runtime;

    private readonly CovenantAuthoritySnapshot _boot = new(
        RuntimeAuthorityGeneration: 1,
        InstallationIdentity: installationIdentity.ToString("D").ToUpperInvariant(),
        AuthorityEpoch: 1,
        MasterKeyVersion: 1,
        RecoveryEnvelopeEpoch: 1,
        HostToolsState: CovenantHostToolsState.Clean,
        TransitionId: null);

    public CovenantAuthoritySnapshot? Current => _runtime is { } runtime
        ? runtime.Current.ActiveAuthority
        : _boot;

    internal void Bind(CovenantRuntimeGenerationProvider runtime) => _runtime = runtime;

}

internal sealed class BenchmarkCampaignScopeProbe(Func<Guid[]> campaigns) : ICovenantCampaignScopeProbe
{

    public ValueTask<Result<CovenantCampaignScopeState>> ResolveAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        // Deleted rather than Live for anything unseeded. A probe that called every identity live
        // would let a mistyped Campaign in the workload read as a real one holding nothing.
        return ValueTask.FromResult(Result<CovenantCampaignScopeState>.Success(
            Array.IndexOf(campaigns(), campaignId) >= 0
                ? CovenantCampaignScopeState.Live
                : CovenantCampaignScopeState.Deleted));

    }

}
