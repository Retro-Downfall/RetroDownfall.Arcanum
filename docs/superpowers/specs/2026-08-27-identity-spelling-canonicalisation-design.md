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

The protected artifact transfer store and the backup session importer render the canonical form at roughly six sites, using the existing house helper rather than a new one. The attachment family's own six writers are converted in the same change that moves its data, and not before: the attachment store across both of its partial-class files, the attachment index repository across its identity bindings, and the attachment-memory provenance store, the Saga memory store and the Lexicon service where each writes the `AttachmentId` it consulted. Converting any of them ahead of the migration would have made new rows disagree with old ones and with the two foreign-key children that key off `SessionAttachments.Id` in whatever spelling they were given.

### 6.2 The data

**Revised under measurement.** This section originally said every converted column is repaired in place. Implementation established that half of them cannot be, and the correction is recorded here rather than applied silently, because the original claim would have a later reader write guards on the assumption that the data behind them is repairable.

A Core schema version step that counts non-canonical rows for each of the identity columns it declares, records the count whatever it is, and repairs the columns it can move. It counts what it declares rather than every identity column in the Grimoire: the two `ToString("N")` columns are a deliberate second canonical form and are excluded by §2, and `artifact_sensitivity.SessionId` is left to the guard that refuses a bad write rather than to a count taken once. `Campaigns.Id` is counted although nothing repairs it, because it is the identity `Sessions.CampaignId` is repaired against — a non-canonical Campaign makes that repair decline every row, and without a count the operator would see a silent no-op with nothing saying why. In-place `UPDATE` under `PRAGMA defer_foreign_keys=ON` inside one transaction, never a table rebuild, for the trigger reasons in §3.

**Version 5 was authored across two changes and both have landed.** The schema step, its verifier and the two reference repairs landed first; the `SessionAttachments` column family and its children — the one genuine data rewrite in this work — landed against the same version afterwards, together with their writers, for the reason §6.1 gives. The release condition that governed the interval is discharged: a journal that records the `(Core, 5)` sweep complete is never re-run, so an installation upgraded in between would have kept the minority spelling in those columns permanently, with nothing left to notice it.

**Repaired: a reference whose canonical target already exists.** `Sessions.CampaignId` against `Campaigns.Id`, `entry_embeddings.EntryId` against `Entries.Id`, and `SessionAttachments`' own `SessionId` and `EntryId` against `Sessions.Id` and `Entries.Id`. The qualification is load-bearing rather than defensive. The point of a reference column is that it joins, so uppercasing one whose target is itself spelled the minority way would break a join that currently works in the name of fixing one that does not; scoping the repair to a reference whose canonical target exists makes it provably a restoration of a broken pairing.

**Moved: the one identity nothing refuses.** `SessionAttachments.Id` held the minority spelling in every row and is moved in place, because no table that depends on it carries a trigger — the refusals that make a Session identity immutable have no counterpart here. It moves together with the five columns that name it, inside one transaction under `PRAGMA defer_foreign_keys=ON`: `session_attachment_chunks.AttachmentId` and `session_attachment_index_state.AttachmentId` by foreign key, and `attachment_memory_consultations`, `saga_memory_attachment_provenance` and `lexicon_fact_attachment_provenance` with no foreign key at all. The last three are the reason the family is declared in one place: each decides whether an attachment-derived consultation, Saga memory or Lexicon fact can still report its source, so missing one converts a join that works into one that silently returns nothing, permanently, on every installation. The parent is what a batch bounds; every column naming the identities in that batch moves with them however many rows there are, because a deferred foreign key is still checked at `COMMIT`.

**Left in the minority form on purpose.** `session_attachment_chunks.SessionId` and `RetrievalScope`. The tapestry reads the first as its live scope-id set and those values become `tapestry_nodes.ScopeId`, so moving them would orphan every attachment-scoped generation and rebuild the tree at provider cost. Because they stay, the one `@sessionId` parameter that served both a chunk column and `SessionAttachments.SessionId` in the index repository's purge predicate is split in two: one parameter cannot serve both a canonical comparison and a minority-spelled one, and because those two predicates are joined by `OR` the failure would have been a silent under-delete leaving orphaned chunks behind rather than anything that raised.

**Verified only, and never moved: an identity a row is known by.** `Sessions.Id` and every column that carries it, `Entries.Id`, and `assistant_entry_finalizations.AssistantEntryId`. A Session identity cannot be moved in place on any installation that has a Session, and this is a durability contract rather than an obstacle: eight of the fourteen foreign-key children of `Sessions.Id` refuse the write by trigger — `assistant_entry_finalizations`, `assistant_entry_erasure_receipts`, `session_summary_artifacts` and `session_title_artifacts` abort every update whatever it changes, while `session_turn_quota_state`, `session_turn_claims`, `assistant_finalization_capacity_reservations` and `session_campaign_bindings` each abort specifically on a changed `SessionId`. `Sessions_turn_quota_state` writes one quota row for every Session ever created, so the fifth of those refusals is reached without exception. `assistant_entry_finalizations` likewise refuses every update to itself, so its own identity column can never be rewritten either. No foreign key anywhere in the tree declares `ON UPDATE CASCADE`, so no child would follow its parent by itself even where the trigger allowed it.

Attempting the move anyway would abort at `COMMIT` and leave the tier permanently unable to reach head, which converts a hand-edited database into an un-upgradable one — worse than not repairing. Verification is sufficient because §5 establishes that no code path could ever have written a non-canonical Session or Entry identity; if one exists by hand edit, the operator learns of it from the count rather than from an upgrade that can never complete.

### 6.3 The guards

One guard per governed identity column, refusing a value that is not uppercase **and** dashed **and** 36 characters. This is the house pattern; the schema tree already carries roughly thirty such guard triggers. It needs no table rebuild, and it closes all four of the register's blind spots at once — because it fires on the write, whatever produced it: EF LINQ, a raw `Guid` through the provider, an interpolation, or SQL nobody has written yet.

**Revised under implementation.** This section originally said "a value that is not its own uppercase", and a case-only check is not enough: `Guid.ToString("N")` renders 32 uppercase characters, so a dash-free identity is already its own uppercase image and passes such a check in silence. The predicate is the one the sweep's own count asks — case, length, and all four dash positions — so the question a guard refuses on and the question the migration reports on are the same question.

**Per column, not per table.** `RAISE(ABORT, …)` takes a string literal, so a trigger covering several columns structurally cannot name the one that failed, and the message is the whole of what a developer sees. Five of the twelve guarded tables also carry identity-shaped columns that are deliberately outside this family — the three provenance `SessionId` columns, `session_attachment_chunks.SessionId`, `lexicon_fact_attachment_provenance.EntryId`, `attachment_memory_consultations.SourceEntryId` — so a trigger named for the table would claim a coverage it does not have. The cost is roughly thirty objects where a dozen would do, paid once, in a tree that is one object per file.

**Also revised: "and a `BEFORE UPDATE`" is not general.** A `BEFORE INSERT` guard is added for every governed column. A `BEFORE UPDATE OF <column>` guard is added for every governed column except those on `assistant_entry_finalizations` and `artifact_sensitivity`, because each of those tables already aborts every update whatever it changes — a finalization is terminal and a sensitivity label is immutable evidence about one exact artifact revision — so an update-time identity check on either could never be reached. That is a per-table finding rather than a general rule, and a test pins it so a table that later loses its blanket refusal is noticed.

**`BEFORE UPDATE OF <column>` rather than `BEFORE UPDATE`, and the choice is load-bearing.** A guard refuses the value being written, and on an `UPDATE` the values being written are the ones the `SET` clause names. A trigger that also judged the columns a statement leaves alone would refuse a row for data that was already there — and the step installs these triggers before it runs its own sweep, which repairs one identity column of a row at a time. A guard of that shape would abort the migration on any installation that has ever held an attachment, and every retry of it, leaving the tier permanently unable to reach head.

**The sweep is kept off its own guards twice, and both are needed.** `UPDATE OF` means a repair of one column never wakes a sibling column's guard. And the repair only ever selects a row whose *shape* is already canonical, so the value it writes is `upper()` of a 36-character dashed string: a dash-free or truncated identity has no correct dashed form to be rewritten into, so uppercasing one would produce a second non-canonical spelling that the guard beside it would then refuse. Such a row is reported by the count and never repaired, which is the behaviour §6.2's count already describes to an operator.

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
