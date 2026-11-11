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
    // All six complete production observations used frozen pre-calibration input tree eb18e1d464cb1924756f8e5c6d0e5be1cca73d06.
    // Source/vendor/build inventory: ec280bfd9113e7eb0e685ed3d0c4beda9b14dffb123082b3bf96352922f7cddf.
    // Evaluated compiler inputs (5,659): 8ed88b7889d1d89869bfde7c2ec0493ef858ec68312c7c9a81c2190c48e7ef4b.
    // Checker/test source inventory: d08c508e448b4e631e0dd216948a3fbcc0d122eb90b01da3d6a526ae67659d36.
    // Release runtime inventory (119 files): 8e17a185b77ac0d590b7e743a69d98e5350fb2bb7a0724c835a6b2d154c32f0c.
    // Compiled test assembly: 7681f75b3cdc8c0204b56155c5dabc77fe11ab4a9b34a4d479ca88c0be3c039f.
    // Each inventory hashes sorted paths and their content digests; before/after inventories were byte-identical.
    // Primary discovery, strict parsing, root ownership and generated-catalog checks reported zero diagnostics under all three
    // arrangements, with 10,500 identical canonical rows over 91 roots. Additional discovery reported zero diagnostics and
    // 1,972 identical canonical rows over 31 roots. Their manifest hashes are respectively
    // 42b10e1559f4890e270192c353689b8f74d7c97fdca49e7a2a0aad27e2551d84 and
    // f1045ccb02d91d66093f5e1d96fbc20086f52779c8a8bc4fc5a53a4dbba3cc56.
    // The maximum is 7,010 states; its rounded five-percent ceiling is 7,368, below the unchanged 7,372 bound of the 8,192 cap.
    // Apprentice execution and ordinary MCP initialization vary by worker placement; every row uses the largest observed count.
    // This post-capture calibration changes ceiling data and evidence comments. Its compiled verification and final binary
    // provenance are recorded separately; the captured assembly above is the pre-calibration assembly, not a byte-identity claim.
    // Guild tool-surface requalification observed all 91 primary roots on the same source under one, two and three workers.
    // All 31 additional roots were observed on those same inputs; both canonical manifest digests agree across all three.
    // Authored input inventory: 718f01ddb3fa6d240cc3dcf04bb9843227dcb1d3e257c4535225467b76772101.
    // Evaluated compiler inputs: 66d1ce925c22f21924796f9fbd5af47314dec9b423b3f7316a57f5f7529eded1.
    // Frozen test runtime: 5522503251a7c40dffbc7b82a284189ed5dbab206933f315becfaf86d78ea066.
    // The C# discovery engine is unchanged. This calibration changes only measured row data and provenance after capture.
    private static readonly HeadroomCeiling[] ProductionRootCeilings =
    [
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.ExecuteAsync", 224), // measured 208, 208, 208 under one, two and three workers; reviewed short-ledger timeout and command-cleanup closure
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.Forget", 16), // measured 2, 2, 2 under one, two and three workers
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.Track", 16), // measured 2, 2, 2 under one, two and three workers
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.CancelAsync", 240), // measured 221, 221, 221 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ContinueAsync", 328), // measured 311, 311, 311 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ExecuteAsync", 664), // measured 632, 632, 632 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ExecuteGuildAsync", 4312), // measured 4105, 4105, 4105 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.GetApprenticeSettings", 16), // measured 3, 3, 3 under one, two and three workers; reviewed authored settings helper reached by the exact local capacity adapter
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.InterveneAsync", 328), // measured 311, 311, 311 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.InterveneOperatorAsync", 328), // measured 311, 311, 311 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.PauseAsync", 224), // measured 212, 212, 212 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ResumeAsync", 320), // measured 303, 303, 303 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ReweaveAsync", 312), // measured 292, 292, 292 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync", 3680), // measured 3502, 3502, 1751 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.StartAsync", 336), // measured 317, 317, 317 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.StopAsync", 16), // measured 5, 5, 5 under one, two and three workers
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.SubscribeChronicleAsync", 24), // measured 12, 12, 12 under one, two and three workers
        new("ArcanumSecurityStartupChecks", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSecurityStartupChecks.StartAsync", 16), // measured 3, 3, 3 under one, two and three workers
        new("ArcanumSecurityStartupChecks", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSecurityStartupChecks.StopAsync", 16), // measured 1, 1, 1 under one, two and three workers
        new("ArcanumSettingsClampStartupLogger", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSettingsClampStartupLogger.StartAsync", 16), // measured 7, 7, 7 under one, two and three workers
        new("ArcanumSettingsClampStartupLogger", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSettingsClampStartupLogger.StopAsync", 16), // measured 1, 1, 1 under one, two and three workers
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.ExecuteAsync", 3080), // measured 2928, 2928, 2928 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.IsBatchInFlight", 16), // measured 1, 1, 1 under one, two and three workers
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.StartAsync", 544), // measured 518, 518, 518 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.StopAsync", 16), // measured 2, 2, 2 under one, two and three workers
        new("CovenantFeatureConfigurationPublisher", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFeatureConfigurationPublisher.StartAsync", 24), // measured 12, 12, 12 under one, two and three workers; prescribed ceiling unchanged
        new("CovenantFeatureConfigurationPublisher", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFeatureConfigurationPublisher.StopAsync", 16), // measured 2, 2, 2 under one, two and three workers
        new("CovenantMaintenanceHostedService", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantMaintenanceHostedService.ExecuteAsync", 776), // measured 736, 736, 736 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("DataRetentionSweepHostedService", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionSweepHostedService.ExecuteAsync", 4648), // measured 4420, 4420, 4420 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("EntryWeavingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.EntryWeavingService.ExecuteAsync", 824), // measured 782, 782, 782 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("FileEncryptionKeyBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.FileEncryptionKeyBootstrapHostedService.StartAsync", 296), // measured 278, 278, 278 under one, two and three workers
        new("FileEncryptionKeyBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.FileEncryptionKeyBootstrapHostedService.StopAsync", 16), // measured 1, 1, 1 under one, two and three workers
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StartAsync", 7248), // measured 6902, 6902, 6902 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StopAsync", 216), // measured 202, 202, 202 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.TakeGeneratedMasterApiKey", 16), // measured 1, 1, 1 under one, two and three workers
        new("GrimoireSchemaTransitionHostedService", "RetroDownfall.Arcanum.Infrastructure.Covenant.GrimoireSchemaTransitionHostedService.ExecuteAsync", 1080), // measured 1022, 1022, 1022 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.Dispose", 16), // measured 2, 2, 2 under one, two and three workers; reviewed durable peer-denial publication, retained reload admission and joined lifecycle
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.ExecuteAsync", 3248), // measured 3088, 3088, 3088 under one, two and three workers; reviewed retained-wait diagnostic projection and shared digest registration
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.OnChanged", 1624), // measured 1544, 1544, 1544 under one, two and three workers; reviewed retained-wait diagnostic projection and shared digest registration
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.StartAsync", 2288), // measured 2178, 2178, 2178 under one, two and three workers; reviewed retained-wait diagnostic projection and shared digest registration
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.StopAsync", 16), // measured 2, 2, 2 under one, two and three workers; reviewed durable peer-denial publication, retained reload admission and joined lifecycle
        new("GuildLocalHostReadiness", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalHostReadiness.RequireExecutionReady", 56), // measured 48, 48, 48 under one, two and three workers; reviewed bounded retained-lock and committed installation identity check
        new("GuildLocalHostReadiness", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalHostReadiness.StartAsync", 512), // measured 486, 486, 486 under one, two and three workers; reviewed durable installation identity and retained mutation-lock startup
        new("GuildLocalHostReadiness", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalHostReadiness.StopAsync", 24), // measured 9, 9, 9 under one, two and three workers; reviewed fence retirement after tracked scheduler draining
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.DrainCancellationAsync", 4512), // measured 4295 under one worker; reviewed Cohort recovery and cleanup-only cancellation closure
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.ExecuteAsync", 4464), // measured 4246 under one worker; reviewed Cohort recovery and cleanup-only cancellation closure
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunAdmittedAsync", 1064), // measured 1011 under one worker; reviewed exact lease handoff to local and remote work
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunCommittedAsync", 16), // measured 6, 6, 6 under one, two and three workers; reviewed tracked committed dispatch acquiring and handing off its exact admission
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunLocalAdmittedAsync", 1016), // measured 963 under one worker; reviewed cancellation routing and claimed dispatch handoff
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunClaimedLocalAdmittedAsync", 1072), // measured 1015 under one worker; reviewed claim, rebind and exact port handoff
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunLocalPortAdmittedAsync", 7372), // measured 7302 under one worker; 70 states (0.96%) below this ceiling and 890 below the unchanged 8192 global cap
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunRemoteAdmittedAsync", 4272), // measured 4067 under one worker; reviewed Cohort recovery and cleanup-only cancellation closure
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.Schedule", 80), // measured 65, 65, 65 under one, two and three workers; reviewed local/remote routing and durable cancellation/callback scheduling
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.StartAsync", 616), // measured 586, 586, 586 under one, two and three workers; reviewed local/remote routing and durable cancellation/callback scheduling
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.StopAsync", 16), // measured 1, 1, 1 under one, two and three workers; reviewed wake admission cancellation and joining of tracked dispatch scopes
        new("GuildMaintenanceHostedService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildMaintenanceHostedService.ExecuteAsync", 3600), // measured 3426 under one worker; reviewed Cohort owner cleanup, retained contribution validation and bounded settlement closure
        new("LongRestSweepHostedService", "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance.ProcessAdmittedAsync", 5744), // measured 5466, 5466, 5466 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("LongRestSweepHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService.ExecuteAsync", 3944), // measured 3754, 3754, 3754 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.ContinueInBackgroundAsync", 6776), // measured 6452, 3226, 6452 under one, two and three workers; reviewed legacy Quest recovery diagnosis and shared digest registration
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync", 16), // measured 3, 3, 3 under one, two and three workers
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.RunStartupPassAsync#0", 2928), // measured 2786, 2786, 2786 under one, two and three workers; reviewed legacy Quest recovery diagnosis and shared digest registration
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StopAsync", 16), // measured 5, 5, 5 under one, two and three workers
        new("Loremaster", "RetroDownfall.Arcanum.Infrastructure.Hosting.Loremaster.ExecuteAsync", 5904), // measured 5622, 5622, 5622 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.RunOrdinaryBootstrapAsync::call:RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#0", 520), // measured 494, 494, 494 under one, two and three workers; reviewed shared memory-search service composition and task capability advertisement
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StartAsync", 16), // measured 3, 3, 3 under one, two and three workers
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#0", 240), // measured 222, 222, 222 under one, two and three workers; reviewed shared memory-search service composition and task capability advertisement
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StopAsync", 72), // measured 58, 58, 58 under one, two and three workers; reviewed complete constructor provenance and worker-local shared bootstrap closure
        new("PidFileService", "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StartAsync", 24), // measured 13, 13, 13 under one, two and three workers
        new("PidFileService", "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StopAsync", 16), // measured 1, 1, 1 under one, two and three workers
        new("ProviderHealthProbeService", "RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthProbeService.ExecuteAsync", 40), // measured 26, 26, 26 under one, two and three workers
        new("QuantizedEmbeddingBackfillService", "RetroDownfall.Arcanum.Infrastructure.Hosting.QuantizedEmbeddingBackfillService.ExecuteAsync", 224), // measured 208, 208, 208 under one, two and three workers; reviewed Core37 quantized companion persistence, availability and erasure closure
        new("RetroDownfall.Arcanum.Cli.Commands.BackupCommands", "RetroDownfall.Arcanum.Cli.Commands.BackupCommands.Create", 1888), // measured 1797, 1797, 1797 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService.RestoreAsync", 4528), // measured 4311, 4311, 4311 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery", "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync", 7368), // measured 7016, 7016, 7016 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFreshUnderMaintenanceLockAsync", 6064), // measured 5774, 5774, 5774 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFullUnderMaintenanceLockAsync", 7272), // measured 6921, 6921, 6921 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyUnderMaintenanceLockAsync", 6928), // measured 6597, 6597, 6597 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.StoppedHostFactoryErasure", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.StoppedHostFactoryErasure.ApplyAsync", 1440), // measured 1365, 1365, 1365 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.EnqueueExtraction", 40), // measured 25, 25, 25 under one, two and three workers; reviewed explicit Guild background-queue refusal
        new("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.ExecuteAsync", 2928), // measured 2788, 2788, 2788 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("SessionAttachmentIndexingService", "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService.ExecuteAsync", 4176), // measured 3974, 3974, 3974 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("SessionAttachmentIndexingService", "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService.TryEnqueue", 16), // measured 5, 5, 5 under one, two and three workers; prescribed ceiling unchanged
        new("SessionAttachmentPendingGcHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SessionAttachmentPendingGcHostedService.StartAsync", 80), // measured 68, 68, 68 under one, two and three workers; prescribed ceiling unchanged
        new("SessionAttachmentPendingGcHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SessionAttachmentPendingGcHostedService.StopAsync", 16), // measured 1, 1, 1 under one, two and three workers
        new("TapestryWeavingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.TapestryWeavingService.ExecuteAsync", 472), // measured 448, 448, 448 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("UnseenServantService", "RetroDownfall.Arcanum.Infrastructure.Hosting.UnseenServantService.ExecuteAsync", 1200), // measured 1142, 1142, 1142 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("UnseenServantService", "RetroDownfall.Arcanum.Infrastructure.Hosting.UnseenServantService.StopAsync", 16), // measured 2, 2, 2 under one, two and three workers
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.ExecuteAsync", 3144), // measured 2994, 2994, 2994 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.GetRuntimeStatus", 16), // measured 3, 3, 3 under one, two and three workers
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.QueueIndexNow", 1304), // measured 1237, 1237, 1237 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.RegisterWorkspace", 1208), // measured 1143, 1143, 1143 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.ResolveIndexedWorkspacePath", 16), // measured 3, 3, 3 under one, two and three workers
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.StopAsync", 16), // measured 8, 8, 8 under one, two and three workers
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.UnregisterWorkspace", 24), // measured 13, 13, 13 under one, two and three workers
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

    // The additional partition uses the same frozen one/two/three-worker observations and margin rule.
    // All 31 additional root counts agree across those arrangements; these are complete additional discoveries.
    private static readonly HeadroomCeiling[] AdditionalRootCeilings =
    [
        new("RetroDownfall.Arcanum.Infrastructure.A2A.A2AInboundSendingRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.A2A.A2AInboundSendingRecoveryHandler.RecoverAsync", 200), // measured 184, 184, 184 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler.RecoverAsync", 16), // measured 5, 5, 5 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler.RecoverAsync", 40), // measured 31, 31, 31 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex.Add", 40), // measured 25, 25, 25 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFamilyReinitializeRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFamilyReinitializeRecoveryHandler.RecoverAsync", 24), // measured 16, 16, 16 under one, two and three workers; reviewed family-reinitialize recovery closure; lower maximum lowers the ratchet
        new("RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantIndexRebuildRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantIndexRebuildRecoveryHandler.RecoverAsync", 32), // measured 18, 18, 18 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler.RecoverAsync", 3792), // measured 3609, 3609, 3609 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler.RecoverAsync", 3552), // measured 3382, 3382, 3382 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler.RecoverAsync", 3776), // measured 3595, 3595, 3595 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler.RecoverAsync", 3552), // measured 3381, 3381, 3381 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler.RecoverAsync", 1472), // measured 1395, 1395, 1395 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_AllObjects", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CanonicalSchemaFingerprint", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CoreObjects", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CoreSchemaFingerprint", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantAcceleratorObjects", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantAcceleratorSchemaFingerprint", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantCanonicalObjects", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantCanonicalSchemaFingerprint", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_TransitionStatements", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_Core", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_CovenantAccelerator", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_CovenantCanonical", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler.RecoverAsync", 336), // measured 316, 316, 316 under one, two and three workers; reviewed exact finite-request recovery root with bounded retained-wait diagnosis
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore.SameBinding", 16), // measured 1, 1, 1 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore.SamePayload", 24), // measured 16, 16, 16 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepRecoveryHandler.RecoverAsync", 32), // measured 21, 21, 21 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Operations.MemoryEvaluationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.MemoryEvaluationRecoveryHandler.RecoverAsync", 368), // measured 347, 347, 347 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Operations.SubagentRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.SubagentRecoveryHandler.RecoverAsync", 208), // measured 191, 191, 191 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionKeyRotationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionKeyRotationRecoveryHandler.RecoverAsync", 384), // measured 365, 365, 365 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionMigrationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionMigrationRecoveryHandler.RecoverAsync", 384), // measured 365, 365, 365 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
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
