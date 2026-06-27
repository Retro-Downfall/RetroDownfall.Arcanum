# 02 — Api (`RetroDownfall.Arcanum.Api`)

**Scope:** the HTTP surface composition — the inference pipeline (`WizardIntelligenceProvider`, `ChatClientFactory`, `SemanticRouter`), inference support (`ManaPreflight`, `SystemPromptBuilder`, tokenizer, `HumanPromptRegistry`, built-in tools), the ~122 endpoints, the security filter + config redaction, streaming writers, and the OpenAI `/v1` parity surface. 82 files, ~16.5k lines.

**Method:** four parallel read-only deep-read passes ([inference pipeline](884bfc62-34d9-4570-8b04-7bebab82397d), [inference support + tools](4034dca9-8026-4c20-b583-845965566407), [endpoints](1ad2a934-e386-4912-b424-3d4015911803), [streaming + /v1](16fd04ea-d3ea-47ac-8e8e-5fa57ace3d73)); headline findings re-verified against source. Severities calibrated to the single-user, loopback-by-default posture.

Severity counts: **P1 ×1 · P2 ×16 · P3 ×10.**

---

## P1 findings

### [P1][correctness] Read-time context compression can silently drop un-summarized middle messages
- **Location:** `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:1517-1526` + `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs:398-404,949-956`
- **Observation:** `GetSessionAsync` loads the **globally most-recent N** entries — `SelectRecentEntries` is `OrderByDescending(CreatedAt).Take(maxMessages).Reverse()` (`:949-956`) where `maxMessages = MaxMessagesPerConversationLoad`. Compression then keeps only entries after the summary watermark: `session.Entries.Where(m => m.CreatedAt.UtcDateTime > watermarkExclusive)` (`WizardIntelligenceProvider.cs:1523-1524`). The summary covers everything **up to** the watermark.
- **Impact:** When more than `MaxMessagesPerConversationLoad` entries exist **after** `LastSummarizedMessageAt` (a long, actively-growing thread that outpaces summarization), the oldest post-watermark entries are never loaded and never reach the model — yet they are also not represented in `Summary`. The result is a silent hole in conversation context (no DB rows are deleted — this is a load-window defect, confirmed independently by two passes). 
- **Recommendation:** Anchor the load window at the watermark in SQL: `WHERE CreatedAt > watermark ORDER BY CreatedAt DESC TAKE max(N, unsummarizedCount)` (then re-order ascending). This also fixes the hot-path full-load perf issue tracked in 01-infrastructure.md.

---

## P2 findings

### Inference pipeline

### [P2][reliability] Streaming timeout/cancel cleanup passes an already-cancelled token (diverges from sync)
- **Location:** `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:790-797,999-1002` vs the sync path at `:400-401,410-417`
- **Observation:** On inference timeout/cancel, the streaming path calls `ResolveInterruptedAssistantEntryAsync(..., inferenceToken)` where `inferenceToken` is already cancelled by the timeout source; that method rethrows `OperationCanceledException` (`:2954-2956`). The sync path uses `CancellationToken.None` (or `callerToken`) for the equivalent cleanup. The outer `finally` (`:1052-1055`) does re-clean with `CancellationToken.None`, mitigating orphan rows.
- **Impact:** Inconsistent client-facing semantics — the stream may surface a raw OCE instead of the intended terminal `IntelligenceEventType.Error` event; cleanup may throw before completing.
- **Recommendation:** Use `CancellationToken.None` (or a dedicated non-cancellable cleanup token) for interrupt cleanup on all paths.

### [P2][concurrency] Endpoint `HttpClient` cache can grow past `MaxCachedEndpointClients`
- **Location:** `src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:207-219,252-277`
- **Observation:** `EvictExcessEndpointClients` only removes entries with `RefCount == 0`; if every cached endpoint is actively leased, eviction breaks while `GetOrAdd` keeps adding new keys.
- **Impact:** Under concurrent inference across many distinct endpoints, `_endpointHttpClients` and their `SocketsHttpHandler`s can exceed the intended cap (32) — unbounded handler/socket growth.
- **Recommendation:** Wait/LRU-evict-with-drain, or refuse new endpoint keys until capacity frees.

### [P2][correctness] `ManaPreflight` token accumulation can overflow / mis-counts non-text content
- **Location:** `src/RetroDownfall.Arcanum.Api/Intelligence/ManaPreflight.cs:41-76,145-156`
- **Observation:** `total += …` uses unchecked `int` with no cap; non-`TextContent` items are counted via `item.ToString()` rather than provider serialization.
- **Impact:** Very large histories could wrap negative and make `totalTokens <= effectiveLimit` true, **skipping** compression exactly when it's needed; non-text parts (tool calls, images) are mis-estimated, making compression fire early or late.
- **Recommendation:** Accumulate/compare in `long`; serialize non-text content the way the outbound mapper does (or document estimates as conservative-only).

### [P2][reliability] `HumanPromptRegistry` waiters are unbounded and only bounded by the inference timeout
- **Location:** `src/RetroDownfall.Arcanum.Api/Intelligence/HumanPromptRegistry.cs:11-39,57-59` + `McpBridgeTool.cs:87-92`
- **Observation:** `_waiters` has no capacity cap; each `ask_human` adds a `TaskCompletionSource`. The normal path is bounded by `inferenceToken` (≤ `InferenceTimeoutSeconds`, up to 3600s), but bridged MCP `ask_human` passes `Timeout.InfiniteTimeSpan`. Cleanup `catch (Exception) when (!ct.IsCancellationRequested) { }` swallows silently.
- **Impact:** Many concurrent unanswered `ask_human` calls hold registry slots (up to an hour each); bridged calls can wait unbounded; cleanup faults are invisible.
- **Recommendation:** Add an `ask_human`-specific timeout and a waiter cap with explicit error responses; log before swallowing cleanup exceptions.

### Endpoints / config

### [P2][correctness] Codex write uses the raw codex cap instead of the effective cap
- **Location:** `src/RetroDownfall.Arcanum.Api/TheForge/CodexEndpoints.cs:217` (write) vs `:201` (read, which uses `EffectiveCodexMaxSizeBytes`)
- **Observation:** Write: `ArcanumSettingClamps.CodexMaxSizeBytes(settings.Value.Codex.MaxSizeBytes)`; read: `ArcanumSettingClamps.EffectiveCodexMaxSizeBytes(settings.Value)` (which `Min`s with `Workspaces:MaxFileReadSizeBytes`).
- **Impact:** When the workspace read cap is lower than the codex cap, PUT accepts content that the read path (and `WizardIntelligenceProvider`) will then refuse — a write/read bound mismatch and DESIGN §3.4 violation.
- **Recommendation:** Use `EffectiveCodexMaxSizeBytes` on the write path.

### [P2][correctness] Prompt-test codex read uses the workspace cap, not the effective codex cap
- **Location:** `src/RetroDownfall.Arcanum.Api/TheForge/PromptEndpoints.cs:426-432`
- **Observation:** Passes `MaxFileReadSizeBytes(Workspaces.MaxFileReadSizeBytes)` (clamp max 10 MiB) to `CodexPathPolicy.ValidateContainedFile`, while codex endpoints use `EffectiveCodexMaxSizeBytes` (default 256 KiB).
- **Impact:** `/prompts/{id}/test` can pull a much larger codex file into prompt assembly than codex GET/PUT allow — extra memory/token exposure.
- **Recommendation:** Pass `EffectiveCodexMaxSizeBytes(settings.Value)`.

### [P2][correctness] Config PUT can persist a literal `"***"` as a new provider's API key
- **Location:** `src/RetroDownfall.Arcanum.Api/Configuration/ConfigurationRedactor.cs:39-48`
- **Observation:** `MergeApiKeys` restores masked values only when the provider name matches an existing one (`currentByName.TryGetValue`); otherwise the request provider passes through unchanged (`: p`), including `ApiKey = "***"`.
- **Impact:** Round-tripping a redacted GET that **adds a new provider** writes `"***"` as the real key (that provider then silently fails to authenticate). Not a secret leak, but a configuration footgun.
- **Recommendation:** For providers absent from `current`, reject `"***"` on `ApiKey`/`Endpoint`/model-map values (require explicit values).

### [P2][security] `run_spell_script` executes arbitrary file types via the default branch
- **Location:** `src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:474-479`
- **Observation:** Non-`.py`/`.js`/`.sh`/`.ps1` extensions fall through to `psi.FileName = scriptFullPath` (run the file directly), rather than an interpreter wrapper.
- **Impact:** Any regular file in a spell's `scripts/` directory — including an `.exe` or extensionless binary shipped in an **imported** spell (`POST /api/spells/import`) — can be launched as a process image (within path/symlink bounds, and subject to Sanctum/ward). Broader than the documented interpreter-wrapped types.
- **Recommendation:** Allow-list the documented script extensions; reject unknown extensions explicitly.

### [P2][security] Sanctum preflight validates only the active spell's scripts root, not resonant roots
- **Location:** `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:2726-2781` vs `ArcanumSpellScriptTool` `CollectScriptRoots` (`:1914-1938`, `Tools/ArcanumSpellScriptTool.cs:293-326`)
- **Observation:** In Strict Sanctum mode, path/tool enforcement runs against `Path.Combine(activeSpell.DirectoryPath, "scripts", scriptName)`, but the tool actually resolves scripts across multiple roots (including Arcane Resonance dependencies).
- **Impact:** A script that exists only under a resonant dependency's `scripts/` folder is executed without Sanctum pre-validating that path — defense-in-depth gap (the tool's own `ToolHelpers` checks still apply at execution time).
- **Recommendation:** Validate every candidate root from `CollectScriptRoots` (or the resolved match) before invocation.

### [P2][correctness] `/intelligence/ping` maps every failure to 500
- **Location:** `src/RetroDownfall.Arcanum.Api/Intelligence/IntelligenceEndpoints.cs:93-95`
- **Observation:** Failures always use `Status500InternalServerError`, while `/prompts/{id}/execute` uses `InferenceErrorMapper.ResolveStatusCode(turn.Error.Code)`.
- **Impact:** model-not-found / path-forbidden / validation errors surface as 500 instead of 404/403/400 — inconsistent status mapping across inference routes.
- **Recommendation:** Reuse `InferenceErrorMapper.ResolveStatusCode`.

### [P2][correctness] Rate-limit rejections bypass the `ApiResponse` envelope
- **Location:** `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs:74`
- **Observation:** Only `RejectionStatusCode = 429` is set; there is no `OnRejected` handler.
- **Impact:** Clients get a bare 429 instead of the uniform `{ data, isSuccess, error, traceId }` envelope every other `/api` route returns.
- **Recommendation:** Add an `OnRejected` that writes `ApiResponse` JSON with explicit `JsonTypeInfo`.

### [P2][security] Provider-test probe returns raw exception messages to the client
- **Location:** `src/RetroDownfall.Arcanum.Api/TheForge/ProviderTestEndpoints.cs:126-136`
- **Observation:** `catch` blocks return `ex.Message` in `ProviderTestResult`; llama-pull, by contrast, returns a sanitized `PublicPullFailureMessage`.
- **Impact:** DNS/TLS/network failure text can leak internal hostnames/paths to API callers (authenticated, but still an info-disclosure channel inconsistent with the sanitized-error posture).
- **Recommendation:** Return a generic probe-failure message; keep detail in logs.

### Streaming / `/v1`

### [P2][reliability] Broken-pipe `IOException` is uncaught on the non-OpenAI SSE/NDJSON writers
- **Location:** `src/RetroDownfall.Arcanum.Api/Streaming/EventEndpoints.cs:281-283`, `src/RetroDownfall.Arcanum.Api/TheForge/InferenceExecuteWriter.cs:97-99`, `src/RetroDownfall.Arcanum.Api/TheForge/ChronicleSseStreamWriter.cs:51-53`
- **Observation:** These write loops catch only `OperationCanceledException`. If `Response.Body.WriteAsync` throws `IOException`/`HttpIOException` on a closed socket *before* `RequestAborted` fires, it is unhandled; with `Response.HasStarted`, `ArcanumExceptionHandler` returns `false`.
- **Impact:** Noisy unhandled-exception logs on abrupt client disconnects, and inference may keep running until the hub enumerator next observes cancellation.
- **Recommendation:** Catch `IOException`/`HttpIOException`, treat as disconnect, and cancel the linked CTS promptly.

### [P2][reliability] `/v1` streaming has no keep-alive during idle gaps
- **Location:** `src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs:330-408`
- **Observation:** Unlike `SseStreamWriter.StreamAsync` (which emits `: keep-alive` comments), the `/v1` stream writes only on real chunks.
- **Impact:** During long gaps (model pull, slow provider, multi-round tool loops) reverse proxies/load balancers may idle-timeout an otherwise-healthy stream.
- **Recommendation:** Interleave keep-alive comments while awaiting hub events.

### [P2][correctness] OpenAI multimodal parsing silently drops unknown part types and is unbounded
- **Location:** `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiChatCompletionMapper.cs:107-147` + `OpenAi/OpenAiMessageContent.cs:43-48`
- **Observation:** Only `"text"` and `"image_url"` parts are handled; others are skipped with no error. The `content` parts array has no element-count cap (bounds validation only checks flattened content length after mapping).
- **Impact:** Unsupported parts are silently lost (hard to debug client-side); a 16 MiB body with a huge parts array causes heavy `StringBuilder`/`List` allocation before content-length checks apply.
- **Recommendation:** Reject unsupported part types with `400 invalid_value`; add a `MaxContentPartsPerMessage` cap during `/v1` validation before mapping.

### [P2][performance] Token preflight does a double full pass with per-message hashing
- **Location:** `src/RetroDownfall.Arcanum.Api/Intelligence/ManaPreflight.cs:57-72` + `WizardIntelligenceProvider.cs:1453,1500`
- **Observation:** Each uncached message runs `ComputeContentHashHex` (UTF-8 encode + SHA-256) before `tokenizer.CountTokens`, and compression-eligible turns run preflight before and after the rebuild.
- **Impact:** Two full hashing + tokenization passes over large histories per compression turn.
- **Recommendation:** Cache by `(encoding, contentHash)` after the first count; avoid re-hashing stable strings; count once and update incrementally.

> Note: the **hot-path full-session load** (`GetSessionAsync`) that backs these inference turns is tracked as a P1 perf item in 01-infrastructure.md.

## P3 findings

- **[P3][design] `/v1` omits server-executed `tool_calls`** (streaming `case ToolCall: break;` at `OpenAiV1Endpoints.cs:376`; buffered `ToolCalls: null` at `:304-308`). This is **intentional and documented** (README "tool_calls omitted — server-executed MCP tools stay on native `/api`", DESIGN §8.8.1) and asserted by `OpenAiV1ParityTests`. Recorded as a known parity limitation, not a defect — streaming clients relying on OpenAI tool deltas won't see them.
- **[P3][openai parity] Non-standard stream terminals:** error chunks use `finish_reason: "error"` (`OpenAiV1Endpoints.cs:604-608`); a client that disconnects before `Result` can still get a terminal chunk defaulting to `finish_reason: "stop"` (`:410-456`). Consider OpenAI's top-level-`error` stream pattern and skipping terminal frames when `aborted && finishReason is null`.
- **[P3][streaming] Internal SSE routes send `[DONE]` only on cancel** (`EventEndpoints.cs:103-106`, `ApprenticeEndpoints.cs:488-491`) — inconsistent; send on success too or not at all. Post-disconnect terminal writes use `CancellationToken.None` (`OpenAiV1Endpoints.cs:450-461`) — wasted writes to a dead socket.
- **[P3][reliability] Sync vs streaming tool-throw handling differs** — streaming substitutes a public failure message and continues (`WizardIntelligenceProvider.cs:923-957`); sync lets the exception fail the turn (`:327-336,424-453`). Align the policy.
- **[P3][docs/style] `IntelligenceEvent` uses `[JsonPropertyName]` on `/api` SSE payloads** (`Core/Intelligence/Models/IntelligenceEvent.cs:12-31`, serialized via `ArcanumJsonContext`) — either add it to the DESIGN §8.2 exception list or move to context casing.
- **[P3][reliability] Capacity frozen at startup:** `ManaPreflight` LRU capacity is read once (`ManaPreflight.cs:21-28`); reloads of `MaxMessagesPerConversationLoad` need a restart.
- **[P3][resource] Static `JsonDocument` tool schemas are never disposed** (`Tools/Arcanum*Tool.cs`) — acceptable for app-lifetime singletons; could be `JsonElement` literals.
- **[P3][docs/style] House-style blank-line drift** in `OpenAiChatCompletionMapper.cs`, `HumanPromptRegistry.cs`, `ArcanumSpellScriptTool.cs`, `SpellDependencyResolver.cs`.

## Verified strengths

- **Cancellation is genuinely wired end-to-end for inference:** `/v1` and NDJSON writers link `HttpContext.RequestAborted` into the inference token, and `WizardIntelligenceProvider` threads that token into the provider stream, tool calls, ward waits, and Grimoire writes — **client disconnect cancels the inference** (verified chain). `OperationCanceledException` is not surfaced as 500.
- **Tokenizer is cached** (`InferenceTokenizerResolver` `ConcurrentDictionary` + singleton); `ManaMeter`/`ManaPreflight` reuse it — no per-call Tiktoken construction.
- **No `AIFunctionFactory.Create` in production** — tools use hand-authored `JsonDocument` schemas with explicit `AIFunction` subclasses; `/api` DTOs serialize via `ArcanumJsonContext`; failable `Results.Json` paths pass explicit `JsonTypeInfo`.
- **Auth is comprehensive:** every `/api` and `/v1` route is under a `MapGroup` with `ApiKeyEndpointFilter` (SHA-256 digest + `FixedTimeEquals`, header length cap, duplicate-header rejection, envelope 401); no unauthenticated route found.
- **Config redaction** covers all provider secrets/URLs + `CommLink.WebhookUrl` on GET and preserves `"***"` for **existing** providers on PUT, with an SSRF guard on PUT.
- **Tool loop is bounded** (`MaxToolInferenceRounds`, clamped), wards await asynchronously (no thread blocking), tool output is capped (`ToolOutputCapBytes`), and the inference wall-clock cap (`InferenceTimeoutSeconds`) is applied via a linked CTS.
- **Spell dependency resolution** is depth-capped (3), cycle-safe, and resonant-byte-bounded. **Streaming** flushes per frame with correct `text/event-stream` + `no-cache` + `X-Accel-Buffering: no` headers and OpenAI `[DONE]` framing; the exception handler returns sanitized 500s with no stack leak.
