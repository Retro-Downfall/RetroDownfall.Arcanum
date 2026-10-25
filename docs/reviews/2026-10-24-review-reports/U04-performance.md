# U04 (Arcanum.Api, Intelligence 4) - PERFORMANCE review

Tree: `.claude/worktrees/codebase-review` @ ac0d2d8e (read-only). Lens: where the code spends time, memory or I/O it does not need to, and what grows without bound.

## Scope note (read first)

`units.json` did not exist at `.../scratchpad/review/units.json` (the scratchpad was empty when I started, and it never appeared). I inferred the unit from the brief ("Intelligence (4)", about 8,904 lines): `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs` is exactly 8,904 lines (`wc -l`) and is the only Intelligence file of that size, so U04 = that file. The other partial, `WizardIntelligenceProvider.ContextPreview.cs` (1,009 lines), is not in the 8,904 and was only grepped.

Measurement caveat: the machine was running 100-157 load average (many reviewers building at once) for the whole session. Wall times below are therefore upper bounds; where I could, I also give process CPU time. Counts (LLM calls, embeds, EstimateContext invocations, tokens) are unaffected. The published Native AOT binary (`.../review/publish/RetroDownfall.Arcanum.Cli`) did not exist, so no AOT startup numbers; the tokenizer cold-start figure is from the JIT'd Debug test host.

## Findings (ranked)

| # | Severity | Title | Anchor |
|---|---|---|---|
| 1 | Important | Covenant admission `Measure()` charges the whole tool-schema set into every fragment, so a Confirmed Covenant is withheld on mid-size windows (and each call is a full estimate) | WizardIntelligenceProvider.cs:7866 |
| 2 | Important | Lexicon fallback extractor makes an extra LLM call on every user turn, even when the Lexicon is empty | WizardIntelligenceProvider.cs:5577 |
| 3 | Important | Every turn after a 5 s idle re-walks the whole working-directory tree looking for SPELL.md | SpellScanner.cs:761 (caller WizardIntelligenceProvider.cs:5376) |
| 4 | Minor | Three identical full-transcript estimates (plus four payload hashes) per provider round | WizardIntelligenceProvider.cs:7943-7964 |
| 5 | Minor | Same prompt embedded twice per turn when SemanticSpellRouting and any retrieval feature are both on | WizardIntelligenceProvider.cs:5405 / 5757 |
| 6 | Minor | `SpellWeaveCache` keeps one snapshot, so alternating workspaces re-embed the whole spell catalog every turn | SpellWeaveCache.cs:74-82 |
| 7 | Minor | Tokenizer vocabulary is built lazily inside the first turn (no warm-up) | InferenceTokenizerResolver.cs:88 |
| 8 | Minor | Dispatch-time token breakdown never attributes Covenant tokens (inspect does) | WizardIntelligenceProvider.cs:3271 |

### 1. Covenant `Measure()` double-charges the tool schemas (Important)

Code (WizardIntelligenceProvider.cs, `ResolveCovenantAdmission`):

```
7854  int baseline = ModelTokenEstimator.EstimateContext(new ModelTokenizationRequest(provider, model, messages, chatOptions, ...)).TotalTokens;   // tools counted here
7864  ulong headroom = Math.Max(0, contextWindowLimit - baseline);
7866  ulong Measure(string text) => text.Length == 0 ? 0UL
          : (ulong)ModelTokenEstimator.EstimateContext(new ModelTokenizationRequest(provider, model,
                [new MeAiChatMessage(ChatRole.System, text)], chatOptions, 0, 0)).TotalTokens;                                                  // tools counted AGAIN
7883  MeasureSections(c) = Measure(GlobalConfirmed) + Measure(CampaignConfirmed) + Measure(CampaignProposed) + allowances   // up to 3x
7891  CovenantDispatchGate.PlanDispatch(scope, headroom, MeasureSections, Measure)
```

`chatOptions` carries every advertised tool, and `ModelTokenEstimator.EstimateContext` charges `Options.Tools` (ModelTokenEstimator.cs:209 `EstimateTools`) on every call. So the cost the planner believes a fragment has is `fragment + all tool schemas`, while the headroom it is compared with already had the tool schemas subtracted. `CovenantAdmissionPlanner.Plan` (Core/Covenant/CovenantAdmissionPlanner.cs:102) refuses Confirmed outright when `measureSections(confirmedOnly) > availableTokenBudget`.

Probe J (microbench of exactly that expression, 32,768-token window, unknown-model profile): `Measure(53-char fragment)` = 72 tokens with no tools, 29,822 with 10 tools, 119,143 with 40 tools, 357,377 with 120 tools (fragment text alone = 63).

Probe K (full provider turn, real `CovenantDispatchGate`, 4 Global-Confirmed entries, ~560-byte MCP tool schemas, stateless single message). Existing Covenant tests run with `DisableMcpTools: true`, so they never see this.

| window | MCP tools | toolsInRequest | Covenant reached model | CovenantConfirmedNoFit | est. input tokens |
|---|---|---|---|---|---|
| 32,768 | 0 | 2 | yes | no | 1,326 |
| 32,768 | 10 | 12 | yes | no | 8,180 |
| 32,768 | 25 | 27 | **no** | **yes** | 18,295 (real headroom about 13k) |
| 32,768 | 40 | 42 | **no** | **yes** | 28,576 (real headroom about 3.1k, section needs about 0.5k) |
| 32,768 | 60 | 62 | turn refused: ContextBudgetExceeded (genuine) | | 43,308 |
| 131,072 | 25 / 40 / 60 | | yes | no | |

Deliberate break (patch saved: `U04-performance-probes/Measure-deliberate-break.patch`): changing only line 7874 to pass `new ChatOptions()` to `Measure` makes the 25- and 40-tool 32k cases admit the Covenant (est. input 18,461 and 28,742, both inside the window). The 18 existing Covenant tests (`WizardIntelligenceProviderCovenantTests`, `CovenantContextPressureTests`, `CovenantAdmissionPlannerTests`) still pass with the break in place, so they neither detect the bug nor pin the intended behaviour.

Performance side: the planner calls `Measure` up to 2 + 32 (`MaxProposedRemovals`) times for sections plus once per eligible candidate (up to 64+64+32 = 160, `CovenantLimits`), about 194 calls per provider attempt, each a full `EstimateContext` that re-hashes and re-counts every tool schema. Probe F (text already cached, so only the fixed per-call cost remains): 0.011 ms with 0 tools, 0.087 ms with 10, 0.31 ms with 40, 2.35 ms with 120. That is roughly 60 ms (40 tools) to 450 ms (120 tools) of CPU on the time-to-first-token path of every Covenant turn.

Why it matters: with the unknown-model fallback profile (every local Ollama model) token cost is bytes-based, so 25 modest tools already cost about 15k tokens. The operator's Confirmed standing agreement silently disappears from 32k-window models while about 13k tokens of headroom sit unused. It is logged only as `CovenantConfirmedNoFit`.

Fix: measure fragments without tools (`new ChatOptions()`, or `EstimateText` plus the existing framing allowance); this removes both the over-charge and the per-call tool cost. Add a Covenant test with 25+ tools on a 32k window. Effort S.

### 2. Lexicon fallback extractor runs on every user turn (Important)

`RetrieveLexiconEntriesAsync` (5556-5657): when the SemanticRouter supplied no entities (`entities.Count == 0`, line 5577) it calls `LexiconEntityExtractor.ExtractAsync` (line 5606), a buffered LLM call (128 max output tokens, 15 s preflight timeout), before the main call. Gates: `EnableLexiconSystem` (= `Features.Lexicon`, **default true**, PublicConfigurationSettings.cs:27), `ShouldUseLexiconForTurn`, and the unattended+override skip. There is no check that the Lexicon holds anything. `ResolveIntelligence` forces `UseFastModelForSpellRouting = true`, but `FastModel` is empty by default, so the call goes to the main model client (`extractorClient = chatClient`). `ILexiconService` has no emptiness/count method.

Probe A (default features except Lexicon toggled, empty `FakeLexiconService`, no spells, `DisableMcpTools`): buffered turn `Lexicon=true` -> 2 provider calls (first prompt starts "Extract the named entities..."); `Lexicon=false` -> 1 call. Streaming turn `Lexicon=true` -> 1 buffered pre-flight + 1 streaming call.

DESIGN (Lexicon "Read path", ~line 1514) documents the fallback extractor as deliberate ("so memory retrieval stays available"); it does not say it must run when there is nothing to retrieve. Every turn of a fresh installation therefore pays one extra billed LLM round trip, serialised before the first token, on the main model (a local 27B model, or a model swap in Ollama if `FastModel` differs), bounded only by the 15 s timeout.

Fix: add a cheap `HasEligibleEntriesAsync(LexiconScope)` (`SELECT 1 ... LIMIT 1`) and skip the extractor when the scope (Campaign then Global) is empty; optionally start the extractor concurrently with the query embedding and retrievals instead of before them. Effort M (interface + service + fake + tests + DESIGN line).

### 3. Per-turn whole-tree SPELL.md walk (Important)

`ResolveRoutedSpellAsync` (5376) calls `SpellScanner.ScanMetadataAsync(workspaceRoot, ..., ttl)`. The TTL default is 5 s (SpellSettings.cs:30), so any human-paced next turn is a cache miss. The miss path (`EnumerateSpellFiles`, SpellScanner.cs:761-843) walks the entire working directory breadth-first: two `readdir` passes per directory (`EnumerateFiles` just to look for one name, then `EnumerateDirectories`), `Path.GetFullPath`, two `IsPathUnderWorkspace` and one `Directory.ResolveLinkTarget` per subdirectory. Skipped: dot-directories and `node_modules|bin|obj|out|dist` only (not `target`, `build`, `vendor`, `venv`, `Pods`, ...). It runs on a thread-pool thread via `Task.Run` with synchronous I/O.

Probe H (`SpellScanner.ScanMetadataAsync`, ttl 0 and ttl 5, isolated `ARCANUM_TEST_HOME`):

| tree | dirs / files | process CPU per scan | wall |
|---|---|---|---|
| this repository | 389 / 5,896 | 23 ms (warm) | 23-48 ms (295 ms first) |
| synthetic | 2,000 / 19,990 | 91-97 ms | 261-654 ms |
| synthetic | 10,000 / 99,990 | 523-600 ms | 844-1,559 ms |

TTL behaviour: second call inside 5 s = 0.02-0.08 ms; the call after 5.2 s idle re-scans in full (85 ms CPU at 2k dirs, 538 ms CPU at 10k dirs). Cost is linear in workspace size and paid on every turn, before any inference; cold filesystem cache is worse.

Fix: cache by directory change (mtime stamps or the workspace watcher the indexing service already owns) rather than a wall-clock TTL, and/or restrict discovery to the documented spell roots (`spells/`, `.arcanum/spells`), and/or add the common build-output names to `HeavyDirectoryNames`. Effort M.

### 4. Triple estimate per provider round (Minor)

Each tool round runs `EnsureContextBudgetWithMaterializations` (7918): `CascadeSemanticDrops` -> `Count` (7945-7948), `TryTrimOldestToolExchanges` -> `countTokens(messages)` (TurnContextGuards.cs:126), then a third `EstimateContext` for the breakdown (7956). Nothing changes between the three when under budget. `Count` reads only `TotalTokens`, but `EstimateContext` also hashes the whole payload for `PayloadFingerprint` (ModelTokenEstimator.cs:324), and `ModelCallExecutor.ValidatePrecomputedBreakdown` hashes it a fourth time (ModelCallExecutor.cs:772). The deferred-attachment loop (2842-3050) repeats the pattern four passes per referenced attachment plus a deep copy of the message list; on the first round of a Session turn `TryApplyContextCompressionIfNeeded` adds one more (static trace, not run).

Probe B: `EstimateContext` calls per extra tool round = **3.00** (buffered and streaming; message count grows by 2 per round). Cost per round with 52 messages and 40 tools of 2.5 KB: 2.6 ms buffered, 5.2 ms streaming. At 99 messages x 1.5 KB: 18.8 ms/round; 99 x 6 KB: 7.7 ms/round (noisy, loaded host). Probe B2 warm single call: 0.85 ms (340 KB transcript, 40 tools, 519 KB allocated), 6.8 ms (1.2 MB, 4.9 ms of it the fingerprint), 3.5 ms with 120 tools (1.4 MB allocated), 2.2 ms with no tools. Cold tokenization of new text (tool results, attachments) measured 17-19 MB/s up to 100 KB and 3.8-7.9 MB/s at 1-4 MB, then cached by SHA-256 of the text (bounded 4,096 entries).

Small next to a model round trip, so Minor; but it is pure repetition: compute the breakdown once, derive the over-budget test from it, recompute only after a mutation, and skip the fingerprint for probe-only calls. Effort S.

### 5. Duplicate prompt embedding (Minor)

With `Features.Embeddings + SemanticSpellRouting` and any of Saga/CodebaseRetrieval/AttachmentRetrieval/Tapestry, `SemanticSpellRouter.ResolveAsync` embeds `GetSemanticRouterUserProbe(request)` (SemanticSpellRouter.cs:92, reached from 5405) and `ResolveRagQueryEmbeddingAsync` embeds the identical string again (2400 -> 5757). The comment at 5719 says a turn "never pays for the same embedding twice", true only for Phase 3-4. Probe C: one turn -> `EmbedAsync` called **2** times (+1 `EmbedBatch` for the catalog). Each is a provider call and a ledger row. Fix: have `ResolveAsync` return the prompt embedding (or seed `TurnContextSeed.QueryEmbedding`) so retrieval reuses it. Effort S.

### 6. `SpellWeaveCache` single snapshot (Minor)

`SpellWeaveCache` (singleton) holds one `CacheSnapshot` keyed by the whole catalog `(Name, Description)[]` plus provider/model/dimensions. The catalog is global + workspace spells, so two working directories with different local spells (CLI in one repo, Forge or a daemon in another) evict each other. Probe I (50 global + 1 local spell, 6 alternating turns): 6 `EmbedBatch` calls, 306 descriptions embedded; same workspace every turn: 1 call. Fix: key the snapshot per spell (name+description hash -> vector) or per workspace root. Effort S.

### 7. Tokenizer cold start on the hot path (Minor)

`InferenceTokenizerResolver` builds `TiktokenTokenizer.CreateForEncoding("o200k_base")` on the first estimate of the process, i.e. inside the first turn's `EnsureContextBudget`; nothing warms it at startup. Probe D (fresh process, three runs, JIT'd Debug host): 87-433 ms wall (87-94 ms CPU), first estimate 7-34 ms, retained heap +54-62 MB. Under Native AOT the JIT share disappears but the vocabulary build and the resident size remain; not measured (no AOT binary). Fix: touch the resolver from a background startup task. Effort S.

### 8. Dispatch breakdown lacks Covenant attribution (Minor, accounting)

The `systemPromptAttribution` lambda passed to every context gate (3271-3272, 4400-4401) returns the cache-prepared or memory-delegation attribution only; otherwise null. `EstimateSystemMessage` then charges Covenant text to `SystemCodexSpell`. The preview path does attribute (ContextPreview.cs:638). Probe K, admitted case, components: `SystemCodexSpell=793, Tools=343, CurrentPrompt=5, ProviderFraming=12, SafetyMargin=173, ReservedAnswer=1024` with the Covenant heading present in the dispatched prompt and no `CovenantConfirmed` row. Streamed Context frames and audit-log breakdowns therefore disagree with `context inspect`/`mana` (DESIGN ~2009). Effort S: pass `streamSystemPromptDocument.BuildResult().Attribution` when `streamCovenantContent` is non-null.

## Probes run (sources in `U04-performance-probes/`)

Scratch worktree `.../review/wt/U04-perf` @ ac0d2d8e (removed afterwards). Tests added as partial classes / new files in `tests/RetroDownfall.Arcanum.Tests` (probe-only; `WizardIntelligenceProviderFallbackTests.cs` got one internal helper, see `FallbackTests-helper.patch`).

| probe | what | result |
|---|---|---|
| A | extractor LLM call, empty Lexicon, buffered + streaming | 2 calls vs 1 (buffered); 1 pre-flight + 1 stream |
| B | EstimateContext invocations and time per tool round, stateless 52-99 msgs, 40 x 2.5 KB tools, buffered + streaming | 3.00 calls/round; 2.6-18.8 ms/round |
| B2 | warm EstimateContext microbench (4 shapes) | 0.85-6.8 ms, 73 KB-1.4 MB alloc |
| B3 | tokenizer throughput natural text | 17-19 MB/s <=100 KB; 3.8-7.9 MB/s at 1-4 MB; cached 0.01-2.2 ms |
| C | double embedding | EmbedAsync = 2 |
| D | tokenizer first use, fresh process x3 | 87-433 ms, +54-62 MB |
| E | streaming per-token overhead, 2k/20k/100k tokens | 5-11 us/token, about 1.7 KB/token, linear |
| F | Measure cost vs tool count | 0.011 / 0.087 / 0.31 / 2.35 ms |
| H | SPELL.md tree walk, ttl 0/5 | see finding 3 |
| I | SpellWeaveCache alternating workspaces | 6 batch calls vs 1 |
| J | Measure() token value vs tool count | 72 -> 29,822 -> 119,143 -> 357,377 |
| K | provider turn with Covenant + tools, 32k and 128k | see finding 1 |
| K + break | `Measure` with `new ChatOptions()` | Covenant admitted at 25 and 40 tools; 18 existing Covenant tests still green |

Other checks: `EXPLAIN QUERY PLAN` (sqlite3 CLI, schema from `Infrastructure/Data/Schema/Tables/workspace_file_*.sql`) for the three retrieval queries: scoped scan = `SEARCH s USING INDEX idx_workspace_file_chunks_path (WorkspacePath=?)` + PK lookup on embeddings; chunk join = PK `ChunkId=?`; per-file count = covering index `(WorkspacePath, RelativePath)`.

## Checked and found sound

- Per-token streaming path (RunInferenceAttemptAsync -> StreamPromptCoreAsync -> mapper -> TurnEventEmitter -> bounded channel 256): linear 5-11 us/token, no per-token work that grows with the accumulated answer (probe E, 2k -> 100k tokens). About 1.7 KB allocated per token is garbage but irrelevant at LLM rates.
- Retrieval SQL: parameterized, `IN` lists bounded by `maxChunks` (<=50), index-driven (plan above). Managed brute-force cosine scan is permanent and documented (DESIGN section 21.4/21.5; the "when sqlite-vec is absent" wording is stale because `SQLITE_OMIT_LOAD_EXTENSION` makes it always absent).
- `GrimoireRepository.GetSessionAsync` is a bounded window (`MaxMessagesPerConversationLoad`, watermark aware), so history load does not grow with session length.
- Ambient state (`SessionAttachmentTurnBudget`, `TurnAmbientSet`, ledger ambient) is `AsyncLocal`-based; no static registry keyed by session, so no leak on abandoned turns. `TextTokenCache` (4,096), `FullSpellCache` (256), `MetadataScanCache` (32), `CodexReader` cache (64) are all bounded LRUs; Codex reads are stat-stamped.
- `PumpWardAndHumanEventsAsync`: human wait is hoisted and re-armed only on completion; no busy-spin once cancelled (traced). Per-tool-call abandonment grace uses a cancelled `Task.Delay`.
- MCP tool surface is cached per workspace in `McpConnectionManager.GetAvailableToolsAsync` (generation + trusted-digest check). Static tool schemas (`JsonDocument`) are parsed once.
- `ArcanumRuntimeDefaults.ResolveEmbeddings/ResolveIntelligence/...` allocate a few small records per call (about 50 calls per turn); negligible.
- Audit logger is a complete no-op when disabled (default); `contextBreakdownsByCall` is bounded by tool rounds.
- Tapestry retrieval defaults to `CollapsedTree` (one scoped scan per scope); `TreeTraversal` is opt-in and serial.
- Tokenizer data package is 2.4 MB (`Microsoft.ML.Tokenizers.Data.O200kBase`), only `o200k_base` is used; no binary-size concern.

## Coverage

Read in full: `Intelligence/WizardIntelligenceProvider.cs` lines 1-8904 (all of it, in 650-1000-line chunks). Read in full to judge call costs: `ModelTokenEstimator.cs` 1-1040, `ModelCallPayloadFingerprint.cs`, `TurnContextGuards.cs`, `InferenceContextBuilder.cs`, `InferenceTokenizerResolver.cs`, `LexiconEntityExtractor.cs`, `SemanticRouter.cs`, `SemanticSpellRouter.cs`, `WeaveService.cs`, `TurnEventEmitter.cs`, `TurnEngine.cs`, `PingRequestPreflightValidator.cs`, `SessionAttachmentRequestValidator.cs`, `SystemPromptDocument.cs` 130-276, `DivinationService.cs`, `SpellWeaveCache.cs`, `CovenantAdmissionPlanner.cs` 60-198, `CodexReader.cs`, `SessionAttachmentTurnBudget.cs`. Partly read: `ModelCallExecutor.cs` (60-216, 217-620, 640-810), `ContextMaterializationLedger.cs` 1-510, `SessionAttachmentTurnService.cs` 60-380, `SpellScanner.cs` 40-1100, `McpConnectionManager.cs` 895-1100, `GrimoireTurnWriter.cs` 380-480 and 1380-1485, `GrimoireRepository.cs` 540-740, `TurnAccountingHandle.cs` 1-231, `ArcanumRuntimeDefaults.cs` 1-500, `WorkspaceIndexingService.*` register/resolve paths, `SessionContextPinMaterializer.cs` 89-170, `ContextTokenBreakdown` contract. Not read: `WizardIntelligenceProvider.ContextPreview.cs` (grep only), `ToolExecutionPipeline.cs`, `CovenantDispatchGate.cs` beyond the planner call, `BatchProcessingService.cs`.
