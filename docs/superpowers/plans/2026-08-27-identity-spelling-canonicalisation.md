# Identity Spelling Canonicalisation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every stored Guid identity in the Grimoire hold one textual form, so comparisons return to exact indexed equality, the per-turn table scan disappears, and the identity-comparison register can be deleted rather than maintained.

**Architecture:** Six writer call sites are converted to the form the EF provider already renders. A Core schema version-5 step counts non-canonical rows per column and repairs them in place under deferred foreign keys — a verifier with a repair arm, because both minority writers were unreachable for their entire existence. Guard triggers then refuse a non-canonical write whatever produces it, which is what lets the normalised comparisons revert to exact equality and the register retire in favour of a behavioural contract test.

**Tech Stack:** .NET 10, C# with Native AOT discipline, xUnit 2.9.3, raw `DbCommand` SQL over SQLCipher-encrypted SQLite, EF Core 10.0.10 with a mandatory compiled model.

**Spec:** [`docs/superpowers/specs/2026-08-27-identity-spelling-canonicalisation-design.md`](../specs/2026-08-27-identity-spelling-canonicalisation-design.md)

## Global Constraints

- **The Core tier's version-4 source fingerprint is `35B3B5AD90B8BE3571516C88CB0FDF4F8E61712F86F8D1134D07D92B3F980AC1`.** Read out of the head tree on 2026-08-27 before any Core `.sql` file was touched for this work. It cannot be recomputed once the tree changes. Copy it verbatim into `GrimoireSchemaVersionChains.SourcePins` under `(GrimoireSchemaTransactionTier.Core, 5)`.
- **The canonical form is uppercase dashed**, 36 characters — what `guid.ToString().ToUpperInvariant()` produces, which is what `SqliteValueBinder.Bind` does for every non-BLOB Guid. Use the helper the repository already applies at thirteen sites rather than writing a new one.
- **Excluded from conversion, deliberately:** `lexicon_entries.Id` and `IdempotencyClaims.Id`, which are dash-free lowercase, single-writer, and compared by nobody outside their own component. Do not convert them and do not add them to any guard.
- **`PRAGMA foreign_keys=OFF` is a no-op inside a transaction.** Use `PRAGMA defer_foreign_keys=ON` to reorder parent and child writes; it defers enforcement to `COMMIT`.
- **Never rebuild `Sessions` by insert-select.** Its `AFTER INSERT` trigger writes a `session_turn_quota_state` row per Session and would collide on that table's primary key for every existing row; its `AFTER DELETE` trigger would append a spurious owner-deletion event under any `DELETE FROM`. In-place `UPDATE` only.
- **`entry_embeddings.EntryId`, the three `SessionAttachments` identity columns, and `Sessions.CampaignId` have no foreign key.** Nothing but this migration's own discipline pairs them with their parents. A missed `entry_embeddings.EntryId` is worse than an orphan: the weaving service left-joins embeddings to entries, so every entry reports as unembedded and the corpus is silently re-embedded at provider cost.
- **SQLite cannot `ALTER` a `CHECK` or a collation.** Guards are triggers, which are added like any other schema object.
- Native AOT: no reflection-based serialization, no dynamic type loading in production code. A test that reads source or reflects is fine and has precedent.
- **Blank-line style:** a blank line after every opening brace of a method or type body, before every closing brace, between consecutive members, and between almost every statement.
- This suite is on **xunit 2.9.3**, which has no `TestContext`. Use `CancellationToken.None`.
- Hand-written fakes only. No Moq.
- Tests run with `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "<expr>"`. **A filter matches a class or method name, never a file name**, and a substring filter can match more or fewer classes than expected — always check the reported count.
- There is no `timeout` command on this machine.
- Documentation in `docs/` names capabilities, never issues; one logical block is one physical line.

## The columns being converted

**Version 5 was authored across two tasks and both have landed.** Task 2 landed the step, its verifier and the two reference repairs; Task 2b landed the `SessionAttachments` family against the same version, together with the six writers that fill it. The release condition that governed the interval — a journal recording the `(Core, 5)` sweep complete is never re-run, so an installation upgraded between the two would have kept the minority spelling permanently — is discharged, and version 5 is now a complete step.

**Split under measurement.** This was one table of columns to repair. Implementation established that half of them cannot be repaired at all, so it is now two tables and the reasoning is in the spec's §6.2. The correction is marked rather than applied silently, because writing a guard on a column whose data cannot be moved is a different decision from writing one on a column whose data can.

**Repaired: a reference, and only where its canonical target already exists.**

| Table | Columns | Target it has to agree with |
|---|---|---|
| `Sessions` | `CampaignId` | `Campaigns.Id`; no foreign key, and two live comparisons bind canonical |
| `entry_embeddings` | `EntryId` | `Entries.Id`; no foreign key, and the weaving service's left join silently re-embeds the corpus without it |
| `SessionAttachments` | `Id`, `SessionId`, `EntryId` | `Id` is an identity that moves in place, because no table depending on it carries a trigger; `SessionId` and `EntryId` are ordinary references to `Sessions.Id` and `Entries.Id` |
| `session_attachment_chunks`, `session_attachment_index_state` | `AttachmentId` | `SessionAttachments.Id`, by foreign key, so leaving either behind aborts the migration at `COMMIT` |
| `attachment_memory_consultations`, `saga_memory_attachment_provenance`, `lexicon_fact_attachment_provenance` | `AttachmentId` | `SessionAttachments.Id`, with no foreign key at all: each decides whether an attachment-derived consultation, memory or fact can still report its source, so missing one converts a working join into one that silently returns nothing |

`session_attachment_chunks.SessionId` and `RetrievalScope` are the two attachment columns that deliberately stay in the minority form. The tapestry reads `SELECT DISTINCT "SessionId" FROM session_attachment_chunks` as its live scope-id set and those values become `tapestry_nodes.ScopeId`, so moving them would orphan every attachment-scoped generation and rebuild the tree at provider cost — and nothing compares either across a component boundary, which is what this change exists to end.

**Verified only: an identity a row is known by, which no statement can move.**

| Table | Columns | Why it cannot move |
|---|---|---|
| `Sessions` | `Id` | eight of its fourteen foreign-key children refuse the write by trigger, four unconditionally, and `session_turn_quota_state` holds a row for every Session ever created |
| `Entries` | `Id` | four tables reference it without a foreign key and refuse the write: `assistant_entry_finalizations` and `assistant_entry_erasure_receipts` abort every update whatever it changes, while `assistant_finalization_capacity_reservations` and `session_turn_claims` abort specifically on a changed Entry identity — so moving it would need all four to accept a change all four exist to refuse |
| `Entries` | `SessionId` | bound by foreign key to a `Sessions.Id` that cannot move, so moving it alone would break the key |
| `assistant_entry_finalizations` | `AssistantEntryId`, `SessionId` | the table's own guard refuses every update, whatever it changes |
| `session_sensitivity_state` | `SessionId` | bound by foreign key to a `Sessions.Id` that cannot move |

Verification is sufficient for all of them: no code path could ever have written a non-canonical Session or Entry identity, and a hand edit reaches the operator through the count rather than through an upgrade that can never complete.

`Campaigns.Id` and `artifact_sensitivity.SessionId` are single-writer and already canonical in production: guard them, but they need no repair. Both are seeded in forms production never writes by a number of existing fixtures — see Task 3.

---

### Task 1: The writers render the canonical form

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedArtifactTransferStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Covenant/IdentitySpellingContractTests.cs` (create)

**Interfaces:**
- Consumes: the existing uppercase helper used at thirteen sites — find it, do not write a second one.
- Produces: every identity these three components write is canonical.

- [ ] **Step 1: Find the existing helper and every site to change**

`grep -rn 'ToUpperInvariant' src --include=*.cs` finds the house form. Then find the writers: in `ProtectedArtifactTransferStore` the Session, Entry, attachment, finalization and destination-Campaign renderings; in `BackupSessionImporter` the Session, Entry and attachment renderings; in `SessionAttachmentStore` the insert and the promotion `UPDATE`.

Do not guess the line numbers from this plan — they have moved. Locate each by what it renders.

- [ ] **Step 2: Write the failing contract test**

Create `IdentitySpellingContractTests`. This is the test that replaces the register at the end of the plan, so build it to last. One case per writing component, each driving that component through its **outermost production entry point** against a real database, then asserting no non-canonical identity was written:

```csharp
[SkippableFact]
public async Task The_protected_transfer_store_writes_only_canonical_identities()
{

    await using IdentitySpellingHarness harness = await IdentitySpellingHarness.CreateAsync().ConfigureAwait(false);

    await harness.CommitImportedSessionThroughTheStoreAsync().ConfigureAwait(false);

    Assert.Empty(await harness.NonCanonicalAsync().ConfigureAwait(false));

}
```

`NonCanonicalAsync` returns one row per offending `(table, column, value)` so a failure names what was written and where, rather than asserting a bare count. Drive the attachment store and the backup importer the same way in their own cases.

- [ ] **Step 3: Run and confirm it fails**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~IdentitySpellingContractTests"`
Expected: FAIL, naming the non-canonical columns each writer produced.

- [ ] **Step 4: Convert the writers**

Apply the helper at each site found in Step 1. Nothing else changes — no comparison, no schema.

- [ ] **Step 5: Run and confirm it passes**

Same filter. Expected: PASS, and the reported count covers every component case you wrote.

- [ ] **Step 6: Run the suites that exercise these writers**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ProtectedArtifactTransferStoreTests|FullyQualifiedName~BackupSessionImporterTests|FullyQualifiedName~BackupSessionImportPlannerTests|FullyQualifiedName~SessionAttachment"`

Several of these seed fixtures in the minority form on purpose, because that is what production wrote. **Those fixtures now describe history rather than the present** — update each to the canonical form and keep the assertion that pins the stored spelling, so a future writer change still fails loudly. Where a fixture deliberately seeds a *legacy* row to prove the repair arm works, leave it and say so in a comment; Task 2 depends on such a row existing.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "fix(covenant): render every written identity in one spelling"
```

---

### Task 2: Core schema version 5 — verify, and repair if needed

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V5/*.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/IdentitySpellingBackfill.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionFourFixture.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/IdentitySpellingEvolutionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionResourceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaVersionChainTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionThreeFixture.cs`

**Interfaces:**
- Consumes: Task 1's canonical writers.
- Produces: `GrimoireSchemaVersionChains.CoreSchemaVersion == 5`; a backfill that repairs every column in the table above.

- [ ] **Step 1: Read the two precedents before writing anything**

Read `MemoryAnnalsBackfill.cs` and `SagaMemoryCampaignScopeBackfill.cs` — they are the two shipped backfills and establish the resumable, bounded-batch shape. Read `CoreSchemaVersionThreeFixture.cs` for the fixture-rebasing rule: **each fixture peels back exactly one version and rebases on the one above it**, so your new version-four fixture starts from the shipped catalog and `CoreSchemaVersionThreeFixture` must be rebased onto it. Getting that wrong reds the version-three pin test.

- [ ] **Step 2: Write the failing evolution test**

Model it on the existing evolution suites. Three cases:

```csharp
[Fact]
public void Version_four_reconstruction_matches_the_pinned_fingerprint()
{

    Assert.Equal(
        "35B3B5AD90B8BE3571516C88CB0FDF4F8E61712F86F8D1134D07D92B3F980AC1",
        CoreSchemaVersionFourFixture.Fingerprint);

}
```

Plus: evolving a version-four installation reaches the shipped version-five tree; and — the one that matters — **a version-four installation seeded with minority-spelled rows through the production writer that once produced them comes out canonical**, with the parents and their unenforced children moving together. Assert on `entry_embeddings.EntryId` specifically, because a missed one silently re-embeds the corpus.

- [ ] **Step 3: Run and confirm it fails**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~IdentitySpellingEvolutionTests"`
Expected: FAIL — `CoreSchemaVersionFourFixture` does not exist.

- [ ] **Step 4: Write the version-four fixture and rebase version three**

`CoreSchemaVersionFourFixture` reconstructs the version-four tree from the shipped catalog by removing the objects version 5 adds. Then rebase `CoreSchemaVersionThreeFixture` onto it, exactly as version three is rebased onto the shipped catalog today, and update its remarks to say so.

- [ ] **Step 5: Write the backfill**

`IdentitySpellingBackfill`, resumable and bounded like its two predecessors. Per column: count non-canonical rows; if zero, do nothing and record that; if non-zero, repair in place.

The repair runs `UPDATE "<table>" SET "<col>" = upper("<col>") WHERE "<col>" <> upper("<col>")`, inside one transaction with `PRAGMA defer_foreign_keys=ON` set first — parents and children then move in any order and only the end state must be consistent.

**Never** rebuild `Sessions`; see the Global Constraints for the two triggers that make a rebuild wrong. In-place `UPDATE` throughout.

Put the count in the log even when it is zero. On every installation that predates this change it should be zero, and a log line saying so is the evidence that the reasoning held in the field.

- [ ] **Step 6: Declare the version and its pin**

Set `CoreSchemaVersion = 5`, extend that constant's remarks with what version 5 did, add the pin from the Global Constraints under `(Core, 5)`, and register `IdentitySpellingBackfill` under the same key.

- [ ] **Step 7: Run and confirm it passes**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Data.Schema"`
Expected: PASS — including the version-two and version-three pin tests, which the rebasing in Step 4 protects.

- [ ] **Step 8: Extend the two closed inventories**

`GrimoireSchemaTransitionResourceTests` pins every transition statement by name in install order; `GrimoireSchemaVersionChainTests` pins the head version literal. Both fail on a new declaration rather than a wrong result, so run them explicitly.

- [ ] **Step 9: Commit**

```bash
git add src tests
git commit -m "feat(covenant): settle every stored identity on one spelling"
```

---

### Task 3: Guard the writes

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/*_identity_guard_insert.sql`, `*_identity_guard_update.sql`
- Create: matching `Transitions/V5/` statements
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/IdentitySpellingGuardTests.cs` (create)

**Interfaces:**
- Consumes: canonical data from Task 2.
- Produces: a non-canonical write aborts, whatever produced it.

- [ ] **Step 1: Read the house pattern**

The schema tree carries roughly thirty `*_guard_insert` / `*_guard_update` triggers. Read two before writing one, and match their `RAISE(ABORT, …)` message style — the message is what a developer sees, so it should name the column and the expected form.

- [ ] **Step 2: Write the failing guard test**

One case per guarded column, each attempting a non-canonical write **through a production path** and asserting the abort. Where no production path can produce one any more — which is the point of Task 1 — drive the raw store command that would, and say in a comment that the guard exists for the writer nobody has written yet.

- [ ] **Step 3: Run and confirm it fails**

Expected: FAIL — the writes succeed.

- [ ] **Step 4: Write the triggers**

A `BEFORE INSERT` and a `BEFORE UPDATE` per guarded column, refusing a value that is not uppercase **and** dashed **and** 36 characters — case alone passes a dash-free rendering silently, because a 32-character hex string is already its own uppercase image. Guard `Campaigns.Id` and `artifact_sensitivity.SessionId` too: they need no repair but they are identity columns and the guard is what stops the next writer diverging.

**One trigger is already shipped.** `assistant_entry_finalizations_guard_identity` moved forward into the version-5 step, because that step needs a schema object and this is the one identity guard with no ordering relationship to the data beside it: both of the table's writers hand the provider a raw `Guid`, which the value binder renders uppercase unconditionally, and the table's own guard refuses every update so no sweep could move those columns anyway. It guards both of that table's identity columns in one `BEFORE INSERT` rather than one trigger per column; settle which shape the remaining guards take before multiplying it.

**Budget for the fixture sweep, which is measured rather than estimated.** Guarding `Campaigns.Id` reds 45 tests across the Covenant, Saga, retention, backup and endpoint suites; guarding `artifact_sensitivity.SessionId` reds 12, all in one suite from one constant. Every one is a fixture seeding a spelling production never writes, so each is a correction rather than a relaxation — but correcting the seed is not always the end of it. Correcting `CovenantRetentionSeed`'s Session constant clears eleven of the twelve and leaves `CovenantRetentionTests`' `AffectedSessions` arm failing on behaviour, because that arm only ever passed while the seed's spelling made `CovenantProtectedArtifactErasureKernel.RepairSessionSensitivityAsync`'s exact-match count return nothing. It measures real behaviour for the first time once the seed is right, and deciding what it should then assert belongs to that suite. This is the sixth distinct shape this defect family has taken: a test green over a production query that matched no row at all.

Add each as both a head-tree object and a version-5 transition statement, character for character.

- [ ] **Step 5: Run and confirm it passes**

Then run `--filter "FullyQualifiedName~Data.Schema"` again — a guard that reds an existing schema test means a shipped writer is producing a form you did not expect, which is a finding, not a test to relax.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(covenant): refuse a non-canonical identity at the write"
```

---

### Task 4: Give the readers their indexes back

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/EntryTemporalQueries.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ProtectedAssistantArtifactReader.cs`
- Modify: every other site normalised during the family's nine fixes
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantIdentitySql.cs`

**Interfaces:**
- Consumes: canonical data and the guards.
- Produces: exact indexed equality everywhere; `CovenantIdentitySql` reduced to whatever still has a caller, or deleted.

- [ ] **Step 1: Enumerate the sites**

`grep -rn 'CovenantIdentitySql' src --include=*.cs`. Every call site reverts to an exact comparison binding the canonical form.

- [ ] **Step 2: Revert them**

Take `EntryTemporalQueries` first and confirm its three `SessionId`-led indexes are usable again — that is the reason this whole plan exists. The `FormattableString` machinery introduced for the normalised predicate can go with it where it was only there to carry the shape.

The foreign-key resolver is a different case: it resolved a parent's stored spelling because two existed. With one spelling it has nothing to resolve. Delete it if nothing else needs it, and say so in the commit rather than leaving a resolver that always returns its input.

- [ ] **Step 3: Run every suite that covers these paths**

The tests written against the normalised shape must stay green against the exact one. That is the point: they were written to prove behaviour, not spelling.

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~EntryTemporal|FullyQualifiedName~SessionRepositoryTests|FullyQualifiedName~ProtectedAssistantArtifactReaderTests|FullyQualifiedName~Covenant"`

- [ ] **Step 4: Prove the index is back**

Add one test asserting the query plan for the transcript read uses an index rather than scanning — `EXPLAIN QUERY PLAN` over the production SQL, asserting the plan mentions a `SessionId` index and not `SCAN`. Without this the regression that motivated the whole plan can silently return.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "perf(covenant): compare identities exactly, and use the index again"
```

---

### Task 5: Retire the register

**Files:**
- Delete: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantIdentityComparisonInventoryTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Covenant/IdentitySpellingContractTests.cs`
- Modify: `docs/Arcanum.DESIGN.md`

**Interfaces:**
- Consumes: Tasks 1 through 4.
- Produces: the source-scan register is gone; the behavioural contract test is the guard's companion.

- [ ] **Step 1: Confirm the register is empty of live defects**

Run it. Every remaining entry should now be either fixed or a coincidence-match that canonicalisation made exact. **If any entry still names a live defect, stop and report it** — deleting a register that still has something to say is how the next instance gets lost.

- [ ] **Step 2: Correct its three defects in the deletion commit's message**

The register carried an entry naming a column that does not exist on that table, a comment describing behaviour two later fixes made untrue, and prose claiming a table was in scope when its own column list filtered it out. Name all three in the commit message so the retirement is a record rather than a disappearance.

- [ ] **Step 3: Delete it, and widen the contract test to cover what it covered**

The contract test from Task 1 already drives each writer. Extend it to assert across **every** identity column in the table at the top of this plan, not only the ones a given writer touches — so a new writer anywhere is caught by the guard at runtime and by this test in CI.

- [ ] **Step 4: Document the rule**

One sentence in the testing-strategy section of `docs/Arcanum.DESIGN.md`: identities are stored in one spelling, guarded by trigger, and proved by a contract test that drives each writer. Replace the sentence the register added. Names capabilities, never issues; one physical line.

- [ ] **Step 5: Commit**

```bash
git add tests docs
git commit -m "test(covenant): replace the identity register with a guard and a contract"
```

---

### Task 6: Verification

- [ ] **Step 1: Clear accumulated state**

```bash
find . -type d -name TestResults -not -path "*/node_modules/*" -exec rm -rf {} +
```

- [ ] **Step 2: Build with zero warnings**

```bash
dotnet build RetroDownfall.Arcanum.slnx --no-incremental -warnaserror
```

`--no-incremental` is required; an incremental build skips analyzers on unchanged projects and hides exactly what this gate exists to catch.

- [ ] **Step 3: Run every suite, alone**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
```

Expected: one failure, `MacOsSigningScriptTests`, proven pre-existing earlier on this branch. Do not cite a run that overlapped a rebuild or a commit.

- [ ] **Step 4: Break five things and confirm the suite notices**

One at a time, revert and re-run:

1. Make one converted writer render the minority form again. Expected red: that writer's contract case, naming the column.
2. Delete one guard trigger's `RAISE`. Expected red: that column's guard case.
3. Make the backfill skip `entry_embeddings.EntryId`. Expected red: the evolution test's assertion on that column.
4. Revert one reader to the normalised comparison. Expected red: the query-plan test.
5. Change the version-5 pin by one character. Expected red: the version-four reconstruction test.

If any stays green, the test for it is not testing it.

- [ ] **Step 5: Confirm the migration is a no-op on a canonical installation**

Install a fresh version-four database, evolve it without seeding any minority-spelled row, and confirm the backfill logs zero non-canonical rows for every column and rewrites nothing. That is the case every real installation will take.

- [ ] **Step 6: Review the whole diff**

```bash
git diff <base>...HEAD --stat
git diff <base>...HEAD
```

Read it. Confirm nothing landed that no task asked for.

---

## Self-Review

**Spec coverage.** §5's verifier-not-backfill → Task 2. §6.1 writers → Task 1. §6.2 data → Task 2. §6.3 guards → Task 3. §6.4 readers → Task 4. §6.5 register → Task 5. §7's `Sessions.CampaignId` → Tasks 1 and 2, and it is in the column table. §7's three register defects → Task 5 Step 2. §8 testing → each task plus Task 6. §9's two exclusions are absent by design and named in the spec.

**Ordering.** Writers before data, or the backfill repairs rows a writer immediately re-breaks. Data before guards, or the guards abort the repair. Guards before readers, or a reader reverts to exact equality while a writer can still diverge. Register last, because it is the evidence that the rest worked.

**Known judgment calls left to the implementer.** The exact set of normalised sites in Task 4 is discovered by grep rather than listed here, because the count changed four times during the fixes. The guard message wording is left open, constrained only to name the column and the expected form.
