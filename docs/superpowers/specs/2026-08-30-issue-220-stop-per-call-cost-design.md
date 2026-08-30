# Issue #220: Stop Paying the Ward Gate's Per-Call Cost

**Status:** Approved design, pending implementation.

**Branch:** `codex/issue-220-stop-per-call-cost`, cut from the tracked `remove-wards` aggregation branch at `b2561a3a91dcb866552cc203291b7a52d755dc38`. That branch contains current `main` at `decdf011f69ab91c1e48a0d50c2bbf97cd928162` and the completed #216-#219 slices.

**Issues:** Delivery slice #220 under epic #197. Completing this slice does not close #197; #221 and #230 remain separate follow-on work. `remove-wards` remains the integration branch until every #197 slice is complete.

## 1. Objective

Remove the obsolete Ward decision path's remaining per-tool-call settings projection and redundant record-payload/buffering work without changing what an operator sees or what a session remembers.

Every server-executed tool call still produces the same ordered, informational `warded` / `wardResolved` pair with one unique Ward id and origin `ungated`. Every session-backed tool interaction still produces live `toolCall` / `toolResult` events, persists its tool name, arguments, and result in the Grimoire, and reconstructs into Command Center's Incantations pane when the session is recalled.

## 2. Approved invariants

### 2.1 Ward record contract

- Emit exactly one `warded` event before the atomic automatic resolution and one `wardResolved` event after it.
- Use one freshly generated Ward id for the pair.
- Record an allowed `WardResolutionOrigin.Ungated` tombstone through `IWard.RecordAutomaticResolution`.
- Keep the tombstone invisible to `GET /api/wards`; a competing manual resolution continues to return `Ward.AlreadyResolved` / HTTP 409.
- Increment `arcanum_ward_decisions_total` once with the canonical tool name and `origin=ungated`.
- Preserve the pair for successful calls, unknown tools, Sanctum denials, tolerated failures, `apply_patch` precondition refusals, and `retire_covenant`.
- Preserve the existing `workspace_check` risk disclosure even when that tool has no model-supplied arguments.

### 2.2 Tool observability and durable recall

The Ward record is not the tool-interaction record. The latter remains authoritative and is outside the optimization boundary:

- Native streaming emits `toolCall` before execution and `toolResult` after execution. A tolerated exception also emits `toolError` before its synthetic `toolResult`.
- Command Center consumes those events into the live Incantations pane.
- A session-backed normal call appends an assistant `ToolCall` Entry and a system `ToolResult` Entry through `GrimoireTurnWriter.TryAppendToolInteractionAsync` and `GrimoireRepository.AppendToolInteractionAsync`.
- The call Entry retains the tool name and exact bounded argument snapshot; the paired result Entry retains the exact bounded result.
- `apply_patch` retains its stronger deterministic receipt-backed call/result persistence and suppresses the generic duplicate append.
- Session recall continues to route persisted tool Entries through `PersistedToolInteraction` and `SessionWorkspaceService.IngestHistoryTool` into the Incantations pane.

Ordinary tool persistence keeps its current boundary: it applies to session-backed turns and is best effort if the Grimoire append itself fails, while `apply_patch` remains fail-closed around its mandatory receipt. Issue #220 neither weakens nor strengthens that boundary. Stateless calls have no session to recall.

### 2.3 Boundaries that remain authoritative

`Ungated` remains audit information, not execution authority. The change does not bypass or reorder Covenant retirement preflight and disclosure, Sanctum, `WorkspacePathPolicy`, edition and host-process policy, Artifact Attunement, tool-specific validation, or `workspace_check` eligibility.

`ForbiddenArts` remains an operator-authored advertisement filter used only when a request selects `ToolPolicy.NoForbiddenArts`. `UnattendedMode` remains the operator-facing default for genuine human-input tools. Neither becomes an execution gate.

## 3. Scope

### 3.1 In scope

- Resolve the retained compatibility engine's `WardSettings` once when the singleton `WardGate` is constructed instead of during every tombstone prune.
- Remove the remaining `ResolveWard()` call from the per-turn tool-advertisement filter by reading the public `ForbiddenArts` policy directly; its default is already empty and it requires no compatibility-engine projection.
- Guard the Ward argument-document builder so an ordinary call with neither arguments nor a tool-specific disclosure never enters the builder.
- Preserve argument parsing, malformed-input wrapping, redaction, and disclosure merging whenever a record carries a payload.
- Avoid allocating a two-item Ward-event buffer for live streaming, where both events are emitted directly and the returned buffered collection is empty.
- Retain buffering for non-live paths so the pair survives a tolerated invocation exception.
- Measure a fixed multi-tool path before and after the implementation and post the evidence on issue #220 during closeout.
- Add explicit regression coverage for live tool observability, durable session entries, and recall into Incantations.

### 3.2 Out of scope

- Removing the Ward id, automatic-resolution tombstone, active-Ward compatibility API, `IWard.WardAsync`, or historical resolution origins.
- Changing the shape, ordering, redaction, or meaning of Ward, tool-call, or tool-result events.
- Making ordinary tool persistence fail-closed or adding durable Ward-frame storage.
- Removing or changing `ForbiddenArts`, `UnattendedMode`, or `ToolPolicy.NoForbiddenArts`.
- Changing Covenant retirement classification, preflight, capability, Campaign binding, disclosure-before-effect accounting, persistence, or Sanctum behavior.
- The repository-wide Ward terminology and qualification sweep owned by #221.
- Any work owned by #230 or closing epic #197.
- Merging `remove-wards` into `main`.

## 4. Considered approaches

### 4.1 Cache the retained projection and remove only redundant work - selected

Capture one `WardSettings` projection in the singleton `WardGate`, read `ForbiddenArts` directly at the advertisement seam, skip empty/no-disclosure record payloads, and allocate the event buffer only for buffered emission.

This is the smallest change that meets #220 while preserving the compatibility engine's injectable timeout/capacity behavior and every observable contract.

### 4.2 Delete `WardSettings` from production - rejected

Move timeout and capacity to hard-coded `WardGate` constants and delete `ResolveWard` from every production path.

This would remove the projection entirely, but it broadens a performance slice into compatibility-engine restructuring and removes a useful test seam. Resolving once per singleton lifetime is sufficient.

### 4.3 Add a singleton Ward policy service - rejected

Introduce a new DI abstraction that owns both compatibility-engine settings and advertisement policy.

This centralizes lifetime explicitly but adds another service, interface dispatch, registrations, and constructor dependencies to answer a question that the existing singleton and request settings already answer. It is unnecessary surface.

## 5. Runtime design

### 5.1 Host-lifetime compatibility settings

`WardGate` remains the singleton `IWard` implementation and keeps `IOptionsMonitor<ArcanumSettings>` as its construction input. Its constructor evaluates `settings.CurrentValue.ResolveWard()` once and stores the resulting `WardSettings` in a readonly field.

`WardAsync` reads `MaxActiveWards` from that cached projection. `PruneResolvedTombstones` reads `TimeoutSeconds` from it. Neither method accesses `IOptionsMonitor.CurrentValue`, runs `Concat` / `Distinct` / `ToList`, or allocates another `WardSettings`.

This lifetime is consistent with the documented host configuration model: configuration changes require a host restart. The two retained compatibility-engine bounds are not public configuration keys.

### 5.2 Advertisement without a runtime projection

`WizardIntelligenceProvider.ApplyToolPolicyFilters` keeps `ToolPolicy.NoForbiddenArts` behavior but supplies `ToolRiskClassifier.BuildForbiddenToolNames` from `settings.Value.Security?.Ward?.ForbiddenArts ?? []`.

The runtime default list is empty, so the projection does not contribute another value. The filter still uses the request scope's settings snapshot and still performs case-insensitive exclusion only when the caller selects `NoForbiddenArts`.

After this change, production calls `ResolveWard()` once per `WardGate` singleton lifetime and nowhere on a turn or tool-call path.

### 5.3 Conditional Ward argument materialization

`RecordUngatedWardResolutionAsync` determines the tool-specific disclosure before building a JSON document.

- Empty/whitespace argument snapshot plus empty disclosure: `WardArguments` is null and the builder is not called.
- Non-empty argument snapshot: parse and clone the current payload exactly as today.
- Malformed non-empty snapshot: retain the current `{ "raw": ... }` wrapper.
- Any `workspace_check` call: invoke the builder even when arguments are empty so `_arcanumRiskDisclosure` remains present.
- A caller-supplied `_arcanumRiskDisclosure` remains replaced by the host-owned value.

The builder receives the already-resolved disclosure so it does not repeat classification. Its precondition requires at least one of arguments or disclosure; that contract makes an accidental empty/no-disclosure invocation a focused test failure instead of an invisible regression.

### 5.4 Live emission without a redundant buffer

`ProcessSingleToolCallAsync` creates `List<IntelligenceEvent>(2)` only when `liveWardEmit` is null. Live streaming passes no buffer: `EmitWardEventAsync` writes each frame directly, and `ProcessedToolCall.WardEvents` receives `Array.Empty<IntelligenceEvent>()`.

Buffered execution keeps the two-item list. This is required because a tool can throw after its Ward pair is recorded, and the tolerant catch must return those already-created frames to the buffered caller.

No event object is removed. Only the empty list object and its backing two-slot array disappear from the live path.

### 5.5 Retained per-call work

The following costs remain because an observable or compatibility contract consumes them:

- Fresh Ward id string: correlates the two frames and identifies the automatic-resolution tombstone.
- Two `IntelligenceEvent` values: the public record-only event contract.
- `WardResolution` and tombstone insertion: preserves atomic `AlreadyResolved` behavior.
- One metric measurement: preserves the Ward decision counter.
- Argument JSON document and clone when the frame actually carries arguments or disclosure.
- Buffered two-event collection on non-live paths: preserves frames across tolerated failures.

Issue #220 does not replace these with a separate batching, pooling, or durable subsystem.

## 6. Error, cancellation, and ordering behavior

- Cancellation while emitting either live Ward event continues to propagate.
- A failure after `warded` but before automatic resolution remains visible according to the existing emitter/caller boundary; this change does not reorder the operation.
- A tolerated tool exception still records the Ward pair and produces `toolError` plus `toolResult`.
- A Grimoire append failure for an ordinary session tool remains warning-only under the existing contract; no Ward optimization may suppress the attempted append.
- `apply_patch` keeps its persisted-session precondition and mandatory receipt classification.
- A zero-argument `workspace_check` losing its disclosure is a release-blocking regression.

## 7. TDD and measurement design

### 7.1 Characterization gates

Before production edits, run the existing focused Ward, tool-persistence, and Incantations reconstruction tests. Add focused characterization coverage where needed to prove:

- a multi-tool session emits a live `toolCall` / `toolResult` pair for every call;
- the Grimoire stores paired call/result Entries with the expected name, arguments, and result;
- recalling the session reconstructs those entries into Incantations;
- the Ward pair remains ordered and shares one id;
- `workspace_check` retains its disclosure.

Characterization tests may be green before the optimization. Every production behavior change still begins with its own failing test.

### 7.2 Settings-resolution RED/GREEN cycle

Use a counting `IOptionsMonitor<ArcanumSettings>` in `WardGateTests`. Construct one gate, record multiple automatic resolutions, query active Wards, and exercise retained compatibility behavior. Before caching, `CurrentValue` is read again during each prune; the RED assertion expects exactly one access for the gate lifetime. After caching, it passes.

### 7.3 Allocation RED/GREEN cycle

Measure a warmed, synchronous fixed-N tool path with `GC.GetAllocatedBytesForCurrentThread`. Use identical tools and pre-created inputs against:

- a settings snapshot with an empty `ForbiddenArts` list; and
- a settings snapshot with a large fixed `ForbiddenArts` list.

Before caching, each call rebuilds and copies the large list, so the configured run allocates materially more. After caching, construction absorbs the one projection and measured per-call allocation is independent of list size within a narrow fixed tolerance. The assertion reports N, both byte totals, and the delta so the RED run supplies the before measurement.

### 7.4 Argument-builder RED/GREEN cycle

Add a focused builder-contract test that rejects an invocation carrying neither arguments nor disclosure, then add a pipeline test showing a zero-argument ordinary call still succeeds with null `WardArguments`. The pipeline can pass only by taking the branch before the builder. Pair it with a zero-argument `workspace_check` test that requires the host disclosure.

### 7.5 Live-buffer RED/GREEN cycle

Process a call with a live Ward emitter and assert the two frames arrive through that emitter while `ProcessedToolCall.WardEvents` is the shared empty array. The current per-call `List<IntelligenceEvent>` fails the identity assertion; lazy allocation makes it pass. Keep the buffered tolerated-failure test asserting both returned frames.

### 7.6 Before/after closeout evidence

Run the same fixed-N measurement on the unoptimized RED tree and the final implementation using the same machine, configuration, build mode, warmup, N, and test command. Record:

- commit ids;
- host/runtime and build configuration;
- tool names and N;
- warmup/sample method;
- total and per-call allocated bytes;
- settings-dependent delta;
- the retained costs that intentionally remain.

Post the evidence as an issue #220 closeout comment. The issue's reference to a PR body is not part of this repository's Git workflow; the local feature branch merges directly into `remove-wards` after verification.

## 8. Documentation

No canonical product documentation change is expected because event shapes, configuration, runtime behavior, and persistence semantics remain unchanged. This approved specification and its implementation plan document the internal lifetime and measurement contract. If implementation reveals a real public or architectural semantic change, update the owning canonical document in the same commit instead of deferring it to #221.

## 9. Delivery and verification

- Keep all implementation commits on `codex/issue-220-stop-per-call-cost`.
- Review the complete branch diff before final verification.
- Run focused tests during RED/GREEN cycles.
- Run the applicable full build, test projects, coverage threshold, AOT/IL gate, native SQLCipher verification, and repository cleanliness checks once on the reviewed implementation tree with zero errors and zero warnings.
- Merge the verified branch directly into local `remove-wards` without changing its tree, delete the temporary local branch, and push `remove-wards`.
- Post the before/after evidence, close issue #220, and set its project item to Done.
- Leave #197, #221, #230, and `main` unchanged.
