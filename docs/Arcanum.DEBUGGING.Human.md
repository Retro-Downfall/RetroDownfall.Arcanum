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
- **CLI verbs** (`run`, `ask`, `chat`, `watch`, `session`, `memory`, `workspace`, `mcp`, `tool`, `file`, `batch`, `data`, `look`, `lore`, `daemon`, `key`, `config`, `serve`): native request/response cycles use `ArcanumApiClient.SendRequestAsync()`; unified SSE observation uses `WatchSseAsync()` and health observation uses `GetHealthReportAsync()`. `run` first composes bounded input/staging and then delegates to the existing inference, research, Spell-selection, or preview clients. `file`/`batch` are the deliberate exception: `FileBatchApiClient` parses bare OpenAI success objects, streams multipart/content bodies, and never expects `ApiResponse<T>`. Inspect `ApiBootstrapper.MapArcanumEndpoints()` for endpoint wiring. `memory` is HTTP-only: start in `MemoryCommands`, continue through the typed client methods, then `MemoryEndpoints`; verify that reads remain available when a prompt-time feature is disabled and that only named Lexicon deletion mutates. `session` lifecycle commands must remain HTTP-only; debug selection in `CliResourceCatalog`, command routing in `SessionCommands`, and feature-gate failures at `SessionEndpoints`. Workspace `tree`/`info`/`read`/`search`/index inspection must also remain HTTP-only; start in `WorkspaceCommands`, then its typed `ArcanumApiClient` method, and finally the `/api/workspaces` endpoint. MCP lifecycle/diagnostic commands are likewise HTTP-only: start in `McpCommands` or `ToolCommands`, inspect `ToolArgumentReader` and `ResourceSelector<T>`, then continue into `McpEndpoints`, `DiagnosticMcpInvocationEndpoints`, or `ToolInvokeEndpoints`. `config` prefers `/api/config` but deliberately enters labelled local bootstrap through `ConfigurationCommandService` on unavailability. Data-lifecycle status, retention policy, pruning, and explicit reset/delete commands are HTTP-only through `DataRetentionCommands`, the typed client, and `DataRetentionEndpoints`; only `data encryption ...` remains local through `BlobEncryptionLifecycleService`.
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
| `TurnExecutionCoordinator` / `TurnEngine` (`Api/Intelligence/TurnEngine/`) | Semantic source (`ITurnEventSource.RunTurnAsync()`); projection selection (`ITurnPipelineRunner`); commitment tracking (`ProviderAttemptCommitTracker`); budget admission (`TurnAccountingAmbient`); event emission (`TurnEventEmitter`). |
| `ArcanumDelegateTaskTool` / `SubagentRunner` / `DelegatedManaTracker` (`Api/Intelligence/Tools/` + `Subagents/`) | Parent tool arguments; sterile stateless child request; attachment ids intersected with the parent's current-turn materialized allowlist; `MaxSubagentDepth = 1`; provider-call charging; exact budget failure; `subagent` durable operation completion/failure; single terminal telemetry roll-up. |
| `ToolExecutionPipeline` (`Api/Intelligence/`) | Preflight (`ManaPreflight`), attunement (`BuiltInToolRegistry`), ward execution (`ExecuteToolCallWithWardAsync()` / `WardedToolExecutionResult`), `PublicToolFailureMessage`, structured-error logging; shared attachment refresh core used by `ProcessRefreshSessionFileAsync()` and operator `RefreshSessionAttachmentAsync()` for selection, hidden-source Sanctum, MIME-derived kind policy, persistence, structured result, and optional queued injection. Model vision applies only to the injection path. |
| `AttachmentSourceResolver` / `SessionAttachmentStore` | Refresh path reconstruction from encrypted provenance; canonical/link and path-vs-handle identity; double-read stability; session-scoped `RevalidateBoundSourcesAsync()` for authoritative list badges; `PersistRefreshedAsync()` hash reuse/new version under existing gates and byte/version budgets; `PersistNewCoreAsync()` / `InsertRowAsync()` writer ownership and exact encrypted-blob identity revalidation before metadata publication. Attachment bytes are `ARCABLOB` files on disk; SQLCipher stores their metadata, not the blob. |
| `SessionEndpoints` / `AttachmentCommands` / `ArcanumApiClient` | Standalone multipart snapshot creation with per-file and aggregate declared/chunked bounds; server-only workspace reference resolution; attachment DTO/error mapping; authenticated plaintext content stream; selector/picker behavior; metadata-only terminal output; staged atomic export; encrypted stored-snapshot reveal; privacy disclosure without confirmation. |
| `AttachmentMemoryGateAmbient` / `AttachmentMemoryProvenanceStore` | Current-turn promotion authority across provider/tool tasks; typed session/attachment/key/version/hash/materialized-time/source metadata; metadata-only consultation persistence; dynamic Available/Unavailable source status. |
| `SessionContextPinMaterializer` | File/symbol lexical containment; `SecureFileReader.TryOpenRegularFile()` no-follow single-link admission; 64 MiB source ceiling; incremental full-handle SHA-256 with bounded retained content; streamed line-range and CRLF normalization. |
| `CommandCenterAttachmentDriftMonitor` / `ShellCommandDispatcher` | Debounced workspace `FileSystemWatcher` invalidation; authenticated attachment-list revalidation; backend-only Snapshot/Live/Stale transitions; loaded/disk hash rendering; `/attachments refresh <name>` confirmation. No UI-thread hashing or client-side Live assumption. |
| `GrimoireTurnWriter` (`Api/Intelligence/`) | Turn creation (`TryBeginBufferedAssistantReplyAsync()`); interruption (`ResolveInterruptedAsync()` / `ResolveInterruptedAndMarkFinalizedAsync()`); audit writing. |
| `SessionEntryPersistence` / `GrimoireRepository` (`Infrastructure/Repositories/`) | Write-lock (`SessionWriteLock.AcquireAsync()`); busy retry (`SqliteBusyRetry`); append (`AppendMandatoryToolInteractionAsync()`); summarization (`GetUnsummarizedEntriesAsync()`); rollup (`CampaignBackedWorkspaceRegistry`). |
| `CampaignBackedWorkspaceRegistry` / `CampaignRepository` | Registry capacity (`Campaign.MaxReached`); workspace resolution; typed `ICampaignRepository.AddAsync()` return. |
| `WorkspaceIndexingService` / `IWorkspaceFileWatcher` / `WorkspaceFileWatcherFactory` / `WorkspaceCodeChunker` | Watcher admission (`EnsureWatcher()`), bounded coalescing (`QueueWatcherChange()`), overflow recovery (`HandleWatcherError()` → `ReconcileWorkspaceAsync()`), final-state incremental processing (`ProcessPendingWatcherEventsAsync()`), path/handle identity revalidation (`IndexFileAsync()`), stable chunk/embedding reuse, and `FileSystemWorkspaceFileWatcher` disposal on workspace unregister or host stop. Inspect `/api/workspaces/{id}/files/index/status` for `Watching`, `Degraded`, `Overflowed`, `Reconciling`, last event, and last successful index. |
| `WorkspaceCommands` / `ArcanumApiClient` / `CliContextService` | Complete workspace CLI routing; explicit/saved/current-directory selector precedence; independent Workspace/Campaign containment; server-host path copy; registration guidance; typed `/api/workspaces` calls. Confirm no direct file I/O enters the CLI command handler. |
| `FileBatchCommands` / `FileBatchApiClient` / `UploadedFileRepository` / `BatchRepository` | Bare OpenAI JSON parsing; multipart upload; line-aware local JSONL wrapper preflight; one-command upload/create; bounded terminal polling; output/error artifact resolution; filename sanitization; same-directory atomic download; overwrite confirmation; request-count display. In persistence, inspect `CreateForOwnedFileAsync()` / `TryDeleteUnreferencedAsync()` and conditional batch reference writes: encrypted bytes live on disk, SQLCipher rows are metadata, and publication/deletion serialize around exact file identity. |
| `McpConnectionManager` / `TrustedMcpWorkspaceStore` (`Infrastructure/Mcp/`) | Digest-bound admission (`IsApprovedDigestAsync()` / `TrustAsync()`); bounded config load (`SecureFileReader` cap `MaxMcpConfigBytes`); lifecycle (`StartAsync()` / `RestartAsync()`); retirement/replacement ordering; identity-owned cleanup. |
| `AtomicFile` / `SecureFileReader` / `PhysicalFileSystemBrowser` (`Infrastructure/Storage/`, `Security/`, and `Workspaces/`) | Handle-bound open (`O_NOFOLLOW` / `NONBLOCK` / `O_CLOEXEC`); identity revalidation (`FileHandleIdentity`); bounded read (`ArrayPool<byte>`); rollback (backup fingerprint verification); identity-owned temp/backup deletion. |
| `BlobEncryptionLifecycleService` / `BlobEncryptionFileProcessor` / `BlobEncryptionMetadataStore` | Candidate inventory; metadata-versus-envelope classification; pre/post plaintext length and SHA-256 verification; atomic replacement; replace-before-metadata retry; bounded worker/throttle; durable checkpoints; retained-key retirement gate. Use `arcanum operation show <id>` for safe progress and `arcanum data encryption verify` for aggregate reconciliation categories. |
| `DataRetentionCommands` / `DataRetentionEndpoints` / `DataRetentionService` / `DataRetentionLeaseMaintainer` | API-only CLI routing and confirmation; typed status/config contracts; dry-run/apply plan identity; bounded candidate ordering; pin/hold/batch/accounting/active-work diagnostics; shared `ManagedLogMutationGate`; elapsed-time lease heartbeats; `ARCADATA2` prune checkpoints; candidate-local post-delete ownership checks; `DataRetentionRecoveryHandler`, `DataRetentionMutationRecoveryHandler`, and `DataRetentionFactoryResetRecoveryHandler`; factory-reset root and backup boundary. |
| `BudgetReservationService` / `TurnAccountingHandle` | Reservation scope (`EstimateWorstCaseTurnUsd()` — per-call max-not-sum through `CostCalculator.CalculateCost()`); reserve/raise/reconcile (`ReserveAsync()` / `AdjustAsync()` / `ReconcileAsync()`); turn lifecycle (`BeginAsync()` / `EnsureReservationForContextAsync()` / usage recording / `CompleteAsync()`). |
| `IdempotencyEndpointFilters` / `IdempotencyClaimStore` (`Api/Security/` / `Infrastructure/Data/`) | Durable acquisition (`TryAcquireAsync()` before execution); Running-claim lease renewal (`HeartbeatAsync()`); terminalization (`CompleteAsync()` / `MarkAbandonedAsync()`); replay gate (`TryBuildReplay()` → `IdempotencyReplayResult` only for a completed terminal in-cap body); process-local single-flight (`LocalFlight`); fail-open only after safe claim handling. |
| `InferenceExecuteWriter` (`Api/TheForge/`) | Buffered/NDJSON writers (`WriteBufferedAsync()` / `WriteStreamAsync()`); sanitized caught-stream failure (`PublicStreamFailureMessage`); terminal marking (`TurnContextGuards.MarkIdempotencyTerminal()`); exact-byte capture delegated to `IdempotencyBufferingStream`. |
| `PublicInferenceErrorMessages` / `OpenAiStreamErrorMapper` / `ArcanumErrorMapper` (`Api/` + `TheForge/`) | Stable error copy (`NativeGenericFailure` / `OpenAiGenericFailure`); `Hub.Model` maps to 404 and OpenAI `model_not_found`; `Session.RestQueueFull` maps to 503; `Security.IdempotencyInProgress` / `IdempotencyConflict` map to 409; no raw exception leakage. |
| `OpenAiRequestAugmentingHandler` / `ProviderHealthProbe` / `WebhookCommLinkDispatcher` | Headers-first response handling; 64 KiB strict-compatibility diagnostic prefix; caller-cancellation propagation; health status without body reads; capped webhook draining. |
| `CliSessionManager` / `ArcanumPaths` | Session isolation (`ARCANUM_TEST_HOME` isolation; no developer storage access); session identity; diagnostic preview (`CliSessionManagerTests` avoids corrupt preview by reading identity, not untrusted content). |
| `WizardIntelligenceProvider.ContextPreview` / `ContextCommands` | Read-only effective-turn assembly, including unified-run forced Spell, preview-only text/image context, research system/tool policy, output reserve, and sampling options; no turn coordinator, attachment persistence, assistant Entry, tool invocation, budget reservation, or main model call; default content redaction; `noRetrieval` embedding bypass; typed CLI/API output. |
| `RunInputReader` / `RunAttachmentStager` / `RunCommand` / `RunExecutionDispatcher` | Positional/pipe/one-line interactive composition; exact 10 MiB UTF-8 stdin admission with no partial result; repeatable relative or explicit absolute `--with @path`; strict text without an extension allowlist; shared 32-part/32-MiB text aggregate authority rather than a 10-MiB per-file limit; Scrying image policy; UTF-8-safe server chunking and SHA-256 metadata; active-context resolution; Agent/research/exact-or-unique-prefix Spell selection; dry-run handoff; only the research-plus-Spell conflict. |
| `ResourceSelector<T>` / `CliResourceCatalog` / `RecentResourceStore` | Resolution precedence (ID, exact name, unique prefix), ambiguity diagnostics, TTY/`--json` prompt suppression, bounded API page-token progression, cancellation before mutation, safe descriptor columns, recency ordering without authority, and owner-only durable staging with unconditional failure cleanup. |
| `CliApplicationFactory` / `CliInvocationContext` / `ConsoleDispatcher` | Recursive `--json`/`--plain`/`--yes` binding; JSON stdout capture and typed-output bypass; ANSI suppression; stdout payload vs stderr diagnostic routing; exit-code normalization; fixed-copy exception mapping. |
| `WatchCommands` / `WatchEventView` / `ArcanumApiClient` | Six-source watch routing; Session/Apprentice selection; authenticated SSE parsing; multi-line `data:` assembly; heartbeat/`[DONE]`/unexpected-EOF classification; UTC/color projection; free-form event/tool filtering; pure NDJSON stdout; stderr diagnostics; health 503 snapshot parsing; reconnect cursor/gap/backoff; Ctrl+C exit 130. |
| `ConfigCommands` / `ConfigurationCommandService` / `ConfigurationPathAccessor` / `ConfigurationWriter` | Host-API versus local-bootstrap selection; generated-metadata dot-path resolution; typed parse; provider-endpoint secure input/redaction; full-snapshot validation; owner-only editor temp file; atomic write; environment-override diagnostics. Inspect `ConfigurationWriter.UpdateAsync()` for the serialized latest-file read/validate/write boundary and `ConfigurationEndpoints.ResolveCurrentSettings()` for the latest persisted API/model/provider projection. |
| `ArcanumConfigurationStore` / `LocalCertificateGenerator` (`Compendium.Ux/Services/`) | 10 MiB read admission before parse; owner-only durable configuration staging and `finally` cleanup; collision-resistant certificate pair names; staged no-overwrite pair publication and rollback. |
| `ConfirmationPrompt` | `--yes` short circuit; redirected-output fail-closed check before prompt or input read; stderr prompt copy and cancellation-aware input. |
| `ChildProcessFilesystemJail` / `CappedChildProcessRunner` (`Infrastructure/Process/`) | Nonblocking/no-follow process-group launch (`setsid` / direct target group, no blocking FIFO before check); handle-bound child termination; identity-owned cleanup; timeout vs cancellation event; best-effort descendant cleanup. |
| `GrimoireRepository` / `EntryWindowPolicy` / `SqliteBusyRetry` (`Infrastructure/Repositories/`) | Timestamp-group load (expanded CTE covering full tied group before advancing); bounded query limit (`Limit` / `MaxEntriesPerSession`); rollup updates (`CampaignBackedWorkspaceRegistry`); parameterized filtering/ordering/capping; bookmark advance only when complete group is below ceiling; `BEGIN IMMEDIATE` retry. |
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
6. **Inspect isolated delegation:** break at `ArcanumDelegateTaskTool.InvokeCoreAsync()` and `SubagentRunner.RunAsync()`. Confirm the child `PingRequest` has no `SessionId`, workspace, context snapshot, Chronosync, campaign, data streams, tools, or retrieval; inspect `SubagentExecutionAmbient.Depth`; then verify `ModelCallExecutor.RecordDelegatedUsage()` charges provider usage before `ThrowIfExhausted()` ends the loop and the durable `subagent` row reaches `Completed` or `Failed`.
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
   population from migrations or connection close behavior. If this fails only in a larger run,
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
    `SagaExtractionService.ProcessAsync()`; an id absent from the current-turn allowlist must be
    rejected before durable write or embedding. Inspect
    `lexicon_fact_attachment_provenance`, `saga_memory_attachment_provenance`, and
    `attachment_memory_consultations`: they contain typed metadata only. Delete the source row and
    verify readers retain the provenance with `SourceAvailability=Unavailable`. Campaign Logger
    prompts, inference audit JSONL, stable prompt-cache segments, and child requests must contain no
    attachment bytes, excerpt text, host path, or hash.
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
    `arcanum-internal`, blocked names, untrusted workspaces, server ambiguity, timeout, and output
    truncation remain server-enforced. Safe CLI output must omit command, URL, arguments,
    environment, and secret values.
19. **Trace first-class web workflows:** run `arcanum research "question" --max-sources 2
    --max-hops 2 --format markdown`, break in `WebWorkflowCommands.Research()`,
    `ArcanumApiClient.ResearchWebAsync()`, and `WebResearchWorkflowService.ResearchAsync()`. Confirm
    the CLI only consumes NDJSON and never performs a search, fetch, or model call itself. On the
    server, verify the `limits` frame precedes `searching`, citation URLs deduplicate before
    `fetching`/`rendering`, and the synthesis `PingRequest` has `DisableAllTools=true`, the requested
    `MaxOutputTokens`, and the resolved continuation `SessionId`. Redirect stdout and stderr
    separately: only final terminal/Markdown/JSON belongs on stdout. For `browse --render
    javascript`, confirm no provider call occurs and the response is 503
    `WebResearch.JavaScriptRenderingUnavailable` with the `--render static` hint. For domain
    filters, inspect the Perplexity request for `search_recency_filter` and bounded
    `search_domain_filter`; never log the query, URL, page content, or credential.
20. **Trace native file/batch automation:** run `arcanum batch create ./input.jsonl`, break in
    `FileBatchCommands.ValidateBatchJsonlAsync()`, `FileBatchApiClient.UploadFileAsync()`, and
    `FileBatchApiClient.CreateBatchAsync()`. Confirm an obvious wrapper failure reports its local
    line and sends no request, while a valid file streams first to `/v1/files` and then submits only
    the returned id to `/v1/batches`. For `batch watch`, break after each `GetBatchAsync()` and
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

## Related documents

- Architecture and design source of truth: [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md)
- API source of truth: [`Arcanum.API.md`](Arcanum.API.md)
- Human navigation guide: [`Arcanum.Design.Human.md`](Arcanum.Design.Human.md)
- Agent/operator primer: [`Arcanum.README.md`](Arcanum.README.md)
- Complete configuration reference: [`Compendium.README.md`](Compendium.README.md)
- The breakpoints above are verified against the code. Correct any discrepancy here and update the
  owning architecture, API, or configuration document when its contract also changed.
