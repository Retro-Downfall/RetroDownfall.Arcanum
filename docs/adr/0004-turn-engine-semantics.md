# ADR 0004: TurnEngine semantic events and logical-run ownership

- Status: Accepted
- Date: 2026-07-21

## Context

Phase 0 introduced shared seams (`IModelCallExecutor`, `ITurnBudget`, `ITurnRunWriter`, `IBudgetReservationService`, `IIdempotencyClaimStore`, `IToolResultMaterializer`) but left duplicated buffered and streaming orchestration in `WizardIntelligenceProvider`. Phase 1 collapses those paths into one logical-run owner that emits typed semantic events; response shapes are projections. This ADR locks event semantics, commit boundaries, transport/replay layering, and mode policies so extraction cannot invent hidden divergence.

## Decision

### Ownership

`TurnEngine` owns the logical run: preflight, reservation/run lifecycle, `TurnContextSeed` (once), provider candidates + fallback, `ProviderAttemptContext` (per attempt), model/tool loop (including no-tools compatibility retry), output validation, and finalization.

`WizardIntelligenceProvider` is a thin facade over an internal `ITurnEventSource` / `TurnExecutionCoordinator`.

### Transport and replay boundary

`TurnExecutionCoordinator` is the sole consumer of semantic `TurnEvent`s and applies exactly one coordinator projection per request. It does not own HTTP serialization. Streaming projections write typed output into a bounded transport-facing channel. Production `/v1` currently chooses `IntelligenceEventProjection`, then the authoritative compatibility mapper in `OpenAiV1Endpoints` converts those typed native frames to SSE. The separate `OpenAiSseProjection` path is a semantic helper/characterization seam, not the instance used by that route. It shares reasoning-field and typed-error rules with the endpoint mapper, but does not define production terminal usage chunks or tool-argument fragmentation. The HTTP writer serializes outputs to the live response and exact-byte idempotency capture. After an idempotent disconnect, the same projection/serialization pump continues capture-only; after a non-idempotent disconnect, execution is cancelled and drained through `RunAbandoned`. Semantic events are never replayed in place of captured wire bytes.

### Event delivery

Semantic events flow through a bounded `Channel<TurnEvent>` via centralized `TurnEventEmitter.EmitAsync` (ordered sequence assignment, terminal guard). Ward/HITL observers emit request events **before** waiting so projections see them in real time. Keep-alives are transport-only and are never engine events.

### Semantic events (Phase 1 — not durable)

```
RunStarted, TurnStatusChanged, SessionBound, ContextCompressed,
ProviderAttemptStarted, ProviderSelected, ProviderAttemptCommitted,
ProviderAttemptCompleted, ProviderAttemptFailed,
ModelCallStarted, TextDelta, ReasoningDelta, ModelCallCompleted, ModelCallFailed,
ToolCallProposed, ApprovalRequested, ApprovalResolved,
HumanInputRequested, HumanInputReceived,
ToolInvocationStarted, ToolInvocationCompleted,
OutputValidated, RunCompleted, RunFailed, RunAbandoned
```

Terminal events carry `TurnTerminationReason`, optional stable `Error`, accumulated usage, warnings, and interruption metadata. `ReasoningDelta` carries one client-safe `ReasoningContentSegment` separately from `TextDelta`. `RunCompleted` is authoritative for buffered projection (final answer, ordered reasoning segments, usage, tool calls, finish reason, warnings, session id, structured-output warning state).

### Invariants

- Exactly one `RunStarted`. Zero or one `SessionBound`.
- Every provider attempt: one `ProviderAttemptStarted`; exactly one `ProviderAttemptCompleted` or `ProviderAttemptFailed`; at most one `ProviderAttemptCommitted`. At most one attempt commits.
- Provider commitment occurs **before** any provider-derived client-visible event.
- Answer and reasoning remain distinct through model updates, semantic events, projections, validation, and persistence.
- Provider `ProtectedData` never becomes a semantic event; it is retained only on the in-memory raw response needed for same-provider tool continuation.
- Every `ModelCallStarted` has one `ModelCallCompleted` or `ModelCallFailed`.
- Every `ToolInvocationStarted` has one `ToolInvocationCompleted`, unless terminal cancellation.
- Every `ApprovalRequested` / `HumanInputRequested` has a matching resolved/received event, unless terminal cancellation.
- Exactly one of `RunCompleted`, `RunFailed`, or `RunAbandoned`; nothing follows.
- Sequence numbers are strictly monotonic. Tool calls remain sequential within a round; tool-call/result groups remain paired.
- Attachment injection occurs after all exchanges in the round.
- Client-forwarded calls (`ToolCallDisposition.ClientForwarded`) never enter authorization or invocation.
- Run, Grimoire, reservation, accounting, idempotency, and finalization lifecycles execute at most once. Every provider lease is disposed exactly once.

### Provider fallback commit (from raw model updates)

Commitment is driven by `IModelCallExecutor` `ModelCallUpdate`s **before** projection (including when guardrails withhold tokens):

- First non-empty provider answer delta (even if guardrail/strict-buffered)
- First provider reasoning item, including visible text, protected-only data, or output suppressed by capability/request policy
- First complete actionable tool proposal (commits before authorize/invoke)
- Successful empty round completion

Does not commit: run/session/status, provider selection, `ModelCallStarted`, usage-only, keep-alives, auxiliary/internal model output.

After commit, connectivity failure terminates the run — no provider switch. The same commitment also prohibits the outer "model does not support tools" compatibility restart. A hidden/protected reasoning item is commitment even when no frame was emitted, so fallback cannot discard provider state merely because the client did not see it.

### Mode policies (one loop, different flags)

1. **Structured output:** answer text alone is schema-validated; reasoning is never treated as the answer. Buffered retries use the executor + budget and replace rejected answer/reasoning. Best-effort streaming validates post-hoc without retry. Strict streaming buffers answer/reasoning; with retries configured it may use buffered replacement calls, deriving accepted replacement runs from `ChatResponse.Messages[].Contents` so answer/reasoning/answer interleaving is preserved for both safety inspection and release. Otherwise it fails without releasing buffered output.
2. **Tool failures (Phase 1):** buffered uses `Arcanum:Intelligence:TolerateToolFailures`; streaming always suppresses invocation failures. Unifying failure behavior is a separate follow-up PR.
3. **Guardrails streaming:** provider commitment occurs on raw answer or reasoning content. Buffered mode withholds both event kinds, inspects the accepted answer plus projectable reasoning, and then releases ordered runs or returns `RunFailed` with no client-visible content. Explicit passthrough retains its leakage warning.
4. **Disconnect:** ADR 0003 `Auto`. Capture overflow → Abandoned/non-replayable; never Complete a partial response.
5. **ask_human** vs Ward: distinct `HumanInput*` vs `Approval*` events; both emit request-before-wait.
6. **OpenAI `/v1`:** the production endpoint authoritatively maps native `IntelligenceEvent` frames and filters toolResult/toolError/ward/status. `OpenAiSseProjection` is only a semantic helper/characterization for shared reasoning and error rules; it is not the route's projection instance and does not specify terminal usage or tool fragmentation. This is not an engine mode.

### Reasoning projection and persistence boundary

Native buffered projection returns ordered reasoning segments beside answer text. Native NDJSON emits `type:"reasoning"` with `{ text, output }`. OpenAI buffered and both SSE mapping paths use additive `reasoning_summary` / `reasoning_content`; answer `content` stays answer-only. Provider-protected data is never projected. Characterization tests keep the shared reasoning-field and typed-error rules aligned. Production endpoint tests separately own terminal chunk shape, `include_usage` behavior, and tool-argument fragmentation.

Reasoning bodies are ephemeral. Grimoire assistant entries, logs, audit JSONL, inference traces/exports, Master context, Apprentice prompts/results/checkpoints, and Chronicle events never contain them. Audit/accounting/trace surfaces may retain counts, output kind, or event type only. Apprentice orchestration remains a Master/Apprentice relationship; first-class reasoning does not create a reasoning handoff.

### HasIdempotencyKey

Not a public `PingRequest` field. Native/OpenAI endpoints pass Phase 0 idempotency execution context into the internal turn request. Non-HTTP callers default to `false`. A forged body cannot set the flag.

### Project boundaries

`IModelCallExecutor` lives in Core and returns `ModelCallPurpose`-tagged `ModelCallUpdate` / `ModelCallResult`. It classifies MEAI `TextContent` and `TextReasoningContent` independently and does not emit Api `TurnEvent`s. TurnEngine decides whether to emit client-visible `TextDelta` / `ReasoningDelta` (SpellRouting / LexiconExtraction never become client deltas; structured-output retries are buffered candidates).

### Attempt isolation

`TurnContextSeed` once per logical run; `ProviderAttemptContext` per candidate (cloned messages, lease, options, no-tools retry state, commitment). No tool execution before commit. Failed pre-commit attempts must not contaminate the next candidate’s messages/options.

## Consequences

Wizard thin-facade cutover can proceed with characterization fixtures and invariant tests. Phase 3 may select events for durability; Phase 1 does not. Tool-failure unification remains a documented follow-up.

See also: ADR 0001 (safe defaults), ADR 0002 (cost composition), ADR 0003 (disconnect), `docs/Arcanum.CHAT-LOOP.md`.
