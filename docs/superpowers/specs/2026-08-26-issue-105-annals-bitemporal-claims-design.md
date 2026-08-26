# Issue #105: The Annals — Bitemporal Validity and Dependency-Aware Claims

**Status:** Approved in chat on 2026-08-26; implementation has not started.

**Branch:** `codex/issue-105-annals-bitemporal-claims`, cut from `long-term-memory`, merged back with `--no-ff`.

**Issue:** #105, an XL prerequisite blocker under #73, blocked by #82 (closed), #76 (closed), and #102 (closed), blocking #93, #97, #98, #101, #106, and #107.

**Names settled on the issue:** the concept is **The Annals**. A durable assertion is a **claim**; one immutable statement of it is a **claim version**; the guarded current pointer is a **head**; a bounded link from one version to an exact earlier version is a **dependency edge**. `Annal` is the table prefix and the namespace.

## 1. Objective

Give durable-memory versions explicit valid-time and transaction-time semantics, bounded dependency edges, and typed assertion origin, so that later consolidation and curation can express correction, supersession, and historical truth while prior evidence stays immutable.

The Annals is a substrate. This change alters no retrieval, no ranking, no prompt bytes, and no token accounting. With the feature gate off — its default — an installation behaves exactly as it does today, and the existing DCI regression suite proves it byte for byte.

## 2. Scope, settled before design

Four framing decisions were put to the operator and answered before any code was designed.

- **Name.** The Annals, per AGENTS.md convention 5 and the parent's first acceptance criterion.
- **Write scope.** Backfill **and** live write-through. A substrate whose only producer is a one-time upgrade sweep is a frozen snapshot of history, not a memory layer; every new Saga memory and every Lexicon write appends a claim version and advances a guarded head.
- **Stores.** Saga and Lexicon only, which is exactly what the acceptance criteria name. The Covenant already carries immutable identity, typed origin, versions, heads, and guard triggers of its own, and modelling it a second time here would create two answers to a question it has already answered. The Tapestry is derived data rebuilt from its corpus and holds no claims.
- **Gate.** The Core schema step and its backfill install unconditionally, because schema evolution is not optional. Appending claim versions is governed by a new `Arcanum:Features:Annals` key defaulting to `false`.

## 3. Governing constraints

- Raw SQL through the declarative schema tree, one object per file, `CREATE ... IF NOT EXISTS`, installed by `GrimoireSchemaInstaller`. No EF entity, no numbered migration, no compiled-model regeneration.
- Native AOT throughout: no reflection-based serialization, no dynamic type loading. Config POCOs use `{ get; set; }` and never `init`.
- Degrade the way §21.4 degrades. A store-level Annals failure must never fail an inference turn; it must, however, never silently succeed either — see §9.3 for the one place those two rules pull against each other and how it resolves.
- No new API route, no new CLI verb, no change to `Arcanum.CommandMap.json`. One new configuration key.
- Every new durable table joins the existing lifecycle: retention inventory, factory reset plans, memory reset, and pruning.
- Documentation in `docs/` names capabilities, never issues.

## 4. Considered approaches

### 4.1 Move memory content into versioned tables — rejected

Make `annal_versions` the authoritative content store and reduce `saga_memories` and `lexicon_entries` to projections of the current head, mirroring `covenant_versions` and `covenant_heads` exactly.

Rejected on two counts. It duplicates every memory's text in two tables that must then be kept in agreement forever, and two measurements of one quantity eventually disagree; and it puts operator-erasable content behind an append-only guard trigger, so `arcanum data reset-memory --scope saga` could no longer honour its promise without a dedicated erasure coordinator of the kind the Covenant needed. The acceptance criterion enumerates identity, origin, scope, sensitivity, valid time, and transaction time — content is conspicuously not on that list.

### 4.2 A separate capability tier at version 1 — rejected

Install the Annals as a fourth `GrimoireSchemaTransactionTier` alongside Core, Covenant Canonical, and Covenant Accelerator, so it begins at version 1 with no pinned fingerprint and no step to author.

Rejected because the mandated backfill has nowhere to live. `IGrimoireSchemaBackfill` is declared on a *version step*, and a tier at version 1 has no step. The only alternative home is `IGrimoireSchemaDataInitializer`, which runs to completion inside the tier's install transaction — an unbounded sweep over an arbitrarily large Saga store, which is precisely what the resumable backfill machinery exists to prevent. A separate tier also implies a separate failure domain, and the Annals cannot have one: its rows are meaningless without the Core rows they describe.

### 4.3 Core schema version 3, new tables only — selected

The four tables join the Core head tree, Core moves to version 3, and the step's backfill populates claims for rows written before the Annals existed. Because the step adds objects and edits none, `CREATE TABLE` stores its text verbatim and a fresh version-3 installation normalizes identically to one evolved from version 2 — the `ALTER TABLE` shaping hazard that governs `saga_memories.sql` and `lexicon_entries.sql` does not arise here.

## 5. Data model

### 5.1 `annal_claims` — identity

```sql
CREATE TABLE IF NOT EXISTS annal_claims (
    ClaimId TEXT NOT NULL PRIMARY KEY,
    SubjectStoreCode INTEGER NOT NULL CHECK (SubjectStoreCode IN (1, 2)),
    SubjectId TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_claims_subject
    ON annal_claims(SubjectStoreCode, SubjectId);

CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_claims_store_candidate
    ON annal_claims(ClaimId, SubjectStoreCode);
```

`SubjectStoreCode` is `1` for Saga and `2` for the Lexicon. `SubjectId` is the `saga_memories.Id` or `lexicon_entries.Id` that carries the claim's content.

The subject binding lives on the claim rather than on each version, and that is the load-bearing decision of the whole model. A Lexicon correction rewrites one row in place, so every revision of that claim names the same `lexicon_entries.Id`; had the binding been per version, a unique index over it would have refused the second revision, and without one two claims could quietly own the same row.

`ux_annal_claims_subject` is what makes the backfill idempotent: a batch selects subject rows for which no claim exists, so the corpus shrinks by exactly the work that committed, and a re-run after a lost commit selects the same rows again rather than writing a second claim for them.

### 5.2 `annal_versions` — one immutable statement

```sql
CREATE TABLE IF NOT EXISTS annal_versions (
    Sequence INTEGER PRIMARY KEY,
    VersionId TEXT NOT NULL,
    ClaimId TEXT NOT NULL REFERENCES annal_claims(ClaimId),
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    OperationCode INTEGER NOT NULL CHECK (OperationCode IN (1, 2, 3)),
    OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3, 4)),
    ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (0, 1, 2, 3)),
    CampaignId TEXT NULL,
    SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (0, 1)),
    ContentHash BLOB NULL CHECK (ContentHash IS NULL OR length(ContentHash) = 32),
    ValidFromUtc TEXT NOT NULL,
    ValidToUtc TEXT NULL,
    RecordedAtUtc TEXT NOT NULL,
    PredecessorVersionId TEXT NULL REFERENCES annal_versions(VersionId),
    SourceSessionId TEXT NULL
    -- plus the five table-level CHECK constraints enumerated immediately below
);
```

`Sequence` is an `INTEGER PRIMARY KEY`, which is SQLite's rowid alias, so the engine allocates it inside the insert statement and two concurrent writers cannot compute the same value. An explicit `MAX(Sequence) + 1` would race under the deferred transaction the Saga insert path opens, and the resulting unique-constraint abort is not a `SQLITE_BUSY` and would therefore not be retried.

The table-level checks encode every invariant the codes imply:

- `CHECK ((ScopeKindCode = 2 AND CampaignId IS NOT NULL) OR (ScopeKindCode <> 2 AND CampaignId IS NULL))` — a Campaign-scoped version names its Campaign, and no other kind borrows one.
- `CHECK ((OperationCode = 3 AND ContentHash IS NULL) OR (OperationCode <> 3 AND ContentHash IS NOT NULL))` — a retirement is a tombstone and binds to no content, exactly as a Covenant retirement carries neither content nor hashes.
- `CHECK ((Revision = 1 AND PredecessorVersionId IS NULL) OR (Revision > 1 AND PredecessorVersionId IS NOT NULL))` — revision one begins a claim; every later revision links to exactly one predecessor.
- `CHECK (ValidToUtc IS NULL OR ValidToUtc >= ValidFromUtc)` — a validity window never closes before it opens. Both columns are round-trip `"o"`-format UTC text, which orders lexicographically, so this is a real comparison and not a coincidence of formatting.
- `CHECK (OriginCode <> 4 OR SourceSessionId IS NULL)` — a version nobody attested cannot name a Session as its source.

Indexes: `UNIQUE (VersionId)`; `UNIQUE (VersionId, Sequence)` as the candidate key `annal_dependencies` carries a composite foreign key to; `UNIQUE (ClaimId, Revision)`; `UNIQUE (VersionId, ClaimId, Revision, OperationCode)` as the candidate key `annal_heads` carries its composite foreign key to; and `(ClaimId, RecordedAtUtc)` for reading one claim's history in order.

#### Typed assertion origin

| Code | Name | Meaning |
|---|---|---|
| 1 | `OperatorStated` | The operator said it. |
| 2 | `AgentAsserted` | A model wrote it through a tool call it chose to make. |
| 3 | `AgentExtracted` | Headless extraction inferred it from a finished transcript; no one chose to state it. |
| 4 | `SystemBackfilled` | An upgrade classified a row written before the Annals existed. Nobody attested it. |

This is the distinction the parent issue says curation and trust need: "the operator stated this" against "a model inferred this from a transcript". Code `4` is separate from the other three because a backfilled version is evidence of an upgrade, not of an assertion, and a later curation surface must be able to say so.

#### Operation

`1 Assert` opens a claim, `2 Correct` restates it, `3 Retire` ends it. This slice produces `Assert` and `Correct`; `Retire` is declared and constrained now so the curation issues that produce it inherit a shape they cannot contradict.

#### Bitemporality, and why only one of the two ends is stored

**Valid time** is what the claim says about the world. `ValidFromUtc` and `ValidToUtc` are both properties of the statement, set once when the version is written, and a version that says "true until March" is as immutable as one that says "true from January".

**Transaction time** is when Arcanum held the belief. `RecordedAtUtc` is stored. The end of transaction time is **derived, never stored**: a version's belief ends at the `RecordedAtUtc` of the version whose `PredecessorVersionId` names it, and is open when no such version exists.

That derivation is forced by the append-only guard trigger, and it is the better design regardless. A stored `SupersededAtUtc` would have to be written by updating a row that the guard forbids updating, and it would be a second measurement of a quantity the successor's own timestamp already states. Where a value is checked in two places, the second must read the first's result rather than mirror it.

### 5.3 `annal_heads` — the guarded current pointer

```sql
CREATE TABLE IF NOT EXISTS annal_heads (
    ClaimId TEXT NOT NULL PRIMARY KEY,
    SubjectStoreCode INTEGER NOT NULL CHECK (SubjectStoreCode IN (1, 2)),
    CurrentVersionId TEXT NOT NULL,
    CurrentRevision INTEGER NOT NULL CHECK (CurrentRevision > 0),
    CurrentOperationCode INTEGER NOT NULL CHECK (CurrentOperationCode IN (1, 2, 3)),
    UpdatedAtUtc TEXT NOT NULL,
    FOREIGN KEY (CurrentVersionId, ClaimId, CurrentRevision, CurrentOperationCode)
        REFERENCES annal_versions(VersionId, ClaimId, Revision, OperationCode),
    FOREIGN KEY (ClaimId, SubjectStoreCode) REFERENCES annal_claims(ClaimId, SubjectStoreCode)
);
```

Both foreign keys are composite, and that is the point. A plain reference to `VersionId` would let a head adopt a version belonging to another claim, or one whose revision and operation disagree with the head's own columns; a plain reference to `ClaimId` would let a head claim a store its claim does not belong to. `SubjectStoreCode` on the head is what lets a Saga-scoped reset delete Saga heads without joining, and the composite key is what stops it from lying.

`CREATE UNIQUE INDEX ux_annal_heads_current_version ON annal_heads(CurrentVersionId)` — one version is current for at most one claim.

### 5.4 `annal_dependencies` — bounded, deterministic, cycle-safe edges

```sql
CREATE TABLE IF NOT EXISTS annal_dependencies (
    DependentVersionId TEXT NOT NULL,
    DependentSequence INTEGER NOT NULL,
    DependencyVersionId TEXT NOT NULL,
    DependencySequence INTEGER NOT NULL,
    RelationCode INTEGER NOT NULL CHECK (RelationCode IN (1, 2, 3)),
    Ordinal INTEGER NOT NULL CHECK (Ordinal BETWEEN 1 AND 16),
    CreatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (DependentVersionId, DependencyVersionId),
    FOREIGN KEY (DependentVersionId, DependentSequence)
        REFERENCES annal_versions(VersionId, Sequence) ON DELETE CASCADE,
    FOREIGN KEY (DependencyVersionId, DependencySequence)
        REFERENCES annal_versions(VersionId, Sequence) ON DELETE CASCADE,
    CHECK (DependencySequence < DependentSequence)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_dependencies_dependent_ordinal
    ON annal_dependencies(DependentVersionId, Ordinal);

CREATE INDEX IF NOT EXISTS idx_annal_dependencies_dependency
    ON annal_dependencies(DependencyVersionId);
```

Relation codes are `1 Supersedes`, `2 DerivedFrom`, `3 Corroborates`.

**Cycle-safety is structural, not procedural.** Each edge carries both endpoints' sequences, each bound to its version by a composite foreign key so neither can be misstated, and `CHECK (DependencySequence < DependentSequence)` refuses any edge that does not point strictly backwards in allocation order. A cycle requires at least one edge that does not, so the table cannot hold one. There is no recursive CTE, no traversal, and no detector to get wrong — and no way for a future writer to bypass the check by taking a different code path, because the check is in the database.

Sequence reuse after deletion does not weaken this. Edges cascade away with the version they name, so at every instant every live edge satisfies the strict ordering, which is a directed acyclic graph by construction.

**Bounded** — `CHECK (Ordinal BETWEEN 1 AND 16)` with `UNIQUE (DependentVersionId, Ordinal)` caps one version at sixteen edges. The bound lives in the schema rather than in a writer, so the seventeenth edge is refused whatever produced it.

**Deterministic** — the ordinal is a stable total order over a version's edges, so two readers of the same claim see the same dependency list in the same order.

**Targets exact retained versions** — the composite foreign keys mean an edge can only ever name a version that exists, and `ON DELETE CASCADE` means an edge never outlives its endpoints.

### 5.5 Immutability guards

`annal_claims_guard_update`, `annal_versions_guard_update`, and `annal_dependencies_guard_update` abort every `UPDATE`. A correction is the next revision; an edge is rewritten by deleting the version that owns it.

`annal_heads_validate_update` allows a head to advance and nothing else: it aborts when `NEW.CurrentRevision <= OLD.CurrentRevision`, when `NEW.ClaimId <> OLD.ClaimId`, or when `NEW.SubjectStoreCode <> OLD.SubjectStoreCode`. A head is a pointer and is meant to move; what it must never do is move backwards or change what it is a pointer to.

**There is deliberately no delete guard.** The Covenant guards deletion because its versions are the evidence that makes an erasure claim checkable, and it pays for that with a dedicated erasure coordinator and two authorization functions. The Annals is evidence *about memory that still exists*: when the operator forgets a Saga memory, the claim describing it has nothing left to describe, and keeping it would leave a record pointing at content the operator asked to remove. Deletion follows the subject, in the subject's own transaction.

### 5.6 Referential cascade and deletion order

SQLite enforces an immediate foreign key as each row is deleted, not at the end of the statement, so a table that references itself cannot be emptied by one unordered `DELETE`. Deleting revision one before revision two, when revision two names it as predecessor, aborts. The cascade declarations and the deletion order below exist to make erasure a mechanical sequence rather than a puzzle every caller has to solve again.

| Reference | On delete |
|---|---|
| `annal_dependencies` → `annal_versions(VersionId, Sequence)`, both endpoints | `CASCADE` |
| `annal_versions.PredecessorVersionId` → `annal_versions(VersionId)` | `CASCADE` |
| `annal_versions.ClaimId` → `annal_claims(ClaimId)` | no action |
| `annal_heads` → `annal_versions(...)` and `annal_claims(...)` | no action |

Every erasure path therefore deletes in exactly one order: **heads, then versions, then claims**. Edges and predecessor chains fall away by cascade. The two references that carry no cascade are the two whose direction would otherwise let a delete start in the middle: a head must release its version before the version can go, and a version must go before the claim it belongs to.

This matches the belt-and-braces convention `SagaMemoryStore` already follows, which deletes `saga_memory_attachment_provenance` explicitly even though that table declares `ON DELETE CASCADE`. Relying on a pragma the connection sets, for a delete the operator asked for, is the wrong place to be economical.

## 6. Content binding

`AnnalContentDigest` (Core) computes the 32-byte SHA-256 a version binds to.

- Saga: `SHA-256(UTF8(content))`.
- Lexicon: `SHA-256(UTF8(type + "\u001F" + factsText))`, where `factsText` is the same newline-joined projection the FTS index already stores. The unit separator is what stops a type and a fact set from being confused across the boundary — without it, a type ending in text that a fact begins with would hash identically to a different pair.

The digest is a binding, not a copy. It proves which bytes a version was written about without being able to reconstruct them, which is what keeps erasure honest.

It is also what makes Lexicon write-through deterministic. `LexiconService.UpsertCoreAsync` merges incoming facts with existing ones, and a merge that adds nothing new produces an unchanged fact set. Comparing the freshly computed digest against the head version's `ContentHash` decides the question: equal means no version is appended and the head does not move, different means a `Correct` revision is appended. Without that comparison every repeated `scribe_lexicon` call restating a known fact would append a revision recording no change.

## 7. Write-through

`AnnalsClaimWriter` (Infrastructure, `Data/Annals/`) owns every insert into the four tables and is the single implementation all three producers share. It takes the caller's live `DbConnection` and `DbTransaction`, mirroring `SagaMemoryScopeClassifier.ResolveForSessionAsync`, so a claim commits or rolls back with the memory it describes and no second transaction can interleave.

One writer rather than three, because three would be three ideas of what a revision means, and a claim written by the live path would eventually disagree with one written by the sweep about the same question.

### 7.1 Saga

`SagaMemoryStore.InsertCoreAsync` already opens a transaction and resolves the memory's scope from the owning Session's canonical binding. The Annals append goes inside that transaction, after the `saga_memories` insert and before the embedding writes, and reuses the scope that was just derived rather than re-deriving it.

Origin is `AgentExtracted`: Saga has no operator write path and no `scribe_saga` tool, so every row is a headless extraction's inference from a finished transcript. Operation is `Assert` at revision one — Saga has no update path, so a Saga claim never reaches revision two in this slice. `ValidFromUtc` and `RecordedAtUtc` are both the memory's own `CreatedAt`. `SensitivityCode` is `None`; a labelled Saga artifact is refused deletion by the existing guard rather than carried here.

### 7.2 Lexicon

`LexiconService.UpsertCoreAsync` already runs its read-merge-write inside `BEGIN IMMEDIATE`, which serializes concurrent `scribe_lexicon` appends. The Annals append goes inside it.

The insert arm appends revision one, `Assert`. The update arm computes the digest, compares it with the head version's, and — when they differ — appends the next revision as `Correct` with a `Supersedes` dependency edge at ordinal 1 pointing at the outgoing head version, then advances the head. Origin is `AgentAsserted`, because a Lexicon write is a tool call a model chose to make rather than something extracted from a transcript behind its back.

Scope maps from `LexiconScope`: the empty-string global tier is `Global`, and a Campaign scope is `Campaign` with that Campaign named. A Lexicon entry is never `Unclassified` or `LegacyUnresolved` — `ScopeCampaignId` is `NOT NULL DEFAULT ''`, so every row has always had an unambiguous tier.

`LexiconService` gains an `IOptionsMonitor<ArcanumSettings>` constructor parameter to read the gate.

### 7.3 Deletion

Every path that removes a subject row removes its claim in the same transaction: `SagaMemoryStore.DeleteAsync` and `DeleteAllAsync`, `LexiconService.DeleteByNameAsync`, the Saga and Lexicon arms of retention pruning, and both factory-reset table plans. Versions and edges follow the claim.

Deletion is not gated. A claim written while the Annals was enabled must be removable after it is disabled, or disabling the feature would strand rows that no surface can reach and no reset can clear.

## 8. Schema evolution

Core moves to version 3. `GrimoireSchemaVersionChains.CoreSchemaVersion` becomes `3`, and the chain gains one step.

The pin for the version the step leaves is Core's published version-2 source-definition fingerprint:

```
CEFA40F472EB4815F13B257327F8FA78C00B6F671C78DCAB89E4A38B40646F2C
```

This value was read out of the shipped tree **before any file under `Data/Schema/` was edited**. Nothing can recompute it afterwards, because the tree that produced it will no longer exist.

Transition statements live under `Data/Schema/Transitions/V3/`, one statement per file, ordinal-ordered, and each one is the corresponding head file's text character for character. That identity is what makes a fresh version-3 install and one evolved from version 2 normalize to the same `sqlite_master` text. Because the step only adds objects, the `ALTER TABLE` shaping hazard documented in `saga_memories.sql` does not arise: `CREATE TABLE` stores its statement verbatim.

`CoreSchemaVersionTwoFixture` reconstructs the version-2 head tree as the shipped `CoreObjects` with the four `annal_*` objects removed, and a test hashes that reconstruction and compares it against the pin. A wrong pin fails there, in this repository, rather than by refusing every operator's version-2 installation with `SourceDefinitionMismatch`.

`CoreSchemaVersionOneFixture` needs no change: the step to version 3 edits neither `saga_memories.sql` nor `lexicon_entries.sql`, so the version-1 reconstruction still describes version 1.

### 8.1 `MemoryAnnalsBackfill`

Declared on the version-3 step, named `memory-annals-claims`, bounded at 200 rows a batch — the same bound `SagaMemoryCampaignScopeBackfill` chose, and for the same reason: small enough that one batch's transaction never holds the database while an operator waits for a turn.

Each batch reads one bounded page of Saga memories with no claim, then, if room remains in the batch, one bounded page of Lexicon entries with no claim. Every row read is claimed in the same transaction, so the corpus shrinks by exactly the work that commits and a crash before commit simply re-selects the same rows. There is no cursor, for the same reason the Campaign-scope sweep has none: the predicate is its own position.

The read completes before the first write. The selecting query filters on the absence of rows in a table the write inserts into, and writing to a table an open cursor is still filtering against is the case SQLite leaves undefined.

**Conservatism.** Every backfilled version is `OriginCode = SystemBackfilled` and copies its subject's own scope verbatim. A Saga row at `Unclassified` stays `Unclassified`; a row at `LegacyUnresolved` stays `LegacyUnresolved`. Neither is promoted to `Global`, which is precisely the laundering the acceptance criterion forbids: an installation-global claim is retrievable inside every Campaign, and a memory whose ownership was never resolved has no authority to become one.

`ValidFromUtc` and `RecordedAtUtc` are the subject's own timestamp — `saga_memories.CreatedAt`, `lexicon_entries.UpdatedAt` — rather than the moment of the sweep, because that is when Arcanum actually first held the claim. Stamping the upgrade's clock on a six-month-old memory would make transaction time useless for exactly the historical questions it exists to answer.

The backfill runs regardless of the feature gate. Gating it would let the step's DDL commit while its declared sweep never drains, which the transition journal reads as a half-evolved tier.

## 9. Feature gate and degradation

### 9.1 The key

`Arcanum:Features:Annals`, a `bool { get; set; }` on `FeatureSettings`, default `false`. `init` is not usable: the configuration binding generator silently skips `init`-only members, which would leave the feature permanently off while `arcanum.json` said otherwise.

### 9.2 The gap the gate creates, stated plainly

A memory written while the Annals is disabled carries no claim, and enabling the gate later does not give it one — the backfill runs once, at the version step, and nothing re-runs it. This is a real limitation and belongs in the known-limitations section rather than in a footnote. Consolidation and curation, when they arrive, will need to treat an unclaimed subject row as a first-class state rather than as an error.

### 9.3 Failure

A memory store must never fail an inference turn, and an Annals write is not important enough to break one. But a swallowed Annals failure inside the memory's own transaction would leave a committed memory with no claim, silently, which is the state §9.2 already identifies as hard to recover.

The resolution is that the Annals write is **inside** the subject's transaction and shares its fate. If the claim cannot be written, the memory is not written either, and the caller sees the failure the memory store already surfaces for a failed insert — for Saga, a background extraction that requeues on its existing ladder with the watermark unadvanced; for the Lexicon, `ErrorCodes.Lexicon.WriteFailed`, which `scribe_lexicon` already converts to a tool-result string. Neither reaches the turn as an exception, so the degradation contract holds without a silent half-write.

## 10. Lifecycle

`RetentionDataClass.Annals = 29`, inventoried and never aged out on its own timer, exactly as `Covenant` is: it has no `RetentionSettings` property and `DataRetentionSettingsCatalog.ResolveRule` resolves it to null. Its lifecycle is its subject's, and a rule that could age a claim out from under a live memory would leave the memory unexplained.

The four tables join `FactoryPlanTables` and `FactoryDeletionTables` as `FactoryRecordKind.Derived` under that class, ordered in the deletion list so edges, versions, heads, and claims fall before the Saga and Lexicon rows they describe.

**No new `MemoryResetScope` member.** Resetting the Annals independently would leave every head orphaned from the memory it points at, and the operator has no mental model of "the Annals" as a store they can clear. `--scope saga` clears Saga claims and leaves Lexicon claims standing; `--scope lexicon` does the reverse. Both are exact, because `annal_heads.SubjectStoreCode` and `annal_claims.SubjectStoreCode` partition the tables cleanly.

Retention pruning counts an aged subject row's claim rows in the plan's derived count, so a rehearsal reports what will actually be removed.

## 11. Contracts and placement

**Core — `RetroDownfall.Arcanum.Core/Annals/`**

| Type | Role |
|---|---|
| `AnnalSubjectStore` | `Saga = 1`, `Lexicon = 2`. |
| `AnnalOperation` | `Assert = 1`, `Correct = 2`, `Retire = 3`. |
| `AnnalOrigin` | `OperatorStated = 1`, `AgentAsserted = 2`, `AgentExtracted = 3`, `SystemBackfilled = 4`. |
| `AnnalDependencyRelation` | `Supersedes = 1`, `DerivedFrom = 2`, `Corroborates = 3`. |
| `AnnalLimits` | `MaxDependenciesPerVersion = 16`, matching the schema check. |
| `AnnalContentDigest` | The two digest computations of §6. |
| `AnnalClaimVersion` | A read projection: identity, origin, operation, scope, sensitivity, both validity ends, recorded-at, derived transaction-time end. |
| `IAnnalsStore` | Read port: claim by subject, version history for a claim, dependency edges for a version. |

**Infrastructure — `Data/Annals/`**

| Type | Role |
|---|---|
| `AnnalsClaimWriter` | The shared append used by both stores and the backfill. |
| `AnnalsStore` | `IAnnalsStore` over the scoped `ArcanumDbContext` connection. |

**Infrastructure — `Data/Schema/`**

`Tables/annal_claims.sql`, `Tables/annal_versions.sql`, `Tables/annal_heads.sql`, `Tables/annal_dependencies.sql`, one file per guard trigger under `Triggers/`, `Transitions/V3/*.sql`, and `MemoryAnnalsBackfill.cs`.

The read port exists because the curation, recall, and delegation issues this one blocks all need to read claim history, and a port added later would be a port added without a consumer in sight. It is exercised in this slice by the tests that verify write-through, so it is not shipped untested; it is not, in this slice, reachable from an API route, and §12 says so in the documentation rather than implying otherwise.

## 12. Documentation

| Document | Change |
|---|---|
| `Arcanum.DESIGN.md` §5.4.4 | The four tables join the persistence inventory. |
| `Arcanum.DESIGN.md` §5.4.7 | `Annals` joins the retention taxonomy as an inventoried, never-aged class, beside `Covenant`. |
| `Arcanum.DESIGN.md` §10.6.1 | A row in the attachment-derived promotion policy table: a claim inherits its subject's promotion decision and can authorize nothing on its own. |
| `Arcanum.DESIGN.md` §21.4 | The degradation matrix gains the Annals row of §9.3. |
| `Arcanum.DESIGN.md` §21.5 | The §9.2 gap, stated plainly. |
| `Arcanum.DESIGN.md` §21.12 (new) | The Annals: model, bitemporality, the derived transaction-time end, structural cycle-safety, origins, write-through, the backfill's conservatism, and what is deliberately absent. |
| `Arcanum.Command.Reference.md` | `arcanum data status` reports the new class; no new verb. |
| `Compendium.README.md` | `Arcanum:Features:Annals`. |
| `README.md` | The metaphor table gains The Annals; the durable-memory status paragraph records what landed. |

Documentation in `docs/` names capabilities and never issues. `Arcanum.API.md` is untouched: no route changes.

## 13. Test plan

Every test enters through the outermost production entry point, seeds nothing it intends to assert, and passes only values a caller under `src/` actually supplies.

1. **The version-2 pin is right.** `CoreSchemaVersionTwoFixture` reconstructs the version-2 tree; a test hashes it and compares it with the pin the shipped chain carries. A wrong pin fails here rather than against an operator's database.
2. **The upgrade journey.** Install version 2 through the real installer using the version-2 chain set, write Saga memories and Lexicon entries through `ISagaMemoryStore` and `ILexiconService`, then hand the same installer the shipped version-3 chain. Assert every subject row has exactly one claim, one version at revision one, and one head; assert an `Unclassified` Saga memory's version is `Unclassified` and **not** `Global`; assert every backfilled version's origin is `SystemBackfilled` and its `RecordedAtUtc` is the subject's own timestamp and not the sweep's.
3. **The backfill is resumable and idempotent.** Drive it to completion in more than one batch, then run the installer again and assert no second claim appears.
4. **Write-through, gate on.** Insert a Saga memory through `ISagaMemoryStore`; assert claim, version, head, origin `AgentExtracted`, and a content hash equal to the digest of the content actually stored.
5. **Write-through, gate off.** The same insert appends nothing, and the memory is written exactly as it is today.
6. **Lexicon correction.** Two `scribe_lexicon` upserts of one entity with different facts, through the MCP tool surface, produce revision one `Assert` and revision two `Correct`, a `Supersedes` edge at ordinal 1, and an advanced head — and revision one's row is byte-identical to what it was before the second call.
7. **Lexicon no-op.** Re-scribing an identical fact set appends no revision and leaves the head where it was.
8. **Cycle-safety is structural.** An edge whose dependency sequence is not strictly smaller than its dependent's is refused by the database, exercised by writing the edge directly rather than through the writer, because the claim under test is that the *schema* refuses it.
9. **Boundedness.** A seventeenth edge on one version is refused.
10. **Immutability.** `UPDATE` on `annal_versions`, `annal_claims`, and `annal_dependencies` each abort. A head update that lowers the revision aborts; one that raises it succeeds.
11. **Erasure is exact.** Deleting one Saga memory removes its claim, versions, and edges and leaves every other claim standing. `reset-memory --scope saga` leaves no Saga-subject row in any `annal_*` table and leaves Lexicon claims untouched; `--scope lexicon` is the mirror. Factory reset leaves all four tables empty.
12. **Gate-off byte identity.** The existing DCI golden-digest suite passes unchanged, which is the parent's standing criterion.

### 13.1 Mutation check before commit

Three production behaviours the acceptance criteria name are broken in the source, and the suite must fail for each:

- Replace the backfill's scope copy with a constant `Global` — test 2 must fail.
- Delete the `CHECK (DependencySequence < DependentSequence)` from the schema file — test 8 must fail.
- Make the Lexicon update arm append a revision unconditionally — test 7 must fail.

A test that survives its mutation is a test that proves nothing, and the reachability question is separate: for every branch a test proves, the production call site that reaches it must be confirmed to supply the values the branch needs. An optional parameter defaulting to null is where reachability quietly dies.

## 14. Out of scope

Deliberately absent from this slice, and named so no reader infers otherwise:

- No retrieval, ranking, or admission change. Nothing reads a head to decide what a turn recalls.
- No deduplication, supersession sweep, decay, or reinforcement.
- No API route and no CLI verb over claim history.
- No operator correction, retirement, or pinning surface.
- No `Retire` producer. The operation is declared and constrained; nothing writes one yet.
- No claims over the Covenant or the Tapestry.

## 15. Incidental correction

`GrimoireSchemaCatalog.TransitionStatements` carries a remark reading "Empty today: no tier has left version 1", which stopped being true when Core reached version 2. It is corrected in this change to describe the loader's actual state.
