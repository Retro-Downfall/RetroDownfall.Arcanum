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
    // rounded up to a multiple of eight. Selection-root names omit the trailing digest. The 8,192-state cap is unchanged.
    // SDK 10.0.403; frozen ordered input snapshot SHA-256 values:
    //   pre: 16bb18076dcd18a523f5b0f85911cd2c126c8fc2eb2538deff692787e076f0fc
    //   post: d72771ab1b1fac60e1620af60cf4da5aabbed0a4cd09f02c38d1db7d3333e6ed
    // External input snapshots cover known inputs, not a complete execution trace; SHA-256 values:
    //   pre: db9ef6cede7d1f4363c05789cbc9478a407d382e57152c7b7869010454152751
    //   post: 32d0f0fb4f86130d8eedc69b6611729be7e0217547d2e2e908af35e29abb1ebe
    // Measured test assembly SHA-256: 3a6e9aaade90ab16136460d76da4fd27537a7981e2a60e211bb90acd0b6e8f10.
    // Worker TRX SHA-256 values:
    //   one: 6f30571da3ca01f92ee52d60774509671e8e956863a36bb369e1c2e0b890947d
    //   two: 44d843d3e36476d0c45101a4c2252cb99e06e0efcc713884cdf655b0fba8c0f3
    //   three: d672747550092860729f03e86f9b2925cffde96c2b90088975f4661a0c49c0d2
    // Base-source parity is attribution only; two expected diagnostics reflect the new root absent from base.
    //   primary root metrics SHA-256: f3ac4cb2a7f4ee4d8d7f5b3e26bad43f2e59e174cfd9b5f27eb4343d5e02c417
    //   additional root metrics SHA-256: 1b01aa11725e47e56775d2a2fa9f2a919e65a0978b3134f5a522a7c710e9ee65
    //   base diagnostic report SHA-256: 2b17dc29204150fece2950195eb66d3dd231e4fdbb717f9203b5eba895e4ac91
    // Semantic snapshot comparison excludes only Analyzer AccessedTime; raw snapshots retain that filesystem metadata.
    // Captured primary-only manifest: 11860 rows, 95 roots.
    //   SHA-256: F6B06558E216158EC826A02DC9330B1BA2757B34349205EE6DD770C06C646A78
    // Additional: 2015 matching rows, 31 roots, SHA-256 235C59378A34198A929078DB912394F2A2FAE209D8186EBEB8F86963F3DA0B58, zero diagnostics.
    // All three primary exact-manifest tests passed; additional reports zero diagnostics; largest observed root 6990.
    // These measurements used the primary-only manifest before the combined-manifest test-harness correction.
    // The combined-manifest harness and exact recovery-declaration census change no production behavior or traversal engine.
    // Verify the final combined manifest and source-derived census authority separately after those corrections.
    // Later calibration, test/qualification and blank-line formatting changes require separate verification.
    // These measured hashes do not claim byte identity with the final source or rebuilt assembly.
    private static readonly HeadroomCeiling[] ProductionRootCeilings =
    [
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.ExecuteAsync", 224), // measured 208 and 208 under one and two workers; reviewed short-ledger timeout and command-cleanup closure
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.Forget", 16), // measured 2 and 2 under one and two workers
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.Track", 16), // measured 2 and 2 under one and two workers
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.CancelAsync", 240), // measured 222 and 222 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ContinueAsync", 336), // measured 313 and 313 under one and two workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ExecuteAsync", 672), // measured 638 and 638 under one and two workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ExecuteGuildAsync", 4384), // measured 4173 and 4173 under one and two workers; Guild execution closure includes the exact winning-primary authority binding
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.GetApprenticeSettings", 16), // measured 3 and 3 under one and two workers; reviewed authored settings helper reached by the exact local capacity adapter
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.InterveneAsync", 336), // measured 313 and 313 under one and two workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.InterveneOperatorAsync", 336), // measured 313 and 313 under one and two workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.PauseAsync", 224), // measured 213 and 213 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ResumeAsync", 328), // measured 305 and 305 under one and two workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ReweaveAsync", 312), // measured 294 and 294 under one and two workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync", 1832), // measured 1741 and 1741 under one and two workers; tracked, self-admitted Apprentice execution
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.StartAsync", 336), // measured 319 and 319 under one and two workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.StopAsync", 16), // measured 5 and 5 under one and two workers
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.SubscribeChronicleAsync", 24), // measured 12 and 12 under one and two workers
        new("ArcanumSecurityStartupChecks", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSecurityStartupChecks.StartAsync", 16), // measured 3 and 3 under one and two workers
        new("ArcanumSecurityStartupChecks", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSecurityStartupChecks.StopAsync", 16), // measured 1 and 1 under one and two workers
        new("ArcanumSettingsClampStartupLogger", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSettingsClampStartupLogger.StartAsync", 16), // measured 7 and 7 under one and two workers
        new("ArcanumSettingsClampStartupLogger", "RetroDownfall.Arcanum.Infrastructure.Hosting.ArcanumSettingsClampStartupLogger.StopAsync", 16), // measured 1 and 1 under one and two workers
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.ExecuteAsync", 3040), // measured 2894 and 2894 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.IsBatchInFlight", 16), // measured 1 and 1 under one and two workers
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.StartAsync", 272), // measured 259 and 259 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("BatchProcessingService", "RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.StopAsync", 16), // measured 2 and 2 under one and two workers
        new("CovenantFeatureConfigurationPublisher", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFeatureConfigurationPublisher.StartAsync", 24), // measured 12 and 12 under one and two workers
        new("CovenantFeatureConfigurationPublisher", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFeatureConfigurationPublisher.StopAsync", 16), // measured 2 and 2 under one and two workers
        new("CovenantMaintenanceHostedService", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantMaintenanceHostedService.ExecuteAsync", 776), // measured 736 and 736 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("DataRetentionSweepHostedService", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionSweepHostedService.ExecuteAsync", 4872), // measured 4638 and 4638 under one and two workers; admission-gated hosted prune through ApplyOrResumeHostedPruneAsync
        new("EntryWeavingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.EntryWeavingService.ExecuteAsync", 824), // measured 782 and 782 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("FileEncryptionKeyBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.FileEncryptionKeyBootstrapHostedService.StartAsync", 296), // measured 280 and 280 under one and two workers
        new("FileEncryptionKeyBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.FileEncryptionKeyBootstrapHostedService.StopAsync", 16), // measured 1 and 1 under one and two workers
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StartAsync", 7280), // measured 6930 and 6930 under one and two workers; pre-readiness database startup and offline recovery
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StopAsync", 112), // measured 101 and 101 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.TakeGeneratedMasterApiKey", 16), // measured 1 and 1 under one and two workers
        new("GrimoireSchemaTransitionHostedService", "RetroDownfall.Arcanum.Infrastructure.Covenant.GrimoireSchemaTransitionHostedService.ExecuteAsync", 1080), // measured 1026 and 1026 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.Dispose", 16), // measured 2 and 2 under one and two workers; reviewed durable peer-denial publication, retained reload admission and joined lifecycle
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.ExecuteAsync", 3264), // measured 3104 and 3104 under one and two workers; peer reload ExecuteAsync closure includes winning-primary Signet binding checks
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.OnChanged", 1632), // measured 1552 and 1552 under one and two workers; peer reload OnChanged closure includes winning-primary Signet binding checks
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.StartAsync", 2296), // measured 2186 and 2186 under one and two workers; peer reload StartAsync closure includes winning-primary Signet binding checks
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.StopAsync", 16), // measured 2 and 2 under one and two workers; reviewed durable peer-denial publication, retained reload admission and joined lifecycle
        new("GuildLocalHostReadiness", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalHostReadiness.RequireExecutionReady", 56), // measured 48 and 48 under one and two workers; reviewed bounded retained-lock and committed installation identity check
        new("GuildLocalHostReadiness", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalHostReadiness.StartAsync", 504), // measured 474 and 474 under one and two workers; reviewed durable installation identity and retained mutation-lock startup
        new("GuildLocalHostReadiness", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalHostReadiness.StopAsync", 24), // measured 16 and 16 under one and two workers; reviewed fence retirement after tracked scheduler draining
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.DrainCancellationAsync", 4680), // measured 4454 and 4454 under one and two workers; local cancellation drain closure includes result capture and award-result Circle publication with current winner authority
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.ExecuteAsync", 4544), // measured 4324 and 4324 under one and two workers; scheduler recovery closure includes bounded award cancellation and issuer-offer closure checks
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunAdmittedAsync", 1064), // measured 1012 and 1012 under one and two workers; same-lease local and remote routing
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunClaimedLocalAdmittedAsync", 1072), // measured 1018 and 1018 under one and two workers; claimed local dispatch, Signet rebind and port handoff
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunCommittedAsync", 16), // measured 6 and 6 under one and two workers; reviewed tracked committed dispatch acquiring and handing off its exact admission
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunLocalAdmittedAsync", 1016), // measured 964 and 964 under one and two workers; local cancellation checks and claimed routing
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunLocalCompletionAdmittedAsync", 4336), // measured 4122 and 4122 under one and two workers; new directly awaited same-lease completion root retains result, park and child-stop work under the scheduler-owned run
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunLocalPortAdmittedAsync", 5768), // measured 5486 and 5486 under one and two workers; scheduled completion was separated into an exact awaited same-lease root
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunRemoteAdmittedAsync", 4384), // measured 4170 and 4170 under one and two workers; remote dispatch and cancellation closure includes current winner authority and award-result publication
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.Schedule", 80), // measured 66 and 66 under one and two workers; reviewed local/remote routing and durable cancellation/callback scheduling
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.StartAsync", 608), // measured 574 and 574 under one and two workers; reviewed local/remote routing and durable cancellation/callback scheduling
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.StopAsync", 16), // measured 1 and 1 under one and two workers; reviewed wake admission cancellation and joining of tracked dispatch scopes
        new("GuildMaintenanceHostedService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildMaintenanceHostedService.ExecuteAsync", 5176), // measured 4926 and 4926 under one and two workers
        new("LongRestSweepHostedService", "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance.ProcessAdmittedAsync", 5744), // measured 5467 and 5467 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("LongRestSweepHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService.ExecuteAsync", 3936), // measured 3744 and 3744 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.ContinueInBackgroundAsync", 6216), // measured 5920 and 5920 under one and two workers; tracked background recovery startup pass
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync", 16), // measured 3 and 3 under one and two workers
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.RunStartupPassAsync#0", 4736), // measured 4509 and 4509 under one and two workers
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StopAsync", 16), // measured 5 and 5 under one and two workers
        new("Loremaster", "RetroDownfall.Arcanum.Infrastructure.Hosting.Loremaster.ExecuteAsync", 6888), // measured 6556 and 6556 under one and two workers
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.RunOrdinaryBootstrapAsync::call:RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#0", 528), // measured 496 and 248 under one and two workers; ordinary MCP global initialization selected call
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StartAsync", 16), // measured 3 and 3 under one and two workers
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#0", 240), // measured 223 and 223 under one and two workers; reviewed shared memory-search service composition and task capability advertisement
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StopAsync", 72), // measured 58 and 58 under one and two workers; reviewed complete constructor provenance and worker-local shared bootstrap closure
        new("PidFileService", "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StartAsync", 24), // measured 13 and 13 under one and two workers
        new("PidFileService", "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StopAsync", 16), // measured 1 and 1 under one and two workers
        new("ProviderHealthProbeService", "RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthProbeService.ExecuteAsync", 40), // measured 26 and 26 under one and two workers
        new("QuantizedEmbeddingBackfillService", "RetroDownfall.Arcanum.Infrastructure.Hosting.QuantizedEmbeddingBackfillService.ExecuteAsync", 224), // measured 208 and 208 under one and two workers; reviewed Core37 quantized companion persistence, availability and erasure closure
        new("RetroDownfall.Arcanum.Cli.Commands.BackupCommands", "RetroDownfall.Arcanum.Cli.Commands.BackupCommands.Create", 1888), // measured 1798 and 1798 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService.RestoreAsync", 4568), // measured 4343 and 4343 under one and two workers; staged backup restore and recovery closure
        new("RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery", "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync", 7280), // measured 6929 and 6929 under one and two workers; offline authenticated journal and protected-erasure recovery
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFreshUnderMaintenanceLockAsync", 6088), // measured 5793 and 5793 under one and two workers; fresh installation reset under maintenance ownership
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFullUnderMaintenanceLockAsync", 7288), // measured 6938 and 6938 under one and two workers; full installation reset under maintenance ownership
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyUnderMaintenanceLockAsync", 6920), // measured 6587 and 6587 under one and two workers; scoped installation reset under maintenance ownership
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.StoppedHostFactoryErasure", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.StoppedHostFactoryErasure.ApplyAsync", 1424), // measured 1355 and 1355 under one and two workers; stopped-host factory erasure and journal closure
        new("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.EnqueueExtraction", 40), // measured 25 and 25 under one and two workers; reviewed explicit Guild background-queue refusal
        new("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.ExecuteAsync", 5544), // measured 5280 and 5280 under one and two workers
        new("SessionAttachmentIndexingService", "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService.ExecuteAsync", 5128), // measured 4878 and 4878 under one and two workers
        new("SessionAttachmentIndexingService", "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService.TryEnqueue", 16), // measured 5 and 5 under one and two workers
        new("SessionAttachmentPendingGcHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SessionAttachmentPendingGcHostedService.StartAsync", 80), // measured 68 and 68 under one and two workers
        new("SessionAttachmentPendingGcHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SessionAttachmentPendingGcHostedService.StopAsync", 16), // measured 1 and 1 under one and two workers
        new("TapestryWeavingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.TapestryWeavingService.ExecuteAsync", 472), // measured 448 and 448 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("UnseenServantService", "RetroDownfall.Arcanum.Infrastructure.Hosting.UnseenServantService.ExecuteAsync", 1184), // measured 1122 and 1122 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("UnseenServantService", "RetroDownfall.Arcanum.Infrastructure.Hosting.UnseenServantService.StopAsync", 16), // measured 2 and 2 under one and two workers
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.ExecuteAsync", 3128), // measured 2976 and 2976 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.GetRuntimeStatus", 16), // measured 3 and 3 under one and two workers
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.QueueIndexNow", 1296), // measured 1233 and 1233 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.RegisterWorkspace", 1200), // measured 1139 and 1139 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.ResolveIndexedWorkspacePath", 16), // measured 3 and 3 under one and two workers
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.StopAsync", 16), // measured 8 and 8 under one and two workers
        new("WorkspaceIndexingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.WorkspaceIndexingService.UnregisterWorkspace", 24), // measured 13 and 13 under one and two workers
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

    // The additional partition uses the same frozen three-worker observations and margin rule.
    private static readonly HeadroomCeiling[] AdditionalRootCeilings =
    [
        new("RetroDownfall.Arcanum.Infrastructure.A2A.A2AInboundSendingRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.A2A.A2AInboundSendingRecoveryHandler.RecoverAsync", 200), // measured 185 and 185 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler.RecoverAsync", 16), // measured 5 and 5 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler.RecoverAsync", 40), // measured 31 and 31 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex.Add", 40), // measured 25 and 25 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFamilyReinitializeRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFamilyReinitializeRecoveryHandler.RecoverAsync", 24), // measured 16 and 16 under one and two workers; reviewed family-reinitialize recovery closure
        new("RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantIndexRebuildRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantIndexRebuildRecoveryHandler.RecoverAsync", 32), // measured 18 and 18 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler.RecoverAsync", 3872), // measured 3684 and 3684 under one and two workers; factory-reset launch-gap recovery delegating to retained recovery
        new("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler.RecoverAsync", 3528), // measured 3360 and 3360 under one and two workers; mutation launch-gap recovery delegating to retained recovery
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler.RecoverAsync", 3856), // measured 3667 and 3667 under one and two workers; factory-reset retention recovery handler
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler.RecoverAsync", 3528), // measured 3359 and 3359 under one and two workers; mutation retention recovery handler
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler.RecoverAsync", 1528), // measured 1453 and 1453 under one and two workers; prune retention recovery handler
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_AllObjects", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CanonicalSchemaFingerprint", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CoreObjects", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CoreSchemaFingerprint", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantAcceleratorObjects", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantAcceleratorSchemaFingerprint", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantCanonicalObjects", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_CovenantCanonicalSchemaFingerprint", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.get_TransitionStatements", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_Core", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_CovenantAccelerator", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests", "RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaManifests.get_CovenantCanonical", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler.RecoverAsync", 336), // measured 315 and 315 under one and two workers; Guild Quest recovery observer, which does not launch work
        new("RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendRefreshRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendRefreshRecoveryHandler.RecoverAsync", 2096), // measured 1989 and 1989 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore.SameBinding", 16), // measured 1 and 1 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetActiveStore.SamePayload", 24), // measured 16 and 16 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepRecoveryHandler.RecoverAsync", 32), // measured 21 and 21 under one and two workers
        new("RetroDownfall.Arcanum.Infrastructure.Operations.MemoryEvaluationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.MemoryEvaluationRecoveryHandler.RecoverAsync", 368), // measured 347 and 347 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Operations.SubagentRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Operations.SubagentRecoveryHandler.RecoverAsync", 208), // measured 191 and 191 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionKeyRotationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionKeyRotationRecoveryHandler.RecoverAsync", 392), // measured 366 and 366 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionMigrationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Storage.BlobEncryptionMigrationRecoveryHandler.RecoverAsync", 392), // measured 366 and 366 under one and two workers; reviewed shared derived-content digest callback registration on admitted connections
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
