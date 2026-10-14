using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string LongRestHostedRootType = "LongRestSweepHostedService";

    private const string LongRestHostedRootOperation = "RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService.ExecuteAsync";

    private const string LongRestHandoffRootOperation = "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance.ProcessAdmittedAsync";

    // The worker's own root owns its lease down to the admitted maintenance: the wait it sleeps on, and the lease it mints and hands by
    // value. The admitted maintenance is a Lease-Handoff Root that owns the lease it is handed from there, with every database access
    // and paid send below the accept callbacks. These are the diagnostics the production closure produced
    // while a pure-data value derived from the lease counted as a carrier of it (1494 escapes, 729 missing Work frontiers)
    // and while the wake raced a retained read against a delay (3 unowned timer-backed tasks). The test names no other
    // residue of the roots (an unclassified histogram), so it stays true as the workstreams that own
    // those land. It reads the shared production discovery, which the production lane already computes once per process.
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void TheLongRestHostedRootOwnsItsWaitItsLeaseAndItsPaidSendsWithNoEscapeOrMissingFrontier()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        string[] ownedByThisRoot =
        [
            "HOSTED_ADMISSION_HANDLE_ESCAPE",
            "HOSTED_SITE_WORK_FRONTIER_MISSING",
            "HOSTED_SITE_EFFECT_FRONTIER_MISSING",
            "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN",
            "HOSTED_DISPOSAL_TARGET_UNRESOLVED",
        ];

        HostedProducerInventoryDiagnostic[] failures = [.. discovery.Diagnostics.Where(diagnostic => ownedByThisRoot.Contains(diagnostic.Code, StringComparer.Ordinal)
            && (diagnostic.Identity.StartsWith(LongRestHostedRootOperation, StringComparison.Ordinal)
                || diagnostic.Identity.StartsWith(LongRestHandoffRootOperation, StringComparison.Ordinal)))];

        Assert.True(failures.Length == 0, string.Join(global::System.Environment.NewLine,
            failures.Take(20).Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));

        HostedProducerSite[] sites = [.. discovery.Items.Where(static site => site.RootType == LongRestHostedRootType)];

        HostedProducerSite[] sends = [.. sites.Where(static site => site.Kind == HostedProducerSiteKind.ProviderCall)];

        Assert.NotEmpty(sends);

        // The Work every send carries is the lease formal of the admitted maintenance, which admits the kind the worker mints.
        Assert.All(sends, send =>
        {
            Assert.NotNull(send.WorkFrontierCapsuleId);

            Assert.NotNull(send.EffectFrontierCapsuleId);

            Assert.Equal(LongRestHandoffRootOperation, send.AuthorityOperationId);

            HostedProducerSite formal = Assert.Single(sites, site => site.CapsuleId == send.WorkFrontierCapsuleId);

            Assert.Equal(HostedGrimoireProducerInventory.RootLeaseFormalCallee, formal.Callee);

            Assert.Equal(GrimoireWorkKind.LongRestSweep, formal.AdmittedWorkKind);
        });

        // One binding row ties that formal to the kind-17 acquisition the worker minted, at the coordinator's awaited call.
        HostedProducerSite binding = Assert.Single(sites, static site => site.Kind == HostedProducerSiteKind.LeaseHandoff);

        Assert.Equal(LongRestHostedRootOperation, binding.AuthorityOperationId);

        Assert.Equal(LongRestHandoffRootOperation, binding.Callee);

        HostedProducerSite acquisition = Assert.Single(sites, site => site.CapsuleId == binding.WorkFrontierCapsuleId);

        Assert.EndsWith(".TryAcquireWorkLease", acquisition.Callee, StringComparison.Ordinal);

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquisition.AdmittedWorkKind);

        // Neither root is near its budget: a new root is held to 6144 states.
        Assert.All(discovery.AnalysisMetrics!.RootTraversals.Where(static root => root.RootType == LongRestHostedRootType),
            static root => Assert.True(root.AnalyzedStates <= 6144, root.RootOperation + " analysed " + root.AnalyzedStates + " states."));

        HostedProducerSite[] reads = [.. sites.Where(static site => site.Kind == HostedProducerSiteKind.DatabaseAccess)];

        Assert.NotEmpty(reads);

        Assert.All(reads, static read => Assert.NotNull(read.WorkFrontierCapsuleId));
    }
}
