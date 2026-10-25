# U26 (Arcanum.Infrastructure, Data (2)) — reliability review

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e` (read-only). Probes ran in a scratch worktree that has since been removed.

## Unit reconstruction

`scratchpad/review/units.json` was not on disk when this run started (the `review` folder held only `reports/A`, `results/A` and `wt`). I rebuilt the unit from the workflow arguments in the session transcript (`U25 Data (1) = 10549`, `U26 Data (2) = 9123`, `U27 Data (3) = 11953`) and the path-sorted line counts of `src/RetroDownfall.Arcanum.Infrastructure/Data/*.cs`:

- Data (1) ends at `DataRetentionService.FactoryActivation.cs` (cumulative 10,549 lines);
- **Data (2) = `DataRetentionService.FactoryReset.cs` (1,679) + `DataRetentionService.Pruning.cs` (6,696) + `DataRetentionService.WorkspaceReset.cs` (748) = 9,123**;
- Data (3) starts at `DataRetentionService.cs` (8,099 + … = 11,953).

The match is exact, so this is the unit.

## Coverage

Read in full:
- `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs` (1–1680)
- `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs` (1–6697)
- `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.WorkspaceReset.cs` (1–749)

Read in part, to trace callers and contracts:
- `DataRetentionService.cs`: the constructor and fields, `PlanAsync`/`PlanAdmissionAsync`, `ApplyAsync`/`ApplyOrdinaryAsync`/`ApplyStartedOrdinaryAsync`, the hosted prune start/resume/settle paths (680–1770), `AddSessionPruneCandidatesAsync`, `FinalizePlan`/`ComputePlanId`, `CountTableAsync`/`TableExistsAsync`/`OpenConnectionAsync`/`ExecuteAsync`/`DeleteAnnalVersionsAsync`/`ProbeOwnedFile`, and the quarantine helpers (7067–7190).
- `DataRetentionService.FactoryActivation.cs` `ContinueFactoryResetAsync`; `DataRetentionService.CovenantInventory.cs` `BindCovenantErasurePlanIdentity`; `DataRetentionLeaseMaintainer.cs`; recovery handlers.
- `LongRunningOperationStore.cs`: `TryStartSingleFlightAsync`, `SaveCheckpointAsync`, `RequestCancellationAsync`, `ResetForRetryAsync`, `OpenConnectionAsync`. `LongRunningOperationReconciler.cs` `RecoverOneAsync`/`SettleLeasedAsync`.
- `IdentityOwnedFileSystemCleanup.TryQuarantine`, `DailyJsonlAuditWriter`, `ManagedLogMutationGate`, the `InMemoryDaemonExecutionRepository` gate.
- The schema for the campaign summary, session, tapestry and workspace tables and their triggers.
- Docs: `Arcanum.DESIGN.md` data-lifecycle section (about lines 640–770: rules, sweeps, plan identity, durable execution, factory reset), §465–469 (Campaign continuity); `Arcanum.Command.Reference.md` operations section (1113–1137); `RetentionSettings` defaults.

## Findings

### F1 — Critical — A prune with more than 10,000 candidates writes a checkpoint its own recovery rejects, which blocks every retention operation for good
`Data/DataRetentionService.Pruning.cs:4102` (reader cap); writer `:3672–3716`; uncapped plan `:299`.

The planner selects the complete plan (`const int limit = int.MaxValue;`, line 299). DESIGN says retention "applies the complete selected plan … It is not a public total-item restriction". `SavePruneCheckpointAsync` serializes **every** candidate into every checkpoint, refusing only above 5 MiB. `ParsePruneCheckpoint` refuses any payload with more than 10,000 candidate lines (`if (lines.Length - candidateStart > 10_000) throw new InvalidDataException("…exceeds the maximum sweep size.")`).

Any interruption of a prune with 10,001 to about 36,000–45,000 candidates therefore leaves a row nothing can resume. Interruptions include Ctrl-C (surrendered to `ReconciliationRequired`), a host restart, lease adoption, and a hosted `DeferredForMaintenance` resume. The reconciler maps the `InvalidDataException` to `RequiresAttention(operation.checkpoint_corrupt)`. `TryStartSingleFlightAsync` then refuses every `data-retention-*` start while that row is `ReconciliationRequired`, Pending or Cancelling.

The documented remedy does not help. `ResetForRetryAsync` keeps `CheckpointPayload`, so the next pass re-reads the same bytes. `operation cancel` moves the row to Cancelling, which the reconciler adopts and hands to the same handler (trace, not probed).

The blocked operations are prune, delete-session, delete-attachment, reset-memory and factory reset. The default rules make large plans normal: `WorkspaceIndexes` (30 days, one candidate per chunk), `SessionEntryEmbeddings` (30 days, one per embedded entry), `IdempotencyClaims` (7 days) and `LongRunningOperations` (90 days). A first prune on an established installation easily exceeds 10,000 candidates.

**Probe** `U26Probe_PruneOver10000Candidates_WritesACheckpointItsOwnRecoveryRejects`:
1. Seed 10,001 aged legacy `IdempotencyKeys`, plan, and apply. The apply succeeds after 1 min 45 s, with 200 checkpoints of about 760 KB each (an O(N²) write volume).
2. Replay the service's own persisted checkpoint as an interrupted row through the real `LongRunningOperationReconciler` with `DataRetentionRecoveryHandler`.

Output:
```
PROBE checkpoint bytes=760200 applyElapsed=00:01:45.5087447
PROBE reconcile completed=0 attention=1 state=ReconciliationRequired code=operation.checkpoint_corrupt
PROBE next prune success=False code=Data.Conflict message=Another data-retention operation is already active: data-retention-prune bf49a655-… is ReconciliationRequired.
PROBE retry reset=True retried.attention=1 retried.completed=0 state=ReconciliationRequired code=operation.checkpoint_corrupt payloadKept=True
PROBE delete-session success=False code=Data.Conflict message=Another data-retention operation is already active: data-retention-prune bf49a655-… is ReconciliationRequired.
```

**Missing test:** no test plans or recovers more than 10,000 candidates, so any regression in the writer/reader asymmetry passes CI.

**Fix:** stop embedding the candidate list in every checkpoint. Either page the sweep (bounded batches per durable slice, re-planned per page), or persist the frozen candidate set once and checkpoint only the cursor. In either case make the writer's bound equal to the reader's, and enforce it at plan time. As defence in depth, have recovery of an unreadable prune checkpoint re-plan from scratch (prune is `RestartIdempotently`; pending-journal handling aside) rather than strand the row. Effort M.

### F2 — Important — Past 5 MiB of checkpoint the prune can never start: nothing is deleted, with a non-actionable error
`Data/DataRetentionService.Pruning.cs:3692` (throw), reached from the initial save at `:3359`.

This has the same root cause as F1. Each checkpoint line is about 76–150 bytes, so at about 33,000–45,000 candidates the first `SavePruneCheckpointAsync` (taken before any candidate) throws `InvalidOperationException("The retention checkpoint exceeds its bounded payload size.")`. The request path marks the row `Failed` and answers `Data.ReconciliationFailed` "The retention operation failed; its durable history requires operator review." The hosted sweep fails the same way every interval.

Because the plan only grows while nothing is pruned, the condition is self-perpetuating. The operator's only escape is to shrink the selection by hand (raise rule days, prune, lower), which nothing tells them.

**Probe** `U26Probe_PruneWhoseCheckpointExceeds5MiB_CannotStartAndDeletesNothing` (36,000 aged keys, 64-character hashes):
```
PROBE first success=False code=Data.ReconciliationFailed message=The retention operation failed; its durable history requires operator review.
PROBE second success=False code=Data.ReconciliationFailed
PROBE remaining keys=36000
```

**Fix:** the same as F1. Until then, cap or page the plan before apply and return a typed, actionable error. Effort M (with F1).

### F3 — Important — Factory reset leaves the Campaign Summary text (model-written summaries of the erased Sessions) in the Grimoire
`Data/DataRetentionService.FactoryReset.cs:31` (`FactoryPlanTables`) and `:113` (`FactoryDeletionTables`).

Neither list names `campaign_rollup_artifacts`, `campaign_rollup_state` or `campaign_maintenance_checkpoints`.

- `campaign_rollup_artifacts.Content` (up to 8 KiB) is owned by `Campaigns ON DELETE CASCADE`, and factory reset keeps Campaigns, so the rows survive.
- The cascade from `Sessions` removes contributions and `campaign_rollup_sources`; a trigger nulls the state pointer.
- The aggregate text derived from every erased Session stays in the database. `ReconcileFactoryResetAsync` reports success because it checks only its own table list.

DESIGN (factory reset paragraph) says "a summary of an erased transcript is erased transcript content, not a rebuildable cache the reset may leave behind". The `CampaignSummary` memory reset does delete these tables, so the factory reset (a superset) is inconsistent with it. The Covenant protected kernel removes only labelled rollups.

**Probe** `U26Probe_FactoryResetLeavesCampaignRollupContent`: seed a Campaign with a contribution, a rollup and a source edge, then run the real ordinary factory reset (V0 recovery arm, the same `BuildFactoryResetPlanCoreAsync` + `ApplyFactoryResetAsync` the V2 continuation runs).
```
PROBE factory plan items=[ActiveSessions:2/0]
PROBE factory completed=1 attention=0 sessions=0 contributions=0 rollupsWithContent=1 rollupPointers=0
```

**Missing test:** no factory-reset test seeds campaign summaries.

**Fix:**
- Add `campaign_rollup_artifacts` (Physical, CampaignSummaries) and `campaign_maintenance_checkpoints` to both lists.
- Clear or reset `campaign_rollup_state` first, because its `CurrentArtifactId` FK has no ON DELETE.
- Run the deletes under the `SessionRetention`/owner-cleanup authorization their delete guards require.
- Include them in reconciliation.
- Add a factory test with summaries.

Effort M.

### F4 — Minor — The prune log glob matches non-dated names that apply can never delete; any checkpoint holding one is unrecoverable
`Data/DataRetentionService.Pruning.cs:6380`.

`AddLogCandidates` uses `Directory.EnumerateFiles(_logsRoot, "audit-????????.jsonl", SearchOption.TopDirectoryOnly)`. That overload uses `MatchType.Win32` on every OS, where `?` is DOS_QM ("zero or one character before a dot"). Factory reset uses `new EnumerationOptions()`, which is `MatchType.Simple`.

The resulting candidate (for example `audit-log:audit-old.jsonl`) is counted in the preview's files and bytes, but at apply `IsDatedLogFile` refuses it. It is preserved and reported as `Data.PlanChanged` on every prune for ever. It also pins `earliestSkippedIndex`. Worse, `IsValidCheckpointCandidate` rejects it on parse, so an interrupted prune whose plan contains it strands exactly as in F1.

**Measured** (a .NET 10 file-based probe on macOS): `SearchOption` matches `audit-.jsonl`, `audit-1.jsonl`, `audit-2026.jsonl`, `audit-old.jsonl`, `audit-snapshot.jsonl` and `audit-20261001.jsonl`. `EnumerationOptions` matches only `audit-20261001.jsonl` and `audit-snapshot.jsonl`.

**Probe** `U26Probe_NonDatedAuditFileMatchesThePrunePatternAndPoisonsTheCheckpoint`:
```
PROBE candidates=[audit-log:audit-old.jsonl] files=1
PROBE apply success=True files=0 conflicts=[Data.PlanChanged:audit-log:audit-old.jsonl] strayExists=True
PROBE reconcile attention=1 state=ReconciliationRequired code=operation.checkpoint_corrupt
```

**Fix:** enumerate with `MatchType.Simple` and filter through `IsDatedLogFile` (or `IsValidCheckpointCandidate`) at plan time, as the daemon candidates already are at line 2987. Effort S.

### F5 — Minor — Factory reset does not sweep the prune's own `.arcanum-retention-*` quarantine directories under the logs root, and still reconciles as clean
`Data/DataRetentionService.FactoryReset.cs:1271` (snapshot) and `:1368` (plan count).

Factory log inventory walks only top-level dated files and `.arcanum-cleanup-*` directories. A prune log candidate quarantines into `OperationQuarantineDirectoryPrefix(operationId)` = `.arcanum-retention-{op:N}-`. The attachments and files roots are fine, because the managed-tree walk captures every entry. Such a directory persists whenever its pending journal is never processed. Two ways this happens:
- the request path terminalizes a candidate failure as `Failed` (`ApplyStartedOrdinaryAsync` generic catch), for example when `DeleteLogCandidate`'s `TryRestoreQuarantined` fails;
- the row is stranded as in F1 or F4.

The audit or guardrail log inside it is then invisible to the audit readers and survives a factory reset.

**Probe** `U26Probe_FactoryResetLeavesAPruneQuarantinedLogUnderTheLogsRoot`:
```
PROBE factory plan files=1
PROBE factory completed=1 attention=0 datedExists=False strandedExists=True
```

**Fix:** have factory log capture/count also walk `.arcanum-retention-*` directories, or share one quarantine-prefix list with the prune. Consider leaving request-path candidate failures that carry a pending journal as `ReconciliationRequired` (retention code) rather than `Failed`, as the hosted path does. Effort S.

### F6 — Minor — In production the frozen prune authority is never found: apply recomputes cutoffs, and one Session status change in that window fails the whole prune
`Data/DataRetentionService.Pruning.cs:3349`, with `DataRetentionService.CovenantInventory.cs:279`.

`BuildUnifiedPrunePlanCoreAsync` registers `FrozenPruneAuthority` keyed by the plan instance, and its comment warns that a later `with` loses it. With a Covenant gate, which production always has, `PlanAdmissionAsync` returns `BindCovenantErasurePlanIdentity(plan, …)`, that is `plan with { Covenant = inventory }`. The instance `ApplyOrdinaryAsync`/`StartHostedPruneAsync` hand to `ApplyUnifiedPruneCoreAsync` is therefore never in the table.

Apply then falls back to `BuildCandidateCutoffsAsync(…, SnapshotRetention(CurrentRetention))`. That re-reads every `session:` candidate's status and throws `InvalidOperationException` ("no enabled age boundary") if one was deleted, or archived with `ArchivedSessions` disabled, after planning. The request path then answers "requires operator review"; the hosted sweep fails that pass.

The tests cannot see this: every `DataRetentionServiceTests.CreateService` omits `covenantGate`, so the admission fails and the unwrapped plan is used.

**Probe** `U26Probe_GatedPrunePlanIsNotTheInstanceItsFrozenAuthorityWasRegisteredFor` (added to a scratch copy of `CovenantRetentionTests`; reflection over `_frozenPruneAuthorities`):
```
PROBE frozen authority gatedHasCovenant=True gatedRegistered=False ungatedRegistered=True
```

**Fix:** register the authority against the plan identity (`PlanId`) rather than the instance, or re-register after `BindCovenantErasurePlanIdentity`. Make the fallback skip, not throw, for a candidate that lost its boundary (it is preserved by the per-candidate re-check anyway). Effort S.

## Checked and found sound

- **Factory reset lock order** is managed-log gate, then SQLite IMMEDIATE writer, then daemon gate. `InMemoryDaemonExecutionRepository` holds its gate only around in-memory record creation (`StartAsync`/`TryStartAsync`), and `DailyJsonlAuditWriter` never enters SQLite under the log gate. No cycle exists.
- **Factory reset failure handling.**
  - Pre-commit failure restores quarantined files in reverse, then rolls back, and aggregates restore and rollback failures.
  - A commit with a cancelled token never commits (the `DbTransaction.CommitAsync` default) and takes the restore path.
  - Post-commit daemon deletes, quarantine finalization, directory cleanup and reconciliation all run on `CancellationToken.None`; a post-commit fault becomes `RetentionQuarantineRecoveryRequiredException`, then `RequiresAttention(Data.ReconciliationFailed)`.
  - The daemon gate lease is scoped to the try block, so no daemon start races reconciliation.
- **Factory delete order and counts.** The delete order matches FK cascades (Entries before Sessions, BillableOperations before InferenceRuns, `annal_versions` leaf-first), so `changes()` counts equal the plan. Derived counts are measured inside the writer transaction before any trigger fires. The launch row is checked by owner inside the deleting transaction.
- **Quarantine edge case.** `IdentityOwnedFileSystemCleanup.TryQuarantine` returns true with a default quarantine when the source vanished; all three quarantine call sites in the unit (`QuarantineFactoryFiles`, `DeleteLogCandidate`, the `TryQuarantineOwnedFile` path) treat `quarantine == default` as failure.
- **Prune candidate deletes re-prove protection inside the deleting transaction** (pins, holds, conflicts, age, labels, the Saga/Lexicon pin via `PinnedAtUtc IS NULL`, the accounting chain) and roll back when the guarded statement affects 0 rows. Disposing the transaction covers the early-return arms.
- **Checkpoint encoding.** Base64 fields avoid `:` and newline collisions; the parser validates cursor bounds, duplicates and cutoff completeness.
- **Lease renewal** is wall-clock driven per iteration and on every checkpoint boundary.
- **Single connection.** `LongRunningOperationStore` and `DataRetentionService` share the scoped DbContext connection. In the unit's candidate code no true async yield happens inside a transaction, so the maintainer cannot interleave a renewal with an open transaction.
- **Workspace reset.** Apply recomputes the plan identity inside the IMMEDIATE transaction and classifies legacy vector mirrors. Apply runs only offline (the API exposes workspace planning only), so there is no indexer race. Post-commit reconciliation runs on `CancellationToken.None`.
- **CLI prune apply** is on the streaming client (`ArcanumApiClient.DataLifecycle.cs`), so the five-minute response-headers deadline cannot interrupt a long prune.

## Probe artifacts

- Probe sources: `/private/tmp/claude-501/-Users-mat-Source-apps-RetroDownfall-Arcanum/d111c03e-eda5-4c82-a12d-f02f9cd1cdb8/scratchpad/u26probe/U26ReliabilityProbeTests.cs.txt` and `/private/tmp/claude-501/-Users-mat-Source-apps-RetroDownfall-Arcanum/d111c03e-eda5-4c82-a12d-f02f9cd1cdb8/scratchpad/u26probe/CovenantRetentionTests.probe.diff`
- Output: `/private/tmp/claude-501/-Users-mat-Source-apps-RetroDownfall-Arcanum/d111c03e-eda5-4c82-a12d-f02f9cd1cdb8/scratchpad/u26probe/probe.log`
- Glob probe: `/private/tmp/claude-501/-Users-mat-Source-apps-RetroDownfall-Arcanum/d111c03e-eda5-4c82-a12d-f02f9cd1cdb8/scratchpad/u26probe/glob.cs`
- Run: `dotnet test … --filter FullyQualifiedName~U26Probe` gave 6 passed (each probe asserts the defective behaviour), 1 min 55 s.
