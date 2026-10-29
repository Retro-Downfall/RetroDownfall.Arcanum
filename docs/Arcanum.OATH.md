# OATH: Origin-Bound, Authority-Conserving Transactional History

> **Focused architecture companion.** OATH is the formal name for Arcanum's governed durable-memory architecture. Its core law is: **Memory cannot outrank its origin.**
>
> [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md) remains authoritative for shipped architecture, persistence, runtime behavior, and testing. [`Arcanum.API.md`](Arcanum.API.md), [`Arcanum.Command.Reference.md`](Arcanum.Command.Reference.md), and [`Compendium.README.md`](Compendium.README.md) remain authoritative for API, CLI, and configuration contracts. This document explains how those contracts form one memory architecture. It does not create a new API resource named `OATH`, rename existing `Covenant*` types, or supersede an owning document.

**Formal description:** a governed epistemic-claim lifecycle architecture for durable agent memory.

**Design thesis:** a memory system is safe only when every retained claim, derivative, retrieval, provider call, mutation, and disclosure remains bound to its origin, authority, scope, sensitivity, revision history, and evidence of use.

**Scope:** this guide describes the checked-in implementation and its authority boundaries. Feature availability, operator enablement and exact-candidate release qualification are separate facts. Branch history and tracker status are not runtime contracts.

---

## 1. What OATH names

OATH is the architecture that governs how Arcanum creates, stores, derives, retrieves, injects, changes, discloses, backs up, restores, and erases durable memory.

The acronym identifies four load-bearing properties:

| Letter | Property | Concrete meaning in Arcanum |
|---|---|---|
| **O** | **Origin-Bound** | A retained claim or derivative binds immutable source identities, revisions, hashes, scopes, and production receipts. Source deletion changes availability; it does not rewrite history. |
| **A** | **Authority-Conserving** | An ordinary transformation may narrow authority, but cannot promote Proposed data to Confirmed context, broaden Campaign data to Global scope, lower sensitivity, erase lineage, or grant tool rights. |
| **T** | **Transactional** | Local publication uses append-only versions, compare-and-swap heads, idempotency receipts, and atomic assistant-result plus memory commit. Filesystem and external effects use durable journals and receipt-first protocols because they cannot participate in one SQLite transaction. |
| **H** | **History** | Revisions, tombstones, generations, receipts, dependencies, lifecycle events, and erasure evidence remain explicit. Current heads are projections over immutable history, not mutable truth slots. |

The concise research formulation is:

> Every derived memory remains bounded by the authority, scope, sensitivity, and immutable lineage of its sources. Increasing authority requires a new authenticated grant and a new durable receipt.

OATH is a cross-cutting architecture, not one database table and not a replacement name for every memory subsystem. The existing fantasy vocabulary remains intact:

- **The Grimoire** is the encrypted persistence and transaction substrate.
- **The Covenant** is OATH's governed claim and authority substrate (issue #74).
- **The Lexicon** is explicit entity and fact memory.
- **The Saga** is extracted associative memory.
- **The Tapestry** is a rebuildable hierarchy of derived summaries.
- **The Weave** and **Divination** provide embeddings, ranking, and discovery.
- **Session history**, summaries, and Campaign rollups (issue #77) provide episodic and compiled context.
- **The Long Rest** (issue #75) governs consolidation, reinforcement, decay, and supersession.

OATH supplies the rules that those systems must obey when content crosses between them.

## 2. Implementation and contract precedence

### 2.1 Implemented foundations

The live Covenant path includes the compiler/linker, canonical and accelerator tiers, generation-bound operation gate, authenticated management endpoints, agent staging tools, provider disclosure receipts and atomic assistant/mutation publication. Protected backup, restore, retention, reset and full-installation recovery use durable journals and exact owner/effect identities. Declared raw-SQL version chains and resumable backfills carry supported installed tiers forward. Their detailed authority and failure contracts remain in §§4–16 and the owning DESIGN sections.

### 2.2 Memory capabilities and defaults

| Capability | Checked-in implementation and boundary |
|---|---|
| Covenant | Default-off turn participation; Confirmed and Proposed lanes, typed operational defaults, protected inspection/mutation and erasure. |
| Campaign-scoped memory | Default-off retrieval policy; narrows Saga and Lexicon candidates without changing ownership or deleting data. Unknown ownership is never promoted to Global. |
| Campaign rollups | Default-off cross-session summaries with immutable revisions, guarded publication and request-bound protected maintenance. |
| Long Rest | Default-off discovery, consolidation sweeps and evidence-based ranking; exact-version transformations preserve immutable source history. |
| Annals | Default-on automatic Saga/Lexicon claim history with valid and transaction time. Opt-out is prospective; required curation evidence and erasure remain independent of automatic capture. Covenant keeps its own version/receipt model. |
| Memory evaluation | Paired executions bind the same frozen memory plan and report content-free evidence; they grant no production mutation or promotion authority. |
| Agent recall | Default-off, scoped read-only recall under eligible attended native turn authority. |
| Dynamic Context Injection v2 | Default-off stable-prefix policy, restricted to nonsensitive code-owned framing and formatting; protected memory stays cache-ineligible. |
| Delegation capsules | Explicit authenticated memory selection for one child task; no ambient memory, tools or recursive authority inheritance. |

The native source manifest carries assets for `osx-arm64`, `win-x64` and `win-arm64`. Presence and source provenance do not substitute for running the exact candidate's platform qualification. Benchmark comparison likewise requires compatible paired runs on the same host; a historical run is not proof of a later checkout.

### 2.3 Contract precedence

`Arcanum.DESIGN.md` owns architecture, persistence, security and testing. `Arcanum.API.md`, `Arcanum.Command.Reference.md` and `Compendium.README.md` own their exact interfaces. This companion connects those contracts across stores; it does not grant a capability absent from the implementation or make an operator policy optional.

The Annals implements valid-time and transaction-time claims over Saga and Lexicon, with exact-version dependency edges. Covenant v1 has immutable versions, generations and receipts but does not acquire the Annals model merely because both participate in OATH. Feature gates and retrieval eligibility remain separate from whether retained history exists.

## 3. Why an ordinary memory store is insufficient

A vector database can retrieve similar text. A transcript can preserve what was said. A summary can compress old turns. None of those mechanisms answers the harder questions:

- Who authorized this assertion?
- Was it operator-confirmed, agent-proposed, or model-derived?
- Which Global, Campaign, Session, attachment version, source range, and turn produced it?
- Was it valid for this turn's immutable Campaign binding and dataset generation?
- Did a retry use the same context, or observe a later mutation?
- Which exact bytes were disclosed to a provider or external tool?
- Did the output inherit protected sensitivity?
- Can local deletion safely remove the bytes, and what external copies remain nonrevocable?
- Did retrieving or admitting the memory improve the result, or was it merely present?

Without explicit answers, a derived summary can become more authoritative than its source, a Campaign fact can leak into Global context, stale content can survive a reset through a retry, and a vector hit can be mistaken for truth. OATH treats these as protocol errors rather than ranking problems.

### 3.1 Boundaries the memory systems enforce

Each store has a distinct role:

- **Standing agreement is explicit.** Confirmed Covenant entries retain operator-authored preferences; an agent can stage Proposed content but cannot promote it. Typed defaults cover only the closed nonsecurity sampling catalog. Conversation alone does not grant Confirmed authority.
- **Lexicon retrieval is entity-triggered.** `MatchEntitiesAsync` runs against entities the router or `LexiconEntityExtractor` extracted from the prompt. Standing operator preferences belong in Confirmed Covenant context rather than depending on a matching entity mention.
- **Saga retains observations and consolidates their recall separately.** Extraction deduplicates exact retries within the same Session and scope while preserving distinct Session and attachment provenance. Governed Long Rest transformations and sweeps make equivalent exact versions converge to one effective claim without deleting their immutable sources (DESIGN §21.16). Similarity and repetition alone authorize neither a canonical transformation nor usefulness credit.
- **Saga ranking is governed by its opt-in policy.** With Long Rest disabled, retrieval retains raw cosine behavior. With it enabled, the bounded versioned blend uses exact counterfactual evidence, age and assertion origin, with pin and operator exemptions and read-time dormancy. Explicit supersession and validity remain separate eligibility rules; a model similarity score never decides which assertion is true (DESIGN §21.16.6).
- **Retrieval ignores ownership by default.** Ownership is now recorded — every Saga memory carries an explicit scope kind and, where it has one, an owning Campaign, and the Lexicon carries an optional Campaign tier — and `Arcanum:Features:CampaignScopedMemory` makes retrieval honor it on every surface at once. The gate ships off, so an installation that never names it still offers every Saga memory as a candidate for every turn, across Campaigns that Sanctum otherwise isolates. Unowned is not the same as global: memory whose ownership was never resolved is a candidate nowhere until an operator resolves that Session's binding.
- **Session compression and Campaign continuity are separate.** With Campaign rollups off, the legacy compression heading still renders Session summary content. With rollups on, Session and cross-session Campaign summaries have distinct headings, source vectors and current revisions (§20.3).
- **The operator's ability to curate is now partial, and where it stops is the boundary rather than the whole of it.** The Covenant carries exact-version correction, pinning, unpinning, and scope masks over its own prepare-and-commit protocol; Saga and Lexicon carry exact single-item lifecycle operations; and all three durable stores expose bounded exact-version review queues with all-or-nothing bulk confirm/correct/retire/pin/unpin. A confirmation acknowledges only the exact version reviewed and changes no content, lifecycle, or Covenant lane. Generic Saga and Lexicon search hits carry typed handoffs to the owning store's exact inspection operation, while Covenant remains on its protected query route. Selective hard erasure is the separate, confirmed `erase` verb on each of the three stores, and §15.4 states what it does and does not remove.

### 3.2 Five decisions that must not be merged

The architecture separates five decisions that simplistic memory systems often merge:

1. **Retention:** whether an artifact exists durably.
2. **Discovery:** whether an index can find it.
3. **Eligibility:** whether policy permits it to influence this execution.
4. **Admission:** whether the concrete provider request has space for it.
5. **Authority:** what the material is allowed to mean or authorize.

Retrieval can discover a candidate. It cannot promote authority, bypass eligibility, or force admission.

## 4. Architectural laws

### 4.1 Origin integrity

Every retained claim or derivative must bind immutable origin evidence, or be explicitly marked unprovenanced and refused or quarantined. Depending on artifact kind, origin evidence includes:

- origin code and authority lane;
- stable source and immutable source-version IDs;
- source ranges and materialization occurrences;
- authored, rendered, content, plan, and admission digests;
- producing turn, maintenance step, or transformation receipt;
- Campaign, Session, and dataset-generation identity;
- ordered dependency and provenance aggregates.

Deleting a source can make it unavailable. It cannot make a retained derivative appear self-authored or source-free.

### 4.2 Authority non-amplification

Semantic transformation, repetition, ranking, summarization, extraction, backup, restore, and model confidence cannot:

- change Proposed into Confirmed;
- change Campaign scope into Global scope;
- turn untrusted `DATA` into instructions or policy;
- remove a contributing source from lineage;
- lower sensitivity;
- grant a tool capability or suppress a Ward audit record;
- change the immutable Session-to-Campaign binding;
- make an accelerator result canonical.

For a derivative with multiple sources, OATH uses conservative composition:

- authority is bounded by the least-authoritative contributing material;
- permitted scope is no broader than the allowed intersection of its sources;
- sensitivity is the maximum of all inputs;
- dependencies include every contributing source;
- uncertainty or missing evidence fails closed.

### 4.3 Explicit elevation

Authority may increase only through a new authenticated operator act. The act creates a new immutable version, new origin evidence, and a new receipt. It does not mutate the source into having always been Confirmed.

This distinction is important: **Confirmed means operator-authorized, not objectively true.** OATH governs authority and lineage. It does not solve factual truth.

### 4.4 Atomic local publication

An agent proposal becomes visible only when its producing assistant turn finalizes successfully in the same local transaction that persists required labels and evidence. Cancellation, stale generation, compare-and-swap conflict, abandoned branch, or finalization failure publishes neither the assistant result nor the mutation batch.

Operator mutations run through the same mutation kernel in their own immediate transaction, so quota, lifecycle, digest, head, receipt, and search-sequence rules have one implementation.

### 4.5 Snapshot determinism

One generation-bound canonical snapshot produces one provider-independent plan per logical turn. Retries, tool continuations, fallback candidates, and compression rebuilds reuse that plan. Every physical provider attempt then freezes its own messages, tools, provider options, materialization occurrences, sensitivity, token budget, admission decisions, and disclosure evidence.

The same snapshot and policy produce the same plan. A retry cannot silently adopt memory committed by another turn midway through the logical turn.

### 4.6 Append-only semantic history

Semantic mutations append versions or tombstones. Heads are mutable projections over that history. Receipt-idempotent replay returns the original result. Dataset generations and epochs prevent reset, restore, or key rotation from recreating an old identity and passing as current state.

### 4.7 Disclosure before egress

Protected bytes do not reach a provider, external MCP server, process, network destination, message sink, or other content-bearing external effect until the required durable disclosure evidence is acknowledged. Sensitive retirement additionally requires live feature/canonical health, exact target preflight, Campaign scope, and a one-call capability; its Ward pair is informational rather than consent.

The receipt proves what Arcanum authorized and attempted. It does not make an external copy revocable.

### 4.8 Fail closed

Missing, malformed, duplicate, stale, or inconsistent authority, owner, generation, provenance, label, catalog, or effect evidence denies the operation or quarantines the candidate. A derived index can become unavailable without making canonical memory unavailable, but it cannot become an alternative authority source.

### 4.9 Erasure closure

Local erasure traverses every Arcanum-owned protected derivative. A managed file is deleted only after reopening it without following links and verifying the recorded physical identity, length, and full content hash. Changed or unowned artifacts become typed manual blockers. Provider and other external disclosures remain explicitly nonrevocable.

### 4.10 Bounded active context, not silently truncated history

OATH bounds hot-path reads, active sections, staged proposals, exact provenance, and diagnostic tails. It does not silently claim that bounded active context is the complete durable history. Historical versions remain separately pageable and lifecycle-managed.

## 5. The OATH claim model

OATH uses **claim** as the architecture-level term for a durable assertion with identity, authority, lineage, and lifecycle. Covenant v1 realizes that model through scoped entries, independent lanes, immutable versions, and current heads.

| Concept | Meaning |
|---|---|
| **Entry** | Stable identity for one normalized Covenant key within one Global or Campaign scope. |
| **Version** | Immutable `Set` or `Retire` event in one authority lane. |
| **Lane** | `Confirmed` or `Proposed`, each with an independent revision sequence and head. |
| **Head** | Current version pointer and denormalized active projection for one entry and lane. |
| **Origin** | Operator, agent proposal, approved agent retirement, or a typed derivative producer. |
| **Scope** | Global or one exact Campaign for Covenant. There is deliberately no Covenant Session scope. |
| **Session binding** | Immutable `GlobalOnly`, `Campaign`, or legacy-unresolved classification used to determine execution authority. |
| **Generation** | Random dataset identity that makes reset and restore a hard anti-ABA boundary. |
| **Provenance** | Exact source leaves plus an ordered aggregate, or bounded diagnostic generation evidence. |
| **Sensitivity** | Conservative content classification. Covenant-derived content cannot be implicitly downgraded. |
| **Artifact label** | Owner-bound evidence connecting sensitivity to an exact artifact, revision, content digest, and producer. |
| **Snapshot** | Verified current-head facts read from one bounded canonical SQLite snapshot. |
| **Plan** | Provider-independent result of deterministic scope, shadowing, placement, and integrity decisions. |
| **Admission** | Provider-attempt-specific token, pressure, payload, and materialization decision over one plan. |
| **Receipt** | Immutable content-free evidence of mutation, provider attempt, disclosure, finalization, or lifecycle outcome. |
| **Tombstone** | A retained retirement version that remains the head until an explicitly authorized reactivation. |
| **Dependency** | Source or policy identity whose change can make a nonterminal execution or later derivative stale. |

Two distinctions prevent authority laundering:

1. **Sensitivity is not ownership.** `Sensitivity.v1` binds level and bounded generation provenance. `ArtifactLabel.v1` binds that sensitivity to a concrete artifact, owner, revision, content, and producer.
2. **Discovery is not eligibility.** An FTS, vector, Saga, Lexicon, or Tapestry result identifies a candidate. The authoritative scope, lifecycle, label, and turn plan decide whether it can be used.

## 6. Architecture by layer

```mermaid
flowchart TD
    Operator["Authenticated operator"] --> Authority["Authority and operation gate"]
    Turn["Top-level operator-facing turn"] --> Authority

    Authority --> Binding["Canonical Campaign and Session binding"]
    Binding --> Snapshot["Generation-bound canonical snapshot"]
    Snapshot --> Linker["Deterministic compiler and linker protocol"]
    Linker --> Plan["Provider-independent turn plan"]
    Plan --> Admission["Provider-specific admission and frozen call"]
    Admission --> Dispatch["Receipt-first provider and tool dispatch"]

    Dispatch --> Collector["Branch-scoped mutation collector"]
    Collector --> Committer["Atomic assistant, label, receipt, and mutation commit"]
    Committer --> Canonical["Covenant canonical history"]
    Committer --> Derived["Labeled derived artifacts"]

    Canonical --> Outbox["Text-free synchronization outbox"]
    Outbox --> Accelerators["FTS, Weave, Divination, Tapestry discovery"]
    Accelerators -. "candidates only" .-> Plan

    Canonical --> Lifecycle["Retention, backup, restore, reset, and erasure"]
    Derived --> Lifecycle
    Lifecycle --> Recovery["Durable journals and pre-readiness recovery"]
```

### 6.1 Core protocol layer

`RetroDownfall.Arcanum.Core.Covenant` owns portable, provider-neutral rules:

- closed enums and hard limits;
- strict key/content validation;
- checked-in Unicode 17 normalization and safety tables;
- canonical binary and canonical JSON encoding;
- domain-separated digest preimages;
- exact-to-Bloom generation provenance;
- sensitivity and artifact-label construction;
- immutable snapshots, plans, provider-call envelopes, and admissions;
- deterministic linking and pressure-result validation;
- rolling attempt, branch, and disclosure chains.

Core contains no SQLite, EF, ASP.NET, provider SDK, or CLI dependency.

### 6.2 Encrypted canonical persistence layer

Infrastructure owns SQLCipher access, declarative schema installation, connection-local authorization functions, raw parameterized hot-path SQL, mutation transactions, owner cleanup, the canonical-to-accelerator outbox, and FTS synchronization.

One central initializer configures every SQLite connection. Authorization functions start false and become true only through non-serializable, connection-bound scopes. A code path cannot obtain mutation authority merely because it has a database connection.

**Landed (#82).** One process-wide operation gate is the sole admission point. Ordinary work takes a generation-bound lease and keeps it for the whole operation; a destructive operation records its exact recovery owner, closes admission over the affected scopes, drains every live lease, and only then may change anything. That ordering is the law: closing after draining leaves a permanent race in which new readers arrive faster than old ones leave. Installation-wide coverage is a capability rather than a third persisted scope, and protected transfer takes one compound read-and-exclusive lease rather than a read-then-close sequence that would deadlock against its own drain set.

**Landed (#82).** Reads take a caller-owned lease, validate its exact coverage, and never acquire or widen one — a store that could escalate its own coverage would make the drain guarantee unprovable. Writes go through one mutation kernel inside the caller's immediate transaction, which the kernel never opens, commits, or retries; that is what makes a failed batch write nothing. Receipt replay resolves before compare-and-swap, so an exact retry returns its committed answer rather than a revision conflict, and a deliberate no-op is recorded as durably as an applied mutation.

**Landed (#82).** The accelerator is derived and failure-isolated. Canonical commits never depend on it; synchronization applies whole outbox sequences only, because half a canonical commit would leave the applied tuple claiming a sequence whose projection is incomplete. An ineligible, absent, or damaged index yields a successful bounded canonical page with typed rebuild guidance, never an error and never authority.

### 6.3 Runtime authority and admission layer

API orchestration owns:

- pre-binding authentication and no-context policy;
- non-serializable invocation and operator authority values;
- canonical Campaign resolution;
- generation-bound lease acquisition;
- system-prompt attribution and provider-specific token measurement;
- immutable provider option, message, tool, and materialization freezing;
- per-attempt admission and disclosure acknowledgement;
- branch-aware tool loops and mutation collection;
- response finalization and protected-output propagation.

Every intelligence entry point must classify its execution surface explicitly. Subagents, A2A, batch, daemon, recovery, and unattended background execution receive `None`. The explicit selected-memory capability in §20.11 supplies only its bounded listed fragments to one child; it does not upgrade that child's ordinary context or mutation authority.

| Execution surface | Covenant context | Mutation authority |
|---|---|---|
| Session-backed, attended, operator-facing turn | Global plus the immutable canonical Campaign | Single-use staged tools when otherwise eligible |
| Tool continuation, retry, fallback, or compression within that logical turn | Reuses the same turn plan and derives a new physical-attempt admission | Reuses the same branch-aware collector |
| Stateless native turn | Global plus a canonically resolved Campaign when supplied | None, because no durable assistant finalization owns publication |
| OpenAI-compatible `/v1/chat/completions` | None | None |
| Context preview or protected explain | Fresh snapshot, plan, and preview admission | None |
| Explicit no-context execution | None | None |
| Subagent, A2A, batch, daemon, recovery, apprentice, or unattended background inference | None | None |

The OpenAI-compatible completion surface is recorded as `None` rather than as the Global-only reach originally planned for it, because that is what it does. Both of its handlers — buffered and streaming — build their invocation context with `ForStatelessTurn` and no Campaign, and the context provider answers a Campaign-less turn with `Absent(NoCampaign)` before it reads any Covenant state, so neither Global nor Campaign content has ever applied there. The route accordingly no longer declares the Covenant context policy meaningful, and `X-Arcanum-Context-Policy` is refused on it rather than accepted: a caller that sent `none` to a surface that injects nothing would believe it had suppressed something that was never going to happen. Restoring the planned Global-only reach is a deliberate decision to resolve a Campaign context on that surface, not a matter of re-attaching route metadata.

### 6.4 Derived and discovery layer

FTS5, embeddings, Weave, Divination, Saga, Lexicon, and Tapestry may accelerate discovery or produce derived candidates. Their output remains source-linked and sensitivity-bound. They cannot establish Confirmed authority or override the canonical plan.

### 6.5 Operator management layer

Authenticated, typed API services expose inspection, mutation preflight, apply, repair, rebuild, path administration, Session binding resolution, retention, backup, restore, reset, and erasure. Server-backed CLI memory commands call those host services through `ArcanumApiClient`. Local installation lifecycle commands and Compendium configuration editing have their own explicit Core/Infrastructure paths; they do not gain protected management authority from being local.

### 6.6 Lifecycle and recovery layer

Long-running and cross-resource work uses exact operation identities, effect digests, durable journals, monotonic phases, compare-and-swap transitions, generation revalidation, and explicit recovery disposition. Startup keeps affected admission closed until required pre-readiness recovery converges or produces a typed manual blocker.

## 7. End-to-end top-level turn

The OATH turn path is deliberately split into provider-independent planning and physical-attempt admission.

```mermaid
sequenceDiagram
    participant Client as Authenticated client
    participant Gate as Authority gate
    participant Store as Grimoire and Covenant store
    participant Plan as Linker and admission planner
    participant Provider as Provider adapter
    participant Tools as MCP and tools
    participant Commit as Turn committer

    Client->>Gate: Request, API key, context policy, client turn ID
    Gate->>Store: Content-free preflight and durable claim
    Store-->>Gate: Session binding, history watermark, sensitivity revision
    Gate->>Store: Acquire lease and read one bounded snapshot
    Store-->>Plan: History, labels, current Covenant heads
    Plan->>Plan: Verify, link, render, pressure, freeze call
    Plan->>Store: Append disclosure receipt before tainted dispatch
    Store-->>Provider: Acknowledged frozen provider call
    Provider-->>Tools: Optional complete frozen tool calls
    Tools-->>Plan: Results and staged mutation intents
    Plan->>Store: New physical attempt receipt for continuation
    Store-->>Provider: Frozen continuation call
    Provider-->>Commit: Terminal assistant result
    Commit->>Store: One transaction for response, labels, receipts, heads, and intents
    Store-->>Client: Durable terminal result or typed failure
```

### 7.1 Authenticate before content allocation

Covenant management and protected-read endpoints require the master API key before request-body allocation, source-generated decoding, filters, or handler dispatch. Middleware issues a non-serializable authority feature bound to the clean authority epoch and the endpoint's declared requirement. A filter revalidates it for defense in depth.

This ordering prevents an unauthenticated caller from using parser behavior, content length, search rank, or timing to inspect protected state.

### 7.2 Resolve one canonical Campaign context

One resolver combines and verifies:

- immutable Session binding;
- explicit request Campaign;
- registered working-directory Campaign;
- current Campaign availability generation;
- optional path-identity revision and opaque root identity.

Conflicts fail before prompt construction or provider dispatch. A legacy-unresolved Session cannot silently become Global. A supplied path is opened and matched through physical ancestor identities, not trusted as a text prefix.

The resolved context flows through loading, prompt assembly, tool filtering, Ward recording, workspace containment, and finalization. Later stages do not re-resolve scope from a mutable working directory.

### 7.3 Establish the durable turn claim

Eligible native turns enter `CampaignRollupTurnPreparer`: default context policy, a resolved canonical Campaign context, no supplied stateless message history, and either protected-tool persistence, enabled Long Rest, or enabled Campaign rollups in a Campaign. The usual surface is Session-backed; protected-tool persistence also permits a stateless native request with no Session id. This claim path is not universal to every public inference request. An admitted turn uses a client turn ID and two digests:

- a stable request digest for terminal idempotent replay;
- an execution-dependency digest covering current route, provider/model configuration, Prompt or Spell revision, attachments, Campaign/path identity, tool policy, attendance, and options.

The first transaction creates or verifies the Session binding, inserts a `PendingMaintenance` claim, and reserves one future assistant-finalization slot before provider disclosure. A retry with the same request observes or adopts the same claim. A conflicting digest fails. Terminal replay checks the stable request and current authority without requiring obsolete provider dependencies to remain installed.

### 7.4 Read history and labels under authority

Before content-bearing history is read, the history reader must hold one closed authority arm. A disabled, proven-untainted Session uses `SessionTurnHistoryReadAuthority.VerifiedClean` and the ordinary indexed history-plus-label projection without acquiring a Covenant turn lease. Enabled current-Covenant or tainted-history work acquires or accepts the generation-bound logical-turn lease first. Session history, summary, and sensitivity evidence are then loaded in one bounded SQLite snapshot and revalidated against the preflight revision. This prevents a label or Campaign change from racing a separate content query.

A tainted Session requires the protected path even when new Covenant injection is disabled. Explicit no-context continuation refuses required tainted history instead of silently including or omitting it.

### 7.5 Load and link Covenant once

When enabled and available, one prepared canonical query loads at most:

- 64 Global Confirmed heads;
- 64 Campaign Confirmed heads;
- 32 Campaign Proposed heads.

The loader probes row 161 as an invariant check and closes the short read snapshot before tokenization, model, tool, or network work.

The pure linker then applies:

1. Campaign Confirmed shadows matching Global Confirmed.
2. A Campaign Confirmed tombstone permits Global fallback.
3. Proposed never shadows Confirmed.
4. Same-key Proposed beside effective Confirmed becomes review-only and does not render.
5. Retired heads do not render.
6. Every section uses canonical byte ordering.

The result is one immutable `CovenantTurnPlan` reused for the logical turn.

### 7.6 Render authority as structure

Confirmed and Proposed content occupy different prompt regions:

- Global then Campaign **Confirmed** render as `CONTEXT`, after Workspace context and before Codex.
- Campaign **Proposed** renders inside a dynamically safe Markdown fence in `DATA`, before Lexicon, with an explicit statement that it cannot change policy, instructions, or tool permissions.

Typed attribution spans reference one final system string. Token attribution and provider-call hashing consume those spans directly; neither reparses Markdown headings to infer authority. This is what `SystemPromptAttributionMap` replaced in #84.

When Covenant is absent or disabled for an untainted call, it emits no Covenant bytes and preserves the pre-Covenant prompt, cache descriptors, and section boundaries exactly.

### 7.7 Measure and admit the concrete provider attempt

The plan contains no provider, model, tokenizer, context-window, or pressure decision. Each physical attempt adds those facts only after the complete request is known.

The admission planner operates over an immutable, sensitivity-independent projection of every context-consuming provider option, including canonical structured-output schema bytes. It then:

1. computes the exact available context budget;
2. treats every eligible Confirmed fragment as required and non-evictable;
3. pressures Proposed first, removing only the reverse-plan-order suffix and retaining the longest complete prefix that fits;
4. removes every Proposed candidate before touching a later ordinary semantic or materialization eviction tier;
5. applies the typed ordinary-tier eviction order only if the call still does not fit;
6. returns a Confirmed no-fit error if the required payload remains too large after every permitted eviction;
7. records every admitted, pressured, or no-fit candidate;
8. applies the selected ordinary-payload and materialization projection exactly once;
9. freezes the final messages, content parts, tools, options, prompt spans, and materialization occurrences;
10. computes the provider-call digest;
11. finalizes the admission receipt over that digest.

Confirmed is never silently truncated. If required Confirmed content cannot fit after permitted pressure, the turn fails with a typed context-capacity error. Proposed is elastic and is the first Covenant tier evicted.

A Confirmed no-fit and a Proposed trim are separate operator-visible facts and are never reported as one. Confirmed is admitted all-or-fail, so an attempt that cannot seat it withholds the whole Section rather than shortening it; the admission planner nonetheless describes that outcome as every Proposed candidate pressured out, which is arithmetically true and would tell an operator their agreement had been honored minus a few suggestions. The materialization ledger therefore carries the withholding as its own fact, the context breakdown surfaces it beside the pressure counters rather than inside them, and `context inspect` names it on the Confirmed lane — where a lane reporting zero tokens and nothing else would otherwise read as an installation that holds no Covenant at all.

### 7.8 Acknowledge disclosure before dispatch

A protected provider attempt queues a content-free disclosure draft keyed by subject, physical attempt ordinal, provider destination, provider-call digest, admission, sensitivity, and generation evidence. A dedicated committer persists the receipt and updates the subject's rolling disclosure chain under `synchronous=FULL`. Network dispatch begins only after acknowledgement.

Unprotected and enabled-clean calls perform no disclosure work. Every attempt still uses one frozen call and the applicable admission lineage.

### 7.9 Execute tools through explicit capabilities

A Covenant-bearing turn advertises only tools allowed by its invocation context. Covenant content itself grants no tool authority.

The model-facing Covenant tools receive single-use, request-bound capabilities. Their schemas omit Campaign, Session, origin, lane-authority, receipt, and other platform-owned fields. The server adds those facts from the live capability.

A Covenant tool call is classified only over bytes whose identity has already stopped changing. The in-process MCP server frames its own JSON-RPC requests, so a call reaching either mutation handler carries a complete name and a complete argument body by construction, and classification runs over those complete bytes to produce the risk identity and evidence digests used by disclosure plus any historical Ward receipt. No live retirement creates a Ward receipt. That property is supplied by the framing of the one transport these tools are reachable on, not by a barrier of Arcanum's own that withholds partial calls; no provider-side streaming path reaches classification today, and one added later would have to establish completeness for itself. What the model gets back names no key and no content, because every field it carries is an identifier, a coordinate, or a size — an opaque target id, a mutation id, a scope, a lane, an operation, the expected lane revision, and an optional rendered hash and compiled byte cost — and its status says `staged`, never `published`.

### 7.10 Finalize exactly once

The `IGrimoireTurnCommitter` owns one immediate transaction that:

- revalidates the claim, Campaign, dataset generation, plan, and admission lineage;
- inserts or resolves the assistant-finalization guard;
- handles mutation-ID replay before fresh authorization validation;
- applies lane compare-and-swap and quotas;
- appends immutable versions and provenance;
- advances heads and search sequence;
- persists the assistant result, including a valid empty result;
- persists required labels, final receipt, and compact redacted tool receipts;
- commits once.

A failure rolls back the response and every staged mutation. Streaming emits a terminal error rather than a false completion.

## 8. Mutation implementation

### 8.1 Mutation-time compilation

`ICovenantCompiler` transforms authored content before the mutation commits. The live turn never recompiles canonical content.

Policy v1:

- accepts keys matching `[a-z0-9][a-z0-9._-]{0,127}`;
- caps authored content at 2,048 strict UTF-8 bytes;
- rejects empty content, NUL, unpaired surrogates, unsafe controls, and every Unicode `Format` code point;
- preserves exact validated authored bytes;
- normalizes the compiled representation with pinned Unicode 17 NFC;
- canonicalizes policy-defined whitespace;
- escapes backslash and double quote;
- renders one exact `- key: "value"\n` fragment;
- computes the safe Proposed fence length;
- stores authored and rendered SHA-256 identities, byte cost, and policy versions.

Runtime compilation does not depend on host ICU, NLS, culture, or the .NET runtime's current Unicode tables. Checked-in generated tables and a complete corpus make the result stable across supported operating systems and Native AOT builds.

### 8.2 Two independent authority lanes

Confirmed and Proposed maintain independent revision sequences and heads. Agent proposal churn cannot create false conflicts for an operator updating Confirmed content.

- Operator set appends Confirmed content.
- Agent propose appends Campaign Proposed content.
- Retirement appends a tombstone in the selected lane.
- Operator reactivation appends a new Confirmed version after a Confirmed tombstone.
- An agent cannot reactivate a retired Proposed lane in v1.

Retirement does not resurrect an older version. The tombstone remains current until an explicit new version is authorized.

### 8.3 Prepare and apply

Operator set, retire, path, binding, and family-repair mutations use receipt-first prepare/apply protocols:

1. Prepare authenticates, normalizes input, computes current effects, binds revisions and epochs, and returns a stable apply-request digest plus a short-lived purpose-bound envelope.
2. Apply first checks durable operation or mutation receipts by operation ID and request digest.
3. An exact terminal receipt replays even after token expiry or key rotation.
4. A different request digest returns an idempotency conflict.
5. Only genuinely new work decrypts and validates the current envelope before admitting the first side effect.

This ordering prevents an expired token from blocking replay while also preventing replay from becoming new authority.

### 8.4 Branch-scoped agent staging

Internal MCP handlers do not mutate canonical rows. They submit typed intents to a collector with an `Open -> Sealing -> Sealed` lifecycle and an irreversible `Discarded` terminal state.

Intents bind:

- turn, branch, and tool-call identity;
- dataset generation, base-plan, and producing admission;
- canonical Campaign and target lane;
- expected lane revision;
- request, authorization, and mutation digests;
- compiled proposal artifact;
- exact call-scoped attachment materialization provenance;
- historical Ward evidence when decoding a retained legacy receipt; current live retirement carries none.

Tool replay is checked before target uniqueness. Exact replay returns the original staged receipt; changed input under the same identity fails. Branch replacement carries only shared-prefix intents onto the new branch and discards abandoned-branch intents. At most four live staged intents can reach publication.

### 8.5 Quotas preserve retirement capacity

OATH uses hard code-owned bounds for active prompt cost, historical storage, idempotency, and abuse resistance. Important Covenant v1 limits include:

| Resource | Limit |
|---|---:|
| Authored content per version | 2,048 UTF-8 bytes |
| Global Confirmed active section | 4,096 rendered bytes and 64 entries |
| Campaign Confirmed active section | 4,096 rendered bytes and 64 entries |
| Campaign Proposed active section | 4,096 rendered bytes and 32 entries |
| Staged mutations per top-level turn | 4 |
| Stable entries per Global or Campaign scope | 256 |
| Immutable versions per Global or Campaign scope | 8,192 |
| Versions per entry and lane | 1,024 |
| Exact generation identities | 8 before bounded Bloom overflow |
| Attachment sources per agent mutation | 64 |
| Canonical snapshot candidates | 160, with row 161 as an invariant probe |

Version and receipt ceilings reserve capacity for head-changing retirement. A full ordinary quota cannot make active content impossible to retire.

## 9. Persistence implementation

### 9.1 Three transaction tiers

Schema family and transaction tier are independent dimensions:

| Tier | Failure behavior | Contains |
|---|---|---|
| **Core** | Startup-blocking and atomic | Session Campaign bindings, Campaign registry and authority state, finalization guards, sensitivity labels, turn claims, capacity, deletion journals, disclosure state, managed-file evidence, and feature metadata. |
| **Covenant canonical** | Failure-isolated; Covenant canonical paths become unavailable while ordinary Arcanum remains operable | Entries, state/generation, versions, heads, provenance, mutation and turn receipts, aggregates, key epochs, search outbox, rebuild state, and canonical recovery metadata. |
| **Covenant accelerator** | Search degrades while canonical prompt authority remains available | FTS5 virtual table, shadow tables, and accelerator projection state. |

Each tier installs in its own transaction from a closed, ordered declarative catalog. A metadata row records schema version, source-definition fingerprint, installed-catalog fingerprint, and health, and its version means the tier is completely at that version and was validated there. Unknown objects, missing objects, altered DDL, unexpected indexes, or a newer version fail that tier closed. FTS-generated shadow tables are part of the closed manifest.

Schema resources remain one object per SQL file, and the file's path picks its install transaction: directly under a category folder is the startup-blocking core tier, while `Capabilities/Covenant/{Canonical,Accelerator}/<Category>/` is a capability tier that fails on its own. Code-owned data initializers run inside their owning install transaction after DDL and before fingerprint capture.

### 9.2 Declared version steps

An installed tier is carried forward only through a version step this build declares. Each tier has an ordered, closed chain of integer versions; a step ships its statements as one file each under the tier's own `Transitions/V<n>/` folder, pins the source fingerprint of the version it leaves, and may depend on one resumable data sweep. An undeclared schema change is still fresh-install only, and a database that disagrees with this build in any other way is repaired deliberately rather than upgraded in place.

A sweep is bounded, checkpointed, idempotent, and restart-safe, and its cursor is written inside the same transaction as the work it describes, so no cursor can ever describe work that did not commit. A tier's version is recorded only once its whole run finishes and its catalog validates against the closed manifest, so an interrupted run leaves the capability unavailable rather than claiming a version whose promises were not kept. Every condition a run cannot honor — a newer installed version, a definition disagreement, unknown objects, a catalog and its metadata disagreeing about version, and an interrupted run this build cannot finish — resolves to a typed, content-free, fail-closed health rather than a guess.

Which version each tier is at, which steps it has taken, and which of those steps carry a sweep are the chain's own statement and the version constant beside it; no count of them is kept here. A tier that has not moved still runs the loader, the classifier, and the driver in production, and they find nothing to do for it.

Issue #102 generalizes this into reusable, versioned, resumable feature-schema evolution with checkpointed backfills — the prerequisite that lets #75 through #78 evolve existing stores without introducing an EF migration.

### 9.3 Canonical records

The principal canonical structures are:

- `covenant_entries`: stable scoped key identities;
- `covenant_versions`: immutable authored or tombstone events;
- `covenant_heads`: mutable current projections per entry and lane;
- `covenant_version_attachment_provenance`: immutable exact source leaves;
- `covenant_state`: dataset generation, canonical sequence, accelerator epochs, key versions, and rebuild state;
- `covenant_mutation_receipts`: content-free idempotency outcomes;
- `covenant_turn_receipts` and aggregate: compact committed-use evidence;
- `covenant_search_outbox`: text-free canonical-to-FTS synchronization events;
- `covenant_search_documents`: the accelerator projection that carries authored and compiled text;
- `covenant_key_epochs`: bounded per-key dependency epochs and anti-ABA support.

Canonical history does not depend on FTS health. The outbox can collapse to `FullRebuildRequired` instead of allowing accelerator failure to become an unbounded canonical write tax.

### 9.4 Core support records

Core tables hold invariants that must survive optional Covenant damage, including:

- immutable Session Campaign bindings and one-time resolution receipts;
- Campaign registry, path identity, and authority epochs;
- assistant finalization guards;
- public turn claims and bounded maintenance checkpoints;
- artifact sensitivity labels and session sensitivity state;
- owner deletion and cleanup journals;
- disclosure subjects, receipts, and folded lower-bound state;
- managed-file write intents and local-erasure work items;
- operation-specific restore, reset, transfer, and marker intents;
- `long_running_operation_request_identities`.

Cross-tier core owner IDs are historical identities rather than fragile optional foreign keys. Canonical reads prove the current owner exists, and core deletion emits durable cleanup work. This keeps Campaign or Session deletion available when optional Covenant state is degraded.

### 9.5 Hermetic SQLite and SQLCipher

The database runtime contract pins **SQLCipher 4.17.0** on **SQLite 3.53.3** with statically linked **OpenSSL 3.5.7**. Native assets are built from pinned sources, hash-verified, SBOM-described, and delivered by RID with no system-library or extension fallback. `SQLITE_OMIT_LOAD_EXTENSION` is a compile option, not a runtime setting.

The shipping matrix is `osx-arm64`, `win-x64`, and `win-arm64`, and all three assets are checked in and **verified**: each was built twice from the pinned sources on a clean runner and the two libraries compared byte for byte. A RID whose asset were ever removed or left pending would still fail the build rather than fall back, which is the property that made the pending state safe to hold.

> Issue #92's acceptance criteria named a five-RID matrix (`osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`) that predated the hermetic matrix `native-source-manifest.json` declares. Its text has been amended to the manifest's three RIDs, naming the manifest as the authority: the three it dropped have no hermetic toolchain and are not shipping RIDs, and `win-arm64`, which it had omitted, is one.

`SqliteNativeRuntime.Initialize()` freezes provider selection before SQLite use. `ICovenantSqliteConnectionInitializer` applies SQLCipher, foreign-key, busy, secure-delete, and closed authorization-function policy to every EF, raw, backup, restore, reset, worker, fixture, and benchmark connection.

## 10. Canonical identity and evidence

OATH does not bind authority with ad hoc JSON, culture-sensitive strings, or delimiter concatenation. `CovenantCanonicalEncoder` version 1 uses:

- ASCII domain tags terminated by NUL;
- fixed-width big-endian integers;
- RFC 4122 network-order GUID bytes;
- strict UTF-8 with explicit byte lengths;
- one-byte optional presence;
- explicit collection counts;
- raw fixed 32-byte digests and Bloom values;
- canonical finite IEEE-754 binary64 values;
- RFC 8785 canonical JSON where JSON is required.

The protocol defines separate domains for authored content, fragments, sections, requests, authorization, mutations, snapshots, plans, materialization, sensitivity, artifact labels, Session turns, provider options and calls, admissions, Wards, effects, disclosures, receipts, and cursors.

Those digests are installation evidence and deterministic identity, not a blockchain or publicly verifiable truth ledger. They are meaningful only with the surrounding authentication, persistence, and key boundaries.

Rolling attempt, branch, and disclosure chains keep durable evidence O(1) without imposing an arbitrary turn-step ceiling. Counters are checked `u64` values; overflow is an integrity exhaustion, not a configured model-loop stop.

## 11. Authority, concurrency, and recovery

### 11.1 Non-serializable authority

Authority values and leases are process-local capabilities. They cannot be supplied in API JSON, MCP arguments, durable checkpoints, or model output. Durable storage records only the exact owner, effect, epoch, phase, and evidence needed for an authorized recovery service to reacquire authority.

`OperatorAuthorityContextIssuer` is the one place operator authority is minted. Models never receive it.

### 11.2 Generation-bound leases

The operation gate distinguishes ordinary read, write, turn, MCP, accelerator, and cleanup leases from Campaign-exclusive, protected-transfer, installation-read, and Global-exclusive operations.

Every lease binds scope plus the relevant authority, availability, dataset, Campaign, path, and key generations. Revalidation fails old work after reset, restore, Campaign deletion, path remap, key rotation, or host-tools taint.

`CovenantRuntimeGenerationProvider` owns keys, authority, and availability as one immutable process generation. Its public interfaces are projections, not separate publishers. A successful committed reset publishes fresh keys for all six purposes with verified authority/capability state, so every old opaque token fails; recovery-bound purposes may exist alone only during partial no-dataset bootstrap.

An exclusive operation owner is the exact tuple:

```text
(OperationId, CovenantExclusiveOperation, EffectDigest)
```

It cannot be reconstructed from operation kind alone.

### 11.3 Close, drain, mutate, publish, reopen

Destructive work closes admission, drains conflicting leases, persists the exact owner/effect before side effects, executes bounded journaled phases, proves outcomes, and takes exactly one disposition. Covenant erasure additionally quiesces the same singleton disclosure writer; any pre-effect rollback first restores the old writer. After immutable proof, `ReopenedVerified`, composite publication, fresh-writer restart, disposition, and durable failure recording use separate bounded coordinator-owned tokens rather than caller cancellation. There is no total storage/recovery timeout.

Publication consumes the exact immutable candidate under the still-live exclusive registration and swaps keys, authority, and availability together. Completion is one-shot; a failed disposition preserves durable owner evidence for pre-readiness adoption and never attempts a fallback.

### 11.4 Crash-safe cross-resource work

SQLite cannot atomically commit a database row, a provider request, a filesystem rename, and an OS credential update. OATH therefore uses the strongest protocol appropriate to each effect:

- database changes use immediate transactions and compare-and-swap;
- provider and external effects use disclosure-before-egress receipts and physical attempt ordinals;
- file creation uses durable parent/leaf identity evidence, write intents, flush, no-replace rename, reopen verification, label adoption, and parent fsync;
- Campaign markers use retained root capabilities and monotonic marker intents;
- backup/restore and full reset use authenticated, anti-rollback journals under a caller-held installation lock;
- terminal response replay uses immutable claims and finalization guards.

This is transactional history, not a claim of distributed ACID rollback.

### 11.5 Restore-journal credentials

Three profile-namespaced OS credentials authenticate restore recovery evidence and are read before any database opens: `backup-restore-journal-installation-{PROFILE_NAMESPACE}`, `backup-restore-journal-key-{PROFILE_NAMESPACE}`, and `backup-restore-journal-anchor-{PROFILE_NAMESPACE}`. `ProfileNamespaceDigest` is derived from the profile root's retained no-follow parent handle and carries no path text. Ordinary credential cleanup, Covenant reset, family reinitialize, and restore retain all three byte-for-byte; only an attested full installation reset may remove them, apart from `arcanum data repair-installation-identity`, which changes the installation account alone when its value is not a canonical identity: it seeds the value again from the Grimoire's authority row when the Grimoire holds one, otherwise writes back the identity a closed restore or offline-transition anchor names, and removes the value only when neither exists.

A fourth, unrelated installation secret — `campaign-root-identity-key` — keys the opaque identity Arcanum derives for a Campaign's physical workspace directory. Losing it leaves every Campaign path identity unresolved until authenticated repair rather than silently orphaning registered roots.

Two further profile-namespaced credentials — `grimoire-transition-journal-key-{PROFILE_NAMESPACE}` and `grimoire-transition-journal-anchor-{PROFILE_NAMESPACE}` — authenticate the offline-transition journal, and they are the only evidence that could ever finish an interrupted database transformation. They therefore leave only in an attested full installation reset's final credential cleanup, after the trio and after a proof taken under the held installation maintenance lock: the canonical journal file and its three siblings durably absent, and the anchor either `Closed` for this installation with both accounts present or every one of anchor, key, and file absent together. A key beside an absent anchor is the residue of a genesis that began and proves nothing. An outer record still holding a `Claimed` nested receipt blocks the step, because a reset that never saw its transition complete is exactly the reset that may still need the pair. `InstallationResetCredentialCatalog.CollectOrdinaryAccounts` excludes both accounts, so no ordinary cleanup, Covenant reset, family reinitialize, or unattested installation reset can name them — and a healthy-catalog factory erasure removes none of the restore trio whether it runs standalone or as a full reset's nested arm.

These retained credentials are recovery evidence and identity secrets, not any of the six opaque envelope-token families. A committed Covenant reset invalidates all six old token families.

## 12. Sensitivity and protected derivatives

### 12.1 Conservative propagation

Every provider call computes sensitivity as the maximum of its Covenant spans, input messages, summaries, tool results, and retained labels. Any nonzero result is `CovenantDerived` and carries bounded generation provenance.

Up to eight distinct generation IDs remain exact. Adding a ninth transitions permanently to a fixed 256-bit Bloom representation. Merge is associative, commutative, idempotent, and constant-space. The Bloom is diagnostic only; false positives are acceptable and it never authorizes a read or selects an erasure target.

`ArtifactSensitivityLedger` is the one writer of `artifact_sensitivity` and `session_sensitivity_state`, always inside the caller's own transaction, append-only, and refusing every downgrade. `DerivedArtifactWrite` makes sensitivity a required argument, so a new sink cannot be untainted by omission.

No model classifier, substring test, empty current plan, feature disable, or later summary can downgrade a tainted branch.

### 12.2 Closed sink policy inventory

Every assistant or summary consumer must select one explicit policy:

- propagate the label atomically;
- perform the informational Ward audit and required disclosure handling;
- emit only content-free metadata;
- reject Covenant-derived input;
- purge under an authorized lifecycle operation.

The inventory covers assistant entries, turn evidence, summaries, titles, tools, Saga, Lexicon, embeddings, search projections, audit/history projections, notifications, managed workspace files, idempotency claims, attachments, A2A state, daemon history, operational logs, and live streams.

`CovenantDerivedOutputInventory` and its architecture suite fail when a new sink or reader lacks a declared policy. Source-by-source log sanitization is not enough; the final log ring, query, streaming, and progress stores also accept only closed metadata projections — that is what `CovenantProtectedLogScope` enforces as a type.

### 12.3 Protected read partitions

Generic search, vector, FTS, archive, and background projection paths do not admit protected artifacts. Where protected retrieval is required, a physically separate projection opens only under a clean read lease.

Filtering a mixed result after ranking is forbidden. Rank displacement, corpus statistics, and timing can reveal protected membership even when result text is removed.

Tainted reads load artifact and label in the same bounded SQLite snapshot and retain the lease through serialization or stream completion — `CovenantProtectedJsonResult<T>` and `CovenantProtectedStreamResult` revalidate the lease before the first byte, strip validators, mark the response `no-store, private`, and release the lease only after serialization.

### 12.4 Provider cache boundary

OATH suppresses Arcanum-authored explicit provider-cache directives on Covenant-bearing calls. Local cache descriptors remain useful for accounting, but protected segments are explicitly cache-ineligible. Dynamic Context Injection v2 admits only stable, nonsensitive code-owned framing and formatting. Neither Confirmed authority nor repeat use makes protected memory eligible for that prefix.

### 12.5 Sensitive egress

`ToolRiskClassifier` classifies a Covenant-derived content-bearing external or persistent effect as `CovenantSensitiveEgress`. Final complete arguments are frozen before exact preflight and capability binding. `CovenantEgressWardPolicy` retains its historical name but current retirement resolves to `UngatedRetirement` for both attended and unattended eligible turns; removed Ward settings cannot authorize or deny it. `CovenantToolEgressGuard` commits a disclosure receipt before every physical attempt, counting retries and reconnects separately.

Sensitive network redirects require a new destination-bound policy decision for every hop. Cross-origin redirects strip origin-bound credentials before the destination is re-evaluated. DNS and connection policy revalidate the allowed origin and address class at connection time.

An exclusively created and verified managed file may be locally revocable. Append, replacement, editing a preexisting file, or later operator modification is nonrevocable. OATH does not pretend it can rewind an unjournaled edit.

### 12.6 Host-process tools and Covenant cannot coexist

Unsandboxed host-process tools (`execute_command`, `run_spell_script`) and Covenant authority are mutually exclusive. `HostProcessToolsTransitionService`, the pure `HostProcessToolsMarkerPairJoiner`, and `HostProcessToolsStartupGate` classify the installation before any pool, key, or Covenant service exists. A host started with the escape-hatch environment but without a completed, marker-matched taint transition exits with `Covenant.HostToolsTransitionRequired`. An installation that has completed the transition can never open Covenant again on any later start. `PendingHostToolsTaint` counts as tainted everywhere: "cannot prove clean" is the only reading a fail-closed decision may take.

## 13. How the existing memory systems participate

OATH does not merge Arcanum's memory systems. It assigns each one a role and information-flow contract.

| System | OATH role | Authority rule |
|---|---|---|
| **Covenant** | Governed durable claims and operator/agent profile | Confirmed and Proposed remain independent. Covenant supplies the canonical compiler, linker, receipts, labels, and publication barrier. |
| **Lexicon** | Explicit agent-directed entity and fact memory | Lexicon content is untrusted `DATA`, retains exact source provenance, and cannot become Confirmed through repetition or extraction. |
| **Saga** | Automatically extracted associative conclusions | Saga is derived, source-linked, sensitivity-propagating memory. Retrieval does not prove truth or authority. |
| **Tapestry** | Hierarchical summaries over existing corpora | Generations are immutable and atomically published, leaves retain source linkage, and the tree is rebuildable discovery data rather than a source of truth. |
| **Weave and Divination** | Embedding and ranking substrate | Similarity discovers candidates only. Canonical eligibility and OATH authority are evaluated after discovery. |
| **Session history and summary** | Episodic record and bounded compression | A summary is a derived artifact. It inherits sensitivity and binds the exact history revision and maintenance receipt. |
| **Campaign rollup** (#77) | Revisioned, compiled Campaign context | A Session binds one rollup revision so a retry cannot observe a mid-turn update. |
| **Long Rest** (#75) | Consolidation and adaptation | Transformations require receipts and evidence. Retrieval count alone is not reinforcement. |

### 13.1 Maintenance inference

Summary, title, Saga, and Lexicon maintenance cannot run as ambient background inference over tainted history. An authenticated top-level request may derive one single-use maintenance authority bound to:

- one Session and pending turn claim;
- the pre-request history watermark;
- one same-snapshot sensitivity revision;
- one clean read lease;
- tools disabled at the adapter boundary.

Each physical maintenance dispatch receives its own disclosure receipt. The parsed output commits with its sensitivity label and checkpoint. A crash can reuse only a committed deterministic checkpoint; an uncertain provider call gets a new physical attempt ordinal.

Background daemons cannot borrow Covenant read authority. Consolidation over tainted material runs under the next authenticated request's single-use maintenance authority.

### 13.2 Admitted is not useful

OATH distinguishes **admitted** from **useful**. A memory being present in a prompt does not prove it helped. Long Rest (#95) and the evaluation lab (#106) consume compact committed turn receipts, transformation receipts, outcome evidence, and counterfactual comparisons before reinforcing, decaying, or superseding claims.

This prevents the feedback loop in which frequent retrieval is mistaken for correctness and then causes still more retrieval. It is why #95's first acceptance criterion is blunt: *retrieval and admission counts alone produce no positive usefulness credit.*

## 14. Operator and agent surfaces

The registered Covenant inspection and mutation routes are typed, authenticated, body-based and protected by no-store response handling. `ApiBootstrapper` maps both endpoint families; `arcanum memory covenant` exposes their operator workflow. Reset planning/apply uses `/api/data`, and backup restore has explicit protected-state and Campaign-mapping modes. A service port or recovery operation is not by itself a public route; the exact registered surface is owned by the API and Command Reference.

### 14.1 Inspection

- status and capability health;
- current-head list with explicit scope selection;
- bounded free-text query;
- exact entry detail;
- separately paginated immutable versions;
- exact attachment provenance for one version;
- provider-specific explain using a fresh snapshot, plan, and preview admission.

Search text and protected keys remain in request bodies rather than URLs and access logs. Opaque authenticated cursors bind endpoint, filters, generation, sequences, accelerator epoch, and keyset position. A changed source returns a stale-cursor error instead of mixing pages.

Generic `MemorySearchScope.All` and `/api/memory/search` continue to exclude Covenant. Covenant search uses the protected typed query route and its FTS5/fallback generation contract.

### 14.2 Mutation and administration

Registered memory mutations include operator set/retire/correct prepare-and-apply and exact curation prepare-and-apply. Review and erasure have their own typed flows. Retention, backup, restore and reset retain their owning lifecycle surfaces.

Campaign path repair, legacy Session-binding resolution, schema repair, optional-family reinitialize and FTS rebuild also have internal service/recovery contracts. Do not infer a public command or endpoint from those contracts: consult the registered route and CLI inventories before invoking one.

### 14.3 Agent tools

The two hand-authored, source-generated MCP tools are:

- `propose_covenant`, for Campaign Proposed content;
- `retire_covenant`, for exact Campaign-bound retirement under its own capability policy.

Both handlers are registered on every host. `propose_covenant` is advertised on eligible session turns wherever the feature and canonical tier are healthy; `retire_covenant` is advertised on the same feature/canonical basis when its retained capability can be prepared. Both stay registered whatever their advertisement, so a stale or direct invocation fails closed rather than reaching an unregistered name.

`retire_covenant` can be granted. The pipeline emits the ordinary informational `ungated` Ward pair, resolves the exact target from canonical state, binds the Campaign-scoped one-call capability, and commits tool-effect disclosure before dispatch. Missing session/read authority, feature/canonical health, Campaign binding, tool policy, capability, or exact preflight still refuses the effect. Attendance and removed Ward approval settings decide none of those checks.

`propose_covenant` is granted, staged, and published. A turn that reaches its completed assistant finalization seals the collector and hands the resulting batch to `IGrimoireTurnCommitter` beside the answer, so the compiled fragment and the reply it accompanied enter canonical storage in one transaction or neither does. The batch binds the dataset generation and the key-reclamation epoch the turn's own snapshot read, and binds no Campaign registry epoch: an agent proposal reaches exactly one Campaign, and a stand-in value there would be compared like a real one and refuse every proposal on any installation whose registry had ever advanced.

`propose_covenant` receives the same informational Ward pair as every server tool, and that is audit rather than consent. The Proposed lane is review-only beside effective Confirmed content and cannot change it, which is the whole reason it is the lane an agent may write; the write is local canonical storage rather than egress; and the provider dispatch that produced the tool call already committed its own disclosure receipt before any bytes left the process. Retirement is narrower because its exact preflight, one-call capability, and disclosure-before-effect sequence authorize the effect without adding a user prompt.

A turn that ends any other way publishes nothing. Interruption, cancellation, a refused finalization and a guardrails failure discard the collector. A completed untainted turn can publish a nonempty staged batch through the same atomic committer: first proposals do not require previous Covenant content or a fabricated derived label. A turn holding a staged batch that cannot reach the atomic committer refuses rather than persisting its answer alone, and the stream-exit cleanup that runs after such a refusal drops the partial reply instead of committing it a moment later without its batch — because an answer written without the batch silently loses an acknowledgement the tool has already reported to the model.

That makes the `Proposed` lane reachable on a real installation for the first time. The Proposed-lane admission pressure arithmetic, the Campaign-Proposed section-capacity arithmetic, and the closed Proposed admission decision shape now meet real candidates rather than only test ones.

Neither can be reached through `mcp invoke`; `arcanum-internal` is not a diagnostic MCP target and both reserved names remain on the blocklist because that endpoint cannot mint their Master-pipeline context or capabilities.

Scoped agent recall is a third, read-only tool: scoped agent recall across durable memory, granting no mutation, promotion, or broader search authority, and absent for disabled, unattended, ambient background, and unauthorized subagent invocations.

## 15. Backup, restore, reset, and erasure

### 15.1 Backup

A physical backup that includes protected state is itself a protected read and encrypted external disclosure. `CovenantBackupDisclosureBoundary` commits a durable receipt **before the snapshot reads page one** and again **before the archive writes its first byte**, each retry counted as its own physical attempt, under one retained `CovenantInstallationReadLease` with no nested scoped lease. Full backups include canonical Covenant state, sensitivity labels, tainted artifacts, disclosure evidence, and required tier metadata.

Plaintext export is the other kind of egress, and both halves are now closed. `GET /api/sessions/{id}/export` refuses the **entire** Session with `Covenant.PlaintextExportRefused` when any tainted Entry, tool artifact, summary, title, Saga, Lexicon, attachment-derived artifact, or projection exists, and it refuses **before the export graph is read**, so a refused transcript is never assembled. It refuses on either the Session's own `artifact_sensitivity` rows or its conservative `session_sensitivity_state` projection: purged taint still bars a plaintext export for the same reason it still bars a cached replay. There is no approval that overrides the refusal, because a plaintext file is nonrevocable the moment it exists. `POST /api/campaigns/{id}/export` carries no Covenant content, version, receipt, hash, provenance, or tainted artifact, and reports typed content-free `covenantEntryCount` and `taintedArtifactCount` exclusions rather than omitting them silently. When required, both routes hold one conditional Covenant read lease from before the export graph through the last response byte. Feature-off Session export still calls the no-lease inspector and refuses persisted taint; absence of a lease is not absence of protection. The import half is closed the same way: `IProtectedArtifactTransferStore` refuses any Covenant-derived source outright and requires an explicit destination Campaign mapping, spelled `--map-campaign <source-campaign-id>=<destination-campaign-id>` on `arcanum backup restore` and validated against this installation's own Campaigns before anything is staged.

### 15.2 Restore

Restore never reinstates protected state by default. `BackupRestoreProtectedStatePolicy` is the sole, pure decider, split into the arm a rehearsal may report — is this mode applicable, and is the gate on — and the arm it may not, the operator's separate `ProtectedStateConfirmed` field. That field sits beside `Confirmed` rather than widening it, because replacing an installation and reinstating or destroying its protected memory are two destructive answers; neither reaches the effect digest, so a rehearsal and the real run produce the same owner.

`BackupRestoreProtectedStateInspector` reads the extracted snapshot read-only and counts three content-free numbers plus one taint bit: canonical rows, the accelerator projection that carries the authored and compiled text, and sensitivity labels. The schema-seeded `covenant_state` singleton is deliberately excluded so a tier that is merely installed does not read as protected state. This happens **before** the staged tree is composed and before `AcquireExclusiveAsync` is ever called, so a refusal has closed no admission and leaves no journal, anchor, or staging root behind.

A source-tainted archive carrying protected state fails closed under both `Reject` and `RestoreProtectedState`. The only supported continuation is a separately confirmed `PurgeProtectedState`: the purger runs inside the same staged transaction **after** both destination-monotonic joins, empties the whole Covenant family child-first including the accelerator projection and its FTS index, removes each labelled artifact through the same `CovenantArtifactPurgePlans` table the live erasure kernel resolves through, folds every touched Session's projection to zero tainted artifacts without lowering its maximum, and makes no filesystem call at all — a managed-file label names a file on another machine.

Before either destructive prompt the owning command writes `CovenantExternalRetentionDisclosure.DestructiveOperationText` byte-for-byte, then the receipt-backed possible-attempt count with exact or lower-bound semantics, then every resolved retention help target. An ordered-event suite proves the prompt follows all of it and that declining makes no mutating call.

Restore never resumes source-installation authority. Staging:

- converges core, canonical, and accelerator schemas;
- runs the sealed managed-authority sanitizer;
- refuses a generation that still holds authority;
- joins destination taint and disclosure evidence monotonically — an archive from a clean machine can never clear this machine's host-tools taint;
- stamps a fresh dataset generation with advanced accelerator and envelope epochs;
- drains the outbox and leaves FTS dirty for rebuild;
- unresolves every restored Campaign path;
- terminalizes the source machine's in-flight turn claims as `RestoredInterrupted`;
- inventories and opens the destination's own Campaign roots and commits their cleanup children in the same staged transaction.

The exact receipt — including the frozen zero-child vector — publishes through the envelope and anchor **before** the first live-root displacement, so a crash in between leaves the old installation in place and the retry rebuilds the identical checkpoint. After the swap only a committed marker aggregate spends the one `CommitAndReopen`.

`IBackupRestoreStartupRecovery` runs two phases. `RecoverPhysicalTopologyBeforeDatabaseAsync` runs before the guarded directory is even created — creating it would occupy the name a pending rollback has to move the prior installation back into — opens no SQLCipher handle, and converges all four displacement crash points to exactly one live root by comparing durable volume-and-file identities a rename preserves. `RecoverAuthorityBeforeReadinessAsync` reconstructs the exact `BackupRestore` owner, calls `ResumeExclusiveAsync` and never the initial acquisition, and returns `RecoveredReady` only after a successful `CommitAndReopen`; otherwise the journal stays active and both host and CLI readiness stay closed.

**The operator answers at the command line.** `arcanum backup restore --protected-state <mode>` takes the three wire values the effect digest commits to, and omitting it is the refusing default; an unrecognized value is refused before the passphrase is read, so a typo creates no staging root, no journal, and no exclusive owner. `--map-campaign <source-campaign-id>=<destination-campaign-id>` is repeatable, takes Campaign identities rather than names, and applies only to a selective import. The plan refuses an inapplicable mapping, a nil identity, one archived Campaign mapped to two destinations, and a destination Campaign this installation does not have — each by name, before anything is staged — while a destination it cannot *read* refuses nothing, because a missing credential is not evidence about the operator's mapping. With `Arcanum:Features:Covenant` off a selective import takes the plaintext path, which writes every `CampaignId` as `NULL`; a mapping is therefore refused with `backup.restore_campaign_mapping_covenant_required` rather than accepted and dropped, while an import naming no mapping behaves exactly as it always has.

**Still owed.** `BackupRestorePlan` carries the protected-state mode and the typed path mappings but not the Campaign bindings, so a rehearsal reports a bad mapping as a typed blocker and a good one as silence. `BackupRestoreService` also still writes the plain V1 journal alongside the V2 envelope, deliberately: removing it would strand a restore interrupted by an older build. A **new-profile** restore stays outside the reconciliation arm entirely — it displaces nothing — which is a scope boundary rather than a gap.

### 15.3 Reset and family erasure

Covenant has no time-based retention rule. Explicit reset and healthy-catalog factory erasure share ten durable phases from `InventoryPrepared` to `ReopenedVerified`; legacy checkpoint versions retain their old paths. Gate acquisition remains unreachable until the first checkpoint commits.

`POST /api/data/memory/reset/plan` returns the reset's five content-free aggregates under one installation read lease retained through protected serialization. The lease is revalidated before the first byte; the exact no-store/private tuple is applied and validators are stripped. The CLI carries the preview, not the capability, through the shared external-retention disclosure and prompt, then sends its `planId` as `expectedPlanId`. For explicit reset/factory erasure, that identity binds all five counts and their count kind; prune and workspace identities still exclude the report. Apply takes a fresh installation planning lease, rebuilds current state, refuses plan or inventory drift, validates the nonempty current dataset, commits the V3 owner, releases planning admission, and enters the coordinator directly. The preview lease never crosses confirmation. A successful reset reports reconciliation under the originally confirmed plan id and zero unproved deletion totals.

Protected `Request` and `Installation` plan admission must return their required live snapshot lease or fail closed; there is no lease-free fallback for those capabilities. The ordinary `PlanAsync` wrapper still consumes/releases its own admission for prune and workspace callers, and feature-off status may omit the optional Covenant projection exactly as before.

Preflight uses the exclusive lease's nonempty dataset and one private, unpooled, initialized, drain-enrolled read transaction. It exhausts labels and managed producers in nullable-`Guid` pages of at most 256 and, for factory erasure, proves the healthy catalog on that same WAL-visible snapshot. Partial/drifted catalog state or missing, duplicate, mismatched, or malformed ownership evidence refuses before effects. Database pages replay before canonical reset and managed pages after it. Nonrevocable disclosures fold with checked arithmetic into Exact or LowerBound exposure.

The canonical transaction proves `secure_delete`, removes the owned family, stamps a fresh dataset, and preserves authority taint, disclosure evidence, Campaign markers, schema, and restore credentials. Local storage proof checks WAL truncation, compaction, export fallback, sidecar absence, and one immutable reopen. That reopen returns the exact candidate for composite key/authority/availability publication. The same warm writer is restored before any pre-effect rollback and reopened freshly only after successful publication.

After immutable proof, caller cancellation cannot interrupt `ReopenedVerified`, publication, writer restart, disposition, or durable failure recording. Disposition is one-shot with no fallback; uncertainty remains `ReconciliationRequired` with `Covenant.MaintenanceFailed`. Current handlers resume only the leased durable owner. Lock-owning startup validates and adopts exactly one owner before readiness; no-lock CLI bootstrap does not scan or freeze.

Healthy-catalog factory erasure preserves the broad factory-reset contract without changing the frozen phases. Global apply requires a measurable current installation inventory and exclusive lifecycle independently of the inference feature policy; a missing lease, inventory, coordinator, or healthy catalog fails closed instead of deleting ordinary data alone. Its V1 owner runs the protected kernels first, then a required restart-idempotent ordinary cleanup after `ManagedArtifactsProcessed` and before `HandlesClosed`, while exclusive admission is still held. Recovery reruns the continuation while `ManagedArtifactsProcessed` is durable and skips it at `HandlesClosed` or later; legacy V0 checkpoint recovery is unchanged and is not a public bypass. Handle closure, WAL and sidecar proof, composite publication, fresh-writer and general-admission reopen, and exclusive release all follow ordinary cleanup. A same-snapshot plan or catalog mismatch fails with zero effects. The public result keeps the confirmed plan id and reports only executor-observed ordinary totals; the five Covenant aggregates remain inventory.

Under a full installation reset that same erasure is a nested arm rather than standalone work, and both records say so durably. The offline reset arm publishes the `Claimed` receipt in the same publication as the point of no return — the two are one fact from opposite sides, and a crash between them has to read as one transition started rather than as none — then launches the nested apply under that receipt's own nested operation identity, so a resumed reset replays the one nested operation instead of starting a second the record has never heard of. A workspace-scope reset claims nothing, because it runs no offline transition and a claim for a transition that never exists would block the ending forever. The offline CLI process runs the nested arm of a global or all reset itself, through the reset-only `StoppedHostFactoryErasure` the stopped-host composition carries: it acts only on the operation an authenticated reset record claimed, finds that operation by its exact requested identity, and never restarts a row at launch version zero, so legacy version-0 recovery is not a route for a claimed nested row. The transition learns it is nested by reading the outer record rather than by being handed a sink, so first entry and recovery in a fresh process reach the same answer from the same evidence; it resolves to no parent, a bound sink, or one content-free refusal, and it refuses a `CovenantReset` kind, a `Completed` receipt whose effect digest is not this launch's, a resume whose recomputed binding differs from the one its journal committed to, and a resume carrying a committed binding with no outer record at all. `ParentReceiptBindingDigest` is SHA-256 under `arcanum.grimoire.offline-transition.parent-receipt-binding.v1` over the outer operation id, the nested operation id, the nested canonical effect digest, and the `Completed` phase byte; the terminal winner digest is deliberately outside that preimage because it cannot be known before the effect. In the reconciliation suffix, between the exact terminal winner reread and the journal's own `ParentReceiptSatisfied` step, the handler publishes the `Completed` receipt into the outer record, rereads it, and recomputes the binding from what came back. `RecordParentReceiptAsync` takes that recomputed digest rather than copying the committed one forward, and refuses a null proof where a parent is bound and a non-null one where none is. A receipt that cannot be published, or that reads back as something else, parks `KeepClosed` and repeats no effect; an already-exact receipt is reread and never republished, so a replay does not advance the outer envelope revision a second time. After the apply the reset arm rereads the record and adopts whatever the nested transition published, whether that apply succeeded or not.

Every installation-reset scope, global/all included, plans locally under stopped-host authority: the command takes the exact maintenance lock and builds the plan itself, for global/all including the canonical Covenant-aware data plan and the Covenant inventory that becomes the disclosure, and never asks a running host for a plan. A running host stops both dry-run and apply at maintenance-lock contention before any output, and a missing installation identity or, for global/all, a missing local Covenant inventory fails before output, disclosure, or confirmation. For global/all the command measures the Covenant family itself, in its own process: it establishes the Covenant runtime from the plan's read-only database lease, reads the master API key without changing anything, and takes the inventory through the Covenant gate's installation read. An inventory it cannot measure is refused with the cause's own code (`Covenant.OperatorAuthorityUnavailable`, `Covenant.IntegrityFailure`, `Covenant.Unavailable`, `Covenant.HostToolsTransitionRequired`, or `Data.InventoryUnavailable`), and work a stopped host cannot drain is a plan blocker with its remedy; the Installation reset paragraph of `Arcanum.DESIGN.md` §5.4.7 states each. A fresh apply asks any host that has started since planning to shut down, acquires the exact maintenance lock, takes the client coordination lease for global/all, and under that held lock refuses an existing active reset (it resumes through recovery instead), rereads the installation identity, and replans; a plan or Covenant inventory that differs from the confirmed one is refused as `Data.PlanChanged` before any effect. Only then does it publish the owner-only authenticated V2 `Prepared` record and continue offline. No fresh reset hands anything to a running host, and the current CLI mints no new handoff: the host-handoff arm runs only for an authenticated record already at `Prepared + HostFactoryErasure`, whose typed handoff the startup projection rebuilds in memory. On that arm the running host validates the exact installation/data-plan binding under its already-held maintenance lock before the effect. The record's `OperationId` is the requested-operation replay key used to atomically start/replay the host factory LRO, while the host creates a distinct server operation ID. The key is never `RootOperationId`, a server identity, gate owner, or lease owner. Before responding, the host advances the same envelope with a content-free monotonic proof binding both identities, the confirmed data plan, reconciliation, and observed totals. The sequence remains exactly `prepare -> host apply/replay -> proof -> shutdown -> lock -> offline continuation`; the offline phase receives the CLI's exact lock, authenticates the publication, and never calls a lockless factory path. Pre-effect `Data.PlanChanged` may close and retire the handoff, but cancellation or any uncertain host outcome preserves it for replay. Normal startup blocks every active/partial state. The sole recovery-host bridge is the owning authenticated V2, or exact eligible ordinary V1 migrated to V2 before its next effect, at proof-absent global/all `Prepared + HostFactoryErasure`; proof-bearing, later, malformed, ambiguous, and ineligible legacy states remain blocking. That bridge is now decided over two records rather than one. `InstallationResetStartupRecovery.RecoverBeforeBootstrapAsync` recovers the offline-transition journal under the same held installation maintenance lock it already recovers the reset record under, and `InstallationResetNestedTransitionEvidence.Resolve` reduces the pair to one outcome that `GrimoireDatabaseHostedService` acts on before `GrimoireDatabaseBootstrapper` opens anything. Neither record active is `NeitherActive` and admits ordinary launch-gap inspection and bootstrap apply. A reset active with no receipt is `NestedNotStarted` and may begin its nested transition later; a reset active whose receipt is `Completed` with the journal already retired is `NestedRetired` and the workflow continues. A journal active alone with no parent binding is `StandaloneTransition`; both active with a `HealthyCatalogFactoryErasure` journal whose committed binding exactly equals the digest recomputed from the outer record's operation id, the receipt's nested operation id, and the journal's effect digest is `NestedBound`; and both active with the receipt already `Completed` is `NestedReceiptStoredRetirementSuffix`, admitted only while the journal names the same terminal winner the receipt reports and sits in `DatabaseReconciliationPending`, `RetirementPending`, or `KeepClosed`. That arm is tested by the terminal winner rather than by the phase, because the receipt is published after the winner is journaled and before the journal records its own parent step, so demanding the later phase would classify the crossing window itself as a disagreement; a parked journal is admitted for the same reason, since parking is the resumable state and what remains of it is still only the suffix. Those three journal-active outcomes are resumed rather than refused: startup opens the existing catalog through a validate-only unlock, reads the minimum persisted Covenant authority back and refuses unless it agrees with the journal that names it, spends a one-use handoff that closes the operation gate around the reconstructed owner, closes the probe physically, adopts the operation's lease under the held installation lock, and runs the registered recovery handler for the exact operation the journal names. Only `Completed`, `Failed`, or `Abandoned` lets the same start continue into ordinary bootstrap and readiness; a parked transition and every other ending keep readiness closed and report that manual recovery is required. Everything else blocks: a `Claimed` receipt whose journal is missing, since a transition that began may not be read as one that never did; a journal naming a parent with no outer record, since a nested transition may not be downgraded to standalone work; a nested `CovenantReset`; a missing or mismatched binding; a journal at an earlier phase than the stored receipt it stands beside; and two present terminal winner digests that differ, which is the one cross-record check neither record can make alone. Every blocking arm produces the same content-free `Covenant.ManualRecoveryRequired` refusal, and which arm it was is deliberately not in the message. The lock-free `InstallationStartupProbe` keeps its present behaviour and receives no outcome at all, because it holds no lock and so cannot read the journal honestly. Retirement of the reset record verifies `Closed`, removes the exact file and anchor, and removes the reset key last. A transition journal's retirement is deliberately not that shape: it closes its anchor and retains both the anchor and the key, because they are the only evidence that could finish an interrupted database transformation, and they leave only in the attested full reset's final credential cleanup, after the stated terminal proof and after the restore trio.

External remediation does not widen that public surface. It has no endpoint or configuration key and can enter only through `arcanum data factory-reset --all --apply --external-remediation-attestation <file>`. Argv preflight rejects every other scope/mode before configuration bootstrap; the CLI then performs one owner-controlled, no-follow, 64 KiB-bounded read and strict source-generated JSON decode. The same open handle must prove current effective-user ownership and only owner read/write bits on Unix (`0400` or `0600`), or a protected current-user-only ACL on Windows, both before and after reading. Version `1` binds nonempty operation, installation, and host-tools transition UUIDs; a positive `UInt64` taint-time master version; exact authority, database-marker, OS-marker-bytes, and remediation-action SHA-256 digests; a canonical unpadded-base64url 16–32-byte nonce; issuer `RetroDownfall.Remediation.v1`; whole-second UTC issue/expiry within 24 hours; and an exact 64-byte IEEE-P1363 P-256 ECDSA/SHA-256 signature. The verifier resolves only the code-pinned independent public root whose SubjectPublicKeyInfo SHA-256 fingerprint is `cc8f456c657c698ee8843e22fb2a195eeb50db2764b8f7e4853bf0c92ff5fc95`; no Arcanum configuration, database, credential slot, attestation field, or environment value can replace it, and the external private key never enters Arcanum.

The marker evidence carries that taint-time version in the complete unsigned domain. New OS markers are payload version `2` with positive `UInt64BE`; exact legacy version `1` remains readable as positive `UInt32BE` and is widened. The database stores the taint-time version as an exact eight-byte big-endian `BLOB`, including values above SQLite's signed integer maximum, while compatibility readers retain existing positive signed-integer rows; malformed or nonpositive legacy/canonical encodings are blockers.

The signed preimage is ASCII `Arcanum.FullInstallationReset.ExternalRemediation.v1`, NUL, byte version, the three UUIDs in RFC-4122/network order, taint version as `UInt64BE`, the four raw digests, decoded nonce and strict UTF-8 issuer each framed by `UInt16BE` length, then issued and expiry Unix seconds as `Int64BE`; the signature is excluded. The action digest is SHA-256 of ASCII `Arcanum.FullInstallationReset.RemediationAction.v1`, NUL, and byte `1` for exact `All`. Request and signed operation IDs must agree before any lookup, journal, marker read, shutdown, lock, or effect. Verification admits only the current exact `TaintedMatched` pair; clean, pending, mismatch, missing, unreadable, stale, future-issued, expired, unknown-issuer, replayed, or changed evidence is one content-free refusal. Ordinary reset has no attestation arm and refuses every dangerous pair before its host data effect.

Acceptance carries the signed operation unchanged into exact-lock continuation and V2 publication, then writes one encrypted authenticated `FullInstallationResetRemediationClaimV1`. #121 runs no host factory effect, host handoff, online replay, ordinary offline cleanup, or credential deletion; later slices must preserve this signed identity if they add those continuations. The claim holds only version, operation and installation UUIDs, domain-separated attestation/nonce/issuer digests, and whole-second acceptance time. Plaintext statement, signature, issuer, nonce, file path, trust root, and key material are absent from the claim, outer header, output, confirmation, diagnostics, and logs. Fresh admission always checks the live clock, signature, and pinned root. An exact authenticated retry may continue after statement expiry only by rebuilding the canonical projection against the still-exact live marker evidence and fixed-time matching every stored identity and digest; it grants no new authorization and any changed input still refuses. It is an operation-bound one-way authorization checkpoint, not deletion of its own: #122 landed the exact marker-pair compare-delete and Campaign-marker cleanup that claim authorizes, and #123 landed everything after it: managed-file reconciliation, Grimoire deletion, the restore terminal-state proof, the ordered removal of the three profile credentials, and retirement of the active record. The marker capability has macOS and Windows implementations with identity-bound deletion; Linux refuses this capability. A full reset reports completion only after the required marker, managed-file, database and credential proofs succeed.

Data-LRO lease maintenance covers pre-closure planning and launch publication, then stops for the closed transition period. A process-local claim keeps generic reconciliation off that operation while the journal binds its row revision. Ordinary continuation proves the operation id, `Running` state and exact lease owner inside its deletion transaction; it does not require an expiry that no task renews during closure. Factory launch checkpoint version 2 and reset launch version 4 bind the closed-transition protocol. Ownership loss stops effects and becomes a content-free requires-attention outcome.

Real SQLCipher acceptance proves old-read-lease revocation and all-six-token rejection in one process, plus fresh-process exact-owner adoption/resume from `InventoryPrepared`. The `/api/data` reset/factory routes now await that graph through final release before writing a response. Dedicated Covenant management routes remain separate. Full-installation deletion completes through the #123 terminal continuation, which the locked service reaches only when the managed-file reconciliation is verified.

Completion reports local secure erasure separately from external disclosure and never claims remote copies were erased.

### 15.4 Retire, forget, and erase are different

- **Retire** appends a tombstone and preserves history.
- **Forget** (#78) retires from retrieval while keeping the item inspectable, with content-free suppression evidence keyed to the installation so the next extraction pass does not re-add what the operator just removed. The evidence is keyed rather than a bare hash of the content deliberately: an unkeyed digest would be the same value the claim substrate already stores beside it, making the pair two copies of one confirmation oracle.
- **Selective hard erasure** (#100) removes one exact item and its local derivatives and verifies the removal locally, reporting honestly when only a log scrub remains. It records a content-free fingerprint keyed by an operating-system-held key that never enters the Grimoire or a backup, so extraction, agents, and restore cannot bring the item back, and `release` lifts one fingerprint when the operator supplies what it covers. It performs no external revocation: copies that already left the machine, and the local copies it deliberately does not touch, are reported and never removed.
- **Reset** removes one protected family and its local derivatives under exclusive authority.
- **Full installation reset** removes the installation identity and every owned local authority surface after external remediation where required.
- **External revocation** is generally impossible and is never implied by local deletion.

### 15.5 The provider-retention disclosure

Enabling The Covenant sends memory to configured providers, and Arcanum cannot un-send it. With the feature on, eligible content is sent on **every** primary, fallback, retry, compression, and tool-loop provider attempt, and a single turn may reach different configured providers and models. Arcanum suppresses only its own explicit cache instructions on Covenant-bearing calls; a provider's own request logging and automatic prompt caching happen outside this process and no local control reaches them. This is why the default is off, and why the default is a guarantee rather than a suggestion.

## 16. Failure isolation and degradation

OATH treats optional memory as valuable but not entitled to break unrelated product paths.

| Failure | Behavior |
|---|---|
| Core Grimoire schema or authority-state failure | Startup fails closed. |
| Covenant canonical tier absent or invalid | Context-enabled Covenant paths fail typed and closed; ordinary no-context Arcanum remains available; status and repair stay reachable. |
| Covenant accelerator absent or invalid | Canonical prompt authority remains available; inspection uses bounded canonical fallback or reports typed degradation. |
| Proposed artifact has unknown policy or integrity | Quarantine it; do not inject it. |
| Confirmed artifact has unknown policy or integrity | Fail context-enabled inference; do not silently omit required authority. |
| Missing or inconsistent protected label | Deny the read/write and require repair or erasure. |
| Changed Campaign root identity | Close Campaign authority and require authenticated path repair. |
| Marker absent on a restarted child | Typed blocker — an unproven root is not authority to conclude a cleanup happened. |
| An authenticated offline transition still active at startup | Finish it before the database is opened for ordinary use, then continue into ordinary bootstrap and readiness. Only a durable verdict counts as finished; a parked transition, a catalog that cannot be opened, an authority that does not agree with the journal, and a lease that cannot be adopted all keep readiness closed. |
| One maintenance record naming the other while the other is absent | Refuse startup with the content-free `Covenant.ManualRecoveryRequired`. A `Claimed` receipt whose journal is gone is a transition that began, and a journal naming a parent with no reset record may not be resumed as standalone work. |
| Installation-reset record and offline-transition journal both present and disagreeing | The same refusal. Kind, nested identity, binding digest, relative phase, and any two present terminal winner digests must all agree, because neither record may be reconstructed from the other. |
| Provider dispatch uncertain after acknowledgement | Preserve receipt evidence; retry only under a new physical ordinal. |
| Filesystem identity or hash mismatch during erasure | Leave bytes untouched and report a manual blocker. |
| Failed exclusive disposition or post-disposition finalizer | Keep scope closed and durable owner evidence recoverable. |
| External disclosure already occurred | Preserve nonrevocable evidence; local reset does not claim remote deletion. |

Note the asymmetry against the older §21.4 rule that "memory never fails an inference turn." That rule still holds for *disabled or unavailable* optional capability paths. **Enabled canonical corruption fails context-bearing inference explicitly** — silently omitting required Confirmed authority would be the more dangerous failure.

The feature-disabled path is a measured contract. An untainted stateless call performs no optional Covenant work, exposes no tools, and emits byte-identical prompt structure. Previously tainted history deliberately retains protected read and propagation requirements after disablement.

## 17. Implementation dependencies and qualification

Canonical memory depends on the pure Core protocol, hermetic native runtime, declared schema tiers and generation-bound persistence. Live provider calls add frozen-call admission and disclosure; management and lifecycle operations consume the same authority rather than introducing a second write path. Annals-backed curation and transformations feed Long Rest consolidation and evidence-based ranking. Evaluation and explicit delegation consume exact versions without promoting their authority.

These dependencies are implemented in the source tree. A change to one layer must preserve its consumers' contracts and run the owning tests, source-analysis closure, native qualification and full CI required for the candidate being delivered. Feature enablement, tracker closure and release publication require their own applicable acceptance evidence; this guide does not record a historical branch or checklist as current proof.

## 18. Verification model

OATH requires evidence at protocol, persistence, runtime, information-flow, recovery, performance, and documentation boundaries.

### 18.1 Protocol evidence

- literal enum codes and hard ceilings;
- all Unicode 17 normalization and rejection corpus rows;
- canonical binary framing and RFC 8785 JSON vectors;
- every digest domain and optional/list/discriminated-union shape;
- buffered-versus-streaming hash identity;
- exact generation-to-Bloom transition and merge algebra;
- attempt, branch, disclosure, and aggregate chain vectors;
- linker shadowing, fallback, review-only, and order cases;
- Confirmed all-or-fail and Proposed longest-prefix admission.

### 18.2 Persistence evidence

- fresh, repeated, legacy, partial, drifted, and newer-version schema installs;
- closed manifest and exact index-shape validation;
- real SQLCipher transaction and concurrency tests;
- unauthorized direct SQL mutation rejection;
- compare-and-swap, quota boundary, folding, ABA, outbox, and owner-cleanup tests;
- canonical availability under accelerator failure;
- restore reconciliation and generation replacement.

### 18.3 Runtime and information-flow evidence

- compile-time inventory of every inference caller and invocation surface;
- no history read before authority;
- preview/live shared-function parity;
- mutation-after-freeze rejection;
- provider-purpose and option forwarding;
- disclosure acknowledged before dispatch;
- branch abandonment and collector sealing races;
- response-plus-mutation atomicity for buffered and streaming paths;
- no raw Covenant tool arguments in logs, events, progress, or transcript;
- every derived sink and read route assigned one policy, enforced by `CovenantArchitectureBoundaryTests` and `CovenantPublicContractInventory` — both fail in *both* directions, and every naming-convention exception must be declared with a reason;
- tainted content never reaches ambient subagent, A2A, batch, or background inference.

### 18.4 Recovery evidence

- crash at every durable database/filesystem phase;
- exact-owner resume, wrong-owner refusal, and no race-winner replacement;
- old-writer restoration before pre-effect rollback;
- caller cancellation cut off after immutable proof;
- one-shot disposition and durable maintenance attention;
- same-process old-lease revocation, all-six-token invalidation, fresh writer/read behavior;
- fresh-process pre-readiness owner adoption and handler resume;
- bounded inventory, WAL-visible catalog refusal before effects, exact ownership evidence, checked disclosure exposure, and zero leaked handles;
- readiness closed until every required nonterminal intent is reconciled;
- a nested transition resolving its parent identically on first entry and on fresh-process recovery, from the outer record alone;
- every admitting and every blocking pairing of the installation-reset record and the offline-transition journal, including a `Claimed` receipt without its journal, a committed binding without its record, a binding the two records cannot recompute to the same value, a journal earlier than the receipt beside it, and two present terminal winner digests that differ;
- an unpublishable or altered completion receipt parking `KeepClosed` with no repeated effect, and an already-exact receipt reread rather than republished;
- credential cleanup resumed at each of the seven phases, with every removal compared against the projection taken while that group's accounts were all present.

### 18.5 Performance evidence

The approved Covenant workload measures:

- pure linker and renderer latency/allocation at maximum occupancy;
- warm encrypted canonical load, link, and render;
- full enabled pre-dispatch provider stage;
- disabled stateless and disabled untainted-Session overhead;
- tainted-history protected read path;
- disclosure acknowledgement under one and eight writers;
- receipt folding outside the append transaction;
- large structural fixtures for history, tools, and content parts without N+1 work.

Measured latency gates run only in the dedicated reproducible benchmark. Ordinary tests enforce deterministic structure, allocation, query plans, and command counts.

### 18.6 Native AOT and documentation evidence

All public DTOs and MCP structured results are named and source-generated. Tool schemas are hand-authored. No authority path uses reflection-based JSON, runtime type scanning, dynamic proxy, or SQL interpolation. Config POCOs use `{ get; set; }` and not `init`.

The Core digest corpus and Unicode corpus execute inside shipping-RID Native AOT smoke binaries. Documentation inventories ensure every endpoint, CLI command, configuration key, schema object, recovery handler, sensitive sink, error, and JSON root has one owning contract.

## 19. Honest limits and non-goals

OATH is intentionally not:

- a single undifferentiated memory table;
- a guarantee that Confirmed or derived content is factually true;
- automatic promotion based on model confidence, repetition, retrieval rank, or frequency;
- a blockchain, public transparency log, or externally verifiable consensus protocol;
- a distributed transaction that can roll back provider, network, process, or message effects;
- retroactive erasure of provider logs, prompt caches, recipients, filesystem snapshots, or backups;
- full bitemporal valid-time and dependency semantics in Covenant v1;
- automatic authority inheritance by subagents, A2A tasks, batch jobs, or daemons;
- a claim of complete OS isolation from same-user native code or trusted configured MCP servers;
- a cross-turn cache of decrypted Covenant content;
- a replacement for exact API, CLI, configuration, or shipped-design documentation.

Origin binding does not forbid erasure or privacy-preserving suppression. It means that any retained derivative keeps its lineage or an explicit unknown/unavailable state. Erasure removes the owned dependency closure rather than rewriting surviving artifacts to look source-free.

Authority conservation permits deliberate narrowing and explicit authenticated elevation. It forbids hidden amplification.

## 20. Capability contracts in detail

The Covenant foundation establishes the rules the implemented memory capabilities consume. The sections below describe their current boundaries; issue identifiers are historical cross-references, not pending implementation claims.

### 20.1 Feature-schema evolution (#102, XL, prerequisite)

Reusable code-owned schema evolution for declarative raw-SQL capability tiers: versioned DDL, resumable backfills, fingerprints, and health publication.

- Each capability family and tier has ordered integer versions and a closed source and installed-catalog manifest.
- Schema resources remain one object per file; no production numbered EF migration is introduced.
- Backfills are bounded, checkpointed, idempotent, restart-safe, and never advance past uncommitted work.
- Newer versions, definition drift, unknown objects, mixed catalogs, and interrupted transitions produce typed fail-closed health.

The schema coordinator and owning backfills use this machinery whenever a declared version step evolves an existing store.

### 20.2 Campaign-scoped retrieval (#76, XL)

Stop memory extracted inside one Campaign from being retrieved inside another. This stays one vertical delivery issue because Saga, Lexicon, vector, FTS5, fallback, cursor, restore, reset, and retention paths must share one Campaign-scope invariant.

**Saga.** Denormalize the owning `CampaignId` onto the memory row at write time rather than joining through `Sessions` on every query, backfilled idempotently and indexed. Both search paths must honor it — the vec0 KNN path and the managed cosine BLOB fallback — since a filter applied in only one path silently changes results based on whether a native asset is present. The persisted scope kind is authoritative: an explicit installation scope can be globally eligible, while null identifiers or unresolved legacy ownership alone never grant that scope.

**Lexicon.** Add an optional Campaign scope, replacing the unique index on `NameNormalized` with a unique index on scope plus normalized name. Existing rows migrate to global scope. `MatchEntitiesAsync` resolves Campaign scope first, then global, with Campaign shadowing global; both tiers still run exact hits before FTS hits.

**Authority.** Session scope comes from exactly one immutable `GlobalOnly | Campaign | LegacyUnresolved` binding. Working-directory scope comes from an opened physical root identity and protected marker, never a path prefix. Null, ambiguous, or unresolved legacy ownership never becomes installation-global by default; protected continuation fails until the binding is resolved. Campaign deletion never converts historical Sessions into Global authority.

One feature gate, default off. Off means today's behavior exactly, proven by the DCI regression suite. That suite is the set of test classes carrying `[Trait("Suite", "Dci")]` — the rendered prompt goldens, the stable/volatile segmentation the cache plan is cut from, and the per-source token attribution — and it is run with `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "Suite=Dci"`. `DciRegressionSuiteTests` pins that membership from outside the filter, because a filter that has quietly stopped selecting anything still prints `Passed!` and would let an empty run stand in for the proof.

### 20.3 Campaign rollup (#77, XL)

Campaign rollups provide cross-session continuity independently of Session compression. The default-off feature preserves the legacy compression rendering when disabled; enabling it adds the separately maintained Campaign summary described below.

- A durable Campaign-level rollup maintained incrementally through the existing Campaign Logger path, with a watermark in the shape of the Saga extraction watermark: advance only after persistence, retry a failed fold without advancing, lose nothing on failure.
- **Bounded**: refolded rather than appended once it exceeds its code-owned bound. A rollup that grows without limit becomes the context problem it was meant to solve.
- Injected at **session start**, not only under context pressure — continuity is needed when the session is short, not when it is long.
- Distinct DCI headings for the session rollup and the Campaign rollup, with `SystemPromptBuilder` parameter names corrected to stop calling the session summary `campaignSummary`. Renaming changes DCI bytes, so golden coverage updates deliberately in the same change with old and new text both recorded in the commit.
- Separate `ModelTokenEstimator` attribution so `mana` and `context inspect` show the cost of continuity.
- Forking a session must not double-count its entries. Deleting a Campaign removes its rollup; deleting a session refolds rather than orphaning.

Under OATH: rollups are immutable revisioned derived artifacts behind guarded current pointers, a turn binds one exact revision, a rollup derived from tainted history is itself `CovenantDerived`, and protected maintenance is request-bound.

The implementation keeps the feature off by default. With it off, the legacy compression heading remains exactly `### Campaign Summary (compressed context)`. With it on, that same per-Session destination is `### Session Summary (compressed context)`, and the independently maintained Campaign destination is `### Campaign Summary (cross-session context)`. Golden tests record both renderings and the empty case; neither destination gains instruction or tool authority. The contribution lane excludes copied fork prefixes and consumes native history independently of the ordinary compression watermark. Pages are bounded to 32 KiB of input, 8 KiB of output, and 2,048 requested output tokens, without a lifetime contributor ceiling.

Protected summaries use the same authenticated claim, disclosure, immutable source vector, and atomic derived-output rules as other protected maintenance. A committed checkpoint is replayable without another provider send; uncertain dispatches receive a fresh physical ordinal. Clean claimed replies also publish an authoritative finalization guard, so a retry cannot silently pay for an answer twice. The Campaign-summary reset/retention family purges exact derived owners and dependents, preserving the evidence and monotonic revisions that prevent stale work from publishing again.

### 20.4 The Long Rest (#75, Epic)

Make Saga get better as it grows instead of only getting bigger.

**#93 — Deterministic deduplication, dependency-aware supersession, and transformation receipts (XL).**

- One bounded operator-declared Saga transformation makes exact duplicates or equivalent observations converge idempotently to one effective canonical claim **without deleting immutable source versions**. Exact duplicates require the same content-hash format and digest; equivalent observations require an explicit declaration over inspected exact versions and hashes. Similarity may discover candidates under #91, but does not authorize consolidation or establish reinforcement.
- Supersession names the exact survivor version and preserves every original memory, embedding, origin, Session and attachment provenance, Campaign scope, sensitivity, pin, retirement state, validity interval, and dependency edge. Subject heads retain their truthful history; an immutable exact-version suppression projection removes superseded current sources from recall. Operator inspection keeps those rows visible with their eligibility and content-free receipt evidence.
- Every first valid applied or no-change transformation writes an immutable content-free receipt binding policy version, ordered exact inputs, the survivor declaration, output, and canonical hashes. Stale targets and claimless subjects are refused without manufacturing Annals history; protected artifacts are proved before plaintext is read and remain untouched.
- Reprocessing unchanged inputs performs **zero canonical writes** and produces the same result across storage order and shipping RIDs.
- Source correction releases suppression of the old head, and a source pin permits recall while pinned. Survivor correction or retirement does not revive an old superseded assertion. Erasing any participating subject removes the complete receipt and its companions, so surviving subjects follow their own eligibility again.

**#91 — Hybrid discovery and authority-scoped resumable consolidation sweeps (XL).**

- FTS5, vector, and RAPTOR candidates honor canonical Campaign scope, owner generations, protected partitions, and deterministic bounds.
- Near-neighbor thresholds and any model judgment belong to discovery. They produce an exact candidate group for the #93 policy rather than making a similarity score canonical authority.
- Protected work runs only under request-bound maintenance authority, exposes **zero tools**, and persists a disclosure receipt before each provider dispatch.
- Stable checkpoints advance only after committed work; cancellation or failure leaves every unprocessed item eligible for retry.
- Sweeps stop before another dispatch when Campaign binding, deletion, reset, restore, feature, or authority generations change.

Discovery, sweeps and read-time ranking/decay use the independent default-off `Arcanum:Features:LongRest` opt-in (API §8.36, DESIGN §21.16).

**#95 — Counterfactual credit, decay, and explainable ranking (XL).**

- **Retrieval and admission counts alone produce no positive usefulness credit.**
- Ranking modifiers have documented bounds and deterministic tie-breaking independent of storage order. An opaque score is worse than none.
- Superseded, retired, and dormant versions are excluded appropriately; pinned and operator-authored versions honor their approved exemptions. **Decay excludes, it does not delete.**
- `memory explain` reports score components, policy version, and evidence class without exposing protected content.

Sweep mechanics follow the Tapestry precedent in being a gated `BackgroundService`, cancellable at every item and batch boundary, with no whole-sweep deadline, and in resuming a killed sweep from its checkpoint rather than restarting it. Unlike the Tapestry, recovery never runs a Long Rest sweep: startup and background reconciliation only park an interrupted one, and the Long Rest worker (clean work, after a cooldown and within a bounded failure budget) or a fresh authenticated Session request (protected work) readmits it (DESIGN §10.8.2, §21.16.3). Consolidation model spend is priced, reserved, and audited like any other model call — it must be attributable, never hidden.

### 20.5 Memory curation (#78, Epic)

Memory that steers every future turn and cannot be edited is memory the operator has to distrust. Curation extends the #74 mutation, read-authority, immutable-history, and erasure kernels; it must not add a second write path or mutate stored versions in place.

Each durable store gets the same verbs against one item, using the identity that store already has:

| Verb | Effect |
|---|---|
| `show` | full content, provenance, scope, state, and retrieval eligibility for one memory; for the Covenant, content-free by rule: exact head identity, rendered hash, lifecycle, and per-lane curation |
| `correct` | publish corrected content and required history/projections atomically; Saga also replaces its embedding, while Covenant and Lexicon keep their own store-specific contracts |
| `retire` | remove from retrieval while keeping it inspectable; hard erasure is the separate, confirmed `erase` verb, and `release` lifts its fingerprint |
| `pin` / `unpin` | mark durable and exempt from consolidation, decay, and retention pruning |

**#96 — Covenant exact-version operations (XL).** Correction appends a version and preserves provenance, sensitivity, disclosure evidence, and immutable history. Guessed, stale, unseen, quarantined, pressured, or wrong-branch targets fail before mutation. Pin, unpin, retirement, reactivation, and scope-mask behavior remains lane-specific and explains broader-scope fallback before commit.

**#97 — Saga versions and lifecycle (XL).** Correction appends an immutable version and atomically publishes embeddings and current-state projections. Retirement stores content-free keyed suppression evidence that blocks equivalent re-extraction — without it the next extraction pass re-adds what the operator just removed and the curation loop never converges.

**#98 — Lexicon versions and lifecycle (L).** Item identity includes typed scope, normalized name, and exact version, with Campaign-shadowing preserved. Correction updates the external-content FTS projection in the same publication boundary. Retired entries leave matching and retrieval, remain inspectable, and cannot be silently recreated through `scribe_lexicon`.

**#99 — Review queues, bulk actions, and actionable search (XL).** Bounded, newest-first, paginated queues reporting assertion origin, source, scope, and exact version, with a durable review marker. **Unreviewed memory keeps its normal eligibility** — the queue is a tool, not a gate — and advancing a marker never mutates the reviewed item. Search hits route to the owning store's typed operation and prove other stores were unchanged.

**#100 — Selective hard erasure with keyed resurrection suppression (XL).** Erasure requires an exact item and version, typed preflight, explicit confirmation, and a bound effect digest. Canonical versions, projections, embeddings, FTS rows, labels, and managed artifacts are purged under the required generation leases. Suppression fingerprints retain no plaintext, do not correlate across installations, and prevent ordinary extraction or restore from resurrecting erased content. Results distinguish verified local erasure from disclosures Arcanum cannot revoke externally.

**Safety.** Every direct operator mutation requires interactive confirmation or `--yes`. Agent mutations require their own exact capability and compare-and-swap evidence; no Ward prompt substitutes for those authorities. Editing a memory never edits its provenance. No mutation appears on an unauthenticated route or on `/v1`. There is still **no generic delete-all-memory command**.

### 20.6 Bitemporal validity and dependency-aware claims (#105, XL, prerequisite)

The implemented Annals claim model for Saga and Lexicon distinguishes:

- **transaction time:** when Arcanum learned, transformed, superseded, or retired a claim;
- **valid time:** when the claim is asserted to apply in the represented world.

Each Annals claim version gets immutable identity, typed origin, scope, sensitivity, valid-time facts, and transaction-time facts. Dependency edges are bounded, deterministic, cycle-safe, and target exact retained versions. Corrections and supersession append versions and advance guarded current pointers without rewriting historical content. Existing Saga and Lexicon rows receive a conservative, idempotent backfill that **never launders ambiguous Campaign history into Global authority**.

This layer coexists with immutable Covenant versions and generation-bound receipts; it does not duplicate them as Annals claims. Automatic capture defaults on, and required curation evidence remains independent of that toggle.

### 20.7 Counterfactual memory evaluation (#106, XL, prerequisite)

Measure whether admitted durable memory improved task outcomes by comparing controlled executions with and without the same versioned memory plan.

- Every comparison binds exact model, provider options, prompt plan, memory versions, evaluator policy, and workload identity.
- Metrics distinguish **benefit, harm, no measurable effect, and insufficient evidence**.
- Evaluation never mutates production memory or grants confirmation, promotion, correction, or retirement authority.
- Stored reports contain bounded aggregates and digests without prompts, memory plaintext, provider payloads, or secrets.

### 20.8 Scoped read-only agent recall (#101, M)

One gated read-only tool so the agent can ask what it knows about a subject rather than waiting for similarity to surface it. Bounded typed query, scope, and cursor inputs; Campaign identity resolved through #74. Global, Campaign, deleted-owner, retired-version, and protected-partition rules match ordinary retrieval exactly. Covenant results require clean generation-bound read authority and no-store handling, while generic unauthorized search continues to exclude Covenant. The tool is absent for disabled, unattended, ambient background, and unauthorized subagent invocations.

### 20.9 Dynamic Context Injection v2 (#103, XL, research)

Move eligible stable context into a deterministic provider-cacheable prefix while preserving authority, sensitivity, and token-attribution boundaries — **only after measurement**.

- A closed policy admits only stable, nonsensitive `CodeOwnedFraming` and `CodeOwnedFormatting` spans. Memory, attachments, user-controlled content and protected spans are excluded.
- Cache identity binds provider, model, tokenizer, rendered prefix, policy version, and every relevant authority generation.
- Campaign, profile, credential, reset, restore, and protected-state changes invalidate reuse deterministically.
- Benchmarks report hit rate, cached tokens, latency, cost, and invalidation behavior; the disabled path preserves existing prompt bytes.

Performance alone cannot weaken the OATH disclosure boundary.

### 20.10 Typed Covenant operational defaults (#104, L, research)

Represent operator-confirmed **nonsecurity** preferences as constrained typed defaults that approved call sites apply deterministically. The closed catalog grants no authority over authentication, Ward audit behavior, tool advertisement or capabilities, egress, retention, resource ceilings, or platform security policy. Compilation rejects unknown kinds, malformed values, unsupported consumers, and every security-policy target. Applied defaults bind the exact Covenant version and policy version into plans, receipts, and explanations, and exhaustive tests prove no typed default can weaken platform-owned authority.

The implemented catalog is deliberately bounded to `Temperature` (`[0, 2]`, key `defaults.inference.temperature`) and `TopP` (`[0, 1]`, key `defaults.inference.top-p`), both consumed only by `InferenceSampling`. Authoring uses the existing operator Confirmed Global/Campaign path; malformed scalar values and any other `defaults.` target are refused. Ordinary linking governs shadowing and masks, and Proposed content cannot supply executable defaults. Explicit inference request values win. Plans and admitted-default receipts bind the exact entry/version/revision, scope, kind, consumer, and typed policy version; the provider envelope binds the effective options. Explain reports eligible planned defaults with protected values only under `showContent`, and never claims an unspecified request applied them. The exact syntax and contract are owned by DESIGN §10.21.8 and API §8.28.

### 20.11 Least-authority subagent delegation capsules (#107, XL)

A top-level authenticated native invocation may authorize one bounded explicit memory selection through `POST /api/intelligence/memory-delegation`. The ordinary stateless or Session-backed parent may select `memory` in one native `delegate_task` call. The host binds exact current eligible versions, canonical scope and owner, provenance, a closed purpose and actual executed child task identity to one process-local capability. Count, rendered bytes, proof metadata, process quota and lifetime are code-owned bounds; overflow refuses the whole selection. DESIGN §10.21.9 and API §8.10.4 own the architecture and exact wire contract. This section states the runtime contract; it does not record terminal qualification or delivery of #107.

The child remains `None`, stateless, tool-free and without ambient retrieval or mutation/promotion authority. It receives only selected frozen fragments plus ordinary explicit task inputs. Missing selection retains ordinary isolated delegation. Siblings, nested invocations, operation rows and backups cannot recreate, widen, renew or forward the selected-source capability. Data remains data: Proposed and model-writable fragments do not gain system, defaults or tool authority.

Relevant memory mutations conservatively revoke all active capsules in v1 and cancel/drain admitted protected effects before their transaction. Expiry, Campaign deletion, retirement, suppression, reset/restore and authority/generation changes prevent later protected use or publication. Consumed-branch maximum sensitivity and union provenance cover selected sources and frozen explicit parent-derived inputs and propagate through child results, atomic Session tool persistence, parent continuation and complete protected HTTP serialization. A later ordinary child can retain only a non-reading output guard and label for explicit derived input, with no additional selected fragments or read authority. Known provider usage is durably recorded exactly once even if revocation withholds the result; earlier remote disclosure remains disclosed.

A consumed root with actual nonempty staged Covenant mutations refuses the combined finalization before its mutation writer or effects run, committing neither those source changes nor the assistant reply. Immediate parent memory tools may execute and revoke the root, after which protected pair/final publication refuses. Unused-root and ordinary combined commits retain their existing behavior. An unused expired grant only prevents later selection; ordinary parent work with no capsule-derived output retains its normal lifetime and authority. No additional global delegation limit follows from these publication rules.

## 21. Glossary

| Term | Definition |
|---|---|
| **Authority firewall** | Platform rule that semantic content cannot define its own origin, scope, lane, placement, capability, or approval policy. |
| **Canonical** | The authoritative durable representation used for lifecycle and prompt eligibility, independent of derived indexes. |
| **Claim** | Architecture-level assertion with stable identity, authority, lineage, lifecycle, and revision history. |
| **Confirmed** | Operator-authorized Covenant lane rendered as trusted context. It is not a factual-truth label. |
| **Proposed** | Agent-staged Campaign-only Covenant lane rendered as fenced untrusted data. |
| **Derivative** | Summary, extraction, index row, embedding, notification, file, or other artifact computed from one or more sources. |
| **Generation** | Random dataset identity that separates reset/restore lineages and prevents ABA. |
| **Lineage** | Immutable evidence linking an artifact to sources, versions, turns, transformations, and receipts. |
| **Admission** | Concrete provider-attempt decision about which eligible material fits the frozen call. |
| **Materialization** | Exact occurrence of a source in the final provider-neutral payload. |
| **Sensitivity** | Conservative information-flow classification merged across every contributing source. |
| **Ward** | Informational per-tool audit record; retained active-record routes exist only for compatibility. |
| **Disclosure** | Receipt-backed evidence that protected content was authorized for an external destination or locally managed sink. |
| **Locally revocable** | Arcanum-owned artifact that can be identity/hash-verified and deleted locally. |
| **Nonrevocable** | External or unmanaged effect that local reset cannot reliably undo. |
| **Long Rest** | Consolidation and adaptation lifecycle over immutable claims and outcome evidence (#75). |
| **Profile namespace** | Digest derived from a profile root's retained no-follow parent handle, carrying no path text, used to namespace restore-journal, reset-active, and offline-transition-journal credentials. |
| **Reset-active envelope** | AES-256-GCM V2 publication bound to profile, installation, operation, location, revision chain, scope, and plan, with an OS-secret anti-rollback anchor. |

## 22. Source map

The following maintained documents own or explain the detailed contracts summarized here.

- [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md): shipped architecture, persistence, runtime, security, testing, and implementation evidence. Covenant slices are §10.10 through §10.26.6:
  - §10.10 Core protocol foundation
  - §10.11 Canonical persistence and inspection search
  - §10.12 Invocation authority and Campaign binding
  - §10.13 Prompt planning and atomic turn publication
  - §10.14 MCP mutation and sensitive-egress controls
  - §10.15 Derived-output protection and host-process taint
  - §10.16 Public and recovery contract freeze
  - §10.17 Maintenance and protected-erasure recovery
  - §10.18 Operator surfaces, configuration, and the pre-binding authority boundary
  - §10.19.1–§10.19.13 Backup, restore, and protected transfer
  - §10.20.1–§10.20.14 Retention, reset, and full erasure
  - §10.21.1–§10.21.10 Live turns, typed defaults, delegation and scoped recall
  - §10.22.1–§10.22.6 The operator's write and read paths, and the agent's proposal
  - §10.26.1–§10.26.6 Curating what is already remembered
  - §10.23 What an installation holds, and what a turn had to drop
  - §10.24 The release benchmark
  - §10.25–§10.25.1 Evolving an installed tier through a declared version chain
- [`docs/Arcanum.Engineering.md`](Arcanum.Engineering.md): engineering and operator orientation, current capabilities and operational boundaries.
- [`ArcanumOATH.Human.md`](ArcanumOATH.Human.md): plain-language mental model and guided claim lifecycle for readers who do not need implementation-level contracts. Kept in step with this document.
- [`Arcanum.CHAT-LOOP.md`](Arcanum.CHAT-LOOP.md): the shared model/tool-loop and attachment continuation ordering that the OATH runtime integration extends.
- GitHub issues [#73](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/73) through [#115](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/115): the delivery units. Historical acceptance discussions remain there; maintained documentation and source/tests must stand alone without a dated plan or delivery report.

OATH should be updated when a change alters its cross-store authority, lineage, sensitivity, publication, disclosure, recovery, or lifecycle model. Exact route, command, configuration, and shipped implementation changes still update their canonical owning documents in the same change.
