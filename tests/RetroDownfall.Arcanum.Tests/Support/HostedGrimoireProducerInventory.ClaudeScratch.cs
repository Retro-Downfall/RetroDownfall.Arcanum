using Microsoft.CodeAnalysis.CSharp;

namespace RetroDownfall.Arcanum.Tests.Support;

// Scratch only (never committed): root-scoped production discovery for fast iteration.
internal static partial class HostedGrimoireProducerInventory
{
    internal static Action<string>? CLAUDE_SCRATCH_PROBE_SINK;

    internal static Action<HostedProducerAdmittedStateObservation>? CLAUDE_SCRATCH_STATE_SINK;

    internal static HostedProducerDiscovery<HostedProducerSite> ScratchDiscover(string lane, string[] members)
    {
        IReadOnlyList<NonHostedProducerChainEntry> roots = lane == "additional"
            ? AdditionalProductionRoots.Where(root => members.Contains(root.Member)).ToArray()
            : lane == "adhoc"
                ? members.Select(static spec => spec.Split("::")).Select(static parts => new NonHostedProducerChainEntry(
                    parts[0] + "." + parts[1], parts[2], parts[0], parts[1], HostedProducerAuthorityKind.OwnerBoundMaintenance, "scratch", [])).ToArray()
                : NonHostedCatalog.Where(root => members.Contains(root.Member)).ToArray();

        return DiscoverProducerSites(ProductionCompilations, new([], []), [], roots, rootWorkers: 1, admittedStateObserver: CLAUDE_SCRATCH_STATE_SINK);
    }
}
