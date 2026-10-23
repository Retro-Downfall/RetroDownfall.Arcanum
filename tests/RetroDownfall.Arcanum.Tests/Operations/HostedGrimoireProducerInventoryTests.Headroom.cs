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
    // entry lands on decides whether the closure is admitted for one entry or for both, so its count depends on the arrangement:
    // ApprenticeService.RunApprenticeAsync analysed 3,090 states under one worker, 1,545 under two and 3,090 under three when it was
    // measured (e9eaee35); which worker a family lands on is decided by timing. A row is taken from the largest of the three counts; it is
    // not taken from the one that was measured last.
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

    // A row is the largest count any root-worker arrangement the runner can produce reports for the root (one, two or three workers),
    // plus a margin of five percent with a floor of eight states, rounded up to a multiple of eight. The count of a root that a handoff
    // re-roots into depends on the arrangement: under one worker every entry shares one state cache and the closure is admitted once
    // for each distinct entry, while under two or three workers an entry that lands on another worker admits its own, and which worker
    // a family lands on is decided by timing. So the serial count is the largest, and it is the one a table row must hold. A selection
    // root is named without the digest after its tilde. A row whose comment names a count per arrangement is held at the largest.
    //
    // Measured on 2026-10-17, once under each of one, two and three root workers (the primary discovery reported the committed 6,729
    // manifest rows and no diagnostic under all three), every one of the sixty-nine primary roots equals its maximum plus the margin to
    // the state. The three arrangements agree on every root but the two handoff roots named above, and the largest ceiling is 7,200 of
    // the 7,372 the cap allows (the pre-bootstrap transition recovery, at 6,856 states). The plan of a global or all reset inspects the
    // offline-transition journal and does not recover it, so the two stopped-host apply roots and the attested apply stand at 5,011,
    // 5,864 and 6,266 states; the attested apply is the larger because its shared data step reaches the stopped-host fresh and resume
    // arms' steps as well. The startup root stands at 5,620 states, which includes the outage clause of the healthy-catalog inspection
    // it reaches. The additional partition was measured under each too and stands the same under all three, since none of its roots is
    // re-rooted into.
    // Remeasured at ecddc578 on 2026-10-20 under one, two and three root workers: every discovery reported the same
    // 6,833 primary sites and zero diagnostics. The fifteen source-grown roots below use the maximum across all three
    // arrangements and the unchanged margin rule; the other fifty-four rows remain unchanged.
    // Remeasured at 41206473 on 2026-10-21 under one, two and three root workers: all three primary manifests are
    // byte-identical at 6,859 sites with zero diagnostics. Three source-grown roots change ceilings below; the other
    // sixty-six primary rows retain their prescribed ceilings. Two of those also grow by two states, as noted beside
    // their unchanged rows. The additional partition has 1,544 sites,
    // zero diagnostics and unchanged ceilings for all thirty roots under each arrangement.
    // Remeasured on 2026-10-22 for the Comprehension format extractors (#177) under one, two and three root workers: every
    // discovery reported the committed 6,863 primary sites and zero diagnostics. SessionAttachmentIndexingService.ExecuteAsync
    // grows from 1,226 to 3,066 states in every arrangement, so its row is 3,066 plus the margin; the other sixty-eight primary
    // rows and the thirty additional rows are each their unchanged maximum plus the margin.
    private static readonly HeadroomCeiling[] ProductionRootCeilings =
    [
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.ExecuteAsync", 200), // measured 186
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.Forget", 16), // measured 2
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.Track", 16), // measured 2
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.CancelAsync", 224), // measured 210
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ExecuteAsync", 416), // measured 394
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.InterveneAsync", 240), // measured 224
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.PauseAsync", 216), // measured 201
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ResumeAsync", 232), // measured 215
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ReweaveAsync", 224), // measured 207
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync", 3248), // measured 3092, 1546 and 3092 under one, two and three workers; Session creation delegates to the extracted bound-Session helper; physical database sites are unchanged
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.StartAsync", 240), // measured 223
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.StopAsync", 16), // measured 5
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.SubscribeChronicleAsync", 24), // measured 12
        new("ArcanumSecurityStartupChecks", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSecurityStartupChecks.StartAsync", 16), // measured 3
        new("ArcanumSecurityStartupChecks", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSecurityStartupChecks.StopAsync", 16), // measured 1
        new("ArcanumSettingsClampStartupLogger", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSettingsClampStartupLogger.StartAsync", 16), // measured 7
        new("ArcanumSettingsClampStartupLogger", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSettingsClampStartupLogger.StopAsync", 16), // measured 1
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.ExecuteAsync", 2432), // measured 2310
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.IsBatchInFlight", 16), // measured 1
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.StartAsync", 512), // measured 486
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.StopAsync", 16), // measured 2
        new("CovenantFeatureConfigurationPublisher", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFeatureConfigurationPublisher.StartAsync", 24), // measured 10
        new("CovenantFeatureConfigurationPublisher", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFeatureConfigurationPublisher.StopAsync", 16), // measured 2
        new("CovenantMaintenanceHostedService", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantMaintenanceHostedService.ExecuteAsync", 720), // measured 684, 684 and 684 under one, two and three workers; owner cleanup now owns the capsule mutation barrier and its drain
        new("DataRetentionSweepHostedService", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionSweepHostedService.ExecuteAsync", 2888), // measured 2744, 2744 and 2744 under one, two and three workers; retention prune and purge paths now own capsule mutation barriers
        new("EntryWeavingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.EntryWeavingService.ExecuteAsync", 776), // measured 736
        new("FileEncryptionKeyBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.FileEncryptionKeyBootstrapHostedService.StartAsync", 296), // measured 278
        new("FileEncryptionKeyBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.FileEncryptionKeyBootstrapHostedService.StopAsync", 16), // measured 1
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StartAsync", 5944), // measured 5660, 5660 and 5660 under one, two and three workers; operation expiry inspection now reaches the startup-priority descriptor projection, adding two states within the unchanged prescribed ceiling
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StopAsync", 200), // measured 186
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.TakeGeneratedMasterApiKey", 16), // measured 1
        new("GrimoireSchemaTransitionHostedService", "RetroDownfall.Arcanum.Infrastructure.Covenant.GrimoireSchemaTransitionHostedService.ExecuteAsync", 816), // measured 772
        // The Long Rest roots carry the cost of the Long Rest closure. A seam proves every leaf in one statement, which decodes a snapshot and
        // a label from a joined row (SnapshotFrom, Materialize and the digests a label constructor reaches, analysed in each of the fifty
        // or so contexts that reach VerifyFrozenMetadataAsync) beside the per-leaf read a step makes, and the Lease-Handoff chain, the
        // receiver proof, the capability and fence currency rules, the Pairing Gate, the clique packs and the Fold Check add their own
        // members to the two roots. The capsule mutation barrier adds its owned drain to the Long Rest store paths. Under one,
        // two and three workers the measured maxima are 5514 and 3674 states, so the unchanged margin rule yields
        // 5792 and 3864 (5514 + 276 = 5790 and 3674 + 184 = 3858, each rounded up to a multiple of eight).
        new("LongRestSweepHostedService", "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance.ProcessAdmittedAsync", 5792), // measured 5514, 5514 and 5514 under one, two and three workers; Long Rest store mutation paths now own capsule mutation barriers
        new("LongRestSweepHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService.ExecuteAsync", 3864), // measured 3674, 3674 and 3674 under one, two and three workers; Long Rest store mutation paths now own capsule mutation barriers
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.ContinueInBackgroundAsync", 4488), // measured 4268, 4268 and 4268 under one, two and three workers; generic recovery discovery now applies the startup phase and keyset cursor before its bounded page limit
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync", 16), // measured 3
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.RunStartupPassAsync#0", 1816), // measured 1727, 1727 and 1727 under one, two and three workers; expiry discovery now reaches the startup-priority descriptor projection, adding two states within the unchanged prescribed ceiling
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StopAsync", 16), // measured 5
        new("Loremaster", "RetroDownfall.Arcanum.Infrastructure.Hosting.Loremaster.ExecuteAsync", 4432), // measured 4216
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.RunOrdinaryBootstrapAsync::call:RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#0", 512), // measured 484 under one worker and under three, 242 under two
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StartAsync", 16), // measured 3
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#0", 232), // measured 217
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StopAsync", 72), // measured 57
        new("PidFileService", "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StartAsync", 24), // measured 13
        new("PidFileService", "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StopAsync", 16), // measured 1
        new("ProviderHealthProbeService", "RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthProbeService.ExecuteAsync", 40), // measured 26
        new("RetroDownfall.Arcanum.Cli.Commands.BackupCommands", "RetroDownfall.Arcanum.Cli.Commands.BackupCommands.Create", 1840), // measured 1752, 1752 and 1752 under one, two and three workers; archive path validation now uses the shipped Unicode NFC implementation and bounded identity preflight
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService.RestoreAsync", 3800), // measured 3618, 3618 and 3618 under one, two and three workers; restore reaches shipped Unicode NFC/preflight, destination marker inventory, finalization capacity, authenticated attachment inspection and explicit secret rollback/cache invalidation
        new("RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery", "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync", 7232), // measured 6887, 6887 and 6887 under one, two and three workers; offline erasure recovery reaches the capsule maintenance drain
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFreshUnderMaintenanceLockAsync", 5320), // measured 5063, 5063 and 5063 under one, two and three workers; reset and offline erasure recovery reach the capsule maintenance drain and changed mirrored-credential read closure
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFullUnderMaintenanceLockAsync", 6640), // measured 6318, 6318 and 6318 under one, two and three workers; reset and offline erasure recovery reach the capsule maintenance drain and changed mirrored-credential read closure
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyUnderMaintenanceLockAsync", 6216), // measured 5916, 5916 and 5916 under one, two and three workers; reset and offline erasure recovery reach the capsule maintenance drain and changed mirrored-credential read closure
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.StoppedHostFactoryErasure", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.StoppedHostFactoryErasure.ApplyAsync", 1448), // measured 1375, 1375 and 1375 under one, two and three workers; stopped-host erasure recovery reaches the capsule maintenance drain
        new("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.EnqueueExtraction", 32), // measured 21
        new("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.ExecuteAsync", 1408), // measured 1338, 1338 and 1338 under one, two and three workers; Saga store mutations now own the capsule mutation barrier and its drain
        new("SessionAttachmentIndexingService", "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService.ExecuteAsync", 3224), // measured 3066, 3066 and 3066 under one, two and three workers; the detached parser callback now reaches the seven Comprehension format extractors (#177), whose package, XML and walker members are analysed in it
        new("SessionAttachmentIndexingService", "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService.TryEnqueue", 16), // measured 4
        new("SessionAttachmentPendingGcHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SessionAttachmentPendingGcHostedService.StartAsync", 80), // measured 65
        new("SessionAttachmentPendingGcHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SessionAttachmentPendingGcHostedService.StopAsync", 16), // measured 1
        new("TapestryWeavingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.TapestryWeavingService.ExecuteAsync", 448), // measured 424
        new("UnseenServantService", "RetroDownfall.Arcanum.Infrastructure.Hosting.UnseenServantService.ExecuteAsync", 1168), // measured 1106
        new("UnseenServantService", "RetroDownfall.Arcanum.Infrastructure.Hosting.UnseenServantService.StopAsync", 16), // measured 2
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.ExecuteAsync", 3080), // measured 2926
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.GetRuntimeStatus", 16), // measured 3
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.QueueIndexNow", 1272), // measured 1205
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.RegisterWorkspace", 1168), // measured 1111
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
    // Remeasured at fixed checker 8c5e973f on 2026-10-20: all thirty counts agree with the prior candidate
    // CI worker2 measurement; five source-grown rows change and the other twenty-five remain unchanged.
    private static readonly HeadroomCeiling[] AdditionalRootCeilings =
    [
        new("RetroDownfall.Arcanum.Infrastructure.A2A.A2AInboundSendingRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.A2A.A2AInboundSendingRecoveryHandler.RecoverAsync", 184), // measured 174
        new("RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler.RecoverAsync", 16), // measured 5
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler.RecoverAsync", 40), // measured 31
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex.Add", 40), // measured 25
        new("RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFamilyReinitializeRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFamilyReinitializeRecoveryHandler.RecoverAsync", 32), // measured 18
        new("RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantIndexRebuildRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantIndexRebuildRecoveryHandler.RecoverAsync", 32), // measured 18
        new("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler.RecoverAsync", 3280), // measured 3119; fixed-checker worker1 and prior CI worker2 agree; factory/mutation erasure recovery reaches the capsule maintenance drain
        new("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler.RecoverAsync", 3104), // measured 2953; fixed-checker worker1 and prior CI worker2 agree; factory/mutation erasure recovery reaches the capsule maintenance drain
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler.RecoverAsync", 3280), // measured 3118; fixed-checker worker1 and prior CI worker2 agree; factory/mutation erasure recovery reaches the capsule maintenance drain
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler.RecoverAsync", 3104), // measured 2952; fixed-checker worker1 and prior CI worker2 agree; factory/mutation erasure recovery reaches the capsule maintenance drain
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler.RecoverAsync", 896), // measured 851; fixed-checker worker1 and prior CI worker2 agree; prune recovery reaches the candidate-deletion capsule mutation barriers
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
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore.SameBinding", 16), // measured 1
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore.SamePayload", 24), // measured 16
        new("RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepRecoveryHandler.RecoverAsync", 32), // measured 21
        new("RetroDownfall.Arcanum.Infrastructure.Operations.MemoryEvaluationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.MemoryEvaluationRecoveryHandler.RecoverAsync", 352), // measured 333 under one, two and three workers; validates retained evidence and settles interrupted evaluation arms without dispatch
        new("RetroDownfall.Arcanum.Infrastructure.Operations.SubagentRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.SubagentRecoveryHandler.RecoverAsync", 192), // measured 181
        new("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionKeyRotationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionKeyRotationRecoveryHandler.RecoverAsync", 320), // measured 304
        new("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionMigrationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionMigrationRecoveryHandler.RecoverAsync", 320), // measured 304
    ];

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    [Trait("Category", "HostedProducerAdditionalAnalysis")]
    public void Every_additional_root_stays_within_its_ratchet_ceiling()
    {
        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.AdditionalProductionSiteDiscovery;

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
