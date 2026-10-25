# U25 (Arcanum.Infrastructure, Data (1)) — reliability lens

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e` (read-only). Probes ran in a scratch worktree, since removed.

## Unit file list (reconstructed)

`scratchpad/review/units.json` was missing at the start of this run: the scratchpad had been wiped and only empty `reports/`, `results/` and `wt/` folders had been recreated. The published binary was gone too. I rebuilt the U25 file list by re-running the generator from the parent session transcript (`git ls-files src | *.cs`, grouped by area `Data`, split at 12,000 lines). The first chunk comes to exactly 10,549 lines, which matches U25's recorded size:

ArcanumDbContext.cs (184), ArcanumDbContextFactory.cs (87), ArcanumDbContextOptionsConfigurator.cs (84), AttachmentMemoryProvenanceStore.cs (175), BatchAccountingRecoveryStore.cs (364), BatchRepository.cs (1148), BlobEncryptionMetadataStore.cs (193), BudgetAlertRepository.cs (114), BudgetReservationService.cs (806), CovenantErasureCoordinator.cs (4291), CovenantLabeledArtifactGuard.cs (327), CovenantLaunchGapRecoveryHandlers.cs (123), CovenantSensitiveRetentionPurgeCoordinator.cs (649), DataRetentionFactoryResetRecoveryHandler.cs (24), DataRetentionHostedSweepOutcome.cs (32), DataRetentionLeaseMaintainer.cs (187), DataRetentionMutationRecoveryHandler.cs (24), DataRetentionPolicyStore.cs (411), DataRetentionRecoveryHandler.cs (16), DataRetentionService.CampaignSummaries.cs (186), DataRetentionService.CovenantInventory.cs (376), DataRetentionService.FactoryActivation.cs (748). All are under `src/RetroDownfall.Arcanum.Infrastructure/Data/`.

Other agents should know that the other units' file lists and the published binary are missing in the same way.

## Findings

### F1 — Minor — The Campaign-summary retention planner selects protected summaries that apply can never delete

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CampaignSummaries.cs:44-46` (planner) against `:157-164` (apply).
- Claim: the planner selects every aged `campaign_rollup_artifacts` / `campaign_contribution_artifacts` row with only `julianday(CreatedAtUtc) <= julianday(@cutoff)`. The apply step deletes only rows with `SensitivityCode = 0 AND ...`. A protected summary (`SensitivityCode = 1`) is therefore counted as a planned deletion, and apply then returns `CandidateDeleteResult.Empty` for it. `DataRetentionService.Pruning.cs:3523-3553` turns that into a "preserved" candidate with a `Data.PlanChanged` conflict ("changed or became protected after planning") and pins `earliestSkippedIndex`, so the checkpoint cursor stays put for the rest of the sweep. Nothing changed after planning: the row was protected from the start, and every later sweep plans it again.
- Failure scenario: `features.covenant` and `features.campaignRollups` are on, `retention.campaignSummaries.enabled` is true, and one contribution was built from protected input and is older than the cutoff. From then on:
  - every `data prune` preview reports one CampaignSummaries row to delete;
  - every apply and every automatic sweep deletes 0 rows and reports `Data.PlanChanged` for it;
  - the prune checkpoint cursor cannot advance past it, so a crash later in the sweep replays every later candidate.

  The Compendium README (`retention.campaignSummaries.enabled`: "Enables retention for Campaign contribution and aggregate summary artifacts ...") does not say that protected summaries are kept.
- Evidence: probe `DataRetentionServiceTests.U25Probe_protected_campaign_summary_is_planned_but_never_deleted` (source in `reports/C/U25-reliability-probes/`). It seeds one aged contribution with `SensitivityCode = 1`, then plans, applies and plans again. It passed, which confirms the defect. Output:
  ```
  PROBE plan candidates: campaign-contribution:06164EB3-...; rows=1
  PROBE apply rowsDeleted=0; conflicts=Data.PlanChanged:The retention candidate changed or became protected after planning; it was preserved.
  ```
  The row is still present after apply, and the second plan selects it again. The existing `DataRetentionServiceTests.CampaignSummaries` tests seed only `SensitivityCode 0`, so every existing test passes despite this.
- Fix: add `SensitivityCode = 0` to the planner predicate so planning and apply agree. Protected summaries could instead be reported as retained-protected status, not as candidates. Then state in Compendium.README and DESIGN that protected summaries leave only through the sensitivity purge or reset. Add the probe as a regression test. Effort S.

### F2 — Minor — The attachment consultation window uses millisecond `julianday()` against 100 ns watermarks

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/AttachmentMemoryProvenanceStore.cs:106-107`.
- Claim: `ListConsultationsAsync` filters `julianday(e."CreatedAt") > julianday(@afterExclusive) AND julianday(e."CreatedAt") <= julianday(@throughInclusive)`. SQLite keeps a parsed date as integer milliseconds (`iJD`), so instants less than 1 ms apart compare equal. Its only caller, `Loremaster.cs:372-386`, passes the Logger watermark (`LastSummarizedMessageAt`) and the batch's last `CreatedAt`. Both come from the batch that `EntryTemporalQueries` selects, which compares `CreatedAt` as fixed-width 7-digit text (100 ns). The two windows therefore disagree whenever an entry lands within the same millisecond as a batch boundary. Its consultations are then dropped from its own batch and attributed to the previous one.
- Failure scenario: the previous Logger batch ends at an entry stamped T. The next turn's entries (user and assistant share one `now`) are stamped T + 300 µs. This can happen with rapid automated turns, for example A2A or scripted `ask` loops. The next batch contains that assistant entry, but `ListConsultationsAsync(T, end)` returns none of its attachment consultations, so the summary loses its "consulted references". The previous batch's window `(.., T]` did include them, even though their entry was not in that batch.
- Evidence:
  - `sqlite3 :memory: "select julianday('2026-10-24T12:00:00.0004000Z') > julianday('2026-10-24T12:00:00.0001000Z'), '2026-10-24T12:00:00.0004000Z' > '2026-10-24T12:00:00.0001000Z'"` prints `0|1`.
  - Probe `AttachmentMemoryProvenanceStoreU25ProbeTests.U25Probe_consultation_window_loses_sub_millisecond_entries` failed as predicted with `Assert.Single() Failure: The collection was empty`. Output:
    ```
    PROBE consultations in (watermark, +1s]: 0
    PROBE consultations in (-1s, watermark]: 1
    ```
  - The existing `AttachmentMemoryProvenanceStoreTests` uses a ±1 s window, so it cannot see this.
- Fix: compare the canonical text directly (`e."CreatedAt" > @afterExclusive AND e."CreatedAt" <= @throughInclusive`). This is the same rule `EntryTemporalQueries` uses, and `UtcInstantText` is fixed-width and normalized. A better fix is to key the window on Entry `Sequence`. Add the probe as a regression test. Effort S.

### F3 — Minor (docs-mismatch) — DESIGN says the coordinator checkpoints `ReopenedVerified`; the code never does, and the matching resume branch is dead

- File: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs:1886-1892` and `:2551-2568`; related `docs/Arcanum.DESIGN.md` §10.20.4.
- Claim:
  - DESIGN §10.20.4 says "The coordinator checkpoints `ReopenedVerified` and hands that exact candidate ... to `CovenantAuthorityTransitionPublisher`".
  - It also says "`ReopenedVerified` uses [`ResumeOrAcquireExclusiveAsync`] only to close the narrow post-disposition/checkpoint-finalization crash gap".
  - In the code, `ReopenedVerified` is only an in-memory assignment (`state = state with { Phase = ReopenedVerified }`, line 2567). There is no journal or row write for it.
  - Both checkpoint projections (`FromMutationCheckpoint` and `FromFactoryResetCheckpoint`, through `Owned` at line 409) always return `CovenantResetPhaseMachine.First`. So the `or CovenantResetPhase.ReopenedVerified` arm at line 1889-1890, and its comment, can never be taken.
- Failure scenario: an operator or maintainer reading DESIGN to decide what a post-proof crash leaves behind expects a durable `ReopenedVerified`. In fact the journal's last completed phase is `SidecarsVerified`, and recovery repeats `VerifyReopenAsync` before publishing. The behaviour is safe; the documented recovery model and the dead arm are wrong.
- Evidence: `rg ReopenedVerified src` finds only these sites in the coordinator. The phase is never passed to `BeginPhaseAsync` or `CompletePhaseAsync`, and nothing records it otherwise. (Trace; no execution needed.)
- Fix: correct the two DESIGN §10.20.4 sentences. Remove the dead `ReopenedVerified` arm, or say in its comment that it is never reached. Effort S.

## Checked and found sound

- Cancellation and timeout ownership in `CovenantErasureCoordinator`. Caller tokens reach storage only before the proof. Publication and writer restart use the 30 s `PublicationAndWriterBound`, and disposition uses `DispositionBound`. `KeepClosed` cleanups run on `CancellationToken.None`. Every catch in `RunClosedAsync` chooses `KeepClosed` once an effect was attempted or the resumed phase is past `First` (the `CovenantErasureProgress` seed). The pre-effect rollback is gated again by the journal graph (`PrepareReopenAsync`). Checked by tracing every catch arm against the progress flags.
- `CovenantGrimoireClosure.ReleaseAsync` is one-shot (`Interlocked.Exchange`), and the run-level `finally` in `RunUnderLeaseAsync` always releases an unspent closure with `KeepClosed`. Checked by tracing the slot transfers in `CloseGrimoireAsync` (lines 1147-1265).
- `WithLedgerAsync` and `WithRequiredLedgerAsync` always close the ledger, clear the exact pool, and report physical close. Lease-lost and cancellation are rethrown ahead of cleanup errors.
- `AdvanceAsync` handles an in-flight phase from a crash between begin and complete: it skips the begin publication and re-runs the effect. `CompactAsync` and `StageReplacementAsync` are keyed on journal evidence, not on the directory.
- `DataRetentionLeaseMaintainer` does not cause concurrent use of one connection. Renewal goes through `LongRunningOperationStore.RenewLeaseAsync`, which opens its own isolated heartbeat connection (`LongRunningOperationStore.cs:1030-1100`). Action cancellation, observation and heartbeat cleanup are correct. The 89 existing tests pass, including `DataRetentionLeaseMaintainerTests`.
- `BudgetReservationService`:
  - every ceiling decision is made under `BEGIN IMMEDIATE`;
  - the UTC-midnight cross-day checks in `AdjustAsync` and `RecheckDailyLimitAsync` are arithmetically consistent: the reservation's own period includes it, and today's sum adds it once;
  - `UncertainPaid` counts in every day's outstanding sum, as DESIGN documents;
  - recheck runs before `MarkPossiblyDispatchedAsync` in the evaluation flow (`TurnAccountingHandle.cs:215-260`, `MemoryEvaluationProvider.cs:180-210`);
  - text range comparisons rely on fixed-width `UtcInstantText`, and historical forms are normalized by the UTC-instant column inventory transitions.
- `BatchAccountingRecoveryStore`: claim, reconcile and abandon commit together. The Resumable path cannot miss new runs, because `TryBeginLineAsync` and `TryRecordTerminalLineAsync` refuse while a claim exists. `TryCompleteRecoveryAsync` is a compare-and-swap plus the claim delete in one transaction.
- `BatchRepository`:
  - the claim and seal in `TryRecordTerminalLineAsync` run in one transaction;
  - `CompleteLineAsync` has an idempotent-replay check;
  - keyset paging uses (`CreatedAt`, `Id`) with fixed-width text and N-form ids;
  - the status CAS respects recovery claims;
  - `UpdateStatusAsync` has no production caller.
- `SqliteBusyRetry` retries only SQLITE_BUSY/LOCKED (codes 5/6), never cancellation. Every transactional lambda in the unit disposes and rolls back before a retry. The non-transactional multi-statement lambdas (`UpdateStatusAsync`, `CompleteLineAsync`) are idempotent on replay.
- `CovenantLabeledArtifactGuard`: the parameter format (`CovenantIdentitySql.Key`, lowercase N) matches the normalized column predicate. It fails closed on `SqliteException`, and the batch size of 256 stays within parameter limits.
- `CovenantSensitiveRetentionPurgeCoordinator`:
  - a lease is taken only when a target is labelled;
  - failure handling preserves already-purged dispositions;
  - an executor nobody dispatches is blocked as an integrity failure;
  - the `memoryDelegation.BeginOptionalAsync` extension is null-safe.
- `BlobEncryptionMetadataStore`: the attachment path containment check runs, the stored-spelling write-back works, and the read columns are NOT NULL in the schema.
- `DataRetentionPolicyStore` deliberately ignores out-of-process `arcanum.json` edits until the next start. That matches "restart the host after saving changes" (Compendium.README:454).
- `DataRetentionService.FactoryActivation`:
  - the planning lease and the service connection are always released in `finally`;
  - the lease maintainer stops before the closed period, as DESIGN §10.20.4 requires;
  - operation-identity replay is mapped with a fixed-time digest compare.
- `ArcanumDbContextOptionsConfigurator`: the pooled options capture the passphrase once. No in-host rekey path exists (the `SetPassphrase` callers are bootstrap, recovery unlock, reset and backup, and backup re-derives the same value).
- Native AOT: no reflection JSON in the unit; the design-time factory is guarded with `RequiresAssemblyFiles`.

## Probes run

1. `sqlite3 :memory:` comparing julianday and text precision. Result `0|1|1`: julianday treats instants 300 µs apart as equal.
2. Scratch worktree `wt/U25-reliability` at `ac0d2d8e`. I added the two probe tests and built `RetroDownfall.Arcanum.Tests`, then ran `--filter FullyQualifiedName~U25Probe`. The F1 probe passed, confirming the defect. The F2 probe failed as predicted, confirming the defect.
3. In the same worktree I ran the existing classes `BatchAccountingRecoveryStoreTests`, `BatchRepositoryTests`, `BlobEncryptionMetadataStoreTests`, `BudgetAlertRepositoryTests`, `BudgetReservationServiceTests`, `CovenantLabeledArtifactGuardTests`, `DataRetentionLeaseMaintainerTests` and `AttachmentMemoryProvenanceStoreTests`. 89 passed, 0 failed.
4. The worktree was removed afterwards. The probe sources are kept in `reports/C/U25-reliability-probes/`.
