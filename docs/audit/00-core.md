# 00 — Core (`RetroDownfall.Arcanum.Core`)

**Scope:** primitives, configuration + clamps + path policies, the four source-gen JSON contexts, domain contracts, Intelligence bounds validation, Proving Grounds, and the Forge/Conclave logic types. 189 files, ~6.3k lines. This is the foundation every other project depends on, so correctness here has the widest blast radius.

**Method:** three parallel read-only deep-read passes ([foundations](46064aa5-67b2-4cb2-abac-388632186958), [configuration](3b518b3a-7b77-428b-bbc7-877d303103e1), [contracts + Proving Grounds](d22dd765-58a6-4139-a71b-d39382ff779b)); the highest-severity findings below were then re-verified by reading the exact lines.

Severity counts: **P1 ×1 · P2 ×11 · P3 ×11.**

---

## P1 findings

### [P1][reliability] `ConfigurationValidator` never runs at startup — invalid `arcanum.json` boots anyway
- **Location:** `src/RetroDownfall.Arcanum.Core/Configuration/ConfigurationValidator.cs:5`; registered at `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs:144`; only consumed by `src/RetroDownfall.Arcanum.Api/Configuration/ConfigurationEndpoints.cs:78,143`.
- **Observation:** `ConfigurationValidator.Validate(...)` and `OutboundUrlGuard.ValidateArcanumSettingsAsync(...)` are invoked **only** from `PUT /api/config` and `POST /api/config/validate`. Nothing calls them during host bootstrap. `ConfigurationBootstrapper` validates only JSON *parseability* (syntax), not semantics. (Verified by grepping every `.Validate(`/`ValidateArcanumSettingsAsync` call site in the solution.)
- **Impact:** An `arcanum.json` with semantically invalid settings — `DefaultModel`/`FastModel` that match no provider, MCP `RequestTimeoutSeconds < ExecuteCommandTimeoutSeconds`, `MaxJsonRpcLineBytes < ToolOutputCapBytes`, non-existent allowlist roots, blocked/SSRF webhook URL — loads successfully and the host **starts**. The failure only surfaces later at runtime (model resolution throws, tools mis-cap, perception 403s, alerts silently never fire). The whole validator + its 21-case test suite provide no startup protection.
- **Recommendation:** Run `ConfigurationValidator.Validate` + `OutboundUrlGuard.ValidateArcanumSettingsAsync` during startup (an `IHostedService`/`IStartupFilter` or in `ConfigurationBootstrapper`), failing fast with a clear message; optionally re-validate on `IOptionsMonitor` reload.

---

## P2 findings

### [P2][reliability] `Result<T>.Value` throws when serialized on the failure path
- **Location:** `src/RetroDownfall.Arcanum.Core/Primitives/Result.cs:47-49`
- **Observation:** `public T Value => IsSuccess ? _value! : throw new InvalidOperationException(...)` — a public getter with no `[JsonIgnore]`.
- **Impact:** Any direct `JsonSerializer.Serialize(failedResult)` invokes `Value` and throws, turning a domain failure into an unhandled serialization exception. Today it is masked because callers wrap in `ApiResponse<T>` first, but the primitive itself is a serialization landmine for any future direct use (and `Result<T>` types are reachable from the Api JSON context).
- **Recommendation:** Add `[JsonIgnore]` to `Value` (and ideally `IsFailure`), exposing only `IsSuccess`/`Error` to serializers.

### [P2][correctness] `ApiResponse<T>.FromResult` emits `data: <default>` on value-type failures
- **Location:** `src/RetroDownfall.Arcanum.Core/Primitives/ApiResponse.cs:10-14`
- **Observation:** failure branch is `new ApiResponse<T>(default, false, result.Error, traceId)`. For value-type `T` (e.g. `bool`, used on many config/MCP/ward toggle endpoints), `default` is `false`/`0`, not absent.
- **Impact:** Failure envelopes serialize `"data": false` next to `"isSuccess": false` — ambiguous for any client that reads `data` without first checking `isSuccess`. There is no way to distinguish "operation returned false" from "operation failed."
- **Recommendation:** Omit `Data` on failure (e.g. `[JsonIgnore(Condition = WhenWritingNull)]` with a reference/`Nullable` payload), or provide a value-type-aware failure factory.

### [P2][correctness] `PingRequestBoundsValidator` compares UTF-16 char count against a *byte* budget
- **Location:** `src/RetroDownfall.Arcanum.Core/Intelligence/PingRequestBoundsValidator.cs:43,50-52`
- **Observation:** `int maxEntryBytes = ArcanumSettingClamps.MaxEntryContentBytes(...)` but the check is `int contentChars = message.Content?.Length ?? 0; if (contentChars > maxEntryBytes)`, and the error text says "characters".
- **Impact:** The `MaxEntryContentBytes` budget (which gates stateless `/v1` + ping message content, per DESIGN §3.4) is enforced in UTF-16 code units. Multibyte/emoji content can be up to ~3-4× the intended byte size yet pass, and the limit's name and behavior disagree. This is a budget-overrun / memory-pressure vector on the inference path.
- **Recommendation:** Measure `Encoding.UTF8.GetByteCount(content)` for a byte-named limit (or rename the setting to `MaxEntryContentChars` and keep char semantics consistently).

### [P2][reliability] `ValidateOpenApiMessageCount` can NRE on null `Intelligence` settings
- **Location:** `src/RetroDownfall.Arcanum.Core/Intelligence/PingRequestBoundsValidator.cs:72`
- **Observation:** `settings.Intelligence.MaxOpenApiMessages` — direct dereference, unlike `Validate()` at line 12 which does `settings.Intelligence ?? new IntelligenceSettings()`.
- **Impact:** If `ArcanumSettings.Intelligence` is null (possible from a partial hand-edited config that binds Intelligence to null), the `/v1/chat/completions` count guard throws `NullReferenceException` instead of returning a clean validation `Result`.
- **Recommendation:** Mirror `Validate()`'s null-coalescing on this method.

### [P2][security] Proving Grounds adjudication parses & re-prompts trial output unbounded
- **Location:** `src/RetroDownfall.Arcanum.Core/ProvingGrounds/ProvingGroundsArbiter.cs:111` (`JsonDocument.Parse(output)`) and `:233` (`userPrompt.Append(output)`).
- **Observation:** The `jsonSchema` inquisitor does a full `JsonDocument.Parse(output)` with no size guard; the `semantic` inquisitor appends the entire `output` into the judge prompt with no truncation and without going through `PingRequestBoundsValidator`.
- **Impact:** A large trial target output (Apprentice goals, multi-tool transcripts, or external text passed as the trial target) forces a full in-memory parse / unbounded prompt, inflating memory, token cost, and provider latency. The normal ping bounds that protect every other inference path do not apply here.
- **Recommendation:** Clamp `output` to a configured max (reuse `MaxEntryContentBytes`/`MaxPingPromptChars`) before parse and before building the judge prompt.

### [P2][correctness] JSON-schema inquisitor passes unknown `type` values and only checks the top level
- **Location:** `src/RetroDownfall.Arcanum.Core/ProvingGrounds/ProvingGroundsArbiter.cs:313` (`_ => true`) and `:137-201` (top-level `required`/`properties.type` only).
- **Observation:** `JsonValueMatchesDeclaredType` returns `true` for any unrecognized `type` string; nested objects, `items`, `enum`, `minLength`, `additionalProperties`, etc. are never validated; the pass message claims the output "satisfies the lightweight JSON schema subset."
- **Impact:** Trials that authors believe enforce structure can pass on malformed/nested-invalid output (false positives), undermining the Proving Grounds' purpose. Fail-open on unknown type is the more dangerous half.
- **Recommendation:** Fail closed on unrecognized `type`; reject schemas using unsupported keywords at trial-definition time; document the supported subset precisely on `JsonSchemaInquisitor`.

### [P2][reliability] No cancellation check between synchronous inquisitors
- **Location:** `src/RetroDownfall.Arcanum.Core/ProvingGrounds/ProvingGroundsArbiter.cs:39-55`
- **Observation:** The `foreach` runs regex/json inquisitors synchronously; only `semantic` awaits with the token. Each regex carries a 1s `RegexMatchTimeout`, and up to 200 inquisitors are allowed (`MaxInquisitorsPerTrial`).
- **Impact:** A trial with many regex inquisitors against adversarial input can occupy a thread for up to ~200s with no cooperative cancellation, delaying client cancel and shutdown.
- **Recommendation:** `cancellationToken.ThrowIfCancellationRequested()` between inquisitors (and before each parse/match).

### [P2][correctness] Conclave descendant count ignores pagination → `maxDescendants` cap bypass
- **Location:** `src/RetroDownfall.Arcanum.Core/TheForge/ConclaveLineage.cs:167-199`
- **Observation:** `CountDescendantsOfRootAsync` calls `ListAsync(..., limit: 10_000, ...)` and iterates `page.Items` only; `page.HasMore` is never consulted.
- **Impact:** With more than 10,000 apprentices, descendants beyond the first page are uncounted, so the Conclave `maxDescendantsPerRoot` safety limit can be exceeded. (Low likelihood at single-user scale, but it silently defeats a guard rail.)
- **Recommendation:** Paginate to exhaustion, or add a repository method that counts descendants in the database.

### [P2][performance] Conclave lineage check is O(items × depth) DB round-trips
- **Location:** `src/RetroDownfall.Arcanum.Core/TheForge/ConclaveLineage.cs:173-197,203-246`
- **Observation:** For each apprentice in the page, `IsDescendantOfAsync` walks ancestors via repeated `repository.GetByIdAsync` calls.
- **Impact:** Cost grows with apprentice population × tree depth; amplifies the pagination issue and adds latency to every `cast`/Conclave spawn check.
- **Recommendation:** Maintain a lineage/root column or do a single recursive-CTE query.

### [P2][reliability] `ApprenticePlanParser.ParsePlan` throws raw `JsonException` on malformed plans
- **Location:** `src/RetroDownfall.Arcanum.Core/TheForge/ApprenticePlanParser.cs:14-19`
- **Observation:** `JsonSerializer.Deserialize(...)` is not guarded; only the empty/oversize cases throw `InvalidOperationException`. The sibling `TryParseRevisedPlan` (`:71-115`) wraps the same call in `try/catch (JsonException)`.
- **Impact:** Malformed LLM plan JSON surfaces as an uncaught `JsonException` from the parser, inconsistent with the revise path and forcing every caller to know to catch a serialization exception type.
- **Recommendation:** Catch `JsonException` and throw a domain error (or return `Result<List<PlanStep>>`) for symmetry with `TryParseRevisedPlan`.

### [P2][correctness] `SanctumConfig` exposes mutable backing lists as `IReadOnlyList`
- **Location:** `src/RetroDownfall.Arcanum.Core/Sanctum/SanctumConfig.cs:19-26,32-39,45-52`
- **Observation:** `get => _allowedPaths;` returns the internal `List<string>` typed as `IReadOnlyList<string>` (and likewise for the other allow-lists).
- **Impact:** A consumer can downcast and mutate the sandbox allow-lists after construction, defeating the immutability the record implies — a defense-in-depth concern for a security-sensitive type.
- **Recommendation:** Return `Array.AsReadOnly(...)`/`ImmutableArray` or copy on get.

### [P2][performance] `Result`/`Result<T>` are heap-allocated classes
- **Location:** `src/RetroDownfall.Arcanum.Core/Primitives/Result.cs:3,33`
- **Observation:** Both are reference types; every `Success`/`Failure`/implicit conversion allocates.
- **Impact:** These primitives are returned on nearly every domain/repository/intelligence call, so each adds a small-object allocation and GC pressure on hot paths.
- **Recommendation:** Consider a `readonly struct` design. Note the constraint: `Result<T> : Result` inheritance must be unwound first (structs can't inherit), so this is a deliberate, non-trivial refactor — capture as a perf backlog item rather than a quick fix.

---

## P3 findings

### [P3][correctness] `Error.Details` is downcast-mutable and breaks value equality
- **Location:** `src/RetroDownfall.Arcanum.Core/Primitives/Error.cs:7-17`
- **Observation:** `Details` is copied into a `new List<ConfigurationValidationError>(Details)` exposed as `IReadOnlyList<...>?`. The "defensive copy" comment holds for the *source* list, but (a) a consumer can cast the property back to `List<>` and mutate it, and (b) `readonly record struct` equality compares `Details` by reference, so two errors with equal detail *content* but different list instances are unequal.
- **Impact:** Violates the documented immutability guarantee and makes `Error` equality unreliable for caching/dedup/tests when details are populated. Low real-world impact (single-user, `Details` rarely set, `Error.None` is the hot case).
- **Recommendation:** Store `ImmutableArray<ConfigurationValidationError>` (immutable + value equality).

### [P3][maintainability] `Result/Result<T>` constructor + `ApiResponse` validate-on-startup story is undocumented
- **Location:** `src/RetroDownfall.Arcanum.Core/Primitives/Result.cs:5-18`
- **Observation:** The invariant checks (success cannot carry an error; failure must carry one) are correct and good — but there is no doc-comment, and `Error.None` sentinel comparison relies on struct equality of `Error` (which has the `Details` reference-equality caveat above; `None` always has null `Details`, so it's safe today).
- **Impact:** Future addition of a non-null `Details` default to `Error.None` would silently break the `error != Error.None` guard. Worth a guarding comment/test.
- **Recommendation:** Add an XML remark and a unit test pinning `Error.None` equality semantics.

### [P3][maintainability] `ArcanumEvent` doc-comment points at the wrong project for its JSON context
- **Location:** `src/RetroDownfall.Arcanum.Core/Events/ArcanumEvent.cs:4-5`
- **Observation:** Comment says subtypes "are registered on `ArcanumJsonContext`" but that context lives in `RetroDownfall.Arcanum.Api`, not Core.
- **Impact:** Misleads anyone auditing Core's AOT serialization surface; easy to miss event registration gaps.
- **Recommendation:** Name the real path (`RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs`).

### [P3][maintainability] `LlamaServerEvent` is published but has no SSE subscriber
- **Location:** `src/RetroDownfall.Arcanum.Core/Events/LlamaServerEvent.cs` (defined + JSON-registered; consumed via the bus by `LlamaServerManager`, but `EventEndpoints` exposes only `/events/daemon` + `/events/mcp`).
- **Impact:** Llama lifecycle events are published into the bus with no consumer endpoint — silently dropped, or a missing feature.
- **Recommendation:** Add an `/events/llama` SSE endpoint or document the type as internal-only.

### [P3][correctness] `ListPageResult<T>.Items` has no null guard
- **Location:** `src/RetroDownfall.Arcanum.Core/Primitives/ListPageResult.cs:3-7`
- **Observation:** Positional `T[] Items` with no normalization; can be null after deserialization.
- **Impact:** Downstream `foreach`/`.Length` NREs if a null array is ever bound.
- **Recommendation:** Normalize to `Array.Empty<T>()` (or use `ImmutableArray<T>`).

### [P3][maintainability] `Session.Clone()` silently drops `Entries`
- **Location:** `src/RetroDownfall.Arcanum.Core/Storage/Entities/Session.cs:31-47`
- **Observation:** Copies scalars but always sets `Entries = new List<Entry>()`.
- **Impact:** Callers expecting entries to be cloned get an empty collection with no signal.
- **Recommendation:** Rename to `CloneHeader()` or document the intent.

### [P3][correctness] Semantic judge YES/NO uses `StartsWith`, not exact match
- **Location:** `src/RetroDownfall.Arcanum.Core/ProvingGrounds/ProvingGroundsArbiter.cs:280-282`
- **Observation:** `answer.StartsWith("YES"...)` / `StartsWith("NO"...)`. "NOPE"/"NOT"/"YESSIR" classify as NO/YES.
- **Impact:** Edge-case misclassification; low risk given `SemanticJudgeMaxTokens` default of 8 and the system prompt, but not strict.
- **Recommendation:** Trim punctuation and require exact `YES`/`NO`.

### [P3][performance] Regex inquisitor relies on the framework's 15-entry static cache
- **Location:** `src/RetroDownfall.Arcanum.Core/ProvingGrounds/ProvingGroundsArbiter.cs:76`
- **Observation:** Uses the static `Regex.IsMatch(input, pattern, options, timeout)` overload. .NET caches up to `Regex.CacheSize` (default 15) compiled patterns for static calls, so this is *mostly* fine, but a trial cycling through >15 distinct patterns re-parses each time, and there is no pattern-length cap before compilation.
- **Impact:** Minor repeated-compilation cost in pathological trials; no compilation-time DoS bound (the 1s timeout covers matching, not compilation).
- **Recommendation:** Cap pattern length at trial-definition time; optionally keep a small explicit `Regex` cache keyed by `(pattern, options)`.

### [P3][correctness] `AdjudicateAsync` throws on over-limit instead of returning a verdict/Result
- **Location:** `src/RetroDownfall.Arcanum.Core/ProvingGrounds/ProvingGroundsArbiter.cs:31-35`
- **Observation:** Over-`maxInquisitors` throws `InvalidOperationException`; the API runner pre-validates and returns a `Result` for the same condition.
- **Impact:** A direct arbiter caller that skips pre-validation faults; two error models for one condition.
- **Recommendation:** Return a failed `Result`/verdict for consistency.

### [P3][maintainability] `IChronosyncEngine.AnalyzeAndSyncAsync` lacks a `CancellationToken`
- **Location:** `src/RetroDownfall.Arcanum.Core/Chronosync/IChronosyncEngine.cs:7`
- **Observation:** `Task<ChronosyncReport> AnalyzeAndSyncAsync(PatternSnapshot currentSnapshot);` — no token, unlike the rest of Core's async contracts.
- **Impact:** Long-running analysis/sync cannot be cooperatively cancelled.
- **Recommendation:** Add `CancellationToken cancellationToken = default`.

### [P3][docs/style] Minor house-style + doc drift
- **Locations:** `src/RetroDownfall.Arcanum.Core/Primitives/Error.cs:2` (unused `using System.Text.Json.Serialization;`); `src/RetroDownfall.Arcanum.Core/ProvingGrounds/Inquisitor.cs:7-13` and `Storage/Entities/WorkspaceContext.cs`, `Storage/Entities/MageSetting.cs` (missing the one-blank-line-after-each-line convention applied elsewhere); `ProviderResolver` tag matching is one-way (`llama3` configured vs `llama3:8b` requested fails) and is not called out in DESIGN §3.4.
- **Impact:** Cosmetic / minor doc accuracy.
- **Recommendation:** Trim the unused import, align spacing, and document the tag-matching asymmetry (or make `ModelNameMatches` symmetric).

---

## Well-implemented (verified)

- `Result` constructor enforces success/failure↔error invariants (`Result.cs:5-18`).
- `Entry.Session` is `[JsonIgnore]`, preventing navigation cycles in `TheForgeJsonContext` round-trips.
- `ConfigurationJsonContext` registration is comprehensive for the full settings graph; `ConfigurationBootstrapper` deserializes via source-gen (AOT-safe).
- `ArcanumSettingClamps` numeric clamps and the `EffectiveSpellMaxFileSizeBytes`/`EffectiveCodexMaxSizeBytes` `Math.Min` helpers match DESIGN §3.4.
- `WorkspaceRootPolicy.EnforceAllowedRoots` is deny-by-default on an empty allow-list, and rejects outbound symlink escapes for existing paths (test-covered).
- Proving Grounds semantic judge uses a linked CTS + `CancelAfter` and cleanly distinguishes timeout vs. caller-cancel; `JsonDocument` is `using`-disposed; inquisitor count is clamped.
- `LlamaCacheKey`/`LlamaSourceUrl`/`LlamaAdditionalArgumentsPolicy` are tight (http/https-only, `--host`/`--port` override blocked, `[GeneratedRegex]`).

## Cross-level findings discovered here (tracked in their owning report)

- **Codex write uses raw cap, not `EffectiveCodexMaxSizeBytes`** — `src/RetroDownfall.Arcanum.Api/TheForge/CodexEndpoints.cs:217`; and prompt-test codex read uses the workspace cap not the codex cap — `src/RetroDownfall.Arcanum.Api/TheForge/PromptEndpoints.cs:426-432`. → **02-api.md**
- **CommLink `AllowedHosts`/empty `AllowedSchemes` not validated at startup; silent no-send at dispatch** — `WebhookCommLinkDispatcher.cs:45-67`. → **01-infrastructure.md**
- **`LlamaServerManager` port math can exceed 65535**; **duplicate inline `Math.Clamp` in `McpConnectionManager.cs:1691`**; **`SpellScanner` uses hardcoded spell bounds instead of configured `Spells:MaxDependencies`/`MaxDeclaredTools`** (`SpellScanner.cs:759-762`). → **01-infrastructure.md**

## Coverage

All of `Primitives/`, `Serialization/`, `Events/`, `Storage/`, `Configuration/` (40 files), `Intelligence/` (logic + contracts), `ProvingGrounds/`, `Chronosync/`, `CommLink/`, `Daemons/`, `Environment/`, `Hosting/`, `LlamaCpp/`, `Logging/`, `Mcp/`, `Pattern/`, `Sanctum/`, `Security/`, `TheForge/`, `Wards/`, `Workspaces/` contracts were read. Pure DTO/interface files (the large majority of Core's 189 files) were confirmed to be contract-shaped (consistent `CancellationToken`, `Result<T>`, `IReadOnlyList<T>`) with no logic defects.
