# U24 Covenant (2), lens PERFORMANCE

Tree: worktree `codebase-review` at ac0d2d8e. Project: Arcanum.Infrastructure, directory `Covenant/`, second half. 15 files, 7,491 lines, all read in full:

- `CovenantMemoryReviewService.cs` (2540)
- `CovenantOperationGate.cs` (1501)
- `CovenantMutationService.cs` (793), `.Correction.cs` (212), `.Curation.cs` (480)
- `CovenantSchemaRepairExecutor.cs` (430), `CovenantSchemaRepairJournal.cs` (342), `CovenantSchemaRepairStartupRecovery.cs` (422)
- `GrimoireSchemaTransitionCoordinator.cs` (227), `GrimoireSchemaTransitionHostedService.cs` (114)
- `CovenantTurnHeadProbe.cs` (159), `CovenantRequestedOperationStarter.cs` (75), `DeferredCampaignPathMarkerConnectionSource.cs` (28)
- `ICampaignPathMarkerLifecycle.cs` (128), `ICampaignPathMarkerLifecycle.FullInstallationReset.cs` (40)

`units.json` was not present when I started (the orchestrator wrote `u23-files.json` for "Covenant (1)" while I worked). I derived U24 as the remainder of `Infrastructure/Covenant/` after Covenant (1) ends at `CovenantManagementService.cs`; the 15 files sum to exactly 7,491 lines, matching the stated size.

Also read for context: the SQL under `Data/Schema/Capabilities/Covenant/Canonical/Tables` and `Triggers` for every table these files touch, `Data/Covenant/CovenantPersistedAvailabilityPublisher.cs`, `Data/MemoryErasureFingerprintRelease.cs`, `Data/SqliteBusyRetry.cs`, `Data/Covenant/ICovenantConnectionSource.cs`, `Data/Schema/GrimoireSchemaBackfillRunner.cs`, `GrimoireSchemaInstaller.cs` (top level), `Core/Covenant/CovenantLimits.cs`, `CovenantCompiler.cs`, `Core/Primitives/UnicodeNfc.cs`, `Api/Tower/MemoryReviewEndpoints.cs`, DESIGN section 9 and the schema-transition paragraphs, and the tests `CovenantQueryPlanTests`, `CovenantMemoryReviewServiceTests`, `GrimoireSchemaTransitionHostedServiceTests`.

## Measurement conditions

The machine was shared with the other reviewers: load average between 54 and 172 during every run, 18 cores. The test project builds Debug. So wall-clock figures from the probes are upper bounds. Where it matters I used user CPU time (sqlite3 `Run Time user`, `Process.TotalProcessorTime`) or a Release build, and say so.

## Findings

### F1. Review-queue statements bind the campaign with `IS $campaign`, so the partial queue indexes are never used (Minor, effort S)

`CovenantMemoryReviewService.cs` lines 1231, 1265, 1298, 1347, 1859, 1909 (and 1800 in the curation revision read) all say `CampaignId IS $campaign` with a bound parameter. The schema gives the queue two partial indexes (`covenant_review_events.sql`):

```
idx_covenant_review_events_global_queue   (ScopeCode, LaneCode, Sequence DESC) WHERE CampaignId IS NULL
idx_covenant_review_events_campaign_queue (CampaignId, ScopeCode, LaneCode, Sequence DESC) WHERE CampaignId IS NOT NULL
```

SQLite can only prove a partial-index predicate from a literal `IS NULL` or an equality, never from `IS ?`. `EXPLAIN QUERY PLAN` on the shipped SQLCipher (probe `PROBE_queue_statement_plans_on_the_shipped_sqlcipher`, statements copied from the service, `EventSelect` taken by reflection):

```
ReadPage as shipped (CampaignId IS $campaign)
    SEARCH event USING INTEGER PRIMARY KEY (rowid>? AND rowid<?)          <- no queue index
ReadPage with CampaignId = $campaign
    SEARCH event USING INDEX idx_covenant_review_events_campaign_queue (CampaignId=? AND ScopeCode=? AND LaneCode=? AND Sequence>? AND Sequence<?)
ReadPage Global, literal IS NULL
    SEARCH event USING INDEX idx_covenant_review_events_global_queue (ScopeCode=? AND LaneCode=? AND Sequence>? AND Sequence<?)
ReadUpperFrontier as shipped      SEARCH covenant_review_events            <- MAX walks rowids downward, no index
ReadUpperFrontier with =          SEARCH ... USING INDEX idx_covenant_review_events_campaign_queue (CampaignId=? AND ScopeCode=? AND LaneCode=?)
ComputeReviewedThrough as shipped SEARCH event USING INTEGER PRIMARY KEY (rowid>?)
ReadMarker as shipped             SCAN covenant_review_markers
```

So each statement walks the rowid range across every scope and lane of the installation and discards non-matching rows. The repo already knows this rule: `CovenantStoreSql` builds scope-specific SQL and `CovenantQueryPlanTests` says "The Campaign subject index is partial on a non-null Campaign, so only an equality on the Campaign can use it". The review service did not follow it, and no test names either queue index (`rg idx_covenant_review_events tests` is empty), so the regression is invisible.

Cost, identical DDL and statement text in the sqlite3 CLI (3.54) over synthetic data, 300 campaigns x 50 entries, versions interleaved across campaigns, campaign 150, marker at 0:

| events in table | first page (50 rows), shipped vs `=` | frontier of an empty queue, shipped vs `=` | reviewed-through scan, shipped |
|---|---|---|---|
| 90,000 | 5.0 ms vs 0.36 ms | 5.1 ms vs 0.03 ms | 4.5 ms |
| 360,000 | user CPU 24 to 31 ms vs 1.0 ms | user CPU 25 ms vs 0.07 ms | not measured |

Cost is linear in total events installation-wide, not in the size of the queue. Events are bounded by versions, at most `MaxVersionsPerScope` = 8,192 per scope, multiplied by the number of campaigns, so the installation-wide count is unbounded in campaigns. A first list of any campaign, a list of an empty queue, and every apply (`ComputeReviewedThroughAsync` scans from the marker to the first unreviewed current event across all scopes) pay it, inside the write transaction of F2. The two partial indexes are also dead weight on every head change.

Fix: build two statement variants per query (Global: `CampaignId IS NULL` literal; Campaign: `CampaignId = $campaign`), as `CovenantStoreSql` does, for ReadMarker, ReadUpperFrontier, ReadPage, ReadEvent, ComputeReviewedThrough, AdvanceMarker, ReadCurationRevision. Add plan assertions beside `CovenantQueryPlanTests` that name `idx_covenant_review_events_{global,campaign}_queue` and `ux_covenant_review_markers_*`.

### F2. List and Prepare open `BEGIN IMMEDIATE` for reads (Minor, effort S)

`CovenantMemoryReviewService.cs` lines 134-136 (`ListAsync`) and 367-369 (`PrepareAsync`) call `connection.BeginTransactionAsync(cancellationToken)`. In Microsoft.Data.Sqlite 10.0.10 that issues `BEGIN IMMEDIATE`, the database's single writer lock, even though `PrepareAsync` never writes and `ListAsync` writes only the marker `INSERT OR IGNORE` (line 1186) the first time a queue is seen. The repo uses `BeginTransaction(deferred: true)` for its real read snapshots (`CovenantEntryErasureService` 243 and 987, `SessionTurnClaimStore` 71).

Probes:
- Stand-alone (`txmode`): after `BeginTransactionAsync(ct)`, `BeginTransactionAsync(Serializable)`, `BeginTransaction(deferred:false)` and `BeginTransaction()` a second connection's `BEGIN IMMEDIATE` is refused; after `BeginTransaction(deferred:true)` it is not.
- Through the real service (`PROBE_list_and_prepare_hold_the_database_write_lock`): the lease `RevalidateAsync` that runs inside the page transaction tries `BEGIN IMMEDIATE` on a second connection. It is blocked during `ListAsync` and during `PrepareAsync`.
- Contention (`PROBE_list_waits_behind_another_writer_but_a_deferred_read_does_not`, marker pre-created so the list is a pure read): another connection holds `BEGIN IMMEDIATE` for 1,500 ms. A deferred snapshot read returns in 0.1 ms. `ListAsync` returns after 1,535 ms.
- Exhausted wait (`PROBE_list_throws_instead_of_mapping_a_busy_database`): when the writer outlasts the wait, `ListAsync` throws `SqliteException: SQLite Error 5: 'database is locked'`. The apply path maps exactly this to the retryable 503 (`MapApplyFailure`, lines 499-509, `ErrorCodes.Covenant.Unavailable`); List and Prepare have no catch, and `MemoryReviewEndpoints.WithCovenantReadAsync` (lines 435-490) has none, so it is an unhandled 500.

Hold time of the lock on a small database (Debug, loaded): `ListAsync` of 50 rows plus 50 source reads 0.97 ms; `PrepareAsync` of 50 Confirm decisions 2.0 ms. At scale F1's scan is added inside the same lock. In the other direction every session-turn writer waits behind a review page.

Fix: `PrepareAsync` uses `BeginTransaction(deferred: true)`. `ListAsync` reads deferred; if the marker is missing, end the snapshot, create it in a short immediate transaction, reopen deferred. Map busy exceptions in List and Prepare to the existing `DatabaseBusy` answer.

### F3. `ListAsync` persists a marker row for any campaign id a caller names (Minor, effort S)

`ReadOrCreateMarkerAsync` (lines 1163-1212, called from `ListAsync` line 147) does `INSERT OR IGNORE INTO covenant_review_markers` for whatever `(generation, scope, campaignId, lane)` the request names. Nothing checks the campaign exists: `request.Validate()` checks shape only, `gate.AcquireReadAsync` takes no campaign probe (only the exclusive acquisitions call `RequireLiveCampaignAsync`), and the endpoint adds none. Probe `PROBE_listing_an_unregistered_campaign_persists_a_marker_row`: 25 lists of 25 never-registered ids leave 25 rows. The only deleter is `CovenantCleanupWorker.cs:232`, run once per campaign-deletion event, so a stale client listing a campaign after its cleanup recreates an orphan permanently. Growth is keyed on user input with no ceiling, and `ReadMarkerAsync` scans the table (F1), so each row also taxes every later review call.

Fix: resolve the campaign through `ICovenantCampaignScopeProbe` and refuse deleted/unknown ones before creating a marker, or do not persist on read (answer with a synthetic revision-1 marker and create it at apply).

### F4. The schema-transition driver sleeps 15 s after every pass, whatever the pass cost (Minor, effort S)

`GrimoireSchemaTransitionHostedService.Interval` = 15 s (line 37) is applied after any advancing pass (line 63); a pass is capped at `MaxBatchesPerPass` = 16 batches (coordinator line 57). So throughput has a hard ceiling of 16 x `MaxRowsPerBatch` / 15 s whatever the hardware: 273 rows/s for the 256-row UTC canonicalization, 213 rows/s for the 200-row sweeps, 137 rows/s for LongRestSourceIdentity (128). Declared bounds from `Data/Schema/*Backfill.cs`.

Probe `PROBE_backfill_pass_duration_versus_the_15s_interval` (the real `UtcInstantCanonicalizationBackfill`, 16 batches in `BEGIN IMMEDIATE` transactions on SQLCipher, 20,000-row table with no triggers, so an optimistic per-batch cost): one pass processes 4,096 rows in 94 ms, then the service sleeps 15 s. Duty cycle 0.6%; effective 271 rows/s against 43,394 rows/s back to back. Real tables carry FTS and guard triggers so a batch costs more, but the cap is independent of that.

Why it matters: a run in flight keeps its tier unavailable by design (DESIGN: `TransitionIncomplete`, "a Core tier mid-run stands its dependents down"). The Core v9 UTC sweep walks `Entries`, `session_attachment_chunks`, `saga_memories`, `owner_deletion_events` and about 60 other tables (`UtcInstantColumnInventory.CoreVersionNine`), and the Core v7 sweep walks `Entries` at about 190 per batch, so the row count is the size of the user's message and attachment history. For 500,000 swept rows the Covenant capability is unavailable for about 31 minutes (256-row sweep) to 61 minutes (128-row sweep), more than 99% of it sleeping; 1.5 million rows (entries plus attachment chunks) is about 90 minutes. This is upgrade-time only and the data is intact, but the operator has no knob.

Fix: after an advancing pass wait a short yield (for example the pass duration times a small factor, or 250 to 500 ms) rather than 15 s; keep the 5-minute idle interval for "nothing in flight".

### F5. A pass refused admission, or one that failed, is scheduled like an idle pass (Minor, effort S, trace only)

`RunOnceAsync` returns `false` when `TryAcquireWorkLease` is refused (lines 78-83) and when the coordinator returns a failure (lines 94-100); `ExecuteAsync` maps `false` to `IdleInterval` = 5 minutes (line 63). `GrimoireSchemaTransitionHostedServiceTests.SchemaPassDenialCreatesNoScope` shows the closed-gate case returns false. The gate is closed in production by the exclusive Covenant operations (erasure, reset), so a pending run (capability down) that meets one waits up to 5 further minutes after the window reopens instead of 15 s. Rare window, so low priority. Not executed; traced from the code and that test.

Fix: return a three-way result (idle, advanced, deferred) and retry deferred and failed passes at `Interval` (or a short backoff).

## Probes run (all in the scratch worktree, now removed; sources kept)

Kept in `/private/tmp/claude-501/-Users-mat-Source-apps-RetroDownfall-Arcanum/d111c03e-eda5-4c82-a12d-f02f9cd1cdb8/scratchpad/review/probe-u24/`:

- `CovenantMemoryReviewServiceTests.probes.patch`: the six `PROBE_*` facts added to the existing review test class (uses its `ReviewRuntime`).
- `U24PerfProbe.cs`: gate micro-benchmark, compiler timing (Debug), installer converge timing, backfill pass duration.
- `txmode/`: stand-alone console proving which `BEGIN` each Microsoft.Data.Sqlite call issues.
- `compilebench/`: Release micro-benchmark of `CovenantCompiler.Compile`.
- `gen.sql`, `gen2.sql`, `plan1.sql`, `time1-3.sql`: sqlite3 CLI plan and timing scripts over the real DDL.
- `results*.txt`: raw output.

Raw numbers: `results-run1.txt` (plans, lock, 0.97 and 2.01 ms), `results-run3.txt` (contention 1535 ms vs 0.1 ms), `results.txt` (busy exception, orphan markers), `results2.txt` (gate, installer, backfill).

## Checked and found sound

- `CovenantOperationGate`: acquisition and revalidation take one lock for dictionary work plus `Guid.NewGuid()`; no I/O. Measured (Debug, load): 5.2 us and 752 bytes per `AcquireTurn` + dispose single-threaded, 200,000 acquisitions on 8 workers in 336 ms. Revocation callbacks run outside the lock; drain is `Task.WhenAll(...).WaitAsync(30 s)`; closures are removed on every exit path; nothing in it grows (registrations removed on release; at most three slots plus one closure per closing campaign). `ExecuteWhileHeld` callbacks do filesystem stats under the lock (`BackupRestoreCovenantCoordinator`), but only in the restore CLI process whose gate has no ordinary traffic.
- Review reads other than F1: receipt lookup is a primary-key prefix range (already plan-tested); `ReadEventAsync` is a rowid lookup; sources by `(VersionId, Ordinal)` primary key; event sequence by the covering `UNIQUE(VersionId)`; receipts `EXISTS` via the covering `idx_covenant_review_decision_receipts_event`; page joins use `ux_covenant_heads_current_version`, the versions and entries primary keys (plans above).
- N+1 shapes (one source read per row, one event/fingerprint read per decision) are real but bounded: `MemoryReviewLimits.MaxPageSize` = `MaxBulkOperations` = 50; measured 0.97 ms and 2.0 ms per call. Not reported.
- Compiling each correction up to three times (prepare check, apply check, apply) is cheap: `CovenantCompiler.Compile` in Release is 28 us CPU at 100 chars, 31 us at 500, 60 us at 1,000, 125 us at 2,000 (linear), so 50 corrections cost about 19 ms of CPU. A Debug probe under load showed 13 ms per call; that was contention and unoptimised code, not the algorithm.
- Post-commit `RepublishAsync`: one singleton-row read; its `MAX(Sequence)` over `owner_deletion_events` is served by `idx_owner_deletion_events_kind_sequence`.
- Mutation and curation replay reads (`TryReplayAsync`, `TryReplayCurationAsync`) are primary-key lookups with bounded correlated subqueries (at most `MaxStableEntriesPerScope` = 256 curation heads in scope).
- Schema repair executor, journal and startup recovery: cold paths. The journal read is non-sargable (`typeof(PhaseCode) <> 'integer' OR PhaseCode NOT IN (5, 6)`) but the table holds one row per operator-triggered repair. The inspector runs only when an active journal exists. Catalog digest reads `sqlite_master` once (790 objects in a full Grimoire).
- `GrimoireSchemaTransitionCoordinator.ConvergeAsync` re-enters the installer after an advancing pass. A tier whose sweep is draining returns `Incomplete` without DDL; a full head install (`BEGIN IMMEDIATE` plus about 790 DDL statements and inspection) measured 243 to 501 ms (Debug, loaded) and is paid once per completed step, plus on each pass of a Covenant-tier sweep while Core is current. Not reported separately.
- Idle cost of the transition service: one scope, one journal read, one SQLCipher connection (54.5 ms to open and initialise an additional connection in passphrase mode) every 5 minutes.
- `CovenantRequestedOperationStarter`, `DeferredCampaignPathMarkerConnectionSource` (a `GetRequiredService` per call), the two `ICampaignPathMarkerLifecycle` interface files, and `CovenantTurnHeadProbe` are thin delegations with no loops or allocation of note.
- `PrepareCorrectAsync` reads the entry detail twice with identical arguments (`ResolveTargetAsync` then `PrepareAsync`); operator-only and rare, a few milliseconds, not reported.

## Not covered

`CovenantStore.ReadMutationEffectSnapshotAsync` and `ReadDetailAsync` (called from `PrepareAsync`) and the mutation and curation kernels belong to other units; the Global-scope effect snapshot walks campaigns and is worth a look by that unit's reviewer. I did not time the published binary: nothing in this unit is on the startup path other than a one-row journal read.
