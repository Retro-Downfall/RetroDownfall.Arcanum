using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Guild_primary_producer_roots_keep_the_exact_source_contract()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Primary,
            ["GuildLocalHostReadiness", "GuildLocalQuestScheduler", "ApprenticeService"]);

        Assert.NotEmpty(discovery.Items);

        Assert.True(discovery.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(discovery.Diagnostics));
    }
}
