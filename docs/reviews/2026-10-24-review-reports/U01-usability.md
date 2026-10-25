# U01 (Arcanum.Api, Intelligence 1) — usability review

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e` (read-only). Probes ran in a scratch worktree (now removed); probe sources and raw output are in `scratchpad/review/results/A/U01-probe-sources/` and `scratchpad/review/results/A/U01-usability-probes.txt`.

## Unit membership (reconstructed)

`scratchpad/review/units.json` no longer exists (the scratchpad was empty at start). The unit was reconstructed from the workflow progress note ("all 28 U01 files (11,879 lines)"): the 28 top-level `.cs` files of `src/RetroDownfall.Arcanum.Api/Intelligence/` from `ArcanumInvocationContexts.cs` through `InferenceTokenizerResolver.cs`, ordered by (directory, file name). Their line counts sum to exactly 11,879. Files: ArcanumInvocationContexts, AttachmentSourceSanctum, AuditEndpoints, BatchJsonlRecordReader, BatchProcessingService, BatchRecoveryService, BudgetMonitor, CampaignMaintenancePayloadFingerprint, CampaignMaintenanceProviderCallFreezer, CampaignRollupMaintenance, CampaignRollupProviderClientFactory, CampaignRollupTurnPreparer, ChatClientFactory, ChronicleFrameWire, ClientToolCapabilityValidator, ContextCompressionService, ContextMaterializationLedger, CovenantDispatchGate, CovenantProviderCallFreezer, DailySpendAuthority, EmbeddingGeneratorFactory, EmbeddingsResetEndpoints, GitIgnoreRules, GrimoireTurnWriter, HumanPromptRegistry, InferenceContextBuilder, InferenceProviderFailureMessage, InferenceTokenizerResolver.

All 28 were read in full. Owning docs read: API.md (batches, audit, embeddings reset, error table, LongRest rows), DESIGN §10/§11.21/§22.2 passages for batches, Campaign continuity, budget gate, embeddings reset scopes, Compendium.README rows for budget/execution/features, Command Reference (batch, budget, session compact, doctor).

## Findings

### 1. Important — `cost.budget.enabled=true` with the default `dailyLimitUsd=0` silently enforces nothing, and `arcanum budget` / `GET /api/budget` then report $0 spent and $0 remaining
- `BudgetMonitor.cs:34`: `if (!budget.Enabled || budget.DailyLimitUsd <= 0) return Result.Success();` Same condition in `BudgetReservationService.cs:50,155,293`.
- `BudgetEndpoints.cs:41-48` only queries spend when `enabled && dailyLimit > 0`, so with a zero limit `todaySpendUsd` is 0 even when real money was spent, `remainingUsd` is `max(0, 0 - 0) = 0`. `BudgetCommands.cs:63-70` prints "Daily limit $0.00 / Spent today $0.00 (0%) / Remaining $0.00".
- No validator rule (`ConfigurationValidator.cs` has no `Cost.Budget` check), no doctor row, Compendium.README.md:272-273 never says 0 means unlimited ("Maximum UTC-day spend when enforcement is enabled"). `ConfigurationPresetPlanner.cs:507` shows the product knows "enabled with no positive limit" is not a policy, but only the preset UI says so.
- Failure scenario: operator flips `cost.budget.enabled` on in Compendium, forgets the limit (default 0, the Compendium descriptor allows 0), believes spend is capped; inference is unmetered, and the status they check shows "Remaining $0.00", which reads as exhausted, not as unenforced.
- Probe P9: `new BudgetPolicySettings { Enabled = true }` gives `DailyLimitUsd=0`.
- Fix: validator error/warning for `Enabled && DailyLimitUsd<=0` (name both keys), doctor/health row, `GET /api/budget` always reports real spend and an `enforced` flag, CLI prints "Not enforced: set Arcanum:Cost:Budget:DailyLimitUsd". Effort S.

### 2. Important — a batch whose input cannot be opened is left `in_progress`, and `/reset` calls the file "missing"
- `BatchProcessingService.cs:385` `OpenCompatibleReadAsync` runs outside the page/lookahead guard (`StopsAsUnexpectedFailure`, lines 405-435). A header-corrupt, truncated, or not-actually-encrypted input throws `InvalidDataException`/`CryptographicException` out of `ProcessBatchAttemptAsync` to `ProcessBatchWithCleanupAsync`, which logs "remains durable for startup reconciliation" and leaves the row `in_progress`; nothing re-picks an `in_progress` row until restart.
- `BatchRecoveryService.InputExistsAsync` (lines 294-309) maps those same exceptions to `false`, so `POST /v1/batches/{id}/reset` answers `InputMissing`, which `OpenAiV1BatchesEndpoints.cs:86-92` renders as 400 "input file is missing; the batch cannot be safely reset." with code `not_found`. The file is present, only unreadable.
- Doc mismatch: API.md:285 and Command Reference ("when reading the input itself failed, ... the note is on the line after the last finished page") promise that an input read failure ends the batch as `failed` with an error-file note. That holds for a tail/AEAD failure (probe P3[tail]: status `failed`, error file published, Error log with exception type) but not for a failure at open (P3[header], P3[garbage]: `status=in_progress`, no error file, `reset => InputMissing`).
- Fix: wrap the open (and the `UploadedFiles` lookup) in the same stop-as-failed bookkeeping (error-file note on line 1); make `InputExistsAsync` return a tri-state so reset says "input file is present but cannot be decrypted/read (key or file damaged)" and recommends re-upload. Effort S-M.

### 3. Important — a Campaign-rollup summary failure ends the user's own turn as HTTP 400 `Covenant.InvalidContent`, and the policy rejects ordinary `/route` tokens as "host paths"
- `CampaignRollupTurnPreparer.cs:625-631` and `:640-646` return `contributed.Error` / `folded.Error`; `CampaignRollupMaintenance.cs:469-471` returns provider errors and `:485-492` returns the second invalid-summary error. Probe P10a (real `WizardIntelligenceProvider` harness, fake maintenance returning that failure): turn `success=False`, `code=Covenant.InvalidContent`, `message='Campaign continuity must be one bounded summary without host paths or attachment bytes.'`, zero provider calls. `ArcanumErrorMapper.cs:160` maps `Covenant.InvalidContent` to 400.
- The message reads as a rejection of the client's request and names no remedy (disable `features.campaignRollups`, fix `FastModel`). Progress is not advanced, so the next turn runs the same fold and fails the same way. By contrast Long Rest's clean schedule is explicitly best-effort (DESIGN 5184), and nothing in DESIGN states that a rollup failure must end the turn (the turn could proceed with the previously frozen artifact).
- Trigger is plausible: `CampaignSummaryPolicy.cs:148` rejects any whitespace/punctuation-delimited token that starts with `/` and has more than one character. Probe P8: summaries mentioning `/api/health`, `POST /v1/batches` and `/clear` are rejected (`Covenant.InvalidContent`); `src/Foo.cs`, `~/.config/...` pass. A coding-assistant Campaign summary naturally contains routes and slash commands; two failed attempts end the turn.
- Fix: treat a maintenance failure at turn preparation as non-fatal (log with Campaign/Session ids, proceed with the existing artifact) or at minimum wrap it in a distinct error (`Campaign summary maintenance failed: ...; disable features.campaignRollups or fix FastModel`); narrow the path rule (require a second `/`-segment pattern that cannot be a URL route, or only absolute paths that exist under a registered root). Effort M.

### 4. Important — deferral of Campaign rollup maintenance is invisible: reason is dropped, and a Familiar/non-OpenAI-compatible maintenance model never produces a summary
- `CampaignRollupMaintenance.cs:282-285` returns `Success(null)` when `ResolveClientAsync` returns no lease (any provider that is not `OpenAICompatible`, `CampaignRollupProviderClientFactory.cs:80-83`), which becomes `Deferred(published, "This provider cannot prove one frozen physical maintenance attempt.")` (line 247). `CampaignRollupTurnPreparer.cs:625-646` checks only `IsFailure`; `Loremaster.cs:266-290` logs only at Debug "remains pending" without the reason. Probe P10b: a deferred maintenance result gives a successful turn with nothing surfaced.
- Compendium.README.md:181 says maintenance "uses the configured FastModel, otherwise DefaultModel" and never mentions the provider-kind restriction (Long Rest's row does name OpenAI-compatible). `ConfigurationValidator` has no `campaignRollups` rule.
- Failure scenario: an installation whose only provider is the Claude Code or Codex Familiar enables `features.campaignRollups`; no Campaign summary is ever written, no warning, no health/doctor row; only a Debug line without a reason.
- Fix: log `DeferredReason` (Warning once per Campaign per hour) with Campaign/Session ids; add a validator/doctor warning "campaignRollups needs an OpenAI-compatible FastModel/DefaultModel"; document the restriction. Effort S.

### 5. Minor — the turn error for deferred protected Long Rest maintenance drops the reason and operation id it holds
- `CampaignRollupTurnPreparer.cs:585-587`: `if (resumed.Value.Phase is not Completed || resumed.Value.DeferredReason is not null) return new Error(LongRest.Unavailable, "Long Rest protected maintenance remains deferred.")`. `DeferredReason` (an error code such as `LongRest.ProviderUnreachable`, `long_rest.authorized_request_required`) and `operation.Value.Id` are both in scope and discarded. API.md:919 says the Covenant 503s "name an operator action rather than a retry"; this one names nothing, and the operator cannot tell a provider outage (wait) from a parked sweep (`arcanum operation retry <id>`, API.md:1384). Fix: include the code and the operation id in the message. Effort S.

### 6. Minor — operator-cancelled batch lines say "The host stopped"
- `BatchProcessingService.cs:576-580` and `:628-633` seal in-flight lines with `CompleteDispatchedLinesAsync(..., detail: null)`, and `CompleteInterruptedLineAsync` defaults to "The host stopped after this request was durably marked for dispatch" (line 930). Probe P1 (copy of the existing cancel-during-provider-call test) prints exactly that message in the output file after `POST /cancel`. Command Reference/API.md say cancellation seals lines as `batch_interrupted_after_dispatch`; the code is right, the human text is wrong for this path. Fix: pass a cancel-specific detail ("The batch was cancelled after this request was dispatched; ..."). Effort S.

### 7. Minor — an input file that is missing when processing starts fails the batch with no note and no log
- `BatchProcessingService.cs:376-383` sets `failed` and returns; no error file, no `logger` call (probe P2: `status=failed`, output and error file ids null, zero log entries). API.md:285 states the batch object has no reason field and "the reason is that note, plus the operator log". A run of batches never logs a start or finish at Information either (only failures), so this one leaves no trace at all. Fix: write the line-1 note as the unexpected-failure path does and log Warning with batch and input file ids. Effort S.

### 8. Minor — unparseable `image_url` parts pass validation and are silently dropped, so the model answers without the image
- `ScryingValidator.cs:147-155` (Core, the `continue` for a non-matching data URI/URL) leaves a malformed data URI or non-absolute URL "to the provider/mapper to reject"; `InferenceContextBuilder.cs:536-551` and `:604-620` return `null` and the part is dropped. Probe P4: `cat.png`, `images/dog.jpg`, `data:image/png,rawbytes`, `data:image/png;charset=utf-8,abc` all give `validationOk=True` and `imagesReachingModel=0`; a message whose only content part was the image becomes an empty `TextContent`. Client gets 200. Fix: reject in `ValidateRequestImages` with `Scrying.InvalidImageData` naming `messages[i].content[j].image_url.url`. Effort S.

### 9. Minor — native `/api/intelligence/ping` client-tool errors name `tools[i]` (the field is `clientTools`), and `clientTools` without `forwardClientTools` is silently ignored
- `ClientToolCapabilityValidator.cs:131-161` (shared with `/v1`, where `tools` is right) builds `tools[{index}]...` messages; probe P5a: native ping returns `tools[0].function.name is required.` for `clientTools[0]`. `ValidateRequest` line 38 returns success when `ForwardClientTools` is false, so supplied `clientTools`/`clientToolChoice` are dropped (probe P5b), while `/v1` answers 400 `unsupported_parameter` for the same situation (`OpenAiChatCompletionRequestValidator.cs:155-160`). Fix: pass a field prefix into `ValidateToolDefinitions`; reject `clientTools` when the flag is false. Effort S.

### 10. Minor — `POST /api/embeddings/reset?scope=` (empty value) resets everything
- `EmbeddingsResetEndpoints.cs:107-111`: `string.IsNullOrEmpty(value)` returns `All`. Probe P6: `ParseScope("")` gives `All`. Omitting the parameter is documented as `all`; an unset shell variable in `?scope=$S&confirm=true` should be a 400, not a destroy-all. Fix: treat only `null` as default; empty/whitespace is `Validation.InvalidBody`. Effort S.

### 11. Minor — the provider-failure text sends the operator to the API key for rate limits and overload
- `InferenceProviderFailureMessage.cs:15-20`: every HTTP status gets "Check the model, API key, and request". Probe P7: 429, 500 and 503 print that sentence. `ProviderFailureClassifier` deliberately does not fall back on these statuses, so this is the only text the client sees. Fix: 429 -> "rate limited; retry later", 5xx -> "provider error; retry", 401/403 -> key, 404 -> model. Effort S.

### 12. Minor — compaction no-ops are indistinguishable from an empty session
- `ContextCompressionService.cs:195-208`: compression disabled or fewer than `CompressionPreflightMinMessages` (default 6) returns `CompactResult(0,0,0)`; `SessionCommands.cs:558` prints "Compacted session X: 0 -> 0 tokens; 0 entries removed." (probe P11a: 3-entry session gives `tokensBefore=0`). When every entry is pinned and the session is over the limit (`ordered.Count == 0`, line 232), the result is N -> N with no stop code and no log, because the over-limit warning needs `removed > 0` (line 344); probe P11b: 36,846 -> 36,846, `stoppedBy=null`. Fix: add a reason field (`NotNeeded`, `Disabled`, `TooFewMessages`, `OnlyPinnedEntriesRemain`) to `CompactResult` and print it. Effort S.

### 13. Minor — Grimoire turn-writer and Covenant gate warnings omit the Session/turn id and the error code
- `GrimoireTurnWriter.cs:918` and `:943` log `beginFailureLogMessage` with only the model name; the failure's code (deleted Campaign, missing Session, binding mismatch — the case the type's own remarks say used to be swallowed) is returned to the client but never logged. `:1335` ("could not finalize assistant entry for model X"), `:467`/`:450` (tool interaction), `:1175` (claim settle) likewise carry no Session id. `CovenantDispatchGate.cs:344` ("turn acquisition failed with {ErrorCode}; the turn is refused") and `:682` have no logical turn id or Session id, although the Debug record at `:378` carries both. Fix: add `{SessionId}`/`{LogicalTurnId}`/`{ErrorCode}`. Effort S.

### 14. Minor — `Budget.Exceeded` messages do not say the day is UTC, when it resets, or which key to raise
- `BudgetMonitor.cs:73` (same text in `BudgetReservationService.cs:144,282,393`): "Daily budget limit of $X USD has been reached (current spend: $Y USD)." API.md:918 documents the UTC day and `Arcanum:Cost:Budget:DailyLimitUsd`; the refusal an operator actually sees (HTTP 429, no `Retry-After`) does not. For an operator west of UTC the "day" ends mid-afternoon or evening. Fix: append "The budget day is UTC and resets at 00:00 UTC; raise Arcanum:Cost:Budget:DailyLimitUsd to continue." Effort S.

## Checked and sound
- `BatchJsonlRecordReader`: per-record limit/spill/BOM/whitespace behaviour and physical-line numbering traced; per-line error texts name the physical line, measured bytes, the limit and the action (split the request); consistent with DESIGN "Page and publication frontiers".
- Batch unexpected-failure bookkeeping (probe P3[tail]): batch ends `failed`, error file published, Error log names batch id and exception type; matches API.md:285 for failures after open.
- `ValidateEnvelope` texts (method/url/custom_id) are field-specific and match API.md:283.
- `AuditEndpoints`: parameters, `from > to` 400, `X-Arcanum-Next-Cursor`, disabled => empty page all match API.md:252/949; `AuditEndpointTests` cover them. Silent clamping of `limit` to 1..1000 matches "default 100, maximum 1,000".
- `EmbeddingsResetEndpoints`: `confirm=true` gate, allowed-scope list in the 400 text, snake-case aliases and `Validation.InvalidBody` for unknown scope match API.md:80 and DESIGN 5193.
- `ClientToolCapabilityValidator` choice parsing, `ModelUnsupported` and `ToolChoiceUnavailable` messages are actionable.
- `ArcanumInvocationContexts.ResolveToolPolicy` fails closed on undefined values; the `recallTurn` pattern precedence (`is null or ...`) is correct.
- `DailySpendAuthority` is the single resolver for the gate and the report (confirmed in `BudgetEndpoints.cs:36-44`), so figures cannot drift when a limit is set.
- Execution clamps in Compendium.README.md:265-266 equal `ArcanumSettingClamps` (1-20, 1-10).
- `EmbeddingGeneratorFactory` messages name the exact config keys; `InferenceTokenizerResolver` falls back once with a Warning naming the encoding.
- `GitIgnoreRules` parse/match logic (negation, anchoring, `**`, classes, escapes, trailing spaces) traced; caps are documented in the type remarks.
- `ContextMaterializationLedger`, `CovenantProviderCallFreezer`, `CampaignMaintenance*Freezer/Fingerprint`, `AttachmentSourceSanctum`, `HumanPromptRegistry`, `ChronicleFrameWire`: no operator-visible usability defect found (internal contracts; error texts are fixed and content-free by design).

## Probes run
| id | what | result |
|---|---|---|
| P1 | cancel during provider call, read output file | line text begins "The host stopped after this request" |
| P2 | input file deleted before processing | `failed`, no output/error file, 0 log entries |
| P3 | input corrupted (header / non-envelope / tail) | header and non-envelope: `in_progress`, reset gives `InputMissing`; tail: `failed` + error file + Error log |
| P4 | 4 unparseable image URLs through `ScryingValidator` then `BuildInitialMeAiChatMessages` | validation OK, 0 images reach the model |
| P5 | native ping client-tool validation | `tools[0]...` text; `clientTools` without flag => success |
| P6 | `ParseScope("")` | `All` |
| P7 | `InferenceProviderFailureMessage.Build` for 400/401/429/500/503 | same "Check the model, API key..." sentence |
| P8 | `CampaignSummaryPolicy.ParseStructuredResponse` on summaries with routes | `/api/health`, `/v1/batches`, `/clear` rejected |
| P9 | `BudgetPolicySettings{Enabled=true}` | `DailyLimitUsd=0` |
| P10 | Wizard harness with fake maintenance returning failure / deferral | failure ends turn with `Covenant.InvalidContent`; deferral invisible |
| P11 | `CompressSessionAsync` on 3 entries and on 8 pinned over-limit entries | `0->0` and `36846->36846`, no reason |

Code-trace only (execution not feasible without a live host): #3 end-to-end with the real `CampaignRollupMaintenance` and a model that returns two non-compliant summaries (the policy rejection P8 and the turn propagation P10a were each executed); #4 real `Loremaster` Debug line; #5; #14.
