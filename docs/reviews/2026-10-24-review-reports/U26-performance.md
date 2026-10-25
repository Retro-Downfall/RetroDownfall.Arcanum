# U26 (Arcanum.Infrastructure, Data (2)) - PERFORMANCE lens

Tree: branch claude/codebase-review-2026-10 at ac0d2d8e. Reviewer: performance lens.

## Scope note (important)

`scratchpad/review/units.json` did not exist when this review ran (the scratchpad `review/` directory had to be created; no `units.json`/`groups.json` anywhere under /private/tmp or ~/.claude). The unit was therefore reconstructed:
Infrastructure `Data/**/*.cs` (Migrations excluded, 269 files, 99,994 lines), sorted by (directory, file name, ordinal), packed greedily into chunks of at most 12,000 lines. That produces chunk 1 = 10,549 lines and **chunk 2 = exactly 9,123 lines = the stated size of U26**:

- `Data/DataRetentionService.FactoryReset.cs` (1,679)
- `Data/DataRetentionService.Pruning.cs` (6,696)
- `Data/DataRetentionService.WorkspaceReset.cs` (748)

I additionally read `DataRetentionService.FactoryActivation.cs` (748, adjacent, same partial) in full, and the helpers of `DataRetentionService.cs` that these files call in their hot loops (`CountTableAsync`, `TableExistsAsync`, `OpenConnectionAsync`, `FinalizePlan`, `ReadSessionConflictsAsync`, `BuildDeleteAttachmentPlanAsync`, `AddSessionPruneCandidatesAsync`, `ReadUploadedFilesByReferenceEligibilityAsync`, ...) plus `SagaVectorMirror.ClassifyAsync`, `LongRunningOperationStore.SaveCheckpointAsync`, `SqliteConnectionPragmas`, and the Schema/Tables SQL for every table the queries touch. Anything else in `DataRetentionService.cs` (8,099 lines) belongs to another unit.

## Measurement notes

- Probes live in a throwaway worktree (removed afterwards); source saved as `probes/U26/RetentionPerfProbe.cs.txt`, raw outputs as `probes/U26/results-*.txt` and `explain.txt`.
- The test project is a **Debug** build. SQLite/SQLCipher is native, so SQL cost is unaffected; managed-loop numbers were re-measured in a Release console app (`probes/U26/micro`).
- The machine was at load average 80-130 while I measured (other reviewers building). **Wall times are inflated 3-10x; "process-cpu" is the figure to trust.** Every claim below leads with CPU time or with counts (statements, rows, bytes), which do not depend on load.
- The probe seeds rows with raw SQL, then drives the real `DataRetentionService` through `PlanAsync`/`ApplyAsync` with an `sqlite3_profile` hook on the service's connection to count and time every statement.

## Findings (summary)

| # | Sev | Title |
|---|-----|-------|
| 1 | Important | Per-candidate lookups by `lower(replace(Id,'-',''))` on primary keys with no expression index: every candidate scans its whole table, plan and apply are O(candidates x table) |
| 2 | Important | Accounting prune planning is quadratic (retained-session path) and cubic (when adjustments exist): `BudgetReservations`, `CostAdjustments`, `BillableOperations` have no usable `RunId` index |
| 3 | Important | `candidates.Any(... "entry:" + NormalizeGuid(entryId))` inside the entry-embedding loop is O(n^2) in time and allocation, on a rule that is enabled by default |
| 4 | Important | Prune checkpoint rewrites the entire candidate list twice per journal-bearing candidate: O(N^2) bytes written |
| 5 | Important | Entry/attachment/embedding candidate queries re-run per-session conflict subqueries per entry, scanning `BudgetReservations` for every run of the entry's session |
| 6 | Minor | Plan and blocker lists are unbounded: one blocker and one diagnostics query per protected entry/run |
| 7 | Minor | Statement fan-out per candidate (11-123 statements), including repeated `sqlite_master` probes and repeated identical per-session reads |
| 8 | Minor | Workspace reset scans `tapestry_nodes` ~18 times (no `(ScopeKind, ScopeId)` index) and recomputes the full SHA-256 fingerprint for the post-commit reconcile |

Details follow.

---

### 1. Primary-key lookups wrapped in `lower(replace(...))` scan the table once per candidate (Important)

**Where.** `DataRetentionService.Pruning.cs`: 1603-1607 (planning, counts `Entries` by Id per entry candidate), 3174-3178 and 4614-4618 and 4974-4978 (Sessions), 4593-4606 and 5244-5253 and 5307-5311 (UploadedFiles), 4640-4648 / 4983-4988 / 5331-5332 / 5425-5436 / 5522-5561 / 5597-5601 (Entries: five statements per entry in apply plus the age check and the existence check), 4757 / 5083 / 6410 / 6424 (IdempotencyClaims), 4779 / 5101 / 6585 / 6623 (InferenceRuns), 6446 / 6458 (CostAdjustments), 6478 / 6488 (BudgetAlerts), 4826 / 5139 / 4525 / 4534 (SanctumBreaches), 4486 / 4503 (LongRunningOperations), 4593 / 5166 / 5177 / 5190 (Batches), 5518 (`UPDATE SessionAttachments SET EntryId = NULL WHERE lower(replace(EntryId...))`).
Schema: `Schema/Tables/Entries.sql` (only `IX_Entries_SessionId_Norm`), `UploadedFiles.sql`, `Sessions.sql`, `IdempotencyClaims`, `InferenceRuns`, ... have no expression index on `Id`.

**Why.** The V6/V14/V15 work added expression indexes for the *session-keyed* and *file-role* predicates (and pinned them in `DataRetentionNormalizedIdentityQueryPlanTests`), but the identity of the candidate itself is looked up with the same wrapped shape against the primary key. SQLite cannot use the PK for a function-wrapped column.

**Evidence (EXPLAIN QUERY PLAN against the real schema, `probes/U26/explain.txt`):**
```
SELECT SessionId, IsPinned, rowid FROM Entries WHERE lower(replace(Id,'-','')) = @id   -> SCAN Entries
SELECT COUNT(*) FROM UploadedFiles WHERE lower(replace(Id,...)) = @id ...             -> SCAN UploadedFiles
SELECT lower(Status) FROM Sessions WHERE lower(replace(Id,...)) = @id                 -> SCAN Sessions
SELECT COUNT(*) FROM Entries WHERE lower(replace(Id,...)) = @id                       -> SCAN Entries USING COVERING INDEX sqlite_autoindex_Entries_1
(same SCAN for Batches, IdempotencyClaims, LongRunningOperations, InferenceRuns, CostAdjustments, BudgetAlerts, SanctumBreaches)
UPDATE SessionAttachments SET EntryId = NULL WHERE lower(replace(EntryId,...)) = @id  -> SCAN SessionAttachments
```
**Measurement (real service, `Entries` rule enabled, 150 old entries pruned from a table of N entries, 300-byte content; `results-2-entries.txt`):**

| Entries in table | plan (150 cand.) | apply (150 cand.) | apply per candidate |
|---|---|---|---|
| 2,000 | 202 ms | 548 ms | 3.7 ms |
| 8,000 | 217 ms | 2,711 ms | 18 ms |
| 24,000 | 2,472 ms | 17,805 ms | 119 ms |

Per-candidate cost is proportional to the table size, so total cost is O(candidates x rows). In the 24,000-row apply the statement profile is: `DELETE FROM Entries WHERE lower(replace(Id..` 150 x = 6,163 ms; `SELECT COUNT(*) FROM Entries WHERE lower(replace(Id..) AND julianday(CreatedAt)<=..` 150 x = 4,916 ms; `SELECT COUNT(*) FROM Entries WHERE lower(replace(Id..` 300 x = 4,439 ms - i.e. 93% of apply is full-table scans. Planning shows the same: all-old entries, plan only (`results-6-batch3.txt`): 1,500 entries 957 ms CPU, 3,000 2,149 ms, 6,000 7,465 ms (x3.5 per doubling). These tables are 10 MB; a real `Entries` table (kilobytes of content per row, 10^5-10^6 rows) is 100-1000x larger per scan (a scan of `Entries` reads the content pages), so a prune of tens of thousands of entries is hours, not seconds.
For `UploadedFiles` (rule enabled by default) the same lookups appear (4 scans per file) but are cheap on small rows; they matter once an installation holds 10^4+ files or idempotency claims (`IdempotencyClaims` is one row per keyed API request).

**Failure scenario.** An operator enables the `Entries` (or session) retention rule on an installation with 200k entries and runs `arcanum data retention prune`: planning does one scan of `Entries` per selected entry, apply does 6-7 more, each ~0.3-1 s on a real table; with 20k candidates the operation does not finish in any useful time, holds the single-flight retention lease, and the 50-candidate checkpoint cadence never matters.

**Fix.** Add expression indexes in the exact predicate shape (`lower(replace("Id",'-',''))`) for Entries, Sessions, UploadedFiles, IdempotencyClaims, InferenceRuns, LongRunningOperations, Batches, CostAdjustments, BudgetAlerts, SanctumBreaches, plus `SessionAttachments.EntryId`, via a Core schema transition (exactly how V14/V15 did it), extend `DataRetentionNormalizedIdentityQueryPlanTests`. Cheaper alternative where a table is already canonical (UploadedFiles since V15): compare `Id = @id` with the canonical spelling. Effort M.

---

### 2. Accounting prune planning is quadratic/cubic (Important)

**Where.** `DataRetentionService.Pruning.cs` `AddAccountingCandidatesAsync` 2546-2574 (blocked-runs query) and 2629-2663 (candidate query); delete side 6498-6651. Schema: `Tables/BudgetReservations.sql` has **no** index on `RunId` at all; `BillableOperations` has only the plain `IX_BillableOperations_RunId` (unusable for `lower(replace(RunId))`); `CostAdjustments.sql` has no index.

**Why.** Both queries run correlated subqueries per `InferenceRuns` row: `EXISTS/COUNT` over `BudgetReservations`, `BillableOperations`, `CostAdjustments` keyed by `lower(replace(RunId,'-',''))`, and a nested `adjustment.BillableOperationId IN (SELECT Id FROM BillableOperations WHERE lower(replace(RunId...)) = lower(replace(run.Id...)))` that is re-materialised for every adjustment row. EXPLAIN shows `SCAN reservation`, `SCAN adjustment`, `SCAN billable` inside CORRELATED SUBQUERY nodes.

**Evidence.**
- Raw production query, 1 billable operation + 1 reservation + 1 adjustment per run (`results-3-accounting-raw.txt`): 25 runs 6 ms; 50 runs 107 ms; 100 runs 1,262 ms; 200 runs 7,213 ms. The same statement without the nested `IN`: 76 ms at 200.
- Through `PlanAsync` (`results-4-accounting-service.txt`): 200 runs 2,848 ms CPU; **400 runs 21,214 ms CPU** (x7.4 for 2x = cubic). Apply repeats the plan (3,300 ms CPU at 200 runs, of which ~2.85 s is the replan).
- The common case (runs that belong to *retained* sessions, which is every run while session retention is off) is quadratic through the "blocked" query's select-list `EXISTS` over reservations: 1,000 old runs 338 ms CPU; **4,000 old runs 5,282 ms CPU** (`results-6-batch3.txt`), and the plan returns 4,000 blockers.

Accounting retention is enabled by default (365 days, `Core/Configuration/RetentionSettings.cs:71`), so this is paid by every `data retention prune` (preview, then the apply's replan, then crash recovery's replan) once an installation is older than a year.

**Failure scenario.** A year-old installation with 40,000 old runs in retained sessions: ~100x the 4,000-run cost = ~9 CPU-minutes per plan, three plans per prune. With any `CostAdjustments` rows present and 5,000 candidate runs the cubic term (0.35 us per run x adjustment x billable triple) is ~12 CPU-hours.

**Fix.** Add expression indexes `BudgetReservations(lower(replace(RunId,'-','')))`, `BillableOperations(lower(replace(RunId,...)))`, `CostAdjustments(lower(replace(RunId,...)))` and `CostAdjustments(BillableOperationId)`; better, replace the per-run correlated counts with one grouped pass (`LEFT JOIN (SELECT lower(replace(RunId,..)) k, COUNT(*) n FROM ... GROUP BY k)`) and hoist the nested `IN` into an uncorrelated subquery so SQLite materialises it once. Effort M.

---

### 3. Quadratic `candidates.Any(...)` in the entry-embedding loop (Important)

**Where.** `DataRetentionService.Pruning.cs:1943-1950`:
```csharp
foreach ((string entryId, Guid sessionId, bool pinned) in rows)
{
    if (selectedSessions.Contains(sessionId)
        || candidates.Any(candidate => candidate == EntryCandidatePrefix + NormalizeGuid(entryId)))
```
`candidates` is the plan-wide `List<string>` (every candidate of every class so far, and it grows by one for each accepted embedding row). Each probe allocates the prefixed string and re-parses/re-formats the Guid once **per list element**.

**Evidence.**
- Release micro-benchmark of exactly this expression (`probes/U26/micro`): n = 10k 1,511 ms CPU / 9.9 GB allocated; 20k 5,355 ms / 39.7 GB; **40k 21,125 ms / 158.7 GB** (x4 per doubling).
- Through the real `PlanAsync` (Debug, loaded machine, `results-1-embedding.txt`), only `SessionEntryEmbeddings` enabled: 1,000 rows 131 ms; 2,000 485 ms; 4,000 2,823 ms; 8,000 9,171 ms wall, of which SQL is 1,956 ms - the rest is this loop and the GC it provokes.
`SessionEntryEmbeddings` is enabled by default (30 days).

**Failure scenario.** `data retention prune --dry-run` on an installation with 40,000 aged entry embeddings burns ~20 CPU-seconds and allocates 159 GB of short-lived strings (GC pauses stall the SQLite connection and the host's other requests); apply replans and pays it again.

**Fix.** Build a `HashSet<string>` of the `entry:` candidates once before the loop (or reuse `selectedEntryOwners`/the entry ids already selected) and test membership with a precomputed key; do not reformat the Guid per comparison. Effort S.

---

### 4. Prune checkpoint serialises and rewrites the whole candidate list per candidate (Important)

**Where.** `DataRetentionService.Pruning.cs`: `ApplyUnifiedPruneCoreAsync` 3473-3488 (save before delete) and 3530-3541 (save after) for every candidate whose `BuildPruneCandidateJournalAsync` is non-null (`file:`, `session:`, `attachment:`, `audit-log:`, `guardrail-log:`); `SavePruneCheckpointAsync` 3672-3716 / `SerializePruneCheckpoint` 3718-3764 (all N candidates, base64 x2 each, ~125 bytes per line); `SaveCheckpointAsync` binds the blob in an `UPDATE "LongRunningOperations" SET CheckpointPayload=...` (`LongRunningOperationStore.cs:1114`).

**Evidence.** `UploadedFile_prune_scaling` through the real service (`results-5-batch2.txt`): candidates -> apply CPU / per candidate: 250 -> 716 ms / 2.9 ms; 500 -> 1,613 ms / 3.2 ms; 1,000 -> 4,546 ms / 4.5 ms; **2,000 -> 13,897 ms / 6.9 ms**. The statement profile for 2,000 files: `UPDATE ... CheckpointPayload` 4,001 x = 7,946 ms of 10,104 ms SQL time (79%); at 500 files the same UPDATE costs 0.49 ms each (62 KB payload) versus 1.99 ms each (250 KB). Serialisation itself is cheap (`checkpoint_serialize`: 10,000 candidates 8 ms CPU, 976 KB, 9 MB allocated per call), so the cost is the DB write of a payload that grows with N, 2N+1 times.
Model: bytes written ~ 2 x N x (125 B x N): N = 10,000 -> ~25 GB of checkpoint payload through SQLCipher and the WAL; N = 40,000 -> 400 GB, and the 5 MiB cap (3692) refuses to save at all above ~42k candidates. (Reliability cross-reference: `ParsePruneCheckpoint` rejects more than 10,000 candidates (4102), so a crash in a larger sweep cannot be recovered.)
Uploaded files are enabled by default (30 days) and a batch workflow produces thousands.

**Fix.** Persist the immutable candidate list + frozen cutoffs once (at start), and make the per-candidate checkpoint only the cursor and the pending journal (a few hundred bytes), either as a separate row/column or by splitting the payload into header+cursor and a static body saved once. Keep the 50-candidate lease/heartbeat cadence. Effort M.

---

### 5. Per-entry conflict subqueries scan `BudgetReservations` for every run of the entry's session (Important)

**Where.** `AddEntryCandidatesAsync` 1449-1471, `AddAttachmentCandidatesAsync` 1696-1720, `AddEntryEmbeddingCandidatesAsync` 1870-1892, the three `*ProtectionDiagnostics` queries (850-1290), `DeleteEntryCandidateAsync` 5536-5552, `DeleteEntryEmbeddingCandidateAsync` 5687-5693, `ReadSessionConflictsAsync` (main file 4906+).

**Why.** Each repeats, per entry row, `EXISTS(SELECT 1 FROM BudgetReservations reservation JOIN InferenceRuns run ON lower(replace(run.Id..)) = lower(replace(reservation.RunId..)) WHERE lower(replace(run.SessionId..)) = lower(replace(entry.SessionId..)) AND reservation.Status IN (...))`. EXPLAIN: `SEARCH run USING INDEX IX_InferenceRuns_SessionId_Norm`, then `SCAN reservation` (no index on `BudgetReservations.RunId`), i.e. cost per entry = runs_in_session x reservations. The answer is a property of the *session*, but it is recomputed for every entry.

**Evidence.** 400 old entries in 2,000, 20 sessions, runs per session 2 / 10 / 40 (reservations 40 / 200 / 800), plan only (`results-5-batch2.txt`): 221 / 256 / 1,170 ms CPU; the two big entry queries take 275-277 ms each at 800 reservations and the per-candidate `ReadSessionConflicts` reservation join 0.68 ms x 400. Cost ~ E_old x runs/session x reservations x ~21 ns: 20,000 old entries, 20 runs/session, 20,000 reservations = ~170 s per statement, and the diagnostics + candidate statements both do it (and again for attachments and embeddings).

**Fix.** Share the index fix with finding 2, and compute the set of conflicted sessions once per plan (three small queries: active operations, running runs, outstanding reservations -> `HashSet<Guid>`), then exclude by session in the entry/attachment/embedding queries (or use uncorrelated `NOT IN (SELECT ...)`, which SQLite materialises once). Effort M.

---

### 6. Plans and blocker lists are unbounded (Minor)

**Where.** `BuildUnifiedPrunePlanCoreAsync` passes `limit = int.MaxValue` (line 299) into `AddEntryProtectionDiagnosticsAsync` etc. (`diagnosticLimit`), which add one `DataRetentionBlocker` per protected entry and call `AddBoundedSessionConflictDiagnosticsAsync` (a 3-way UNION query) for each, and the accounting "blocked" query adds one blocker per aged run in a retained session.

**Evidence** (`results-6-batch3.txt`): one operator-held session with 4,000 aged entries -> 4,000 blockers, ~671 KB of plan content, 4,009 statements; 4,000 aged runs in one retained session -> 4,000 blockers, ~816 KB. A 100k-entry held session is ~17 MB in the plan response and 100k extra queries; the response is built, hashed into `ComputePlanId`, and serialised on every dry-run.

**Fix.** Cap diagnostics per class (e.g. 100 detailed blockers plus a count, or one blocker per session), and ask the conflict question once per session. Effort S-M (touches the API contract text in DESIGN/API docs).

---

### 7. Statement fan-out per candidate (Minor)

**Measured statements per candidate** (profile hook): entry-embedding plan 11 (88,010 for 8,000); entry plan 15 (22,659 / 1,500); **entry apply 83** (12,399 / 150); session plan **123** (246,020 for 2,000 sessions, 4.2 s CPU); uploaded-file apply ~10. At ~12 us a statement this is minor next to findings 1-5, but it is pure waste: 4-5 per candidate are `SELECT 1 FROM sqlite_master WHERE name=@table` (`TableExistsAsync` inside `CountTableAsync`, `ReadSessionConflictsAsync`, ...) for tables whose existence cannot change during a plan, `SagaVectorMirror.ClassifyAsync` is re-run per candidate, the context-pin count (1553) and `ReadSessionConflictsAsync` (1577) duplicate conditions the candidate SQL already excluded, and identical per-session reads are repeated for every entry of the same session. Every statement is built from a fresh string and prepared again (Microsoft.Data.Sqlite does not cache statements).
**Fix.** Memoise `TableExistsAsync` for the lifetime of a plan, cache per-session conflict reads in a dictionary, delete the duplicated checks. Effort M.

---

### 8. Workspace reset: unindexed `tapestry_nodes` scope scans and a wasted fingerprint pass (Minor)

**Where.** `DataRetentionService.WorkspaceReset.cs`: `tapestry_nodes WHERE ScopeKind='Workspace' AND ScopeId=@root` at 156-179 (two DELETEs) and 460-467 / 471-481 / 489-500 / 586-631 (counts and fingerprints); the post-commit `ReadWorkspaceResetSnapshotAsync` at 246-252 only needs `TotalOwnedRows` but computes every fingerprint (full SHA-256 over every chunk id, embedding id, node id). `tapestry_nodes` indexes are `(GenerationId, Layer)`, `ParentScopeKey`, `ParentNodeId`, `(GenerationId, ChildMembershipHash)` - none on scope.
**Evidence.** EXPLAIN: `SCAN tapestry_nodes` for the count, `SCAN node` for the embedding join, `SCAN tapestry_nodes` inside the DELETE subquery. Timing: one scope count = 14 ms at 5,000 nodes and 58 ms at 20,000 nodes (1.5 KB content each) (`results-5-batch2.txt`); a reset runs ~5 such scans per snapshot x 3 snapshots + 3 in the deletes = ~18 (25 with the vec mirror) while holding the writer transaction for the apply-time ones. 200k nodes = ~10-15 s of scanning.
**Fix.** `CREATE INDEX ... ON tapestry_nodes(ScopeKind, ScopeId)`; make the post-commit reconcile read counts only. Effort S.

---

## Checked and sound (with how)

- **Session-keyed and file-role predicates are index-served**: EXPLAIN of the entries candidate query shows `SEARCH pin USING INDEX IX_SessionContextPins_SessionId_Norm`, `SEARCH run USING INDEX IX_InferenceRuns_SessionId_Norm`, `SEARCH operation USING INDEX IX_LongRunningOperations_State_LeaseExpiresAt`; the embedding join is `SCAN entry` + `SEARCH embedding USING INDEX IX_entry_embeddings_EntryId_Norm`; workspace chunk lookups use `idx_workspace_file_chunks_path`; `annal_claims`/saga/lexicon derived counts are covering-index `SEARCH`es.
- **`AddBatchFileStatusAsync` join** (status path, lines 104-159): 3 role queries over 500/1,000/2,000 batches x 1,500/3,000/6,000 files take 1-7 ms (`status_batch_file_join`), so it is index-driven; no finding.
- **Checkpoint serialisation CPU/alloc** is small (0.8 us and ~900 bytes per candidate per call); the cost is the blob write (finding 4).
- **Factory reset**: managed-tree capture costs 213 ms CPU per 20,000 files per pass (10.6 us/file, plain enumeration 10 ms); the reset does ~6 passes (three plan builds, snapshot, validate, reconcile) so ~64 us/file, ~13 s for 200k files, and it is a one-off offline operation. `DELETE FROM "Entries"` with its triggers: 5,000 rows 69 ms CPU, 20,000 rows 1,379 ms CPU (70 us/row), so a million entries is minutes inside one writer transaction - acceptable for a factory reset, noted not reported. The ~200 `COUNT(*)` statements over ~65 tables x 3 phases use covering-index scans.
- **Plan identity / finalisation** (`FinalizePlan`, `ComputePlanId`) is linear (Distinct, GroupBy, one StringBuilder).
- **Lease renewal and checkpoint cadence** in `ApplyUnifiedPruneCoreAsync` is wall-clock driven and cheap per iteration (one `HeartbeatAsync` per heartbeat interval or 50 candidates).
- **`_frozenPruneAuthorities`** is a `ConditionalWeakTable` keyed by plan reference: no unbounded retention; `AsyncLocal` planning timestamp is restored in `finally`.
- **Native AOT closure**: these partials add no reflection, no new generic instantiation of note; JSON use is source-generated (`ArcanumCoreJsonContext`).

## Probes run

1. `Explain_matrix` - 30 production statements, `explain.txt`.
2. `Embedding_plan_scaling` (1k/2k/4k/8k), `Embedding_candidates_any_micro` (Debug 10k/20k/40k) + Release console micro (10k/20k/40k).
3. `Entries_prune_scaling` (total 2k/8k/24k, 150 old), `Entries_all_old_plan_scaling` (1.5k/3k/6k).
4. `Accounting_query_raw` (25/50/100/200), `Accounting_prune_scaling` (200 complete, 400 plan complete; 50/100 and the 400 apply were cut short to free the loaded machine).
5. `UploadedFile_prune_scaling` (250/500/1000/2000), `Checkpoint_serialize_micro` (1k-40k).
6. `Entries_with_runs_plan_scaling` (2/10/40 runs per session), `Protected_session_plan_scaling` (500/2k/4k), `Accounting_blockers_for_retained_sessions` (1k/4k).
7. `Session_cutoff_scaling` (250-2000), `Tapestry_scope_scan` (5k/20k), `Status_batch_file_join_scaling`, `Factory_tree_capture_micro`, `Factory_delete_entries_micro`.
