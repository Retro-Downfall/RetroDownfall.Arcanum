using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Guild_primary_producer_roots_keep_the_exact_source_contract()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        string[] roots = ["GuildLocalHostReadiness", "GuildLocalQuestScheduler", "GuildMaintenanceHostedService", "ApprenticeService"];

        foreach (string root in roots) Assert.NotEmpty(ObserveFullProductionRoot(discovery, root));

        Assert.True(discovery.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(discovery.Diagnostics));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Guild_effect_admission_keeps_other_hosted_callers_and_exact_manifest_frontiers_qualified()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        string[] roots = ["Loremaster", "SagaExtractionService", "SessionAttachmentIndexingService", "ApprenticeService", "GuildLocalQuestScheduler"];

        HostedProducerSite[] observed = [.. roots.SelectMany(root => ObserveFullProductionRoot(discovery, root))];

        Assert.True(discovery.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(discovery.Diagnostics));

        string manifest = HostedGrimoireProducerInventory.RenderCapsuleManifest(observed);

        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.True(parsed.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(parsed.Diagnostics));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Startup_and_transition_roots_preserve_existing_headroom_after_explicit_owner_mode()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        Assert.True(discovery.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(discovery.Diagnostics));

        HostedProducerAnalysisMetrics metrics = Assert.IsType<HostedProducerAnalysisMetrics>(discovery.AnalysisMetrics);

        const string databaseHost = "GrimoireDatabaseHostedService";

        const string databaseHostOperation = "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.";

        const string ownershipOperation = databaseHostOperation + "OwnsStartedDatabase";

        (string RootType, string Operation)[] transitionRoots =
        [
            ("RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery",
                "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync"),
            ("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService",
                "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFullUnderMaintenanceLockAsync"),
            ("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService",
                "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyUnderMaintenanceLockAsync"),
        ];

        Assert.NotEmpty(ObserveFullProductionRoot(discovery, databaseHost));

        foreach ((string root, string operation) in transitionRoots) Assert.NotEmpty(ObserveFullProductionRoot(discovery, root, operation));

        HostedProducerRootTraversalMetric[] observed = [.. metrics.RootTraversals.Where(root => root.RootType == databaseHost
            || transitionRoots.Contains((root.RootType, root.RootOperation)))];

        HostedProducerRootTraversalMetric ownership = Assert.Single(observed,
            root => root.RootType == databaseHost && root.RootOperation == ownershipOperation);

        (string RootType, string Operation)[] expectedLegacyRoots =
        [
            (databaseHost, databaseHostOperation + "StartAsync"),
            (databaseHost, databaseHostOperation + "StopAsync"),
            (databaseHost, databaseHostOperation + "TakeGeneratedMasterApiKey"),
            .. transitionRoots,
        ];

        Assert.Equal(
            expectedLegacyRoots.OrderBy(root => root.Operation, StringComparer.Ordinal),
            observed.Where(root => root != ownership)
                .Select(root => (root.RootType, Operation: root.RootOperation))
                .OrderBy(root => root.Operation, StringComparer.Ordinal));

        Assert.Null(ownership.SelfAdmittedWorkKind);

        Assert.DoesNotContain(discovery.Items,
            site => site.RootType == databaseHost && site.AuthorityOperationId == ownershipOperation);

        foreach (string line in RenderRootTraversalMetrics(observed)) output.WriteLine(line);

        HeadroomCeiling[] selected = ProductionRootCeilings.Where(row => observed.Any(root =>
            root.RootType == row.RootType && HeadroomRootName(root.RootOperation) == row.RootOperation)).ToArray();

        HeadroomCeiling ownershipCeiling = Assert.Single(selected,
            row => row.RootType == databaseHost && row.RootOperation == ownershipOperation);

        Assert.Equal(24, ownershipCeiling.Ceiling);

        string[] violations = HeadroomViolations(observed, selected,
            HostedGrimoireProducerInventory.MaximumAnalyzedStatesPerRoot);

        Assert.True(violations.Length == 0, string.Join(global::System.Environment.NewLine, violations));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Guild_inbound_peer_reload_roots_keep_the_exact_source_contract()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerSite[] observed = ObserveFullProductionRoot(discovery, "GuildInboundPeerReloadService");

        Assert.True(discovery.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(discovery.Diagnostics));

        string manifest = HostedGrimoireProducerInventory.RenderCapsuleManifest(observed);

        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.True(parsed.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(parsed.Diagnostics));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    [Trait("Category", "HostedProducerAdditionalAnalysis")]
    public void Guild_cancellation_recovery_root_keeps_the_exact_source_contract()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.AdditionalProductionSiteDiscovery;

        Assert.NotEmpty(ObserveFullProductionRoot(discovery,
            "RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler",
            "RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler.RecoverAsync"));

        Assert.True(discovery.Diagnostics.Count == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(discovery.Diagnostics));
    }
}
