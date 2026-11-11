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
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.ExecuteAsync", 224), // measured 208, 208, 208 under one, two and three workers; reviewed short-ledger timeout and command-cleanup closure
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.Forget", 16), // measured 2, 2, 2 under one, two and three workers
        new("A2ASendingLeaseRenewer", "RetroDownfall.Arcanum.Infrastructure.A2A.A2ASendingLeaseRenewer.Track", 16), // measured 2, 2, 2 under one, two and three workers
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.CancelAsync", 240), // measured 221, 221, 221 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ContinueAsync", 328), // measured 311, 311, 311 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ExecuteAsync", 664), // measured 632, 632, 632 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ExecuteGuildAsync", 4408), // measured 4192, 4192, 4192 under one, two and three workers; Guild execution closure includes the exact winning-primary authority binding; the primary manifest adds six authority-site contexts; same-SDK base two-worker observation 4189; exact contextual rows 300 to 306, 6 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (6))
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.GetApprenticeSettings", 16), // measured 3, 3, 3 under one, two and three workers; reviewed authored settings helper reached by the exact local capacity adapter
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.InterveneAsync", 328), // measured 311, 311, 311 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.InterveneOperatorAsync", 328), // measured 311, 311, 311 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.PauseAsync", 224), // measured 212, 212, 212 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ResumeAsync", 320), // measured 303, 303, 303 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ReweaveAsync", 312), // measured 292, 292, 292 under one, two and three workers; reviewed unfinished-plan admission and retained-wait diagnostics with shared digest registration
        new("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync", 1840), // measured 1751, 1751, 1751 under one, two and three workers; tracked, self-admitted Apprentice execution; same-SDK base two-worker observation 1751; exact contextual rows 131 to 131 unchanged
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
        new("DataRetentionSweepHostedService", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionSweepHostedService.ExecuteAsync", 4640), // measured 4414, 4414, 4414 under one, two and three workers; admission-gated hosted prune through ApplyOrResumeHostedPruneAsync; same-SDK base two-worker observation 4420; exact contextual rows 393 to 393 unchanged
        new("EntryWeavingService", "RetroDownfall.Arcanum.Infrastructure.Hosting.EntryWeavingService.ExecuteAsync", 824), // measured 782, 782, 782 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("FileEncryptionKeyBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.FileEncryptionKeyBootstrapHostedService.StartAsync", 296), // measured 278, 278, 278 under one, two and three workers
        new("FileEncryptionKeyBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.FileEncryptionKeyBootstrapHostedService.StopAsync", 16), // measured 1, 1, 1 under one, two and three workers
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StartAsync", 7232), // measured 6884, 6884, 6884 under one, two and three workers; pre-readiness database startup and offline recovery; same-SDK base two-worker observation 6924; exact contextual rows 524 to 527, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StopAsync", 216), // measured 202, 202, 202 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.TakeGeneratedMasterApiKey", 16), // measured 1, 1, 1 under one, two and three workers
        new("GrimoireSchemaTransitionHostedService", "RetroDownfall.Arcanum.Infrastructure.Covenant.GrimoireSchemaTransitionHostedService.ExecuteAsync", 1080), // measured 1022, 1022, 1022 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.Dispose", 16), // measured 2, 2, 2 under one, two and three workers; reviewed durable peer-denial publication, retained reload admission and joined lifecycle
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.ExecuteAsync", 3304), // measured 3142, 3142, 3142 under one, two and three workers; peer reload ExecuteAsync closure includes winning-primary Signet binding checks; three new authority-site contexts are present for this root; same-SDK base two-worker observation 3140; exact contextual rows 211 to 214, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.OnChanged", 1656), // measured 1571, 1571, 1571 under one, two and three workers; peer reload OnChanged closure includes winning-primary Signet binding checks; three new authority-site contexts are present for this root; same-SDK base two-worker observation 1570; exact contextual rows 211 to 214, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.StartAsync", 2336), // measured 2220, 2220, 2220 under one, two and three workers; peer reload StartAsync closure includes winning-primary Signet binding checks; three new authority-site contexts are present for this root; same-SDK base two-worker observation 2218; exact contextual rows 174 to 177, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService.StopAsync", 16), // measured 2, 2, 2 under one, two and three workers; reviewed durable peer-denial publication, retained reload admission and joined lifecycle
        new("GuildLocalHostReadiness", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalHostReadiness.RequireExecutionReady", 56), // measured 48, 48, 48 under one, two and three workers; reviewed bounded retained-lock and committed installation identity check
        new("GuildLocalHostReadiness", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalHostReadiness.StartAsync", 512), // measured 486, 486, 486 under one, two and three workers; reviewed durable installation identity and retained mutation-lock startup
        new("GuildLocalHostReadiness", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalHostReadiness.StopAsync", 24), // measured 9, 9, 9 under one, two and three workers; reviewed fence retirement after tracked scheduler draining
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.DrainCancellationAsync", 4664), // measured 4436, 4436, 4436 under one, two and three workers; local cancellation drain closure includes result capture and award-result Circle publication with current winner authority; same-SDK base two-worker observation 4295; exact contextual rows 324 to 344, 24 added and 4 removed; every removed physical syntax site retained under changed enclosing identity/frame
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.ExecuteAsync", 4472), // measured 4258, 4258, 4258 under one, two and three workers; scheduler recovery closure includes bounded award cancellation and issuer-offer closure checks; same-SDK base two-worker observation 4246; exact contextual rows 306 to 322, 18 added and 2 removed; every removed physical syntax site retained under changed enclosing identity/frame
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunAdmittedAsync", 1064), // measured 1012, 1012, 1012 under one, two and three workers; same-lease local and remote routing; same-SDK base two-worker observation 1011; exact contextual rows 132 to 135, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunClaimedLocalAdmittedAsync", 1072), // measured 1018, 1018, 1018 under one, two and three workers; claimed local dispatch, Signet rebind and port handoff; same-SDK base two-worker observation 1015; exact contextual rows 138 to 141, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunCommittedAsync", 16), // measured 6, 6, 6 under one, two and three workers; reviewed tracked committed dispatch acquiring and handing off its exact admission
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunLocalAdmittedAsync", 1016), // measured 964, 964, 964 under one, two and three workers; local cancellation checks and claimed routing; same-SDK base two-worker observation 963; exact contextual rows 128 to 131, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunLocalCompletionAdmittedAsync", 4312), // measured 4106, 4106, 4106 under one, two and three workers; new directly awaited same-lease completion root retains result, park and child-stop work under the scheduler-owned run; new root absent in base; exact contextual rows 0 to 278, 278 added: formal, authority, result settlement, Circle and owner checks; complete closure reviewed separately
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunLocalPortAdmittedAsync", 5752), // measured 5471, 5471, 5471 under one, two and three workers; scheduled completion was separated into an exact awaited same-lease root; claim and pulse remain in the dispatch lifetime; same-SDK base two-worker observation 7302; exact contextual rows 402 to 423, 25 added and 4 removed; every removed physical syntax site retained under changed enclosing identity/frame
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunRemoteAdmittedAsync", 4360), // measured 4150, 4150, 4150 under one, two and three workers; remote dispatch and cancellation closure includes current winner authority and award-result publication; same-SDK base two-worker observation 4067; exact contextual rows 382 to 402, 24 added and 4 removed; every removed physical syntax site retained under changed enclosing identity/frame
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.Schedule", 80), // measured 65, 65, 65 under one, two and three workers; reviewed local/remote routing and durable cancellation/callback scheduling
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.StartAsync", 616), // measured 586, 586, 586 under one, two and three workers; reviewed local/remote routing and durable cancellation/callback scheduling
        new("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.StopAsync", 16), // measured 1, 1, 1 under one, two and three workers; reviewed wake admission cancellation and joining of tracked dispatch scopes
        new("GuildMaintenanceHostedService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildMaintenanceHostedService.ExecuteAsync", 3608), // measured 3432, 3432, 3432 under one, two and three workers; maintenance loop includes bounded award-primary unavailability reconciliation and issuer-offer closure; same-SDK base two-worker observation 3426; exact contextual rows 318 to 334, 18 added and 2 removed; every removed physical syntax site retained under changed enclosing identity/frame
        new("LongRestSweepHostedService", "RetroDownfall.Arcanum.Api.Intelligence.LongRestMaintenance.ProcessAdmittedAsync", 5744), // measured 5466, 5466, 5466 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("LongRestSweepHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.LongRestSweepHostedService.ExecuteAsync", 3944), // measured 3754, 3754, 3754 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.ContinueInBackgroundAsync", 6768), // measured 6444, 6444, 6444 under one, two and three workers; tracked background recovery startup pass; same-SDK base two-worker observation 6452; exact contextual rows 504 to 504 unchanged
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync", 16), // measured 3, 3, 3 under one, two and three workers
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.RunStartupPassAsync#0", 2928), // measured 2782, 2782, 2782 under one, two and three workers; bounded selected startup reconciliation call; same-SDK base two-worker observation 2786; exact contextual rows 474 to 474 unchanged
        new("LongRunningOperationStartupHostedService", "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StopAsync", 16), // measured 5, 5, 5 under one, two and three workers
        new("Loremaster", "RetroDownfall.Arcanum.Infrastructure.Hosting.Loremaster.ExecuteAsync", 5936), // measured 5648, 5648, 5648 under one, two and three workers; Loremaster hosted execution and live authority checks; same-SDK base two-worker observation 5644; exact contextual rows 312 to 315, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.RunOrdinaryBootstrapAsync::call:RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#0", 520), // measured 494, 494, 247 under one, two and three workers; ordinary MCP global initialization selected call; same-SDK base two-worker observation 494; exact contextual rows 30 to 30 unchanged
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StartAsync", 16), // measured 3, 3, 3 under one, two and three workers
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StartAsync::call:RetroDownfall.Arcanum.Infrastructure.Mcp.IMcpGlobalInitializationCoordinator.InitializeGlobalAsync#0", 240), // measured 222, 222, 222 under one, two and three workers; reviewed shared memory-search service composition and task capability advertisement
        new("McpServerBootstrapHostedService", "RetroDownfall.Arcanum.Infrastructure.Mcp.McpServerBootstrapHostedService.StopAsync", 72), // measured 58, 58, 58 under one, two and three workers; reviewed complete constructor provenance and worker-local shared bootstrap closure
        new("PidFileService", "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StartAsync", 24), // measured 13, 13, 13 under one, two and three workers
        new("PidFileService", "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StopAsync", 16), // measured 1, 1, 1 under one, two and three workers
        new("ProviderHealthProbeService", "RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthProbeService.ExecuteAsync", 40), // measured 26, 26, 26 under one, two and three workers
        new("QuantizedEmbeddingBackfillService", "RetroDownfall.Arcanum.Infrastructure.Hosting.QuantizedEmbeddingBackfillService.ExecuteAsync", 224), // measured 208, 208, 208 under one, two and three workers; reviewed Core37 quantized companion persistence, availability and erasure closure
        new("RetroDownfall.Arcanum.Cli.Commands.BackupCommands", "RetroDownfall.Arcanum.Cli.Commands.BackupCommands.Create", 1888), // measured 1797, 1797, 1797 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService.RestoreAsync", 4544), // measured 4323, 4323, 4323 under one, two and three workers; staged backup restore and recovery closure; same-SDK base two-worker observation 4322; exact contextual rows 991 to 994, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery", "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync", 7344), // measured 6990, 6990, 6990 under one, two and three workers; offline authenticated journal and protected-erasure recovery; same-SDK base two-worker observation 7027; exact contextual rows 430 to 433, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFreshUnderMaintenanceLockAsync", 6040), // measured 5748, 5748, 5748 under one, two and three workers; fresh installation reset under maintenance ownership; same-SDK base two-worker observation 5785; exact contextual rows 562 to 565, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFullUnderMaintenanceLockAsync", 7240), // measured 6894, 6894, 6894 under one, two and three workers; full installation reset under maintenance ownership; same-SDK base two-worker observation 6932; exact contextual rows 625 to 628, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyUnderMaintenanceLockAsync", 6872), // measured 6542, 6542, 6542 under one, two and three workers; scoped installation reset under maintenance ownership; same-SDK base two-worker observation 6608; exact contextual rows 595 to 598, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
        new("RetroDownfall.Arcanum.Infrastructure.InstallationReset.StoppedHostFactoryErasure", "RetroDownfall.Arcanum.Infrastructure.InstallationReset.StoppedHostFactoryErasure.ApplyAsync", 1424), // measured 1351, 1351, 1351 under one, two and three workers; stopped-host factory erasure and journal closure; same-SDK base two-worker observation 1365; exact contextual rows 97 to 97 unchanged
        new("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.EnqueueExtraction", 40), // measured 25, 25, 25 under one, two and three workers; reviewed explicit Guild background-queue refusal
        new("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.ExecuteAsync", 3000), // measured 2850, 2850, 2850 under one, two and three workers; hosted Saga extraction and live authority path; same-SDK base two-worker observation 2838; exact contextual rows 133 to 139, 6 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (6))
        new("SessionAttachmentIndexingService", "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService.ExecuteAsync", 4200), // measured 4000, 4000, 4000 under one, two and three workers; hosted attachment indexing and live authority path; same-SDK base two-worker observation 3996; exact contextual rows 136 to 139, 3 added and none removed (src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Authority.cs (3))
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

    // The additional partition uses the same frozen three-worker observations and margin rule.
    private static readonly HeadroomCeiling[] AdditionalRootCeilings =
    [
        new("RetroDownfall.Arcanum.Infrastructure.A2A.A2AInboundSendingRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.A2A.A2AInboundSendingRecoveryHandler.RecoverAsync", 200), // measured 184, 184, 184 under one, two and three workers; reviewed shared derived-content digest callback registration on admitted connections
        new("RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.A2A.A2AOutboundSendingRecoveryHandler.RecoverAsync", 16), // measured 5, 5, 5 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler.RecoverAsync", 40), // measured 31, 31, 31 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreStagingIndex.Add", 40), // measured 25, 25, 25 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFamilyReinitializeRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantFamilyReinitializeRecoveryHandler.RecoverAsync", 24), // measured 16, 16, 16 under one, two and three workers; reviewed family-reinitialize recovery closure; lower maximum lowers the ratchet
        new("RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantIndexRebuildRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Covenant.CovenantIndexRebuildRecoveryHandler.RecoverAsync", 32), // measured 18, 18, 18 under one, two and three workers
        new("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapFactoryResetRecoveryHandler.RecoverAsync", 3784), // measured 3598, 3598, 3598 under one, two and three workers; factory-reset launch-gap recovery delegating to retained recovery; same-SDK base two-worker observation 3620; complete candidate additional digest matched across three workers; per-root candidate site delta not captured
        new("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.CovenantLaunchGapMutationRecoveryHandler.RecoverAsync", 3544), // measured 3371, 3371, 3371 under one, two and three workers; mutation launch-gap recovery delegating to retained recovery; same-SDK base two-worker observation 3393; complete candidate additional digest matched across three workers; per-root candidate site delta not captured
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionFactoryResetRecoveryHandler.RecoverAsync", 3768), // measured 3584, 3584, 3584 under one, two and three workers; factory-reset retention recovery handler; same-SDK base two-worker observation 3606; complete candidate additional digest matched across three workers; per-root candidate site delta not captured
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionMutationRecoveryHandler.RecoverAsync", 3544), // measured 3370, 3370, 3370 under one, two and three workers; mutation retention recovery handler; same-SDK base two-worker observation 3392; complete candidate additional digest matched across three workers; per-root candidate site delta not captured
        new("RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionRecoveryHandler.RecoverAsync", 1464), // measured 1392, 1392, 1392 under one, two and three workers; prune retention recovery handler; same-SDK base two-worker observation 1395; complete candidate additional digest matched across three workers; per-root candidate site delta not captured
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
        new("RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler.RecoverAsync", 336), // measured 315, 315, 315 under one, two and three workers; Guild Quest recovery observer, which does not launch work; same-SDK base two-worker observation 316; exact contextual rows 32 to 32 unchanged
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
