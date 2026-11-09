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
            ["GuildLocalHostReadiness", "GuildLocalQuestScheduler", "GuildMaintenanceHostedService", "ApprenticeService"]);

        Assert.NotEmpty(discovery.Items);

        Assert.True(discovery.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(discovery.Diagnostics));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    [Trait("Category", "HostedProducerAdditionalAnalysis")]
    public void Guild_cancellation_recovery_root_keeps_the_exact_source_contract()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Additional,
            ["RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler.RecoverAsync"]);

        Assert.NotEmpty(discovery.Items);

        Assert.True(discovery.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(discovery.Diagnostics));
    }
}
