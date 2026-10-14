using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string LongRestHostedRootType = "LongRestSweepHostedService";

    private const string LongRestHostedRootOperation = "RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService.ExecuteAsync";

    // The worker's own root owns its lease end to end: the wait it sleeps on, the lease it mints and hands by value, and every
    // database access and paid send below the accept callbacks. These are the diagnostics the production closure produced
    // while a pure-data value derived from the lease counted as a carrier of it (1494 escapes, 729 missing Work frontiers)
    // and while the wake raced a retained read against a delay (3 unowned timer-backed tasks). The test names no other
    // residue of the root (its state budget, an unclassified histogram), so it stays true as the workstreams that own
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
            && diagnostic.Identity.StartsWith(LongRestHostedRootOperation, StringComparison.Ordinal))];

        Assert.True(failures.Length == 0, string.Join(global::System.Environment.NewLine,
            failures.Take(20).Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));

        HostedProducerSite[] sites = [.. discovery.Items.Where(static site => site.RootType == LongRestHostedRootType)];

        HostedProducerSite[] sends = [.. sites.Where(static site => site.Kind == HostedProducerSiteKind.ProviderCall)];

        Assert.NotEmpty(sends);

        Assert.All(sends, send =>
        {
            Assert.NotNull(send.WorkFrontierCapsuleId);

            Assert.NotNull(send.EffectFrontierCapsuleId);

            // The Work every send carries is the kind-17 acquisition the worker minted.
            Assert.Equal(GrimoireWorkKind.LongRestSweep, Assert.Single(sites.Where(site => site.CapsuleId == send.WorkFrontierCapsuleId)
                .Select(static site => site.AdmittedWorkKind).Distinct()));
        });

        HostedProducerSite[] reads = [.. sites.Where(static site => site.Kind == HostedProducerSiteKind.DatabaseAccess)];

        Assert.NotEmpty(reads);

        Assert.All(reads, static read => Assert.NotNull(read.WorkFrontierCapsuleId));
    }
}
