# U04 (Arcanum.Api, Intelligence (4)) - USABILITY lens

Tree: worktree `codebase-review` at ac0d2d8e. Reviewer: U04 / usability.

## Scope and coverage

`units.json` did not exist under `scratchpad/review/` when this review ran, so the unit was identified from the brief: "Arcanum.Api, Intelligence (4), about 8904 lines". `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs` is exactly 8,904 lines and is the only file of that size in the folder, so it was taken as the unit.

- Read in full: `WizardIntelligenceProvider.cs` (lines 1-8904).
- Read in full as the same class: `WizardIntelligenceProvider.ContextPreview.cs` (1-1009).
- Read in full as direct collaborators, to judge the error and log text this unit produces: `PublicInferenceErrorMessages.cs`, `InferenceProviderFailureMessage.cs`, `ProviderFailureClassifier.cs`, `ClientToolCapabilityValidator.cs`, `PingRequestPreflightValidator.cs`, `ToolLoopProgressDetector.cs`, `TurnContextGuards.cs`, `ChatClientFactory.cs`, `WeaveService.cs`, `TurnEngine/TurnEngine.cs` (ProduceAsync), `ModelCallExecutor.cs` lines 150-260. Also the relevant parts of `ArcanumErrorMapper.cs`, `OpenAiStreamErrorMapper.cs`, `OpenAiV1Endpoints.cs` (error mapping), `ReasoningRequestValidator.cs`, `ReasoningCapabilities.cs`, `ConfigurationValidator.cs`, `SpellScanner.LoadFullAsync/TryParseSpellFileAsync`, `SpellPathPolicy.cs`, `LoggingBootstrapper.cs`, `DivinationService` log sites.
- Docs read: DESIGN section 10.1-10.2.3, 10.7 (whole), 11.14, tool inventory; API 8.8-8.10, error catalog rows; Compendium rows for providers/features; Command Reference rows for `--temperature` and `/memory`.
- Not reviewed in depth (other units): `ToolExecutionPipeline`, `GrimoireTurnWriter`, `InferenceContextBuilder`, Covenant dispatch internals.

None of the earlier owner rulings (R-122, R-376, R-398, R-126, R-185, R-225, R-297, F044, the Comprehension/PDF rulings) touch what is reported here.

## Probes run

All probes are xunit tests added to a scratch worktree (`scratchpad/review/wt/U04-usability`, since removed). Sources and raw output were kept:

- `scratchpad/review/results/A/U04UsabilityProbes.cs` (partial of `WizardIntelligenceProviderFallbackTests`)
- `scratchpad/review/results/A/U04LongRestStatusProbe.cs` (partial of `WizardIntelligenceProviderTests`)
- `scratchpad/review/results/A/U04-harness-hooks.patch` (the small hooks added to existing test helpers: a `partial` keyword, a logger hook, a stream-responder hook, a throwing fake grimoire, and probe S6)
- `scratchpad/review/results/A/U04-probe-output.txt` (raw output of the final run, 24 tests passed)

| Probe | What it shows |
|---|---|
| S1 | A streamed turn whose provider is silent: with the production composition (health tracker) the client receives 0 frames until the first token; without a tracker it receives 4 (status, sessionBound, conversationBound, context). |
| S1b | The existing "protected maintenance status" scenario, rerun with a tracker: the status reaches the client after all 3 paid provider sends; without a tracker it arrives with 0 sends made. |
| S2, S2b | Buffered and streamed "model does not support tools" restart. |
| S3 | Client-tool forwarding logs a Warning on every turn. |
| S4 | Model-resolution log lines on the tracker-less path versus the tracker path. |
| S5 | `ReadOnlyToolNames` versus the registered tool names; the real filter run on a candidate list. |
| S6 | An attachment retrieval exception is logged at Debug and there are 0 entries at Information or above. |
| S7 | Silent clamping or ignoring of sampling parameters and `response_format`; reasoning output resolution. |
| S8 | The no-progress stop text and log. |
| S9 | An explicit `OverrideSpellPath` that cannot load resolves to "no spell" with success. |
| S10 | Context overflow: error text, HTTP status, `/v1` body. |
| S11 | A SQLite exception during session load is reported as a provider problem. |
| S12 | Log lines produced by a plain provider connection failure. |

## Findings

### 1. [Important] Production streaming withholds every pre-commit frame until the provider's first token (ui-responsiveness)

`WizardIntelligenceProvider.cs:1363-1405, 1447-1489` (`preCommitBuffer`), `1635-1639` (`IsPreCommitStreamingEvent`), `1782`, `1819-1832`.

In the resilience branch of `StreamPromptCoreAsync` the outer loop buffers `Status`, `SessionBound`, `ConversationBound` and `Context` frames in `preCommitBuffer` and only yields them once the first non-pre-commit event (token, reasoning, tool call, error, result) arrives. `IProviderHealthTracker` is always registered in the host (`Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs:1830`, `Api/ApiBootstrapper.cs:650` builds the provider with `ActivatorUtilities`), so every production stream takes this branch. The tracker-less single-lease branch (lines 1085-1191), which passes frames straight through, is reachable only from tests.

Consequences for the operator and client:
- "Mage is generating response...", the compression notice (`MemoryCompressionNotice`), the session id and the context accounting frame are all invisible for the whole time from turn start to the provider's first token. For a local model with a long prefill that is tens of seconds with nothing on screen except blank NDJSON keep-alives.
- A client that disconnects in that window never learns the id of the Session the turn created.
- The explicit "Finishing protected memory maintenance..." status (lines 1819-1832) exists so the client is told before the paid protected maintenance starts. The inner iterator yields it, the outer loop buffers it and immediately calls `MoveNextAsync`, which resumes the iterator into `finally { preparationProgress.AcknowledgeProtectedMaintenance(); }` (line 1830). The "client has been told" release therefore happens before the client has been told anything.

Proof (S1, S1b): with a stream whose first token is held back, `withTracker=True` -> 0 frames received before release, all of status/sessionBound/conversationBound/context arrive after it; `withTracker=False` -> the same 4 frames arrive before release. Rerunning the existing protected-maintenance scenario with a tracker: "provider sends already made when the client received it = 3" (3 of 3), versus 0 without a tracker.

Why no test catches it: `WizardIntelligenceProviderTests.CreateWizard` defaults `healthTracker` to null (`tests/.../WizardIntelligenceProviderTests.cs:9563`), so `LongRestStatus_a_streamed_turn_with_protected_debt_says_so_once_before_the_assistant_begins` (asserts `sendsWhenStatusArrived == 0`) exercises the tracker-less path and passes while the shipping path violates it. The tracker-based tests in `WizardIntelligenceProviderFallbackTests` assert the fallback outcome, not the arrival time of informational frames.

Fix: yield informational pre-commit frames as they are produced; commitment only decides whether a *fallback restart* is still allowed, not whether the client may see a status line. On a fallback restart, suppress frames already emitted (the shared `TurnContextSeed` keeps the Session id stable). Add a tracker to the `LongRestStatus` and a "status before first token" test. Effort M.

### 2. [Important] Context overflow gives no provider, model, or remedy; it maps to HTTP 429 and `/v1` clients get a generic message (error-message / api-contract)

`WizardIntelligenceProvider.cs:7990` calls `TurnContextGuards.CheckContextBudget` (`TurnContextGuards.cs:201-218`).

The message is `Context budget exceeded: 11492 accounted tokens (10468 input + 1024 reserved) exceed the 600-token window using profile 'fallback:OpenAICompatible:provider-a:wizard-fallback-test-model:o200k_base'.` It does not name the provider or model, does not say where the limit comes from, and says nothing about what to do. The most common cause on a local install is the factual `providers.contextWindowLimit` default of 8192 (Compendium.README.md:115) being below the model's real window, or a long Session. Sibling messages do give the action ("Use a larger-context model or reference fewer attachments", attached-file boundary text "Split ... and retry").

DESIGN section 10.7.3 (terminal-event table) states the contract: the context-boundary error "names the provider/model request fact or context admission boundary and the smaller-request, compaction, or model-selection action", and a retained boundary error "identifies the owner, the safe measured value and limit ... and the exact retrieval, continuation, configuration, or recovery action". The code names neither.

Mapping, from S10:
- `ArcanumErrorMapper.cs:134` maps `Hub.ContextBudgetExceeded` to **429**. API.md:917 documents the code only as "Confirmed Covenant content could not fit"; the dominant general case is undocumented. A 429 is a "retry later" signal; standard OpenAI SDK clients retry it with backoff, and an oversize prompt will never succeed.
- On `/v1`, the stream mapper (`OpenAiStreamErrorMapper.Map`, default arm + `SanitizeMessage`) and the buffered mapper (`OpenAiV1Endpoints.MapPublicOpenAiError`/`ResolvePublicInferenceFailureMessage`) reduce it to `{"message":"Inference failed. See server logs for details.","type":"api_error","code":"inference_failed"}`. OpenAI-compatible tools look for HTTP 400 `context_length_exceeded` to summarise or truncate; they cannot tell this from an outage.

Fix: include provider/model, the limit's source (`providers[].contextWindowLimit`) and the three actions in the message; map to 400 (native) and `invalid_request_error`/`context_length_exceeded` (`/v1`), keeping 429 only if Covenant admission really needs it; document the general case in API.md section 8.23. Effort S for the text, M with the status and `/v1` mapping.

### 3. [Minor] Failures that are not the provider's are reported as provider failures (error-message)

`WizardIntelligenceProvider.cs:1346-1349, 1439-1442` (resilient stream), `1186, 1538`, `TurnEngine.cs:140-147`, `PublicInferenceErrorMessages.cs:10`.

Any exception that escapes `RunInferenceAttemptAsync` before the first provider byte - Grimoire read/write, attachment preparation, spell routing, tool discovery - lands in a `catch` that formats `BuildInferenceFailureMessage(candidateProvider, ex)`, i.e. "Provider 'X' is unreachable. Verify the service is running and Arcanum:Providers is configured correctly." For a buffered turn the same exception reaches `TurnEngine.ProduceAsync` and becomes "Inference failed. Ensure the provider is running and reachable, then try again."

S11: a `SqliteException("database is locked")` thrown by the Session load produced, on the streaming path, `Provider 'ollama-local' is unreachable. Verify the service is running and Arcanum:Providers is configured correctly.` (code `Hub.Error`, provider call count 0), and on the buffered path `Inference failed. Ensure the provider is running and reachable...`. The log line is the only truth: `Provider ollama-local failed during pre-commit streaming ...; exception type Microsoft.Data.Sqlite.SqliteException`.

An operator with a locked or failing Grimoire is told to restart Ollama. Fix: attribute by origin - only exceptions from the model call (`ModelCallExecutor`/lease construction) get the provider message; anything else gets a distinct message and code (for example "A local component failed: Grimoire. See server logs") and does not call `MarkFailed`. Effort M.

### 4. [Minor] Retrieval failures (Saga, Tapestry, attachments, workspace index) are invisible: Debug is never emitted in production and `context inspect` gives a generic reason (diagnostics)

`WizardIntelligenceProvider.cs:5796, 5811, 5859, 5915, 6019, 6231, 6828, 8662` (all `LogDebug`); `WizardIntelligenceProvider.ContextPreview.cs:1006`.

The host's Serilog floor is a hard-coded `MinimumLevel.Information()` (`Infrastructure/Logging/LoggingBootstrapper.cs:45`); the repository states the consequence itself: "a Debug event reaches neither the rolling file, nor the ring buffer behind `GET /api/logs`, nor `arcanum logs`" (`AuditLogPageReader.cs:246`). So the exception handlers that turn a Saga/Tapestry/attachment store fault, a failed `RegisterWorkspace`, or a failed Saga-extraction enqueue into "continue without context" leave no trace at all. S6: an attachment retrieval exception produced one Debug entry, 0 at Information or above, turn `ok=True warnings=0`. (Divination and Weave failures are logged at Warning by those services, so embedding and vector-search faults are covered; store and join exceptions are not.)

The operator's diagnostic for "why is my context missing" is `arcanum context inspect`/`mana`, and its per-source reason for a retrieval source with no tokens is `No effective content was available for this source.` for a disabled feature, a failed retrieval and a genuine no-hit alike (`ResolveSourceReason`, line 1006).

Fix: raise these to Warning (type-only, as the existing test `AttachmentRetrievalFailure_LogsTypeOnly` requires - it does not pin the level), add the source name, and give the preview reasons distinct text: feature disabled (name the key), retrieval failed, no hit above threshold. Effort S.

### 5. [Minor] `toolPolicy: readOnlyTools` uses a stale allowlist and the API doc points at a section that does not define it (api-contract / docs)

`WizardIntelligenceProvider.cs:7392-7409`; `docs/Arcanum.API.md:461`.

`ReadOnlyToolNames` contains `read_lore`, a name no tool registers (the tool is `read_saga`; `ArcanumInternalToolServerTests.cs:4122` asserts `read_lore` does not exist). `read_saga`, `web_search` and `read_url` are not in the list, while `send_commlink_alert` (outbound webhook), `delegate_task` and `petition_dungeon_master` are. S5 output: allowlisted names that no tool registers = `read_lore`; with the real filter, `read_saga`/`web_search`/`read_url` are dropped and `read_lore` and `send_commlink_alert` kept. API.md:461 says the policy's "semantics" are in DESIGN section 10.2.1, but that section does not mention `readOnlyTools` at all (the only DESIGN mention is the `refresh_session_file` sentence at line 1394), so a client cannot learn what the policy advertises. The tests cover only forwarded client tools (`write_file`/`read_file_chunk`).

Fix: derive the set from tool metadata or at least correct the name and decide on `read_saga`/web tools; write the semantics (the list and the rule) into DESIGN section 10.2.1 and the API doc; add a test comparing the allowlist with `ArcanumInternalToolServer.RegisteredToolNames`. Effort S.

### 6. [Minor] An explicit spell version that cannot be loaded is silently dropped (api-contract)

`WizardIntelligenceProvider.cs:5355-5362`; the only caller that sets `OverrideSpellPath` is `Tower/SpellExecutionEndpoints.cs:687-715` (`POST /api/spells/{name}/execute?version=`).

The endpoint checks only `File.Exists(versionPath)`. `ResolveRoutedSpellAsync` then calls `SpellScanner.LoadFullAsync`, which returns null for an oversize file (above the 1 MiB clamp), a frontmatter value rejected by `SpellFrontmatterValidator` (a line break, a value starting with `---`), or a file whose root or identity check fails. The result is `Result.Success(null)`: the turn runs with no spell and the caller gets a normal 200. The same request by name (`OverrideSpellName`) fails loudly with `Validation.SpellOverride`. S9: valid v1 resolves; a 2 MiB `SPELL.v2.md` -> `success=True resolved=<null: turn runs with NO spell>`; contrast: unknown name -> `Validation.SpellOverride`. (Only the oversize trigger was reproduced; the others are by reading `TryParseSpellFileAsync`.)

Fix: when the caller named a path, a null load is an error (`Spell.NotFound`/`Spell.InvalidFile` naming the file). Effort S.

### 7. [Minor] The "model does not support tools" restart is logged at the wrong levels, without model or provider, and without a signal in buffered responses (diagnostics)

`WizardIntelligenceProvider.cs:3687-3746` (log at 3724); `ModelCallExecutor.cs:196`.

When an undeclared model rejects tools at runtime the pipeline retries once without tools. S2: the expected condition produces an Error-level `Model call {id} failed for purpose MainInference; exception type System.InvalidOperationException.` and then an Information `Model does not support tools; retrying without local tools (exception type ...)`. Neither names the provider or model, neither mentions the config key that avoids the wasted failing call on every later turn (`providers.models.supportsTools: false`, Compendium.README.md:111), and the buffered result has `Warnings.Count == 0` (the streaming path at least emits "This model does not support tools; continuing without local tools.", S2b, which also lacks the key). Only English "does not support tools" text is recognised (`LooksLikeModelDoesNotSupportTools`, line 8698); other servers' wording ends as "returned HTTP 400" (finding 12) with no hint.

Fix: log the retry at Warning once per provider/model with `{Provider}`, `{Model}` and the key; downgrade the executor's Error line for this classified case; add the key to the status text and add a buffered warning. Effort S.

### 8. [Minor] Forwarded client tools log a Warning on every turn (noisy logs)

`WizardIntelligenceProvider.cs:7312`.

`BuildClientForwardedToolSet` logs `Client-supplied tools detected. These bypass Arcanum's tool loop and security controls.` at Warning each time it runs - once per provider attempt of every turn - for a feature the operator deliberately enabled with `Arcanum:Features:ClientTools`. S3: 3 turns -> 3 identical Warning lines, with no session, model or tool count. Agent integrations send tools on every message, so this is one warning per chat message and defeats alerting on Warning.

Fix: warn once at startup/configuration time (or once per process); per turn use Debug or a counter. Effort S.

### 9. [Minor] API.md describes reasoning-output resolution that no longer exists, and does not mention the `features.reasoningSummaries` gate (docs)

Code: `WizardIntelligenceProvider.cs:7570-7591` (`ResolveClientReasoningOutput`), caller `1734-1738`. Doc: `docs/Arcanum.API.md:433`.

API.md says: "When omitted, the resolved capability chooses `full` when `SupportsFull`, otherwise `summary` when `SupportsSummary`; `AllowsClientOutput` is required, and streaming also requires `SupportsStreaming`." Those per-model facts were removed - `ConfigurationValidator.cs:37-38, 276-296` rejects `supportsSummary`/`supportsFull`/`supportsStreaming`/`allowsClientOutput` at startup and Compendium.README.md:119 says so - and the code resolves: omitted -> `Summary` (`_ => ReasoningOutputMode.Summary`), no capability lookup, and the `streaming` parameter is unused. S7: omitted+capable -> `Summary`; requested `Full` with `features.reasoningSummaries=false` -> `None`. The latter is the default (`features.reasoningSummaries` default `false`, Compendium.README.md:177), so a client that sends `reasoning_output: "full"` to a reasoning-capable model passes validation and silently receives no reasoning; the API doc never mentions the operator gate.

Fix: rewrite the API.md paragraph (omitted -> summary; operator gate; where the facts live). Consider returning a warning when a requested output is suppressed. Effort S.

### 10. [Minor] Out-of-range sampling parameters and unknown `response_format` are silently altered or ignored (api-contract)

`WizardIntelligenceProvider.cs:8122-8200` (`ApplyInferenceParameters`); no range check in `PingRequestBoundsValidator.cs` or `OpenAiChatCompletionRequestValidator.cs`.

S7: `temperature=7, top_p=3, presence_penalty=-9, max_tokens=-5, response_format="xml"` -> options `temperature=2, topP=1, presence=-2`, max tokens unset, response format ignored; `response_format: json_schema` with no schema silently becomes plain JSON mode (and then skips schema validation, since `ResponseFormatJsonSchema` is null). The CLI contract validates `--temperature` as 0 through 2 (Command.Reference.md:430) and OpenAI answers 400 for these; Arcanum accepts and rewrites. API.md:433 lists the applied parameters but not the clamping.

Fix: validate and return 400 `Validation.*` naming the field, or document the clamp. A schema-less `json_schema` should be 400. Effort S.

### 11. [Minor] The no-progress stop does not say which tool looped or what to do (error-message)

`WizardIntelligenceProvider.cs:4252-4270`.

`Tool execution stopped because the latest call/result round repeated a recent round and produced no new evidence.` (S8) is a constant: no tool name, no count of rounds, no next step; the warning log carries only an 8-byte hash (`recurring round signature 683A5DBB9F164B5F`), which maps to nothing an operator can see. Tool names are already in `toolLoopProgressEntries` and are non-sensitive (the tool-outcome metric labels them). DESIGN section 10.7.3 requires a boundary error to identify "the owner ... and the exact ... action".

Fix: include the distinct tool names of the repeated round in the message and the log, and a one-line hint (change the request, or check the tool's output). Effort S.

### 12. [Minor] Every inference-failure log line omits provider, model, status and session although the public text says "see server logs for detail" (diagnostics)

`WizardIntelligenceProvider.cs:1182, 1330, 1418, 1533, 3557, 3749`; `ModelCallExecutor.cs:196`.

S12: a refused connection produced `Model call <id> failed for purpose MainInference; exception type System.Net.Http.HttpRequestException.` plus `Hub inference failed; exception type ...` (buffered) or `Streaming read failed; exception type ...` (stream), each at Error and each without provider, model, HTTP status or session id; only the first carries a model-call id, which appears in nothing else. The public text names the provider and HTTP status but points the operator to the log for the rest, and the log adds nothing. Type-only logging is deliberate (DESIGN 1259, 3653), but "safe operation identifiers" (provider name, model, status code, session id) are exactly what that policy says to keep.

Fix: add `{Provider}`, `{Model}`, `{HttpStatus}` and `{SessionId}` to these lines and emit one line per failure rather than two. Effort S.

### 13. [Minor] The provider-HTTP-error text gives the same advice for 401, 404, 429 and 5xx (error-message)

`InferenceProviderFailureMessage.cs:18`, used by `WizardIntelligenceProvider.cs:8798-8802`; pinned by `InferenceProviderFailureMessageTests.cs:27`.

`Provider 'X' returned HTTP N. Check the model, API key, and request; see server logs for detail.` is right for 400/401/403/404 but wrong for 408/429/5xx, where the action is to wait, check quota, or switch provider. The code is `Hub.Error` for all of them (HTTP 500), so a client cannot branch either. The test pins the advice for a 500.

Fix: status-class-specific text (auth, model/endpoint, rate limit/quota, provider fault) and consider typed codes for "provider rejected" versus "provider unavailable". Effort S.

## Checked and found sound

- Fallback semantics (connectivity-only, pre-commit-only, Grimoire turn deferred and resolved in `finally`) match DESIGN section 10.7.2, including the owner of a deferred handle (`ResolveDeferredTurnAsync`).
- Attached-file and Scrying images: base64 is validated in `ScryingValidator.ValidateBase64Size` before line 2111's `Convert.FromBase64String`, so a `FormatException` cannot escape as a "provider" failure.
- `ask_human` gating: buffered and unattended turns never advertise it; the synthetic denial is recorded and returned; the CLI renders `toolError` as "Tool X failed (tolerated)" followed by the result.
- Attachment admission errors name the attachment and the remedy (lines 3009-3021); `OverrideSpellName` errors name the missing spell; `Hub.SessionTurnBusy`, `Budget.Exceeded`, guardrail and structured-output errors carry their typed code in `data` on every path (`ErrorFrame`).
- Missing provider key does not throw: the factory sends the `no-key` placeholder and the provider's 401 surfaces as the HTTP-status message. `FastModel` is validated against the provider table at configuration time (`ConfigurationValidator.cs:1144`), so an unresolvable fast model is not a normal runtime failure.
- The tracker-less branches (single-lease, `Hub model resolution failed; exception type` without the requested model, S4a) are test-only in production because the tracker is always registered; the resilient path logs the requested model (S4b). Not a finding.
- Weave embedding failures are logged at Warning with the exception (`WeaveService.cs`), Divination search failures at Warning (`DivinationService.cs:111,178,238,599`).
- `ContextPreview`: model/session/spell failures return typed errors; the compression reason strings are accurate; no UI-blocking work.
- Not reported: `Hub.Error` being the code for any untyped provider failure (documented, API.md:399/878); the "Campaign Summary" wording of `MemoryCompressionNotice` (matches the documented legacy heading when campaign rollups are off).
