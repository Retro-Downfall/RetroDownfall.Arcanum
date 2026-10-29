using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualStartupAndOwnerRecoveryKeepClosedDispatchAfterCompositionProof()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        _ = ObserveFullProductionRoot(result, "LongRunningOperationStartupHostedService");

        _ = ObserveFullProductionRoot(result,
            "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery",
            "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync");

        Assert.NotEmpty(result.Items);

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code.StartsWith("HOSTED_RECOVERY_", StringComparison.Ordinal)
                || diagnostic.Code == "HOSTED_AGGREGATE_PROOF_MISSING")];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));
    }
}
