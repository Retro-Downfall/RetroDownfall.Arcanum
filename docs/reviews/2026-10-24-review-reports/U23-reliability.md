# U23 (Arcanum.Infrastructure, "Covenant (1)") - reliability review

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e`. Lens: reliability.

Note on the unit list: `scratchpad/review/units.json` was not present when this review ran (the scratchpad had been
emptied). The unit was reconstructed from its stated size: the alphabetical listing of
`src/RetroDownfall.Arcanum.Infrastructure/Covenant/*.cs` reaches exactly 9,964 cumulative lines at
`CovenantManagementService.cs`, so U23 is the first 20 files of that directory (listed under Coverage).

## Findings (most severe first)

### F1 (Important, cancellation) - every owner-cleanup pass revokes all live memory-delegation grants, even with nothing to clean

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceCoordinators.cs:44`

```csharp
internal async ValueTask<Result<CovenantCleanupOutcome>> RunBatchAsync(int maxEvents, CancellationToken cancellationToken)
{
    await using MemoryDelegationMutation? memoryMutation = await memoryDelegation.BeginOptionalAsync(cancellationToken).ConfigureAwait(false);
    Result<CovenantCleanupLease> acquired = await gate.AcquireCleanupAsync(CovenantOperationScope.Global, cancellationToken)...
```

`MemoryDelegationCoordinator.BeginMutationAsync` (Core, `MemoryDelegationCoordinator.cs:332-350`) calls `RevokeAll()`,
which marks every live registration terminal, cancels its `Cancellation` (the root's and every child's `Revocation`),
and then waits for admitted effects to drain. `CovenantMaintenanceHostedService` drives this coordinator on every pass
(`CovenantMaintenanceHostedService.cs:121-137`), every five minutes while the feature is on and the canonical tier is
healthy (`Interval`, line 60). The barrier is taken before the gate lease, before the journal is read, and whether or
not any owner-deletion event is pending, so an idle installation revokes every grant every five minutes. A refused gate
acquisition also revokes, because the barrier was already taken.

Failure scenario: an operator with Covenant on calls `POST /api/intelligence/memory-delegation`. Grants live at most
five minutes and the sweep fires every five minutes, so a delegation whose grant is live at a sweep boundary loses it:
an unconsumed grant is refused (`MemoryDelegation.StaleSource`), and a consumed child has its in-flight provider effect
cancelled and its result withheld while its provider usage is still billed (the behaviour
`WizardIntelligenceProviderTests.MemoryDelegation` pins for a mutation landing mid-run: `Assert.Null(returned)`). The
proportion of delegations lost is roughly the child's run time over 300 s, with no deletion having happened at all. The
pass also parks behind the effect's teardown while it holds the Grimoire work lease and the delegation admission
semaphore.

The codebase already uses the right shape elsewhere: `GrimoireRepository.TurnCommit.cs:39` takes the barrier only when
`request.Mutations` is non-empty, and `LongRestSweepStore.cs:94` skips it when deferred; DESIGN 10.21.9 scopes revocation
to "a relevant mutation".

Proof: probes `Probe_an_empty_owner_cleanup_pass_revokes_a_live_memory_delegation_grant` (empty journal: outcome 0/0/0,
yet `root.Revocation.IsCancellationRequested` is true and a later `ConsumeAsync` fails) and
`Probe_an_empty_owner_cleanup_pass_cancels_an_in_flight_delegated_effect_and_waits_for_it` (the admitted effect's
revocation is cancelled and the pass stays incomplete until the effect is disposed). Both pass at `ac0d2d8e`.

Fix (S/M): before taking the barrier, read the cleanup cursor and probe for one pending event (or `FullSweepRequired`)
with `CovenantOwnerDeletionReader` on the scope connection; return the zero outcome (and still republish availability if
that behaviour is wanted) without the barrier when nothing is pending. Keep the existing barrier -> lease -> BEGIN
IMMEDIATE order for the non-empty case; the worker already re-reads the cursor inside the transaction, so a race with a
new event only defers it to the next pass. Add the probe above as the regression test (an empty pass leaves a live grant
unrevoked).

### F2 (Minor, cancellation) - a refused, replayed or not-found Covenant erase also revokes every live delegation grant

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantEntryErasureService.cs:351`

`ApplyAsync` begins the memory-delegation mutation as its first statement, before the null/empty-token check (355), the
parse (360), the receipt-first replay (435-440), the absent-entry path (`AnswerAbsentAsync`, 458-466, which holds no
erasure authority and can delete nothing) and the authority-epoch refusal (451). Every one of those exits revokes and
cancels all live grants without mutating anything.

Proof: probe `Probe_an_invalid_covenant_erase_request_revokes_a_live_memory_delegation_grant` calls `ApplyAsync(null, ...)`;
it returns `Validation.InvalidBody` and the live root's revocation is cancelled.

Fix (S): take the barrier only on the branch that closes a scope, immediately before `gate.AcquireEntryErasureAsync`
(after the probe read lease is released), which keeps the barrier-before-gate ordering.

### F3 (Minor, crash-consistency; latent) - recovery reports a ReopenedVerified family reinitialize as Completed while publication is still owed

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeCoordinator.cs:576-579` (handler) against
lines 304-357 (coordinator).

The coordinator checkpoints `ReopenedVerified` (304-310) and only then runs `PublishCommittedAsync` (key, issuer,
availability, gate transition), `ReopenDisclosureWriterAsync`, and the disposition; nothing after publication is
checkpointed. A resumed run from `ReopenedVerified` therefore publishes. The registered recovery handler returns
`Completed()` for exactly that checkpoint, so a crash between the checkpoint and a successful publication, or a
`KeepClosed` after a failed publication or writer reopen, is closed as finished: the unpublished candidate key and
generation are never published and the closed gate owner is never resumed. The existing test
`Reinitialize_recovery_completes_only_a_verified_reopen_and_closes_an_unstarted_row` pins this.

Latent: `CovenantFamilyReinitializeCoordinator` is constructed nowhere and the transition has no implementation (DESIGN
10.17 "deliberately absent"), but the handler is registered and the inconsistency would become data-affecting the day the
coordinator is wired.

Proof: probe `Probe_U23_a_resumed_ReopenedVerified_checkpoint_still_owes_publication_that_recovery_reports_as_completed`
runs the coordinator from `ReopenedVerified` (Steps contain `publish` and `reopen-writer`) and the handler on the same
checkpoint (`Completed`).

Fix (S): add a durable terminal phase written after publication and writer reopen (or let the long-running completion
itself be that record) and complete only on it; treat `ReopenedVerified` as resume-required; change the pinning test.

### F4 (Minor, idempotency; latent) - every resumed family reinitialize re-runs the pre-drop erasure

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeCoordinator.cs:208`

`EraseProtectedStateAsync` sits between two guarded `AdvanceAsync` calls but is not itself guarded, so a run resumed from
`FamilyDropped`, `DatabaseCompacted`, `AcceleratorInstalled`, `SidecarsVerified` or `ReopenedVerified` enumerates the old
dataset generation's work and calls both erasure kernels again, after the family it describes was dropped and the tiers
reinstalled. The class's own contract ("a phase already recorded is a step already committed, and re-running it would
repeat an effect") is broken, and an erasure source that reads the dropped family, or a kernel that rejects the old
generation under the new lease, fails the step and pins the operation at `KeepClosed` on every retry.

Proof: probe theory `Probe_U23_a_resumed_run_past_the_drop_re_runs_the_pre_drop_erasure` (three phases): Steps contain
`erase-artifacts` and `erase-managed-files` and the kernel call count is 1. The existing
`A_resumed_run_never_repeats_a_step_its_checkpoint_already_records` passes because it only asserts the absence of the one
named step.

Fix (S): run the erasure only when `state.Phase < CovenantFamilyReinitializePhase.LocalArtifactsProcessed`, and extend the
resume test to assert the kernels are not called.

### F5 (Minor, unbounded-growth; latent) - a stale index rebuild "restarts" into an orphaned operation nothing drives

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantIndexRebuildCoordinator.cs:237-259`

On `RestartRequired`, `AbandonStaleAsync` abandons the row and calls `StartAsync`, discarding the result (`_ = await
StartAsync(...)`). The new operation's identity never reaches the caller (`AdvanceAsync` returns the old progress with
`RestartRequired`), no checkpoint is ever written for it, and the next startup's handler closes it as
`covenant.index_rebuild_identity_changed`. The remark "starts a new one rather than rewriting this operation's identity"
is therefore a no-op that only leaves a Running ledger row with a five-minute lease. Latent: only the unregistered
`CovenantMaintenanceService` starts rebuilds.

Proof: trace (no driver exists to execute). `StartAsync` returns `Result<LongRunningOperation>`; `AbandonStaleAsync` drops it
at line 256 and returns `Result.Success()`; `SaveCheckpointAsync` returns that to `AdvanceAsync`, which returns `advanced`.

Fix (S): return the replacement operation to the caller (e.g. on the progress result) so the driver continues it, or do not
start one and let the caller decide.


## Coverage

Read in full, every line (20 files, 9,964 lines), all under `src/RetroDownfall.Arcanum.Infrastructure/Covenant/`:

1. `CampaignPathFullInstallationResetContracts.cs` (488)
2. `CampaignPathMarkerLifecycle.FullInstallationReset.cs` (2,048)
3. `CampaignPathMarkerLifecycle.MarkerOwnershipProof.cs` (119)
4. `CampaignPathMarkerLifecycle.RestartRootProof.cs` (168)
5. `CampaignPathMarkerLifecycle.RestoreInventory.cs` (290)
6. `CampaignPathMarkerLifecycle.cs` (1,093)
7. `CampaignPathMarkerLifecycleContracts.cs` (372)
8. `CampaignPathMarkerRootAuthority.cs` (296)
9. `CovenantCampaignScopeProbe.cs` (149)
10. `CovenantContextProvider.cs` (171)
11. `CovenantEntryErasureService.cs` (1,112)
12. `CovenantExclusiveDisposition.cs` (136)
13. `CovenantExportPolicy.cs` (333)
14. `CovenantFamilyReinitializeCoordinator.cs` (587)
15. `CovenantFeatureConfigurationPublisher.cs` (152)
16. `CovenantIndexRebuildCoordinator.cs` (305)
17. `CovenantMaintenanceCoordinators.cs` (271)
18. `CovenantMaintenanceHostedService.cs` (232)
19. `CovenantMaintenanceService.cs` (527)
20. `CovenantManagementService.cs` (1,115)

Read partly, to trace callers and contracts: `ICampaignPathMarkerLifecycle.cs`, `ICovenantConnectionSource.cs`,
`CampaignPathMarkerIntentStore.cs` (insert/read), `CovenantPersistedAvailabilityPublisher.cs` (republisher),
`CovenantStore.ReadScopeCensusAsync`, `CovenantCleanupWorker.RunBatchAsync` head, `CovenantOperationGate` (Campaign
scope probe use), `HostToolsMarkerPairResetCoordinator` (inventory, prepare, reconcile and release call sites),
`MemoryDelegationCoordinator` (BeginMutationAsync, RevokeAll, Consume/AdmitEffect), `MemoryDelegationCapabilities.cs`,
`LongRunningOperationRecoveryRegistry` (two descriptors), `ServiceCollectionExtensions` (registrations of every unit
type), DESIGN 10.19.7, 10.21.9, the 10.17 maintenance tables and the "deliberately absent" paragraphs.

Reachability matters for severity here and was established by tracing registrations:
- `campaign_path_identities` has no production writer (Campaign-path administration is unbuilt, DESIGN "deliberately
  absent"), so the restore-cleanup and full-reset Campaign cleanup arms of the marker lifecycle only ever see an empty
  registry in production; defects there are latent.
- `CovenantMaintenanceService` is registered in no container and `CovenantFamilyReinitializeCoordinator` is constructed
  nowhere; `CovenantIndexRebuildCoordinator` is registered but only `CovenantMaintenanceService` uses it. Their recovery
  handlers are registered.
- Live in production when `Arcanum:Features:Covenant` is on: `CovenantContextProvider`, `CovenantEntryErasureService`,
  `CovenantExportPolicy` (the lease-less arm also when off), `CovenantManagementService`, `CovenantCampaignScopeProbe`,
  `CovenantFeatureConfigurationPublisher` (always), `CovenantMaintenanceHostedService` and its three coordinators, and
  the memory-delegation route (`POST /api/intelligence/memory-delegation`, which requires Covenant read authority).

## Checked and sound

- `CovenantEntryErasureService` commit uncertainty: Microsoft.Data.Sqlite installs a rollback hook in `SqliteTransaction`,
  so a COMMIT that SQLite auto-rolls back (SQLITE_FULL/IOERR) sets `_externalRollback` and the dispose at line 802 does
  not throw; `rollbackFailed` is not set spuriously, the fresh-connection re-read finds no receipt, and the scope reopens
  as a rollback. A busy COMMIT is excluded from the uncertainty catch and the `await using` rolls the transaction back
  before `SqliteBusyRetry` re-runs from the receipt probe (the comment "left the transaction open" is inaccurate, the
  behaviour is right). Every completion passes `CancellationToken.None`; the republisher swallows and logs. 26 existing
  tests cover the matrix; `CovenantEntryErasureServiceTests` pass.
- `CovenantContextProvider` releases the turn lease on every non-plan exit, including exceptions from the store and the
  linker (`retained` flag in `finally`).
- `CovenantManagementService.ExplainAsync` transfers the turn lease exactly once (set only after `Create` returns) and
  hands an inert lease on refusal; `StatusAsync` turns a census failure into `Failed` because
  `CovenantStore.ReadScopeCensusAsync` catches `SqliteException` (CovenantStore.cs:953) and rolls back on `None`.
  Search cursors are authenticated envelopes; truncated payloads raise `ArgumentException`/`EndOfStreamException`, both
  caught.
- `CovenantExportPolicy`: ledger identities uppercase (`ArtifactSensitivityLedger.Format`, line 701), canonical identities
  `ToString("D")` (CovenantStore binds the same), so neither count reads zero by casing; missing tables refuse as
  ManualRecoveryRequired; the lease is revalidated after the reads. `CovenantExportPolicyTests` pass.
- `CovenantCampaignScopeProbe` binds the owner-deletion lookup uppercase (matching the Campaigns Id trigger) and the live
  lookup through `GrimoireEntitySql.Format` (uppercase); it opens its own scope and disposes it.
- `CovenantFeatureConfigurationPublisher`: callbacks serialized under one `Lock`, each republishes `CurrentValue`, so
  out-of-order delivery cannot leave the last edit unapplied; disposal is idempotent and removes the subscription.
- `CovenantMaintenanceHostedService`: stopping-token cancellation breaks the loop, any other failure in one sweep is
  logged and the next sweep runs; the work lease outlives the scope by `await using` order. Coordinators commit before
  republishing and roll back on a worker failure.
- Marker lifecycle ownership: every opened root authority is disposed or retained on every exit (unretained-seed release,
  inventory catch-and-rethrow, identity-mismatch disposal in the factory, displaced-root disposal, one-shot
  `Interlocked.Exchange` dispose); retention is refused after lifecycle disposal under the same lock; the post-disposition
  finalizer is one-shot and refuses a non-committing disposition. In-process reconcile is idempotent across a crash
  (absence through a retained root completes). The restart-arm absence blocker is the documented DESIGN 10.19.7 choice.
- Full-reset Campaign cleanup: one short transaction per child with a compare-and-swap on `PhaseRevision`; already
  terminal children counted with no I/O; checked arithmetic on counts; caller cancellation rethrown; inventory and
  revalidation release the owner's retained roots in `finally` on every failure; replay compares the complete vector.
- `CampaignPathRestoreCleanupIntentVector`: preimage sized exactly, duplicates and empty identities refused, the empty
  digest frozen. `CovenantExclusiveDisposition.Select` is total and returns KeepClosed on every uncertainty.
- Existing suites run on the shared build: 79 tests (maintenance hosted service, coordinators, reinitialize, operation
  recovery, entry erasure) and 178 tests (marker lifecycle, full-reset inventory/cleanup, export policy, feature publisher,
  delegation lifecycle) all pass.

Latent observations, not raised as findings (unreachable in production, or fail-closed):
- Restore preparation replay adopts an existing child by owner/Campaign/kind uniqueness
  (`CampaignPathMarkerIntentStore.InsertOrReadRestoreCleanupAsync`) without comparing the stored marker digest, path or
  revision with what the attempt just observed; a changed marker yields a success receipt that reconciliation later refuses
  with ManualRecoveryRequired. The full-reset arm compares the whole vector; the restore arm could do the same.
- The phase-ladder comment in `CampaignPathMarkerLifecycleContracts.cs:38-42` promises a durable phase after each effect;
  the RestoreCleanup arm writes all five phases and the delete in one transaction, so a crash after the delete leaves
  `Prepared`, which on restart is the documented absence blocker.
- `CovenantIndexRebuildRecoveryHandler` closes a crashed, not stale, rebuild with `covenant.index_rebuild_identity_changed`
  (documented in the DESIGN table, but the Core registry's RecoveryIntent text says "Resume ... from its exact durable
  cursor"). `AdvanceAsync` checkpoints with the caller's `operation.CheckpointVersion` and decodes the caller's payload, so
  any future driver must re-read the row between batches.
- The three maintenance coordinators begin `BEGIN IMMEDIATE` without `SqliteBusyRetry`; a writer holding the lock past the
  busy timeout surfaces as a logged error and the batch waits for the next pass.


## Probes run

Scratch worktree `scratchpad/review/wt/U23-reliability` at `ac0d2d8e`, test project built, then removed. Probe sources are
saved beside this report in `U23-reliability-probes/` (`U23ReliabilityProbeTests.cs` and a diff adding two probes inside
`CovenantFamilyReinitializeCoordinatorTests`).

| Probe | Shows | Result |
| --- | --- | --- |
| `Probe_an_empty_owner_cleanup_pass_revokes_a_live_memory_delegation_grant` | F1: empty journal, outcome 0/0/0, live grant revoked, later consume refused | Passed (defect confirmed) |
| `Probe_an_empty_owner_cleanup_pass_cancels_an_in_flight_delegated_effect_and_waits_for_it` | F1: admitted effect cancelled; pass blocked until the effect is disposed | Passed (defect confirmed) |
| `Probe_an_invalid_covenant_erase_request_revokes_a_live_memory_delegation_grant` | F2: `ApplyAsync(null)` returns InvalidBody and revokes the grant | Passed (defect confirmed) |
| `Probe_U23_a_resumed_ReopenedVerified_checkpoint_still_owes_publication_that_recovery_reports_as_completed` | F3: coordinator publishes from ReopenedVerified; handler says Completed | Passed (defect confirmed) |
| `Probe_U23_a_resumed_run_past_the_drop_re_runs_the_pre_drop_erasure` (FamilyDropped, SidecarsVerified, ReopenedVerified) | F4: erasure kernels re-run on resume | Passed x3 (defect confirmed) |

Command: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build --filter
"FullyQualifiedName~U23ReliabilityProbeTests|FullyQualifiedName~Probe_U23"`: 7 passed, 0 failed.

