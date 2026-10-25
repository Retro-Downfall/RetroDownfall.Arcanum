# U24 — Arcanum.Infrastructure "Covenant (2)" — reliability lens

Tree: `/Users/mat/Source/apps/RetroDownfall.Arcanum/.claude/worktrees/codebase-review` at `ac0d2d8e`.

`units.json` was not present in the scratchpad when this review started. The unit's file list was rebuilt from the
sibling `u23-files.json` (U23 = "Covenant (1)", 9,964 lines, ending at `CovenantManagementService.cs`). U24 is the rest of
`src/RetroDownfall.Arcanum.Infrastructure/Covenant/`. Its line counts add up to exactly 7,491, which matches the unit size the
task gives.

## Files (all read in full)

| File | Lines |
|---|---|
| CovenantMemoryReviewService.cs | 2540 |
| CovenantMutationService.Correction.cs | 212 |
| CovenantMutationService.Curation.cs | 480 |
| CovenantMutationService.cs | 793 |
| CovenantOperationGate.cs | 1501 |
| CovenantRequestedOperationStarter.cs | 75 |
| CovenantSchemaRepairExecutor.cs | 430 |
| CovenantSchemaRepairJournal.cs | 342 |
| CovenantSchemaRepairStartupRecovery.cs | 422 |
| CovenantTurnHeadProbe.cs | 159 |
| DeferredCampaignPathMarkerConnectionSource.cs | 28 |
| GrimoireSchemaTransitionCoordinator.cs | 227 |
| GrimoireSchemaTransitionHostedService.cs | 114 |
| ICampaignPathMarkerLifecycle.FullInstallationReset.cs | 40 |
| ICampaignPathMarkerLifecycle.cs | 128 |

I also read the callers and collaborators needed to trace behaviour. These were read in the parts named, not reviewed as part
of this unit:
- `CovenantMaintenanceService.cs` (U23): `RepairSchemaAsync`, `ExecuteRepairAsync` and `FinishAsync`.
- `CovenantExclusiveDisposition.cs` (U23): read in full.
- `CovenantOperationLeaseContracts.cs` (Core): the lease base classes.
- `CovenantMutationEndpoints.cs` and `MemoryReviewEndpoints.cs`: the read helper.
- `ArcanumExceptionHandler.cs`.
- `SqliteBusyRetry.cs`: header and `ExecuteAsync`.
- `CovenantAvailabilityRepublisher`.
- `CovenantStore.ReadRetirementTargetAsync`.
- `CovenantOperatorCurationFactory`.
- `MemoryDelegationMutation`.
- The DDL of `covenant_schema_repair_intents` and its two guard triggers, the review events, receipts and markers tables,
  the curation heads and receipts tables, the mutation receipts table, and the `covenant_review_events_head_update` trigger.
- `GrimoireSchemaTransitionJournal` row semantics.

Owning docs read:
- DESIGN §10.17: schema repair, and "What is deliberately absent".
- DESIGN §10.22: the section headers.
- API §8 for the review error contract (the R-171 paragraph) and the error-code to status table.
- Command Reference §memory covenant, the line saying the maintenance port is not registered.

## Findings

### F1 — Minor (latent; would be Critical once the maintenance port is routed): a failed schema repair leaves an active `Prepared` journal that closes Covenant admission on every later start, with no way out

- **Where:** `CovenantSchemaRepairStartupRecovery.cs:204-211`, which turns a failed repair into `Kept("repair")`. The journal
  is left at `Prepared`, even when nothing was mutated.
- **Related:**
  - `CovenantMaintenanceService.cs:217-228` (U23): a failed repair goes straight to `FinishAsync` with `RollbackAndReopen`
    while the journal is still at `Prepared`.
  - `CovenantSchemaRepairExecutor.cs:187-195,264`: failures are reported after DDL may already have run.
  - `Data/Schema/Triggers/covenant_schema_repair_intents_guard_update.sql`: allows only `1→{2,4}`, `2→3`, `3→4` and `4→{5,6}`.
  - Test `CovenantSchemaRepairTests.Recovery_keeps_a_failed_repair_closed_and_leaves_its_journal_active`: pins the
    keep-closed half of this.

**Trace (proved by probe P1):**
1. `RepairSchemaAsync` commits a `Prepared` intent (phase 1, revision 0).
2. `RepairAsync` fails. This happens for an action that does not fit the catalog (`InstallAbsentCanonicalFamily` over a
   present family, or `RepairExistingFamily` over an absent one), for an installer result that is not healthy, for a
   `SqliteException`, or for an index statement that is not found or that fails.
3. `ExecuteRepairAsync` calls `FinishAsync(intent@Prepared, RollbackAndReopen)`.
4. The gate reopens. The finalizer then tries to move the journal from `Prepared` to `Abandoned` (1→6). The trigger aborts
   that edge, so `TryAdvanceAsync` returns `MaintenanceFailed` and `CompleteAsync` returns `ManualRecoveryRequired`.
5. `FinishAsync` reports `Phase = ReopenPending` (line 366, because the finalizer did not finish). The durable row is in
   fact still `Prepared`.
6. On the next start, `PrepareBeforeEffectsAsync` adopts the owner, which installs the installation closure.
   `RecoverPreparedAsync` finds the catalog digest unchanged and reruns the same repair. The repair fails the same way, so
   the pass returns `Kept("repair")`, then `KeptClosed`, and the lease is disposed without a disposition, so the closure
   stays installed.
7. From then on, all of the following are refused with `Covenant.Unavailable`:
   - every ordinary Covenant lease: installation read, scoped read/write, turn, MCP, cleanup and accelerator;
   - every other exclusive operation: a different schema repair, a Covenant reset, a family reinitialize, a restore, and a
     factory erasure through the gate.

   No route can resume the adopted owner, because the in-request path always mints a new `OperationId`. The only exits are
   editing the database by hand or a factory reset.

**Probe P1 output** (scratch copy of `CovenantSchemaRepairTests`, real triggers, stub executor with `RepairFails = true`,
action `RepairExistingFamily`):
```
PROBE1 result.IsSuccess=True disposition=ManualRecoveryRequired reportedPhase=ReopenPending blocking=Covenant.MaintenanceFailed durablePhase=1
PROBE1 same-process new exclusive after failure: success=True
PROBE1 start#1 outcome=KeptClosed phase=1 read.ok=False read.code=Covenant.Unavailable otherRepair.ok=False otherRepair.code=Covenant.Unavailable reset.ok=False
PROBE1 start#2 outcome=KeptClosed phase=1 read.ok=False read.code=Covenant.Unavailable otherRepair.ok=False otherRepair.code=Covenant.Unavailable reset.ok=False
```

**A neighbouring defect in the same path.** `CovenantSchemaRepairExecutor.RepairAsync` returns a failure after durable DDL
may have committed:
- the installer ran but its result is not healthy (line 187, `ManualRecovery`);
- index N failed after indexes 1 to N-1 were created as autocommit statements (line 264).

The in-request path then selects `RollbackAndReopen`, asserting that nothing was mutated, about a catalog that changed. The
startup path keeps it closed, and the next start then sees `catalog-moved` and stays closed for good.

**Why it is latent.** DESIGN §10.17 says `ICovenantMaintenanceService` is registered in no container and no route reaches
it, so no production `Prepared` journal can be written today. `CovenantSchemaRepairStartupRecovery` itself is live and runs
on every host start (`GrimoireDatabaseBootstrapper.cs:1209`). The moment the port is routed, as the frozen contract
intends, this becomes an operator blocked with no workaround.

**No test catches it.** No test exercises `RepairSchemaAsync` with a failing repair. The only service tests stop at
inspection failure or a non-exact connection.

**Suggested fix:**
- When `RepairAsync` fails, reinspect. If the catalog digest still equals `InspectedCatalogDigest`, take the proven
  no-mutation path in both places: advance `Prepared→ReopenPending`, then `RollbackAndReopen`, then the finalizer moves the
  journal to `Abandoned`.
- If the digest moved, keep it closed, but advance through `CatalogCommitted` so the next start does not answer
  `catalog-moved`.
- Make `RepairAsync` report "mutated" alongside the failure, for example `Result<(bool Mutated, Error?)>`.
- Make `FinishAsync` report the durable phase, not an inferred one.
- Amend the pinned test to expect `Abandoned` and a reopen when the digest is unchanged.

**Effort:** M.

### F2 — Minor (latent): a restart treats every `ReopenPending` journal as durably mutated, so a no-op repair interrupted before its finalizer is kept closed or misfiled

**Where:** `CovenantSchemaRepairStartupRecovery.cs:192`
(`bool durablyMutated = current.Phase is not CovenantSchemaRepairPhase.Prepared;`) feeding lines 287-292.

`ReopenPending` is reached two ways:
- the proven no-mutation edge, `Prepared→ReopenPending`, at revision 1;
- the committed path, `Prepared→CatalogCommitted→HealthVerified→ReopenPending`, at revision 3.

DESIGN §10.17 names "a completion that fails after the gate had already acted" (a finalizer failure) as the case that
leaves the journal at `ReopenPending` "until the next start resumes it". A process kill between the `ReopenPending` commit
and the finalizer does the same.

On restart the code assumes mutation:
- If the canonical tier is not valid, `HealthPublished` is false and the disposition is `KeepClosed`. The journal stays at
  `ReopenPending` and admission stays shut on every start. This is exactly the catalog where a `RepairOrdinaryIndex` no-op
  happens: an invalid family with no missing index, for example a missing trigger, which no action may repair. The
  original process would have chosen `RollbackAndReopen`.
- If the canonical tier is valid, the disposition is `CommitAndReopen` and the journal is filed `Completed` instead of
  `Abandoned`.

**Probe P2** (scratch copy; real journal and triggers; `Prepared→ReopenPending` through `TryAdvanceAsync`, then
`RecoverBeforeReadinessAsync`):
```
PROBE2 canonicalValid=False outcome=KeptClosed terminalPhase=4 repairCalls=0
PROBE2 canonicalValid=True outcome=RecoveredReady terminalPhase=5 repairCalls=0
```
The second line shows `Completed` (5) where it should be `Abandoned` (6). The first line shows a journal that never
terminates.

**Suggested fix.** The trigger forces `NEW.Revision = OLD.Revision + 1`, and the insert writes revision 0, so the path is
already recorded in the row: derive
`durablyMutated = current.Phase switch { Prepared => false, ReopenPending => current.Revision != 1, _ => true }`. Add a
test for both branches.

**Effort:** S.

### F3 — Minor (live): operator Covenant set, retire, correct and curate commits turn a busy database into a generic 500 after up to 30 s (a new angle on R-171)

- **Where:** `CovenantMutationService.cs:509-511`, in `CommitAsync`: `BeginTransactionAsync(Serializable)` opens
  `BEGIN IMMEDIATE`, with no `SqliteBusyRetry` and no exception mapping.
- **Same shape:** `CovenantMutationService.Curation.cs:297-299`, in `CommitCurationAsync`.
- **Related:** `CovenantMutationEndpoints.cs`, which has no catch, and `ArcanumExceptionHandler.cs`, the generic 500
  `Hub.Unhandled` branch.

R-171 fixed this class of fault for the review apply only. `CovenantMemoryReviewService.ApplyAsync` retries busy for up to
2 minutes and answers 503 `Covenant.Unavailable`, "retry the same request", with nothing written, as documented in API §8.
The four operator commit routes over the same database still let the `SqliteException` escape:
- `Microsoft.Data.Sqlite` retries inside one command for `DefaultTimeout` (30 s).
- The exception handler then answers **500 `Hub.Unhandled` "An internal error occurred."** and logs the exception object
  at Error, including the driver text. The review path logs by type and code only, on purpose.

`SqliteBusyRetry`'s own remarks say exclusive-maintenance holds longer than 30 s exist and are the reason its default is
2 minutes.

**Probe P3** (scratch copy of `CovenantMutationServiceTests`; a second connection holds `BEGIN IMMEDIATE`; the service
connection has `DefaultTimeout=1` and `busy_timeout=50`):
```
PROBE3 elapsedMs=1157 thrown=SqliteException sqlite=5 answered=-
```

**Suggested fix:**
- Wrap the transaction body of `CommitAsync` and `CommitCurationAsync` in `SqliteBusyRetry.ExecuteAsync`. Every attempt
  starts fresh, and the kernel already resolves a raced replay inside the transaction.
- Map an exhausted busy or `GrimoireBusyTimeoutException` to `Covenant.Unavailable`, and other storage faults to
  `Covenant.WriteFailed`, exactly as `CovenantMemoryReviewService.MapApplyFailure` does. Log by type and code only.
- Document the 503 on the four routes in API §8.28.

**Effort:** S.

### F4 — Minor (live): review list and prepare, both reads, take the database write lock, so any writer blocks them and a busy database surfaces as a 500

**Where:**
- `CovenantMemoryReviewService.cs:134-136` (`ListAsync`) and `:367-369` (`PrepareAsync`): `BeginTransactionAsync(ct)`
  goes through `SqliteConnection.BeginTransaction(Unspecified)`, which runs `BEGIN IMMEDIATE`. `ListAsync` needs it only
  for `ReadOrCreateMarkerAsync`'s one-time `INSERT OR IGNORE`. `PrepareAsync` writes nothing.
- Neither method catches anything, and `MemoryReviewEndpoints.WithCovenantReadAsync` has no catch.

Under WAL, a deferred read transaction is never blocked by a writer. These two reads are blocked by every concurrent
Grimoire writer, and after 30 s they answer the generic 500.

**Probe P4** (scratch copy of `CovenantMemoryReviewServiceTests`; a second connection holds `BEGIN IMMEDIATE`):
```
PROBE4 list under a held writer: thrown=SqliteException sqlite=5
```

**Suggested fix:**
- Run `ListAsync` and `PrepareAsync` in a deferred read transaction.
- Create the marker only when it is absent, in a separate short `BEGIN IMMEDIATE`, under `SqliteBusyRetry`, before the
  read. Alternatively, treat an absent marker as `(ReviewedThrough 0, Revision 1)`, which is what the insert would create,
  and create it in apply.
- Map busy to 503 `Covenant.Unavailable` as in F3.

**Effort:** M.

## Checked and sound

- **`CovenantOperationGate`, close ordering.** The closure is installed (`TryInstallClosure`) and new acquisitions are
  refused under one lock before the drain starts. `AcquireOrdinary` re-checks `IsAcquisitionBlocked` under the lock after
  capturing facts outside it, so an acquire that races a closure is refused or is in the drain set. This is consistent with
  `Closure.Covers` for all three slots. Checked by reading the code against `IsAcquisitionBlocked`, `Covers` and
  `TryInstallClosure`.
- **Gate, revocation outside the lock.** `RequestRevocation` contains `ObjectDisposedException` (a racing release) and
  `AggregateException` (a faulty callback). The drain times out after 30 s, or the caller's token fires, and in either case
  the closure is removed, so none is left half-installed. `InstallExclusiveRegistration` removes the closure if it throws.
  The `Lock` is reentrant for `ValidateTurnLeaseAsync` → `RevalidateAsync` and for `ExecuteWhileHeld` → `RevalidateExclusive`.
  Release compares the exact instance, and dispose exchanges a flag, so only one release happens.
- **Exclusive lease completion.** `CompleteAsync` observes cancellation once, before the disposition is claimed, and the
  claim cannot be retried with another code. `KeepClosed` keeps the closure and its owner, and `ReleaseExclusive` then
  allows a `Resume` by the same owner only.
- **Review apply.** Busy is retried per attempt, and the erasure gate is captured before `BEGIN` on each attempt. The raced
  replay is re-read inside the transaction. The lease is revalidated before `COMMIT`. A cancelled `CommitAsync` does not
  commit, and dispose rolls back, so there is never a half-written receipt or marker. The marker advance is a
  compare-and-swap on `(ReviewedThrough, Revision)`. The post-commit republish cannot throw, because
  `CovenantAvailabilityRepublisher` catches everything, so a committed apply is never reported as failed.
- **Review receipts and replay.** The prefix range scan on the `DecisionId` primary key, the fixed-time digest
  comparisons, the per-ordinal `TryAdd` duplicate guard, and the generation check are all sound.
- **Mutation service.** It is receipt-first, before the token is decoded. The request digest is recomputed by the same
  factory for prepare and commit. Issued-at and expires-at must agree between body and header. The correction target is
  compared outside the digest, as the code documents. The post-commit `RepublishAsync` is unguarded, but safe because the
  republisher never throws (checked by reading it).
- **Curation replay.** The LEFT JOIN omits `ScopeCode`, but that is safe because of `CHECK ((ScopeCode = 1 AND CampaignId
  IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL))` on both tables.
- **`CovenantTurnHeadProbe.ResolveRetirementPreflightAsync`.** It hard-codes `CovenantScope.Campaign`, which is safe
  because `CovenantStore.ReadRetirementTargetAsync` refuses a turn that is not bound to a Campaign first.
- **Schema repair inspection.** Every index the canonical manifest declares contains "covenant", so
  `MissingOrdinaryIndexesAsync`'s `LIKE '%covenant%'` cannot misreport one as missing (checked against all 38
  `CREATE INDEX` names in `Capabilities/Covenant/Canonical/Tables`). `LIKE 'covenant_%'` in the catalog digest
  over-includes, which only makes the digest stricter. The journal parse refuses malformed rows content-free, and two
  active intents are refused.
- **`GrimoireSchemaTransitionCoordinator` and its hosted service.** Busy is retried per runner pass. A non-cancellation
  failure records a bounded code with `CancellationToken.None`. Shutdown cancellation breaks the loop cleanly. The hosted
  service holds one admission lease and one scope per pass.
- **`CovenantRequestedOperationStarter` and `DeferredCampaignPathMarkerConnectionSource`.** Both are thin pass-throughs
  with no state.
- **Baseline.** 185 existing tests over these classes pass in the review tree (`CovenantSchemaRepairTests`,
  `CovenantMemoryReviewServiceTests`, `CovenantMutationServiceTests`, `CovenantOperationGate*`,
  `GrimoireSchemaTransition*`, `CovenantTurnHeadProbe*`).

## Noted, not filed

A review apply that is retried after the operator erased one of its target entries finds fewer receipts than decisions
(they are cascade-deleted with the version's review event). It answers 409 `MemoryReview.RequestReuse`, which is
misleading, instead of a replay or a stale answer. This needs an erasure between the apply and an exact retry. I traced it
but did not probe it, so it is not filed.

## Probes run

All four probes ran in the scratch worktree `wt/U24-reliability` (since removed), built with
`dotnet build tests/RetroDownfall.Arcanum.Tests/... -v q` and run with
`dotnet test --no-build --filter FullyQualifiedName~PROBE_U24 </dev/null`. Result: 4/4 ran, with the outputs quoted
above.

1. `CovenantSchemaRepairTests.PROBE_U24_failed_in_request_repair_leaves_a_prepared_journal_that_wedges_every_later_start`.
   Uses `CovenantMaintenanceService` over the real journal and triggers, a stub executor with `RepairFails = true`, then
   two simulated restarts with a fresh gate and `CovenantSchemaRepairStartupRecovery`.
2. `CovenantSchemaRepairTests.PROBE_U24_restart_treats_a_no_mutation_reopen_pending_journal_as_mutated`. Commits
   `Prepared`, advances to `ReopenPending`, then recovers with `canonicalValid` false and then true.
3. `CovenantMutationServiceTests.PROBE_U24_a_busy_database_escapes_the_operator_set_as_an_exception`. Prepares a set, has
   a second connection hold `BEGIN IMMEDIATE`, then calls `SetAsync` with `DefaultTimeout=1`.
4. `CovenantMemoryReviewServiceTests.PROBE_U24_a_review_list_takes_the_write_lock_and_a_busy_database_escapes_as_an_exception`.
   Seeds a head, has a second connection hold `BEGIN IMMEDIATE`, then calls `ListAsync` under a read lease.
