# U02 (Arcanum.Api, Intelligence (2)) - USABILITY review

Tree: `.claude/worktrees/codebase-review`, branch `claude/codebase-review-2026-10` at `ac0d2d8e`. Read-only; probes ran in a scratch worktree (`scratchpad/review/wt/U02-usability`, now removed). Probe sources and raw output: `results/A/U02-usability-probes/`.

## Unit definition (reconstructed)

`scratchpad/review/units.json` did not exist when this reviewer started (the scratchpad was empty), and no published binary was present, so no binary was run. The unit was reconstructed from the sibling U03 report: U01..U04 are the top-level `Intelligence/*.cs` files in C-locale order with cumulative sizes 11879 / 11444 / 7134 / 8904. I reproduced those sums from the tree. U02 is therefore the 45 top-level files from `IntelligenceEndpoints.cs` to `ToolExecutionEvent.cs` (11,444 lines):

IntelligenceEndpoints, LexiconEntityExtractionResponse, LexiconEntityExtractor, LongRestConfigurationEpoch, LongRestMaintenance(.Dispatch), LongRestProvider, LongRestProviderCallFreezer, LongRestProviderClientFactory, LongRestProviderFailure, ManaMeter, ManaToolCatalog, ModelCallExecutor (+ CampaignMaintenance, LongRest, MemoryEvaluation), ModelCallPayloadFingerprint, ModelInfoBuilder, ModelTokenEstimator (+ 3 partials), NonBillableSurfaces, OpenAiRequestAugmentingHandler, OutputTokenLimitWirePolicy, PingRequestPreflightValidator, PromptCacheClientIdentity, PromptCacheConfigurationEpoch, PromptCacheDispatchPolicy, PromptCacheDispatchValidator, PromptCachePlanner, PromptCachingChatOptionsAdapter, ProviderFailureClassifier, PublicInferenceErrorMessages, ReasoningChatOptionsAdapter, SanctumCampaignResolution, SemanticRouter, SemanticSpellResponse, SemanticSpellRouter, SessionAttachmentRequestValidator, SessionAttachmentToolInjection, SessionAttachmentTurnService, SessionContextPinMaterializer, SessionTurnConcurrencyGate, SpellRoutingDecision, StructuredOutputValidator, SystemPromptTokenAttribution, ToolExecutionEvent.

Before I found the sibling report I had guessed a different range and read, in full, `GrimoireTurnWriter`, `HumanPromptRegistry`, `InferenceContextBuilder`, `InferenceProviderFailureMessage`, `InferenceTokenizerResolver`, `Guardrails/*`, `MemoryDelegation/*`, `MemoryRecall/*` and `OpenAi/*`. Those belong to U01 and the subdirectory units, but the findings I made in them are real and are reported with their true files so the synthesis can de-duplicate.

## Coverage

All 45 U02 files were read in full. Owning docs read: API.md 8.8.x, 8.10.1-8.10.4, the error-code table (around lines 880-920) and the guardrails/audit rows; DESIGN 10.8 (loop terminal table around line 1700), 1334; Command Reference Long Rest operation-code table (lines 1100-1146); Compendium guardrail and Long Rest rows. Cross-read for context: `ArcanumErrorMapper.cs`, `OpenAiV1Endpoints.cs` (error mapping and streaming error frame), `OpenAiChatCompletionRequestValidator.cs`, `OpenAiV1EmbeddingsEndpoints.cs` (dimensions branch), `WizardIntelligenceProvider.cs` (guardrail call sites, failure messages), `LongRestSweepCoordinator.Run.cs`, `LongRestSweepHostedService.cs`, `ProviderApiKeyResolver.cs`, `ChatClientFactory.cs`, `AuditEndpoints.cs`, `GuardrailAuditLogger.cs`, `EmbeddingsResetEndpoints.cs`, `WebWorkflowEndpoints.cs`, `ToolInvokeEndpoints.cs`.

## Findings (12)

| # | Severity | Where | One line |
|---|---|---|---|
| 1 | Important | `OpenAi/OpenAiStreamErrorMapper.cs:69`, `OpenAiV1Endpoints.cs:279-292,1194,1209`, `ModelCallExecutor.cs:599-606,653,745,778` | `/v1` turns a context overflow and a daily-budget refusal into `429` + "Inference failed. See server logs for details." |
| 2 | Important | `Guardrails/GuardrailsPipeline.cs:541,544,157-175,469-491` | PII detectors reject epoch timestamps and long IDs, scan the whole transcript, and the error names no kind |
| 3 | Important | `LongRestProviderFailure.cs:84-93`, `LongRestProviderClientFactory.cs:104-107`, `LongRestMaintenance*.cs` | A parked Long Rest sweep cannot be diagnosed: HTTP status discarded, no provider/model, swallowed exceptions, no logger |
| 4 | Minor | `ModelCallExecutor.cs:196-208` (+3 more sites) | Failure log has no provider, model or HTTP status while the message sends the operator to the logs |
| 5 | Minor | `SessionAttachmentTurnService.cs:455-467,511-518`, `SessionContextPinMaterializer.cs:930-934` | Attachment materialization swallows the exception and drops the extractor's own failure reason |
| 6 | Minor | `SemanticSpellRouter.cs:75,84,99,219` | Persistent embedding failure of an opt-in feature logs only at Debug |
| 7 | Minor | `WizardIntelligenceProvider.cs:8769`, `Guardrails/GuardrailsPipeline.cs:19` | Input-stage guardrail audit records never carry the session id |
| 8 | Minor | `Guardrails/GuardrailsPipeline.cs:288-294,357-364,486-490` | Allowed-topic miss is reported as "matched"; an invalid blocked-topic regex silently disables the rule |
| 9 | Minor | `MemoryDelegation/MemoryDelegationEndpoints.cs:94` | Every refusal message is replaced by one constant, including body-shape and validation faults |
| 10 | Minor | `OpenAi/OpenAiChatRequest.cs:39-40`, `OpenAi/OpenAiEmbeddingRequest.cs:7-17` | `logprobs`, `top_logprobs` and embeddings `dimensions` are accepted and silently ignored |
| 11 | Minor | `OpenAi/OpenAiModel.cs:26-29` vs `API.md:445` | The docs promise a `reasoning` object that does not exist |
| 12 | Minor | `GrimoireTurnWriter.cs:918,943,1255,1335` | Begin/finalize failure logs carry no session id, entry id or error code |

### 1. `/v1` hides context overflow and daily-budget refusals behind `429` + "Inference failed" (Important)

**Claim.** The native route returns the actionable text for `Hub.ContextBudgetExceeded` and `Budget.Exceeded`. The OpenAI-compatible route replaces both with `api_error` / `inference_failed` / "Inference failed. See server logs for details." and keeps the 429 status.

**Evidence.**
- `OpenAiStreamErrorMapper.Map` has arms for reasoning, client tools, guardrails and structured output; everything else lands in `_ => new OpenAiErrorDetail(ResolveMessage(error), "api_error", null, "inference_failed")` (lines 69-73), and `ResolveMessage` only lets four fixed strings through (lines 10-16, 85-98).
- Buffered path: `OpenAiV1Endpoints.cs:279-292` maps through `MapPublicOpenAiError` (default arm, line 1194) and `ResolvePublicInferenceFailureMessage` (default arm, line 1209) and takes the status from `ArcanumErrorMapper.ResolveStatusCode`, which gives 429 for both codes (`ArcanumErrorMapper.cs:134-135, 320-321`).
- Probe P1 (`FakeIntelligence.NextFailure`): `/v1/chat/completions` returned `429 {"error":{"message":"Inference failed. See server logs for details.","type":"api_error","param":null,"code":"inference_failed"}}` for both codes. `/api/intelligence/ping` returned 429 with the real text (`Context budget exceeded: 9000 accounted tokens (8000 input + 1000 reserved) exceed the 8192-token window ...` and `Daily budget of 5.00 USD reached.`). The streaming error frame (mapper called directly) gave the same generic text.
- Docs: DESIGN.md:1707-1708 says the context-boundary error "names ... the smaller-request, compaction, or model-selection action", and the budget error "reports the operator-owned token/cost policy". The native message names the counts but no action (`TurnContextGuards.CheckContextBudget`), and neither survives on `/v1`. `API.md:917` documents 429 for `Hub.ContextBudgetExceeded` only as "Confirmed Covenant content could not fit", but the code uses it for every overflow.
- The same code is also used for internal staleness, not overflow: `ModelCallExecutor.cs:599-606` ("The provider payload or its authority changed after materialization; replan before dispatch."), `:653`, `:745`, `:778`, `ModelCallExecutor.LongRest.cs:235`, `ModelCallExecutor.CampaignMaintenance.cs:109`.

**Scenario.** An editor or agent framework talking to `/v1` sends a long transcript. It gets 429, which OpenAI SDKs retry with backoff (the same deterministic refusal, three times by default) and then shows "Inference failed. See server logs for details." The client cannot tell overflow from rate limiting or from the operator's daily budget, so it cannot compact or switch model, and an OpenAI-style `context_length_exceeded` handler never fires. The operator has to read the server log to learn the cause.

**Fix.** On `/v1` only: map `Hub.ContextBudgetExceeded` to 400 `invalid_request_error` / `context_length_exceeded` with the counts message; map `Budget.Exceeded` to `insufficient_quota` with a message naming the daily limit key (a `Retry-After` for the next UTC day if wanted). Give the internal stale-payload and stale-admission conditions their own code (the shared code would otherwise be mis-mapped by the new arm). Add the compaction/model action to the native overflow message (`arcanum session compact`, raise `contextWindowLimit`, pick a larger model). Update API.md:917. Effort S.

### 2. Guardrails PII detectors reject ordinary technical numbers and whole transcripts (Important)

**Claim.** With `Features:Guardrails` on and the default `DetectPii: true`, any text containing a 10-digit run (a phone match) or a 13-19 digit run (a card match, no Luhn check) is rejected, and on `/v1` the scan covers every message including old assistant and tool messages. The rejection says "personally identifiable information detected. Redact PII and retry." and names neither the kind nor the span. The only operator remedy is switching all PII detection off.

**Evidence.**
- `GuardrailsPipeline.cs:541` `\b(?:\d[ \-]?){13,19}\b`; `:544` the phone pattern accepts a bare `\d{3}\d{3}\d{4}`; `ConcatenateMessageText` (lines 157-175) joins every message regardless of role; `WizardIntelligenceProvider.cs:8754-8770` passes all `StatelessMessages`; `BuildError` (469-491) ignores the violation type except for the `pii-` prefix.
- Probe P4: `epoch 1729800000` -> `Guardrails.PiiDetected` (audited `pii-phone`); `created_at_ms=1729800000123`, `order 4000123456789012` and `build number 20261024123456` -> `Guardrails.PiiDetected` (`pii-credit-card`). The Wizard probe sent a stateless transcript whose earlier assistant message said "It started at epoch 1729800000." and the third user turn "thanks, and when did it end?" was rejected with the same PII error.
- Config: `security.guardrails.detectPii` is a single boolean (Compendium.README.md:129); there is no per-detector switch and no allow pattern.

**Scenario.** An operator turns guardrails on to catch pasted card numbers. A developer pastes a log with a Unix timestamp; the request is refused. In an agent conversation over `/v1` the assistant's own earlier output or a tool result contains a timestamp, and every later turn of that conversation is refused for good because the whole transcript is rescanned. The user cannot find the offending text from the message.

**Fix.** Add a Luhn check and word-boundary rules for the card/phone patterns, scan only the new user turn(s) (or messages after the last assistant message), put the violation type in the message ("pii-phone detected"), and add per-detector toggles. Effort M.

### 3. A parked Long Rest sweep cannot be diagnosed from outside (Important)

**Claim.** The provider status is parsed and thrown away, the error text names no provider or model, a credential-resolution exception is swallowed without a log (and carries a message that promises logs and blames reachability), and the whole maintenance layer has no logger.

**Evidence.**
- `LongRestProviderFailure.Classify` reads the status (`ParseStatus`, lines 97-116) and returns only `ProviderRejected` ("The Long Rest provider rejected the call.") or `ProviderUnavailable` (lines 84-93). Probe P3: HTTP 401, HTTP 404 and HTTP 400 all gave the identical `LongRest.ProviderRejected` error.
- The log at `ModelCallExecutor.LongRest.cs:104` and `:185` is `Long Rest provider call failed ({ExceptionType}).`; the provider and model are in scope (`chat.Context.Provider`, `selected.Model`) and are not logged.
- `LongRestProviderClientFactory.ResolveCredentialAsync` (lines 88-108) catches every exception and returns `Hub.Error` / `NativeGenericFailure` ("Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details."). The class takes no logger (constructor lines 24-31), so nothing can be logged. Probe P3 confirmed the text for a resolver that throws.
- `LongRestMaintenance` takes no `ILogger` (`LongRestMaintenance.cs:19-22`). `SettleUnresolvedAsync` and `ReleaseUnsentAsync` swallow their own failures (`LongRestMaintenance.Dispatch.cs:239-261`), and `KeepClaimAliveAsync` turns any exception into `dispatch.CancelAsync()` with no record (lines 311-314). The coordinator turns a refusal into `RequiresAttention(refusal.Error.Code)` without logging (`LongRestSweepCoordinator.Run.cs`, the `catch (Refused refusal)` block), and there is no `ILogger` anywhere in `Infrastructure/Operations/LongRest/`. The hosted service only logs thrown exceptions, by type (`LongRestSweepHostedService.cs:167-172`).
- Docs: Command Reference line 1134 tells the operator for `LongRest.ProviderRejected` to "Fix the provider configuration (model, credential, request limits)". Three candidates, nothing to choose between them. A credential-resolution failure surfaces as terminal code `Hub.Error`, which is not in the table (the "any other `LongRest.*` code" row does not match it).
- With the real `ProviderApiKeyResolver`, a store failure returns a null key (status result, not a throw), so the common credential problem arrives as an HTTP 401 `ProviderRejected`, which is the case where the status matters most.

**Scenario.** After a Keychain change the Long Rest embedding call returns 401. `arcanum operation show` and `doctor` report `LongRest.ProviderRejected`; the log has `Long Rest provider call failed (ClientResultException)`. The operator cannot tell a bad key from a missing model or an over-large request without reproducing the call by hand.

**Fix.** Keep the status in the error (message "rejected the call (HTTP 401)" or a field on `Error`), log provider name, model and status once per refusal, add an `ILogger` to `LongRestMaintenance`/`LongRestProviderClientFactory`, log the swallowed catch sites at Warning, and document `Hub.Error` for the credential case or give it a `LongRest.*` code. Effort M.

### 4. Provider-call failure logs name neither provider, model nor status (Minor)

`ModelCallExecutor.ExecuteBufferedAsync` logs `Model call {ModelCallId} failed for purpose {Purpose}; exception type {ExceptionType}.` (lines 196-200; same shape at `ModelCallExecutor.CampaignMaintenance.cs:101-102`) and returns `NativeGenericFailure`, whose text ends "See server logs for details." (`PublicInferenceErrorMessages.cs:9-10`). Probe P5 (provider `cloud-a`, model `fast-model`, client throws a 404 `ClientResultException`) logged exactly `log [Error] Model call 71bc12e6... failed for purpose LexiconExtraction; exception type System.ClientModel.ClientResultException.` The context holds `Provider.Name` and `Model`, and `InferenceProviderFailureMessage.TryGetHttpStatus(ex)` gives the status. Callers add `SemanticRouter preflight failed (Hub.Error)` and `LexiconEntityExtractor preflight failed (Hub.Error)` (SemanticRouter.cs:123-125, LexiconEntityExtractor.cs:86-88), which carry nothing more. `OpenAiRequestAugmentingHandler.cs:96-97` logs "Provider rejected strict structured-output request; retrying once without strict: true." with no host or provider, so an operator with several providers cannot tell which one lacks `strict` support. A wrong `FastModel` therefore fails every router and Lexicon preflight with a stream of ERROR lines that cannot be traced to it. The main chat path is better: its user message includes the provider and status. Fix: add `{Provider}`, `{Model}`, `{HttpStatus}` to these log templates (S).

### 5. Attachment materialization swallows the error and drops the extractor's reason (Minor)

`SessionAttachmentTurnService.MaterializeReferenceAsync` ends in `catch (Exception)` with no variable and no logger parameter (lines 511-518); the whole turn then fails with `Validation.AttachedFiles` and "Attachment 'x' could not be read for model context. Retry the request or refresh the attachment." (the `WizardIntelligenceProvider.cs:2854-2868` caller). `PrepareAsync` in the same class does log its failures with session and entry ids (lines 330-337), so this is an omission, not a policy. For documents, the extraction's own fixed reason (`extraction.FailureReason`, "Deferred: try again", and so on) is discarded at lines 461-467 ("could not be extracted for model context"), and again at `SessionContextPinMaterializer.cs:930-934` ("Document text extraction failed or produced no text."). The model-facing path already has the right helper, `SessionAttachmentToolInjection.NotExtractedReason` (lines 247-254). Corrupt or undecryptable blobs and a busy document worker look identical, "retry" is wrong advice for the first, and nothing is logged. Fix: reuse `NotExtractedReason`, pass a logger and log the exception type with the attachment id. Effort S.

### 6. Semantic spell routing degrades silently (Minor)

`SemanticSpellRouter` logs a failed prompt embedding (line 99), an unavailable provider (84), an unavailable catalog cache (75), a catalog mismatch (154) and any unexpected exception (219, with the exception object) all at Debug. Each failure silently falls back to the paid full-catalog LLM router, which is the cost the opt-in feature (`features.semanticSpellRouting`) exists to avoid, and a persistent embedding outage is invisible at the default log level. The comment documents the fallback, not the level. Other optional layers in the Wizard log the same class of failure at Warning (for example "RAG query embedding threw; semantic retrieval for this turn will be skipped", `WizardIntelligenceProvider.cs:5812`). Only the dimension-mismatch case is Warning. Fix: log embedding failures at Warning, throttled to once per configuration epoch. Effort S.

### 7. Input-stage guardrail audit records have no session id (Minor)

`FilterGuardrailsInputAsync` builds `new GuardrailAuditContext(null, request.Model)` (`WizardIntelligenceProvider.cs:8769`); the output stage passes `sessionId?.ToString()` (line 8792). The pipeline's own doc says the context exists "so the audit record can name the session and model a violation blocked" (`GuardrailsPipeline.cs:14-19`). The Wizard probe sent a request with `SessionId` set and a blocked SSN: `audit record: stage=Input type=pii-ssn sessionId='<null>'`. `GET /api/guardrails/audit?sessionId=` therefore never returns input-stage rows, and The Forge audit browser's session filter and "open session" action (`AuditBrowserViewModel.cs:277,337,350`) have nothing to act on for the most common violation. `request.Model` can also be null when the default model is used. Fix: pass `request.SessionId?.ToString()` and the resolved model. Effort S.

### 8. Guardrail topic rules: wrong wording and a silent off switch (Minor)

An `AllowedTopics` miss adds a `topic-allowed` violation (lines 288-294), but `BuildError` reports every non-PII violation as "content matched a guardrail policy (toxicity or topic)" (lines 486-490). Probe P4: allowed-topic miss -> "Input rejected: content matched a guardrail policy (toxicity or topic)." An operator whose allow list rejects everything is told the opposite of what happened. A `BlockedTopics` entry that is not a valid regex is skipped (`MatchTopic`, lines 357-364; the skip is pinned by `FilterInputAsync_BlockedTopics_InvalidRegex_IsSkippedNotThrown`), nothing validates it at load (`ConfigurationValidator` has no guardrail rule), the doctor does not look at it, and the only signal is one Warning per scan per request. Probe P4: `BlockedTopics: ["(secret"]` allowed "tell me the secret now" three times and logged three warnings. The docs never say the patterns are case-insensitive .NET regexes with a 500 ms budget (Compendium.README.md:132-133 "topic-pattern allowlist/blocklist"; API.md:953 only calls them "operator topic patterns"). The skip-and-warn behaviour is an earlier ruling; the new angle is the missing config-time signal. Fix: add a "did not match any allowed topic" message, validate patterns in `ConfigurationValidator` with a message naming `security.guardrails.blockedTopics[i]`, log an invalid pattern once per epoch, and document the semantics. Effort S.

### 9. Memory-delegation refusals carry one constant message (Minor)

`MemoryDelegationJsonResult.ExecuteAsync` replaces every failure with `new Error(code, "The memory delegation request could not be completed.")` (`MemoryDelegationEndpoints.cs:94`). The text is not content-bearing for most refusals: unknown JSON member, wrong `Content-Type`, oversize body (all reported as `MemoryDelegation.InvalidGrant`, 400), and the `Validation.*` errors from `PingRequestPreflightValidator`, whose messages are the most specific in the codebase ("measured 1,200,000 UTF-8 bytes; limit 1,048,576 ... Split the content ..."). Probe P2: an unknown member, a `text/plain` body and a specific validation error all produced the same envelope text. API.md:561 says refusals are "content-free", which supports hiding Covenant material but not hiding which field is wrong. A developer integrating with the endpoint (strict closed DTO, `UnmappedMemberHandling.Disallow`) gets no hint for a typo. Fix: keep the constant only for `MemoryDelegation.StaleSource`/`Covenant.*`/authority codes; return the specific message for body-shape and `Validation.*` codes (a distinct reason for each body fault in `MemoryDelegationRequestJson`). Effort S.

### 10. Accepted-but-ignored OpenAI parameters (Minor)

`OpenAiChatRequest` declares `logprobs` and `top_logprobs` (lines 39-40); `ToPingRequest` never reads them and every response sets `Logprobs: null`. No document mentions logprobs. The same validator rejects `n != 1`, `tools` and `tool_choice` with a named-parameter 400, so the surface is explicit about some unsupported parameters and silent about others. For embeddings, `OpenAiV1EmbeddingsEndpoints.cs:182-191` logs a server-side warning when `dimensions` is sent and returns the full-width vector; the only description is the DTO comment (`OpenAiEmbeddingRequest.cs:7-10`) and API.md does not mention it. A client that asked for 256 dimensions to fit an index gets the configured width and fails later at insert. Fix: reject (`unsupported_parameter`) `logprobs: true` and a `dimensions` value different from the configured width, or at least document both. Effort S.

### 11. `/v1/models` reasoning fields: docs and code disagree (Minor)

API.md:445 says `/v1/models` adds "the same optional typed `reasoning` capability object returned by the native endpoint". Neither endpoint has such an object: `OpenAiModel` carries flat `reasoning_wire_dialect` and `reasoning_max_budget_tokens` (`OpenAiModel.cs:26-29`), and `ModelInfoDto` carries flat `WireDialect` and `MaxBudgetTokens`. The other reasoning capabilities (`controlSupport`, `supportsSummary`, ...) are not exposed anywhere. A client written from the docs finds no `reasoning` key. Fix: correct API.md:445 and :36, or emit the object. Effort S.

### 12. Turn-writer failure logs lack identifiers (Minor)

`BeginAssistantReplyCoreAsync` logs `Grimoire could not begin assistant reply for model {ModelName}.` at the two failure sites (`GrimoireTurnWriter.cs:918, 943`) and returns the error to the caller, dropping its code from the log; `:1255-1257` and `:235` log only `{ErrorCode}`; `:1335` logs `Grimoire could not finalize assistant entry for model {ModelName}.` with the exception but no session or entry id (the `ex` argument is also withheld for protected turns). The same file's other warnings do carry `AssistantEntryId` (line 307). An operator matching a client's 409/500 to a server line has only a timestamp. I did not run the planned probe (rebuild queue); the templates are literal. Fix: add `{SessionId}`, `{AssistantEntryId}` and `{ErrorCode}`. Effort S.

## Considered and not reported

- `completion_window` is ignored on `/v1/batches`, but DESIGN.md:4000 documents it as metadata only.
- `GET /api/guardrails/audit` and `/api/audit` clamp `limit` instead of returning 400; both endpoints match and the docs describe the shared contract.
- `arsenal` silently treats an unnormalizable `workingDirectory` as no workspace: documented at API.md:389.
- `ProviderFailureClassifier` does not fall back on HTTP 5xx or 429: the comment states it is deliberate.
- Text attachments truncated without a notice (`SessionAttachmentToolInjection.cs:207-231`): attachment content is capped at the same 1 MiB at validation, so the branch is not reachable in practice.
- The routers' prompts mix "tool" and "spell"; wording only.

## What I checked and found sound

- `OpenAiChatCompletionRequestValidator`: names the offending parameter and index for role, content-part type, `n`, tools and tool_choice; `model_not_found` is 404; unsupported content part types are rejected rather than dropped.
- `EmbeddingsResetEndpoints`: requires `confirm=true`, lists the allowed scopes in the error, accepts both spellings.
- `/intelligence/human-response`: 400/404/200 and the byte limit match API.md:385; the 404 text names all three causes.
- `GuardrailsAuditEndpoints`: 400 `Validation.InvalidQuery`, cursor header and case-insensitive filters match API.md:953; page failures all carry the same code, so passing only the message loses nothing.
- Mana counter: the 400 and the unconfigured-model fallback match API.md 8.10.1; context inspect resolves Campaign authority per the 8.10.2 table.
- `LongRestMaintenance.CheckLongRestWindow` messages are actionable ("Raise the provider's contextWindowLimit ..."); `LongRestConfigurationEpoch.ResolveRecipe` returns the shared `Describe` sentence; the Command Reference code table matches the codes the code emits (apart from the `Hub.Error` gap in finding 3).
- `PingRequestPreflightValidator` messages carry the measurement, the limit and the next step.
- `InferenceTokenizerResolver` logs a bad encoding once and caches the fallback; `InferenceContextBuilder` compression warnings carry session id and token counts.
- Guardrail timeouts fail closed and are audited as `pii-undetermined`/`topic-blocked`, matching API.md:953.
- `ModelCallExecutor` cancellation handling: a timeout of an auxiliary preflight reaches the callers' `catch (OperationCanceledException)` branches and logs as Information, so the "timed out" lines are reachable.
- `SessionContextPinMaterializer` frames carry status and a diagnostic for every degraded pin (missing, modified, truncated, unsafe), and the per-turn deferral is announced to the model.

## Probes run

All in-process xunit probes (`results/A/U02-usability-probes/`): P1 `/v1` vs `/api` for `Hub.ContextBudgetExceeded` and `Budget.Exceeded` through `ArcanumWebApplicationFactory`; P2 `MemoryDelegationRequestJson.ReadAsync` and `MemoryDelegationEndpoints.Failure` executed against a `DefaultHttpContext`; P3 `LongRestProviderClientFactory` with a throwing resolver and `LongRestProviderFailure.Classify` for 401/404/400; P4 `GuardrailsPipeline` for allowed-topic text, four numeric inputs and an invalid regex with a capturing logger; P5 `ModelCallExecutor` with a throwing client and a capturing logger; Wizard probe for the input-stage audit session id and the transcript rescan. P6 (turn-writer log) was not run: the shared build queue made one rebuild take 14 minutes.
