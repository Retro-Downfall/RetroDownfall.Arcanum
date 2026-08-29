# Issue #97: Immutable Saga correction, retirement, pinning, re-embedding, and re-add suppression

**Status:** Designed on 2026-08-26. Approved by the operator before any code was written.

**Branch:** `codex/issue-97-saga-version-operations`, cut from `long-term-memory`, to be merged back with `--no-ff`.

**Issue:** #97, an XL delivery slice under #78, blocked by #86, #76, #102 and #105 (all closed), blocking #93, #99 and #100.

**Names settled on the issue:** a **correction** rewrites one Saga memory's text while naming the exact content it believes it is replacing. A **retirement** takes one memory out of retrieval without deleting it, and records keyed evidence that stops extraction re-adding it. A **reinstatement** undoes a retirement. A **pin** marks one memory operator-durable. Together these are the **curation** verbs for the Saga store.

## 1. Objective

Give the operator the four things they cannot do to an auto-extracted memory today: read exactly what one is and where it came from, change its text without losing it, take it out of retrieval without losing it either, and mark it durable. Saga is the one durable store written entirely without the operator's participation — headless extraction infers it from finished transcripts and it starts steering retrieval immediately — so it is the store where the absence of curation is felt most.

Every correction and every retirement appends an immutable version to the Annals, which #105 built and which declared a retirement operation nothing writes. This slice is that operation's intended writer.

## 2. Scope, settled before design

Four framing questions were put to the operator and answered before any code was designed.

- **Delivery shape.** Delivered whole rather than decomposed into children. One branch, one merge, one issue closed. The schema step, the correction, the retirement and its suppression, the pin, and the detail view share one substrate and one protocol; split apart they could not be tested independently.
- **Where superseded text lives.** Nowhere. A correction rewrites the `saga_memories` row in place, and the immutable version is an Annals claim revision binding a SHA-256 of the content it describes. That is the Annals' own stated principle — a version proves which content it describes without being able to reconstruct it — and it is what lets forgetting a memory leave no residue that still carries what the operator asked to remove. A content-bearing version table would make every correction leave the rejected text durable, and would give the forget promise a second table to reach.
- **Whether retirement can be undone.** Yes, and it re-embeds. Retirement deletes the memory's embedding rows outright so that exclusion from retrieval is structural; reinstatement lifts the retirement, releases that memory's suppression evidence, and re-embeds, which is an honest provider cost and is refused when the embedding substrate is degraded.
- **Whether curation obeys the Annals feature gate.** No — it is ungated, on the same terms erasure already is. "Editing a memory never edits its provenance" means the record that the operator edited it is evidence rather than retrieval, and with `Arcanum:Features:Annals` off nothing reads a head to decide what a turn recalls, so prompt bytes, token accounting, and ranking are exactly what they were.

Two further boundaries were stated rather than asked, because the tree already answers them.

- **Acceptance criterion four names two sweeps that do not exist.** Long Rest is #93, which is blocked *by* this issue, and nothing in the installation decays. A pin is therefore enforced where sweeps actually run — retention planning and retention execution — recorded in durable state the later sweeps inherit, with the absence stated rather than implied. That is the treatment §10.26.3 gave the Global Covenant pin that binds no agent path.
- **Keyed suppression for hard erasure is #100, and review queues, bulk actions, and actionable search are #99.** This slice adds retirement suppression only, and leaves `DELETE /api/saga/{id}` and `arcanum saga delete` meaning exactly what they mean today.

## 3. Governing constraints

- **`annal_versions.OperationCode` bakes `CHECK (OperationCode IN (1, 2, 3))` into a shipped table.** SQLite cannot `ALTER` a `CHECK`, and the table is referenced by `annal_heads` through a four-column composite foreign key, by `annal_dependencies` through two more, and is guarded by an append-only trigger. No new `AnnalOperation` member may be declared, which is why a reinstatement is modelled as a restatement rather than as a fourth operation.
- **The Core tier's version-3 source fingerprint is `2CC5BB384111470F86668C4928B54306C7B8F7DCFDBBB152DF9F7C0CF162CC2F`,** read out of the head tree before any object file was touched. Nothing can recompute it afterwards. A fixture reconstructs the version-3 tree by freezing `saga_memories.sql`'s version-3 text and removing the objects version 4 adds, and a test hashes that reconstruction, so a wrong pin fails there rather than against every operator's installation.
- **`ALTER TABLE ... ADD COLUMN` splices `, <column-def>` in front of the stored declaration's closing parenthesis, verbatim and without the preceding newline.** A head file that does not match that layout reports `DefinitionDrift` on every evolved installation and on none of the fresh ones, which is the hardest shape of that failure to reproduce because a developer's own database is always fresh. `saga_memories.sql` already carries that layout from version 2 and explains it in a comment; version 4 extends the same line.
- **`ALTER` cannot add a `CHECK` either,** so no invariant over the new columns can be a table constraint. The writers own them, exactly as `SagaMemoryScopeKind` and its writers own the existing "Campaign is present exactly when the kind is Campaign-scoped" rule.
- Raw SQL through the declarative schema tree, one object per file, `CREATE ... IF NOT EXISTS`. No EF entity, no numbered migration, no compiled-model regeneration.
- Native AOT throughout: no reflection-based serialization, no dynamic type loading.
- Every new durable table joins the existing lifecycle: retention inventory, retention pruning, both memory-reset arms, factory reset, Campaign-scoped reset, and the backup database worker.
- Documentation in `docs/` names capabilities, never issues.

## 4. Considered approaches

### 4.1 A content-bearing `saga_memory_versions` table — rejected

Every correction writes the superseded text to a version table, so an operator can read what a memory used to say and roll back to it.

Rejected on the promise it breaks. Saga's whole erasure story is that forgetting a memory removes it; a version table means the text an operator corrected *away* survives their correction, and the retirement that was supposed to stop content reaching the model leaves a copy of it one join away. It also gives every erasure path a second table to reach, and the path that under-reached would be the one that left the hole. The Annals already answers the question this table would answer — what did this claim say, when, and on whose word — without holding the bytes.

### 4.2 Lifecycle in a sidecar table rather than columns — rejected

`saga_memory_curation` keyed by `MemoryId` with `ON DELETE CASCADE`, so the version-4 step only ever adds objects and never touches `saga_memories`, which is the property that lets a fresh installation and an evolved one agree without writing a head file in SQLite's own layout.

Rejected because the subject here *is* the row. Unlike the Covenant's curation subject, which is a scoped key that may name a Campaign holding no entry at all, every Saga curation fact is a property of one existing `saga_memories` row and has nowhere else to live. A sidecar buys avoidance of a trap this file has already sprung once, survived, and documented — the version-2 scope pair went in the same way and a test proves the fresh and evolved trees agree — while charging a `LEFT JOIN` to every listing, every detail read, and both halves of retention. Two columns on the row that owns them read as one definition; a join that a future reader forgets is a silently unpinned memory.

### 4.3 Columns for lifecycle, add-only tables for evidence — selected

Retirement and pin are timestamps on `saga_memories`, because they describe that row. The suppression evidence and its key are new tables, because they must *outlive* that row: a suppression that vanished when the memory was hard-deleted would let the next extraction pass re-add exactly what the operator removed, and the curation loop would never converge.

## 5. Storage — Core schema version 4

### 5.1 Two columns on `saga_memories`

`RetiredAtUtc TEXT NULL` and `PinnedAtUtc TEXT NULL`, added by two `ALTER TABLE ... ADD COLUMN` statements and written into the head file on the same line the version-2 pair occupies.

Null means active and unpinned. Each column carries *when* as well as *whether*, so the detail view reads one value rather than pairing a flag with a timestamp that could disagree with it. Neither column is `NOT NULL`, so neither needs a default and neither rewrites an existing row.

### 5.2 `saga_retirement_suppressions`

The keyed evidence, one row per retired content-and-scope. It holds the 32-byte suppression digest as its primary key, the scope kind and Campaign the digest was computed over, and when the retirement happened. It holds no memory identity and no content.

No memory identity, because the row has to survive the memory: an operator who retires a memory and then hard-deletes it must not thereby re-enable the extraction they rejected. The scope columns are stored rather than derived because the Campaign-scoped memory reset selects on them — that is the operation that takes one Campaign's memories and the evidence about them together, and it compares the Campaign exactly, which is why that column is settled the way every other stored Campaign identity is.

### 5.3 `saga_suppression_key`

One row by `CHECK`, holding 32 random bytes and when they were generated. Created lazily inside the first retirement's own transaction, so an installation that never retires anything never generates one.

### 5.4 The version step

Five transition statements under `Transitions/V4/`, one per file in install order: the two `ALTER TABLE ... ADD COLUMN` statements, `saga_retirement_suppressions` and the Campaign index the cleanup path reads, and `saga_suppression_key`. The step declares no backfill: the columns are nullable and both tables start empty, so there is no existing row for a sweep to classify. The chain's version-3 pin is the fingerprint recorded in §3.

## 6. Correction

### 6.1 The contract

`arcanum memory saga correct <id>` and `POST /api/memory/saga/{id}/correct`. The corrected text arrives through `--file` or piped standard input, exactly as the Covenant's own write verbs take it, so a memory's replacement text never lands in shell history or in the process list of a shared machine.

The request names one target fact beyond the text: `ExpectedContentHash`, the SHA-256 of the content the operator read. There is no version identity to name and no revision to name, because a Saga memory has one lane, one row, and one current content — the hash is the whole of what "I saw this and decided it was wrong" means here. That is the field the Covenant's own correction called load-bearing, and it is the only one of its four that has a counterpart in this store.

There is no prepare-and-commit token pair. The Covenant's two-step protocol exists to bind a Ward disclosure, an operator-authority lease, and a compiled-hash measurement taken on the server across a five-minute window; Saga has no protected-state authority, no Ward, and no compiler, so a token would be ceremony with nothing behind it. What the token protocol actually protects — that the operator committed against the state they were shown — is protected here by comparing the hash inside the write transaction.

### 6.2 The write

The embedding is computed first, outside any transaction, because a provider call inside one holds a write lock across the network. Then one transaction:

1. re-read the row and compare `AnnalContentDigest.ForSagaMemory(current content)` against `ExpectedContentHash`;
2. update `Content`;
3. replace the `saga_memory_embeddings` row;
4. replace the `saga_memory_embeddings_vec` mirror when vec0 is available;
5. append an Annals `Correct` version with origin `OperatorStated`, binding the new content's digest.

The comparison happens inside the transaction rather than before it opens, because a check outside is a measurement of a state the write no longer describes.

### 6.3 What is refused

| Condition | Class |
|---|---|
| no memory with that id | not found |
| the stored content's digest is not the one named | stale target — the operator is correcting content they did not read |
| the memory is retired | refused; a retired memory is reinstated before it is corrected, which is a different sentence |
| the embedding substrate is unavailable or misconfigured | refused before anything is written |

The last one carries the weight. A correction that cannot re-embed would leave `Content` saying one thing and the vector saying another, so retrieval would keep surfacing the text the operator just rejected — which is precisely the outcome the acceptance criterion exists to prevent. Refusing is the only honest answer.

**Revisited.** The corrected text being what is already stored is not refused. Refusing it bought the operator nothing — the power of this system is in its memory and its security, not in restrictions on a request that happens to turn out to be a no-op — so it now succeeds: the store writes nothing at all (no content update, no embedding replacement, no vec0 mirror write, no Annals claim revision), reports `Unchanged` so a caller can still tell "nothing needed doing" from "a correction was applied", and returns the memory's current projection exactly as a real correction would.

### 6.4 What a correction does not touch

`CreatedAt` stays what it was: the memory was formed then, and the Annals records when it was corrected. Attachment provenance rows survive untouched and keep reporting their source as unavailable when it is gone, per §10.6.1. Scope is not re-derived, because a memory's ownership is its owning Session's binding at the moment it was written and an operator's correction is not a new Session.

## 7. Retirement

### 7.1 The write

`arcanum memory saga retire <id>` and `POST /api/memory/saga/{id}/retire`, carrying the same `ExpectedContentHash`. One transaction:

1. compare the digest;
2. stamp `RetiredAtUtc`;
3. delete the `saga_memory_embeddings` row and the vec0 mirror;
4. write the suppression row, generating the installation key if this is the first retirement;
5. append an Annals `Retire` version — a tombstone binding no content, which the table enforces rather than trusting the writer to remember.

Refused when there is no such memory, when it is already retired, and when the digest disagrees.

**Revisited.** Retiring a memory that is already retired is not refused. The operator named a state and the memory is in it, and answering no there argues with them rather than serving them — the same reasoning §6.3 was revisited under. It now succeeds: nothing is written at all, the result reports `AlreadyRetired` so a caller can still tell "this call did it" from "it was already so", and the memory's projection comes back exactly as a real retirement's would. Two refusals remain here — no memory with that id, and a digest that disagrees — and the retirement is checked before the digest is compared, so an already-retired memory reports that whatever hash the call carried. `AlreadyRetired` survives as a refusal for correction alone, exactly as §6.3's table states, because correcting a retired memory acts on it not at all rather than leaving the operator with the state they asked for.

### 7.2 Why exclusion is structural

Deleting the embedding rows is what takes the memory out of retrieval, and it is deliberate rather than incidental. Every path that can surface a Saga memory reaches it through an embedding: the accelerated path reads `saga_memory_embeddings_vec`, the managed path reads `saga_memory_embeddings`, and the Campaign-scoped path inner-joins the blob table to `saga_memories`. A row with no embedding is unreachable from all three without a predicate that four call sites would have to agree about, and a predicate one of them forgot is a retired memory still steering turns.

The `saga_memories` row itself stays. That is what makes retirement different from deletion: the memory remains inspectable, listable, and marked retired, and the operator can read what they took out and put it back.

## 8. Suppression

### 8.1 The digest

`HMAC-SHA256(key, "arcanum/saga/retirement-suppression/v1" ‖ scope kind code ‖ Campaign identity ‖ content)`, over a unit-separated preimage, with the content field last so no field boundary can be forged by a value that happens to contain the separator's neighbours.

The content is the exact stored text, hashed under the same definition of sameness `AnnalContentDigest.ForSagaMemory` already uses. One definition of "the same content", not two.

### 8.2 Why it is keyed

The Grimoire is encrypted at rest, so keying is not what protects the content from someone reading the file. It buys two other things, and the design claims only those two.

- **Domain separation from a hash that is already stored.** `annal_versions.ContentHash` is a bare SHA-256 of the same bytes. An unkeyed suppression digest would be that identical value, so the suppression row would join straight to a claim, and the pair would be two copies of one confirmation oracle rather than one.
- **One row makes every other row inert.** After a retired memory is hard-deleted its suppression row survives as the only trace of it. Deleting the single key row is then enough to make every surviving suppression digest permanently unusable for confirming a guess about content that is gone, which one row cannot do for an unkeyed hash.

### 8.3 Where it is enforced

At `SagaMemoryStore`'s insert path, inside the insert transaction, after the scope classifier has derived the memory's ownership and before the row is written. That is the one chokepoint every Saga write goes through, so extraction cannot reach around it and neither can any future writer.

`ISagaMemoryStore.InsertAsync` starts returning a written-or-suppressed outcome rather than a bare `Task`. `SagaExtractionService` logs the suppression and **still advances its watermark**: a deliberate rejection is not a failure, and treating it as one would make the same page re-extract forever on a retry ladder that can never converge.

The extraction service does not pre-check. A courtesy check before the write would be a second statement of the rule, and the second statement is the one that drifts.

### 8.4 Scope, and the boundary of "equivalent"

The digest binds the scope kind and Campaign, so retiring a memory inside one Campaign suppresses re-extraction inside that Campaign and nowhere else. The alternative would let a rejection made in one piece of work silently govern another the operator never had an opinion about — which is the reasoning Campaign-scoped retrieval already applies to what a turn may recall.

"Equivalent" means the same content under §8.1's definition. An extraction that rewords a conclusion produces different content and is not suppressed. That is a real boundary and is stated rather than implied: deterministic deduplication and dependency-aware supersession are a different capability with a different proof obligation.

## 9. Reinstatement

`arcanum memory saga reinstate <id>` and `POST /api/memory/saga/{id}/reinstate`. Embed first, then one transaction: clear `RetiredAtUtc`, delete that memory's suppression row, insert the embedding BLOB and the vec0 mirror, and append an Annals version binding the content again.

That version is a `Correct`. There is no fourth operation code and there cannot be one — `annal_versions.OperationCode` bakes its vocabulary into a `CHECK` SQLite cannot alter, on a table joined by three composite foreign keys and guarded by an append-only trigger, and adding a member would be a rebuild of the only record of what durable memory has claimed. A reinstatement *is* the claim being restated after a tombstone, which is what `Correct` means; the head moves from a retirement to a restatement at the next revision, which is exactly the one motion the head's validate trigger allows.

Refused when there is no such memory, when it is not retired, and when the embedding substrate is unavailable — a reinstatement that cannot embed would leave a memory the operator believes is back but that nothing can retrieve.

**Revisited.** Reinstating a memory that is not retired is not refused either, on §7.1's reasoning and in its shape: nothing is written, the result reports `NotRetired`, and the projection comes back. There is no `Saga.NotRetired` code anywhere in the vocabulary, because no caller can ever receive one — a code declared for an answer that is never given is a contract inviting a client to handle a case that does not exist. Three refusals remain: no memory with that id, a digest that disagrees, and an embedding substrate that cannot produce a vector.

## 10. Pin and unpin

`arcanum memory saga pin <id>` and `unpin <id>`, with `POST /api/memory/saga/{id}/pin` and `/unpin`, setting and clearing `PinnedAtUtc`.

**Retention planning** stops selecting a pinned memory as a candidate. **Retention execution** re-checks the same guard immediately before the delete, so a pin taken between a plan and its apply is honored rather than raced — a plan is a measurement, and a measurement that authorizes a later delete has to be re-proved at the moment it is used.

The plan carries a curation inventory: how many memories are pinned, and how many of those this plan would otherwise have pruned. A dry-run that silently omitted the exempted rows would tell an operator their retention rule reaches further than it does.

**A pin has nothing else to exempt a memory from today, and the design says so** rather than letting a test imply otherwise. There is no consolidation sweep and no decay pass anywhere in the installation; the one that is planned is a separate capability that does not exist yet. The state is durable, the shape is fixed, and the sweeps that arrive inherit it.

**A pin does not fight the operator.** Correction, retirement, reinstatement, and the existing explicit delete all still work on a pinned memory. A pin an operator has to argue with is a pin they stop using, and an unused pin protects nothing. What a pin binds is the automatic path, because that is the one that acts without being asked.

## 11. The detail view

`arcanum memory saga show <id>` and `GET /api/memory/saga/{id}` return, for one memory:

- the row itself — identity, content, when it was formed, its Session, tags, and source;
- its typed scope, and the Campaign when it has one;
- attachment provenance when the memory came from one, with the source reported as unavailable when it is gone;
- lifecycle: retired and when, pinned and when;
- retrieval eligibility as a typed reason;
- the Annals claim when one exists — the exact current version identity, its revision, its origin, its sensitivity, both timelines, and the version history behind it.

**Eligibility is a reason, not a boolean,** and it is read off the same rule retrieval uses rather than restated: eligible, retired, ownership unresolved, or embedding missing. "Ownership unresolved" is the honest answer for a memory whose owning Session's binding never resolved — it is retrievable in no scope at all, which is a different thing from being retired and a different thing from being broken.

**A memory with no claim is a first-class state.** That is what every row written before the Annals substrate existed looks like, and what every row written while `Arcanum:Features:Annals` was off looks like. The detail view says so rather than reporting an error, and a correction of such a memory opens its claim on the way through.

## 12. Surfaces

Six verbs under `arcanum memory saga`, beside `arcanum memory covenant` and `arcanum memory lexicon`, each naming exactly one store in its result and each confirming interactively or with `--yes`, matching the existing destructive-command contract. Six routes under `/api/memory/saga/`, authenticated, none on `/v1`.

`arcanum saga list | divine | delete | stats` and `/api/saga*` are untouched. Those are the store's own read-and-delete surface; `arcanum memory saga` is the curation surface that names one store, and the two answer different questions. The listing surfaces do gain the lifecycle fields, because a listing that could not tell a retired memory from a live one would make the operator open every row to find out.

## 13. Lifecycle

The two new tables join every path that already owns Saga data: the retention inventory, both memory-reset arms, the Campaign-scoped reset, and factory reset. A backup carries them without being told to, because the database component is a page-level copy of the whole encrypted file and a table joins a backup by existing; there is no list there to extend and nothing that could omit one. Retention pruning is the one owning path they stay out of, and deliberately: a suppression names no memory and has to outlive the one it describes, which is the whole reason it holds no memory identity.

The suppression key is cleared by whatever clears the suppressions, and generated again on the next retirement. Clearing it alone would leave rows that can never match again while still looking like evidence. The Campaign-scoped reset is the exception that proves the rule rather than a departure from it: it clears one Campaign's evidence rather than the installation's, and the key every other scope's digests are bound by stays.

**Deleting a Campaign removes neither its memories nor its suppressions, and that is a decision.** `CampaignRepository.DeleteAsync` clears the Session references and removes the Campaign row; the memories extracted inside that Campaign survive it and stay retrievable, exactly as they did before this slice. Adding destruction to a delete path would take data the operator never asked to lose, and the Campaign-scoped memory reset already exists for the operator who does want it.

## 14. Testing

Every acceptance test enters through the outermost production entry point — the mapped route or the registered CLI verb — and seeds nothing it asserts. A precondition is reached by driving the production write path: a memory to be corrected is written by the store's own insert, and a suppression is created by an actual retirement rather than by an inserted row.

Before the slice is called done, the behaviours the acceptance criteria name are broken in the source one at a time and the suite is confirmed to fail: the content-hash comparison, the embedding deletion that makes retirement structural, the suppression check at the insert chokepoint, the pin's exemption in the retention candidate query, and the pin's re-check at execution. For every branch a test proves, the production call site that reaches it is identified, because an optional parameter defaulting to null is where reachability quietly dies.

The suppression test that matters most is the end-to-end one: retire a memory through the route, then run extraction through its own service with the same conclusion, and assert the row did not come back — not that a helper returned true.

## 15. Closed inventories this change grows

- The Core schema version constant, its version-3 pin, and a version-3 fixture that freezes `saga_memories.sql`'s current text and removes the version-4 tables.
- `GrimoireSchemaTransitionResourceTests` — every new transition statement, pinned by name in install order.
- `GrimoireSchemaVersionChainTests` — the Core head version literal.
- The retention inventory's table list, both memory-reset arms, the Campaign-scoped reset, and factory reset. Pruning is not among them and a backup needs no list of its own; §13 says why.
- `ErrorCodes.Saga` — the new refusal codes.
- `docs/Arcanum.CommandMap.json`, regenerated, which a committed test diffs.
- `SettingDescriptors` and its coverage count, if a configuration key is added. None is planned.
