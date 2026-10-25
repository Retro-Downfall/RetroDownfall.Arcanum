# U23 (Arcanum.Infrastructure, "Covenant (1)") - performance review

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e` (read-only). Lens: performance. Probes ran in a scratch worktree
(`scratchpad/review/wt/U23-performance`, detached at `ac0d2d8e`, removed afterwards).

## Note on the unit list

`scratchpad/review/units.json` did not exist when this review ran (the scratchpad was empty). The unit was reproduced exactly
by re-running the cutting script recorded in the parent session transcript against `git ls-files`: area `Covenant (1)` of
project `Arcanum.Infrastructure` is 20 files, 9,964 lines, the first 20 files of `src/RetroDownfall.Arcanum.Infrastructure/Covenant/`
in path order. Both the file count and the line total match the task text.

## Summary

The unit is mostly cold code (reset, restore, erasure, schema repair, family reinitialize; the last three are dormant or
operator-only) plus two warm paths: the per-turn `CovenantContextProvider.BeginTurnAsync` and the five-minute
`CovenantMaintenanceHostedService`. The per-turn path is cheap and already benchmarked (see "Checked and sound"). The
findings are all in how the maintenance driver and its coordinators behave over time, plus one scan cost under the
exclusive closure of an entry erasure.

| # | Severity | Finding | Effort |
|---|---|---|---|
| 1 | Important | A single outbox sequence larger than 512 rows is never applied; one cleanup batch can create one. The search accelerator is then stuck on the fallback forever, status says "Synchronizing", and nothing logs it | S |
| 2 | Minor | Every idle maintenance pass bumps the Covenant availability generation twice with nothing changed; that counter is hashed into the DCI v2 prompt-cache scope marker at the start of the system prompt | S |
| 3 | Minor | The owner-cleanup sweep drains at most 64 deletion events per five minutes (768 per hour) in one ordered queue shared by Sessions and Campaigns | S |
| 4 | Minor | A live entry erasure scans every version/receipt/provenance row in the installation (normalised-identity predicates) three times while the Covenant scope is closed; measured 61 ms per pass at 20,000 versions | M |

---

## 1. Important - one outbox sequence over 512 rows wedges the search accelerator permanently

**Where.** `Data/Covenant/CovenantSearchOutboxWorker.cs:113,150-155,216-238` (the algorithm), driven with the 512-row bound by
`Covenant/CovenantMaintenanceHostedService.cs:147` through `Covenant/CovenantMaintenanceCoordinators.cs:132-134`. The
sequence-size producer is `Data/Covenant/CovenantCleanupWorker.cs:118-124,161-164`.

**What is wrong.**

- The worker applies "whole sequences only". `SynchronizeAsync` reads `maxRows + 1` pending rows (line 113), then
  `ChooseContiguousTarget` (216-238) walks the sequences in order and stops at the first one that does not fit in the bound:
  `if (checked(counted + size) > maxRows) break;`. If the first pending sequence alone holds more than `maxRows` rows, `target`
  stays at `applied`, and lines 152-155 return `new CovenantOutboxSyncOutcome(applied, 0, 0, 0, RebuildRequired: pending.Length > maxRows)`
  as a success. Nothing is written, so the next pass reads the same rows and returns the same answer. Because the stuck
  sequence is the oldest, no later (small) sequence can ever be reached either.
- `CovenantCleanupWorker.RunBatchAsync` gives every delta of a batch the same sequence (it advances the search sequence once and
  numbers outbox ordinals 0..N-1 across all owners, lines 118-124 and 161-164), and a batch takes up to 64 owner events. A
  Campaign emits one outbox row per head. So one cleanup batch over a few well-populated Campaigns produces a single sequence of
  hundreds of rows. A Campaign may hold up to 256 entries, i.e. up to 512 heads (`MaxStableEntriesPerScope`), so even two full
  Campaigns exceed 512.
- The driver ignores the signal: `CovenantMaintenanceHostedService` reports only `ProjectionsWritten` ("0 projection(s)") at Debug
  level (lines 150-152), and `CovenantSearchOutboxCoordinator` commits an empty transaction and republishes the unchanged
  tuple. `CovenantSearchHealthRule` then reports `Synchronizing` with guidance `WaitForSynchronization` (applied tuple names this
  dataset, so "the outbox continues from it"). DESIGN 10.22.6 says nothing in this release starts an index rebuild, and
  `ICovenantMaintenanceService` is registered in no container, so there is no remedy short of a Covenant reset.

**Cost.** Search never leaves the canonical fallback (at most 2,048 candidates materialised and `LIKE`-filtered per query,
reported as `Truncated` past that); the pending outbox grows without bound toward `MaxPendingSearchOutboxRows` (65,536,
`CovenantScopeCapacity.cs:41`), at which point canonical writes are refused as "pending search-outbox rows". The condition is
uncommon (several well-populated Campaigns deleted within one five-minute window, or a backlog that built up while the host was
down), but once hit it is permanent and silent.

**Reproduction (probe P1, real worker, coordinator-equivalent gate, scratch SQLCipher Grimoire).**
6 Campaigns x 100 Confirmed heads (600 heads), projection fully caught up (applied = canonical = 600). Delete all six Campaigns,
run one `CovenantCleanupWorker` batch (64-event bound), then run the worker with `DefaultBatchRows = 512`:

```
P1 cleanup: campaigns=6 headsRemoved=600
P1 outbox sequence 601 holds 600 rows (DefaultBatchRows=512)
P1 sync pass 1: appliedSeq=600 consumed=0 written=0 removed=0 rebuildRequired=True | db applied=600 canonical=601 pendingRows=600
...(passes 2-5 identical)...
P1 after one later ordinary mutation: appliedSeq=600 consumed=0 rebuildRequired=True pendingRows=601 canonical=602 applied=600
```

Five consecutive passes made no progress, and an ordinary one-row mutation made afterwards is stuck behind it.

**Existing tests do not catch it.** `CovenantSearchOutboxWorkerTests.A_bounded_batch_stops_on_a_sequence_boundary_and_resumes`
seeds one head per sequence, so no test has a sequence larger than the bound. All 26 tests in `CovenantSearchOutboxWorkerTests`,
`CovenantMaintenanceCoordinatorTests` and `CovenantOwnerCleanupTests` pass on the unmodified tree with the wedge present, and
still pass with the fix below.

**Suggested fix (verified in the scratch worktree).** A sequence is the unit of atomicity, so when the first pending sequence
alone exceeds the bound, apply it whole (it is bounded by the 65,536-row ceiling). In `SynchronizeAsync`, when
`ChooseContiguousTarget` returns `applied`, re-read with a limit of `CovenantLimits.MaxPendingSearchOutboxRows + 1` and set
`target = pending[0].SearchSequence`. With that 14-line change probe P1 gives:

```
P1 sync pass 1: appliedSeq=601 consumed=600 written=0 removed=600 rebuildRequired=False | db applied=601 canonical=601 pendingRows=0
P1 after one later ordinary mutation: appliedSeq=602 consumed=1 rebuildRequired=False pendingRows=0 canonical=602 applied=602
```

and the 26 existing tests still pass. Also: log a Warning from the driver when the outcome has `RebuildRequired` and
`ProjectionsWritten == 0`, and add a test with one sequence larger than the bound.

---

## 2. Minor - idle maintenance passes churn the availability generation, which is hashed into the DCI v2 cache scope marker

**Where.** `Covenant/CovenantMaintenanceCoordinators.cs:78-80` (cleanup) and `:151-153` (outbox) republish after every committed
batch even when it applied nothing (documented as deliberate, to correct a stale snapshot).
`Data/Covenant/CovenantAvailability.cs:291-295` and `Security/CovenantRuntimeGenerationProvider.cs:225` then publish
`Generation = current.Availability.Generation + 1` unconditionally. The consumer is
`Api/Intelligence/PromptCacheDispatchValidator.cs:213`, which hashes `captured.Availability.Generation` into the scope marker
that `Infrastructure/Intelligence/SystemPromptBuilder.cs:200` appends to the end of the preamble
(`[arcanum-dci-scope-v2:<digest>]`), and the plan key `"arcanum-pc-v2-" + Digest(prefix, tools, marker)`. Line 306 of the validator
also requires `authority.Capture() == prepared.Authority`, a whole-record equality that includes the counter.

**What is wrong.** With the Covenant feature enabled and healthy, each pass publishes twice, so the counter moves twice every
five minutes while no fact has changed. Every DCI v2 system prompt then gets a new marker immediately before its stable
sections, so provider prefix reuse is invalidated at least once per pass; a pass landing between `PrepareAsync` and a physical
dispatch makes the dispatch return `ReplanRequired`. The DCI v2 spec (`docs/superpowers/specs/2026-10-19-dci-v2-provider-cache.md`)
names state generations (dataset, canonical, deletion, Campaign availability) as scope inputs, not a publication counter.

`CovenantFeatureConfigurationPublisher.Republish` (lines 133-150) has the same shape: `IOptionsMonitor.OnChange` fires for any
`arcanum.json` change, and it republishes the unchanged flag, bumping the counter even with the Covenant off.

**Scope, honestly.** The pass-driven churn exists whenever `Features.Covenant` is on and healthy (the pass returns early
otherwise); it costs something only when `Features.DynamicContextInjectionV2` is on too (both default off). Protected turns
(Covenant content admitted) already use the ordinary layout, so it bites turns whose Campaign has no admitted Covenant content.
Cost: one avoidable prefix miss per five minutes of activity, plus replans.

**Measurement (probe P2, full test host, Covenant on, background pass removed so the test drives it).** Ten consecutive idle
passes: `Generation 8 -> 10`, `10 -> 12`, ... `26 -> 28`, every other published fact identical, `LastHealthTransition` unchanged.
Each pass cost 0.3-0.6 ms wall (p50 0.6 ms, max 2.9 ms on the first), so the pass itself is not a cost; the churn is.

**Suggested fix.** In `CovenantAvailabilityRepublisher.RepublishAsync` (or `CovenantAvailability.Publish`), skip the publication
when the new persisted facts equal the current snapshot (keeping the stale-snapshot correction: it still publishes when they
differ). Independently, hash the state generations rather than the raw counter into the marker.

---

## 3. Minor - owner cleanup drains 64 events per five-minute pass, in one queue for Sessions and Campaigns

**Where.** `Covenant/CovenantMaintenanceHostedService.cs:60,86,127-135` (one `RunBatchAsync(DefaultBatchSize)` per pass, then
`Task.Delay(Interval = 5 min)`); `Data/Covenant/CovenantCleanupWorker.cs:37` (`DefaultBatchSize = 64`);
`Data/Covenant/CovenantOwnerDeletionReader.cs:80-95` (`ORDER BY Sequence LIMIT $limit` across both owner kinds).

**What is wrong.** The pass never loops while the batch is full, so the catch-up rate is a fixed 64 events / 5 min = 768 per hour
(18,432 per day) regardless of backlog, and the single ordered queue means a Campaign deletion queued behind N Session deletions
waits N/64 passes. A retention sweep that deletes 20,000 Sessions (`retention.activeSessions.enabled`, the first run on a
long-lived installation) takes 313 passes, about 26 hours, to reach the Covenant tier. The cleanup worker's own remark says this
bound is "the difference between catching up and never finishing". The query also aggregates the whole pending backlog
(`GROUP BY OwnerKindCode, OwnerId ... ORDER BY MAX(Sequence) LIMIT`) on every pass, so each pass costs O(backlog).

**Impact, stated modestly.** In this release the Session half removes little (turn receipts have no producer yet), so the visible
effect is the lag in removing a deleted Campaign's canonical rows and projection (and the retention guarantee that goes with
it), not wasted CPU. Derived from the constants and a single call site; not executed.

**Suggested fix.** Re-run the sweep immediately (bounded by a time budget, e.g. 2 s) while `CampaignsCleaned + SessionsCleaned == maxEvents`,
and keep the five-minute delay only for an idle pass. The same loop applies to the outbox sweep (512 rows).

---

## 4. Minor - live entry erasure scans the whole installation three times inside the closure

**Where.** `Data/Covenant/CovenantEntryErasurePlan.cs:1080` (`EntryPredicate`), `:977-989` (`ReadVersionsAsync`), and the
version/receipt/provenance/review predicates built from `CovenantIdentitySql.Keyed` (`lower(replace(col,'-','')) = @id`);
called by `Covenant/CovenantEntryErasureService.cs:710` (`MeasureAsync`), `:723-725` (`RunAsync` Delete) and `:770-772`
(`ProveAbsentAsync`), all inside the `BEGIN IMMEDIATE` transaction that runs under the entry-erasure closure (a Global entry or
a reclaiming erase closes the whole installation, so every Covenant turn is refused or drained for the duration).

**What is wrong.** A normalised column cannot use a BINARY index, so each statement against `covenant_versions`,
`covenant_mutation_receipts`, `covenant_version_attachment_provenance`, `covenant_review_events` and the decision receipts is a
scan of the whole table, not the entry's rows. DESIGN 10.17 accepts that cost for the keyed purge, whose rows can be another
installation's, and says an exact comparison "keeps its index" wherever the spelling is settled and the rows are this
installation's own. A live erase reads only this installation's canonical family, where the spelling is the writer's
`ToString("D")`; the normalised shape here is inherited from the staged-restore mode of the same plan. The cost lands inside the
closure, which the 10.17 rationale ("a background path") does not cover.

**Measurement (probe P3, `CovenantEntryErasurePlan.RunAsync` in Count mode, one Global target entry, N other versions; receipts
table empty in this bed, so real installations are slower).**

| versions in installation | one Count pass, p50 (min-max) |
|---|---|
| 1,001 | 1.56 ms (1.41-17.2) |
| 5,001 | 12.16 ms (11.6-29.9) |
| 20,001 | 60.82 ms (50.4-208.8) |

Linear in installation size. `EXPLAIN QUERY PLAN`: `SCAN covenant_versions USING COVERING INDEX ux_covenant_versions_head_candidate`,
`SCAN covenant_mutation_receipts`, `SCAN covenant_version_attachment_provenance ...`, against
`SEARCH covenant_versions USING COVERING INDEX idx_covenant_versions_entry_created (EntryId=?)` for the exact form. Apply runs
three such plan passes under the closure (count, delete, absence proof), about 180 ms at 20,000 versions before receipts; the
quota ceilings (8,192 versions and 16,640 receipts per scope, unbounded scope count) allow far larger.

**Suggested fix.** In `CovenantEntryErasureMode.Live` use exact `EntryId = $entry` / `VersionId IN (...)` predicates (keeping the
normalised shape for `Staged`), or add expression indexes on `lower(replace(EntryId,'-',''))` for the canonical tables as was done
for `artifact_sensitivity` and `entry_embeddings`.

---

## Coverage

Read in full (all 20 files, 9,964 lines): `CampaignPathFullInstallationResetContracts.cs`,
`CampaignPathMarkerLifecycle.FullInstallationReset.cs` (2,048), `CampaignPathMarkerLifecycle.MarkerOwnershipProof.cs`,
`CampaignPathMarkerLifecycle.RestartRootProof.cs`, `CampaignPathMarkerLifecycle.RestoreInventory.cs`,
`CampaignPathMarkerLifecycle.cs`, `CampaignPathMarkerLifecycleContracts.cs`, `CampaignPathMarkerRootAuthority.cs`,
`CovenantCampaignScopeProbe.cs`, `CovenantContextProvider.cs`, `CovenantEntryErasureService.cs`, `CovenantExclusiveDisposition.cs`,
`CovenantExportPolicy.cs`, `CovenantFamilyReinitializeCoordinator.cs`, `CovenantFeatureConfigurationPublisher.cs`,
`CovenantIndexRebuildCoordinator.cs`, `CovenantMaintenanceCoordinators.cs`, `CovenantMaintenanceHostedService.cs`,
`CovenantMaintenanceService.cs`, `CovenantManagementService.cs`.

Callees and consumers read to judge cost (not in the unit): `CovenantSearchOutboxWorker.cs`, `CovenantCleanupWorker.cs`,
`CovenantOwnerDeletionReader.cs`, `CovenantPersistedAvailabilityPublisher.cs`, `CovenantAvailability.cs` (publish methods),
`CovenantRuntimeGenerationProvider.PublishAvailability`, `CovenantOperationGate` (`RequireLiveCampaignAsync`, `Revalidate`,
`CaptureFacts`), `CovenantEntryErasurePlan.cs` (lines 1-200, 380-700, 880-1266), `CovenantStore.ReadScopeCensusAsync`,
`CovenantStoreSql.TurnReceiptBacklog`, `CovenantSearchHealthRule`, `PromptCacheDispatchValidator.cs`, `SystemPromptBuilder.BuildCacheLayout`,
`PromptCacheAuthoritySnapshotProvider`, `ICovenantConnectionSource`/`CovenantConnectionSource`, `SqliteBusyRetry`, the schema files for every
table the unit counts or scans, and `HostToolsMarkerPairResetCoordinator` (revalidation path).

Docs read: DESIGN 10.10-10.13 in full, 10.17 and 10.18 in full, 10.24 (benchmark), the DCI v2 bullets in the prompt-cache
section, 16.7, and the DCI v2 spec's identity section.

Reachability noted while judging: `CovenantMaintenanceService`, `CovenantIndexRebuildCoordinator.AdvanceAsync` and
`CovenantFamilyReinitializeCoordinator` are registered in no container or constructed nowhere (DESIGN 10.17 "What is deliberately
absent"), so their costs are latent and not reported. `CovenantCampaignScopeProbe` is reached only from the gate's exclusive
Campaign acquisition (`RequireLiveCampaignAsync`).

## Checked and sound (with how)

- **Per-turn planning cost.** `CovenantContextProvider.BeginTurnAsync` is one gate acquisition, one bounded read and a pure link.
  The recorded benchmark baseline (`tests/RetroDownfall.Arcanum.Covenant.Benchmarks/covenant-baseline-osx-arm64.json`) gives
  `turn.plan` p50 130.6 us / 123 KB allocated, `turn.admission` 1.5 us, `status.census` 26 us. Not worth changing.
- **Maintenance pass cost when idle.** Probe P2: 0.3-0.6 ms per whole three-sweep pass. The context is pooled
  (`AddDbContextPool`, pool size 32) and `ArcanumDbContextOptionsConfigurator.ConfigureProvider` leaves `Pooling` at its default,
  so a pass does not pay a SQLCipher key derivation (the measured pass time is consistent with that). For contrast a fresh
  unpooled open measured 67 ms p50 (probe P4); that applies to the `Pooling = false` factories only.
- **Index use of the unit's own SQL.** `idx_owner_deletion_events_kind_sequence` serves the `MAX(Sequence) WHERE OwnerKindCode = 1`
  in the republisher; `idx_artifact_sensitivity_session` / `_campaign` and `idx_covenant_entries_campaign` serve
  `CovenantExportPolicy.CountAsync`; `idx_managed_file_write_intents_phase` and `idx_local_erasure_work_items_state` serve the
  inventory counts.
- **Scans that are bounded by a cap.** `CovenantProtectedInventoryService`'s `external_disclosure_receipts WHERE RevocabilityCode = 2`
  cannot seek `(DestinationCode, RevocabilityCode)` and the receipts table is append-only by design (DESIGN 10.20: joined disclosure
  evidence has no removal), so it is a covering-index scan of every receipt; `session_sensitivity_state WHERE TaintedArtifactCount > 0`
  and `TurnReceiptBacklog` (`GROUP BY SessionId`; `MaxTurnReceiptsInstallationWide` is 65,536 and no production code writes the
  table yet, DESIGN 10.11) are scans too. All are
  operator-initiated plan or sweep reads costing single-digit milliseconds at realistic sizes; not reported.
- **Full-installation-reset reconcile.** `TerminalizeCleanupChildrenAsync` revalidates the authenticated receipt up to twice per child
  and each revalidation re-reads the journal and recomputes O(N) digests, so the phase is O(N^2) in Campaigns. N is capped at 4,096
  and a realistic installation has tens of Campaigns, in a once-per-installation destructive operation; not reported.
- **`DisposeUnretainedSeedAuthoritiesAsync`** uses `retained.Any(...)` per seed, O(N^2) over at most 4,096 reference compares
  (16.7 million, milliseconds); not reported.
- **Restore inventory fds.** `InventoryRestoreCleanupAsync` opens every registered root at once (two descriptors each, up to 8,192)
  and the restore runs in the CLI process, so retention is bounded by that process's life; not reported.
- **Hosted-service cadence when Covenant is off (the default).** A timer wake each minute, no I/O. Fine.
- **No `Task.Run`, sync-over-async, `Thread.Sleep` or unbounded collection** in any of the 20 files (searched by pattern, then read).
  The only polling is the maintenance delay.

## Probes run

All in `U23PerfProbeTests.cs` (source saved beside this report as `U23-probe-source.cs.txt`), run with `--no-build </dev/null`
in the scratch worktree.

| Probe | Purpose | Output file |
|---|---|---|
| P1 | outbox wedge: 6 Campaigns x 100 heads, delete, one cleanup batch, then the outbox worker at 512 rows | `U23-probe-output-baseline.txt` (wedge), `U23-probe-output-with-fix.txt` (fix applied) |
| P2 | full host, Covenant on, ten idle passes: generation delta and wall time | `U23-probe-output-baseline.txt` |
| P3 | erasure plan Count pass at 1k/5k/20k versions plus `EXPLAIN QUERY PLAN` | `U23-probe-output-baseline.txt` |
| P4 | cost of a fresh unpooled SQLCipher open (context only) | `U23-probe-output-baseline.txt` |

Existing tests run: 26 tests (`CovenantSearchOutboxWorkerTests`, `CovenantMaintenanceCoordinatorTests`, `CovenantOwnerCleanupTests`)
on the unmodified tree (all pass) and again with the probe fix to `CovenantSearchOutboxWorker.cs` (all pass).
