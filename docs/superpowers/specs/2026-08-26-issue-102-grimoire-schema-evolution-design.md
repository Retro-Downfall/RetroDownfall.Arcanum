# Issue #102: Resumable Raw-SQL Feature-Schema Evolution and Backfills

**Status:** Approved in chat on 2026-08-26; implementation has not started.

**Branch:** `codex/issue-102-schema-evolution`, cut from `long-term-memory`, merged back with `--no-ff`.

**Issue:** #102, a foundation blocker under #73, blocked by #81 (closed), blocking #76, #77, #93, #91, #96, #97, #98, #100, and #105.

## 1. Objective

Give the declarative raw-SQL schema tree a code-owned way to move an already-installed capability tier from one integer version to the next, including a data backfill that is bounded, checkpointed, idempotent, restart-safe, and incapable of advancing past uncommitted work. Every condition this cannot honor — a newer installed version, definition drift at the same version, unknown objects, a catalog and its metadata disagreeing about version, and an interrupted transition — resolves to a typed, content-free, fail-closed tier health rather than a guess.

The engine ships with every shipped tier still at version 1. No production version step and no production backfill exists when this lands; later durable-memory features author the first ones.

## 2. The defect this closes

`GrimoireSchemaInstaller.ClassifyExistingAsync` refuses an installed version above the manifest's and a same-version source-fingerprint disagreement. When the installed version is **below** the manifest's it returns `null`, which means proceed. Proceeding runs the head tree's `CREATE ... IF NOT EXISTS` statements — which cannot alter an existing table — and then `WriteMetadataAsync` stamps the manifest's version over the row regardless.

Today that arm is unreachable, because `GrimoireSchemaManifestBuilder.CovenantSchemaVersion` is `1` and every tier is built with it. The first tier to ship a version 2 turns it into a silent false upgrade: the metadata claims v2, the catalog is still v1, no backfill ran, and every later inspection compares the v2 manifest against a v1 catalog and reports `InstalledCatalogDrift` with nothing to say why. That fall-through is where the engine goes.

## 3. Governing constraints

- Raw SQL only, through the declarative tree. No EF entity, no numbered migration, no compiled-model regeneration, no `Database.MigrateAsync`.
- One object per file, named after the object, for head objects. Transition statements are one **statement** per file, named after the step.
- Native AOT throughout: no reflection-based serialization, no dynamic type loading, no filesystem dependency at install time. Schema and transition SQL are embedded resources.
- Every new diagnostic value is closed and content-free. No SQL text, path, exception message, or secret-derived value reaches a health code, a log field a diagnostic surfaces, or a journal column.
- Core keeps its distinct failure domain: a Core refusal still throws and aborts startup. A Core tier that is merely mid-transition does not.
- No new public API route, no new CLI verb, no new configuration key, no change to `Arcanum.CommandMap.json`.
- The feature adds no behavior to an installation whose tiers are all at head, which is every installation this build can produce. Existing suites must pass unchanged.

## 4. Considered approaches

### 4.1 Version-scoped source trees — rejected

Keep a complete `V1/`, `V2/`, … copy of each tier's object files, so every version owns a full closed manifest and any version can be independently inspected.

Rejected because it duplicates every unchanged object once per version, and because "the schema tree is the single source of truth" stops being true the moment two trees describe the same object. A reviewer reading `covenant_entries.sql` would have to know which of N copies is live.

### 4.2 Transition steps expressed in C# — rejected

Express each version step as a C# type emitting SQL strings.

Rejected because it puts DDL back into C#, which the declarative tree exists to remove, and because the issue's second acceptance criterion exists specifically to protect the one-object-per-file rule.

### 4.3 Head tree plus declared transition steps — selected

The object tree **is** the head version. Moving an older installed version to head is an ordered, contiguous, code-owned chain of steps; each step's DDL is one statement per file under a `Transitions/` subtree beside the tier's objects, and a step may name exactly one resumable backfill. Historical versions are recognized by a **pinned source-definition fingerprint** carried on the step that leaves them, not by retaining their sources. A fresh install runs no step at all, because the head tree's `IF NOT EXISTS` statements build the head shape directly.

## 5. The version chain

### 5.1 `GrimoireSchemaVersionStep`

```csharp
internal sealed record GrimoireSchemaVersionStep(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    int FromVersion,
    int ToVersion,
    string FromSourceDefinitionFingerprint,
    IReadOnlyList<GrimoireSchemaTransitionStatement> Statements,
    IGrimoireSchemaBackfill? Backfill);
```

`FromSourceDefinitionFingerprint` is the uppercase 64-character value the tier's head tree produced **at** `FromVersion`. It is a pinned literal captured when the step is authored, in the same spirit as `CovenantAcceleratorSyntheticManifest`'s pinned shadow DDL: the tree that produced it no longer exists, so nothing can recompute it, and a change to it is a reviewed change rather than an absorbed one.

`ToVersion` is always `FromVersion + 1`. A step that skipped a version would make the chain's ordering unverifiable.

### 5.2 `GrimoireSchemaTransitionStatement`

```csharp
internal sealed record GrimoireSchemaTransitionStatement(
    string ResourcePath,
    int Ordinal,
    string Name,
    string Sql);
```

A dedicated record rather than a reused `GrimoireSchemaObject`, because a transition statement has no schema category and is never installed by a converge. Sharing the type would let one be mistaken for the other in exactly the place where that mistake is unrecoverable.

`ResourcePath` is carried for the same reason `GrimoireSchemaObject` carries it: a diagnostic naming a file a reader can open is worth more than one naming a table.

### 5.3 `GrimoireSchemaVersionChain`

A sealed class per `(family, tier)` holding the head manifest, the head objects, and the ordered steps. Its constructor is the validating boundary and rejects:

- a step whose family or tier disagrees with the chain's;
- a step where `ToVersion != FromVersion + 1`;
- steps that are not contiguous starting at `1 → 2`;
- a step count that is not `HeadVersion - 1`;
- a last step whose `ToVersion` is not `HeadVersion`;
- a `FromSourceDefinitionFingerprint` that is not 64 uppercase hexadecimal characters;
- a step with no statements, or with duplicate statement ordinals;
- two steps naming the same backfill.

Members:

- `HeadVersion => HeadManifest.Version`
- `SourceDefinitionFingerprintFor(int version)` — the head manifest's fingerprint when `version == HeadVersion`, otherwise the pin on the step leaving `version`.
- `TryGetStep(int fromVersion, out GrimoireSchemaVersionStep step)`

### 5.4 `GrimoireSchemaVersionChainSet`

Holds exactly one chain per transaction tier, rejects a duplicate or a missing tier, and exposes `ForTier`. It is an injected constructor dependency of `GrimoireSchemaInstaller` with a DI-registered default built from `GrimoireSchemaManifests`, `GrimoireSchemaCatalog`, and the catalog's transition resources.

`GrimoireSchemaManifestBuilder.CovenantSchemaVersion` is replaced by three per-tier constants — `CoreSchemaVersion`, `CovenantCanonicalSchemaVersion`, `CovenantAcceleratorSchemaVersion`, all `1`. One shared constant has always supplied the Core manifest's version as well as both Covenant tiers', which both misnames it and makes it impossible to move one tier's version without moving all three. That is exactly the constant this feature makes load-bearing.

Each step's `FromSourceDefinitionFingerprint` and each step's optional backfill are declared in pinned tables beside the chain factory, keyed by `(tier, toVersion)`. Both are empty today. The factory throws when a transition resource exists with no matching pin, so a step cannot ship without the value that recognizes the version it leaves.

Injecting the set rather than reading `GrimoireSchemaManifests` statically is what makes multi-version behavior testable through the production entry point. A test installs a synthetic version 1 through the real `InstallAsync`, then hands the same installer the same tier's two-version chain and calls `InstallAsync` again. No test hand-seeds a `grimoire_feature_schemas` row, and no test asserts a precondition it wrote itself.

## 6. Transition resources

### 6.1 Path grammar

Transitions live beside the objects of the tier they evolve, mirroring how the object tree already separates Core from a capability:

```
Data/Schema/
  Tables/ FullTextSearch/ Triggers/ Views/      # Core head objects
  Transitions/
    V2/
      010_add_entries_campaign_id.sql           # one statement, named after the step
      020_backfill_index.sql
  Capabilities/Covenant/Canonical/
    Tables/ Triggers/                           # head objects
    Transitions/
      V2/
        010_add_covenant_entries_validity.sql
```

The directory version is the step's `ToVersion`. The filename ordinal is the install order **within** that step; duplicate ordinals throw at chain construction rather than installing in an arbitrary order.

`GrimoireSchemaCatalog` gains the loader. The path decoder already throws on an unrecognized segment where a category is expected; `Transitions` becomes a recognized segment that routes the resource to the transition collection instead of to a tier's object list. `{{EmbeddingDimensions}}` resolution and the unresolved-placeholder refusal apply to transition SQL exactly as they apply to object SQL.

### 6.2 Transition resources are excluded from every source fingerprint

`CanonicalSchemaFingerprint`, `CoreSchemaFingerprint`, `CovenantCanonicalSchemaFingerprint`, and `CovenantAcceleratorSchemaFingerprint` cover head objects only.

This is load-bearing and gets its own test. If a transition resource entered a tier's source fingerprint, then authoring the very step that upgrades version 1 to version 2 would change the fingerprint recorded **for version 1**, and every installation at version 1 would refuse with `SourceDefinitionMismatch` before the step it needs could run. The feature would break itself on its first use.

### 6.3 Step DDL need not be idempotent

Head objects must be `CREATE ... IF NOT EXISTS`, because a converge re-runs them on every start. A step's statements run inside a single transaction with the journal write that records the step's completion, so a step either fully applies or leaves nothing behind. `ALTER TABLE ... ADD COLUMN`, which has no `IF NOT EXISTS` form, is therefore a legal step statement. Nothing re-runs a committed step.

## 7. Backfills

### 7.1 Contract

```csharp
internal interface IGrimoireSchemaBackfill
{
    string Name { get; }

    int MaxRowsPerBatch { get; }

    Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken);
}

internal sealed record GrimoireSchemaBackfillBatch(
    string? NextCursor,
    int RowsProcessed,
    bool IsComplete);
```

`Name` is stable, 1 to 64 characters, and recorded in the journal so a resumed run can prove the pending backfill is the one this binary declares.

The implementation contract, stated in the interface's own documentation because it is not enforceable by a signature:

- Write only through the supplied transaction. Never commit, roll back, open a second connection, or retry.
- A batch must process at most `MaxRowsPerBatch` rows.
- A batch must be safe to re-run from the last committed cursor and produce the same durable effect, because a crash between the batch's work and its commit is indistinguishable from the batch never running.
- `IsComplete` means the corpus is drained. A complete batch may still report rows and a cursor; the cursor is discarded with the journal row.

### 7.2 Why the cursor advance is not a separate write

The coordinator writes `NextCursor` into the journal row **inside the same transaction** as the batch's data writes, and commits once. That single fact is the whole of "never advances past uncommitted work": there is no ordering to get wrong, no second statement that can fail after the first succeeded, and no window in which the cursor describes work that did not commit.

## 8. The transition journal

One new Core table, one file, `Data/Schema/Tables/grimoire_schema_transitions.sql`:

```sql
CREATE TABLE IF NOT EXISTS grimoire_schema_transitions (
    FamilyCode INTEGER NOT NULL,
    TransactionTierCode INTEGER NOT NULL,
    FromVersion INTEGER NOT NULL CHECK (FromVersion > 0),
    TargetVersion INTEGER NOT NULL CHECK (TargetVersion > 0),
    CompletedThroughVersion INTEGER NOT NULL CHECK (CompletedThroughVersion > 0),
    TargetSourceDefinitionFingerprint TEXT NOT NULL CHECK (length(TargetSourceDefinitionFingerprint) = 64),
    BackfillName TEXT NULL CHECK (BackfillName IS NULL OR length(BackfillName) BETWEEN 1 AND 64),
    BackfillCursor TEXT NULL CHECK (BackfillCursor IS NULL OR length(BackfillCursor) BETWEEN 1 AND 256),
    BackfillRowsProcessed INTEGER NOT NULL CHECK (BackfillRowsProcessed >= 0),
    Revision INTEGER NOT NULL CHECK (Revision >= 0),
    LastDurableErrorCode TEXT NULL CHECK (LastDurableErrorCode IS NULL OR length(LastDurableErrorCode) BETWEEN 1 AND 64),
    StartedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (FamilyCode, TransactionTierCode),
    CHECK (TargetVersion > FromVersion),
    CHECK (CompletedThroughVersion >= FromVersion AND CompletedThroughVersion < TargetVersion),
    CHECK (BackfillCursor IS NULL OR BackfillName IS NOT NULL)
);
```

### 8.1 There is no phase column

The state of a run is fully determined by two columns that already have to exist:

| `CompletedThroughVersion` | `BackfillName` | Meaning |
|---|---|---|
| `c` | `NULL` | Everything through version `c` is durably complete. The DDL for step `c → c+1` has not run. |
| `c` | a name | The DDL for step `c → c+1` committed. That step's backfill is draining and has reached `BackfillCursor`. |

A phase column would be a third statement of the same fact, and the repository has already paid for two measurements of one quantity disagreeing. `CompletedThroughVersion < TargetVersion` is a table `CHECK`, because a completed run writes the metadata row and deletes the journal row in one transaction — a row saying the run is finished is a row that should not exist. Completing the **last** step therefore never writes `CompletedThroughVersion = TargetVersion`; it finishes the run instead, per §10.1.

`Revision` is a compare-and-swap guard. A host coordinator and a CLI bootstrap can both hold the encrypted file, and every journal advance is conditional on the revision it read. The loser fails its transaction and retries on the next pass rather than advancing a cursor twice.

### 8.2 Why the metadata version does not move per step

`grimoire_feature_schemas.SchemaVersion` advances exactly once per run, at the end, together with the installed-catalog fingerprint produced by a full inspection against the head manifest. The metadata row therefore keeps one unambiguous meaning: *this tier is completely at this version and was validated there.*

The alternative — advancing metadata per step — would need an installed-catalog fingerprint for an intermediate version, and there is no manifest for a version whose sources no longer exist. Writing a stale fingerprint, writing a placeholder, or making the column nullable would each make the one column that answers "is this catalog the one I recorded" answer it sometimes.

### 8.3 What is not validated, stated plainly

An intermediate version's catalog is **not** independently validated. Validation happens once, against the head manifest, after the final step of the run. This is a deliberate consequence of not retaining historical source trees, and it is bounded by three facts: every step is a closed, ordered, code-owned list of statements; every step is atomic; and the terminal state is fully inspected against a closed manifest before any metadata is written.

## 9. Classification and health

### 9.1 New `GrimoireSchemaTierHealth` values

```
TransitionIncomplete  = 7
TransitionUnresumable = 8
MixedCatalogVersions  = 9
```

- **`TransitionIncomplete`** — a journaled run this binary can finish has not finished. The tier is not healthy, so the capability is unavailable and a dependent tier reports `DependencyUnavailable`. It is **not** a refusal: for Core it does not throw, because a Core tier whose backfill aborts startup could never run that backfill.
- **`TransitionUnresumable`** — a journal row this binary cannot finish. Fail-closed, and a refusal for Core.
- **`MixedCatalogVersions`** — the metadata row and the catalog disagree about version with no journal row to explain it.

`GrimoireSchemaInspectionFailure` is unchanged. Unknown objects continue to surface as `UnexpectedObject` and therefore `InstalledCatalogDrift`.

### 9.2 The decision table

`GrimoireSchemaEvolutionPlanner` is a pure decision over the metadata row, the journal row, and the chain. It performs no I/O, so every arm is directly testable.

| Observed | Decision |
|---|---|
| No metadata row and no owned object present | `FreshInstall` at head |
| No metadata row, owned objects present | `Refuse(MetadataMissing)` |
| Metadata version > head | `Refuse(IncompatibleNewerVersion)` |
| Metadata version == head, recorded fingerprint == head fingerprint, no journal row | `Converge` |
| Metadata version == head, recorded fingerprint != head fingerprint | `Refuse(SourceDefinitionMismatch)` |
| Metadata version < head, recorded fingerprint != the chain's pin for that version | `Refuse(SourceDefinitionMismatch)` |
| Metadata version < head, pin matches, no journal row, catalog already validates at head | `Refuse(MixedCatalogVersions)` |
| Metadata version < head, pin matches, no journal row, catalog does not validate at head | `BeginRun(from: metadata version, target: head)` |
| Journal row present and every resumability check passes | `ResumeRun(...)` |
| Journal row present and any resumability check fails | `Refuse(TransitionUnresumable)` |

`MixedCatalogVersions` is reachable through a restored or hand-edited database whose catalog was advanced without its metadata. Stamping head there would be precisely the hole this feature exists to close, because nothing proves the skipped versions' backfills ever ran.

### 9.3 Resumability checks

A journal row is resumable only when all of the following hold. Any failure is `TransitionUnresumable`, with the failing check recorded as a closed code.

- `TargetVersion == chain.HeadVersion`
- `TargetSourceDefinitionFingerprint == chain.HeadManifest.SourceDefinitionFingerprint`
- `FromVersion == metadata row's SchemaVersion`
- The chain has a step leaving `CompletedThroughVersion`
- `BackfillName` is null, or names exactly the backfill that step declares

The fingerprint check is what stops a binary swap mid-run from finishing a run that a different head defined.

## 10. Execution

Five types, each with one job.

| Type | Owns |
|---|---|
| `GrimoireSchemaEvolutionPlanner` | The decision. Pure; no database. |
| `GrimoireSchemaTransitionJournal` | Reading, inserting, revision-checked advancing, and deleting the journal row. |
| `GrimoireSchemaInstaller` | Fresh install, converge, and applying one step's DDL. Writes the metadata row. |
| `GrimoireSchemaBackfillRunner` | Draining a pending backfill in bounded batches, each batch and its cursor in one transaction. |
| `GrimoireSchemaTransitionCoordinator` | The connection, `SqliteBusyRetry`, one bounded pass, and re-entering convergence after a run completes. |

`GrimoireSchemaTransitionHostedService` schedules the coordinator on an interval, on the long-running host only, through the installation-reset-recovery-aware registration the Covenant maintenance service already uses.

### 10.1 At bootstrap

`InstallTierAsync` asks the planner, then:

- **`FreshInstall`** and **`Converge`** behave exactly as today: ordered head DDL, the tier's one data initializer, inspection against the head manifest, metadata write, commit.
- **`BeginRun`** inserts the journal row and applies steps in order. A step with no backfill commits its statements together with `CompletedThroughVersion = ToVersion` and continues. A step **with** a backfill commits its statements together with `BackfillName` set and **stops**; the tier reports `TransitionIncomplete`.
- **`ResumeRun`** picks up at `CompletedThroughVersion` under the same rules — **unless** the journal row names a pending backfill, which means that step's DDL has already committed. Re-executing it would throw duplicate-object, and on Core that propagates out of the install and aborts startup, reintroducing through the resume path the very deadlock `TransitionIncomplete` exists to prevent. A row with a pending backfill therefore returns `TransitionIncomplete` immediately and leaves the sweep to the coordinator.
- When the last step completes, the run finishes in one transaction: inspect against the head manifest, write the metadata row at head with the resulting installed-catalog fingerprint, delete the journal row, commit. Finishing is one shared routine called by both drivers — the installer's last backfill-free step and the backfill runner's final batch — on the division the maintenance sweeps already keep: the driver owns the transaction, the routine owns what finishing means. Two copies would be two ideas of when a version is installed, and the journal deliberately has no completion flag for them to disagree through. A failed inspection rolls that transaction back and leaves the journal row untouched, so the tier reports `InstalledCatalogDrift` and the run is retried rather than half-recorded. A drift that is genuine fails identically on every later attempt, which is the fail-closed outcome; each attempt records a bounded `LastDurableErrorCode` and logs the detail nowhere the health code can carry it.

Each step is its own transaction, each wrapped in `SqliteBusyRetry`, so a concurrent CLI opening the same encrypted file waits instead of failing. Cancellation propagates and is never recorded as tier health.

### 10.2 After readiness

The coordinator is gated on **the journal, not on tier availability**. Gating on availability would deadlock in both directions: a Covenant tier mid-transition is unavailable by design, and a Core tier mid-transition would abort startup before its own backfill could run.

One pass: read the journal rows; for each, resolve the chain and the pending step; drain at most `GrimoireSchemaTransitionCoordinator.MaxBatchesPerPass` batches, each in its own transaction with its own cursor write. When a backfill completes and its step is **not** the last, that same final transaction advances `CompletedThroughVersion` and clears `BackfillName` and `BackfillCursor`. When it completes and its step **is** the last, that same transaction finishes the run instead, exactly as §10.1 describes. Either way the pass then re-enters tier convergence, so the next step's DDL runs without waiting for a restart.

After a run completes, the coordinator republishes Covenant tier health through the same path the bootstrapper uses — `CovenantAvailability.PublishSchema` followed by `CovenantPersistedAvailabilityPublisher.PublishAsync` — under a new `CovenantHealthTransition.SchemaEvolution` value. Without it a tier that became healthy after a backfill would keep reporting unavailable until the next restart.

A failing pass records a bounded `LastDurableErrorCode` on the journal row, logs the exception, and leaves the row for the next pass. It never deletes a row it did not finish.

## 11. What this deliberately does not do

- **No shipped version step and no shipped backfill.** Every tier stays at version 1. The loader and the driver run in production and find nothing, which is the posture this repository already argues for explicitly about the turn-receipt compactor: a sweep introduced alongside the rows it must drain is a sweep nobody has watched run empty.
- **No CLI drain.** The CLI bootstrap applies backfill-free steps, because it shares the install path, but it does not drain a backfill; draining would block a CLI verb behind an unbounded sweep. A CLI-only installation therefore sits at `TransitionIncomplete` until a host runs. It is fail-closed throughout and no data is at risk.
- **No downgrade.** A version above head is still refused. There is no reverse step.
- **No intermediate-version validation**, for the reason in §8.3.
- **No new operator surface.** Tier health already reaches Covenant availability; nothing new is routed, and `Arcanum.CommandMap.json` is untouched.

## 12. Testing strategy

Every behavior below enters through `GrimoireSchemaInstaller.InstallAsync` or `GrimoireSchemaTransitionCoordinator.RunOnceAsync` against a real SQLCipher scratch database. No test seeds a `grimoire_feature_schemas` row, a journal row, or a catalog state that it then asserts.

The multi-version fixture is a synthetic chain for a real tier: a small object set at v1, the same set plus one step at v2. The test installs v1 through the real installer with a v1-only chain, then calls the real installer again with the two-version chain. Both calls are the production entry point.

1. **Fresh install runs no step.** A two-version chain against an empty database installs the head shape directly, writes head metadata, and writes no journal row.
2. **A backfill-free step evolves and validates.** v1 installed, then the v2 chain: the catalog is at v2, metadata is at v2 with a recomputed installed-catalog fingerprint, and no journal row remains.
3. **A step with a backfill stops at the journal.** The DDL is committed, the journal row names the backfill, metadata is still v1, and tier health is `TransitionIncomplete`.
4. **The coordinator drains it and finishes the run.** After enough passes, metadata is at v2, the journal row is gone, and health is `Healthy`.
5. **The cursor never outruns the commit.** A backfill whose second batch throws leaves the cursor at the first batch's committed value and the rows it wrote intact, and a later pass resumes from exactly there and reaches the same terminal state as an uninterrupted run.
6. **A batch is bounded.** A backfill declaring a small `MaxRowsPerBatch` over a larger corpus takes more than one batch, and the coordinator's pass is bounded.
7. **Restart safety.** Disposing the coordinator mid-run and constructing a fresh one from the same database resumes correctly.
8. **Newer version.** Metadata above head refuses with `IncompatibleNewerVersion`; Core throws.
9. **Definition drift at head.** A recorded head fingerprint that disagrees refuses with `SourceDefinitionMismatch`.
10. **Definition drift at an older version.** A recorded v1 fingerprint that is not the chain's pin for v1 refuses with `SourceDefinitionMismatch` and runs no step.
11. **Unknown object.** An object the manifest does not declare, owned by no tier, refuses with `InstalledCatalogDrift` carrying `UnexpectedObject`.
12. **Mixed catalog.** A catalog already at head with metadata at v1 and no journal row refuses with `MixedCatalogVersions`.
13. **Unresumable journal.** Each of the five §9.3 checks gets its own case, and each is its own mutation target.
14. **Core mid-transition does not abort startup.** A Core tier at `TransitionIncomplete` returns rather than throwing, and its dependent tiers report `DependencyUnavailable`.
15. **Chain validation.** Gaps, reordering, a wrong head, a duplicate backfill name, and a duplicate statement ordinal each throw at construction.
16. **Transition resources are outside every source fingerprint.** All four fingerprints computed with and without a transition resource present are equal.
17. **The shipped catalog declares no transition today**, and every shipped chain is at version 1 with zero steps.
18. **The production driver exists.** A source-scan test pins a production call site for the coordinator and the hosted service, with a needle unique to that call — including its first argument, because a coordinator that names its method after the worker method it drives satisfies a bare-name search from the wrong side.

Before the slice is called green, one production behavior per acceptance criterion is broken in source and the suite is confirmed to fail: collapse the planner's evolve arm to `Converge`; move the cursor write out of the batch transaction; delete the `TargetSourceDefinitionFingerprint` resumability check; and remove the `MixedCatalogVersions` arm. Every row of a table-driven case gets its own mutation, because rows fail independently.

## 13. Documentation impact

This feature **reverses** a currently normative statement. `docs/Arcanum.DESIGN.md` §5.4.5 says "Fresh install only… There is intentionally no incremental or data migration," and `GrimoireSchemaRefusedException`'s message says the same thing in prose to the operator. Both are rewritten, not appended to. The policy becomes: an **undeclared** schema change is still fresh-install-only, and a **declared** version step is applied.

- `docs/Arcanum.DESIGN.md` — §5.4.5 the transition subtree and the fingerprint-exclusion invariant; §5.4.5a the tier table, the health list, and the per-step algorithm; the rewritten fresh-install paragraph; a new §10.25 owning the engine, ending in its own statement of what is deliberately absent; §16.2 glossary rows for the journal table and the new health values.
- `docs/Arcanum.OATH.md` — §9.1's tier table, the §2.1/§2.2 status tables, and the §22 document map's section range.
- `docs/ArcanumOATH.Human.md` — §9 prose and the §11 status table.
- `README.md` — the schema-tree listing and the Covenant status paragraph.
- `docs/Arcanum.DEBUGGING.Human.md` — what an operator sees and does when a tier reports a transition state.

No document outside `README.md` and `docs/Arcanum.OATH.md` references an issue, in either the direct or the inferred form. No document is hard-wrapped to a column width.
