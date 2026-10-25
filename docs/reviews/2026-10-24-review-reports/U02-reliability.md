# U02 (Arcanum.Api, Intelligence (2)) — reliability lens

Tree: `/Users/mat/Source/apps/RetroDownfall.Arcanum/.claude/worktrees/codebase-review` at `ac0d2d8e`.

## Unit file list (how it was established)

`units.json` did not exist in `scratchpad/review/` when this review ran (the directory was empty, and other reviewers were reconstructing their own units). The U02 list was rebuilt from the stated size: walking `src/RetroDownfall.Arcanum.Api/Intelligence` in `os.walk` order (root files first, sorted, then sorted subdirectories) and greedily packing files into units of at most 12,000 lines gives Intelligence (1) = 11,879 lines and Intelligence (2) = **11,444 lines exactly**, the figure the task gives. That second unit is these 48 root files, from `IntelligenceEndpoints.cs` to `ToolExecutionEvent.cs`:

IntelligenceEndpoints, LexiconEntityExtractionResponse, LexiconEntityExtractor, LongRestConfigurationEpoch, LongRestMaintenance.Dispatch, LongRestMaintenance, LongRestProvider, LongRestProviderCallFreezer, LongRestProviderClientFactory, LongRestProviderFailure, ManaMeter, ManaToolCatalog, ModelCallExecutor.CampaignMaintenance, ModelCallExecutor.LongRest, ModelCallExecutor.MemoryEvaluation, ModelCallExecutor, ModelCallPayloadFingerprint, ModelInfoBuilder, ModelTokenEstimator.CampaignMaintenance, ModelTokenEstimator.LongRest, ModelTokenEstimator.MemoryEvaluation, ModelTokenEstimator, NonBillableSurfaces, OpenAiRequestAugmentingHandler, OutputTokenLimitWirePolicy, PingRequestPreflightValidator, PromptCacheClientIdentity, PromptCacheConfigurationEpoch, PromptCacheDispatchPolicy, PromptCacheDispatchValidator, PromptCachePlanner, PromptCachingChatOptionsAdapter, ProviderFailureClassifier, PublicInferenceErrorMessages, ReasoningChatOptionsAdapter, SanctumCampaignResolution, SemanticRouter, SemanticSpellResponse, SemanticSpellRouter, SessionAttachmentRequestValidator, SessionAttachmentToolInjection, SessionAttachmentTurnService, SessionContextPinMaterializer, SessionTurnConcurrencyGate, SpellRoutingDecision, StructuredOutputValidator, SystemPromptTokenAttribution, ToolExecutionEvent (all `.cs`).

**Coverage:** I read all 48 files in full. To trace callers and contracts I read parts of: `WizardIntelligenceProvider.cs` (the pin, attachment and structured-output call sites), `ToolExecutionPipeline.cs` (the attach catch block), `ChatClientFactory.cs` (options and policies), `CampaignRollupProviderClientFactory.cs`, `MemoryEvaluationProviderClientFactory.cs`, `ContextMaterializationLedger.cs` (Accept and TryMarkInjected), `Subagents/DelegatedManaTracker.cs`, Core `LongRestFaultPolicy.cs`, `SessionAttachmentTurnBudget.cs`, `CovenantModels.cs` (`CovenantDigest`), `ILongRestAttemptStore.cs`, Infrastructure `LongRestAttemptStore.cs` / `.Claims.cs`, `LongRestSweepCoordinator.Run.cs`, `SessionAttachmentStore.cs` (OpenReadAsync, ReadBytesAsync, ValidateReferencesAsync), `EncryptedBlobStore.cs` (the exception types it throws), `BoundedLruCache.cs`, and `IEncryptedBlobStore.cs`. Owning docs read: DESIGN §10.2.6 (context pins), §10.7.6 (loop termination table), §10.8.2 (Unknown-Usage Settlement and Cap), §11.9 (sanitized error envelopes), the Long Rest closed-lane paragraphs (DESIGN lines 5751, 5786, 5840, 5854, 5951), and Compendium README row `providers.outputTokenLimitWireDialect`.

## Findings

### 1. Important: one corrupt pinned attachment fails every turn of its Session

- **File:** `src/RetroDownfall.Arcanum.Api/Intelligence/SessionContextPinMaterializer.cs:126`
- **Claim:** `MaterializeAsync` converts only `IOException`, `UnauthorizedAccessException` and `InvalidOperationException` into a per-pin `Error` block:
  ```csharp
  catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
  ```
  The attachment store reports an unreadable blob with `InvalidDataException` (`SessionAttachmentStore.OpenReadAsync:1350-1353` length mismatch, `EncryptedBlobStore.cs:575-712` truncated header or chunk) or `CryptographicException` (`EncryptedBlobStore.cs:627`). `InvalidDataException` derives from `SystemException`, not `IOException`. Lookup failures are `DbException`, and `ArgumentException`/`NotSupportedException` can also escape. Neither caller catches: `WizardIntelligenceProvider.cs:2011-2016` (every bound turn) and `WizardIntelligenceProvider.ContextPreview.cs:228-238` (`/intelligence/context/inspect`).
- **Failure scenario:** a Session pins a text attachment, and its encrypted blob is later truncated or swapped (a disk fault, an interrupted restore, a row/blob mismatch). Pins are re-materialized afresh before every bound turn (DESIGN §10.2.6), so **every** later turn in that Session, and every context inspect, fails with a generic error until the operator finds and unpins that one pin. DESIGN §10.2.6 says: "A single failure becomes an error/status block whose diagnostic is a constant per exception type ... and cannot mutate or corrupt the session."
- **Evidence:** the probe `Probe_pin_materializer_corrupt_attachment_escapes` (a `NoOpSessionAttachmentStore` whose `readBytes` throws) gave `escaped: System.IO.InvalidDataException` and `escaped: System.Security.Cryptography.CryptographicException`. The existing `SessionContextPinMaterializerTests` (76 tests in the combined run, all passing at ac0d2d8e) exercise only `IOException` (test file line 1468). A regression that removed any of the three types from the filter would also pass every test except the matching one.
- **Fix:** catch `Exception` when it is not caller cancellation (`!cancellationToken.IsCancellationRequested || ex is not OperationCanceledException`) around each pin. Extend `DescribeFailure` with a constant for corrupt or unreadable sources (`InvalidDataException`, `CryptographicException`). Add tests for both exception types and for a store lookup that throws.
- **Effort:** S

### 2. Important: the Long Rest lane ignores the provider's declared output-token field

- **File:** `src/RetroDownfall.Arcanum.Api/Intelligence/LongRestProviderClientFactory.cs:110-115`
- **Claim:** `Options()` builds the SDK options without `OutputTokenLimitWirePolicy`. `ChatClientFactory.cs:199` installs that policy for interactive turns. `providers.outputTokenLimitWireDialect` is a provider-level contract ("Exact top-level output-limit field accepted by this HTTP provider ... opt into `maxTokens` when the endpoint requires the legacy field (including Ollama 0.40.6)", Compendium README:114; DESIGN 5951). For a `maxTokens` provider, Long Rest summary and adjudication requests send `max_completion_tokens: 2048`, a field that endpoint does not honour.
- **Failure scenario:** an operator on a `maxTokens` endpoint enables Long Rest. Every summary and adjudication call goes out with no output bound the endpoint honours.
  - The 2048-token worst-case reservation (`LongRestProvider.cs:73-79`) understates the real spend.
  - A reasoning model can run long.
  - An answer over 65,536 characters is discarded by `ReadLongRestText` (`ModelCallExecutor.LongRest.cs:264-266`) after it has been paid for. It becomes `LongRest.InvalidOutput`, which `LongRestFaultPolicy` classes as `NeedsOperator`.
  
  The same omission exists in `CampaignRollupProviderClientFactory.cs:91-95` and `MemoryEvaluationProviderClientFactory.cs:93-97`.
- **Evidence:** `Probe_long_rest_client_ignores_max_tokens_dialect`, with a provider whose `OutputTokenLimitWireDialect = MaxTokens`:
  - long-rest body: `{"messages":[...],"model":"model","max_completion_tokens":2048}`
  - interactive body with the policy: `{"messages":[...],"model":"model","max_tokens":2048}`

  `LongRestProviderDispatchTests` inspects the Long Rest body (tools, strict) but not the output-limit field.
- **Fix:** add `options.AddPolicy(new OutputTokenLimitWirePolicy(selection.Provider.OutputTokenLimitWireDialect), PipelinePosition.PerCall)` for the chat client, and validate `Enum.IsDefined` as `ChatClientFactory.cs:99` does. Do the same in the rollup and memory-evaluation factories, add a wire-body test for each lane, and document it in DESIGN 5751.
- **Effort:** S

### 3. Minor: a pre-send refusal in Long Rest loses its code and parks the sweep as needs-operator

- **File:** `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.LongRest.cs:102-107` (and the same at `183-188`)
- **Claim:** every non-cancellation exception raised inside `beforeSend` becomes a generic `Hub.Error`:
  ```csharp
  return dispatched ? LongRestProviderFailure.Classify(exception) : new Error(ErrorCodes.Hub.Error, PublicInferenceErrorMessages.NativeGenericFailure);
  ```
  Several refusals arrive this way:
  - `LongRestProvider.cs:135-147` throws `InvalidOperationException(ErrorCodes.LongRest.StaleFence)`, deliberately carrying `StaleFence`.
  - `LongRestMaintenance.Dispatch.cs:86-132` throws `Refused(StaleInput / store codes)`.

  `Hub.Error` is not in `LongRestFaultPolicy.KnownCodes`, so `Classify` returns `NeedsOperator`, while `StaleFence`, `StaleInput` and `ConfigurationChanged` are `RestartGeneration` (`LongRestFaultPolicy.cs:96-104,164`). When the configuration epoch moved, `LongRestProvider.cs:214-216` then answers `Stale()`, which is `LongRest.InvalidRequest` (line 285), also `NeedsOperator`. The coordinator parks the sweep with that code (`LongRestSweepCoordinator.Run.cs:137,146-149`).

  A refusal raised after `MarkSentAsync` (`Dispatch.cs:127`; the post-mark gates at 129-131 and the provider's post-check at 142-147) is also settled as `UsageUnknown` (`Dispatch.cs:157-161`, any code other than `ProviderUnreachable`). That adds one to the Unknown-Usage Cap of two, although no SDK call started.
- **Failure scenario:** the operator saves a configuration change (any provider, pricing, default or fast model) while a sweep is inside a send fence, or a source or claim fact moves between the unit's first gate and the fence. The sweep should settle `Abandoned` and restart, but parks as needs-operator. If the refusal is past the mark, the unit also burns a cap slot, and a second occurrence parks it under `LongRest.TransportUnknown`.
- **Evidence:** `Probe_long_rest_before_send_refusal_code_is_replaced`, using `LongRestProviderFixture`:
  - `fence=stalefence code=Hub.Error class=NeedsOperator sends=0`
  - `fence=source-before code=Hub.Error class=NeedsOperator sends=0`
  - `fence=config-after-journal code=LongRest.InvalidRequest class=NeedsOperator sends=0`

  The existing theory `LongRestProviderDispatchTests.A_refused_or_stale_callback_never_reaches_HTTP` (lines 112-138) asserts only `result.IsFailure` and 0 sends, so it passes whatever code comes back. Settlement after the mark is traced through `Dispatch.cs:127-131 → executor 102-107 → Dispatch.cs:157-161 → LongRestAttemptStore.SettleUnresolvedSendAsync:237-263` (FailureCount+1, `TransportUnknown`).
- **Fix:**
  1. Let fence exceptions out of the executor untranslated, or wrap them in a typed `LongRestSendRefusedException(Error)` that `LongRestProvider` and `LongRestMaintenance` unwrap. Map the provider's StaleFence throw and its post-call epoch check to `LongRest.StaleFence` / `ConfigurationChanged`, not `InvalidRequest`.
  2. When the refusal came from the fence (no SDK call began), settle a Sent attempt as `NotSent`. Otherwise make DESIGN §10.8.2 say that a post-mark fence refusal is charged to the cap.
  3. Assert the code in the dispatch theory.
- **Effort:** M

### 4. Minor: internal exception text reaches the client as an attachment validation error

- **File:** `src/RetroDownfall.Arcanum.Api/Intelligence/SessionAttachmentRequestValidator.cs:52-55`
- **Claim:** `catch (InvalidOperationException ex) { return ex.Message; }` uses the store's validation exception type as a catch-all. Infrastructure faults are caught too: `ObjectDisposedException` during host shutdown, an EF context-misuse `InvalidOperationException`, `ComprehensionDataNotice`-style guards. Their raw message is returned. `WizardIntelligenceProvider.cs:2018-2023` turns it into `Validation.AttachedFiles` (400), sent to the client verbatim.
- **Failure scenario:** a turn with `attachmentReferences` arrives while the scoped context is being disposed, or an EF misuse occurs. The client gets HTTP 400 "Cannot access a disposed context instance ... Object name: 'ArcanumDbContext'." This reports a server fault as the caller's error and leaks internal detail, against DESIGN §11.9 ("Inference-pipeline errors must not leak internal exception text to clients").
- **Evidence:** `Probe_reference_validator_returns_internal_fault_text` (the store's `ValidateReferencesAsync` throws `ObjectDisposedException`) returned `validation message=Cannot access a disposed context instance. Internal detail. Object name: 'ArcanumDbContext'.`
- **Fix:** have `ISessionAttachmentStore.ValidateReferencesAsync` return a `Result` or a dedicated exception type with code-owned messages, catch only that, and let other faults reach the turn's sanitized failure path.
- **Effort:** S

## Checked and sound

- **`LongRestConfigurationEpoch`:** ABA and initial-subscription race, by reading the ctor and `Changed`. `CovenantDigest` equality is by value (`SequenceEqual`), so equal settings do not bump the epoch.
- **`LongRestMaintenance.ExecutePaidAsync` lifecycles:**
  - the external-effect group is `await using`;
  - the `dispatch` and `stop` CTSs outlive the heartbeat, which is awaited in `finally` before the using-declarations dispose;
  - heartbeat failure cancels dispatch;
  - prepared attempts are withdrawn on every pre-mark exit (catch at 149, branch at 163).

  All traced line by line.
- **Long Rest claim renewal:** concurrent heartbeat and gate renewals cannot CAS-fail each other. `LongRestAttemptStore.Claims.cs:21-25` is a deadline-extending update conditioned on `LeaseDeadlineUtc > now`, not on the old value.
- **`LongRestProvider` accounting:** completed in `finally` with `CancellationToken.None`. A paid result whose final accounting fails becomes `AccountingCompletionRefused`. Operator precedence in the admission guard (lines 33-36) is correct.
- **`LongRestProviderFailure.ParseStatus`:** I suspected that a provider body containing "HTTP nnn" could override the real status. An F# probe against the pinned OpenAI 2.12 / System.ClientModel showed that the "Status:" form carries no body: `Service request failed.\nStatus: 503 (Service Unavailable)`, the same for a JSON non-OpenAI body and an HTML 502. The "HTTP" form puts the real status first (`HTTP 503 (server_error: )\n\nupstream HTTP 404 ...`). It classified correctly (`ProviderUnavailable`), so this is not a finding.
- **`LongRestProviderClientFactory`:** credential resolution on a frozen copy, cancellation checked before allocating SDK objects, one connection per send (existing loopback tests pass).
- **`ModelCallExecutor`:**
  - in the buffered and streaming paths, cancellation rethrows only for the caller's token;
  - the protected effect and its linked CTS are disposed on every path;
  - streaming usage is recorded in `finally` even when the consumer abandons the stream;
  - prompt-cache refusals are recognized in both the direct and the aggregated (retry) forms.
- **`BudgetExhaustedException` from `BeginModelCall` inside the buffered try:** preserved as `Cause` for non-protected calls, and the Wizard rethrows `Cause`.
- **`PromptCacheDispatchPolicy`:** AsyncLocal scope is restored on dispose, and the sync `Process` path (sync-over-async) is not reached, because MEAI uses the async SDK calls.
- **`ModelTokenEstimator`:**
  - `Math.Clamp(count, 0, renderedTotal - allocated)` cannot get min > max, because allocated is at most renderedTotal by construction;
  - the shared `TextTokenCache` is thread-safe (`BoundedLruCache` locks);
  - `CountText` and `InferenceTextCounter` give identical counts (TiktokenTokenizer has no normalizer), so the shared cache cannot mix results;
  - `FormatValue` recursion terminates for MEAI/SDK raw representations.
- **`OutputTokenLimitWirePolicy`:** PerCall, so it runs once before retries and rejects ambiguous double fields.
- **`OpenAiRequestAugmentingHandler`:**
  - the replayed prefix stream disposes the underlying response stream;
  - streaming bodies are passed through;
  - the strict-flag retry restores the original bytes and content type.
- **`SessionAttachmentTurnService.PrepareAsync`:**
  - all refusable checks run before the first persist;
  - created rows are rolled back on a fault or caller cancellation, on an independent 10 s budget, without replacing the original outcome;
  - `MaterializeReferenceAsync` catches everything except caller cancellation.

  Text and images are bounded at persist time by `SessionAttachmentContentPolicy` (1 MiB text, 20 MiB images), so the whole-blob `ReadBytesAsync` in `SessionAttachmentToolInjection` is bounded.
- **`SessionAttachmentToolInjection`:** `attach_session_file` exceptions are caught by the tool pipeline (`ToolExecutionPipeline.cs:566-590`). Ledger-then-budget ordering in `TryAcceptToolMaterialization` cannot strand content: ledger identity and content-hash dedup reject before the turn-budget check whenever the budget would refuse.
- **`SessionContextPinMaterializer` (apart from finding 1):**
  - frame budgeting always ends with the end marker;
  - UTF-8 truncation backs off continuation bytes correctly;
  - the hash cap stops reading past the preview;
  - directory walk is bounded at 2,048 directories and by the byte limit, and observes cancellation.
- **`SanctumCampaignResolution`:** fails closed. Unloadable claims become `Unreadable`, strictness ordering matches the enum (Disabled < AuditOnly < Strict < Unreadable), and only caller cancellation escapes `TryLoadAsync`.
- **`SessionTurnConcurrencyGate`:** KeyValuePair removal compares the lease reference, so a stale lease cannot release a newer one.
- **`StructuredOutputValidator`:** its stop conditions match DESIGN §10.7.6, where the absence of a retry count is documented. The caller's per-retry context gate bounds growth even when usage is not reported.
- **`SemanticRouter`, `LexiconEntityExtractor`, `SemanticSpellRouter`:** preflight timeouts are clamped to 1-600 s, and only caller cancellation propagates.
- **`IntelligenceEndpoints`:** 415/400 handling on `ping-stream`, failure codes go through `ArcanumErrorMapper`, and the human-response body is bounded.

## Probes run

The probe source is saved at `/private/tmp/claude-501/-Users-mat-Source-apps-RetroDownfall-Arcanum/d111c03e-eda5-4c82-a12d-f02f9cd1cdb8/scratchpad/review/probe-u02/U02ReliabilityProbes.cs`. It was compiled in the scratch worktree `wt/U02-reliability` (now removed) and run with `dotnet test --no-build --filter FullyQualifiedName~U02ReliabilityProbes </dev/null`.

| Probe | Result |
|---|---|
| `Probe_pin_materializer_corrupt_attachment_escapes(InvalidData)` | exception escaped: `System.IO.InvalidDataException` (finding 1) |
| `Probe_pin_materializer_corrupt_attachment_escapes(Cryptographic)` | exception escaped: `System.Security.Cryptography.CryptographicException` (finding 1) |
| `Probe_long_rest_before_send_refusal_code_is_replaced(stalefence)` | `code=Hub.Error class=NeedsOperator sends=0` (finding 3) |
| `Probe_long_rest_before_send_refusal_code_is_replaced(source-before)` | `code=Hub.Error class=NeedsOperator sends=0` (finding 3) |
| `Probe_long_rest_before_send_refusal_code_is_replaced(config-after-journal)` | `code=LongRest.InvalidRequest class=NeedsOperator sends=0` (finding 3; the probe's assertion expected Hub.Error, and the actual code is a second wrong code) |
| `Probe_long_rest_failure_classifier_reads_body_marker` | hypothesis refuted: message has no body, classified `ProviderUnavailable` (not a finding) |
| `Probe_long_rest_client_ignores_max_tokens_dialect` | long-rest body `max_completion_tokens:2048`, interactive body `max_tokens:2048` (finding 2) |
| `Probe_reference_validator_returns_internal_fault_text` | `Cannot access a disposed context instance. Internal detail. Object name: 'ArcanumDbContext'.` (finding 4) |
| F# script `probe-u02/msg.fsx` (four 5xx/502 body shapes against the pinned SDK) | message formats as quoted above; ParseStatus sound |
| Existing tests in the review tree: `LongRestProviderDispatchTests.A_refused_or_stale_callback_never_reaches_HTTP`, `SessionContextPinMaterializerTests`, `LongRestOwnedTransportLoopbackTests` | 76 passed, 0 failed at ac0d2d8e. None catches findings 1-3. |
