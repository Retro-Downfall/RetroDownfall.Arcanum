using System.Reflection;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The Headroom Tripwire. The checker refuses a root that analyses more than the per-root state cap, and a root
    // refused for its size reports nothing about the sites below the cap, so a root is never allowed to drift toward it
    // unnoticed. Every primary production root has a committed ceiling: what it analyses plus a small stated margin,
    // and never more than ninety percent of the cap. The cap is not raised. A ceiling follows the measured count of its root: a change
    // that makes a root cheaper lowers its row in the same change, and a change that makes one dearer raises it only after the change has
    // earned the room, only to the rule below, never past the ninety percent bound, and with the reason beside the row.
    //
    // A ceiling is meant to be the maximum, over one, two and three root workers, of what the root analyses, plus the margin, so a
    // discovery under any of the three stays at or under it, CI's two included (the production lanes run two). A root a handoff
    // re-roots into is entered once from its own declaration and once from the traversal that hands off to it, and which worker each
    // entry lands on decides whether the closure is admitted for one entry or for both, so its count depends on the arrangement.
    // Which worker a family lands on is decided by timing. A row is taken from the largest of the three counts, never just the last run.
    private const int HeadroomPercentOfCap = 90;

    private readonly record struct HeadroomCeiling(string RootType, string RootOperation, int Ceiling);

    // The selection roots carry a digest of the selected source after a tilde; the capsule manifest already pins that
    // digest, so the ratchet names a root by the part before it.
    private static string HeadroomRootName(string rootOperation)
    {
        int digest = rootOperation.LastIndexOf('~');

        return digest > 0 && rootOperation.Length - digest - 1 == 64 ? rootOperation[..digest] : rootOperation;
    }

    private static string[] HeadroomViolations(IReadOnlyList<HostedProducerRootTraversalMetric> roots, IReadOnlyList<HeadroomCeiling> ceilings, int cap)
    {
        List<string> violations = [];

        int ninetyPercent = cap * HeadroomPercentOfCap / 100;

        Dictionary<(string RootType, string RootOperation), int> table = [];

        foreach (HeadroomCeiling row in ceilings)
        {
            if (!table.TryAdd((row.RootType, row.RootOperation), row.Ceiling))
            {
                violations.Add("The ceiling table names " + row.RootType + " " + row.RootOperation + " more than once.");
            }

            if (row.Ceiling < 1 || row.Ceiling > ninetyPercent)
            {
                violations.Add("The ceiling of " + row.RootType + " " + row.RootOperation + " is " + row.Ceiling + ", outside 1 to " + ninetyPercent + " (" + HeadroomPercentOfCap + "% of the " + cap + "-state cap).");
            }
        }

        HashSet<(string RootType, string RootOperation)> measured = [];

        foreach (HostedProducerRootTraversalMetric root in roots)
        {
            (string RootType, string RootOperation) key = (root.RootType, HeadroomRootName(root.RootOperation));

            if (!measured.Add(key))
            {
                violations.Add("The discovery reports " + key.RootType + " " + key.RootOperation + " more than once.");

                continue;
            }

            if (!table.TryGetValue(key, out int ceiling))
            {
                violations.Add("The root " + key.RootType + " " + key.RootOperation + " analysed " + root.AnalyzedStates + " states and has no ceiling row.");
            }
            else if (root.AnalyzedStates > ceiling)
            {
                violations.Add("The root " + key.RootType + " " + key.RootOperation + " analysed " + root.AnalyzedStates + " states, above its ceiling of " + ceiling + ".");
            }
        }

        foreach ((string RootType, string RootOperation) row in table.Keys)
        {
            if (!measured.Contains(row))
            {
                violations.Add("The ceiling row " + row.RootType + " " + row.RootOperation + " names no root of the discovery.");
            }
        }

        return [.. violations.Order(StringComparer.Ordinal)];
    }

    // A row is the maximum measured under one, two and three root workers, plus max(8, ceil(maximum * 0.05)),
    // rounded up to a multiple of eight. Selection roots omit the digest after the tilde. Hard/model/effect limits are unchanged.
    // Reviewed untrusted-data production source snapshot: 555259a43bf2d38c89736097ab8511a0a71794ccdcd63dfb33de2e778f23142c.
    // The checker and hard/model/effect limits are unchanged. The digest covers sorted source/vendor/MSBuild input paths and bytes.
    // One-, two- and three-worker discoveries report zero primary/additional diagnostics and the same
    // 7,896-capsule primary manifest. Primary manifest comparison and additional headroom passed in each arrangement.
    // All 73 primary and 31 additional roots were measured in each arrangement; final compiled ceiling metadata was checked
    // against all three captured measurements after adding the two newly catalogued finite guidance roots.
    // Every ceiling is prescribed by the maximum of its three measured counts. The largest maximum is 6,850
    // (ceiling 7,200), below the unchanged 7,372 bound of the 8,192-state cap.
    // RunApprenticeAsync reaches 3,176 (ceiling 3,336), with the two-worker check observing 1,588;
    // ordinary MCP bootstrap reaches 486 (ceiling 512). Changed rows state all three counts and the reviewed closure.
    private static readonly HeadroomCeiling[] ProductionRootCeilings =
    [
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.ExecuteAsync", 224), // measured 208, 208, 208 under one, two and three workers; reviewed short-ledger timeout and command-cleanup closure
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.Forget", 16), // measured 2
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.Track", 16), // measured 2
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.CancelAsync", 232), // measured 214, 214, 214 under one, two and three workers; reviewed Apprentice control, callback and bound-Session closure
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ContinueAsync", 248), // measured 229, 229, 229 under one, two and three workers; reviewed awaited LegacyUnknown guidance checkpoint persistence under caller admission
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ExecuteAsync", 424), // measured 402, 402, 402 under one, two and three workers; reviewed Apprentice control, callback and bound-Session closure
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.InterveneAsync", 248), // measured 229, 229, 229 under one, two and three workers; reviewed explicit await of shared provenance-aware checkpoint persistence
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.InterveneOperatorAsync", 248), // measured 229, 229, 229 under one, two and three workers; reviewed host-context validation and awaited Operator checkpoint persistence under caller admission
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.PauseAsync", 216), // measured 205, 205, 205 under one, two and three workers; prescribed ceiling unchanged
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ResumeAsync", 232), // measured 219, 219, 219 under one, two and three workers; prescribed ceiling unchanged
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ReweaveAsync", 224), // measured 211, 211, 211 under one, two and three workers; prescribed ceiling unchanged
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync", 3336), // measured 3176, 1588, 3176 under one, two and three workers; reviewed provenance-preserving prompt and guidance-consumption closure
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.StartAsync", 240), // measured 227, 227, 227 under one, two and three workers; prescribed ceiling unchanged
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.StopAsync", 16), // measured 5
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.SubscribeChronicleAsync", 24), // measured 12
        new("ArcanumSecurityStartupChecks", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSecurityStartupChecks.StartAsync", 16), // measured 3
        new("ArcanumSecurityStartupChecks", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSecurityStartupChecks.StopAsync", 16), // measured 1
        new("ArcanumSettingsClampStartupLogger", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSettingsClampStartupLogger.StartAsync", 16), // measured 7
        new("ArcanumSettingsClampStartupLogger", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSettingsClampStartupLogger.StopAsync", 16), // measured 1
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.ExecuteAsync", 3056), // measured 2910, 2910, 2910 under one, two and three workers; reviewed batch JSONL, accounting recovery and owned-writer cleanup closure
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.IsBatchInFlight", 16), // measured 1
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.StartAsync", 528), // measured 500, 500, 500 under one, two and three workers; reviewed batch JSONL, accounting recovery and owned-writer cleanup closure
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.StopAsync", 16), // measured 2
        new("CovenantFeatureConfigurationPublisher", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFeatureConfigurationPublisher.StartAsync", 24), // measured 12, 12, 12 under one, two and three workers; prescribed ceiling unchanged
        new("CovenantFeatureConfigurationPublisher", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFeatureConfigurationPublisher.StopAsync", 16), // measured 2
        new("CovenantMaintenanceHostedService", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantMaintenanceHostedService.ExecuteAsync", 768), // measured 724, 724, 724 under one, two and three workers; reviewed family maintenance and connection-state callback closure
        new("DataRetentionSweepHostedService", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionSweepHostedService.ExecuteAsync", 4600), // measured 4380, 4380, 4380 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("EntryWeavingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.EntryWeavingService.ExecuteAsync", 816), // measured 770, 770, 770 under one, two and three workers; reviewed Core37 quantized companion persistence, availability and erasure closure
        new("FileEncryptionKeyBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.FileEncryptionKeyBootstrapHostedService.StartAsync", 296), // measured 278
        new("FileEncryptionKeyBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.FileEncryptionKeyBootstrapHostedService.StopAsync", 16), // measured 1
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StartAsync", 6440), // measured 6132, 6132, 6132 under one, two and three workers; reviewed Quest schema upgrade and integrity closure
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StopAsync", 208), // measured 192, 192, 192 under one, two and three workers; reviewed physical-connection shutdown and connection-state callback closure
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.TakeGeneratedMasterApiKey", 16), // measured 1
        new("GrimoireSchemaTransitionHostedService", "RetroDownfall.Arcanum.Infrastructure.Covenant.GrimoireSchemaTransitionHostedService.ExecuteAsync", 992), // measured 944, 944, 944 under one, two and three workers; reviewed Quest schema upgrade and integrity closure
        new("GuildMaintenanceHostedService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildMaintenanceHostedService.ExecuteAsync", 1912), // measured 1818, 1818, 1818 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        // Both Long Rest roots retain snapshot/label decoding, lease handoff, receiver and fence proofs, pairing/clique/fold
        // checks and owned capsule-mutation drains. The complete measured maxima are 5,456 and 3,744 across all arrangements.
        // The unchanged margin rule gives 5,456 + 273 = 5,729 -> 5,736 and 3,744 + 188 = 3,932 -> 3,936.
        new("LongRestSweepHostedService", "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance.ProcessAdmittedAsync", 5736), // measured 5456, 5456, 5456 under one, two and three workers; reviewed Guild Signet schema, owner-cleanup and retained-evidence closure after the corrected maximum-17 proof; cheaper closure lowers the ratchet
        new("LongRestSweepHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService.ExecuteAsync", 3936), // measured 3744, 3744, 3744 under one, two and three workers; reviewed Guild Signet schema, owner-cleanup and retained-evidence closure after the corrected maximum-17 proof; cheaper closure lowers the ratchet
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.ContinueInBackgroundAsync", 6416), // measured 6108, 6108, 6108 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync", 16), // measured 3
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.RunStartupPassAsync#0", 2752), // measured 2615, 2615, 2615 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StopAsync", 16), // measured 5
        new("Loremaster", "RetroDownfall.Arcanum.Infrastructure.Hosting.Loremaster.ExecuteAsync", 5080), // measured 4832, 4832, 4832 under one, two and three workers; reviewed Guild Signet schema, owner-cleanup and retained-evidence closure after the corrected maximum-17 proof; cheaper closure lowers the ratchet
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.RunOrdinaryBootstrapAsync::call:RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#0", 512), // measured 486, 486, 486 under one, two and three workers; maximum and prescribed ceiling retained across worker arrangements
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StartAsync", 16), // measured 3
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#0", 232), // measured 218, 218, 218 under one, two and three workers; prescribed ceiling unchanged
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StopAsync", 72), // measured 57
        new("PidFileService", "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StartAsync", 24), // measured 13
        new("PidFileService", "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StopAsync", 16), // measured 1
        new("ProviderHealthProbeService", "RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthProbeService.ExecuteAsync", 40), // measured 26
        new("QuantizedEmbeddingBackfillService", "RetroDownfall.Arcanum.Infrastructure.Hosting.QuantizedEmbeddingBackfillService.ExecuteAsync", 224), // measured 208, 208, 208 under one, two and three workers; reviewed Core37 quantized companion persistence, availability and erasure closure
        new("RetroDownfall.Arcanum.Cli.Commands.BackupCommands", "RetroDownfall.Arcanum.Cli.Commands.BackupCommands.Create", 1888), // measured 1792, 1792, 1792 under one, two and three workers; reviewed explicit operation identity and checkpoint admission
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService.RestoreAsync", 4032), // measured 3840, 3840, 3840 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery", "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync", 7200), // measured 6850, 6850, 6850 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFreshUnderMaintenanceLockAsync", 5752), // measured 5477, 5477, 5477 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFullUnderMaintenanceLockAsync", 6968), // measured 6631, 6631, 6631 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyUnderMaintenanceLockAsync", 6648), // measured 6330, 6330, 6330 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.StoppedHostFactoryErasure", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.StoppedHostFactoryErasure.ApplyAsync", 1480), // measured 1408, 1408, 1408 under one, two and three workers; reviewed Quest retention, owner-cleanup and reset closure
        new("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.EnqueueExtraction", 32), // measured 21
        new("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.ExecuteAsync", 1440), // measured 1368, 1368, 1368 under one, two and three workers; reviewed Core37 quantized companion persistence, availability and erasure closure
        new("SessionAttachmentIndexingService", "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService.ExecuteAsync", 3392), // measured 3228, 3228, 3228 under one, two and three workers; reviewed Core37 quantized companion persistence, availability and erasure closure
        new("SessionAttachmentIndexingService", "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService.TryEnqueue", 16), // measured 5, 5, 5 under one, two and three workers; prescribed ceiling unchanged
        new("SessionAttachmentPendingGcHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SessionAttachmentPendingGcHostedService.StartAsync", 80), // measured 68, 68, 68 under one, two and three workers; prescribed ceiling unchanged
        new("SessionAttachmentPendingGcHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SessionAttachmentPendingGcHostedService.StopAsync", 16), // measured 1
        new("TapestryWeavingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.TapestryWeavingService.ExecuteAsync", 448), // measured 424, 424, 424 under one, two and three workers; reviewed canonical BLOB Tapestry closure
        new("UnseenServantService", "RetroDownfall.Arcanum.Infrastructure.Hosting.UnseenServantService.ExecuteAsync", 1192), // measured 1130, 1130, 1130 under one, two and three workers; reviewed maintenance worker and completion-owned cleanup closure
        new("UnseenServantService", "RetroDownfall.Arcanum.Infrastructure.Hosting.UnseenServantService.StopAsync", 16), // measured 2
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.ExecuteAsync", 3136), // measured 2982, 2982, 2982 under one, two and three workers; reviewed Core37 quantized companion persistence, availability and erasure closure
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.GetRuntimeStatus", 16), // measured 3
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.QueueIndexNow", 1296), // measured 1231, 1231, 1231 under one, two and three workers; reviewed Core37 quantized companion persistence, availability and erasure closure
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.RegisterWorkspace", 1200), // measured 1137, 1137, 1137 under one, two and three workers; reviewed Core37 quantized companion persistence, availability and erasure closure
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.ResolveIndexedWorkspacePath", 16), // measured 3
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.StopAsync", 16), // measured 8
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.UnregisterWorkspace", 24), // measured 13
    ];

    // The roots the shared production discovery analysed against the table above. It reads the discovery the production lane
    // already computes once per process and never starts a second one.
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Every_production_root_stays_within_its_ratchet_ceiling()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerAnalysisMetrics metrics = Assert.IsType<HostedProducerAnalysisMetrics>(discovery.AnalysisMetrics);

        foreach (string line in RenderRootTraversalMetrics(metrics.RootTraversals)) output.WriteLine(line);

        string[] violations = HeadroomViolations(metrics.RootTraversals, ProductionRootCeilings, HostedGrimoireProducerInventory.MaximumAnalyzedStatesPerRoot);

        Assert.True(violations.Length == 0, string.Join(global::System.Environment.NewLine, violations));
    }

    // The additional partition holds the recovery handlers and the other non-hosted chains. It is the same ratchet over the same
    // kind of discovery: the shared lazy `AdditionalProductionSiteDiscovery` that the other additional-partition tests already
    // read, never a second one. Its roots do not depend on the arrangement of root workers (no handoff re-roots into one), so a
    // row is the one count plus the same margin.
    // All thirty-one counts agree under one, two and three workers on the reviewed input tree named above.
    private static readonly HeadroomCeiling[] AdditionalRootCeilings =
    [
        new("RetroDownfall.Arcanum.Infrastructure.A2A.A2AInboundSendingRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.A2A.A2AInboundSendingRecoveryHandler.RecoverAsync", 192), // measured 178, 178, 178 under one, two and three workers; reviewed inbound sending-recovery collaborator closure
        new("RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler.RecoverAsync", 16), // measured 5
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler.RecoverAsync", 40), // measured 31
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex.Add", 40), // measured 25
        new("RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFamilyReinitializeRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFamilyReinitializeRecoveryHandler.RecoverAsync", 24), // measured 16, 16, 16 under one, two and three workers; reviewed family-reinitialize recovery closure; lower maximum lowers the ratchet
        new("RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantIndexRebuildRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantIndexRebuildRecoveryHandler.RecoverAsync", 32), // measured 18
        new("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler.RecoverAsync", 3416), // measured 3249, 3249, 3249 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler.RecoverAsync", 3184), // measured 3026, 3026, 3026 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler.RecoverAsync", 3400), // measured 3236, 3236, 3236 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler.RecoverAsync", 3184), // measured 3025, 3025, 3025 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler.RecoverAsync", 1456), // measured 1384, 1384, 1384 under one, two and three workers; reviewed durable Portal retention, owner-cleanup, source-event and reset closure
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_AllObjects", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CanonicalSchemaFingerprint", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CoreObjects", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CoreSchemaFingerprint", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantAcceleratorObjects", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantAcceleratorSchemaFingerprint", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantCanonicalObjects", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantCanonicalSchemaFingerprint", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_TransitionStatements", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_Core", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_CovenantAccelerator", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_CovenantCanonical", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler.RecoverAsync", 192), // measured 177, 177, 177 under one, two and three workers; reviewed content-free Quest recovery under paired recovery Work and Effect
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore.SameBinding", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore.SamePayload", 24), // measured 16
        new("RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepRecoveryHandler.RecoverAsync", 32), // measured 21
        new("RetroDownfall.Arcanum.Infrastructure.Operations.MemoryEvaluationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.MemoryEvaluationRecoveryHandler.RecoverAsync", 360), // measured 340, 340, 340 under one, two and three workers; reviewed retained-evidence validation and interrupted-evaluation ledger closure
        new("RetroDownfall.Arcanum.Infrastructure.Operations.SubagentRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.SubagentRecoveryHandler.RecoverAsync", 200), // measured 185, 185, 185 under one, two and three workers; reviewed subagent recovery and terminal collaborator closure
        new("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionKeyRotationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionKeyRotationRecoveryHandler.RecoverAsync", 384), // measured 359, 359, 359 under one, two and three workers; reviewed blob inventory, ownership, lifecycle and short-ledger cleanup closure
        new("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionMigrationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionMigrationRecoveryHandler.RecoverAsync", 384), // measured 359, 359, 359 under one, two and three workers; reviewed blob inventory, ownership, lifecycle and short-ledger cleanup closure
    ];

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    [Trait("Category", "HostedProducerAdditionalAnalysis")]
    public void Every_additional_root_stays_within_its_ratchet_ceiling()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.AdditionalProductionSiteDiscovery;

        Assert.Empty(discovery.Diagnostics);

        string manifest = HostedGrimoireProducerInventory.RenderCapsuleManifest(discovery.Items);

        string manifestDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest)));

        output.WriteLine($"HOSTED_ADDITIONAL_MANIFEST\tsites={discovery.Items.Count}\tsha256={manifestDigest}\tdiagnostics={discovery.Diagnostics.Count}");

        HostedProducerAnalysisMetrics metrics = Assert.IsType<HostedProducerAnalysisMetrics>(discovery.AnalysisMetrics);

        foreach (string line in RenderRootTraversalMetrics(metrics.RootTraversals)) output.WriteLine(line);

        string[] violations = HeadroomViolations(metrics.RootTraversals, AdditionalRootCeilings, HostedGrimoireProducerInventory.MaximumAnalyzedStatesPerRoot);

        Assert.True(violations.Length == 0, string.Join(global::System.Environment.NewLine, violations));
    }

    private static HostedProducerRootTraversalMetric HeadroomRoot(string rootType, string rootOperation, int states) =>
        new(rootType, rootOperation, 1, states, TimeSpan.Zero);

    [Fact]
    public void The_headroom_ratchet_accepts_a_table_that_names_every_root_within_its_ceiling()
    {
        HostedProducerRootTraversalMetric[] roots = [HeadroomRoot("A", "A.Run", 100), HeadroomRoot("B", "B.Run~" + new string('F', 64), 7)];

        HeadroomCeiling[] table = [new("A", "A.Run", 100), new("B", "B.Run", 8)];

        Assert.Empty(HeadroomViolations(roots, table, 8192));
    }

    [Fact]
    public void The_headroom_ratchet_refuses_a_root_missing_from_the_table()
    {
        HostedProducerRootTraversalMetric[] roots = [HeadroomRoot("A", "A.Run", 100), HeadroomRoot("B", "B.Run", 7)];

        string violation = Assert.Single(HeadroomViolations(roots, [new("A", "A.Run", 120)], 8192));

        Assert.Contains("B B.Run", violation, StringComparison.Ordinal);

        Assert.Contains("no ceiling row", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_headroom_ratchet_refuses_a_table_row_with_no_root()
    {
        HostedProducerRootTraversalMetric[] roots = [HeadroomRoot("A", "A.Run", 100)];

        string violation = Assert.Single(HeadroomViolations(roots, [new("A", "A.Run", 120), new("Gone", "Gone.Run", 50)], 8192));

        Assert.Contains("Gone Gone.Run", violation, StringComparison.Ordinal);

        Assert.Contains("names no root", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_headroom_ratchet_refuses_a_root_above_its_ceiling()
    {
        HostedProducerRootTraversalMetric[] roots = [HeadroomRoot("A", "A.Run", 121)];

        string violation = Assert.Single(HeadroomViolations(roots, [new("A", "A.Run", 120)], 8192));

        Assert.Contains("analysed 121 states, above its ceiling of 120", violation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(7372, true)]
    [InlineData(7373, false)]
    [InlineData(8192, false)]
    [InlineData(0, false)]
    public void The_headroom_ratchet_holds_every_ceiling_to_ninety_percent_of_the_cap(int ceiling, bool accepted)
    {
        HostedProducerRootTraversalMetric[] roots = [HeadroomRoot("A", "A.Run", 100)];

        string[] violations = HeadroomViolations(roots, [new("A", "A.Run", ceiling)], 8192);

        Assert.Equal(accepted, violations.Length == 0);
    }

    [Fact]
    public void The_headroom_ratchet_refuses_a_duplicated_row_and_a_duplicated_root()
    {
        Assert.Contains("more than once", Assert.Single(HeadroomViolations([HeadroomRoot("A", "A.Run", 1)], [new("A", "A.Run", 9), new("A", "A.Run", 9)], 8192)), StringComparison.Ordinal);

        Assert.Contains("more than once", Assert.Single(HeadroomViolations([HeadroomRoot("A", "A.Run", 1), HeadroomRoot("A", "A.Run", 1)], [new("A", "A.Run", 9)], 8192)), StringComparison.Ordinal);
    }

    // The budgets that bound the production traversal are fixed by this pin, and the defaults the production entry passes
    // are read from the entry's own signature, so a changed default cannot hide behind an unchanged constant.
    [Fact]
    public void LimitsAreFrozen()
    {
        Type inventory = typeof(HostedGrimoireProducerInventory);

        BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static int Constant(Type owner, string name, BindingFlags flags) =>
            (int)Assert.IsType<int>((owner.GetField(name, flags) ?? throw new InvalidOperationException(owner.Name + "." + name + " is missing.")).GetRawConstantValue());

        Assert.Equal(8192, Constant(inventory, "DefaultMaximumAnalyzedStatesPerRoot", all));

        Assert.Equal(4096, Constant(inventory, "DefaultEvaluationEnvironmentMaximumMembers", all));

        Assert.Equal(64, Constant(inventory, "DefaultEvaluationEnvironmentMaximumDepth", all));

        Assert.Equal(128, Constant(inventory, "DefaultTraversalMaximumDepth", all));

        Assert.Equal(8192, HostedGrimoireProducerInventory.MaximumAnalyzedStatesPerRoot);

        Type graph = inventory.GetNestedType("ProducerGraph", BindingFlags.NonPublic) ?? throw new InvalidOperationException("ProducerGraph is missing.");

        foreach (string work in new[] { "CleanupProvenanceContext", "RecoveryTupleProvenanceContext", "EnumerationProof" })
        {
            Type owner = graph.GetNestedType(work, BindingFlags.NonPublic) ?? throw new InvalidOperationException(work + " is missing.");

            Assert.Equal(256, Constant(owner, "MaximumWork", all));
        }

        // The callable-mutation query keeps its own budget, a constant of the graph itself.
        Assert.Equal(256, Constant(graph, "MaxCallableWork", all));

        MethodInfo entry = Assert.Single(inventory.GetMethods(all), static method => method.Name == "DiscoverProducerSitesUnderPremises");

        Dictionary<string, object?> defaults = entry.GetParameters().Where(static parameter => parameter.HasDefaultValue).ToDictionary(static parameter => parameter.Name!, static parameter => parameter.RawDefaultValue);

        Assert.Equal(4096, defaults["evaluationEnvironmentMaximumMembers"]);

        Assert.Equal(64, defaults["evaluationEnvironmentMaximumDepth"]);

        Assert.Equal(128, defaults["traversalMaximumDepth"]);

        Assert.Equal(8192, defaults["maximumAnalyzedStatesPerRoot"]);
    }
}
