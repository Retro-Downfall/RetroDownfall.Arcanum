# U03 (Arcanum.Api, Intelligence (3)) - USABILITY review

Tree: `.claude/worktrees/codebase-review`, branch `claude/codebase-review-2026-10` at `ac0d2d8e`. Read-only; probes ran in a scratch worktree (`scratchpad/review/wt/U03-usability`, now removed).

## Unit definition (reconstructed)

`scratchpad/review/units.json` did not exist when this reviewer ran (the scratchpad was empty), so the unit was reconstructed from the workflow arguments recorded in the session log (`workflows/wf_4f3c2f94-71f.json`): U03 = "Arcanum.Api / Intelligence (3)", 7134 lines. U01+U02+U03+U04 are the top-level `Intelligence/*.cs` files in C-locale sorted order (U04 is `WizardIntelligenceProvider.cs`, 8904 lines; the subdirectories belong to U57/U58). Cumulative sums reproduce the recorded sizes exactly (11879 / 11444 / 7134 / 8904). U03 is therefore:

| File | Lines |
|---|---|
| ToolExecutionPipeline.cs | 2490 |
| ToolLoopProgressDetector.cs | 138 |
| ToolResultMaterializer.cs | 368 |
| TurnAccountingAmbient.cs | 101 |
| TurnAccountingHandle.cs | 632 |
| TurnAmbientSet.cs | 132 |
| TurnContextGuards.cs | 244 |
| TurnIdempotencyAmbient.cs | 63 |
| WeaveService.cs | 375 |
| WebResearchWorkflowService.cs | 1349 |
| WebWorkflowEndpoints.cs | 233 |
| WizardIntelligenceProvider.ContextPreview.cs | 1009 |
| **Total** | **7134** |

The published binary and `units.json` were absent, so no binary was run; every probe is an in-process xunit probe (sources archived in `results/A/U03-usability-probes/`, output in `probe-output.txt` there).

## Coverage

All 12 files were read in full (every line). Owning docs read: DESIGN section 11.27 (native web research), 10.7.3 (loop terminal table, ward/tool order), 10.2.1 (graceful partial tool failure, tool-result materialization), the "Operator-safe errors" paragraph, API.md 8.10.2/8.10.3 and the error-code table, Command Reference `search`/`browse`/`research`, ConstraintReduction row for the 8,192-byte tool-result unit. Cross-read for context: `StructuredToolResults.cs`, `UnifiedDiffParser.cs`, `ArcanumErrorMapper.cs`, `OpenAiV1Endpoints.cs` (error mapping), `OpenAiStreamErrorMapper.cs`, `ChatClientFactory.cs`, `BudgetReservationService.cs`, `WebWorkflowModels.cs`, and the CLI `WebWorkflowCommands.cs` frame consumer.

Baseline: 199 existing tests matching `WebResearch|TurnContextGuards|ToolResultMaterializer|ToolLoopProgress|WebWorkflowEndpoint|WeaveService` pass on the unmodified tree, so none of the findings below is caught by an existing test.

## Probes run (scratch worktree, `--no-build`, stdin `</dev/null`)

| Id | What it drives | Result |
|---|---|---|
| P1 | `TurnContextGuards.CheckContextBudget`, `ArcanumErrorMapper`, `/v1` mappers | text has no remedy; HTTP 429; `/v1` buffered code `inference_failed`; `/v1` stream message "Inference failed. See server logs for details." |
| P2 | `WebResearchWorkflowService.ResearchAsync` with a reader that times out for one of two citations | no frame mentions the failure; result.citations lists the unread source; synthesis prompt carries an empty `Source [2]` block |
| P2b | same with synthesis `FinishReason="length"` | result.truncated=False, answer cut mid-sentence delivered as complete |
| P3 | Sanctum denial text for an unparsable `apply_patch`, via reflection on `SanctumDenialMessage`, plus `UnifiedDiffParser.Parse` | model sees a Sanctum block; parser's own message ("The hunk header is malformed." / "...Split the diff into smaller apply_patch calls.") is lost |
| P4 | `TryParseRefreshSelector` with a malformed `attachmentId` | rejected with the "Exactly one of attachmentId or logicalKey" text |
| P5 | `ToolResultMaterializer.Materialize` on 248,900 chars | 8,190 chars: head + `\n...\n` + tail + marker; no sizes, JSON broken |
| P6 | `WeaveService.EmbedAsync` with a 401 and a failing `CompleteRunAsync` | client text "provider is unavailable"; Warning without provider/model; accounting completion failure logged at **Debug** with no run id |
| P8 | `PreviewContextAsync` with embedding failing vs retrieval not configured | identical "No effective content was available for this source."; aux row says embedding "was invoked" in both cases |
| P9 | `SearchAsync`/`BrowseAsync` validation | "valid domain filters" for `https://example.com`; same text for `resultCount=50` |

Raw output: `results/A/U03-usability-probes/probe-output.txt`.

## Findings

### 1. Important - Sanctum preflight reports a malformed or oversized `apply_patch` as a Sanctum block (ToolExecutionPipeline.cs:2041-2050, 2203-2253, 2385-2393)

`ValidateToolPathsAndNetworkAsync` parses the patch to list touched paths. When parsing fails it discards the parser's `Code`/`Message` (`TryParseApplyPatchManifest` returns only `parsed.Success`) and raises `UnparsablePatchDenial`, which `SanctumDenialMessage` renders for the model as:

`The Sanctum Guard has blocked this action: PatchPathPreflight - apply_patch target paths could not be validated within the active policy limits..` newline `The Dungeon Master must update the Sanctum config to allow this operation.`

The parser knows exactly what is wrong (P3: `invalid_hunk` "The hunk header is malformed."; `patch_too_large` "...the per-request allocation limit is 1024 bytes... Split the diff into smaller apply_patch calls."). With the Sanctum off the tool itself returns that diagnosis; with the Sanctum on the model gets a policy refusal instead. A malformed unified diff is the most common model mistake with this tool, so every Sanctum-enabled Campaign hits it. The model cannot repair the patch, tells the user to change Sanctum config, or retries blindly; the operator reading the transcript is sent to the wrong control. The footer is also wrong for `PolicyUnavailable` (Sanctum unreadable): there the fix is repairing the Campaign, not "allowing" the operation. Double period is the same defect's cosmetic trace.

Fix: carry the parser's code and message into the denial reason (and do not wrap it as a policy breach), choose the footer by `SanctumDenialKind`, say "operator" not "Dungeon Master" to the model. Effort S. Regression every current test passes: any change to the denial text, since no test asserts it for an unparsable patch.

### 2. Important - Research keeps, and cites, sources whose page was never read (WebResearchWorkflowService.cs:506-553, 599-614)

Only `SsrfBlocked` drops a citation. Every other read failure (timeout, 403 bot protection, JavaScript-only shell, unsupported content, redirect overflow, response too large) and the case of no ReadUrl-capable reader leave `content = ""` and fall through to `sources.Add(...)` after a `rendering` progress frame. P2: frames are `fetching 2 of 2` then `rendering 2 of 2`; the result's `citations` lists `[2] https://bad.test/b`; the synthesis prompt contains an empty fenced `Source [2]` block (URL and title only). DESIGN 11.27 (line 4047) promises "`Source [n]` numbering stays contiguous over the sources that were actually read" and that a refused citation is "not kept as an empty source"; the Campaign-binding test file header says the same. No per-source frame names the URL or the outcome at all, so even denied sources are only identifiable by index (the operator needs the Sanctum breach list to learn which host to allow).

Scenario: `arcanum research "..." --sources 5` where two pages 403 (routine). The CLI prints "Fetching/Rendering source 2 of 5" for both, the answer cites `[2]` from a URL+title the model never read, and nothing tells the operator.

Fix: on any non-success read emit a `source_unreadable` progress frame carrying host and error code, drop the source like a denied one (or keep it out of `citations`), and emit one frame when no reader exists; include the host in `source_denied`. Effort S. Plausible regression all current tests pass: a reader failing with a non-SSRF code leaves an empty cited source.

### 3. Important - Context-window overflow is HTTP 429, hidden behind a generic text on `/v1`, and names no remedy (TurnContextGuards.cs:213-217)

`CheckContextBudget` returns `Hub.ContextBudgetExceeded` with `Context budget exceeded: 10024 accounted tokens (9000 input + 1024 reserved) exceed the 8192-token window using profile 'test'.` (P1). Problems:

- The failure is permanent for the request (the prompt must shrink), yet `ArcanumErrorMapper` maps it to 429 (ArcanumErrorMapper.cs:134). Standard OpenAI-compatible clients (the main `/v1` consumers) retry 429 with backoff and treat it as a rate limit; the real rate limiter at ApiBootstrapper.cs:107-138 emits `rate_limit_exceeded` with `Retry-After`, so the two are indistinguishable.
- On `/v1/chat/completions` the code falls through `MapPublicOpenAiError` to `api_error`/`inference_failed` with "Inference failed. See server logs for details." (buffered and stream, P1), so the numbers that explain it are removed and no `context_length_exceeded` signal exists.
- API.md:917 documents 429 for this code only as "Confirmed Covenant content could not fit". DESIGN 1708-1709 says the context-boundary error "names the provider/model request fact or context admission boundary and the smaller-request, compaction, or model-selection action" and identifies "the exact ... configuration, or recovery action". The message names neither the provider/model (only a token-profile id) nor any action. The same code is also used for internal integrity failures (ModelCallExecutor.cs:599-605, 653, 745, 778: "the provider payload ... changed after materialization"), which a client cannot tell from an overflow.

Fix: add provider/model and the remedy to the text ("shorten the prompt, `arcanum session compact`, start a new Session, choose a model with a larger window, or raise `providers.contextWindowLimit`"); map a true overflow to 400/413 with a distinct `/v1` code `context_length_exceeded` and a safe message (it contains only counts), keep 429 for budget/rate limits, split the internal-integrity uses onto their own code; update API.md and `CovenantErrorContractTests`. Effort M (contract change).

### 4. Important - Research reports a token-budget-truncated synthesis as complete (WebResearchWorkflowService.cs:569-638)

`ExecutePromptAsync` returns `PromptTurnResult.FinishReason` (`"length"` when the provider cut the answer) and `Warnings`. `ResearchAsync` ignores both: `Truncated = searches.Any(search => search.Truncated)` reflects only the search provider. P2b: finish_reason `length` produces `result.truncated=False` and the answer "The 2026 change was that the committee decided to" with no progress or warning frame. The default `--token-budget` is 2000 and reasoning models spend it on reasoning, so a clipped Markdown report is an ordinary outcome. Secondary: `Model = request.Model ?? DefaultModel ?? "server-default"` echoes the request rather than the model used (`PromptTurnResult` does not carry it), and `usage.costUsd` is search-provider cost only while the token counts include synthesis; API.md enumerates none of the result fields, so `truncated`, `model` and `costUsd` meanings are undocumented.

Fix: set `Truncated` (and emit a warning progress frame) when `FinishReason == "length"`, forward `Warnings`, document the result fields, label cost as search-only. Effort S.

### 5. Minor - Context preview cannot say why retrieval contributed nothing (WizardIntelligenceProvider.ContextPreview.cs:906-915, 1006; WizardIntelligenceProvider.cs:5768-5812)

P8: with the embedding call failing, and with retrieval features off, `WorkspaceRag`, `TapestryRag` and `LexiconSaga` all report `included=false` with "No effective content was available for this source."; the only trace of the failed embedding is an auxiliary row that says the embedding "was invoked" (added before the call, never updated) and a Debug log ("RAG query embedding failed", WizardIntelligenceProvider.cs:5797). `context inspect` / `run --dry-run` exist to explain exactly this; only `noRetrieval` and "no Session/Workspace" get explicit reasons. In a live turn the same failure is also Debug-only and emits no status.

Fix: thread feature/availability and the embedding outcome (code) into `ResolveSourceReason` and the auxiliary row, log embedding failure once per turn at Warning. Effort M.

### 6. Minor - Embedding failures are under-diagnosed and a failed accounting completion is invisible (WeaveService.cs:233-240, 254-257)

(a) `catch (Exception) -> Warning "Embedding provider call failed; The Weave will be treated as unavailable..."` carries neither the provider nor the model, and the client text is "The embedding provider is unavailable" for a 401, a 404 model or a timeout alike (P6). (b) `CompleteAsync` failure in the `finally` is logged at **Debug** without run id or reservation id (P6 shows it), although `TurnAccountingHandle` documents "Failures are logged by the caller" and the streaming path logs the same failure at Warning with the session id (WizardIntelligenceProvider.cs:4843). The handle's own comments say an unclosed run stays `Running` (no expiry sweep for runs) and that nothing sweeps expired reservations in production (`SweepExpiredAsync` has no production caller), so the operator-visible effects (budget refusal with low actual spend, a pinned Running run) have no log to explain them.

Fix: LogWarning with RunId/ReservationId/provider/model; distinguish 401/403/404 in the client message. Effort S.

### 7. Minor - Web workflow validation messages do not name the field or the rule (WebResearchWorkflowService.cs:698, 962, 158)

P9: `includeDomains:["https://example.com"]` and `resultCount:50` both return "Search requires a query, count 1-20, freshness day|week|month|year, and valid domain filters."; research returns one sentence listing four requirements for any violation. The domain rule (host only, no scheme/path/port/whitespace, at most 20 entries, at most 253 chars) appears nowhere. The JavaScript refusal says "retry with --render static", a CLI flag, to an HTTP caller (`renderMode: "static"` is the API field).

Fix: report the first failing field with its rule and the offending value class; use API field names in API messages. Effort S.

### 8. Minor - Model-facing tool error text gives the model no next step (ToolExecutionPipeline.cs:672-677, 1037-1077, 1263-1266; ToolResultMaterializer.cs:19-20, 198-200)

(a) `refresh_session_file` with a malformed `attachmentId` and no `logicalKey` is rejected as "Exactly one of attachmentId or logicalKey is required." (P4) although the model supplied one; a bad GUID plus a valid `logicalKey` silently uses the key. (b) A call to an unadvertised tool returns `No local tool registered for 'x'.` without listing the advertised names. (c) The generic truncation marker `[truncated: tool result exceeded token/byte budget]` (P5: 248,900 chars become head + `...` + tail + marker) states neither how much was omitted nor how to obtain the rest, and splits JSON mid-token, whereas the structured envelopes carry omitted counts and the ConstraintReduction row for the same 8,192-byte unit (line 396) says it "reports truncation/omitted counts". The class summary also says a tokenizer may be "injected", but there is no injection point.

Fix: name the malformed selector, list advertised tool names (bounded), put original/shown sizes and a "narrow the request" hint in the marker (`ToolResultMaterialization` already has the numbers). Effort S.

### 9. Minor - Research failure frames omit the identifiers and spend the operator needs (WebWorkflowEndpoints.cs:157-175; WebResearchWorkflowService.cs:388-398)

The terminal frame says "Check the host logs for details." but carries no trace id, and the log line is `"Web research stream failed after {TraceId} started"` bound to `httpContext.TraceIdentifier`, while every envelope on the sibling `/api/web/search|browse` routes reports `Activity.Current?.Id`; there is nothing to correlate. The `WebResearch.BudgetExceeded` frame says "Research stopped after reported provider cost exceeded the $0.02 limit." without the amount spent, and the already-billed search results are discarded with no synthesis (the Command Reference lists "an explicit token/cost policy is reached" in the same sentence as the stop conditions that do proceed to synthesis, so the operator cannot tell which behaviour to expect). All research failures, including request validation, are HTTP 200 with an `error` frame; API.md's status table implies HTTP statuses for the same codes and does not say so.

Fix: add `traceId` to `WebResearchStreamFrame` and log with the same id; include spend so far in the budget frame; state the 200-with-error-frame contract in API.md 8.10.3. Effort S.

### 10. Minor - The no-progress stop names no tool (ToolLoopProgressDetector.cs:37-80; WizardIntelligenceProvider.cs:4254-4262)

The user-facing error is "Tool execution stopped because the latest call/result round repeated a recent round..." and the log carries an 8-byte hash only. The detector holds the tool names but exposes nothing but `RepeatedSignature`. A local model that loops on one tool ends the turn with no hint which tool or arguments looped. Fix: expose `RepeatedToolNames` and include them in the message and the Warning (with session id). Effort S.

### 11. Minor - Silently trimmed tool exchanges can trip the no-progress stop (TurnContextGuards.cs:121-199; WizardIntelligenceProvider.cs:7951) - trace only, not executed

`TryTrimOldestToolExchanges` deletes the oldest call/result pairs from the model's message list with no marker. The model that lost the evidence re-issues the same call; the detector compares against the last 8 rounds' fingerprints (independent of the message list), sees identical calls and results, and ends the turn with `Hub.NoProgressDetected`. Reachable on small-context local models under context pressure. Fix: replace a trimmed pair with a short placeholder tool result ("earlier result removed to fit the context window; re-run if needed") and exclude re-reads of trimmed pairs from fingerprint comparison. Effort M.

### 12. Minor - Model-resolution failures lose their cause (WizardIntelligenceProvider.ContextPreview.cs:150-155; also WizardIntelligenceProvider.cs:709-715)

`ChatClientFactory` throws three distinct code-owned `InvalidOperationException`s (no model resolvable, undefined wire dialect, unsupported provider type for provider 'X') whose messages are actionable. The preview catches and discards them without logging; the live path logs only `exception type InvalidOperationException`. Everything reaches the client as "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel." Fix: log the (safe, code-owned) message and the requested model; for the unsupported-provider case say so. Effort S.

## Checked and found sound

- ToolLoopProgressDetector window (8), SHA-256 round fingerprint, constant-time compare and O(1) memory match DESIGN 10.7.3.
- ToolResultMaterializer arithmetic: P5 output 8,190 chars including marker, within the 2,048-token / 8,192-byte bounds in DESIGN; structured envelopes (`StructuredToolResults.cs`) keep valid JSON and carry omitted counters.
- Tolerant tool-failure policy and its logging (type, call id, no exception message) match DESIGN 1295; `PublicToolFailureMessage` is a documented contract.
- Search/browse attachment failure is non-fatal with `attachmentError` (matches API.md, DESIGN, CLI); `source_denied` frames name the control that refused (matches DESIGN 11.27); research validates before any billable call; question-length bound message states the number and the reason.
- `/api/web/search|browse` use the shared envelope and `ArcanumErrorMapper` statuses; `InvalidBody` is 400.
- `TurnAmbientSet`/`TurnAccountingAmbient`/`TurnIdempotencyAmbient` match DESIGN's per-call re-establishment description; internal exceptions there are programmer-contract errors.
- Preview reasons for `noRetrieval`, Covenant pressure and Covenant no-fit are accurate and test-covered; attunement-excluded tools carry specific reasons (existing test).
- DESIGN section references cited in code comments (2.1, 10.7.3, 11.9, 11.15, 11.27) resolve.
