# Arcanum Whole-Tree Review Remediation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remediate every finding carried into the 2026-10-04 whole-tree review register (8 Blocking, 70 Important, 331 Minor) with test-first changes, then qualify and merge to `main`.

**Architecture:** The spec is the review document; its register gives every finding a row `R-nnn` with locations, the defect, a concrete fix, the single failing test that pins it, the document to update and any design conflict. This plan groups rows into tasks by subsystem and shared files, in four phases: safety rails for the test suite, the eight Blocking rows, the Important rows with their subsystem's Minor rows, then build/CI, tests and docs. Each task is TDD per row: write the row's failing test, see it fail, make the minimal fix, see it pass, update the row's document, commit.

**Tech Stack:** .NET 10 Native AOT, ASP.NET Core minimal APIs with source-generated JSON and request delegates, EF Core 10 (compiled model, SaveChanges and transactions only) with parameterized direct SQLite on SQLCipher, xUnit 2.9 with hand-written fakes.

**Spec:** `docs/superpowers/specs/2026-10-04-arcanum-whole-tree-review.md` (the register is the authority; a task's text lists row ids and the implementer reads those rows).

## Global Constraints

- `AGENTS.md` is binding: Native AOT discipline (no reflection `JsonSerializer`, every `/api` payload on `ArcanumJsonContext`, `{ get; set; }` on `Arcanum:` config POCOs, EF only for compiled model/change tracking/SaveChanges/transactions, every read/filter/aggregate and set-based write is parameterized direct SQLite, `SqliteNativeRuntime.Instance.Initialize()` before any open), API-first (`Cli` stays a thin HTTP client), `Result`/`Result<T>` flow, file-scoped namespaces, positional records for DTOs, no `[JsonPropertyName]` on `/api` wire types, primary constructors for DI.
- Blank-line rule (AGENTS.md rule 4) on every file you touch: run `./scripts/align-csharp-blanklines.sh --compact-delimiters <each changed .cs file>` before committing. Do not run it repo-wide.
- Naming: plain descriptive names for anything new; do not invent thematic names (user waiver of AGENTS.md rule 5).
- TDD per row: the register's "Failing test" is written first and must be red for the right reason before the fix; run it with `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~<TestClass>"`. Never run the full suite, `./scripts/coverage.sh`, or any `verify-*.sh` script; the controller runs each once at the end.
- Docs travel with code: update the row's "Docs" target in the same commit (`docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`, `docs/Compendium.README.md`). Edit `README.md` only where a row names it. Never touch `docs/Arcanum.Review.*.md`, `docs/Arcanum.ConstraintReduction.*.md`, `docs/ArcanumOATH.Human.md` or `docs/Arcanum.OATH.Human.md` (the owner has uncommitted changes to those in the primary checkout).
- Point of no return: after a commit, rename, publish or external effect, compensating and bookkeeping work runs on `CancellationToken.None` (or a bounded token), never the caller's token.
- Windows-only behaviour: implement through a seam so a macOS-runnable test pins the logic; add the Windows-lane test with `Skip.IfNot(OperatingSystem.IsWindows())`; say in the report that it was not run here.
- Git: one or more commits per task, message ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`; never `git stash`, never switch branches, never touch other worktrees.
- Tests must never touch the developer's real `~/.config/arcanum`; Task 1 installs the guard and every later test must pass under it.

## Review Focus

Inputs the spec implies but no single row's test exercises, most likely to bite first:
1. A tool call inside a streaming turn on a Campaign-bound session with Covenant enabled: every ambient non-null, memory writes land in Campaign scope (R-008 test must cover both buffered and streaming modes).
2. A workspace cloned from a hostile repository containing a symlink chain, a `.git/hooks` write target and a `.tmp.<guid>` sibling of a protected record (R-007, R-014, R-018 tests use real filesystem fixtures, not resolver seams).
3. A macOS tool child that over-allocates memory while the limiter is configured (R-006 real-shell test must assert either enforcement or an explicit "unenforced" report).
4. Cancellation landing one instruction after a durable commit in every destructive path touched (R-001, R-002, R-027, R-103, R-204 tests cancel from the step after the commit, not before).
5. A `dotnet test` run from a shell whose environment is not `Testing` (R-078 guard must fail the run, not write the real profile).

---

## Rulings

Decisions made on the owner's behalf where the spec and governed docs conflict; each is recorded in the SDD ledger and repeated in the final report.
- R-010: add the labelled-artifact guard to Campaign-targeted resets and change DESIGN :2529.
- R-026: run the content-free ledger reads when the Covenant flag is off and change DESIGN :2419/:2437.
- R-075: correct DESIGN :508 (build/CI/test-time proof); do not wire the validator.
- R-328: remove the `key set <value>` positional; DESIGN :179/:4144 win over Command Reference :464.
- R-329: keep `session compact` unconfirmed; document the exemption.
- R-122 (rebuild driver), R-376 (CI gate on publish), R-398 (analyzer project split): parked; R-121 and R-128 are fixed.
- R-196: document which workers honour maintenance revocation; no code change.
- R-375: compact-delimiters only on touched files; repo-wide run left to the owner.
- R-095: no change (V19 rejected hardening `ArcanumPaths`); keep the pinning test green.
- Windows-only rows (R-004, R-005, R-042, R-043, R-235, R-253): seam + macOS test + Windows-lane test not run here.

---

## Task template

Every task below follows the same steps for each register row it lists. The task text names the rows and anything the rows cannot know (ordering, shared helpers, hazards from neighbouring rows). The implementer reads the rows from the spec.

For each row, in the order listed:
- [ ] **Step A: Write the row's failing test** exactly as the row's "Failing test" names it (class, method, assertion); a row that says "none (docs)" or "none (deletion)" skips to Step C.
- [ ] **Step B: Run it and confirm it fails for the reason the row describes** (`dotnet test ... --filter "FullyQualifiedName~<Class>"`).
- [ ] **Step C: Implement the row's "Fix"** (and the hazards it names) with the smallest change.
- [ ] **Step D: Run the test class again; it passes, and the other tests in that class still pass.**
- [ ] **Step E: Update the row's "Docs" target** in the same change.
After the task's rows:
- [ ] **Step F: Run `./scripts/align-csharp-blanklines.sh --compact-delimiters` on every changed `.cs` file; re-run the touched test classes once.**
- [ ] **Step G: Commit** (`git add <files>`; message `fix(<subsystem>): <summary> (R-xxx, R-yyy)`).

---

## Phase 0 — Safety rails

### Task 1: Test-home isolation guard (R-078, R-393, R-391 scoped)

**Files:** `tests/RetroDownfall.Arcanum.Tests/Support/ArcanumTestHomeScope.cs` (lift from `tests/RetroDownfall.Compendium.Tests/ArcanumTestHomeScope.cs`), `tests/.../Cli/CliTestHarness.cs`, `tests/.../Cli/ConfigurationCommandServiceTests.cs`, `tests/.../Configuration/ConfigurationBootstrapperTests.cs`, `tests/.../InstallationReset/StoppedHostGrimoireConnectionAuthorityTests.cs`, `tests/.../Collections/EnvironmentIsolationContractTests.cs`, `tests/.../Cli/MemoryStatusCovenantCommandTests.cs`.
**Rows:** R-078 (reconciled fix: shared scope for the three escaping classes; `CliTestHarness.RunAsync` precondition that opens a harness-owned scope only when the ambient home is unredirected; guard predicate compares against the exact real `<profile>/.config/arcanum` and `<ApplicationData>/arcanum` directories, never a profile prefix), R-393, and from R-391 only the deletion of nothing (defer the module initializer; record that in the report).
**Interfaces:** Produces `ArcanumTestHomeScope` (`IDisposable`, sets `HOME`, `USERPROFILE`, `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT`, `ARCANUM_TEST_HOME`; restores all) under `RetroDownfall.Arcanum.Tests.Support`; every later task may use it.
**Order:** R-078 contract test (IL walker resolving `ldstr`) first, then the harness test `CliHarnessIsolationTests.Name_resolved_resource_selection_stays_inside_the_test_home`, then `TestHomeGuardTests`, then R-393.

## Phase 1 — Blocking

### Task 2: Re-establish turn ambients per tool call (R-008)

**Files:** `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `ToolExecutionPipeline.cs`, `SessionAttachmentToolInjection.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryScopeResolver.cs`, `Core/Intelligence/AttachmentMemoryProvenance.cs`, tests `tests/.../Intelligence/WizardIntelligenceProviderTests.cs` and a new `WizardIntelligenceProviderAmbientTests.cs`.
**Rows:** R-008 (one per-turn ambient set applied inside a non-yielding helper immediately before each post-yield tool call, extending the existing V-2 re-set; `End()`/`Snapshot()` on captured objects; Lexicon/Saga scope resolvers fail closed when Campaign scoping is on and no session resolves; do not call `AttachmentMemoryGateAmbient.BeginTurn` twice per tool call).
**Tests first:** `StreamingAndBufferedToolCall_ObservesAllTurnAmbients` (both modes), a Wizard-level `retire_covenant`/`propose_covenant` model-tool-call test, `StreamingToolCall_ScribeLexicon_InCampaignBoundSession_WritesCampaignScope`.
**Model:** most capable (8,157-line file; concurrency semantics).

### Task 3: Canonical symlink containment (R-007, R-147, R-163, R-254)

**Files:** `Infrastructure/Security/WorkspacePathPolicy.cs`, `Infrastructure/Mcp/SandboxedFileIo.cs`, `Infrastructure/Security/SecureFileReader.cs`, `Infrastructure/Workspaces/PhysicalFileSystemBrowser.cs`, `PhysicalFileSystemWriter.cs`, `Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.FileTools.cs`, tests `tests/.../Mcp/WorkspacePathPolicySymlinkTests.cs`, `SandboxedFileIoTests.cs`, `PhysicalFileSystemBrowserContainmentTests.cs`, `tests/.../Security/WardGateTests.cs`.
**Rows:** R-007 (canonicalise with a per-component re-walk, depth cap 40, canonicalise the root too, kernel-path handle revalidation; convert the seam-based tests to real-filesystem fixtures and leave one code path), R-147 and R-163 (the `..`-prefix bug class), R-254 (drop the WardGate sleeps).
**Tests first:** the three R-007 tests with real links plus the green control `_PureLinkChain_Rejects`.
**Model:** most capable (security boundary with 97 call sites).

### Task 4: Factory erasure leaves the lease heartbeat at launch publication (R-001, R-108)

**Files:** `Infrastructure/Data/DataRetentionService.FactoryActivation.cs`, `DataRetentionService.cs` (remarks at 6987-6989), tests `tests/.../Data/CovenantErasureSameProcessTests.cs`.
**Rows:** R-001 (move the coordinator call and its result mapping out of the maintainer lambda; keep the maintained segment and the `DataRetentionLeaseLostException` catch over it; caller token for the coordinator as the direct arm does), R-108.
**Model:** most capable.

### Task 5: Installation-reset opening rollback and orphan temporaries (R-002, R-018, R-157)

**Files:** `Infrastructure/InstallationReset/InstallationResetActiveStore.cs`, `InstallationResetActiveAnchorStore.cs`, `InstallationResetActiveRecordKeyProvider.cs`, tests `tests/.../InstallationReset/InstallationResetActiveStoreTests.cs`, `InstallationResetStartupRecoveryTests.cs`.
**Rows:** R-002 (a `RetireUnpublishedOpening` equivalent under a checkpoint token; never relax `RecoverCoreAsync`; rewrite the test that pins the wedge), R-018 (exact-name temporary cleanup under the held lock, recovery entry points only), R-157.
**Model:** most capable.

### Task 6: Covenant management lease ownership (R-003, R-176, R-177)

**Files:** `Infrastructure/Covenant/CovenantManagementService.cs`, `Core/Covenant/CovenantSearchContracts.cs`, tests `tests/.../Covenant/CovenantOperatorJourneyTests.cs`, `CovenantManagementServiceTests.cs`.
**Rows:** R-003 (ownership handoff with `transferred` flag; never dispose on the success path), R-176 (validate list/version cursors or delete the validator; choose validate), R-177.
**Model:** standard.

### Task 7: Child-process resource limits fail closed (R-006, R-234)

**Files:** `Infrastructure/Platform/ProcessResourceLimiter.cs`, `Infrastructure/Security/SanctumGuard.cs`, `Core/Sanctum/SanctumConfig.cs`, tests `tests/.../Platform/ProcessResourceLimiterTests.cs`, `ProcessRunnerResourceLimitTests.cs`.
**Rows:** R-006 in the order the row gives: (b) drop `ulimit -v` on macOS and state memory is unenforced there (DESIGN :3492/:3498) or enforce RSS another way, then (a) `|| exit 126` fail-closed clauses mapped to a `ResourceLimitError`; R-234 folded in (one effective-memory helper).
**Model:** most capable (shell semantics, platform behaviour).

### Task 8: Windows AppContainer broker (R-004, R-005, R-043, R-235, R-042)

**Files:** `Infrastructure/Process/ChildProcessSandboxRoots.cs`, `WindowsAppContainerLauncher.cs`, `ChildProcessFilesystemJail.cs`, `WindowsAppContainerRestoreJournal.cs`, new `WindowsBrokerTargetResolver.cs`, tests `tests/.../Process/ChildProcessSandboxRootsTests.cs`, `WindowsBrokerTargetResolverTests.cs`, `WindowsAppContainerBrokerTests.cs` (Windows lane).
**Rows:** R-004 (OS-conditional system roots via an `os` seam; drop non-existent roots on every OS), R-005 (pure resolver over PATH/PATHEXT/file-exists; `.cmd`/`.bat` refused or wrapped; user-profile executables need their directory added to the read-execute roots), R-043 (Windows-lane smoke; correct DESIGN :3826/:3998 until it runs), R-235 (specific-job verification, distinct exit-71 message), R-042 (restore by removing this run's SID ACEs from a fresh DACL; journal the SID).
**Note:** nothing Windows-only can run here; macOS tests pin the pure logic; report that the Windows-lane tests were added but not executed.
**Model:** most capable.

## Phase 2 — Important rows with their subsystem's Minor rows

### Task 9: Core correctness (R-009, R-083, R-085, R-086, R-087, R-093, R-094, R-096, R-097)

**Files:** `Core/Desktop/ApplicationDiscoveryServices.cs`, `ApplicationLaunchContracts.cs`, `ApplicationLauncher.cs`, `Core/Covenant/CovenantWireValidation.cs`, `Core/ProvingGrounds/ProvingGroundsArbiter.cs`, `Core/Operations/LongRunningOperationRecoveryRegistry.cs`, `Core/Security/HostProcessToolPolicy.cs`, `Core/Cli/DoctorDiagnosticRunner.cs`, `Core/Conclave/ApprenticeExecutionPolicy.cs`, `ApprenticePlanParser.cs`, `Core/Configuration/ConfigurationBootstrapper.cs`, their tests.
**Rows:** as listed; R-009 first (security: anchor `RepositoryRoot` on `AppContext.BaseDirectory` only; no `DevelopmentProject` candidate for a published image without opt-in).

### Task 10: Core and Secrets cleanup (R-079, R-080, R-081, R-082, R-084, R-088, R-089, R-090, R-091, R-092, R-098, R-099, R-100)

**Files:** `Core/Covenant/CovenantLinker.cs`, `CovenantToolInvocationContext.cs`, `CovenantLimits.cs`, `Core/Annals/AnnalContentDigest.cs`, `Core/Telemetry/TelemetryService.cs`, `Core/Configuration/IntelligenceSettings.cs`, `ThemeColors.cs`, `SymlinkPathResolver.cs`, `Core/Caching/SingleFlight.cs` (+ Infrastructure forwarder), `Core/Intelligence/ToolRiskClassifier.cs`, `Secrets/Security/MacOsCredentialStore.cs`, `HostProcessToolsMarkerCredentialCapability.cs`, their tests.
**Rows:** as listed. R-095: no change (ruling).

### Task 11: Data retention and erasure (R-010, R-101, R-102, R-103, R-104, R-105, R-106, R-107, R-109)

**Files:** `Infrastructure/Data/DataRetentionService*.cs`, `CovenantSensitiveRetentionPurgeCoordinator.cs`, `MemoryErasureEvidence.cs`, `DataRetentionLeaseMaintainer.cs`, schema expression indexes, `docs/Arcanum.DESIGN.md` (:2529, :697), their tests.
**Rows:** R-010 first (design change ruled; filter by artifact set, not label `CampaignId`), then the rest.
**Model:** most capable (destructive paths).

### Task 12: Grimoire connections and stores (R-011, R-110, R-111, R-112, R-113, R-114, R-115, R-116, R-117, R-118, R-119, R-120)

**Files:** `Infrastructure/Data/BudgetReservationService.cs`, `SessionAttachmentStore*.cs`, `GrimoireOrdinaryConnectionFactory.cs`, `GrimoireConnectionAdmissionGate.cs`, `SessionContextPinStore.cs`, `IdempotencyClaimStore.cs`, `BudgetAlertRepository.cs`, `Data/Covenant/CovenantConnectionEnrolmentInterceptor.cs`, `GrimoireMaintenanceConnectionFactory.cs`, `FtsMatchQuerySanitizer.cs`, `UploadedFileRepository.cs`, `BatchRepository.cs`, `TurnRunWriter.cs`, `GrimoireEntitySql.cs`, `docs/Arcanum.DESIGN.md:190`, their tests.
**Rows:** as listed; R-119 needs a schema transition file (one `.sql` per object, never a migration).

### Task 13: Covenant store and schema (R-121, R-123, R-124, R-125, R-126, R-127, R-128)

**Files:** `Infrastructure/Data/Covenant/CovenantIndexRebuilder.cs`, `CovenantStoreSql.cs`, `CovenantCleanupWorker.cs`, `CovenantMutationKernel.cs`, `CovenantSearchOutboxWorker.cs`, `Infrastructure/Covenant/CovenantIndexRebuildCoordinator.cs`, `Schema/**` (reserved-table allow-list), their tests.
**Rows:** as listed; R-122 parked (ruling) and recorded in the ledger.

### Task 14: Backup restore orchestration (R-012, R-013, R-129, R-130, R-131, R-135, R-136)

**Files:** `Infrastructure/Backup/BackupRestoreService.cs`, `BackupSecretRewrapper.cs`, `BackupRestoreRecovery.cs`, `BackupRestoreJournal.cs`, `docs/Arcanum.DESIGN.md` §5.4.9, their tests.
**Rows:** R-012 first (invert the retain default; set the flag after the `Cleanup` advance), R-013 (result-returning `RestoreAsync`, capture "absent" distinctly), then the rest.
**Model:** most capable.

### Task 15: Backup codec, planner and migrator (R-132, R-133, R-134, R-137, R-138, R-139, R-140, R-141)

**Files:** `Infrastructure/Backup/BackupArchiveCodec.cs`, `BackupInventoryPlanner.cs`, `BackupService.cs`, `BackupRestoreJournalKeyProvider.cs`, `BackupCreateRecoveryHandler.cs`, `BackupArchiveMigrator.cs`, `Cli/Services/BackupPassphraseReader.cs`, their tests.
**Rows:** as listed.

### Task 16: Workspaces and coding tools (R-014, R-015, R-142, R-143, R-144, R-145, R-146, R-148, R-149, R-150)

**Files:** new `Infrastructure/Workspaces/WorkspaceProtectedPaths.cs`, `CodingTools/WorkspacePatchPlanner.cs`, `WorkspaceFileFingerprintService.cs`, `MultiFileCommitCoordinator.cs`, `WorkspaceCheckRestoreArtifactSeeder.cs`, `WorkspaceCheckRuntime.cs`, `WorkspaceCheckExecutionPolicy.cs`, `PhysicalFileSystemWriter.cs`, `CodexReader.cs`, `UnifiedDiffParser.cs`, `RuntimeWorkspaceRegexFactory.cs`, `Infrastructure/Mcp/SandboxedFileIo.cs`, `ApplyPatchToolExecutionService.cs`, their tests.
**Rows:** R-014 first (guard applies only to model/API-driven writes; the host's own `.arcanum` writes in `CampaignEndpoints` stay), then R-015, then the rest. R-147 was done in Task 3.

### Task 17: Installation reset service (R-016, R-017, R-151, R-152, R-153, R-154, R-155, R-156)

**Files:** `Infrastructure/InstallationReset/InstallationResetService.cs`, `InstallationResetCredentialCatalog.cs`, `HostToolsMarkerPairResetCoordinator.cs`, `FullInstallationResetTerminalContinuation.cs`, their tests.
**Rows:** R-016 and R-017 first, then the rest; R-153 adds logging only (design sanctions the content-free return).

### Task 18: MCP connection manager and internal tools (R-019, R-020, R-021, R-022, R-023, R-024, R-158, R-159, R-160, R-161, R-162, R-164, R-165)

**Files:** `Infrastructure/Mcp/ConnectionManager/McpConnectionManager.Lifecycle.cs`, `McpConnectionManager.Merge.cs`, `McpConnectionManager.cs`, `McpToolMerger.cs`, `SdkMcpClientWrapper.cs`, `McpToolResultFormatter`, `McpSecurityLimits.cs`, `ChannelClientTransport.cs`, `InternalTools/*.cs`, `docs/Arcanum.DESIGN.md` (:226, :925, merge/precedence), their tests.
**Rows:** R-019 (hazard: wire only the internal-server logger; never a logger factory for the McpHttp transport), R-020 then R-021, R-022, R-023 (replace with the manager-level R-158 test), R-024 (document in-process scope for stdio; cap `FormatContentText` accumulation; HTTP bound), then the Minors.

### Task 19: Covenant, Lexicon and Saga review services (R-025, R-026, R-166, R-167, R-168, R-169, R-170, R-171, R-172, R-173, R-174, R-175, R-178)

**Files:** `Infrastructure/Covenant/CovenantExportPolicy.cs`, `CovenantOperationGate.cs`, `CovenantMemoryReviewService.cs`, `CovenantMutationService.Correction.cs`, `CampaignPathMarkerLifecycle*.cs`, `Infrastructure/Memory/SagaMemoryReviewService.cs`, `Infrastructure/Lexicon/LexiconService.MemoryReview.cs`, `LexiconService.Lifecycle.cs`, `Core/Covenant/CovenantOperationLeaseContracts.cs`, `docs/Arcanum.DESIGN.md` (:2419, :2437), `docs/Arcanum.API.md` §8.34, their tests.
**Rows:** R-026 first (design change ruled; off-arm reads must not latch `CovenantProcessResidence`), R-025 (tests only), then the Minors.

### Task 20: Apprentice and Saga extraction (R-027, R-028, R-179, R-180, R-189, R-192, R-193, R-194, R-195, R-201, R-202)

**Files:** `Infrastructure/Hosting/ApprenticeService.cs`, `SagaExtractionService.cs`, `Infrastructure/Data/SagaMemoryStore.cs`, `Core/Operations/LongRunningOperationRecoveryRegistry.cs`, `Infrastructure/Operations/*RecoveryHandler.cs`, `Api/Intelligence/BatchRecoveryService.cs`, `Infrastructure/Repositories/ApprenticeRepository.cs`, `docs/Arcanum.DESIGN.md` (§5.7, §10.8.1, ~4696), their tests.
**Rows:** R-027 first (commit completion before Shifting Fate with `CancellationToken.None`), R-028 (re-enqueue the failed segment's policy; deny-all only for lost provenance), then the Minors; for R-179 choose deletion of dead handlers over wiring producers unless a row says otherwise, and keep the registry contract tests green.
**Model:** most capable.

### Task 21: Workspace indexing (R-029, R-030, R-031, R-032, R-186, R-187, R-197, R-198)

**Files:** `Infrastructure/Hosting/WorkspaceIndexingService*.cs`, `WorkspaceFileWatcher.cs`, `TapestryWeavingService.cs`, `EntryWeavingService.cs`, `Loremaster.cs`, `docs/Arcanum.DESIGN.md` (~4668), `docs/Compendium.README.md` (`MaxFilesToIndex`), their tests.
**Rows:** R-029 first (one shared `IsEligible` predicate; test every relative segment for a leading dot on Unix), R-030, R-031, R-032, then the Minors.

### Task 22: Hosting, daemons and operations (R-181, R-182, R-183, R-184, R-185, R-188, R-190, R-191, R-196, R-199, R-200, R-203)

**Files:** `Infrastructure/Daemons/InMemoryDaemonExecutionRepository.cs`, `DaemonRunner.cs`, `Infrastructure/Hosting/UnseenServantService.cs`, `MacOsDaemonManager.cs`, `WindowsDaemonManager.cs`, `LinuxDaemonManager.cs`, `PidFileService.cs`, `GrimoireDatabaseBootstrapper.cs`, `GrimoireCliInitialization.cs`, `Infrastructure/Coordination/InstallationMaintenanceCoordination.cs`, `ClientMutationBlockerStore.cs`, `Infrastructure/Operations/LongRunningOperationStartupHostedService.cs`, `Infrastructure/Resilience/ProviderHealth*.cs`, `Cli/Services/ArcanumServeLauncher.cs`, their tests.
**Rows:** as listed; R-196 is documentation only (ruling).

### Task 23: Offline transition journal, blobs and markers (R-033, R-034, R-204, R-205, R-206, R-207, R-208, R-209, R-210, R-211, R-212)

**Files:** `Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalStore.cs`, `GrimoireOfflineTransitionJournalFilePrimitives.cs`, `GrimoireOfflineTransitionJournalFileStore.cs`, `GrimoireOfflineTransitionTerminalSuffixFinisher.cs`, `GrimoireOfflineTransitionStartupRecovery.cs`, `Infrastructure/Storage/EncryptedBlobStore.cs`, `BlobEncryptionLifecycleService.cs`, `BlobEncryptionFileProcessor.cs`, `AtomicFile.cs`, `Infrastructure/Tower/PhysicalCampaignRootOpener.MarkerCapabilities.cs`, `PromptRenderer.cs`, `Cli/Commands/DataEncryptionCommands.cs`, `docs/Arcanum.DESIGN.md` (:2537, :2891), their tests.
**Rows:** R-033 first (anchor before key; allow closed epoch-0 anchor with key `NotFound`), R-034 (rollback on rename-2 failure plus a `Canonical == null && Previous != null` recovery arm; no `File.Replace`), then the Minors.
**Model:** most capable.

### Task 24: Repositories, configuration, DI and CommLink (R-035, R-036, R-213, R-214, R-215, R-216, R-217, R-218, R-219, R-220)

**Files:** `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`, `Infrastructure/Familiars/FamiliarProbe.cs`, `FamiliarProcessRunner.cs`, `Infrastructure/Repositories/ApprenticeRepository.cs`, `PromptRepository.cs`, `SessionTurnClaimStore.cs`, `Infrastructure/CommLink/*.cs`, `Infrastructure/Configuration/FileConfigurationPresetPersistence.cs`, `Infrastructure/Operations/LongRunningOperationReconciler.cs`, their tests.
**Rows:** R-035 and R-036 first (composition tests), then the Minors; for R-214 choose documenting "installed but unconsumed" unless wiring is a one-file change.

### Task 25: Tapestry (R-037, R-038, R-039, R-040, R-221, R-222, R-224, R-225, R-229, R-230, R-232)

**Files:** `Api/Intelligence/WizardIntelligenceProvider.cs` (:5513-5541 only), `Api/Tower/MemoryEndpoints.cs` (:941-945), `Infrastructure/Weave/TapestryStore.cs`, `TapestrySummarizer.cs`, `TapestryWeaver.cs`, `TapestryWeavingService.cs`, `Infrastructure/Data/Covenant/CovenantProtectedArtifactErasureKernel.cs`, new `TapestryScope.ForSession/ForSessionAttachment` helpers, schema partial unique index file, `docs/Arcanum.DESIGN.md` §21.11, their tests.
**Rows:** R-037 first (read-side spelling for the Session kind only, both readers together), then R-224 immediately (fixing R-037 activates the erasure exposure), R-038 (`DisableAllTools`/`NoTools` on the summarizer request), R-039, R-040, then the Minors.

### Task 26: Weave, prompt building and spells (R-041, R-223, R-226, R-227, R-228, R-231, R-233)

**Files:** `Infrastructure/Intelligence/Spells/SpellRepository.cs`, `Infrastructure/Intelligence/SystemPromptBuilder.cs`, `Infrastructure/Intelligence/WebResearch/WebResearchBounds.cs`, `Infrastructure/Weave/SagaCurationService.cs`, `SessionAttachmentTextExtractor.cs`, `DivinationService.cs`, `Core/Primitives/EmbeddingBlobCodec.cs`, their tests.
**Rows:** R-041 first (secure byte read for scripts and sidecar; skip non-regular files), then the Minors.

### Task 27: Process runner and A2A client (R-044, R-045, R-046, R-236, R-237, R-238, R-239, R-240, R-241, R-242, R-243, R-244)

**Files:** `Infrastructure/A2A/A2AClientService.cs`, `A2ASendingLedger.cs`, `A2APushNotifications.cs`, `Infrastructure/Process/CappedChildProcessRunner.cs`, `MacOsDescendantSupervisor.cs`, `UnixProcessGroup.cs`, `MacOsSandboxExecProfileBuilder.cs`, `ChildProcessFilesystemJail.cs`, `ChildProcessEnvironmentProfile.cs`, DI named-client registration, `docs/Arcanum.DESIGN.md` (§5.7.1, :3494, :4209), their tests.
**Rows:** R-044, R-045, R-046 first, then the Minors.

### Task 28: Credential stores and key handling (R-047, R-048, R-049, R-248, R-249, R-255, R-256, R-257, R-264, R-269)

**Files:** `Infrastructure/Security/DataProtectionSecretStore.cs`, `OsKeychainSecretStore.cs`, `ProviderCredentialStore.cs`, `WebResearchCredentialStore.cs`, `ArcanumMasterKeyBootstrapper.cs`, `GrimoireKeyDerivation.cs`, `GrimoireKdfSidecar.cs`, `SecureFilePermissions.cs`, `DataProtectionKeyPaths.cs`, new mirrored-credential helper, `Api/Security/ApiKeyAuthenticator.cs`, `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`, `docs/Arcanum.DESIGN.md` §11.2, `README.md:95` (ruled: correct the over-claim), their tests.
**Rows:** R-269 first (extract the helper so R-249 and R-256 land once), then R-047, R-048 (bounded OS read, latch, single-flight, same guard on the startup read), R-049 (known-answer vectors computed with Python `hashlib.pbkdf2_hmac`/`hkdf` outside the test), R-256 (preferred option: stale-mirror marker; Peek keeps returning `Corrupted`; write the policy into DESIGN §11.2 item 4), then R-248, R-255, R-257 (docs), R-264.
**Model:** most capable.

### Task 29: Security guards, audit loggers and transitions (R-245, R-246, R-247, R-250, R-251, R-252, R-253, R-258, R-259, R-260, R-261, R-262, R-263, R-265, R-266, R-267, R-268, R-270)

**Files:** `Infrastructure/Security/OutboundUrlGuard.cs`, `ListenAnySecurityPolicy.cs`, `Core/Environment/ArcanumEnvironment.cs`, `IdentityOwnedFileSystemCleanup.cs`, `Infrastructure/Logging/GuardrailAuditLogger.cs`, `InferenceAuditLogger.cs`, `InMemoryLogRingBuffer.cs`, `HttpsCertificateLoader.cs`, `CovenantEnvelopeCodec.cs`, `CovenantAuthorityStartupReconciler.cs`, `CovenantAuthorityTransitionPublisher.cs`, `Infrastructure/Diagnostics/RuntimeDiagnostics.cs`, `GrimoireDiagnostics.cs`, `SecureDirectoryNameEnumerator.cs`, `AttachmentSourceResolver.cs`, `CampaignRootIdentityKeyProvider.cs`, `HostProcessToolsTransitionService.cs`, `HostProcessToolsAuthorityStore.cs`, `FileHandleIdentity.cs`, their tests.
**Rows:** as listed; R-253 is Windows-only (seam + skip).

### Task 30: Wizard turn lifecycle (R-050, R-051, R-052, R-053, R-054, R-271, R-273, R-274, R-277)

**Files:** `Api/Intelligence/WizardIntelligenceProvider.cs`, `InferenceContextBuilder.cs`, `TurnAccountingHandle.cs`, `ToolExecutionPipeline.cs`, `GrimoireTurnWriter.cs`, `TurnEngine/Projections/IntelligenceEventProjection.cs`, `docs/Arcanum.API.md` (§8 NDJSON error frame), `docs/Arcanum.CHAT-LOOP.md:33`, `docs/Arcanum.DESIGN.md` §22.2, their tests (including the `RecordingTurnRunWriter` fix of R-273 before R-271/R-277).
**Rows:** R-050, R-054 (typed codes on every streaming error frame), R-051 (thread snapshot in the seed), R-052 (filter the OCE catch on the caller token), R-053 (accumulated-spend check in the plateau branch; renew `ExpiresAt`), then R-273, R-271, R-274, R-277.
**Model:** most capable.

### Task 31: Wizard tools, pins and preflight (R-055, R-056, R-272, R-275, R-276, R-278, R-279, R-280, R-281, R-282, R-283, R-284, R-285, R-286, R-287, R-288)

**Files:** `Api/Intelligence/ToolExecutionPipeline.cs` (preflight switch), `SessionContextPinMaterializer.cs`, `Tools/ArcanumReadUrlTool.cs`, `ArcanumBrowseWebTool.cs`, `ArcanumWebSearchTool.cs`, `ArcanumSpellScriptTool.cs`, `TurnEngine/TurnExecutionRequest.cs`, `TurnExecutionCoordinator.cs`, `Projections/OpenAiSseProjection.cs`, `Api/OpenAiV1Endpoints.cs`, `WizardIntelligenceProvider.cs` (retrieval sites only), `docs/Arcanum.CHAT-LOOP.md:38`, `docs/Arcanum.DESIGN.md` (:3417/:3422, §11.27), their tests.
**Rows:** R-055 and R-056 first (tests only; R-056 uses an intermediate directory symlink), then R-272 (invariant test over registered tool names), R-283 together with R-056's hazard, then the rest; for R-279 choose deletion of the dead projection unless routing is a one-file change.

### Task 32: Api intelligence services (R-289 through R-305)

**Files:** `Api/Intelligence/Guardrails/GuardrailsPipeline.cs`, `Subagents/DelegatedManaTracker.cs`, `SubagentRunner.cs`, `ContextCompressionService.cs`, `SessionAttachmentTurnService.cs`, `BatchProcessingService.cs`, `OpenAi/BatchJsonlLines.cs`, `WeaveService.cs`, `Familiars/ClaudeCodeCliChatClient.cs`, `FamiliarChatClient.cs`, `SemanticRouter.cs`, `LexiconEntityExtractor.cs`, `AuditEndpoints.cs`, `OpenAiRequestAugmentingHandler.cs`, `WebWorkflowEndpoints.cs`, `WebResearchWorkflowService.cs`, `IntelligenceEndpoints.cs`, `docs/Arcanum.API.md` (§8.26, Guardrails, `/v1/batches`), `docs/Arcanum.DESIGN.md:1285`, their tests.
**Rows:** all seventeen; R-290 is documentation (state the ceiling is provider-usage-based and post-hoc).

### Task 33: Api framework, idempotency and streaming (R-057, R-058, R-059, R-060, R-306, R-307, R-308, R-309, R-310, R-311, R-312, R-313, R-314, R-315, R-316)

**Files:** `Api/Middleware/ArcanumExceptionHandler.cs`, `Api/ApiRequestJson.cs`, `Api/ApiBootstrapper.cs` (`ThrowOnBadRequest`), `Api/Conclave/ApprenticeEndpoints.cs`, `Api/Tower/SessionEndpoints.cs` (SSE), `CampaignEndpoints.cs` (import read), `CodexEndpoints.cs`, `Api/Security/IdempotencyIdentity.cs`, `IdempotencyEndpointFilters.cs`, `ApiKeyAuthenticator.cs`, `Api/A2A/A2ACallbackEndpoints.cs`, `Api/OpenAiV1BatchesEndpoints.cs`, `Api/Data/DataRetentionEndpoints.cs`, `Api/Health/HealthEndpoints.cs`, `Api/Models/*Invoke*.cs`, `Api/Tower/InferenceExecuteWriter.cs`, `Api.DevHost/Program.cs`, `tests/RetroDownfall.Arcanum.Tests.csproj` (`JsonSerializerIsReflectionEnabledByDefault=false`, expect a first-run cleanup), `docs/Arcanum.API.md` (:327, :412, :867, error table, `/api/meta`, NDJSON), `docs/Arcanum.DESIGN.md` (:965, :2163, :3292, :3552), their tests.
**Rows:** R-057 first (exception-handler arm + `ThrowOnBadRequest`), R-058, R-059, R-060 (the csproj switch may surface many unregistered types; register them), then the Minors; R-306 fingerprint fold with its DESIGN sentence change.

### Task 34: Tower endpoints (R-061, R-062, R-063, R-064, R-317, R-318, R-319, R-320, R-321, R-322, R-323, R-324, R-325)

**Files:** `Api/Tower/PromptEndpoints.cs`, `CampaignEndpoints.cs`, `SessionDivinationEndpoints.cs`, `SessionEndpoints.cs`, `SpellExecutionEndpoints.cs`, `SpellAuthoringEndpoints.cs`, `MemoryErasureEndpoints.cs`, `MemoryEndpoints.cs`, `CovenantMutationEndpoints.cs`, `CovenantInspectionEndpoints.cs`, `Api/Primitives/ArcanumErrorMapper.cs`, `Infrastructure/Repositories/PromptRepository.cs` (`ReplaceCampaignPromptsAsync`), `SessionRepository.cs`, `Infrastructure/Weave/DivinationService.cs`, `docs/Arcanum.API.md` rows named per finding, their tests.
**Rows:** R-061 (resolve `workingDirectory` through `SpellWorkspaceResolver`), R-063 (validate every prompt before the sweep; one transaction), R-062, R-064 (iterative over-fetch), then the Minors.

### Task 35: CLI exit codes and command contracts (R-065, R-066, R-068, R-072, R-073, R-074, R-331, R-332, R-333, R-365, R-366, R-367, R-368, R-369, R-370, R-371, R-372, R-373, R-374)

**Files:** `Cli/Commands/RunInputReader.cs`, `Cli/Commands/CliFailureExit.cs`, every `WriteError`/`return 1` site the R-068 row lists, `Cli/Commands/Tower/SessionCommands.cs`, `CampaignCommands.cs`, `PromptCommands.cs`, `SagaCommands.cs`, `SpellCommands.cs`, `CovenantCommands.cs`, `MemoryCommands.*.cs`, `Cli/Commands/Conclave/ApprenticeCommands.cs`, `Cli/Commands/Configuration/ResourceBrowseCommands.cs`, `WorkspaceCommands.cs`, `Cli/Commands/Wards/WardCommands.cs`, `Cli/Commands/InstallationFactoryResetCommand.cs`, `Cli/Commands/ScryingFocusStager.cs`, `Api/Tower/SagaEndpoints.cs` (page type with `HasMore` for R-066), `docs/Arcanum.Command.Reference.md` rows named per finding, their tests.
**Rows:** R-068 first (every host `Result` through `CliFailureExit`), R-066, R-065, R-072, R-073, R-074, then the Minors.

### Task 36: CLI commands, secrets on argv and local verbs (R-067, R-069, R-326, R-327, R-328, R-329, R-330, R-334, R-335, R-336, R-337, R-338, R-339, R-340, R-341)

**Files:** `Cli/Commands/WebWorkflowCommands.cs`, `Cli/Services/ArcanumHealthProbe.cs`, `Cli/Diagnostics/HostHealthDiagnostics.cs`, `Cli/Commands/InstallationResetApplyBoundary.cs`, export/overwrite sites (`CampaignCommands.cs:411`, `SpellCommands.cs:643`, `PromptCommands.cs:717`), `Cli/Infrastructure/CliCommandTree.Core.cs`, `Cli/Commands/KeyCommands.cs`, `SessionCommands.cs` (compact doc only), `AskCommand.cs`, `ServeCommand.cs`, `SensitiveValueInput.cs`, `ResourceBrowseCommands.cs` (`mcp trust`), `CliArgReader.cs`, `Tower/AuthoredContentReader.cs`, `DoctorCommand.cs`, `Configuration/ConfigCommands.cs`, `Daemon/DaemonCommands.cs`, `FileBatchCommands.cs`, `docs/Arcanum.Command.Reference.md` (:464, :566/:576 and rows named), `docs/Arcanum.DESIGN.md` (:179 reference), their tests.
**Rows:** R-067, R-069 first; R-328 per ruling (remove the positional); R-329 per ruling (doc sentence); then the rest.

### Task 37: CLI services and infrastructure (R-342, R-343, R-344, R-345, R-346, R-347, R-348, R-349, R-350, R-351)

**Files:** `Cli/Services/ServeProcessLauncher.cs`, `ServeOwnershipPolicy.cs`, `BackupPassphraseReader.cs`, `FileBatchApiClient.cs`, `ArcanumApiClient.cs`, `CliContextStore.cs`, `WatchSseParser.cs`, `Cli/Infrastructure/CliApplicationFactory.cs`, `CliCommandTree.cs`, `Cli/UX/RecentResourceStore.cs`, `Cli/Program.cs`, `docs/Arcanum.DESIGN.md` §11 (host lifetime sentence for R-342), `docs/Arcanum.Command.Reference.md` (`-v`, context section), their tests.
**Rows:** R-342 (document stop-on-exit; no detach), then the rest.

### Task 38: Command Center and UX (R-070, R-071, R-352, R-353, R-354, R-355, R-356, R-357, R-358, R-359, R-360, R-361, R-362, R-363, R-364)

**Files:** `Cli/CommandCenter/CommandCenterChatRunner.cs`, `CommandCenterTurnAttachmentBuilder.cs`, `CommandCenterHost.cs`, `CommandCenterApp.cs`, `CommandCenterState.cs`, `SessionLogBuffer.cs`, `IncantationFormatter.cs`, `StreamingUiCoalescer.cs`, `BoundedStreamingTextBuffer.cs`, `Cli/UX/EphemeralReasoningRenderer.cs`, `CliLineReader.cs`, `StreamingRenderCadence.cs`, `MarkdigSpectreRenderer.cs`, `Cli/Infrastructure/CliApplicationFactory.cs` (bare-launch token), `docs/Arcanum.DESIGN.md` Command Center sections, their tests.
**Rows:** R-070 first (reject non-regular files; cooperative build; move staging inside the `try`; rewrite the FIFO test), R-071 (sanitize at the `SessionLogBuffer` append boundary; not `AskCommand` raw stdout), then the Minors; for R-359 choose deletion of the dead renderer unless wiring is trivial.

## Phase 3 — Build, CI, tests and documentation

### Task 39: Build and CI (R-075, R-076, R-077, R-377, R-378, R-379, R-380, R-381, R-382, R-383, R-384, R-385, R-386, R-387, R-388, R-389, R-390)

**Files:** `docs/Arcanum.DESIGN.md` (:508, :343, :100, §13, §19), `src/RetroDownfall.Arcanum.NativeSqlCipher/buildTransitive/*.targets` (comment), `.github/workflows/release.yml`, `release-macos-arm64.yml`, `ci.yml`, `build-windows.yml`, `private-beta-release.yml`, `verify-native-sqlcipher.yml`, new `global.json`, `Directory.Build.props`, `src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj`, `scripts/verify-aot-il-warnings.sh`, `scripts/coverage_threshold.py` and its tests, `tests/coverage.runsettings`, `Infrastructure/Mcp/ArcanumInternalToolServer.cs` (`[ExcludeFromCodeCoverage]`), `tests/RetroDownfall.Arcanum.Tests/Packaging/ContinuousIntegrationWorkflowTests.cs`, `ReleasePipelineTests.cs`, their tests.
**Rows:** R-075 per ruling (docs), R-076, R-077, then the Minors; R-376 parked; R-375 per ruling (no repo-wide reformat; keep Engineering.md :92 aligned with rule 4 wording). For R-380 measure the coverage effect only in the final qualification, not here.

### Task 40: Test-suite health (R-392, R-394, R-395, R-396, R-397, R-399)

**Files:** `tests/.../Hosting/SagaExtractionServiceTests.cs`, `tests/.../Fixtures/GrimoireFixture.cs`, `RestartableArcanumProfileFixture.cs`, `ArcanumWebApplicationFactory.cs`, `tests/.../Support/TempWorkspace.cs`, the bare-catch sites R-396 lists, `tests/coverage.runsettings`, `scripts/coverage.sh`, `docs/Arcanum.DESIGN.md` §13.1, their tests.
**Rows:** as listed; R-398 parked; R-399 is a record correction (nothing to change in code; note it in the report).

### Task 41: Documentation truthfulness (R-400 through R-409)

**Files:** `docs/Arcanum.Engineering.md` (110 links, anchor), `docs/Arcanum.API.md` (§8.23 error catalog, :57, :167, :284-297), `docs/Arcanum.Command.Reference.md` (:21-22, :70, :275, :292, :836), `docs/Arcanum.DESIGN.md` (:3996, :4087), `AGENTS.md` (:14-15, :46), `README.md:182` plus a root `LICENSE` with the inline MIT text, new `tests/.../Build/DocumentationLinkTests.cs`, `ApiErrorCatalogDocumentationTests.cs`, `ApiWireShapeInventoryTests.cs`, extensions to `CliSurfaceTests`, `DocumentationCodeContradictionTests`.
**Rows:** as listed; for R-401 do NOT touch the four files the owner is renaming/deleting; only report which links and tests will break when the owner's change lands.

### Task 42: Closing pass

**Steps:**
- [ ] Re-read the SDD ledger's deferred-minor and parked lines; confirm every register row is either in a task commit, parked with a ruling, or recorded as a follow-up.
- [ ] Run `./scripts/align-csharp-blanklines.sh --compact-delimiters --check <every .cs file changed on the branch>`; fix stragglers.
- [ ] Controller runs final qualification once each: `dotnet build RetroDownfall.Arcanum.slnx`, `dotnet test tests/RetroDownfall.Arcanum.Tests/...`, `dotnet test tests/RetroDownfall.Compendium.Tests/...`, `dotnet test tests/RetroDownfall.TheForge.Tests/...`, `./scripts/coverage.sh --threshold`, `./scripts/verify-aot-il-warnings.sh`, `./scripts/verify-shipping-publish.sh --rid osx-arm64`, `./scripts/verify-native-sqlcipher.sh --rid osx-arm64`.
- [ ] Merge `--no-ff` into `main` from the primary checkout once every gate is green.
