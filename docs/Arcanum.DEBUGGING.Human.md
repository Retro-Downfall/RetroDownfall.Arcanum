# Arcanum — Developer Debugging Guide

This is the verified breakpoint and debugging recipe guide for developers working on the
RetroDownfall.Arcanum repository. It complements `Arcanum.DESIGN.md` (architecture and design),
`Arcanum.API.md` (HTTP contracts), and
`Arcanum.Command.Reference.md` (complete CLI usage), and
`Arcanum.Design.Human.md` (conceptual navigation) rather than duplicating them. Every class and
method referenced below exists in the current source; nothing here is speculative.

## When to use this guide

Use this document when you need to understand how to debug a failing endpoint, trace an inference
turn, verify workspace tool containment, inspect persistence behavior, or confirm a security
boundary. For architecture decisions, read `Arcanum.DESIGN.md`; for route and wire contracts, read
`Arcanum.API.md`. For a quick overview, read
`Arcanum.Design.Human.md`. For command syntax and options, read
`Arcanum.Command.Reference.md`. For the complete `arcanum.json` reference, see
`Compendium.README.md`.

## Running Arcanum under a debugger

- **Host (`arcanum serve`)**: `ServeCommand.Run()` (`Cli/Commands/ServeCommand.cs`) →
  `WebApplication.CreateSlimBuilder()` → `AddArcanumApiServices()` (`ApiBootstrapper`) →
  `MapArcanumEndpoints()` → `StartAsync()` → `WaitUntilReadyAsync()` →
  `WaitForShutdownAsync()`.
- **CLI verbs** (`run`, `ask`, `chat`, `watch`, `open`, `center`, `session`, `memory`, `workspace`, `mcp`, `tool`, `file`, `batch`, `data`, `look`, `lore`, `daemon`, `key`, `config`, `preset`, `serve`): native request/response cycles use `ArcanumApiClient.SendRequestAsync()`; unified SSE observation uses `WatchSseAsync()` and health observation uses `GetHealthReportAsync()`. `run` first composes bounded input/staging and then delegates to the existing inference, research, Spell-selection, or preview clients. `open` resolves through `CliResourceCatalog` before `ApplicationLauncher`; `center` and `open center` call the in-process `ICommandCenterHost`. `file`/`batch` are the deliberate exception: `FileBatchApiClient` parses bare OpenAI success objects, streams multipart/content bodies, and never expects `ApiResponse<T>`. Inspect `ApiBootstrapper.MapArcanumEndpoints()` for endpoint wiring. `memory` is HTTP-only: start in `MemoryCommands`, continue through the typed client methods, then `MemoryEndpoints`; verify that reads remain available when a prompt-time feature is disabled and that only named Lexicon deletion mutates. `session` lifecycle commands must remain HTTP-only; debug selection in `CliResourceCatalog`, command routing in `SessionCommands`, and feature-gate failures at `SessionEndpoints`. Workspace `tree`/`info`/`read`/`search`/index inspection must also remain HTTP-only; start in `WorkspaceCommands`, then its typed `ArcanumApiClient` method, and finally the `/api/workspaces` endpoint. MCP lifecycle/diagnostic commands are likewise HTTP-only: start in `McpCommands` or `ToolCommands`, inspect `ToolArgumentReader` and `ResourceSelector<T>`, then continue into `McpEndpoints`, `DiagnosticMcpInvocationEndpoints`, or `ToolInvokeEndpoints`. `config` prefers `/api/config` but deliberately enters labelled local bootstrap through `ConfigurationCommandService` on unavailability. Data-lifecycle status, retention policy, pruning, and explicit reset/delete commands are HTTP-only through `DataRetentionCommands`, the typed client, and `DataRetentionEndpoints`; only `data encryption ...` remains local through `BlobEncryptionLifecycleService`. The `preset list|show|diff|apply|reset` family is also deliberately local: begin in `PresetCommands` and continue through the shared `IConfigurationPresetService`; no preset HTTP endpoint participates.
- **CLI process contract**: start at `CliApplicationFactory.RunAsync()` → `CliCommandTree.Build()` →
  `CliInvocationContext.Push()`. Payload/diagnostic routing is `ConsoleDispatcher`; destructive
  approval is `ConfirmationPrompt`; final failure categorization is `CliFailureMapper`.
- **Build and publish closure**: use `dotnet build` for the normal debugger loop. Run
  `./scripts/verify-aot-il-warnings.sh [RID|all]` for the shipping publish closure, first-party
  trim/AOT warning gate, and runtime-regex smoke publish (plus execution for the current host RID).
  Windows/Linux shipping RIDs use Native AOT; macOS shipping uses an untrimmed, folder-based
  self-contained publish.
- **Debug-only host**: `Arcanum.Api.DevHost/Program.cs` mirrors the serve wiring but does not ship.

## Breakpoint map (verified load-bearing classes)

| Path | What to inspect at a breakpoint |
|---|---|
| `WizardIntelligenceProvider` (`Api/Intelligence/`) | Turn entry (`ValidateReasoningForCandidate()`); provider resolution (`ProviderResolver.ResolveCandidates()`); model-call execution (`ModelCallExecutor`); context admission (`BuildModelCallContext()`); streaming writer (`InferenceExecuteWriter.WriteStreamAsync()`); interrupted/finalized cleanup (`GrimoireTurnWriter.ResolveInterruptedAndMarkFinalizedAsync()`); audit (`TryLogInferenceAuditAsync()`). |
| `TurnExecutionCoordinator` / `TurnEngine` (`Api/Intelligence/TurnEngine/`) | Semantic source (`ITurnEventSource.RunTurnAsync()`); projection selection (`ITurnPipelineRunner`); commitment tracking (`ProviderAttemptCommitTracker`); explicit budget admission (`TurnAccountingAmbient`); progress signature/no-progress terminal state; cancellation handoff; event emission (`TurnEventEmitter`). Confirm model-call/tool-round counts are telemetry, never terminal policy. |
| `ArcanumDelegateTaskTool` / `SubagentRunner` / `DelegatedManaTracker` (`Api/Intelligence/Tools/` + `Subagents/`) | Parent tool arguments; sterile stateless child request; attachment ids intersected with the parent's current-turn materialized allowlist; explicit files validated per path/content without a total file count; child tools disabled by construction; provider-call charging; exact token/cost budget failure; caller cancellation; `subagent` durable operation completion/failure; single terminal telemetry roll-up. There is no turn/depth counter. |
| `ToolExecutionPipeline` (`Api/Intelligence/`) | Preflight (`ManaPreflight`), attunement (`BuiltInToolRegistry`), ward execution (`ExecuteToolCallWithWardAsync()` / `WardedToolExecutionResult`), `PublicToolFailureMessage`, structured-error logging; shared attachment refresh core used by `ProcessRefreshSessionFileAsync()` and operator `RefreshSessionAttachmentAsync()` for selection, hidden-source Sanctum, MIME-derived kind policy, persistence, structured result, and optional queued injection. Model vision applies only to the injection path. |
| `AttachmentSourceResolver` / `SessionAttachmentStore` | Refresh path reconstruction from encrypted provenance; canonical/link and path-vs-handle identity; double-read stability; session-scoped `RevalidateBoundSourcesAsync()` for authoritative list badges; `PersistRefreshedAsync()` hash reuse/new version under per-session byte protection without a version-count ceiling; `PersistNewCoreAsync()` / `InsertRowAsync()` writer ownership and exact encrypted-blob identity revalidation before metadata publication. Attachment bytes are `ARCABLOB` files on disk; SQLCipher stores their metadata, not the blob. |
| `SessionEndpoints` / `AttachmentCommands` / `ArcanumApiClient` | Standalone multipart snapshot creation with per-file and aggregate declared/chunked bounds; server-only workspace reference resolution; attachment DTO/error mapping; authenticated plaintext content stream; selector/picker behavior; metadata-only terminal output; staged atomic export; encrypted stored-snapshot reveal; privacy disclosure without confirmation. |
| `AttachmentMemoryGateAmbient` / `AttachmentMemoryProvenanceStore` | Current-turn promotion authority across provider/tool tasks; typed session/attachment/key/version/hash/materialized-time/source metadata; metadata-only consultation persistence; dynamic Available/Unavailable source status. |
| `SessionContextPinMaterializer` | File/symbol lexical containment; `SecureFileReader.TryOpenRegularFile()` no-follow single-link admission; no total source-file or additional per-turn pin-count ceiling; every pin already accepted by the unchanged session-management admission contract is considered; incremental full-handle SHA-256 with 64 KiB per-pin / 256 KiB per-turn retained bytes and explicit deferred-pin count; streamed line-range and CRLF normalization. |
| `CommandCenterAttachmentDriftMonitor` / `ShellCommandDispatcher` | Debounced workspace `FileSystemWatcher` invalidation; authenticated attachment-list revalidation; backend-only Snapshot/Live/Stale transitions; loaded/disk hash rendering; `/attachments refresh <name>` confirmation. No UI-thread hashing or client-side Live assumption. |
| `GrimoireTurnWriter` (`Api/Intelligence/`) | Turn creation (`TryBeginBufferedAssistantReplyAsync()`); interruption (`ResolveInterruptedAsync()` / `ResolveInterruptedAndMarkFinalizedAsync()`); audit writing. |
| `SessionEntryPersistence` / `GrimoireRepository` (`Infrastructure/Repositories/`) | Write-lock (`SessionWriteLock.AcquireAsync()`); busy retry (`SqliteBusyRetry`); append (`AppendMandatoryToolInteractionAsync()`); summarization (`GetUnsummarizedEntriesAsync()`); rollup (`CampaignBackedWorkspaceRegistry`). |
| `CampaignBackedWorkspaceRegistry` / `CampaignRepository` | Workspace resolution; typed `ICampaignRepository.AddAsync()` return; SQLite immediate transaction/unique integrity; concurrent inserts without a total campaign-count rejection; `GetAllAsync()` follows every advancing repository page and fails explicitly on non-advancing pagination. |
| `WorkspaceIndexingService` / `IWorkspaceFileWatcher` / `WorkspaceFileWatcherFactory` / `WorkspaceCodeChunker` | Watcher admission (`EnsureWatcher()`), allocation-bounded coalescing (`QueueWatcherChange()`), overflow recovery (`HandleWatcherError()` → `ReconcileWorkspaceAsync()`), lazy complete cancellable traversal (`EnumerateCandidateFiles()`), pooled character-page reads and per-page embedding (`IndexFileAsync()`), continuing per-tick checkpoints, path/handle identity revalidation, stable chunk/embedding reuse across re-indexing, and watcher disposal on workspace unregister or host stop. Inspect `/api/workspaces/{id}/files/index/status` for `Watching`, `Degraded`, `Overflowed`, `Reconciling`, last event, and last successful index; repository/file size must not become a total-work rejection or `ReadToEnd` allocation. |
| `DivinationService` / `WeaveIndexAvailability` / `EmbeddingsVectorStatus` | Managed fallback streams every matching BLOB row, decodes/scorers one row at a time, retains only the requested top-K heap, and observes caller cancellation. Seed a best match after 50,000 orthogonal rows and verify it wins; health, `/api/meta`, and doctor must describe a complete managed scan and report compatibility row budget `0`. |
| `EyeOfTheWorldService` / `PhysicalWorkspaceScanner` / `SpellScanner` / `SpellSearchService` | Complete cancellation-aware contained traversal; canonical resolved-directory visited sets for cycle/escape defense; deterministic ordering; bounded provider-facing projections rather than total entry/depth/result rejection. Verify Eye signature buckets/ToC, solution discovery, Spell per-file/metadata boundaries, complete dependency graphs, and search results beyond former 50,000-entry, 64-depth, and 1,000-result totals. |
| `UnifiedDiffParser` / `WorkspacePatchPlanner` / `ApplyPatchToolExecutionService` | Patch-byte parser allocation; no duplicate file/hunk/line, aggregate-original-input, elapsed, or result-item totals; per-file input/output and cumulative reversible output/staging protection; containment/fingerprint/fuzzy-ambiguity integrity; atomic sequential commit/rollback; failure-only `RecoveryTimeoutMilliseconds`; adaptive output-budget totals and omitted counts. |
| `WorkspaceCheckTrxParser` | Stream all top-level contained TRX paths without a 32-file total; no-follow identity/hardlink validation; per-document XML protections; aggregate output bytes; diagnostic count/message and result materialization bounds. |
| `A2AClientService` | URL allowlist plus `OutboundUrlGuard`; retained `MaxConcurrentA2ATasks` live-slot semaphore; excess outbound work waits cancellably instead of returning `Sending.MaxTasksReached`; remote protocol/result mapping; progress reported on remote state transitions only; remote cost read as known-or-explicitly-unknown, never zero. |
| `A2ASendingLedger` | Durable record of in-flight A2A correspondences in `LongRunningOperations`. Best-effort by design: a Grimoire failure degrades to "no durable record" and is logged rather than failing the Sending, so a missing record after a restart means the write failed, not that the Sending never happened. |
| `SagaExtractionService` / `SagaMemoryStore` / `GrimoireRepository` | Unbounded deduplicated pending-session queue; oldest-first 10-entry checkpoint targets widened through complete timestamp groups; watermark advance only after valid persistence; automatic requeue on LLM/embedding/malformed-response failure; no interval/output/memory-count total. Verify provider capability, cancellation, paged storage/retrieval, provenance, explicit deletion, and retention remain authoritative. |
| `WorkspaceCommands` / `ArcanumApiClient` / `CliContextService` | Complete workspace CLI routing; explicit/saved/current-directory selector precedence; independent Workspace/Campaign containment; server-host path copy; registration guidance; typed `/api/workspaces` calls. Confirm no direct file I/O enters the CLI command handler. |
| `FileBatchCommands` / `FileBatchApiClient` / `BatchProcessingService` / `BatchRecoveryService` / `UploadedFileRepository` / `BatchRepository` | Bare OpenAI JSON parsing; multipart upload; line-aware local JSONL wrapper preflight; one-command upload/create; bounded terminal polling; output/error artifact resolution; filename sanitization; same-directory atomic download; overwrite confirmation; request-count display. In processing, verify 64-line internal pages continue past the former total-count ceiling, reserve explicit budget per page, durably mark each line before provider dispatch, atomically store terminal output/error, and publish completed checkpoints in input order. Restart must skip completed lines and seal uncertain dispatched lines as `batch_interrupted_after_dispatch` without replay. Preserve encrypted output on cancellation or budget rejection and never cancel/delete non-terminal work by age. In persistence, inspect `BatchLineCheckpoints`, `CreateForOwnedFileAsync()` / `TryDeleteUnreferencedAsync()`, and conditional batch reference writes: encrypted bytes live on disk, SQLCipher rows own dispatch/result metadata, and publication/deletion serialize around exact file identity. |
| `McpConnectionManager` / `TrustedMcpWorkspaceStore` (`Infrastructure/Mcp/`) | Digest-bound admission (`IsApprovedDigestAsync()` / `TrustAsync()`); bounded config load (`SecureFileReader` cap `MaxMcpConfigBytes`); lifecycle (`StartAsync()` / `RestartAsync()`); retirement/replacement ordering; identity-owned cleanup. |
| `AtomicFile` / `SecureFileReader` / `PhysicalFileSystemBrowser` (`Infrastructure/Storage/`, `Security/`, and `Workspaces/`) | Handle-bound open (`O_NOFOLLOW` / `NONBLOCK` / `O_CLOEXEC`); identity revalidation (`FileHandleIdentity`); bounded read (`ArrayPool<byte>`); rollback (backup fingerprint verification); identity-owned temp/backup deletion. |
| `BlobEncryptionLifecycleService` / `BlobEncryptionFileProcessor` / `BlobEncryptionMetadataStore` | Candidate inventory; metadata-versus-envelope classification; pre/post plaintext length and SHA-256 verification; atomic replacement; replace-before-metadata retry; bounded worker/throttle; durable checkpoints; retained-key retirement gate. Use `arcanum operation show <id>` for safe progress and `arcanum data encryption verify` for aggregate reconciliation categories. |
| `LongRunningOperationReconciler` / `LongRunningOperationStartupHostedService` | Expired-lease paging, recovery lease/revision ownership, checkpoint validation, bounded concurrency, and complete multi-page recovery. Treat `maxOperations` as one query page only. Verify manual reconcile drains all pages; startup waits at most 10 seconds for readiness, reports Degraded when unfinished, and immediately continues periodic recovery until completion or host shutdown. |
| `BackupService` / `BackupInventoryPlanner` / `BackupDatabaseSnapshotter` / `BackupSecretSnapshotReader` / `BackupArchiveCodec` / `BackupCreateRecoveryHandler` / `BackupPassphraseReader` | Dry-run/create planner parity; typed scope/component status and explicit-only sensitive state; stepped, cancellable online SQLCipher snapshot and schema capture; no-heal filtered portable-key reads; bounded `ARCABACK` encryption and authenticated manifest; staged self-verification/no-clobber publication; exact identity-bound crash cleanup; outer-only inspect/list; full verify temporary-root cleanup; hidden/environment/descriptor passphrase routing with no argv secret. |
| `DataRetentionCommands` / `DataRetentionEndpoints` / `DataRetentionService` / `DataRetentionLeaseMaintainer` | API-only CLI routing and confirmation; typed status/config contracts; dry-run/apply plan identity; bounded candidate ordering; pin/hold/batch/accounting/active-work diagnostics; shared `ManagedLogMutationGate`; elapsed-time lease heartbeats; `ARCADATA2` prune checkpoints; candidate-local post-delete ownership checks; `DataRetentionRecoveryHandler`, `DataRetentionMutationRecoveryHandler`, and `DataRetentionFactoryResetRecoveryHandler`; factory-reset root and backup boundary. |
| `BudgetReservationService` / `TurnAccountingHandle` | Reservation scope (`EstimateWorstCaseTurnUsd()` — per-call max-not-sum through `CostCalculator.CalculateCost()`); reserve/raise/reconcile (`ReserveAsync()` / `AdjustAsync()` / `ReconcileAsync()`); turn lifecycle (`BeginAsync()` / `EnsureReservationForContextAsync()` / usage recording / `CompleteAsync()`). |
| `IdempotencyEndpointFilters` / `IdempotencyClaimStore` (`Api/Security/` / `Infrastructure/Data/`) | Durable acquisition (`TryAcquireAsync()` before execution); Running-claim lease renewal (`HeartbeatAsync()`); terminalization (`CompleteAsync()` / `MarkAbandonedAsync()`); replay gate (`TryBuildReplay()` → `IdempotencyReplayResult` only for a completed terminal in-cap body); process-local single-flight (`LocalFlight`); fail-open only after safe claim handling. |
| `InferenceExecuteWriter` (`Api/TheForge/`) | Buffered/NDJSON writers (`WriteBufferedAsync()` / `WriteStreamAsync()`); sanitized caught-stream failure (`PublicStreamFailureMessage`); terminal marking (`TurnContextGuards.MarkIdempotencyTerminal()`); exact-byte capture delegated to `IdempotencyBufferingStream`. |
| `PublicInferenceErrorMessages` / `OpenAiStreamErrorMapper` / `ArcanumErrorMapper` (`Api/` + `TheForge/`) | Stable error copy (`NativeGenericFailure` / `OpenAiGenericFailure`); `Hub.Model` maps to 404 and OpenAI `model_not_found`; `Session.RestQueueFull` maps to 503; `Security.IdempotencyInProgress` / `IdempotencyConflict` map to 409; no raw exception leakage. |
| `OpenAiRequestAugmentingHandler` / `ProviderHealthProbe` / `WebhookCommLinkDispatcher` | Headers-first response handling; 64 KiB strict-compatibility diagnostic prefix; caller-cancellation propagation; health status without body reads; capped webhook draining. |
| `CliSessionManager` / `ArcanumPaths` | Session isolation (`ARCANUM_TEST_HOME` isolation; no developer storage access); session identity; diagnostic preview (`CliSessionManagerTests` avoids corrupt preview by reading identity, not untrusted content). |
| `WizardIntelligenceProvider.ContextPreview` / `ContextCommands` | Read-only effective-turn assembly, including unified-run forced Spell, preview-only text/image context, research system/tool policy, output reserve, and sampling options; no turn coordinator, attachment persistence, assistant Entry, tool invocation, budget reservation, or main model call; default content redaction; `noRetrieval` embedding bypass; typed CLI/API output. |
| `RunInputReader` / `RunAttachmentStager` / `RunCommand` / `RunExecutionDispatcher` | Positional/pipe/one-line interactive composition; exact 10 MiB UTF-8 stdin admission with no partial result; repeatable relative or explicit absolute `--with @path`; strict text without an extension allowlist; 1 MiB UTF-8 chunks and shared 32 MiB aggregate authority without a file/part-count ceiling; Scrying image policy; SHA-256 metadata; active-context resolution; Agent/research/exact-or-unique-prefix Spell selection; dry-run handoff; only the research-plus-Spell conflict. |
| `MarkdigSpectreRenderer` | Complete chat-answer projection through lazy at-most-256-Ki-character parse chunks. Render a response with a sentinel after the former cutoff and confirm the sentinel is present, no truncation marker is emitted, UTF-16 surrogate pairs are not split, and one Markdig parse never receives the complete oversized string. |
| `ResourceSelector<T>` / `CliResourceCatalog` / `RecentResourceStore` | Resolution precedence (ID, exact name, unique prefix), ambiguity diagnostics, TTY/`--json` prompt suppression, cursor paging until source exhaustion with repeated-token no-progress detection instead of a 100-page ceiling, cancellation before mutation, safe descriptor columns, recency ordering without authority, and owner-only durable staging with unconditional failure cleanup. |
| `OpenCommands` / `ApplicationDeepLinkCodec` / `ApplicationLauncher` | Selector completion before process start; canonical server ID and optional opaque Workspace scope ID; target/schema validation; one direct `ArgumentList` payload; ordered candidate continuation; safe display paths; repository-relative development and exact CLI fallbacks. |
| `TheForgeDeepLinkCoordinator` / `TheForgeDeepLinkRouter` / `CompendiumDeepLinkStartup` | Deferred routing only after authenticated Connected state; Session/Prompt/Spell document navigation; Campaign/Apprentice focus; Workspace-ID-to-server-path resolution; safe wrong-target/future-schema rejection; Compendium Edition default. |
| `CliApplicationFactory` / `CliInvocationContext` / `ConsoleDispatcher` | Recursive `--json`/`--plain`/`--yes` binding; JSON stdout capture and typed-output bypass; ANSI suppression; stdout payload vs stderr diagnostic routing; exit-code normalization; fixed-copy exception mapping. |
| `WatchCommands` / `WatchEventView` / `ArcanumApiClient` | Six-source watch routing; Session/Apprentice selection; authenticated SSE parsing; multi-line `data:` assembly; heartbeat/`[DONE]`/unexpected-EOF classification; UTC/color projection; free-form event/tool filtering; pure NDJSON stdout; stderr diagnostics; health 503 snapshot parsing; reconnect cursor/gap/backoff; Ctrl+C exit 130. |
| `ConfigCommands` / `ConfigurationCommandService` / `ConfigurationPathAccessor` / `ConfigurationWriter` | Host-API versus local-bootstrap selection; generated-metadata dot-path resolution; typed parse; provider-endpoint secure input/redaction; full-snapshot validation; owner-only editor temp file; atomic write; environment-override diagnostics. Inspect `ConfigurationWriter.UpdateAsync()` for the serialized latest-file read/validate/write boundary and `ConfigurationEndpoints.ResolveCurrentSettings()` for the latest persisted API/model/provider projection. |
| `ConfigurationPresetCatalog` / `ConfigurationPresetPlanner` / `ConfigurationEnvironmentResolver` / `ConfigurationPresetService` / `FileConfigurationPresetPersistence` | Six immutable v1 partial overlays and their exact owned paths; full candidate planning without I/O; persisted/effective/proposed diff rows; environment-variable source without raw values; safety/privacy-only override blockers with benign masks shown as drift; Research-only credential probing; idempotence and Active/Drifted/Custom inspection; optimistic full-settings hash; semantic/outbound candidate validation; bounded no-follow state/rollback/journal reads; strict catalog provenance; owner-only before/after journal; conditional recovery/reset that preserves later drift and unowned customization. Confirm `AddArcanumConfigurationPresets` supplies this shared flow. |
| `ArcanumConfigurationTransaction` / `ConfigurationWriter` / `ArcanumConfigurationStore` | Current-user named cross-process serialization for every canonical configuration write; preset transaction coverage across configuration and sidecar finalization; Compendium owner-only staging; exact-byte SHA-256 acknowledgement that suppresses only matching self-write watcher events. |
| `PresetCommands` / `PresetsSectionViewModel` / `PresetsPage` | Shared `list`/`show`/`diff`/`apply`/`reset` contract; secret redaction in text and JSON; disclosure, glossary, progressive guidance, recommendations, and completion summary parity; exact environment-aware diff rendering; explicit Compendium Apply/Reset without a second confirmation; unsaved-edit save-or-cancel gate; stale-plan clearing before canonical reload. There is no preset API or The Forge route. |
| `ArcanumConfigurationStore` / `LocalCertificateGenerator` (`Compendium.Ux/Services/`) | 10 MiB read admission before parse; owner-only durable configuration staging and `finally` cleanup; collision-resistant certificate pair names; staged no-overwrite pair publication and rollback. |
| `ConfirmationPrompt` | `--yes` short circuit; redirected-output fail-closed check before prompt or input read; stderr prompt copy and cancellation-aware input. |
| `ChildProcessFilesystemJail` / `CappedChildProcessRunner` / `CommandOutputArtifactStore` (`Infrastructure/Process/` + `Mcp/InternalTools/`) | Nonblocking/no-follow process-group launch (`setsid` / direct target group, no blocking FIFO before check); command execution with no Arcanum-owned total duration; bounded in-memory preview plus complete decoded UTF-8 output in private connection-scoped artifacts; opaque `read_command_output` strict-UTF-8 `RandomAccess` offset/nextOffset pages; automatic attunement dependency; caller-cancellation event; handle-bound tree termination; `FileOptions.DeleteOnClose` cleanup on failure/cancel/connection disposal/abrupt exit; explicit fail-closed storage errors; no total artifact quota. |
| `GrimoireRepository` / `EntryWindowPolicy` / `SqliteBusyRetry` (`Infrastructure/Repositories/`) | Timestamp-group-safe checkpoint pages (expanded CTE keeps the complete tied group before advancing); recent/provider-context query slices without a total persisted-session ceiling; rollup updates (`CampaignBackedWorkspaceRegistry`); parameterized filtering/ordering; watermark advance only after a complete checkpoint; cancellable `BEGIN IMMEDIATE` retry. |
| `GrimoireDatabaseBootstrapper` / `GrimoireDatabaseUnavailableException` | Dedicated-secret read status; no API-key fallback after a present-but-corrupt secret; sanitized controlled startup failure; normal hosted-service/test cleanup rather than process termination. |
| `ArcanumMasterKeyBootstrapper` / `MasterApiKeyUnavailableException` | Corrupt master-key state with/without an existing Grimoire; sanitized fixed recovery copy; no replacement generation when data exists; controlled startup failure rather than process termination. |
| `DataProtectionSecretStore` / `WebResearchCredentialStore` | `SecureFileReader` admission for protected credential mirrors; no-follow single-link regular-file identity; 64 KiB ciphertext ceiling; rejected/oversized/undecryptable input mapped to corrupt without secret leakage. |
| `GrimoireKdfSidecarFile` | No-follow single-link sidecar admission; 4 KiB read ceiling; owner-only durable staging; atomic replacement and failure cleanup. |

## Debugging recipes

1. **Trace one turn end-to-end:** break at `WizardIntelligenceProvider.ExecutePromptAsync()`
   (resolution loop), the `TurnExecutionCoordinator` step, `TurnEngine` event emission (`Status`,
   `SessionBound`, `Reasoning`, `Token`, `Error`), and
   `InferenceExecuteWriter.WriteStreamAsync()` NDJSON serialization. For an idempotent request,
   continue through `TurnContextGuards.MarkIdempotencyTerminal()`, claim-store `CompleteAsync()`,
   and `IdempotencyEndpointFilters.TryBuildReplay()`. Confirm replay preserves sequence order
   (`Sequence`, not `CreatedAt DESC, Id DESC`) and returns the exact captured terminal bytes.
2. **Inspect replay/claim behavior:** break at `IdempotencyClaimStore.TryAcquireAsync()` before
   execution, `HeartbeatAsync()` while a Running owner holds the lease, and
   `IdempotencyEndpointFilters.TryBuildReplay()`. Only a Completed claim with a terminal, in-cap
   body may become `IdempotencyReplayResult`; partial, cancelled, or over-cap streams must reach
   `MarkAbandonedAsync()` instead. Inspect `LocalFlight` / `ReleaseLocalFlight()` to confirm
   same-process waiters share the leader and never start a second handler.
3. **Inspect workspace-local MCP admission:** `McpConnectionManager.Config` (`BuildMergedToolsForWorkspaceAsync()` bounded `SecureFileReader` read + digest parse), `TrustedMcpWorkspaceStore.IsApprovedDigestAsync()` / `TrustAsync()` (digest on `ManagedMcpServerEntry`), retirement/replacement (`StartAsync()` blocked after retirement), identity-owned cleanup (`IdentityOwnedFileSystemCleanup`). Confirm digest matching, retirement ordering, cleanup tracking.
4. **Inspect filesystem identity / cleanup:** `SecureFileReader` nonblocking open (`O_NOFOLLOW` / `NONBLOCK` / `O_CLOEXEC`); `FileHandleIdentity` metadata validation; `IdentityOwnedFileSystemCleanup` quarantine/re-check/deletion; `AtomicFile` rollback (backup fingerprint verification, identity capture required for backup identity).
5. **Inspect reservation/accounting lifecycle:** start at
   `BudgetReservationService.EstimateWorstCaseTurnUsd()` and confirm answer/reasoning completion
   headroom is a per-call maximum, then follow `TurnAccountingHandle.BeginAsync()` into
   `BudgetReservationService.ReserveAsync()`. Before provider I/O,
   `EnsureReservationForContextAsync()` may raise the high-water amount through `AdjustAsync()`;
   provider usage reaches `RecordChatUsageAsync()` / `RecordUsageAsync()`; `CompleteAsync()`
   reconciles or releases the reservation. Separately inspect `GrimoireRepository` timestamp-group
   summarization and `SqliteBusyRetry` busy/locked retries for a fresh transaction per attempt and a
   cancellation-observed exit.
6. **Inspect isolated delegation:** break at `ArcanumDelegateTaskTool.InvokeCoreAsync()` and `SubagentRunner.RunAsync()`. Confirm the child `PingRequest` has no `SessionId`, workspace, context snapshot, Chronosync, campaign, data streams, tools, or retrieval and that a nested isolation scope still leaves tools disabled without consulting a depth counter. Verify `ModelCallExecutor.RecordDelegatedUsage()` charges provider usage before `ThrowIfExhausted()` applies only the explicit token/cost ceiling, then confirm completion or caller cancellation moves the durable `subagent` row to its exact terminal state.
7. **Inspect blob migration/rotation recovery:** seed a version-zero row and matching plaintext file, then break in `BlobEncryptionFileProcessor.MigrateAsync()`. Stop after `EncryptedBlobStore.WriteAsync()` replaces the file but before `UpdateEncryptionMetadataAsync()` commits; rerun `arcanum data encryption migrate` and confirm the valid envelope is verified and metadata advances without another plaintext rewrite. During rotation, confirm `FileEncryptionKeyProvider` can read both key ids and retires the old id only after aggregate verification reports zero remaining/failed files.
8. **Verify the CLI pipe contract:** run `arcanum operation list --json | jq .` and confirm stdout
   contains one document. Send stderr to a separate file to verify diagnostics never enter that
   document. Break in `FlushJsonOutput()` to distinguish typed output
   (`StructuredPayloadWritten`) from the `CliTextPayload` compatibility wrapper. For a destructive
   command, redirect stdout without `--yes` and break in
   `ConfirmationPrompt.PromptForConfirmationAsync()`; it must throw before reading `Console.In`.
9. **Trace a safe configuration update:** break in `ConfigurationCommandService.ReadAsync()` to
   confirm `/api/config` wins when available and local bootstrap is explicitly diagnosed otherwise.
   Continue through `ConfigurationPathAccessor.Set()`, `ValidateAsync()`, and `WriteAsync()`; a
   provider endpoint must arrive from stdin/hidden prompt and display only `***`. For `config edit`,
   verify owner-only temp permissions, mask restoration, validation-before-write, and deletion in
   `finally`. Then race a full `PUT /api/config` against `PUT /api/data/retention`: break in
   `ConfigurationWriter.UpdateAsync()` and confirm its lock spans the latest-file read, redacted-mask
   merge, outbound/semantic validation, and atomic replacement. After either write,
   `ConfigurationEndpoints.ResolveCurrentSettings()` must make `/api/config`, `/api/models`,
   `/api/providers`, and `/v1/models` agree on `ConfigurationWriter.Latest`; other runtime consumers
   remain on the process-start snapshot until restart. Use
   `ConfigEndpointTests.PutConfig_SerializesValidationAndWriteWithConcurrentRetentionUpdate` and
   `ConfigEndpointTests.ConfigAndModelDiscoveryReadsShareTheLatestPersistedSnapshot` as the
   regression anchors.
10. **Diagnose API-host/WAL CI teardown failures:** run
   `ArcanumWebApplicationFactoryDisposalTests` and
   `GrimoireDatabaseBootstrapperTests.CheckpointOnShutdownAsync_truncates_populated_wal_when_no_readers_hold_it`
   together several times. On macOS/Linux, use the serialized focused command to remove build-server
   noise from the reproduction:

   ```bash
   env DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1 dotnet test \
     tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
     --disable-build-servers -m:1 \
     --filter "FullyQualifiedName~ArcanumWebApplicationFactoryDisposalTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests.CheckpointOnShutdownAsync_truncates_populated_wal_when_no_readers_hold_it"
   ```

   For path mismatches, break in
   `ArcanumWebApplicationFactory.StopApplicationHostAsync()` and the hosted-service `StopAsync`;
   the captured host must finish stopping before `DisposeIsolatedResources()` restores
   `ARCANUM_TEST_HOME`. For an empty pre-checkpoint WAL, verify the test's pooling-disabled owner is
   still open, `wal_autocheckpoint` is zero, and no read transaction is active; do not infer WAL
   population from schema installation or connection close behavior. If this fails only in a larger run,
   rerun the exact failing test in isolation before classifying it as a product regression.
11. **Compare preview with a real turn:** start with `arcanum context inspect "probe"
    --no-retrieval --json`, then repeat through `arcanum run --dry-run "probe" --json` with the
    same selectors/options. Unified dry-run always sets `NoRetrieval=true`, so this is the valid
    spend-free static-plan comparison: no query embedding, retrieval, persistence, selected live
    route, or main provider call may occur. Confirm Workspace RAG, attachment RAG, and Lexicon/Saga
    rows explain that they were skipped. Default `context inspect` is deliberately different: it may
    perform auxiliary semantic routing/retrieval (including a query-embedding call), so do not
    expect its source rows or category profile to equal `run --dry-run`. Treat every preview as a
    pre-inference projection rather than an exact live payload; the live Agent handoff may add local
    `PatternSnapshot` and `ChronosyncDelta` context. Run the same prompt through `ask` only when
    intentional model spending is acceptable, then compare the emitted/audited
    `ContextTokenBreakdown`. Provider-reported post-call input may replace the displayed total, but
    the estimate remains separately attributable.
12. **Verify bounded untrusted I/O:** run `RequestAugmentingHandlerTests`,
    `ProviderHealthProbeTests`, `WebhookCommLinkDispatcherTests`, and
    `SessionContextPinMaterializerTests`. Break at the first response/file stream read. Confirm a
    health response body is untouched, strict fallback stops after 64 KiB, webhook drain stops at
    its cap, caller cancellation propagates, and a context pin never retains more than its output
    limit even while hashing the accepted source handle.
13. **Diagnose SSE heartbeat concurrency tests:** `SseStreamWriterTests` deliberately holds the first
    `MoveNextAsync` pending until `WriteSignalStream` observes a keep-alive write. If it stalls, inspect
    the pending-move reuse in `SseStreamWriter.StreamAsync`; do not replace the signal with short
    scheduler delays or a cancellation token that can expire while coverage suspends the process.
14. **Trace `refresh_session_file`:** begin at the internal MCP selector/schema, then break at
    `ToolExecutionPipeline.ProcessRefreshSessionFileAsync()`,
    `AttachmentSourceResolver.ResolveCurrentAsync()`, and
    `SessionAttachmentStore.PersistRefreshedAsync()`. Confirm the selected id/key was visible at
    turn start; the model supplied no path; Sanctum sees the actual canonical path before either
    handle read; both reads hash identically; detected MIME selects the refreshed kind; unchanged
    content reuses the row; changed content creates one next version with current kind/MIME;
    `TryBuildRefreshedContentsAsync()` consumes inject-once only after materialization; and the User
    extras follow every tool result in the round. Native streams emit
    `attachmentRefreshed` after `toolResult`; OpenAI streams omit it.
    For an operator refresh, start at `ShellCommandDispatcher.RefreshAttachmentAsync()`, continue
    through `POST /api/sessions/{id}/attachments/{attachmentId}/refresh` and
    `ToolExecutionPipeline.RefreshSessionAttachmentAsync()`, and confirm the same resolver/persistence
    core runs with injection and the model-vision gate disabled. For drift, edit a tracked source and break in
    `CommandCenterAttachmentDriftMonitor.PumpAsync()` and `RevalidateBoundSourcesAsync()`; the watcher
    callback must not hash or set Live itself.
    For standalone creation, break inside `SessionEndpoints.MapSessionEndpoints()` at the routes
    whose endpoint names are `CreateSessionAttachmentSnapshot` and
    `CreateSessionAttachmentReference`. Snapshot upload must call `PersistNewAsync()` with no source
    claim, while reference must call `ResolveForReferenceAsync()` and then
    `PersistNewResolvedSourceAsync()` without rereading by path. Confirm failures contain no source
    path and map through `ArcanumErrorMapper` rather than a blanket conflict.
15. **Trace attachment extraction and retrieval:** break at
    `SessionAttachmentIndexingService.TryEnqueue()`, `SessionAttachmentIndexProcessor.ProcessAsync()`,
    and `SessionAttachmentIndexRepository.ReplaceAsync()`. Confirm bytes arrive through
    `ISessionAttachmentStore.ReadBytesAsync()`, limits are clamped before extraction, unsupported
    formats become `NotEligible`, and no partial chunks survive dimension/provider failure. For a
    retrieval probe, inspect `SessionAttachmentRetrievalService.SearchAsync()` and verify its scope
    column is `RetrievalScope` for latest-only search or `SessionId` only for explicit historical
    search. Continue through `AcceptAttachmentRagMaterializations()` and inspect the single
    `ContextMaterializationLedger`: session ids must match; explicit whole versions must suppress
    semantic chunks; configured chunk/attachment/byte/token bounds must reject excess results; and
    filenames/ranges must render under `### Retrieved Session Attachment Context` as adaptive-fenced
    untrusted DATA. On a later attach/refresh tool call, `ReconcileSuppressedSemanticContext()` must
    rebuild the active system prompt before both buffered and streaming continuation calls. Under
    context pressure, verify Saga, workspace RAG, and attachment RAG disappear in that order while
    accepted explicit files remain and complete tool exchanges stay paired. `ContextTokenBreakdown`
    should report history, explicit-attachment, refreshed-file, attachment-RAG, and workspace-RAG
    token fields; the metadata-only attachment index belongs to system context. Dropped workspace /
    attachment chunks and estimated tokens should increment only when context admission evicts them,
    survive provider reconciliation, and produce the Command Center warning. Queue overflow must
    return without failing attachment creation; reconciliation should rediscover the missing Bound
    row. In Command Center, confirm the footer advances `Pending` to `Indexed` or `Failed` and the
    pane total changes from estimated to the valid provider-billed input after the usage frame.
16. **Trace attachment-derived memory promotion:** start with
    `AttachmentMemoryGateAmbient.RegisterMaterialized()` and confirm only successful ledger or
    attach/refresh materialization publishes an opaque attachment id. Continue through
    `ArcanumInternalToolServer.ExecuteScribeLexiconAsync()` and
    `SagaExtractionService.ExtractForSessionAsync()`; an id absent from the current-turn allowlist must be
    rejected before durable write or embedding. Inspect
    `lexicon_fact_attachment_provenance`, `saga_memory_attachment_provenance`, and
    `attachment_memory_consultations`: they contain typed metadata only. Delete the source row and
    verify readers retain the provenance with `SourceAvailability=Unavailable`. Campaign Logger
    prompts, inference audit JSONL, stable prompt-cache segments, and child requests must contain no
    attachment bytes, excerpt text, host path, or hash. Seed more than one extraction page with a
    tool call/result sharing the boundary timestamp: every entry must be reviewed oldest-first and
    the pair must stay together. Fail one page and confirm the watermark does not advance; remove
    the failure and confirm the queued retry catches up without a total memory-count rejection.
17. **Trace Workspace/Campaign CLI mapping:** run `arcanum workspace current` inside a registered
    root, break in `WorkspaceCommands.Current()`, and compare its deepest containing Workspace and
    Campaign independently. Continue through `ResolveWorkspaceAsync()` for a file/search/index
    command and verify the final operation is an authenticated `ArcanumApiClient` request. For a
    remote-host thought experiment, use a path that is valid only on the server and confirm help and
    output call it a server path; never add `File.*` or `Directory.*` content access to the CLI.
18. **Trace MCP/tool CLI administration:** run `arcanum mcp show` or `arcanum tool list`, break in
    `McpCommands` / `ToolCommands`, and confirm every operation reaches a typed `ArcanumApiClient`
    request. For invocation, test inline JSON, `@file`, and redirected stdin at
    `ToolArgumentReader.TryRead()`; oversized/non-object input must fail before the API call. Follow
    external invocation through `DiagnosticMcpInvocationService` and confirm
    `arcanum-internal`, blocked names, untrusted workspaces, server ambiguity, caller cancellation, and output
    truncation remain server-enforced. Safe CLI output must omit command, URL, arguments,
    environment, and secret values. Use a fake MCP client to verify initialization and HTTP connect
    deadlines own only those lifecycle phases; after connection, hold an invocation on a completion
    gate past the former request deadline and assert it completes when released. Cancel a second
    invocation through the caller token and assert no total request timer manufactured the failure.
19. **Trace first-class web workflows:** run `arcanum research "question" --sources 2
    --format markdown`, break in `WebWorkflowCommands.Research()`,
    `ArcanumApiClient.ResearchWebAsync()`, and `WebResearchWorkflowService.ResearchAsync()`. Confirm
    the CLI only consumes NDJSON and never performs a search, fetch, or model call itself. On the
    server, verify the `limits` frame precedes `searching`, every pass that continues adds at least
    one deduplicated citation URL, and `source_target_reached` or `source_exhausted` records the
    terminal reason before `fetching`/`rendering`. The synthesis `PingRequest` must have
    `DisableAllTools=true`, the requested `MaxOutputTokens`, and the resolved continuation
    `SessionId`. Redirect stdout and stderr
    separately: only final terminal/Markdown/JSON belongs on stdout. For `browse --render
    javascript`, confirm no provider call occurs and the response is 503
    `WebResearch.JavaScriptRenderingUnavailable` with the `--render static` hint. For domain
    filters, inspect the Perplexity request for `search_recency_filter` and allocation-safe
    `search_domain_filter`; never log the query, URL, page content, or credential. Use a fake stream
    that advances one chunk before each idle interval to prove the idle deadline resets on progress;
    then stop producing bytes to prove the same policy cancels a genuinely stalled read. Do not
    sleep until a wall-clock total deadline.
20. **Trace native file/batch automation:** run `arcanum batch create ./input.jsonl`, break in
    `FileBatchCommands.ValidateBatchJsonlAsync()`, `FileBatchApiClient.UploadFileAsync()`, and
    `FileBatchApiClient.CreateBatchAsync()`. Confirm an obvious wrapper failure reports its local
    line and sends no request, while a valid file streams first to `/v1/files` and then submits only
    the returned id to `/v1/batches`. In `BatchProcessingService`, use at least 129 lines and verify
    all three internal pages execute; then reject the third page's explicit budget reservation and
    confirm the first two pages remain in the encrypted output with an actionable remaining-line
    error. Give a progressing batch an old `CreatedAt` and verify `TickAsync()` neither cancels nor
    deletes it; host cancellation must leave `in_progress` for startup reconciliation. For
    `batch watch`, break after each `GetBatchAsync()` and
    verify the delay doubles only to the 10-second cap and terminal states stop polling. For
    `file download` and `batch output|errors`, return a traversal-shaped metadata filename, verify
    `SafeFilename()` selects only a sanitized leaf, interrupt during content copy, and confirm the
    original destination survives and the unique stage file is cleaned. Redirect output with an
    existing destination: without `--yes`, confirmation must fail before the content GET; with
    `--json`, stdout must contain exactly one source-generated object and no progress text.
    For publication, break at `UploadedFileRepository.CreateForOwnedFileAsync()`: the publisher must
    write the encrypted `ARCABLOB` file, capture its no-follow identity before waiting for the
    SQLite writer, revalidate that identity inside `BEGIN IMMEDIATE`, and only then insert metadata.
    SQLCipher must contain metadata only, never the file bytes; loser cleanup must target only the
    captured identity. Race `TryDeleteUnreferencedAsync()` against `BatchRepository.CreateAsync()`
    and `UpdateStatusAsync()`: either the reference commits and deletion reports referenced/409, or
    deletion commits and the conditional reference write throws `BatchFileReferenceException`.
    Confirm quarantine rollback/finalization restores or deletes the exact captured bytes. Use
    `UploadedFileRepositoryTests.CreateForOwnedFileAsync_WhenDeleteWriterWins_RejectsWithoutMetadataAndPreservesReplacement`,
    `UploadedFileRepositoryTests.CreateForOwnedFileAsync_WhenCreateWins_DeleteSeesMetadataAndOwnedBytes`,
    and `BatchRepositoryTests.CreateAsync_waits_for_concurrent_file_delete_and_rejects_stale_reference`
    as the race anchors.
21. **Trace standalone attachment automation:** create one snapshot by piping a known payload into
    `arcanum attachment add - --name probe.txt --mime text/plain --session <id>` (for example,
    `printf 'probe' | arcanum attachment add - --name probe.txt --mime text/plain --session <id>`),
    and one live row with
    `arcanum attachment reference probe.txt --workspace <workspace> --session <id>`. Break in
    `AttachmentCommands`, the three
    attachment endpoints, `AttachmentSourceResolver`, and `SessionAttachmentStore`. The snapshot
    must be `SnapshotOnly`; only the reference may carry refreshable provenance. Modify the server
    file, run `attachment refresh`, and verify the shared service creates exactly one next version.
    Run `versions`, pin/unpin, and confirm an image pin reports `Unsupported` during implicit
    materialization rather than disappearing. `show --privacy` must print without reading stdin or
    requiring acknowledgement. For `export`, interrupt the content stream and confirm the old
    destination survives and the staged file is cleaned; `--output -` must fail before a content
    GET. `reveal` must target a locally present `ARCABLOB` snapshot path, never the live source;
    missing/non-envelope local targets must recommend export without launching. Redirect every
    metadata command with `--json` and verify no plaintext bytes or absolute source path reaches
    stdout/stderr. For the storage race, break at `PersistNewCoreAsync()` / `InsertRowAsync()` and
    race factory reset after the encrypted file is written but before its row insert. The insert
    must acquire or reuse SQLite writer ownership, revalidate the exact on-disk blob identity, and
    publish metadata in that transaction. If reset wins, no row is published and identity-bound
    cleanup preserves a replacement; if publication wins, reset sees both metadata and bytes.
    Verify `CopyBytesForForkAsync()` / `InsertForkRowsInAmbientTransactionAsync()` preserve the same
    invariant. Regression anchors are
    `DataRetentionServiceTests.PersistAttachment_WhenFactoryResetWinsBeforeRowInsert_DoesNotPublishMissingBytes`
    and
    `DataRetentionServiceTests.PersistAttachment_WhenOwnedBlobIsReplacedBeforeRowInsert_PreservesReplacement`.
22. **Trace unified watch behavior:** run `arcanum --json watch logs --event-type warning
    --reconnect`, redirect stdout and stderr separately, and break in `WatchCommands`,
    `WatchEventView.Matches()`, and `ArcanumApiClient.WatchSseAsync()`. Confirm the request carries
    the API key and log query filters, multi-line `data:` is reassembled without a client-only size
    cap, comments produce stderr liveness diagnostics, the Session live sentinel is treated as a
    heartbeat, `[DONE]` stops cleanly, and stdout contains only valid
    one-object-per-line JSON. Simulate EOF before `[DONE]`: without reconnect it exits 1 with a
    stderr diagnostic; with reconnect it warns of a possible gap and uses capped exponential delay
    without a retry-count ceiling. For Session, verify the next request advances `since` to the last
    received valid Entry id without claiming replay. Return a valid Unhealthy 503 envelope to `watch
    health --interval 1` and confirm it renders as data. Cancel every source and both compatibility
    aliases (`session watch`, `apprentice chronicle`) and confirm exit 130. Cap/auth failures must
    remain explicit errors, and no heartbeat, `[DONE]`, ANSI, or reconnect diagnostic may enter
    NDJSON stdout.
23. **Trace unified `run` composition without spending:** start with `printf 'piped context' |
    arcanum run --dry-run "positional instruction" --with @notes.unusual --json`, redirecting
    stdout and stderr separately. Break in `RunInputReader.ReadAsync()` and confirm instruction and
    pipe remain separate; then follow `CliInferenceContextResolver`,
    `RunAttachmentStager.StageAsync()`, and `RunExecutionDispatcher.PreviewAsync()`. Stdout must be
    one valid preview payload, while text byte/part/SHA-256 and image byte/SHA-256 staging
    diagnostics remain on stderr. Repeat
    with a relative arbitrary-extension UTF-8 file, an explicitly supplied absolute text path, and
    an allowed image. Confirm only typed content/labels reach `ContextPreviewRequest`, the host
    materializes that content into the planned current turn, and no client path grants server
    filesystem access.
    Exercise the stdin boundary with 10,485,760 bytes and then one byte more; the first preview must
    preserve the complete source in server-sized UTF-8-safe parts, while the second must fail before
    any API request with no truncated payload. Simulate a redirected-stream read failure and verify
    it also fails before dispatch rather than falling back to only the positional instruction. Run dry previews for the default route,
    `--research`, and an exact or unique-prefix `--spell`; verify research preview validates its
    bounds and carries its server-owned untrusted-source instruction and all-tools-disabled synthesis
    policy, forced Spell preview carries and resolves `OverrideSpellName` without retrieval, and
    neither starts search or inference. On a live research trace, verify Campaign-only context and
    the prospective synthesis request validate before provider search. Confirm live staged sources
    use the normal attachment pipeline—persisted and Session-bound when Attachments are enabled,
    in-memory when disabled—while every dry run remains non-persistent. Finally combine
    `--research --spell <name>` and confirm that sole route conflict fails, while stdin, `--with`,
    context, sampling, `--plain`, and `--json` introduce no additional capability conflict.
24. **Trace unified retention without risking unrelated data:** isolate `ARCANUM_TEST_HOME`, seed
    one old eligible row plus one pinned/active-conflict row, then run `arcanum data prune
    --dry-run --json`. Break in `DataRetentionCommands`, the typed API client,
    `DataRetentionEndpoints`, and `DataRetentionService.PlanAsync()`. Confirm the CLI performs no
    direct storage I/O, the plan has stable class totals/candidates/reason codes, and no durable
    operation or deletion occurs. Seed an expired dated inference/guardrail JSONL file and invoke
    each audit writer; verify both writers append today's record without deleting the old file,
    while retention dry-run still plans it. Race each writer against factory reset at the shared
    managed-log gate: a completed append must be counted and cleared, while an append waiting behind
    reset must publish afterward without causing reconciliation failure. Set `maxItemsPerSweep=1`,
    make the oldest item pinned, and verify the next eligible item is still selected. Confirm one
    frozen planning timestamp controls candidate selection and `GeneratedAt`. Apply with the preview
    `planId` through the API and break at the atomic cross-retention lease start,
    `DataRetentionLeaseMaintainer` elapsed-time heartbeats, each checkpoint, dependency deletion,
    and candidate-local reconciliation. The `ARCADATA2` prune checkpoint must preserve that
    `GeneratedAt` and every candidate's original cutoff across restart. Recovery must use the more
    restrictive of the original and current cutoffs, preserve a candidate whose current rule is
    disabled, and fail closed on legacy `ARCADATA1` or incomplete cutoff authority. Use
    `DataRetentionAgeBoundaryTests.PlanAsync_Prune_UsesOneFrozenTimestampForSelectionAndGeneratedAt`,
    `DataRetentionAgeBoundaryTests.RecoverPruneAsync_WhenPolicyShortens_EnforcesPersistedOriginalCandidateCutoff`,
    and `DataRetentionAgeBoundaryTests.RecoverPruneAsync_WithLegacyCheckpoint_FailsClosed` as the
    recovery anchors. Substitute a managed file after its no-follow identity capture and verify the
    operation preserves both bytes and metadata.
    Inspect the exact `ARCAMUT2` root-role/relative-path/no-follow-identity/quarantine-prefix
    manifest before file mutation; seed unrelated or malformed quarantine and confirm recovery
    fails closed instead of adopting it. Interrupt between SQL/file phases, reconcile/retry, and confirm repeated application converges
    without deleting the pinned/active candidate. Delete an attachment and verify its bytes/chunks/embeddings/index
    state leave while Saga/Lexicon provenance remains and reads `Unavailable`. Before a factory-reset
    probe, create an external backup sentinel and verify the plan/prompt names it as preserved and
    apply never targets it, configuration, or security/key material. Interrupt factory reset before
    durable completion and verify startup recovery reruns the idempotent cleanup, preserves the
    reset's own marker, and allows daemon/data work waiting behind the reset boundary to continue.
25. **Trace application launch and deep-link routing:** run `arcanum open session <selector>` with
    breakpoints in `OpenCommands`, `CliResourceCatalog.SelectSessionAsync()`, and
    `ApplicationLauncher.TryLaunch()`. Selection must produce the canonical Session GUID before any
    discovery/start call. Inspect `ProcessStartInfo`: `UseShellExecute` is false, `Arguments` is
    empty, and the application-facing arguments are exactly `--arcanum-deep-link` plus one compact
    JSON value. A bundle launch prefixes those with `/usr/bin/open -n <bundle> --args`; a development
    launch prefixes them with `dotnet run --project <project> --`.
    Repeat with spaces, quotes, Unicode, and a leading hyphen in a Spell name; no text may become an
    extra argument. For a Workspace Spell, confirm the envelope contains `WorkspaceInfo.Id`, not its
    path, then continue in The Forge through `TheForgeDeepLinkCoordinator.RouteAsync()`, authenticated
    Workspace lookup, and `TheForgeDeepLinkRouter.RouteAsync()`. Disable installed candidates and
    confirm every safe display path, the repository-relative `dotnet run` command, and the exact CLI
    fallback are printed. In a Windows/Linux package-layout probe, confirm side-by-side extracted
    `*-win-x64` or active-architecture `*-linux-x64|arm64` folders are attempted and the other Linux
    architecture is not. Cancel a picker and verify the starter is untouched. Finally pass a
    wrong-target or future-schema payload directly to either desktop app: The Forge must decline it
    without revealing raw payload, while Compendium retains Edition.
26. **Trace portable backup without touching real state:** isolate `ARCANUM_TEST_HOME` and begin
    with `arcanum backup create --scope metadata-only --dry-run --json`. Break in the backup command
    handler and `BackupInventoryPlanner.BuildAsync()`; confirm dry-run and creation use the same
    typed request, arbitrary paths cannot enter through include/exclude, no installation secret is
    read, and structured output contains warnings/status but no passphrase or portable key bytes.
    With configuration selected, create matching `arcanum.preset.json` and
    `arcanum.preset.rollback.json` files and confirm both appear as configuration entries beside
    `arcanum.json`. Add `arcanum.preset.journal.json` and confirm that the transient journal never
    appears; a pending journal fails the configuration component and prevents the possibly
    mid-transaction configuration and sidecar pair from being captured. Give state and rollback
    different bytes and confirm both are omitted while the configuration component reports failure.
    Create the metadata-only archive through a hidden prompt or controlled inherited descriptor,
    then run outer-only `backup inspect`, passphrase-backed inspect, `backup verify`, and `backup
    list`. The first and last must not request a secret; decrypted inspect must return the manifest
    without extracting entries; verify must remove its protected payload/extraction staging.
    For a full probe, keep a SQLCipher connection in WAL mode, commit one generation while leaving a
    later write uncommitted, and break in `BackupDatabaseSnapshotter.CreateAsync()`. Confirm the
    copied database contains the committed generation only, passes `quick_check` and
    `foreign_key_check`, and was not created by copying `.db`/`-wal`/`-shm`. Repeat with a large
    database, cancel after a successful online-backup page step while pages remain, and confirm the
    next boundary throws cancellation, finishes the native backup handle, publishes no destination,
    and identity-cleans its temporary database. There is no elapsed-time or page-count cutoff; a
    transient busy/locked source continues retrying until it progresses, fails, or the caller
    cancels. Break in
    `BackupSecretSnapshotReader` and confirm it performs non-healing reads and serializes only the
    Grimoire secret plus file keys active/referenced by selected encrypted blobs—not the raw OS/DP
    stores or unrelated master API key.
    Delete one manifest-required attachment after planning and verify creation returns incomplete,
    publishes no archive, and names the typed failed component without leaking content. Repeat with
    a selected symlink/reparse path, an intermediate linked directory, and a source replaced during
    streaming; each must fail closed. In `BackupArchiveCodec.WriteAsync()`, inspect owner-only
    same-directory staging, bounded hashing/encryption, durable flush, staged self-verification, and
    atomic no-clobber move. Cancel during a large file and confirm owned staging disappears while an
    existing destination remains byte-for-byte unchanged.
    Finally flip one header byte and one authenticated payload byte in separate archive copies and
    try a wrong passphrase. Every mutation must be detected; an authenticated-byte failure must not
    distinguish corruption from the wrong passphrase. Verify a clean full archive and follow the
    owner-only temporary extraction through manifest size/hash comparison, portable-secret database
    open, `quick_check`, schema-id comparison, and cleanup. The archive must be owner-only and no
    plaintext state, key, or passphrase may appear in the file header, terminal JSON, diagnostics,
    logs, or leftover temporary paths.
    **Then trace the restore that must commit everything or nothing:** create
    a full archive from a seeded installation, then run `arcanum backup restore <archive> --dry-run`.
    Break in `BackupRestoreService.BuildPlanAsync()` and confirm the plan authenticates, classifies
    the format against `BackupRestoreFormatCatalog`, measures required bytes as restored + displaced
    + headroom, and mutates nothing; the installation must be byte-identical afterwards. Flip byte 11
    of a copy to declare a newer format and confirm the refusal is `backup.restore_format_newer` with
    upgrade guidance rather than a corruption message, before any staging directory exists. Hold
    `ArcanumMaintenanceLock` from a second process and confirm restore refuses; note the lock file
    lives in the *parent* of the Grimoire root so the commit rename cannot be blocked by an open
    handle. Wipe the installation, restore onto the empty root with a fake secret store, and confirm
    the Grimoire secret came from the archive's portable material and not from any credential store.
    Drop a table from the snapshot before backing up and confirm restore reports
    `SchemaMigrationRequired` and reinstates the object through `GrimoireSchemaInstaller` — never by
    touching migration history. Supply `--map campaign-root=C:\...=/srv/...` and confirm
    `Campaigns.Path`, `WorkspaceContexts.RootPath`, attachment provenance, and Sanctum allow-lists
    are rewritten with converted separators, that Windows sources match case-insensitively while Unix
    sources do not, and that unmapped absolute paths are reported rather than guessed. Confirm every
    `WorkspaceFile` attachment ends at `WorkspaceUnavailable` while its snapshot bytes remain
    readable, that trusted MCP metadata is withheld with a reason, and that `Host:ListenAny` is
    `false` in the restored configuration. Inject a fault at the `Reconcile` phase and confirm the
    result is `RolledBack`, the original tree is back with its original bytes, and no staging root
    survives; inject one at `Validate` and confirm `Rejected` with the installation untouched. Then
    reproduce each commit boundary by hand — journal phase before `Commit`; live and staged present;
    live missing with `previous/` present; staged gone with live and `previous/` both present — and
    confirm `BackupRestoreRecovery.Resolve()` maps each to discard, rollback, rollback, and
    reconciliation-required respectively. Finally run `arcanum backup migrate <archive> -o <new>` and
    confirm the source bytes are unchanged and the output verifies.
27. **Trace a preset without hiding effective configuration:** isolate `ARCANUM_TEST_HOME`, write a
    valid configuration, and run `arcanum preset list`, `arcanum preset show research`, then
    `arcanum preset diff research --json`. With
    `ARCANUM_Arcanum__Features__WebBrowsing=false`, follow
    `ConfigurationEnvironmentResolver.Resolve()` into `ConfigurationPresetPlanner.Plan()` and
    confirm the row separately reports persisted `false`, effective `false`, proposed persisted
    `true`, the environment-variable name, `EnvironmentOverrideIsEffective=true`,
    `PersistedValueChanges=true`, and `EffectiveValueChanges=false`. Raw environment values must not
    enter provenance. This benign feature mask must remain visible as drift without becoming an
    applicability blocker; only the missing Research credential should make this plan inapplicable,
    and only Research diff/apply should probe its secure store. Repeat with
    `ARCANUM_HOST_ANY=true` and Private/Offline to confirm a contradictory privacy override does
    block, then repeat with Automation and confirm a missing positive budget blocks apply without
    creating or enlarging one.

    For a successful apply, break from `PresetCommands.Apply()` through
    `ConfigurationPresetService.ApplyAsync()`, candidate validation,
    `FileConfigurationPresetPersistence.ApplyAsync()`, and `ConfigurationWriter.UpdateAsync()`.
    Confirm `ArcanumConfigurationTransaction` serializes CLI and Compendium writers with the same
    current-user cross-process mutex; the expected settings hash rejects a stale preview before
    mutation; and configuration, owner-only provenance, and rollback state finalize under that
    transaction. Inspect the prepared journal: it must contain only owned before/after values and
    hashes plus previous/next provenance, never a full `ArcanumSettings`. Attempt oversized and
    symlinked sidecars plus catalog-forged or state/rollback-mismatched provenance and confirm the
    bounded no-follow reader and strict validation reject them.

    Interrupt after the configuration write, make a concurrent unrelated/manual edit, and confirm
    recovery conditionally reverses only owned values still equal to the journal's after-values.
    Change one preset-owned persisted value and one unrelated value, then run
    `arcanum preset reset`: only owned values that still equal their preset-applied value return to
    baseline, while the drifted and unrelated values remain and the restored/preserved counts tell
    the truth.

    Finally open Compendium's Presets section and compare its definition, prerequisite states,
    exact diff, progressive disclosure, glossary, recommendations, and completion summary with the
    CLI output. Selection is preview-only; explicit Apply/Reset use the same service without another
    confirmation, while an unsaved Compendium edit disables both with save-or-cancel guidance.
    After success, confirm the old plan is cleared before reload and that only a watcher event whose
    bytes match the acknowledged SHA-256 fingerprint is suppressed. Different bytes must still
    surface as an external change. Verify no HTTP request or The Forge navigation occurs. The
    guided wizard that consumes this same service is covered by recipe 27a.

27a. **Debug the guided setup wizard without touching a real installation:** substitute the
    authorities it composes — configuration command service, preset service and persistence,
    provider and web-research credential stores, CLI context store, and provider probe — and drive
    the state machine with a scripted prompt. Run `arcanum setup --plan` first: it must print the
    diff, credential actions, blockers, and completion summary while performing zero writes, and it
    must mask the provider endpoint, which is a sensitive configuration value.

    For abort safety, end input at every step index in turn and assert exit 130 plus zero
    configuration writes, zero preset applies, no stored credential, and no CLI context save. Do the
    same for declining the final plan. A run that reaches the commit must write exactly once in the
    order credential, configuration, preset, context.

    For rollback, fail the preset apply and assert the previous configuration is restored and the
    credential this run created is deleted; then pre-seed an existing credential for the same
    provider and assert the wizard reports an actionable partial-commit state naming
    `arcanum key provider set <provider>` instead of pretending it restored a value it never read.
    Fail the configuration write instead and assert the preset is never applied and no context is
    saved.

    For validation classification, return each `SetupConnectivityStatus` from the probe in turn and
    confirm the blocker names the specific dependency. Confirm the commit is blocked unless
    `--allow-unreachable-provider` is passed. Assert the probe requests only `{endpoint}/models` with
    `GET`, so validation never spends inference tokens.

    For disclosure, pipe credentials through `--provider-key-stdin` and `--research-key-stdin` and
    assert the secret appears in neither stdout, stderr, nor the `--json` document, and that no
    option accepts a secret value in argv. `SetupDraft.ToString()` must redact credentials.

    For idempotency, seed the fake configuration with the values the wizard would write and assert
    `--plan` reports an applicable plan with an empty configuration diff, and that unrelated
    settings such as `cli.showManaBar` and `retention.automaticSweepsEnabled` survive a commit
    unchanged.

28. **Debug progress-driven work and cancellation without wall-clock sleeps:** use fake transports,
    completion gates, and injected delay/time providers. For the model/tool loop, feed more changing
    tool proposals/results than the former count ceilings, then a final answer; assert every round
    runs and the buffered/streaming terminal reason matches. Feed the same normalized proposal and
    classified result twice to assert deterministic `no_progress`. For `execute_command` and
    `run_spell_script`, hold a fake child open past the former total-deadline
    decision point while it emits progress, then release it and assert success; in a separate case
    cancel the owning token and follow process-group kill, cleanup classification, and exit 130.
    Emit output beyond `ToolOutputCapBytes`, verify the preview stays bounded and no text is lost
    across `read_command_output` pages, and prove each stream file disappears as soon as its final
    page is consumed. Then dispose the MCP connection and prove its `0700`/`0600` artifact tree is
    gone. Also terminate a child host without disposal and prove delete-on-close removed every
    artifact file (an empty root is harmless). Emit stdout and stderr whose combined spill crosses a
    small explicit Sanctum `MaxFileWriteMb`; assert the shared measured limit kills the process tree,
    deletes partial artifacts, and reports the owner, saved state, and exact operator action. Inject
    another spill-write failure and assert an explicit error rather than a successful truncated
    result. Confirm a spell declaring only
    `execute_command` receives host-exposed read-only `read_command_output`, while neither tool is
    introduced when the host did not expose it.
    For human prompts, leave the reservation pending until a controlled response completes it, then
    cancel a second reservation and verify the 64-waiter admission gate remains independent of wait
    duration. For SQLite, use the injected delay callback to return several BUSY/locked results,
    then success; assert per-delay backoff caps while attempt count does not terminate the work.
    For research, return a deterministic sequence of new URL sets followed by one identical set;
    assert continuation for every changing pass and `source_exhausted` on the first no-progress
    pass. For workspace indexing and Apprentice capacity, create data beyond former total-work
    ceilings, advance checkpoints/slot-release signals explicitly, and assert every item eventually
    completes or a caller cancellation records its saved state. Never make these tests sleep until
    an arbitrary timeout; the progress signal or cancellation token must own every transition.

## Related documents

- Architecture and design source of truth: [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md)
- API source of truth: [`Arcanum.API.md`](Arcanum.API.md)
- Human navigation guide: [`Arcanum.Design.Human.md`](Arcanum.Design.Human.md)
- Agent/operator primer: [`Arcanum.README.md`](Arcanum.README.md)
- Complete configuration reference: [`Compendium.README.md`](Compendium.README.md)
- The breakpoints above are verified against the code. Correct any discrepancy here and update the
  owning architecture, API, or configuration document when its contract also changed.
