using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Quantized_backfill_database_access_stays_under_ordinary_work_without_provider_calls()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        const string root = "QuantizedEmbeddingBackfillService";

        const string operation = "RetroDownfall.Arcanum.Infrastructure.Hosting.QuantizedEmbeddingBackfillService.ExecuteAsync";

        HostedProducerSite[] sites = [.. discovery.Items.Where(static site => site.RootType == root)];

        Assert.NotEmpty(sites);

        HostedProducerSite acquisition = Assert.Single(sites, static site => site.Callee.EndsWith(".TryAcquireWorkLease", StringComparison.Ordinal));

        Assert.Equal(GrimoireWorkKind.QuantizedEmbeddingBackfill, acquisition.AdmittedWorkKind);

        HostedProducerSite[] readsAndWrites = [.. sites.Where(static site => site.Kind == HostedProducerSiteKind.DatabaseAccess)];

        Assert.NotEmpty(readsAndWrites);

        Assert.All(readsAndWrites, static site => Assert.NotNull(site.WorkFrontierCapsuleId));

        Assert.DoesNotContain(sites, static site => site.Kind == HostedProducerSiteKind.ProviderCall);

        HostedProducerInventoryDiagnostic[] failures = [.. discovery.Diagnostics.Where(static diagnostic => diagnostic.Identity.StartsWith(operation, StringComparison.Ordinal))];

        Assert.True(failures.Length == 0, string.Join(global::System.Environment.NewLine,
            failures.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Identity}: {diagnostic.Detail}")));
    }
}
