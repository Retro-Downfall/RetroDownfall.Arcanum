# Arcanum — Developer Debugging Guide

This is the verified breakpoint and debugging recipe guide for developers working on the
RetroDownfall.Arcanum repository. It complements `Arcanum.DESIGN.md` (authoritative contracts) and
`Arcanum.Design.Human.md` (conceptual navigation) rather than duplicating them. Every class and
method referenced below exists in the current source; nothing here is speculative.

## When to use this guide

Use this document when you need to understand how to debug a failing endpoint, trace an inference
turn, verify workspace tool containment, inspect persistence behavior, or confirm a security
boundary. For architecture decisions, read `DESIGN.md`. For a quick overview, read
`Arcanum.Design.Human.md`. For the complete `arcanum.json` reference, see `Compendium.README.md`.

## Running Arcanum under a debugger

- **Host (`arcanum serve`)**: `ServeCommand.Run()` (`Program.cs`) → `WebApplication.CreateSlimBuilder()` (`ServeCommand`) → `AddArcanumApiServices()` (`ApiBootstrapper`) → `MapArcanumEndpoints()` → `RunAsync()`.
- **CLI verbs** (`ask`, `chat`, `session`, `workspace`, `mcp`, `tool`, `file`, `batch`, `look`, `lore`, `daemon`, `key`, `config`, `serve`): native request/response cycles use `ArcanumApiClient.SendRequestAsync()`; session SSE uses `WatchSessionAsync()`. `file`/`batch` are the deliberate exception: `FileBatchApiClient` parses bare OpenAI success objects, streams multipart/content bodies, and never expects `ApiResponse<T>`. Inspect `ApiBootstrapper.MapArcanumEndpoints()` for endpoint wiring. `session` lifecycle commands must remain HTTP-only; debug selection in `CliResourceCatalog`, command routing in `SessionCommands`, and feature-gate failures at `SessionEndpoints`. Workspace `tree`/`info`/`read`/`search`/index inspection must also remain HTTP-only; start in `WorkspaceCommands`, then its typed `ArcanumApiClient` method, and finally the `/api/workspaces` endpoint. MCP lifecycle/diagnostic commands are likewise HTTP-only: start in `McpCommands` or `ToolCommands`, inspect `ToolArgumentReader` and `ResourceSelector<T>`, then continue into `McpEndpoints`, `DiagnosticMcpInvocationEndpoints`, or `ToolInvokeEndpoints`. `config` prefers `/api/config` but deliberately enters labelled local bootstrap through `ConfigurationCommandService` on unavailability. `data encryption ...` is intentionally local: `DataEncryptionCommands` initializes the Grimoire and calls `BlobEncryptionLifecycleService`.
- **CLI process contract**: start at `CliApplicationFactory.RunAsync()` → `CliCommandTree.Build()` →
  `CliInvocationContext.Push()`. Payload/diagnostic routing is `ConsoleDispatcher`; destructive
  approval is `ConfirmationPrompt`; final failure categorization is `CliFailureMapper`.
- **Native AOT**: `dotnet build -c Release` on Windows/Linux produces the trimmed image; macOS uses folder-based publish (`Directory.Build.props`).
- **Debug-only host**: `Arcanum.Api.DevHost/Program.cs` mirrors the serve wiring but does not ship.

## Breakpoint map (verified load-bearing classes)

| Path | What to inspect at a breakpoint |
|---|---|
| `WizardIntelligenceProvider` (`Api/Intelligence/`) | Turn entry (`ValidateReasoningForCandidate()`); provider resolution (`ProviderResolver.ResolveCandidates()`); model-call execution (`ModelCallExecutor`); context admission (`BuildModelCallContext()`); streaming writer (`InferenceExecuteWriter.WriteStreamAsync()`); interrupted/finalized cleanup (`GrimoireTurnWriter.ResolveInterruptedAndMarkFinalizedAsync()`); audit (`WriteAuditRecordAsync()`). |
| `TurnExecutionCoordinator` / `TurnEngine` (`Api/Intelligence/TurnEngine/`) | Semantic source (`ITurnEventSource.RunTurnAsync()`); projection selection (`ITurnPipelineRunner`); commitment tracking (`ProviderAttemptCommitTracker`); budget admission (`TurnAccountingAmbient`); event emission (`TurnEventEmitter`). |
| `ArcanumDelegateTaskTool` / `SubagentRunner` / `DelegatedManaTracker` (`Api/Intelligence/Tools/` + `Subagents/`) | Parent tool arguments; sterile stateless child request; attachment ids intersected with the parent's current-turn materialized allowlist; `MaxSubagentDepth = 1`; provider-call charging; exact budget failure; `subagent` durable operation completion/failure; single terminal telemetry roll-up. |
| `ToolExecutionPipeline` (`Api/Intelligence/`) | Preflight (`ManaPreflight`), attunement (`BuiltInToolRegistry`), `WardedToolExecution`, `PublicToolFailureMessage`, structured-error logging; shared attachment refresh core used by `ProcessRefreshSessionFileAsync()` and operator `RefreshSessionAttachmentAsync()` for selection, hidden-source Sanctum, MIME/model policy, persistence, structured result, and optional queued injection. |
| `AttachmentSourceResolver` / `SessionAttachmentStore` | Refresh path reconstruction from encrypted provenance; canonical/link and path-vs-handle identity; double-read stability; session-scoped `RevalidateBoundSourcesAsync()` for authoritative list badges; `PersistRefreshedAsync()` hash reuse/new version under existing gates and byte/version budgets. |
| `AttachmentMemoryGateAmbient` / `AttachmentMemoryProvenanceStore` | Current-turn promotion authority across provider/tool tasks; typed session/attachment/key/version/hash/materialized-time/source metadata; metadata-only consultation persistence; dynamic Available/Unavailable source status. |
| `SessionContextPinMaterializer` | File/symbol lexical containment; `SecureFileReader.TryOpenRegularFile()` no-follow single-link admission; 64 MiB source ceiling; incremental full-handle SHA-256 with bounded retained content; streamed line-range and CRLF normalization. |
| `CommandCenterAttachmentDriftMonitor` / `ShellCommandDispatcher` | Debounced workspace `FileSystemWatcher` invalidation; authenticated attachment-list revalidation; backend-only Snapshot/Live/Stale transitions; loaded/disk hash rendering; `/attachments refresh <name>` confirmation. No UI-thread hashing or client-side Live assumption. |
| `GrimoireTurnWriter` (`Api/Intelligence/`) | Turn creation (`TryBeginBufferedAssistantReplyAsync()`); interruption (`ResolveInterruptedAsync()` / `ResolveInterruptedAndMarkFinalizedAsync()`); audit writing. |
| `SessionEntryPersistence` / `GrimoireRepository` (`Infrastructure/Repositories/`) | Write-lock (`SessionWriteLock.AcquireAsync()`); busy retry (`SqliteBusyRetry`); append (`AppendMandatoryToolInteractionAsync()`); summarization (`GetUnsummarizedEntriesAsync()`); rollup (`CampaignBackedWorkspaceRegistry`). |
| `CampaignBackedWorkspaceRegistry` / `CampaignRepository` | Registry capacity (`Campaign.MaxReached`); workspace resolution; typed `ICampaignRepository.AddAsync()` return. |
| `WorkspaceIndexingService` / `WorkspaceFileWatcher` / `WorkspaceCodeChunker` | Watcher admission (`EnsureWatcher()`), bounded coalescing (`QueueWatcherChange()`), overflow recovery (`HandleWatcherError()` → `ReconcileWorkspaceAsync()`), final-state incremental processing (`ProcessPendingWatcherEventsAsync()`), path/handle identity revalidation (`IndexFileAsync()`), stable chunk/embedding reuse, and watcher disposal on workspace unregister or host stop. Inspect `/api/workspaces/{id}/files/index/status` for `Watching`, `Degraded`, `Overflowed`, `Reconciling`, last event, and last successful index. |
| `WorkspaceCommands` / `ArcanumApiClient` / `CliContextService` | Complete workspace CLI routing; explicit/saved/current-directory selector precedence; independent Workspace/Campaign containment; server-host path copy; registration guidance; typed `/api/workspaces` calls. Confirm no direct file I/O enters the CLI command handler. |
| `FileBatchCommands` / `FileBatchApiClient` | Bare OpenAI JSON parsing; multipart upload; line-aware local JSONL wrapper preflight; one-command upload/create; bounded terminal polling; output/error artifact resolution; filename sanitization; same-directory atomic download; overwrite confirmation; request-count display. |
| `McpConnectionManager` / `TrustedMcpWorkspaceStore` (`Infrastructure/Mcp/`) | Digest-bound admission (`IsApprovedDigestAsync()` / `TrustAsync()`); bounded config load (`SecureFileReader` cap `MaxMcpConfigBytes`); lifecycle (`StartAsync()` / `RestartAsync()`); retirement/replacement ordering; identity-owned cleanup. |
| `AtomicFile` / `SecureFileReader` / `PhysicalFileSystemBrowser` (`Infrastructure/Storage/` / `Security/`) | Handle-bound open (`O_NOFOLLOW` / `NONBLOCK` / `O_CLOEXEC`); identity revalidation (`FileHandleIdentity`); bounded read (`ArrayPool<byte>`); rollback (backup fingerprint verification); identity-owned temp/backup deletion. |
| `BlobEncryptionLifecycleService` / `BlobEncryptionFileProcessor` / `BlobEncryptionMetadataStore` | Candidate inventory; metadata-versus-envelope classification; pre/post plaintext length and SHA-256 verification; atomic replacement; replace-before-metadata retry; bounded worker/throttle; durable checkpoints; retained-key retirement gate. Use `arcanum operation show <id>` for safe progress and `arcanum data encryption verify` for aggregate reconciliation categories. |
| `BudgetReservationService` | Reservation scope (`EstimateWorstCaseTurnUsd()` — max-not-sum; `SaturatingMultiply` / `SaturatingAdd` for multi-call); reservation reconciliation; timestamp-group load; rollup updates. |
| `IdempotencyEndpointFilters` / `IdempotencyClaimStore` (`Api/Security/` / `Infrastructure/Data/`) | Durable claim (`DurableClaim` before execution); lease renewal (`HeartbeatAsync()`); replay (`ReplayEligibleAsync()` — only `Complete` terminal in-cap claims replay); single-flight (`ConcurrentDictionary` coordinator); fail-open after owner release for terminal response only. |
| `InferenceExecuteWriter` (`Api/TheForge/`) | NDJSON writer (`WriteStreamAsync()`); timeout (`PublicStreamTimeoutMessage` mapped to `Hub.Timeout` for `/v1` streaming); sanitized failure (`PublicStreamFailureMessage` mapped to native generic failure); exact-byte capture (`IdempotencyBufferingStream`); replay eligibility (`ReplayResponse` / `ReplayEligibleAsync` / `Complete`). |
| `PublicInferenceErrorMessages` / `OpenAiStreamErrorMapper` / `ArcanumErrorMapper` (`Api/` + `TheForge/`) | Stable error copy (`NativeGenericFailure` / `OpenAiGenericFailure`); `Hub.Model` / `Hub.Timeout` mapped to 503 for `/v1` streaming; `Session.RestQueueFull` mapped to 503; `Security.IdempotencyInProgress` mapped to 409; `Security.IdempotencyConflict` mapped to 409; no raw exception leakage. |
| `OpenAiRequestAugmentingHandler` / `ProviderHealthProbe` / `WebhookCommLinkDispatcher` | Headers-first response handling; 64 KiB strict-compatibility diagnostic prefix; caller-cancellation propagation; health status without body reads; capped webhook draining. |
| `CliSessionManager` / `ArcanumPaths` | Session isolation (`ARCANUM_TEST_HOME` isolation; no developer storage access); session identity; diagnostic preview (`CliSessionManagerTests` avoids corrupt preview by reading identity, not untrusted content). |
| `ResourceSelector<T>` / `CliResourceCatalog` / `RecentResourceStore` | Resolution precedence (ID, exact name, unique prefix), ambiguity diagnostics, TTY/`--json` prompt suppression, bounded API page-token progression, cancellation before mutation, safe descriptor columns, recency ordering without authority, and owner-only durable staging with unconditional failure cleanup. |
| `CliApplicationFactory` / `CliInvocationContext` / `ConsoleDispatcher` | Recursive `--json`/`--plain`/`--yes` binding; JSON stdout capture and typed-output bypass; ANSI suppression; stdout payload vs stderr diagnostic routing; exit-code normalization; fixed-copy exception mapping. |
| `ConfigCommands` / `ConfigurationCommandService` / `ConfigurationPathAccessor` | Host-API versus local-bootstrap selection; generated-metadata dot-path resolution; typed parse; provider-endpoint secure input/redaction; full-snapshot validation; owner-only editor temp file; atomic write; environment-override diagnostics. |
| `ArcanumConfigurationStore` / `LocalCertificateGenerator` (`Compendium.Ux/Services/`) | 10 MiB read admission before parse; owner-only durable configuration staging and `finally` cleanup; collision-resistant certificate pair names; staged no-overwrite pair publication and rollback. |
| `ConfirmationPrompt` | `--yes` short circuit; redirected-output fail-closed check before prompt or input read; stderr prompt copy and cancellation-aware input. |
| `ChildProcessFilesystemJail` / `CappedChildProcessRunner` (`Infrastructure/Process/`) | Nonblocking/no-follow process-group launch (`setsid` / direct target group, no blocking FIFO before check); handle-bound child termination; identity-owned cleanup; timeout vs cancellation event; best-effort descendant cleanup. |
| `GrimoireRepository` / `EntryWindowPolicy` / `SqliteBusyRetry` (`Infrastructure/Repositories/`) | Timestamp-group load (expanded CTE covering full tied group before advancing); bounded query limit (`Limit` / `MaxEntriesPerSession`); rollup updates (`CampaignBackedWorkspaceRegistry`); parameterized filtering/ordering/capping; bookmark advance only when complete group is below ceiling; `BEGIN IMMEDIATE` retry. |
| `GrimoireDatabaseBootstrapper` / `GrimoireDatabaseUnavailableException` | Dedicated-secret read status; no API-key fallback after a present-but-corrupt secret; sanitized controlled startup failure; normal hosted-service/test cleanup rather than process termination. |
| `ArcanumMasterKeyBootstrapper` / `MasterApiKeyUnavailableException` | Corrupt master-key state with/without an existing Grimoire; sanitized fixed recovery copy; no replacement generation when data exists; controlled startup failure rather than process termination. |
| `DataProtectionSecretStore` / `WebResearchCredentialStore` | `SecureFileReader` admission for protected credential mirrors; no-follow single-link regular-file identity; 64 KiB ciphertext ceiling; rejected/oversized/undecryptable input mapped to corrupt without secret leakage. |
| `GrimoireKdfSidecarFile` | No-follow single-link sidecar admission; 4 KiB read ceiling; owner-only durable staging; atomic replacement and failure cleanup. |

## Debugging recipes

1. **Trace one turn end-to-end:** break at `WizardIntelligenceProvider.ExecutePromptAsync()` (resolution loop), `TurnExecutionCoordinator` step, `TurnEngine` event emission (`Status`, `SessionBound`, `Reasoning`, `Token`, `Error`), `InferenceExecuteWriter.WriteStreamAsync()` NDJSON serialization, replay writer (`ReplayResponse` / `ReplayEligibleAsync` / `Complete`). Confirm sequence-based order (`Sequence` ordering, not `CreatedAt DESC, Id DESC`) in replayed results.
2. **Inspect replay/claim behavior:** `IdempotencyClaimStore.TryAcquireAsync()` (durable claim before execution), `HeartbeatAsync()` lease renewal (`Claimed` state never executes/replays; only `Running` claims renew), `ReplayEligibleAsync()` (only `Complete` terminal in-cap claims replay; empty-terminal replay; Abandoned for partial/non-claimed/past-cap-abandoned), `DurableClaim` / `IdempotencyLocalFlight` coordinator behavior.
3. **Inspect workspace-local MCP admission:** `McpConnectionManager.Config` (`BuildMergedToolsForWorkspaceAsync()` bounded `SecureFileReader` read + digest parse), `TrustedMcpWorkspaceStore.IsApprovedDigestAsync()` / `TrustAsync()` (digest on `ManagedMcpServerEntry`), retirement/replacement (`StartAsync()` blocked after retirement), identity-owned cleanup (`IdentityOwnedFileSystemCleanup`). Confirm digest matching, retirement ordering, cleanup tracking.
4. **Inspect filesystem identity / cleanup:** `SecureFileReader` nonblocking open (`O_NOFOLLOW` / `NONBLOCK` / `O_CLOEXEC`); `FileHandleIdentity` metadata validation; `IdentityOwnedFileSystemCleanup` quarantine/re-check/deletion; `AtomicFile` rollback (backup fingerprint verification, identity capture required for backup identity).
5. **Inspect reservation/accounting lifecycle:** `BudgetReservationService.EstimateWorstCaseTurnUsd()` (per-call max-not-sum); `TurnAccountingHandle.ReserveTurnBudget()` / `EstablishClampedSnapshot()`; `TurnAccountingHandle` lease renewal; `GrimoireRepository` timestamp-group summarization; `SqliteBusyRetry` busy/locked retry with fresh transaction per retry and cancellation-observed exit.
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
   `finally`.
10. **Diagnose API-host/WAL CI teardown failures:** run
   `ArcanumWebApplicationFactoryDisposalTests` and
   `GrimoireDatabaseBootstrapperTests.CheckpointOnShutdownAsync_truncates_populated_wal_when_no_readers_hold_it`
   together several times. For path mismatches, break in
   `ArcanumWebApplicationFactory.StopApplicationHostAsync()` and the hosted-service `StopAsync`;
   the captured host must finish stopping before `DisposeIsolatedResources()` restores
   `ARCANUM_TEST_HOME`. For an empty pre-checkpoint WAL, verify the test's pooling-disabled owner is
   still open, `wal_autocheckpoint` is zero, and no read transaction is active; do not infer WAL
   population from migrations or connection close behavior.
11. **Verify bounded untrusted I/O:** run `RequestAugmentingHandlerTests`,
    `ProviderHealthProbeTests`, `WebhookCommLinkDispatcherTests`, and
    `SessionContextPinMaterializerTests`. Break at the first response/file stream read. Confirm a
    health response body is untouched, strict fallback stops after 64 KiB, webhook drain stops at
    its cap, caller cancellation propagates, and a context pin never retains more than its output
    limit even while hashing the accepted source handle.
12. **Diagnose SSE heartbeat concurrency tests:** `SseStreamWriterTests` deliberately holds the first
    `MoveNextAsync` pending until `WriteSignalStream` observes a keep-alive write. If it stalls, inspect
    the pending-move reuse in `SseStreamWriter.StreamAsync`; do not replace the signal with short
    scheduler delays or a cancellation token that can expire while coverage suspends the process.
13. **Trace `refresh_session_file`:** begin at the internal MCP selector/schema, then break at
    `ToolExecutionPipeline.ProcessRefreshSessionFileAsync()`,
    `AttachmentSourceResolver.ResolveCurrentAsync()`, and
    `SessionAttachmentStore.PersistRefreshedAsync()`. Confirm the selected id/key was visible at
    turn start; the model supplied no path; Sanctum sees the actual canonical path before either
    handle read; both reads hash identically; unchanged content reuses the row; changed content
    creates one next version; `TryBuildRefreshedContentsAsync()` consumes inject-once only after
    materialization; and the User extras follow every tool result in the round. Native streams emit
    `attachmentRefreshed` after `toolResult`; OpenAI streams omit it.
    For an operator refresh, start at `ShellCommandDispatcher.RefreshAttachmentAsync()`, continue
    through `POST /api/sessions/{id}/attachments/{attachmentId}/refresh` and
    `ToolExecutionPipeline.RefreshSessionAttachmentAsync()`, and confirm the same resolver/persistence
    core runs with injection disabled. For drift, edit a tracked source and break in
    `CommandCenterAttachmentDriftMonitor.PumpAsync()` and `RevalidateBoundSourcesAsync()`; the watcher
    callback must not hash or set Live itself.
14. **Trace attachment extraction and retrieval:** break at
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
15. **Trace attachment-derived memory promotion:** start with
    `AttachmentMemoryGateAmbient.RegisterMaterialized()` and confirm only successful ledger or
    attach/refresh materialization publishes an opaque attachment id. Continue through
    `ProcessScribeLexiconAsync()` and `SagaExtractionService.ProcessAsync()`; an id absent from the
    current-turn allowlist must be rejected before durable write or embedding. Inspect
    `lexicon_fact_attachment_provenance`, `saga_memory_attachment_provenance`, and
    `attachment_memory_consultations`: they contain typed metadata only. Delete the source row and
    verify readers retain the provenance with `SourceAvailability=Unavailable`. Campaign Logger
    prompts, inference audit JSONL, stable prompt-cache segments, and child requests must contain no
    attachment bytes, excerpt text, host path, or hash.
16. **Trace Workspace/Campaign CLI mapping:** run `arcanum workspace current` inside a registered
    root, break in `WorkspaceCommands.Current()`, and compare its deepest containing Workspace and
    Campaign independently. Continue through `ResolveWorkspaceAsync()` for a file/search/index
    command and verify the final operation is an authenticated `ArcanumApiClient` request. For a
    remote-host thought experiment, use a path that is valid only on the server and confirm help and
    output call it a server path; never add `File.*` or `Directory.*` content access to the CLI.
17. **Trace MCP/tool CLI administration:** run `arcanum mcp show` or `arcanum tool list`, break in
    `McpCommands` / `ToolCommands`, and confirm every operation reaches a typed `ArcanumApiClient`
    request. For invocation, test inline JSON, `@file`, and redirected stdin at
    `ToolArgumentReader.TryRead()`; oversized/non-object input must fail before the API call. Follow
    external invocation through `DiagnosticMcpInvocationService` and confirm
    `arcanum-internal`, blocked names, untrusted workspaces, server ambiguity, timeout, and output
    truncation remain server-enforced. Safe CLI output must omit command, URL, arguments,
    environment, and secret values.
18. **Trace first-class web workflows:** run `arcanum research "question" --max-sources 2
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
19. **Trace native file/batch automation:** run `arcanum batch create ./input.jsonl`, break in
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

## Related documents

- Authoritative contracts: [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md)
- Human navigation guide: [`Arcanum.Design.Human.md`](Arcanum.Design.Human.md)
- Agent/operator primer: [`Arcanum.README.md`](Arcanum.README.md)
- Complete configuration reference: [`Compendium.README.md`](Compendium.README.md)
- Source of truth for `DEBUGGING.Human.md`: the verified breakpoints above are drawn directly from the code; any discrepancy should be corrected in this file and then verified against `DESIGN.md` §13–§18.
