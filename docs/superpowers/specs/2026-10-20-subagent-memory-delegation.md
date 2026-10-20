# Least-authority subagent memory delegation (#107)

Status: architecture and limits approved by the user on 2026-10-20. Writing this spec/plan, implementation with TDD, autonomous reviews, full qualification, integration into `memory-enhancement` and closure of #107 when green are authorized; no further approval gate is introduced. Baseline `0845aa08`; implementation branch `codex/issue-107-memory-capsules`.

## 1. Product boundary

Add one buffered native inference wrapper, `POST /api/intelligence/memory-delegation`, accepting a parent `PingRequest` and one explicit operator grant. The parent runs ordinary inference and may select the grant in one ordinary `delegate_task` call. Its child receives only selected exact memory through a separate host-owned capability. Existing native routes, OpenAI compatibility, CLI, Forge, background work, A2A and ungranted delegation retain their behavior.

A selector is an invocation-local name, not a bearer credential. Model arguments may select an already-authorized grant; they cannot supply its scope, versions, expiry or authority. There is no capsule CRUD, envelope token, durable executable authority, checkpoint plaintext or recovered capsule execution.

The child still runs with `ArcanumInvocationContext.None`, no tools and no ambient retrieval. Ordinary Covenant read, mutation and retirement predicates remain false. A distinct capsule materializer supplies exact selected fragments without widening the invocation. The wrapper supports the ordinary Session-backed or stateless parent forms; Session-backed protected tool-result persistence must be covered rather than silently narrowing this route to stateless work.

## 2. Wire and selection contract

Use source-generated `MemoryDelegationRequest(PingRequest Parent, MemoryDelegationGrant Grant)`. The grant contains explicit Global-or-Campaign scope, exact Covenant/Saga/Lexicon target arrays and closed purpose `DelegatedTask`. Coordinates follow the established evaluation forms: Covenant entry/version/fragment digest; Saga memory/version/hash-format/content digest; Lexicon entry/version/hash-format/content digest. Reject unknown grant members, invalid enum values, duplicate targets and malformed coordinates; freeze defensive copies after validation.

Reuse `PingRequestBoundsValidator`, `CanonicalPingResolution` and authenticated `ArcanumInvocationContexts.ForTurn`. Grant scope must equal canonical parent scope; Global versions may participate inside a Campaign scope only under normal owning-store/linker eligibility. Parent context policy `None`, missing clean request epoch, missing required capabilities/subagent support or `ToolPolicy.NoTools` refuses before protected hydration or inference. The grant may select exact versions the authenticated parent can lawfully read even if they did not fit its normal context window.

Prepare exactly one selector, `memory`. Only the wrapper parent's bound `ArcanumDelegateTaskTool` instance advertises optional `memory_grant` accepting that value. Existing ordinary instances retain their schema. Selecting it atomically binds the grant to one host-minted child operation ID and a canonical digest of the actual validated child request: executed validated prompt bytes, model selection, explicit file paths/content/attachment identities, token/cost limits and purpose. Use versioned domain-separated framed strict UTF-8 and fixed numeric encodings, and hash immutable executed values. Purpose is this typed task binding, not a promise to police semantic compliance with natural-language instructions.

Missing selector uses ordinary isolated delegation and inherits no memory. Bad selector, already consumed grant or stale proof refuses that child. Parent completion invalidates an unused grant. Expiry or revocation of a never-consumed grant refuses later selection but does not cap ordinary parent work or block a parent response that contains no capsule-derived result; the normal parent authority and sensitivity rules still apply. Once consumed, its protected branch remains fenced through parent continuation and publication. The parent may continue ordinary work after receiving a child result, but cannot renew, transfer or consume the grant again.

Return existing `ApiResponse<PromptResponseDto>`. The wrapper retains its live scope through protected response serialization, revalidates before the first byte, applies `no-store, private` and strips validators. Use existing 400/401/403/409/503 mapping conventions for malformed/authentication/authority/stale-or-consumed-or-quota/unavailable failures. Errors contain no source content. No protected-response idempotency cache or replay path may resurrect grant authority.

## 3. Code-owned policy v1 limits

| Dimension | Bound |
| --- | --- |
| Selected exact versions | 1–16 combined across stores |
| Full proof manifest | At most 128 selected plus dependency coordinates |
| Rendered selected memory | At most 32 KiB strict UTF-8 including framing |
| Proof metadata | At most 64 KiB canonical bytes per grant |
| Maximum lifetime | Five minutes from authenticated preparation, no renewal |
| Active grants per parent | One |
| Active grants per process | 32, including consumed children until drained |
| Successful consumption | Once, for one child |
| Forwarding/subdelegation | None |

Reject the entire grant on overflow, without truncation or partial target selection. The quota bounds retained selected text to 1 MiB and proof metadata to 2 MiB; ordinary parent/child request and response bounds also apply. Expiry is clipped by selected source validity deadlines and parent lifetime. A `TimeProvider` absolute deadline cancels active work; long-running-operation heartbeat renewal cannot extend it. No new configuration knobs.

## 4. Exact source proof and authority

Factor shared read/proof mechanics from `Infrastructure/Data/MemoryEvaluation/MemoryEvaluationSourceReader.*` only as necessary. Preserve the evaluation facade's stateless-operator/no-tools/scope/genuine-lease/epoch checks. A distinct capsule facade validates authenticated parent ownership before invoking shared mechanics; it must not fabricate an evaluation invocation or dummy workload to bypass evaluation authority.

Selected targets must be exact current eligible heads. Prove claim/version/hash format/digest, origin, scope, lifecycle, valid/recorded time, owner binding, sensitivity and provenance. Preserve canonical subject-owner Session separately from nullable version-authoring Session. Reject claimless, retired, erased, superseded, suppressed, invalid-time, unsupported, wrong-scope and label-inconsistent sources. Pinning never overrides eligibility.

Covenant runs the normal linker across lawful scoped candidates before extracting selected targets; omitted masks and Campaign shadows still apply. Dependencies are bounded metadata proof, not extra hydrated/injected memory; historical dependency versions obey retained-edge semantics. Load selected content and labels in coherent admitted SQLite snapshots and verify actual bytes against exact digests. The authenticated issuer may use the existing bounded scoped Covenant snapshot solely to run normal linking; unselected lawful candidates are transient issuer inputs, never retained in the capsule or exposed to the child. This avoids changing ordinary linking semantics. Freeze all mutable collections.

A sealed, nonserializable capability binds root invocation, parent lease registration, child identity at consumption, purpose/task digest, canonical scope, exact source/dependency manifest, installation/dataset/runtime authority/authority epoch/admission/Campaign generations, expiry, sensitivity and generation provenance. Registry provenance and exact live ownership confer authority; a DTO or copied epoch never does. No capability type appears in JSON contexts.

## 5. Linearization, invalidation and drain

Registry states are Prepared, Consumed and terminal Revoked/Completed, with one atomic successful consumption. Wrong-child/forged attempts do not consume the genuine grant. Terminal transitions are idempotent. The genuine borrowed `CovenantTurnLease` stays held through child execution and protected response serialization. Link request abort, parent/child completion, operation cancellation, shutdown, expiry, lease loss, authority and source invalidation to child cancellation. Release slots and read ownership only after in-flight work drains. Mutation drain waits for active capsule provider/effect uses, never for the whole parent request lifetime: the parent may itself invoke a memory mutation. Keep revocation permanently visible in the retained root binding through parent continuation and response publication, including after the child has returned.

Gate validation alone does not prove source versions remain eligible. Add one shared in-process coordination owner used by capsule dispatch/publication and relevant owning-store mutations. Under its asynchronous admission protocol/semaphore, the dispatch side performs final proof validation and registers the physical effect; no await occurs inside a CLR monitor lock; the mutation side closes grant admission before its database transaction, cancels admitted effects and drains them before completing the source change. Never hold a SQLite write transaction across network/provider awaits. Drain before opening the mutation transaction and use a fixed lock order with existing gates; teardown must never require a lock held by its waiter.

For v1, a relevant memory mutation may conservatively revoke every active capsule in this process, bounded at 32. This includes unrelated grants and is a deliberate conservative policy; precise per-source invalidation is deferred. Ordinary noncapsule operations remain unaffected. New preparations are fenced until the mutation's committed or rolled-back state can be revalidated. Source changes that roll back may still leave grants revoked; no grant reopens.

Cover Saga/Lexicon correction, retirement, erasure, current-head and eligibility/suppression/ownership changes, plus Covenant head/lane/mask changes, at shared owning-store mutation boundaries including background paths. Saga's curation writes do not acquire Covenant write leases, so modifying only `ICovenantOperationGate.AcquireWriteAsync` cannot satisfy this requirement. Campaign deletion, reset/restore, key/authority transitions and host-tools taint also use existing revocation/drain/generation machinery. Publication of protected child and parent output, including the entire root HTTP serialization effect, participates in the same fence.

Revalidate at preparation, consumption, before each physical protected send, after durable disclosure immediately before dispatch admission, and before protected publication. A mutation admitted first causes zero subsequent sends/publications; an effect admitted first may already have disclosed remotely, is cancelled/drained and cannot publish after revocation. Retain truthful disclosure/accounting. Never claim remote plaintext was recalled.

Restart abandons every grant; an operation row, backup, selector or look-alike source cannot recreate one. Existing subagent recovery remains `AbandonSafely`. Every host-composed source writer uses the same singleton coordinator; structural composition tests prove that an active-grant path cannot omit or substitute it. Legacy direct test/offline constructors with no delegation runtime may retain optional dependencies, but this cannot create a bypass in host composition.

## 6. Materialization and sensitivity

Add only selected frozen fragments to the child's isolated instruction, task and explicit files. Reuse store-specific CONTEXT/DATA rendering; Proposed/model-writable data cannot become system authority, executable defaults or tool authority. Do not enable fresh Covenant/Saga/Lexicon, Codex, transcript, summary, attachment or workspace retrieval. Preserve selected fragment origin/placement and token-breakdown source attribution; ordinary ungranted children gain no additional prompt bytes. A capsule-only child needs a dedicated read-only disclosure/admission scope because ordinary `CovenantDispatchGate.BeginTurnAsync(None)` is ineligible; this scope exposes no mutation collector and never converts None into ordinary read authority.

At parent pipeline entry, capture the wrapper's explicit root grant binding and pass it into the bound delegate tool. Do not rely on reading a root AsyncLocal after an async-iterator yield. Child entry masks the root binding and carries only its distinct consumed capability. Existing `SubagentExecutionAmbient` can carry that child state without making an arbitrary ordinary child inherit a grant.

Compute conservative maximum sensitivity and merge provenance across capsule sources and other child inputs. Preserve this label through child provider admission, typed `SubagentRunResult`, parent tool result, subsequent parent calls, atomic tool/assistant persistence and final serialization. Merge the label before `AppendToolExchange`, `TryAppendToolInteractionAsync` or event publication; converting the summary to a string first must not discard it. For Session-backed parents, persist tool call/result text and their sensitivity atomically before publishing entries. The retained turn sensitivity must affect later dispatches and interrupted/terminal outputs, not only the current disclosure.

An empty answer, failure, summary, feature toggle or absent current Covenant spans cannot downgrade a tainted branch. Bloom provenance is diagnostic, never source authorization or exact erasure targeting. Add explicit `CovenantDerivedOutputInventory` policies. Logs, progress and operation metadata remain content-free: no capsule text, prompt, capabilities or raw protected provider errors.

## 7. Acceptance and delivery

Observe behavioral RED before GREEN. Use real SQLite source/mutation kernels and deterministic provider/journal/event barriers for authority, exact-head/dependency/owner/shadow/mask proof, one-use concurrency, task/scope/child binding, numeric bounds, expiry, cancellation/drain, invalidation at hydration/disclosure/dispatch/publication, sensitivity propagation and protected serialization. Preserve existing evaluation and ordinary subagent regression suites. No schema migration is required for ephemeral grants.

Update owning `Arcanum.DESIGN.md`, exact `Arcanum.API.md`, and OATH #107/authority guidance. Root README, CLI, Forge and configuration surface are unchanged. Qualify the exact candidate through required build, tests, coverage, source-analysis and shipping/native lanes. Integrate only when every required lane is terminally green and exact SHA/tree/ref equality is proved; close #107 after verified integration.
