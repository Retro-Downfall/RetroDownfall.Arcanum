# Task 7 implementation report

## Status

In progress. This report records the authenticated nonterminal-recovery slice. The two-host V4/V2 qualification, terminal-suffix finisher and matrix, retained-finalizer restart, full Task 7 authority suites, documentation closeout, and final single Task 7 commit remain pending.

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

These are slice checkpoints, not final Task 7 verification. All remaining Task 7 suites remain mandatory before final completion.

## Self-review

- No ordinary operation-store read, list, acquisition, or settlement is used after authenticated closure. The only row access is inside the tracked ledger windows.
- The explicit admission is neither ambient nor stored in a mutable coordinator slot. Creator and ledger are reference-bound and the exact adopted operation object is retained.
- The handler cannot start while a ledger handle remains live; preparation returns only after close, exact-pool clear, and physical-close report succeed.
- Invalid authenticated handler input always returns attention/manual-recovery refusal. No arbitrary terminal row is accepted and generic CAS semantics were not weakened.
- Launch-gap pre-closure admission remains distinct; only its already-adopted V4/V2 handler outcome shares self-settling mapping.
- Same-process recovery explicitly qualifies retained-owner coordinator semantics. It does not claim cross-process authentication; the required real two-host tests remain pending.
- Terminal rows remain unadoptable. No terminal-suffix behavior has been added in this slice.
