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
- **CLI verbs** (`ask`, `chat`, `look`, `lore`, `daemon`, `key`, `serve`): request/response cycles through `ArcanumApiClient.SendRequestAsync()`; inspect `ApiBootstrapper.MapArcanumEndpoints()` for endpoint wiring.
- **Native AOT**: `dotnet build -c Release` on Windows/Linux produces the trimmed image; macOS uses folder-based publish (`Directory.Build.props`).
- **Debug-only host**: `Arcanum.Api.DevHost/Program.cs` mirrors the serve wiring but does not ship.

## Breakpoint map (verified load-bearing classes)

| Path | What to inspect at a breakpoint |
|---|---|
| `WizardIntelligenceProvider` (`Api/Intelligence/`) | Turn entry (`ValidateReasoningForCandidate()`); provider resolution (`ProviderResolver.ResolveCandidates()`); model-call execution (`ModelCallExecutor`); context admission (`BuildModelCallContext()`); streaming writer (`InferenceExecuteWriter.WriteStreamAsync()`); interrupted/finalized cleanup (`GrimoireTurnWriter.ResolveInterruptedAndMarkFinalizedAsync()`); audit (`WriteAuditRecordAsync()`). |
| `TurnExecutionCoordinator` / `TurnEngine` (`Api/Intelligence/TurnEngine/`) | Semantic source (`ITurnEventSource.RunTurnAsync()`); projection selection (`ITurnPipelineRunner`); commitment tracking (`ProviderAttemptCommitTracker`); budget admission (`TurnAccountingAmbient`); event emission (`TurnEventEmitter`). |
| `ArcanumDelegateTaskTool` / `SubagentRunner` / `DelegatedManaTracker` (`Api/Intelligence/Tools/` + `Subagents/`) | Parent tool arguments; sterile stateless child request; `MaxSubagentDepth = 1`; provider-call charging; exact budget failure; `subagent` durable operation completion/failure; single terminal telemetry roll-up. |
| `ToolExecutionPipeline` (`Api/Intelligence/`) | Preflight (`ManaPreflight`), attunement (`BuiltInToolRegistry`), `WardedToolExecution`, `PublicToolFailureMessage`, structured-error logging. |
| `GrimoireTurnWriter` (`Api/Intelligence/`) | Turn creation (`TryBeginBufferedAssistantReplyAsync()`); interruption (`ResolveInterruptedAsync()` / `ResolveInterruptedAndMarkFinalizedAsync()`); audit writing. |
| `SessionEntryPersistence` / `GrimoireRepository` (`Infrastructure/Repositories/`) | Write-lock (`SessionWriteLock.AcquireAsync()`); busy retry (`SqliteBusyRetry`); append (`AppendMandatoryToolInteractionAsync()`); summarization (`GetUnsummarizedEntriesAsync()`); rollup (`CampaignBackedWorkspaceRegistry`). |
| `CampaignBackedWorkspaceRegistry` / `CampaignRepository` | Registry capacity (`Campaign.MaxReached`); workspace resolution; typed `ICampaignRepository.AddAsync()` return. |
| `WorkspaceIndexingService` / `WorkspaceFileWatcher` / `WorkspaceCodeChunker` | Watcher admission (`EnsureWatcher()`), bounded coalescing (`QueueWatcherChange()`), overflow recovery (`HandleWatcherError()` → `ReconcileWorkspaceAsync()`), final-state incremental processing (`ProcessPendingWatcherEventsAsync()`), path/handle identity revalidation (`IndexFileAsync()`), stable chunk/embedding reuse, and watcher disposal on workspace unregister or host stop. Inspect `/api/workspaces/{id}/files/index/status` for `Watching`, `Degraded`, `Overflowed`, `Reconciling`, last event, and last successful index. |
| `McpConnectionManager` / `TrustedMcpWorkspaceStore` (`Infrastructure/Mcp/`) | Digest-bound admission (`IsApprovedDigestAsync()` / `TrustAsync()`); bounded config load (`SecureFileReader` cap `MaxMcpConfigBytes`); lifecycle (`StartAsync()` / `RestartAsync()`); retirement/replacement ordering; identity-owned cleanup. |
| `AtomicFile` / `SecureFileReader` / `PhysicalFileSystemBrowser` (`Infrastructure/Storage/` / `Security/`) | Handle-bound open (`O_NOFOLLOW` / `NONBLOCK` / `O_CLOEXEC`); identity revalidation (`FileHandleIdentity`); bounded read (`ArrayPool<byte>`); rollback (backup fingerprint verification); identity-owned temp/backup deletion. |
| `BudgetReservationService` | Reservation scope (`EstimateWorstCaseTurnUsd()` — max-not-sum; `SaturatingMultiply` / `SaturatingAdd` for multi-call); reservation reconciliation; timestamp-group load; rollup updates. |
| `IdempotencyEndpointFilters` / `IdempotencyClaimStore` (`Api/Security/` / `Infrastructure/Data/`) | Durable claim (`DurableClaim` before execution); lease renewal (`HeartbeatAsync()`); replay (`ReplayEligibleAsync()` — only `Complete` terminal in-cap claims replay); single-flight (`ConcurrentDictionary` coordinator); fail-open after owner release for terminal response only. |
| `InferenceExecuteWriter` (`Api/TheForge/`) | NDJSON writer (`WriteStreamAsync()`); timeout (`PublicStreamTimeoutMessage` mapped to `Hub.Timeout` for `/v1` streaming); sanitized failure (`PublicStreamFailureMessage` mapped to native generic failure); exact-byte capture (`IdempotencyBufferingStream`); replay eligibility (`ReplayResponse` / `ReplayEligibleAsync` / `Complete`). |
| `PublicInferenceErrorMessages` / `OpenAiStreamErrorMapper` / `ArcanumErrorMapper` (`Api/` + `TheForge/`) | Stable error copy (`NativeGenericFailure` / `OpenAiGenericFailure`); `Hub.Model` / `Hub.Timeout` mapped to 503 for `/v1` streaming; `Session.RestQueueFull` mapped to 503; `Security.IdempotencyInProgress` mapped to 409; `Security.IdempotencyConflict` mapped to 409; no raw exception leakage. |
| `CliSessionManager` / `ArcanumPaths` | Session isolation (`ARCANUM_TEST_HOME` isolation; no developer storage access); session identity; diagnostic preview (`CliSessionManagerTests` avoids corrupt preview by reading identity, not untrusted content). |
| `ChildProcessFilesystemJail` / `CappedChildProcessRunner` (`Infrastructure/Process/`) | Nonblocking/no-follow process-group launch (`setsid` / direct target group, no blocking FIFO before check); handle-bound child termination; identity-owned cleanup; timeout vs cancellation event; best-effort descendant cleanup. |
| `GrimoireRepository` / `EntryWindowPolicy` / `SqliteBusyRetry` (`Infrastructure/Repositories/`) | Timestamp-group load (expanded CTE covering full tied group before advancing); bounded query limit (`Limit` / `MaxEntriesPerSession`); rollup updates (`CampaignBackedWorkspaceRegistry`); parameterized filtering/ordering/capping; bookmark advance only when complete group is below ceiling; `BEGIN IMMEDIATE` retry. |

## Debugging recipes

1. **Trace one turn end-to-end:** break at `WizardIntelligenceProvider.ExecutePromptAsync()` (resolution loop), `TurnExecutionCoordinator` step, `TurnEngine` event emission (`Status`, `SessionBound`, `Reasoning`, `Token`, `Error`), `InferenceExecuteWriter.WriteStreamAsync()` NDJSON serialization, replay writer (`ReplayResponse` / `ReplayEligibleAsync` / `Complete`). Confirm sequence-based order (`Sequence` ordering, not `CreatedAt DESC, Id DESC`) in replayed results.
2. **Inspect replay/claim behavior:** `IdempotencyClaimStore.TryAcquireAsync()` (durable claim before execution), `HeartbeatAsync()` lease renewal (`Claimed` state never executes/replays; only `Running` claims renew), `ReplayEligibleAsync()` (only `Complete` terminal in-cap claims replay; empty-terminal replay; Abandoned for partial/non-claimed/past-cap-abandoned), `DurableClaim` / `IdempotencyLocalFlight` coordinator behavior.
3. **Inspect workspace-local MCP admission:** `McpConnectionManager.Config` (`BuildMergedToolsForWorkspaceAsync()` bounded `SecureFileReader` read + digest parse), `TrustedMcpWorkspaceStore.IsApprovedDigestAsync()` / `TrustAsync()` (digest on `ManagedMcpServerEntry`), retirement/replacement (`StartAsync()` blocked after retirement), identity-owned cleanup (`IdentityOwnedFileSystemCleanup`). Confirm digest matching, retirement ordering, cleanup tracking.
4. **Inspect filesystem identity / cleanup:** `SecureFileReader` nonblocking open (`O_NOFOLLOW` / `NONBLOCK` / `O_CLOEXEC`); `FileHandleIdentity` metadata validation; `IdentityOwnedFileSystemCleanup` quarantine/re-check/deletion; `AtomicFile` rollback (backup fingerprint verification, identity capture required for backup identity).
5. **Inspect reservation/accounting lifecycle:** `BudgetReservationService.EstimateWorstCaseTurnUsd()` (per-call max-not-sum); `TurnAccountingHandle.ReserveTurnBudget()` / `EstablishClampedSnapshot()`; `TurnAccountingHandle` lease renewal; `GrimoireRepository` timestamp-group summarization; `SqliteBusyRetry` busy/locked retry with fresh transaction per retry and cancellation-observed exit.
6. **Inspect isolated delegation:** break at `ArcanumDelegateTaskTool.InvokeCoreAsync()` and `SubagentRunner.RunAsync()`. Confirm the child `PingRequest` has no `SessionId`, workspace, context snapshot, Chronosync, campaign, data streams, tools, or retrieval; inspect `SubagentExecutionAmbient.Depth`; then verify `ModelCallExecutor.RecordDelegatedUsage()` charges provider usage before `ThrowIfExhausted()` ends the loop and the durable `subagent` row reaches `Completed` or `Failed`.

## Related documents

- Authoritative contracts: [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md)
- Human navigation guide: [`Arcanum.Design.Human.md`](Arcanum.Design.Human.md)
- Agent/operator primer: [`Arcanum.README.md`](Arcanum.README.md)
- Complete configuration reference: [`Compendium.README.md`](Compendium.README.md)
- Source of truth for `DEBUGGING.Human.md`: the verified breakpoints above are drawn directly from the code; any discrepancy should be corrected in this file and then verified against `DESIGN.md` §13–§18.
