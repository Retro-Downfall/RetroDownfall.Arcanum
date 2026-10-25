# U28 (Arcanum.Infrastructure, Data (4)) — reliability review

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e`. Reviewer lens: reliability.

`units.json` was not present in the scratchpad when this review started (the scratchpad `review/` directory had been cleared), so the unit was reconstructed from the group C run's own arguments (`~/.claude/projects/.../workflows/wf_98297366-e88.json`: U28 = "Data (4)", 10,500 lines) and the chunking of the top-level `Data/*.cs` files: Data (1)..(5) total 47,572 lines, which is exactly the top-level `Data/*.cs` set; Data (2) = 9,123 and Data (3) = 11,953 reproduce, and the 31 files from `GrimoireMaintenanceConnectionFactory.cs` through `SessionAttachmentStore.Lifecycle.cs` (ordinal sort) sum to exactly 10,500.

## Coverage

Read in full (31 files, 10,500 lines), all under `src/RetroDownfall.Arcanum.Infrastructure/Data/`:
GrimoireMaintenanceConnectionFactory.cs, GrimoireOrdinaryConnectionFactory.cs, GrimoireOrdinaryConnectionLifecycle.cs, GrimoireRequestAdmissionScope.cs, GrimoireSqlCommandFactory.cs, GrimoireWalCheckpoint.cs, IAuthenticatedCovenantErasureRecoveryHandler.cs, ICovenantLabeledArtifactTransactionGuard.cs, ISqliteNativeRuntime.cs, IdempotencyClaimStore.cs, IdempotencyStore.cs, LongRunningOperationStore.RequestAdmission.cs, LongRunningOperationStore.cs, MemoryErasureEvidence.cs, MemoryErasureFingerprintRelease.cs, MemoryErasureGuard.cs, MemoryErasureKeyWarmup.cs, MemoryErasureLabels.cs, MemoryScopeResolver.cs, SagaErasureWriteGate.cs, SagaExtractionCursorResolver.cs, SagaMemoryScopeClassifier.cs, SagaMemoryStore.Curation.cs, SagaMemoryStore.Ranking.cs, SagaMemoryStore.Recall.cs, SagaMemoryStore.cs, SagaRetirementSuppression.cs, SagaSuppressionKeyStore.cs, SagaVectorMirror.cs, SanctumBreachRepository.cs, SessionAttachmentStore.Lifecycle.cs.

Read in part, for context only (other units): SessionAttachmentStore.cs (fields, gates, InsertRowAsync, UpdateSourceAsync, ResolveUnderRoot, orphan sweep), UtcInstantText.cs, UtcInstantSql.cs, SqliteBusyRetry.cs, Schema/GrimoireCoreSchemaVersion.cs, Schema/SagaExtractionCursorBackfill.cs, Schema/SagaMemoryCampaignScopeBackfill.cs (join), Schema/Tables/{saga_memories, saga_extraction_watermarks, SessionAttachments, IdempotencyClaims, SanctumBreaches}.sql, Annals/AnnalsClaimWriter.cs (assert idempotency), Security/WorkspacePathPolicy.cs (containment walk), Operations/LongRunningOperationReconciler.cs, Operations/LongRunningOperationStartupHostedService.cs, Operations/LongRunningOperationRecoveryAdmission.cs, Storage/BlobEncryptionLifecycleService.cs (recovery), Repositories/SessionRepository.cs (ForkAsync), Hosting/UnseenServantService.cs, Hosting/SessionAttachmentPendingGcHostedService.cs, Hosting/SagaExtractionService.cs (cursor use), DataRetentionService.Pruning.cs / FactoryReset.cs (idempotency blockers), Api/Security/SanctumEndpoints.cs.

Docs read: DESIGN §10.8.1–10.8.2 (lines ~1780–1830), §11.17 idempotency (~3900–3936), §10.6/§16 schema notes (595, 505, 697, 1801, 2174), §21.15 pointer; Command Reference `operation` family (1108–1121); API `/api/operations/{id}/cancel` (256).

## Findings

### F1 — Important — An unobserved cancellation is resumed, not cancelled (LongRunningOperationStore.cs:922)

`AcquireLeaseAsync` sets `"State" = CASE WHEN "Kind" = @longRest AND "State" = @cancelling THEN @cancelling ELSE @running END`. For every kind except `long-rest-sweep`, taking the lease of an expired `Cancelling` row rewrites it to `Running`. `FindExpiredAsync` (line 638) deliberately discovers expired/unleased `Cancelling` rows so that "a cancellation nobody observed is settled by the reconciler" (Command Reference line 1119), and the reconciler (`LongRunningOperationReconciler.cs` ~229–302) hands the handler `lease.Operation` — the row as it reads *after* acquisition, i.e. `Running`. `SettleResultAsync` (594–608) only re-maps a `Cancelling` Long Rest row. The handler therefore cannot tell that the operator asked to stop, and every `RestartIdempotently`/`ResumeFromCheckpoint` kind redoes the work: `blob-encryption-migration` / `blob-encryption-key-rotation` re-run the whole migration and retire keys (`BlobEncryptionLifecycleService.RecoverAsync`, 170–228), `data-retention-prune` restarts from its cursor and deletes more data, legacy `data-retention-factory-reset` V0 restarts. `RequestCancellationAsync` (1225) also admits `ReconciliationRequired` rows, so "cancel" on an attention row turns into a fresh recovery run of the same handler.

Scenario: a blob-encryption migration (or a retention prune) is running; the host is killed (or the cancel lands just before the owner dies). After restart the operator runs `arcanum operation cancel <id>` on the orphaned row → `Cancelling`. Within a minute the background recovery pass (`LongRunningOperationStartupHostedService.BackgroundInterval`) acquires it as `Running`, runs the migration to completion and records `Completed`.

DESIGN §10.8 line 1788 states the `Cancelling → Running` rewrite, but presents discovery of `Cancelling` as *settling* the cancellation; for resume-capable kinds the code does the opposite of what the operator asked. The existing test `AnUnobservedCancellationIsRecoverableOnceItsLeaseLapses` (LongRunningOperationStoreTests.cs ~1249) asserts only `recovery.Acquired`, so it passes whichever state the lease leaves.

Probe P1 (passes): blob-encryption-migration row leased by `dead-host`, `RequestCancellationAsync`, lease lapses, `LongRunningOperationReconciler.ReconcileAsync` with a recording handler → handler called once with `operation.State == Running`; summary `Completed == 1`; persisted state `Completed`.

Fix: keep `Cancelling` across lease acquisition for every kind (generalise the Long Rest `CASE`), and in `SettleResultAsync` settle a row that was `Cancelling` at discovery or at re-read as `Abandoned` (`operation.cancelled`) unless the handler reports the work already terminal; or give handlers an explicit "cancelled" entry point. Assert the post-acquisition state in the existing test. Update DESIGN §10.8 and the Command Reference. Effort M.

### F2 — Minor (large effect, rare trigger) — Missing-file sweep deletes a live attachment's row whenever it cannot prove containment or existence (SessionAttachmentStore.Lifecycle.cs:695–722)

`SweepMissingFileRowsAsync` deletes the row when `ResolveUnderRoot` throws (`InvalidOperationException`, logged as "escaping RelativePath") or when `File.Exists` is false. `ResolveUnderRoot` (SessionAttachmentStore.cs:2381) throws whenever `WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck` returns false, and that walk returns false not only for a real escape but for any `IOException`/`UnauthorizedAccessException` from `File.GetAttributes` (WorkspacePathPolicy.cs:713–725); `File.Exists` likewise returns false on any stat error. A row deleted this way is gone for good, and the next reconcile (it runs on every host start, `SessionAttachmentPendingGcHostedService`) finds the still-present ciphertext unreferenced and unlinks it — the orphan sweep is careful never to unlink on a filesystem error (`IsModifiedAtOrAfter`, "an unexpected filesystem error can never be the reason a live attachment's ciphertext is unlinked"), but the row sweep that feeds it has no such rule.

Probe P3b (passes): persist an attachment; temporarily move its logical-key directory outside the root and link it back (a walk the containment check cannot prove); `ReconcileAsync` completes without error and deletes the row; restore the directory exactly; `ListBoundAsync` is empty while the blob exists; a second `ReconcileAsync` deletes the blob.
Probe P3a (fails as a reproduction, recorded for honesty): a `chmod 000` session directory does *not* reach the row delete, because `DeleteStalePendingAsync` runs the orphan sweep first and its `Directory.EnumerateFiles` throws `UnauthorizedAccessException` (SessionAttachmentStore.cs:2126), aborting the whole reconcile. The destructive branch is therefore reached by a transient stat/lstat error (EIO, network home) or a below-root link, not by a plain permission flip.

Fix: make the sweep tri-state — delete only on a definite ENOENT/ENOTDIR for the leaf after a containment walk that completed, and skip-and-warn when containment or existence cannot be established (have `WorkspacePathPolicy` report "could not classify" separately from "escapes"). Effort S–M.

### F3 — Minor — Claims stranded in Running by a crash are never swept (IdempotencyClaimStore.cs:371–395)

`DeleteExpiredAsync` deletes only `Completed`/`Failed`/`Abandoned` rows. A request killed before `CompleteAsync`/`MarkAbandonedAsync` (SIGKILL, power loss, OOM) leaves its claim `Running` with an expired lease forever: nothing marks it abandoned, the Unseen Servant never deletes it, and the retention planner reports it on every prune as a `Data.IdempotencyLeaseActive` blocker "requires reconciliation before pruning" (DataRetentionService.Pruning.cs:2370–2412) which nothing performs. It only clears if a client happens to replay the same Idempotency-Key. DESIGN §10.8.1 (line 1801) says idempotency claims recover "through the Unseen Servant's expiry sweep", and §11.17 (line 3926) says claim rows older than the TTL are swept — the code contradicts both.

Probe P2 (passes): `TryAcquireAsync` with `CreatedAt` 30 days ago and lease 30 days ago + 5 min; `DeleteExpiredAsync(UtcNow)` returns 0; `TryGetAsync` still returns the row in `Running`.

Fix: in `DeleteExpiredAsync` also remove (or first mark `Abandoned`) `Running`/`Claimed` rows whose `LeaseExpiresAt < @olderThan`. Effort S.

### F4 — Minor — Fork copy-time blob authority is never used in production; tests exercise only the path production cannot take (SessionAttachmentStore.Lifecycle.cs:288–292, 334–359; SessionAttachmentStore.cs:66–67)

`CopyBytesForForkAsync` stores the identity it captured for each copy in a `ConditionalWeakTable<SessionAttachmentForkCopyPlan, ForkBlobAuthority>`, which keys by object reference. `SessionRepository.ForkAsync` (Repositories/SessionRepository.cs:551–566 and 617–635) builds a new list of plan records for the insert pass, so `TryGetValue` never hits: every forked blob is decrypted and SHA-256-verified again and its identity recaptured inside the ambient fork transaction, which already holds the Grimoire write lock (the session and entry rows were inserted before the attachment pass), and a copy that was replaced between the passes is re-captured and published instead of refused. The existing tests (SessionAttachmentStoreTests.cs:2171/2178, 2253/2271) pass the same plan instance to both calls, so they pin the retained path only.

Probes P4/P5 (both pass): copy with plan A, replace the copy by a new inode holding the same ciphertext; `InsertForkRowsInAmbientTransactionAsync` with an equal-but-distinct plan B (production's shape) succeeds and publishes the row; with the same instance A it throws `IOException` "missing or replaced bytes".

Fix: key the retained authority by `NewAttachmentId` in a per-fork dictionary (or have `ForkAsync` reuse the copy-pass plans), and add a test that uses distinct plan instances. Effort S.

## Checked and found sound

- `TryStartSingleFlightAsync` (LongRunningOperationStore.cs:333–358): the `INSERT … SELECT … WHERE NOT EXISTS` takes the write transaction at statement start, so two connections cannot both pass the predicate; a stale WAL snapshot surfaces as SQLITE_BUSY, which `SqliteBusyRetry` re-evaluates.
- Lease CAS (`AcquireLeaseAsync`, `RenewLeaseAsync` on an isolated unpooled connection, `TryTransitionAsync`, `ResetForRetryAsync`, `RequestCancellationAsync` owner-bound exclusion): predicates checked against DESIGN §10.8; one-row `== 1` checks; RETURNING read of the claimed row.
- `FindExpiredAsync` keyset cursor: `COALESCE(LeaseExpiresAt, CreatedAt)` and `Id` compared as fixed-width `UtcInstantText` and `"N"` text on both sides (UtcInstantText.cs:16); the reconciler's `attempted` set prevents re-processing rows whose lease release moves them behind the cursor.
- `ResolveOrCreateAsync`: `BeginTransaction(IsolationLevel.Serializable)` on Microsoft.Data.Sqlite issues `BEGIN IMMEDIATE`; replay rolls back before reading; fixed-time digest compare.
- `MemoryErasureEvidence.IsInstalledAsync` stamp cache: schema cookie + data_version + total_changes; positive answers cached only outside a transaction; traced rollback, DDL-by-other-connection and own-DML cases.
- `MemoryErasureGuard` two-phase protocol and `RunWithRetryAsync`: credential read refused inside a transaction (autocommit check), key copies disposed on every path, one retry then `Unavailable`.
- Saga writes (`InsertCoreAsync`, Retire/Reinstate/Correct/SetPin, Delete/DeleteAll): fresh transaction per busy retry, immediate write transaction before the labelled-artifact guard, suppression digest pair used symmetrically by insert and reinstate, `AnnalsClaimWriter.AppendAssertAsync` is idempotent (returns false when a claim exists), mirror classified from `sqlite_master` inside the transaction.
- `SagaSuppressionKeyStore.ReadOrCreateAsync` INSERT OR IGNORE then read handles the two-retirement race.
- `IdempotencyClaimStore` reclaim is an atomic CAS (`LeaseExpiresAt < now` or terminal), the unique-violation race re-reads, and owner/state predicates guard heartbeat/complete/terminal.
- `GrimoireOrdinaryConnectionFactory` / `GrimoireOrdinaryConnectionLifecycle`: every refusal path terminalizes the admission ticket (`MarkFailed` / `MarkRefusedAfterOpen` / `ResolveTicketAfterFailedRefusal`), state guarded by one lock, stale registrations neutralised by `LifetimeId`.
- `GrimoireMaintenanceConnectionFactory`: each failure path reports NotOpened/PhysicallyClosed; `ImmutableFileUri` percent-encodes `?`, `#`, `%` and the drive colon.
- `GrimoireRequestAdmissionScope`: the check-then-act in `TryAdmit` cannot race `Dispose` because its only caller is the request middleware (ApiBootstrapper.cs:1060–1075) and disposal is the request scope's.
- `SanctumBreachRepository`: transaction per busy retry; campaign id normalised for the FK; `before` cursor ties would need two breaches with identical `DateTimeOffset.UtcNow` ticks (each breach stamps its own), so not reported.
- `SessionAttachmentStore.Lifecycle` fork vs. reconcile: `ForkAsync` holds both session gates across copy and commit, so the directory sweep (which takes the fork session gate and re-checks `SessionExistsAsync`) cannot remove a fork in flight; `DeleteSweptRowAsync` deletes only on an unchanged `State`/`RelativePath`.
- `RevalidateAttachmentSourcesAsync` catches `OperationCanceledException` along with everything else (unlike `RevalidateBoundSourcesAsync`), but the following `UpdateSourceAsync` observes the cancelled token before it writes, so no CorruptMetadata is persisted on shutdown; inconsistency only, not reported.
- `GetExtractionCursorAsync` throws on a NULL `LastExtractedEntrySequence`, which exists only during the Core V7 backfill window; fails closed and self-heals, not reported.

## Neighbour observations (outside U28, not findings here)

- SessionAttachmentStore.cs:2126: the orphan sweep's `Directory.EnumerateFiles(..., SearchOption.AllDirectories)` does not ignore inaccessible directories, so one unreadable directory aborts the whole startup reconcile (stale-pending GC, missing-row and orphan sweeps) on every start (seen in probe P3a).
- The background recovery pass (every minute) uses the same discovery as startup, so the retention `ReconciliationRequired` carve-out is re-recovered every minute, while `LongRunningOperationStore.StartupRecoveredAttention`'s doc says "a host start recovers again".

## Probes run

Scratch worktree `scratchpad/review/wt/U28-reliability` at `ac0d2d8e` (removed afterwards). Probe source kept at `scratchpad/review/results/C/U28ReliabilityProbeTests.cs`; run log `scratchpad/review/results/C/U28-probes.log`.

| Probe | Test | Result |
|---|---|---|
| P1 | `U28ProbeLongRunningOperationTests.Probe_unobserved_cancellation_is_handed_to_the_handler_as_Running_and_completes` | Passed (defect confirmed) |
| P2 | `U28ProbeIdempotencyClaimTests.Probe_claim_stranded_Running_by_a_crash_is_never_swept` | Passed (defect confirmed) |
| P3a | `U28ProbeSessionAttachmentTests.Probe_unreadable_session_directory_makes_reconcile_destroy_a_live_attachment` | Failed: reconcile aborted earlier with `UnauthorizedAccessException` from the orphan sweep enumeration; row survived |
| P3b | `U28ProbeSessionAttachmentTests.Probe_unprovable_containment_makes_reconcile_destroy_a_live_attachment` | Passed (row deleted, blob deleted on the next pass) |
| P4 | `U28ProbeSessionAttachmentTests.Probe_fork_insert_never_reuses_the_identity_captured_at_copy_time_for_an_equal_plan` | Passed (defect confirmed) |
| P5 | `U28ProbeSessionAttachmentTests.Probe_fork_insert_with_the_same_plan_instance_refuses_the_replaced_copy` | Passed (control) |

Also ran, in the review worktree with `--no-build`: `LongRunningOperationStoreTests.AnUnobservedCancellationIsRecoverableOnceItsLeaseLapses` and `SessionAttachmentStoreTests.InsertForkRowsInAmbientTransactionAsync_refuses_a_copy_unlinked…` — both pass at `ac0d2d8e`.
