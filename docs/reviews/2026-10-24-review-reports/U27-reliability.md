# U27 — Arcanum.Infrastructure "Data (3)" — reliability lens

Tree: `/Users/mat/Source/apps/RetroDownfall.Arcanum/.claude/worktrees/codebase-review` at `ac0d2d8e`.
Probe worktree: `/private/tmp/claude-501/-Users-mat-Source-apps-RetroDownfall-Arcanum/d111c03e-eda5-4c82-a12d-f02f9cd1cdb8/scratchpad/review/wt/U27-reliability` (removed after the probes).

## Unit reconstruction

`units.json` was not present in the review scratch directory when this reviewer ran (the directory was
recreated empty at 18:28 and never received the file). The unit was reconstructed from the stated area
and size: the top-level C# files of `src/RetroDownfall.Arcanum.Infrastructure/Data/` (sub-directories
excluded), in `git ls-files` order, chunked greedily at a 12,000-line cap, give chunk 3 = exactly 11,953
lines, the size the task states, and the only contiguous run of Data files with that total. The same cap
reproduces the sibling "Covenant (1)" unit (9,964 lines). The 10 files:

| File | Lines | Read |
|---|---:|---|
| `Data/DataRetentionService.cs` | 8,099 | fully |
| `Data/DataRetentionSweepHostedService.cs` | 182 | fully |
| `Data/EfSaveChangesRetry.cs` | 31 | fully |
| `Data/ExactUsdText.cs` | 128 | fully |
| `Data/FtsMatchQuerySanitizer.cs` | 95 | fully |
| `Data/GrimoireConnectionAcquisitionRouteAttribute.cs` | 6 | fully |
| `Data/GrimoireConnectionAdmissionContracts.cs` | 589 | fully |
| `Data/GrimoireConnectionAdmissionGate.cs` | 2,604 | fully |
| `Data/GrimoireEntitySql.cs` | 187 | fully |
| `Data/GrimoireInstallationMaintenancePaths.cs` | 32 | fully |

Context read (not part of the unit, read only as far as needed to judge the unit's callers/callees):
`MemoryDelegationCoordinator` (Close/Reopen/RevokeAll), `LongRunningOperationStore` (single-flight insert,
recovery selection, `StartupRecoveredAttention`), `CovenantErasureCoordinator` (closing sequence, abort,
OCE handling), `CovenantConnectionDrain.DrainAsync`, `GrimoireOrdinaryConnectionLifecycle.BeginOpen`,
`IdentityOwnedFileSystemCleanup.TryRestoreQuarantined`, `DataRetentionService.FactoryReset.cs`
(`RestoreQuarantinedFactoryFiles`), `DataRetentionService.Pruning.cs` (candidate failure handling),
`DataRetentionService.CovenantInventory.cs` (planning admission), `DataRetentionEndpoints.cs`,
`DataRetentionCommands.ResetMemory` (CLI), schema files for Campaigns/Sessions/Entries/Prompts/
Apprentices/SessionAttachments/annal_review_markers/campaign_rollup_sources.

Docs read: DESIGN §9 (Native AOT and query discipline), §10.20 durable execution / reconciliation paragraph
(line 754 ff.), the admission-gate census paragraph (line 2799) and the promotion paragraph (line 2847);
API §8.31 (Grimoire request admission), API data-retention route table and §8.20 enum paragraph;
Command Reference memory-reset paragraph (line 1230).

## Findings (most severe first)

### F1 — DELETE of an in-flight attachment answers 404 `Data.NotFound` instead of its blocker (Minor, wrong-behaviour)

- **Where:** `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:1396-1413` (`ApplyOrdinaryAsync`), with the plan built at `:2518-2533` (`BuildDeleteAttachmentPlanAsync`).
- **Claim:** `BuildDeleteAttachmentPlanAsync` returns an empty-items plan carrying the `Data.AttachmentInFlight` blocker for any attachment whose `State` is not `Bound`. `ApplyOrdinaryAsync` tests "DeleteSession/DeleteAttachment with zero items → `Data.NotFound`" *before* it tests blockers, so the blocker is never reported on apply.
- **Failure scenario:** a turn is in flight (or a crash left an attachment `Pending` until attachment GC reclaims it); `DELETE /api/data/attachments/{id}` (or `arcanum data delete-attachment`) returns **404** "The requested data source was not found." The row and the bytes are still there. A client that treats 404 on DELETE as "already gone" (the usual idempotent-delete reading) concludes the attachment was removed.
- **Evidence (probe, executed):** `ReviewProbe_in_flight_attachment_delete_reports_not_found` — seeded a Bound attachment, set `State='Pending'`, planned and applied:
  `REVIEWPROBE inflight: planBlockers=[Data.AttachmentInFlight] planItems=0 apply=Data.NotFound|The requested data source was not found. rowsLeft=1 fileExists=True`.
  No existing test applies a delete to a non-Bound attachment (grep for `InFlight`/`'Pending'` in the retention suites finds none), so this regression passes every existing test.
- **Fix:** move the blocker (and conflict) checks ahead of the not-found check, or make the not-found check `Items.Length == 0 && Blockers.Length == 0`. Add a test for the pending-attachment delete. **Effort S.**

### F2 — A promoted initiating request that survives a proven abort drops out of the next closure's census (Minor, race)

- **Where:** `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs:419-428` (promotion removes the lease from `_requestLeases`), `:738-821` (`AbortClosingAsync` never puts it back), `:1088-1099` (`ReleaseRequest` skips removal for a promoted lease).
- **Claim:** promotion removes the initiating request from the stage-one census so the request can run its own erasure. If that closure is then aborted (the coordinator's `RollbackAndReopen` after `Grimoire.WorkDrainTimeout`, `CovenantErasureCoordinator.cs:3748-3779`), the request is still live — it goes on to write its operation row (`FailCovenantResetAsync`) and its response — but it is no longer in `_requestLeases`. A second owner's stage one therefore completes without waiting for it, and stage two's drain (`CovenantConnectionDrain.DrainAsync` closes every enrolled handle) can close that request's connection under it. DESIGN line 2799 states "A surviving old lease remains in the next closure's census"; this one does not.
- **Failure scenario:** a Covenant reset times out in stage one and aborts; while the initiating request is still unwinding, a background maintenance owner (Covenant maintenance, schema transition) begins closing. Its `DrainRequestAndWorkAsync` succeeds immediately and `CloseConnectionAdmissionAsync` issues a closed lease while the request still believes it holds an admitted lifetime; the request's terminal ledger write can fail on a connection the drain closed, leaving the row for later reconciliation. The window is the request's tail, so this is rare.
- **Evidence (probe, executed):** `ReviewProbe_promoted_request_surviving_abort_is_missing_from_next_census` — promote R under owner 65, time out stage one on a blocking work lease, prove-abort, then `Begin(owner 66)`: `DrainRequestAndWorkAsync` → Success and `CloseConnectionAdmissionAsync` → Success with R undisposed (test passed). Control `ReviewProbe_control_unpromoted_survivor_is_waited_for` passed: the same survivor unpromoted keeps stage one waiting. The existing `Promoted_request_cannot_replay_after_abort_or_remove_a_new_request_from_the_next_census` disposes the promoted lease *before* checking that the drain is blocked, so it passes with the gap present.
- **Fix:** in `AbortClosingAsync`, if `_closure.InitiatingRequest` is still unreleased, clear `IsPromoted`, clear `Lifetime.PromotedConnection`, and re-add it to `_requestLeases` (its terminal TCS must then be re-armed, or keep a separate "promoted survivor" set that stage one also awaits). Add the probe above as a regression test. **Effort S–M.**

### F3 — A retention mutation that meets a held write lock blocks a thread ~31 s ignoring cancellation, then reports "operator review" and terminalizes the operation (Minor, blocking-io / error-message)

- **Where:** `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:4808-4820` (`BeginMutationTransactionAsync` → synchronous `SqliteConnection.BeginTransaction(deferred: false)`, no `SqliteBusyRetry`), surfaced through the generic catch at `:1740-1766`. Same helper is used by `DeleteSessionAsync` (`:3079`), `DeleteAttachmentAsync` (`:3449`), `ApplyMemoryResetAsync` (`:3632`) and the WorkspaceReset/CampaignSummaries/Pruning partials.
- **Claim:** `BEGIN IMMEDIATE` runs inside Microsoft.Data.Sqlite's own busy loop bounded by `DefaultTimeout` (30 s; `SqliteBusyRetry.cs:23-35` documents exactly this) on the calling thread; the caller's token is not observed. When it gives up, the `SqliteException(5)` is not a busy-retryable outcome anywhere in this service: it falls into the catch-all, which stamps the operation `Failed` with `Data.ReconciliationFailed` and returns HTTP 500 "The retention operation failed; its durable history requires operator review." Nothing was mutated; the honest answer is "busy, retry".
- **Failure scenario:** any long write holder (another component's long transaction, an external `sqlite3`/backup, a leaked transaction) while an operator deletes a session or resets a memory store. The request hangs ~31 s even after the client cancels, then tells the operator to review durable history for a no-op; a pool thread is blocked for the duration. Also the failure bookkeeping then queues behind the same lock (the probe's call took 65 s in total).
- **Evidence (probe, executed):** `ReviewProbe_delete_session_busy_begin_blocks_and_reports_operator_review` — took `BEGIN IMMEDIATE` on a second connection from the session-gate hook, cancelled the caller's token at 2 s, released the lock at 40 s:
  `errorLoggedAfterLock=00:00:31.39 exception=Microsoft.Data.Sqlite.SqliteException:SQLite Error 5: 'database is locked'. elapsed=00:01:04.84 result=Data.ReconciliationFailed|The retention operation failed; its durable history requires operator review. ops=[5:Data.ReconciliationFailed] sessionsLeft=1`.
- **Fix:** open the mutation transaction through `SqliteBusyRetry` (or `SqliteBusyRetry.Begin` cursor) with a lowered per-attempt `DefaultTimeout`/`busy_timeout` the way `CampaignRepository.AddAsync` does, honour the token between attempts, and map a final busy failure to `Data.Conflict` (retryable, operation `Failed` with that code) rather than `ReconciliationFailed`. **Effort M.**

### F4 — `GET /api/data/status` fails with FileNotFoundException when a managed file disappears mid-measurement (Minor, race)

- **Where:** `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:7954-7971` (`MeasureOwnedTree`) and `:7973-7989` (`CountFiles`), called from `GetStatusAsync` at `:197`, `:246`, `:5818-5824`.
- **Claim:** the tree is enumerated into `FileInfo` objects and only afterwards summed with `file.Length`; a file removed in between throws `FileNotFoundException`. `GetStatusAsync` has no catch and the endpoint (`DataRetentionEndpoints.cs:47-60`) none either. The attachments tree routinely holds transient files: promotion stages `destination + ".tmp"`, `EncryptedBlobStore` stages `".<name>.tmp.<guid>"`, and attachment GC writes and deletes `.sweep-clock.tmp.<guid>` in the root (`SessionAttachmentStore.cs:2149-2154, 2216, 2333`).
- **Failure scenario:** `arcanum data status` (or the Forge) while an attachment is being stored or GC runs → HTTP 500 instead of a status report.
- **Evidence (probe, executed):** `ReviewProbe_status_throws_when_a_managed_file_vanishes_during_measurement` — 2,000 files plus a writer/deleter loop on `.staged-N.tmp`: `REVIEWPROBE status: System.IO.FileNotFoundException after 5 calls in 00:00:00.079: Could not find file '.../attachments/<dir>/.staged-6.tmp'`.
- **Fix:** read the length from the enumeration itself (`new DirectoryInfo(root).EnumerateFiles("*", options)` gives `FileInfo` with cached length, or `FileSystemEnumerable` projecting `entry.Length`) and/or catch `FileNotFoundException`/`DirectoryNotFoundException` per entry and skip it. **Effort S.**

### F5 — A failed in-process restore after a rolled-back delete terminalizes the operation and strands the attachment bytes in quarantine (Minor, crash-consistency)

- **Where:** `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:3260-3275` and `:3538-3553` (rollback path calls `RestoreQuarantinedFactoryFiles`, `DataRetentionService.FactoryReset.cs:1590-1601`, which throws `IOException` on a refused restore), then the catch-all at `:1740-1766` writes `Failed`.
- **Claim:** by the time the delete runs, `PrepareMutationJournalAsync` has saved the `ARCAMUT2` v2 journal naming every file, and `RecoverMutationAsync` (`:7189-7328`) knows how to restore exactly those quarantines when the target still exists. But when the transaction rolls back after quarantine (commit failure, cancellation at commit) and the in-process restore is refused (original path occupied, transient IO error, quarantine directory not removable — `TryRestoreQuarantined` returns false in each case), the restore's `IOException` replaces the original exception and the catch-all marks the row `Failed`. Recovery never selects `Failed` rows (`LongRunningOperationStore` `StartupRecoveredAttention` and the adopt predicate), so the bytes stay under `.arcanum-retention-<op>-<hex>/` while the rolled-back attachment row still points at the original path. A cancellation that triggered the rollback also loses its lease-surrender path.
- **Failure scenario:** the session and its attachment rows survive (rollback) but the attachment file is gone from its path: every later read of that attachment fails, and nothing durable will move it back.
- **Evidence (probe, executed):** `ReviewProbe_failed_restore_after_rollback_strands_quarantined_attachment` — a deferred foreign key made `COMMIT` fail after the quarantine; the no-follow metadata test seam reported the original path occupied once the file had left it:
  `REVIEWPROBE restore: apply=Data.ReconciliationFailed|... requires operator review. op=5:Data.ReconciliationFailed:v2 originalExists=False quarantined=[<id>.bin] sessionRows=1 attachmentRows=1`.
- **Fix:** in `ApplyStartedOrdinaryAsync`, when a v2 journal was saved and the failure is not a pre-quarantine refusal, settle `ReconciliationRequired` with `RetentionRecoveryTerminalCode` (the pattern `SettleUnexpectedHostedPruneFailureAsync` already uses), so `RecoverMutationAsync` restores or finalizes from the journal; and in the rollback path preserve the original exception (aggregate the restore failure instead of replacing it). **Effort S–M.**

### F6 — A Campaign-targeted reset of a store with no owner is refused as 409 `Data.Blocked`, not the documented 400 `Data.InvalidRequest`, after the CLI has already asked for confirmation (Minor, docs-mismatch / cli-ergonomics)

- **Where:** `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:2684-2695` (plan blocker carries `ErrorCodes.Data.InvalidRequest`) and `:1406-1413` (every blocker is re-coded `Data.Blocked`); the same re-coding applies to the `BuildPlanAsync` default arm (`:718-724`). CLI: `src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs:216-241` prompts without looking at `preview.Value.Blockers`.
- **Claim:** `docs/Arcanum.Command.Reference.md:1230` says `--campaign` with any scope other than saga/lexicon/campaign-summary "is refused with `Data.InvalidRequest`". The preview succeeds (a plan whose blocker reason is `Data.InvalidRequest`), the CLI asks "Reset the entry memory scope for campaign …?", and the apply is refused as `Data.Blocked` → HTTP 409, with the CLI exit code for a conflict rather than invalid input.
- **Evidence (probe, executed):** `ReviewProbe_untargetable_campaign_reset_is_refused_as_blocked` — `REVIEWPROBE targeted: planBlockers=[Data.InvalidRequest] apply=Data.Blocked|Only Saga and Lexicon memories record an owning Campaign, …`.
- **Fix:** when a blocker's `ReasonCode` is `Data.InvalidRequest`, return that code (400) from apply; better, reject the targeted request before planning (endpoint/`PlanAdmissionAsync`) and have the CLI refuse locally, matching the Command Reference. **Effort S.**

## Checked and found sound

- **Admission gate locking and signalling** (`GrimoireConnectionAdmissionGate.cs`, read in full): every state transition is under `_sync`; revocation `Cancel()` and effect-group revocation run outside the lock with `AggregateException` contained; `_maintenanceAdoptionInterlock` is released on every failure path (`finally` in both acquire methods) and exactly once on release (owner compared under lock); `ReleaseMaintenanceIoLaneAsync`'s handle wait is bounded and the interlock is returned either way; generation increments are `checked`; `AbortClosingAsync` deliberately ignores the caller token (documented); stage-two cancellation leaves the owner retained (documented, coordinator converts OCE). Verified by reading and by running the 198 existing tests for the gate, the sweep service, `ExactUsdText` and the FTS sanitizer (all pass).
- **`BeginOrResumeExclusive` cannot be left half-transitioned by `CloseForMaintenance`**: `MemoryDelegationCoordinator.CloseForMaintenance/RevokeAll` only `TrySetResult`s drains, uses `CancelAsync` (callbacks off-thread) and catches `ObjectDisposedException` for preparations; registration CTSs are never disposed — so the call made between `_state = Closing` and `_closure = …` does not throw in practice.
- **Sweep hosted service**: the deferral wait (`WaitForOpenGenerationAfterRefusalAsync(predecessor)`) cannot hot-loop — `TryBeginExternalEffectGroup` only refuses under maintenance; the continuation claim is released in `finally`; the scope is disposed before the work lease; stop cancels the KeepClosed wait (existing tests).
- **`ExactUsdText`**: exact BigInteger addition with explicit overflow refusal; legacy INTEGER/REAL values fall back through `GetDecimal` (invariant string parse); parameters bound as invariant TEXT.
- **`FtsMatchQuerySanitizer`**: all three callers (`GrimoireRepository:991`, `SessionRepository:106`, `LexiconService:1036`) skip an empty result, so no `MATCH ''`.
- **`GrimoireEntitySql`**: column lists and nullability match `Campaigns/Sessions/Entries/Prompts/Apprentices.sql` (e.g. `ModelUsed`, `Sessions.TotalCostUsd TEXT NOT NULL DEFAULT 0` read through `ExactUsdText`).
- **Commands inside open transactions**: `TableExistsAsync`/`CountTableAsync` create commands without setting `Transaction`, but Microsoft.Data.Sqlite's `CreateCommand` copies the connection's current transaction, so they are valid inside `DeleteEntryIndexesAsync`/`DeleteAttachmentRowsAsync` (and the existing delete tests exercise them).
- **Identity spellings in reset selections**: Lexicon markers/entries use lowercase "D" (writer `LexiconService.MemoryReview.cs:553`), Saga markers and campaign rollups uppercase (`SagaMemoryReviewService.cs:2374`, `CampaignRollupStore.cs:326`), matching `BuildMemoryResetSelections`.
- **Recovery codes**: `TrySurrenderLeaseForReconciliationAsync`, `TrySurrenderHostedPruneAsync` (`Data.ReconciliationFailed`) and `TryParkCancelledCovenantResetAsync` (`Covenant.MaintenanceFailed`) stamp exactly the pairs `LongRunningOperationStore.StartupRecoveredAttention` re-selects; a v0 mutation row closes as `RetentionMutationNeverStarted`.
- **Prune sweep cannot be wedged by one labelled Session**: `DataRetentionService.Pruning.cs:4318` swallows the label refusal per candidate.
- **Post-commit work** (reconciliation counts, `Completed` transition) runs on `CancellationToken.None` on the ordinary path, as DESIGN §10.20 states; hosted prune completion on the caller token is consistent with the documented "prune keeps its own cancellation points" (restart-idempotent).
- **Entry-scope memory reset skips the label guard** (`RefuseLabeledMemoryResetAsync`), but no production writer creates `SensitiveArtifactKind.Embedding` labels (grep: only the purge kernel, policy and `EmbeddingsResetService` name it), so nothing is orphaned today.

## Probes run

| Probe | Result |
|---|---|
| Existing: `GrimoireConnectionAdmissionGateTests`, `DataRetentionSweepHostedServiceTests`, `ExactUsdTextTests`, `FtsMatchQuerySanitizerTests` (review tree, `--no-build`) | 198 passed |
| `ReviewProbe_promoted_request_surviving_abort_is_missing_from_next_census` | passed (gap present) |
| `ReviewProbe_control_unpromoted_survivor_is_waited_for` | passed (control) |
| `ReviewProbe_in_flight_attachment_delete_reports_not_found` | passed (NotFound returned) |
| `ReviewProbe_untargetable_campaign_reset_is_refused_as_blocked` | passed (Blocked returned) |
| `ReviewProbe_status_throws_when_a_managed_file_vanishes_during_measurement` | passed (FileNotFoundException after 5 calls) |
| `ReviewProbe_delete_session_busy_begin_blocks_and_reports_operator_review` | passed (31.4 s block, ReconciliationFailed, Failed) |
| `ReviewProbe_failed_restore_after_rollback_strands_quarantined_attachment` | passed (Failed v2 row, file in quarantine) |

Probe sources are preserved beside this report as
`U27-reliability.DataRetentionServiceTests.ReviewProbe.cs.txt` and
`U27-reliability.GrimoireConnectionAdmissionGateTests.ReviewProbe.cs.txt`; the scratch worktree was removed.

