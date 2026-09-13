# Task 7 implementation report

## Status

In progress. The authenticated nonterminal-recovery and real two-host V4/V2 slices are implemented. The terminal-suffix finisher and matrix, retained-finalizer restart, final Task 7 authority suites, and final Task 7 commit remain pending. Slice 2 has its standalone evidence report in `task-7-slice-2-report.md`.

## Binding rulings applied

- Same-process phase-boundary qualification may issue recovery directly from the retained live Covenant owner plus freshly recovered exact journal evidence. It must still traverse production closed-ledger read/adoption, authenticated self-settling dispatch, and the coordinator's retained-authority path. Cross-process authentication and handoff belong to the two-host tests.
- Authenticated recovery uses the same scoped `CovenantErasureCoordinator` as dispatch and the concrete V4/V2 handler. The admission is explicit, one-shot, bound by reference identity to that coordinator and its ledger, and carries the full exact journal evidence and expected operation fingerprint.
- Both Covenant and Grimoire are Closed before real maintenance adoption. Candidate read and adoption use short tracked ledger windows; every window closes the connection, clears the exact pool, and reports physical closure before handler entry. There is no ordinary-open fallback.
- The coordinator consumes the retained Covenant lease and Grimoire closure once. Pre-consumption disposal, identity or checkpoint drift, post-consumption process-claim failure, and reconstruction cleanup failures retain both authorities Closed.
- V4/V2 handlers are explicitly self-settling. Authenticated-journal and launch-gap dispatch map their proved durable result directly and never run generic post-handler settlement. Other handlers retain generic settlement.
- Authenticated journal resume is resume-only. It re-recovers and exact-matches binding, slot epoch, envelope revision, and envelope digest; it never begins or sweeps a journal.
- The four concrete host/CLI V4/V2 handlers are each registered once as scoped concrete instances. Normal and authenticated interfaces are factory aliases to that exact instance.

## TDD chronology and evidence

### Retained Closed owner and legal phase matrix

1. Representative RED: `Every_phase_boundary_a_crash_can_fall_between_resumes_to_the_same_ending` failed with `GrimoireMaintenanceUnavailableException` when the helper called ordinary `LongRunningOperationStore.ListAsync` while Grimoire was Closed.
2. The helper now retains exact immutable successful-write snapshots and reconstructs recovery evidence from the authenticated journal. Manual test-only lease spending/reacquisition was removed.
3. Focused retained-owner gate RED proved the real same owner could not re-enter stage one after `Closed`; the correction admits only the same owner, same `Closed` state, prior `StageOneDrained`, and zero request/work leases without changing generation. Foreign/stale-owner coverage remains intact.
4. Mutation: removing the retained-Closed arm restored the focused failure. Restored production returned GREEN.
5. The complete `Every_phase_boundary...` selection passed all 32 rows in 1m02s through production authenticated dispatch. The sole pre-first-effect row preserves the exact proved rollback terminal state and source route; every other row reaches the identical erased route and completed terminal state.

### Closed-before-adoption and self-settlement

1. `Authenticated_adoption_pauses_only_after_both_real_gates_are_closed` pauses after the real database adoption. While paused, request lease, work lease, ordinary connection open, and Covenant read all refuse and recovery remains incomplete. Release resumes to `Completed`.
2. V4/V2 authenticated dispatch now selects the internal self-settling contract by exact kind and supported checkpoint version and passes the explicit admission through `DataRetentionService`/CLI adapters to the coordinator.
3. Launch-gap RED showed generic settlement attempted a stale-owner second compare-exchange after the real handler terminalized the row and cleared `LeaseOwner`. Direct outcome mapping preserves the same terminal winner object, revision, `CompletedAt`, state, error, and cleared owner.
4. Mutation: bypassing the launch-gap self-settling branch restored the pre-readiness refusal. Restored production passed the focused winner-preservation test.

### Resume-only journal evidence

1. `ResumeAuthenticatedAsync` was added beside, not by weakening, `OpenOrResumeAsync`.
2. Focused tests prove stale revision/digest evidence refuses and current evidence resumes, and absent-journal evidence refuses without creating the slot.
3. Mutation: removing exact envelope revision and digest comparisons made `Authenticated_resume_requires_the_exact_current_journal_and_never_rebegins` fail at its refusal assertion. Restored production is GREEN.

### Admission lifetime, cleanup, and strict ledger windows

1. The admission uses one atomic `Available -> Consumed` or `Available -> Released` state. Post-consumption failure alone performs `Consumed -> Released`; a racing disposer cannot steal transferred ownership.
2. Focused integration tests pass for disposed-before-consume, wrong coordinator with exact ledger, exact coordinator with wrong ledger, checkpoint drift before consumption, operation-claim conflict after consumption, and exact-pool-clear failure before adoption/handler entry. Each proves both ordinary Grimoire admissions and Covenant reads remain closed.
3. Exact-pool failure records one injected failure and zero adoption calls. The cleanup path attempts connection close, exact-pool clear, and physical-close reporting independently, then refuses the window if any failed.
4. Recovery-specific reconstruction tests inject retained stage-two refusal and failure after issuance of the real Closed lease but before the lane. Both retain the same Grimoire generation and both gates closed.
5. The route-gate test seam wraps the exact resumed lease and records only dispositions accepted successfully by that retained registration. Checkpoint drift and a claimed operation each prove exactly one accepted `KeepClosed` before release and zero commit/rollback dispositions; the claim row then proves a fresh admission remains possible.
6. Mutations:
   - making admission disposal a no-op made the disposed test RED because `TryConsume` became true;
   - ignoring exact-pool-clear failure made the pool-clear test RED because dispatch continued;
   - restoring the issued-Closed cleanup to `RollbackAndReopen` made only the lane row RED because request admission reopened;
   - disposing the resumed Covenant lease before admission-owned cleanup made the claimed-operation row RED with expected `[KeepClosed]`, actual `[]`, proving the released registration rejects the required disposition.
   All four mutations were restored.

### Review fix: pre-session authenticated refusal

1. Fresh review found that authenticated recovery arrived with real retained closures but still used launch-seeded `InventoryPrepared` progress until its phase session was reconstructed. Quiesce refusal or exact-journal resume refusal therefore reached ordinary `AbortBeforeErasureAsync`, reopened the writer, inferred a no-effect rollback, and treated the active journal as `NoJournal`.
2. Focused RED: both injected `quiesce` and `resume` rows made `RunAuthenticatedAsync` report success through `RollbackAndReopen` instead of refusing recovery.
3. The recovery-only pre-session unwind now bypasses ordinary abort and `CloseAsync`. It spends exactly one accepted `KeepClosed` on the retained Grimoire and Covenant authorities, preserves the adopted nonterminal operation and byte-identical journal evidence, never calls writer reopen, and returns attention/refusal. The ordinary fresh-run proven-no-effect rollback is unchanged.
4. Restored GREEN: 2/2 rows prove no successful operation write, identical journal binding/slot/revision/digest, one successful `KeepClosed` per authority with zero commit/rollback, zero disclosure-writer `ReopenAsync` calls after the injected refusal, request/work/open/Covenant-read refusal, and a subsequent authenticated admission for the same operation.
5. Mutation: forcing the recovery rows back through ordinary `AbortBeforeErasureAsync` made both rows RED with expected post-fault writer reopens `0`, actual `1`. The mutation was restored.

### Exact V4/V2 handler guards

1. Focused V2 RED: a wrong checkpoint reference fell through to coordinator recovery and normalized to `Covenant.MaintenanceFailed` instead of refusing at the authenticated handler guard.
2. V2 now requires exact kind, `RestartIdempotently` policy, current version, payload, and current checkpoint reference. V4 symmetrically requires exact kind, `ReconcileAndComplete` policy, current version, payload, and reference.
3. The pre-adoption candidate and adopted snapshot independently require those exact policies/references in addition to operation, journal binding, launch, revision, owner, and terminal-state checks.
4. Restored GREEN: V2 kind/policy/reference and V4 policy/reference guard rows pass.
5. Mutations: independently omitting V2 reference, V2 policy, V4 reference, or V4 policy makes only the matching row RED by falling through to `Covenant.MaintenanceFailed`. Each was restored.

### DI inventory

1. Initial inventory RED counted normal/authenticated aliases as extra handlers.
2. The inventory now requires, for only the four V4/V2 concrete handlers, exactly one scoped concrete descriptor, one scoped normal factory alias, and one scoped authenticated factory alias. Resolution in one scope must make both aliases `ReferenceEquals` the concrete instance, which is counted once as the owner.
3. Focused inventory coverage includes missing authenticated alias and separate-instance authenticated alias. `RecoveryHandlerCoverageTests` passed 7/7.
4. The CLI/full-host architecture assertion was updated only for the same four owner-bound handlers; every unrelated handler retains the original implementation-type/lifetime/count assertion. Independent mutations redirecting the CLI normal alias and authenticated alias each made the exact architecture test RED. Restored CLI and full-host composition passed 3/3.

## Current verification snapshot

- Isolated single-node test-project build after stale build-server shutdown: succeeded, 0 warnings, 0 errors, 54.32s; incremental rebuilds also succeeded.
- New admission lifetime/pool integration controls: 4/4 passed in 5.8s before the later checkpoint-drift addition.
- Combined focused admission, exact-journal, factory-guard, and DI selection: 14/14 passed before the later symmetric V4/closure additions.
- V4/V2 handler guards plus stage-two/lane reconstruction: 7/7 passed.
- Fully restored focused gate, phase, dispatch, lifetime, guard, and DI run: 24/24 passed in 11.0s.
- Restored CLI launch-gap, CLI graph, and full-host composition: 3/3 passed.
- Fully restored 32-row same-process phase-boundary theory: 32/32 passed in 1m02s.
- Restored checkpoint-drift and claimed-operation ownership-order controls: 2/2 passed in 2s.
- Final broad authenticated/gate/self-settlement/DI selection: 120/120 passed in 1m15s.
- Final exact 32-row same-process phase-boundary theory: 32/32 passed in 1m02s.
- Final CLI launch-gap, CLI graph, and full-host composition: 3/3 passed.
- Final Arcanum test-project build: succeeded, 0 warnings, 0 errors.
- Review-fix focused pre-session refusal: 2/2 passed after a fresh build.
- Review-fix broad authenticated/gate/self-settlement/DI selection: 122/122 passed in 1m18s.
- Review-fix exact 32-row same-process phase-boundary theory: 32/32 passed in 1m02s.

These are slice checkpoints, not final Task 7 verification. All remaining Task 7 suites remain mandatory before final completion.

## Self-review

- No ordinary operation-store read, list, acquisition, or settlement is used after authenticated closure. The only row access is inside the tracked ledger windows.
- The explicit admission is neither ambient nor stored in a mutable coordinator slot. Creator and ledger are reference-bound and the exact adopted operation object is retained.
- The handler cannot start while a ledger handle remains live; preparation returns only after close, exact-pool clear, and physical-close report succeed.
- Invalid authenticated handler input always returns attention/manual-recovery refusal. No arbitrary terminal row is accepted and generic CAS semantics were not weakened.
- Launch-gap pre-closure admission remains distinct; only its already-adopted V4/V2 handler outcome shares self-settling mapping.
- Same-process recovery explicitly qualifies retained-owner coordinator semantics. It does not claim cross-process authentication; the required real two-host tests remain pending.
- Terminal rows remain unadoptable. No terminal-suffix behavior has been added in this slice.

## Slice 2: real two-host authenticated recovery

### TDD chronology and rulings

1. The first real V4 second-host RED stopped before adoption: the recovery-only authority load saw the fresh process-wide `HostProcessToolsRuntimePolicy` as unpublished and denied Covenant. Normal host-tools classification could not fix that circular prerequisite because it runs later in ordinary database bootstrap.
2. The recovery pass now captures the OS marker before opening SQLite, verifies the journal's exact `InstallationId` against the recovery catalog, joins that captured marker with the durable authority row and environment, and publishes only a fresh provisional policy. The actual process singleton remains unpublished and Covenant-denying through the post-adoption barrier. Authority load independently verifies installation identity; both Load and Consume require the provisional policy and retain an already-published real denial as a veto, including a denial that appears while the secret read is paused.
3. The next V4 RED reached real maintenance adoption but refused the exact parked `ReconciliationRequired/Covenant.ErasureIncomplete` row. The store now exposes that arm only from held-lock, exact-fingerprint `AdoptUnderInstallationLockAsync` for current V4 mutation and V2 factory-reset kinds. Ordinary acquisition, classified recovery, both expiry discoveries, other kinds/codes, drifted fingerprints, and terminal rows are unchanged refusals.
4. The next V4 RED reached candidate publication but the fresh runtime's unavailable Covenant tiers had null diagnostics and failed the existing structural validator. `CovenantRuntimeGenerationState.Initial` now uses the existing content-free `Covenant.Unavailable` code for both unhealthy tiers while retaining unavailable capability, null schema versions/fingerprints, and unavailable FTS. Normal schema publication remains the only healthy classifier.
5. The next V4 RED terminalized and verified the transition but failed disclosure-writer `ReopenAsync` before schema health existed. Authenticated startup recovery now defers writer restoration for both commit and proved no-effect rollback. Only an exact `Resumed` startup outcome sets the bootstrap flag; ordinary bootstrap resolves the same concrete/lifecycle singleton and requires its cold reopen after schema, identity/authority publication, protected recovery, physical install-handle close, and launch-gap recovery, but before Covenant or Grimoire readiness.
6. The real two-host theory now runs V4 direct reset and V2 standalone factory reset from host 1's authentic `KeepClosed` state. Host 2 starts on a `LongRunning` thread and pauses after the real maintenance lease adoption. At that barrier startup, database readiness, and the final hosted-service sentinel are incomplete; both real gates are Closed; request, work, ordinary-open, and Covenant read admission refuse; the actual host-tools policy is still unpublished/denied; ordinary providers, workers, effects, and mutation counters are zero; and retirement has not begun.
7. Release converges both entry points through the exact self-settling handler and retirement. V4 retains ordinary rows/files while erasing protected state; V2 erases protected and ordinary catalog/files and retains null parent binding with no receipt activity. Both preserve the immutable launch bytes, requested/server identities, installation identity, master API credential, journal-key fingerprint, and host-1 waiter; host 2 publishes exactly its own baseline generation plus one and fresh request/work/open admission succeeds.
8. The startup task is owned immediately after creation. Its cleanup always releases the artificial adoption barrier and races harness publication against task completion; a published harness is disposed before awaiting startup, while constructor failure is observed through the already-terminal task. No detached cleanup can outlive the retained profile.
9. Final self-review found that the V2 row inferred two required facts through shared retirement helpers instead of asserting them at their owning boundaries. The two-host theory now inspects the parent active store while the transition is parked and requires no active parent publication for both direct V4 and standalone V2. Its V2 terminal branch also re-reads `covenant_authority_state.InstallationIdentity` from the recovered catalog and exact-matches the authenticated envelope identity. The focused V4/V2 plus writer-failure selection remained green at 3/3.

### Negative and mutation evidence

- Marker status malformed/unavailable refuses before database open; wrong installation, clean-row/marker mismatch, and escape-hatch decisions refuse without publishing the real policy. Exact identity and normal later publication controls remain green.
- A real-policy denial at Load, between Load and Consume, or during the secret read refuses before availability/key publication. Mutation removing the post-secret veto made the paused-denial row pass into consumption and was restored.
- Held-lock exact V4/V2 adoption passes. Ordinary, classified, future-expiry discovery, current-terminal-fingerprint, wrong code, wrong kind, and drifted fingerprint cases all refuse without changing the row. Mutation disabling the lock-only arm made both positive rows RED and was restored.
- Mutation returning null initial unavailable diagnostics reproduced the candidate-projection/`SidecarsVerified` failure and was restored.
- Mutation removing exact installation verification made the wrong-installation classifier row RED and was restored.
- Mutation disabling the provisional recovery classification made the real V4 full-host row fail before adoption with Load/Consume/adoption all zero and was restored.
- Authenticated phase-boundary recovery records zero writer reopens across all 32 rows, including the proved rollback row; the live-host success control still reopens status, CRUD, inference, and disclosure. Mutation restoring in-coordinator authenticated commit reopen made the matrix RED and was restored.
- Moving bootstrap writer restoration before schema and moving it after readiness each made the focused ordering guard RED with `Authenticated writer restoration moved outside the post-recovery, pre-readiness window.` Both mutations were restored.
- The same-singleton real `CovenantDisclosureWriter.ReopenAsync` failure test aborts before either readiness signal. The full-host failure row additionally preserves a Completed terminal operation, deleted journal, accepted `CommitAndReopen`, and dark readiness.
- The exact connection inventory caught the new `WithRequiredLedgerAsync(3)` opener and the `AcquireLeaseAsync(8)` arity drift. After adding only those identities, the full inventory passed 35/35.

### Restored verification snapshot

- Startup recovery, classifier, authority, adoption, runtime-transition, and hosted-service authorities: 72/72 passed.
- Real V4/V2 two-host recovery plus failure-before-readiness: 3/3 passed; with the two writer bootstrap authorities: 5/5 passed.
- Exact 32-row phase-boundary matrix plus live-host reopen control: 33/33 passed in 1m05s.
- Grimoire connection acquisition inventory after the in-scope correction: 35/35 passed.
- Combined DI aliases, CLI/full-host composition, connection inventory, typed dispatch, and phase authorities: 73/73 passed.
- `dotnet build RetroDownfall.Arcanum.slnx -c Release --no-restore --nologo`: succeeded with 0 warnings and 0 errors after restoring the six missing isolated-worktree assets files.

These are slice-2 checkpoints, not final Task 7 verification. Terminal-suffix behavior remains exclusively slice 3.

### Slice 2 formal review fix round 1

1. Mutation first set the writer-failure seam to `false`. The existing expected-failure row incorrectly passed 1/1 by entering the ordinary successful-startup path, confirming the review finding.
2. The expected-failure path now separately awaits startup, disposes any unexpectedly returned harness, and throws an explicit test failure if readiness succeeds. With the bypass mutation retained it turned RED with the intended unexpected-success message; restoring the failure seam returned GREEN.
3. A focused pre-restart exception seam observed host 1 remained undisposed under the old local declaration. Declaring host 1 with `await using` while retaining its explicit idempotent disposal at the restart boundary made the row GREEN.
4. Restored focused verification passed V4, V2, expected writer failure, and early host-1 cleanup 4/4 with no failures or skips. No production files changed in this review round.
