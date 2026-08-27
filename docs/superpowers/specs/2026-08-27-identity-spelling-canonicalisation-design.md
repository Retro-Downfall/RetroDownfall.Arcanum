# Identity spelling: one form in the database

**Status:** Designed on 2026-08-27, on branch `codex/issue-97-saga-version-operations`, after nine sites of a defect family were fixed by normalising comparisons and the tenth landed that cost on the per-turn conversation read path.

## 1. Objective

Make every stored Guid identity in the Grimoire hold one textual form, so a comparison can be an exact indexed equality again.

Nine defects have been fixed by changing the comparison: `lower(replace(col, '-', '')) = @id`. Each fix was correct and each cost the index. That was affordable on erasure, backup and operator paths. It stopped being affordable when the same shape reached `EntryTemporalQueries`, where it now forfeits all three `SessionId`-led indexes on the largest table in the database, on the path that runs once per turn, for every user rather than only for the imported Sessions that motivated it.

Normalising at read also cannot finish the job. The enforcement register built to end the search has four blind spots, one of which — EF LINQ — carries a live instance: `GrimoireRepository.GetSessionAsync` translates `c => c.Id == id` to an unnormalised comparison and simply cannot be fixed by any predicate this approach can write.

Canonicalising the data fixes that class outright. When every row holds one form, EF LINQ compares correctly by construction, the register becomes deletable rather than maintainable, and the hot-path scan is reverted rather than tolerated.

## 2. Scope, settled before design

- **The canonical form is uppercase dashed** — `A1B2C3D4-…`, 36 characters. This is not a preference; it is what the EF SQLite provider renders and the only alternative is a global value converter, rejected in §4.1.
- **`SessionAttachments` is converted**, though it is internally consistent today. Its three identity columns are compared by Covenant components against `Sessions.Id` and `Entries.Id`, no foreign key protects the relationship, and leaving it lowercase keeps two register entries alive forever — which forecloses the whole point of the change.
- **The two `ToString("N")` columns are excluded.** `lexicon_entries.Id` and `IdempotencyClaims.Id` each have exactly one writing component whose every reader uses the same form, neither is compared by any Covenant, Backup or Repositories component, and neither is a foreign key into the converted tables. Converting them would rewrite two tables and rebuild a full-text mirror for no correctness gain. The exclusion is recorded as a deliberate second canonical form scoped to two single-writer tables, so the next reader inherits the reasoning rather than discovering an inconsistency.
- **The register is retired, not extended.** It was scaffolding for a search that is now complete; its replacement is a guard that fires at write time.

## 3. Governing constraints

- **The provider uppercases unconditionally.** `SqliteValueBinder.Bind` does `guid.ToString().ToUpperInvariant()` when the store type is not BLOB, verified against the `10.0.10` source this repository pins. Every EF-mapped Guid property and every raw `Guid` handed to `AddWithValue` therefore lands uppercase.
- **The compiled model is mandatory.** The context always applies the generated model so EF never materialises one at runtime, which is what makes the Native AOT suppressions honest. Any change to how a Guid maps means regenerating that model and re-running the AOT gate.
- **SQLite cannot `ALTER` a `CHECK` or a collation.** Either means the twelve-step table rebuild, on tables carrying fourteen foreign-key children in one case.
- **`PRAGMA foreign_keys=OFF` is a no-op inside a transaction.** The tool for reordering parent and child writes is `PRAGMA defer_foreign_keys=ON`, which defers enforcement to `COMMIT` so only the end state must be consistent. Foreign keys are not merely enabled but verified on every Covenant connection, so an in-place parent rewrite fails in either order without it.
- **`Sessions` must not be rebuilt by insert-select.** It carries an `AFTER INSERT` trigger that writes a `session_turn_quota_state` row per Session; a rebuild collides on that table's primary key for every existing row. Its `AFTER DELETE` trigger would likewise append a spurious owner-deletion event under any `DELETE FROM`. In-place `UPDATE` avoids both.
- **`entry_embeddings.EntryId` and the `SessionAttachments` columns have no foreign key.** Only the migration's own discipline pairs them with their parents, and the failure is worse than orphaned rows: the weaving service left-joins embeddings to entries, so a missed `entry_embeddings.EntryId` reports every entry as unembedded and silently re-embeds the entire corpus at provider cost.
- Native AOT throughout. Raw SQL through the declarative schema tree, one object per file.
- Documentation in `docs/` names capabilities, never issues.

## 4. Considered approaches

### 4.1 A global value converter — rejected

Attach a converter so EF renders lowercase or dash-free, matching the minority writers.

Rejected on three counts. It requires regenerating the compiled model and re-running the AOT gate. It inverts the data problem — every existing row on every installation is uppercase, so the converter makes all of them invisible to EF the moment it ships, which is a far larger event than the one being avoided. And it is form-selection rather than enforcement: it constrains EF and leaves eight raw-SQL writers untouched.

### 4.2 `COLLATE NOCASE` on the identity columns — rejected

Make the two dashed forms compare equal without rewriting any data, keeping the index.

Rejected, and recorded here so it is rejected on the record rather than forgotten. It does nothing about the dash-stripping half of the problem, so the dash-free form still misses and the comparison shape still has to change. More seriously, it silently alters primary-key uniqueness: two identities differing only in case become a conflict. And it needs the table rebuild anyway, since SQLite cannot `ALTER` a collation.

### 4.3 Convert the minority writers, verify the data, guard the writes — selected

Six one-line edits make the two minority writers render the canonical form, using a helper the repository already applies at thirteen sites. A schema step verifies the data holds one form and repairs it if not. Guard triggers refuse a non-canonical write thereafter. The normalised comparisons added for the hot path are reverted to exact equality, and the register is deleted.

## 5. Why the migration is a verifier rather than a backfill

Both minority-spelling writers were unreachable for their entire existence, and this is the fact the whole design rests on.

The unprotected merge path checks whether a Session exists in the archive by binding the lowercase form against a column the archive holds uppercase. It matches nothing, every requested Session is reported absent, and the method returns before it opens a transaction. That gate has been at those lines since the file was introduced.

The protected transfer store has exactly one public method, one production caller, and one gate above that caller: the import planner, which refused every archive by the same mechanism until the fix earlier on this branch. The planner and the transfer store were introduced in the same commit, so there was never a window between them.

An installation predating this branch therefore holds the canonical form in every column being converted, except `SessionAttachments`, which holds one consistent minority form written by four sites that agree.

Two caveats are stated rather than waved away. An identity whose hex digits happen to include no letters renders identically either way — such a row is already canonical and needs nothing. And manual database edits are outside what source can rule out.

**The migration therefore begins with a count, and that count is its precondition,** not a formality: one `SELECT COUNT(*) … WHERE col <> upper(col)` per converted column. Zero across the board turns "verifier, not backfill" from an argument into evidence on that installation. Non-zero means the repair arm runs, which must therefore exist and be tested even though it is expected never to fire in the field.

## 6. What changes

### 6.1 The writers

The protected artifact transfer store and the backup session importer render the canonical form at roughly six sites, using the existing house helper rather than a new one. `SessionAttachments`' own store is converted with them, since that column family moves in this change.

### 6.2 The data

A Core schema version step that, per converted column, counts non-canonical rows and repairs them in place. In-place `UPDATE` under `PRAGMA defer_foreign_keys=ON` inside one transaction, never a table rebuild, for the trigger reasons in §3. Parents and their unenforced children — `entry_embeddings.EntryId`, the `SessionAttachments` columns, `Sessions.CampaignId` — move together because nothing else will make them.

### 6.3 The guards

A `BEFORE INSERT` and a `BEFORE UPDATE` trigger per converted column, refusing a value that is not its own uppercase. This is the house pattern; the schema tree already carries roughly thirty such guard triggers. It needs no table rebuild, and it closes all four of the register's blind spots at once — because it fires on the write, whatever produced it: EF LINQ, a raw `Guid` through the provider, an interpolation, or SQL nobody has written yet.

### 6.4 The readers

Every comparison normalised during the nine fixes reverts to exact equality, and `EntryTemporalQueries` regains its three indexes. This is the step that pays for the change, and it must come after the guard triggers are in place, not before.

`GetSessionAsync` needs no edit and gains correctness for free: EF LINQ compares uppercase against uppercase once the data is canonical. That is the blind spot no predicate could have reached.

### 6.5 The register

Deleted, and replaced by a behavioural contract test that drives each production writer through its outermost entry point against a real database and asserts no non-canonical identity was written. That test closes the two blind spots no source scan can see — a raw `Guid` handed to the provider, and a rendering that crosses a method boundary.

## 7. Two things the investigation found that this change must also carry

**`Sessions.CampaignId` is an unregistered member of the family.** It has two writers, no foreign key, and two production comparisons that bind the canonical form: a Campaign deletion that clears the column, and a campaign-filtered session listing. An imported Session would keep pointing at a deleted Campaign and would be omitted from that listing. It is converted with the rest.

**The register carries three defects of its own**, which matter because it was about to be trusted as a catalogue: an entry naming a column that does not exist on that table; a comment describing behaviour that two later fixes made untrue; and prose claiming a table is in scope when the column list filters it out. They are corrected in the same change that retires it, so the retirement is not mistaken for a cover-up.

## 8. Testing

The precondition count and the repair arm are both tested, the repair against a database seeded in the minority form through the production writer that once produced it.

Every guard trigger is tested by attempting a non-canonical write through the production path and asserting the abort, not by writing the row directly.

The reader reversion is tested by the tests that already exist for those paths — they were written against the normalised shape and must stay green against the exact one, which is the point.

**The acceptance bar remains the mutation check**, for the reason this family taught nine times: a fixture that seeds the form the broken code expects passes while proving nothing. Every test enters through the real production writer and reads identities back out of rows.

## 9. What is deliberately absent

The timestamp-encoding family is not addressed here. The keyset fallback in `EntryTemporalQueries` compares a stored `"o"`-format timestamp against a provider-bound `DateTimeOffset` rendered with a space separator and no fraction; under the same BINARY collation these do not compare consistently. It is the same *shape* of defect — two encodings of one value — and a different family, needing its own investigation.

The finalization capacity-reservation gap is not addressed either. A protected import writes an imported finalization guard without the consumed reservation the schema demands, so a Session with any committed assistant turn still cannot import. It is not an identity defect and does not move with this one.
