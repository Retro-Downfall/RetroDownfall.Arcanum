# SDD ledger — plan: docs/superpowers/plans/2026-09-06-issue-256-turnstile-fast-path-addendum.md

Spec: docs/superpowers/specs/2026-09-06-issue-256-turnstile-fast-path-addendum.md
Worktree: /Users/mat/Source/apps/RetroDownfall.Arcanum/.worktrees/issue-256-hosted-producer-admission
Plan/spec approval commit: a5a5d230
Main plan: docs/superpowers/plans/2026-09-06-issue-256-hosted-producer-admission.md

## Task todo

- [x] Task A: Add the admission benchmark without changing production
- [x] Task B: Characterize all current gate invariants
- [ ] Task C: Implement and measure the benchmark-gated hot path, if justified
- [ ] Task D: Prove queue-free bounded reopen in main-plan Tasks 3-13
- [ ] Task E: Prove maintenance classification in main-plan Task 14

## Preflight self-consistency scan

| Task | Tests against implementation | Files and later consumers | Finding |
|---|---|---|---|
| A | Ordinary tests pin schemas, comparison behavior, failure exits, and leak validation; Native AOT smoke exercises the real host | New outside-solution benchmark, script, tests, and explicit CI invocation feed Tasks B/C and qualification | Consistent; benchmark source and comparator must be identical at B and C |
| B | Deterministic gate characterization precedes any correction and pins every current invariant | Gate tests and any correctness-only gate correction define reviewed monitor baseline B | Consistent; a correction changes B but is not optimization evidence |
| C | Structural RED tests precede candidate production edits; complete gate slices and exact B/C process pairs decide retention | Candidate gate/tests consume A and B; rejection restores production byte-identical to B while retaining evidence | Consistent; candidate may ship only on all predeclared gates |
| D | Every main-plan producer proves its exact slot/queue ownership, backlog bound, reopen, reclose, cancellation, and shutdown behavior | Tasks 3-13 consume the dormant gate signal without adding a gate scheduler | Consistent; producer capacity remains producer-owned |
| E | Task 14 executable reverse-caller and authority proofs pin backup/reset/restore/schema classification | Inventory consumes final producer code and existing exclusive-transition identities | Consistent; no speculative capability hierarchy |

## Preflight shared-file and shared-interface scan

| Tasks | Producer and consumer | Finding |
|---|---|---|
| A, B | Harness calls the unchanged gate while B expands characterization | Compatible; H is committed before B and the harness remains unchanged |
| A, C | Comparator and workload schema evaluate exact B/C binaries | Compatible only if candidate changes stay inside the checked admission-source allowlist |
| B, C | B fixes current correctness before C changes performance internals | Ordered; C descends from reviewed B and preserves every B test |
| C, D | Optional gate internals expose the same public admission/generation contracts to producers | Compatible; D cannot depend on candidate-only internals |
| D, E | Final producer state machines create the exact roots and frontiers Task 14 catalogs | Ordered through main-plan Tasks 3-14; Task 14 must not manufacture proof from scanner output |
| A, Qualification | Native host/script/CI smoke feed exact-tip qualification | Compatible; smoke is correctness evidence, paired B/C runs are the only optimization evidence |

## Preflight rulings

Ruling: Run addendum Tasks A and B after main-plan Task 2 is independently approved, then decide whether Task C is justified from committed-H calibration and reviewed-B evidence; a candidate still requires exact B/C acceptance evidence — this prevents benchmark work from racing the inventory proof or turning calibration into a performance claim — if wrong, sequencing costs time but no production semantics.

Ruling: Treat addendum Task D as binding acceptance criteria inside each main-plan Task 3-13 review rather than one separate implementation dispatch, and Task E likewise inside main-plan Task 14 — this keeps ownership tests beside the producer state they prove — if wrong, final review can add a dedicated cross-producer audit without changing the gate architecture.

Ruling: The gate owns only a dormant generation signal; no permanent/global queue or queue threshold is permitted. Workspace's first two identities run without an overflow queue and only a third links an existing producer state into its lazy intrusive FIFO — this is the user's explicit approved fast-path design — if wrong, revert the isolated design commit before producer work rather than introduce hidden scheduler policy.

Ruling: Task A may add exactly one Infrastructure friend grant for `RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks`; Core needs none, and the host must use the real internal gate/drain/lifecycle/native initializer/options configurator plus pooled EF composition — this preserves an honest executable boundary without publishing a benchmark API — if wrong, the harness cannot prove it measures production admission and H must not be recorded.

Ruling: The B/C compiled-source allowlist is exactly `GrimoireConnectionAdmissionGate.cs` plus the possibly absent-at-B `GrimoireConnectionAdmissionEpoch.cs`; all candidate types must fit those files or H is invalidated and must be recommitted — this prevents measurement of a drifting harness or broader product change — if wrong, a reviewed pre-H brief/spec correction is required rather than widening evidence after measurement.

Task A reconnaissance: complete (`task-A-recon.md`; outside-solution AOT host feasible, one Infrastructure IVT, real SQLCipher/pooled EF, dedicated threads, two-publish/twelve-process qualification; no production edits)
Task A implementation: complete at corrected proposed H `092869fdc4f746850115ab0a17bfc08dce490751` (base 671a7c89; implementer /root/task2_local_publication; strict TDD; no production gate or Covenant benchmark edits)
Task A preflight review (3 Important requirements gaps corrected before implementation: temp-home deletion ownership, process-wide counter boundary, and host-only AOT JSON roots)
Task A preflight rereview: approved (0 Critical/Important/Minor; implementation may proceed)

Ruling: The benchmark executable exclusively creates and owns a nonce-marked child under a fresh system-temporary parent, refuses a pre-set `ARCANUM_TEST_HOME`, and deletes only after exact canonical parent/prefix/non-link/marker validation; the script never deletes the database home — this makes direct invocation and qualification safe from user-path deletion — if wrong, cleanup fails closed and leaves an isolated temp directory rather than risking user data.

Ruling: Persistent workers use precreated and warmed kernel-backed wait handles around the armed/run/loop-complete/parked protocol. Arming finishes before the process baseline; volatile run publication precedes stored worker start stamps; worker allocation/timestamp terminals precede real completion stamps; the last completer wakes the blocked controller once; the controller records process terminals and releases all completed workers once; workers then record parked stamps. Primary timing/allocation excludes handle calls. Process Gen0/contention includes the fixed rendezvous and is diagnostic only — if wrong, those metrics are invalid evidence and comparison exits 2.

Ruling: Native `--smoke` executes the real host `AdmissionBenchmarkJsonContext` against the embedded manifest, revision run, full six-pair bundle, and accepted/rejected reports, while ordinary tests pin exact pure-source links — this prevents an immutable H that fails only when qualification serializes an AOT-only payload — if wrong, the native smoke fails closed before H review.

## Task A independent whole-review remediation

Ruling: `659a3441d90aa25d5050cf8dd561ed3d88816edc` and its calibration are invalid and superseded, not H evidence. Independent review verified that the instrument ceiling-expanded non-divisible totals, replayed synthetic transition order, busy-spun the controller and completed workers, bracketed terminal callbacks only around throughput, truncated allocation ratios, excluded EF from the universal p99 gate, accepted incomplete/malformed input identity and derived math, had no linked watchdog, hard-coded live waiters to zero, accumulated historical churn, leaked direct connections, did not execute a real link attack, rewrote qualification cancellation to exit 2, and could hash empty toolchain output successfully.

Ruling: the prior no-event boundary is itself the measurement architecture-breaker. The corrected harness uses one maximum-size persistent worker set and precreated/warmed OS-backed wait handles. Arming precedes the process baseline; volatile run publication precedes actual stored worker start stamps; workers record allocation/timestamp terminals and actual completion stamps before blocking; the last completer emits exactly one completion signal; the blocked controller records process terminals and emits one shared release; workers then record actual parked stamps. Primary timing/allocation excludes handle calls. Process Gen0/contention includes the fixed nonallocating/non-`Monitor` rendezvous and remains diagnostic only.

Ruling: corrected identity uses a checked-in path/optionality-only catalog plus a manifest-pinned framed shape digest, immutable-content digest outside the two-file candidate allowlist, and complete sorted per-path presence/content digests. Runtime, xUnit tracked-file enumeration, and qualification `git ls-tree` derive the closure independently, including every embedded schema SQL file, so symmetric omission cannot pass.

Ruling: remediation requires a new normal proposed-H commit, new Native AOT smoke, and fresh calibration. No old calibration value, count, or artifact hash may be cited as valid.

Task A remediation: complete. Docs invalidation/correction commit `ea2aabe5`; corrected proposed-H implementation commit `092869fdc4f746850115ab0a17bfc08dce490751`; Task A aggregate 124/124; packaging 12/12; locked restore clean; no production-project lockfiles; clean host build 0 warnings/errors; both format checks, shell syntax, and diff check clean; real published Native AOT smoke exit 0 with 36 cells.

Task A calibration: exact clean proposed H `092869fdc4f746850115ab0a17bfc08dce490751`; calibration-only exit 0; artifact `/private/tmp/grimoire-admission-calibration-092869fdc4f7.json`; 405,969 bytes; SHA-256 `81fbea3ba095b03389dbef67f7e8422ae767c398789c52371631aa92825bcbe5`. Closed validation passed exact revision/profile/36-cell accounting/checksum/worker/allocation/final-state/churn/runtime and all 1,641 sorted catalog inputs. This is not B/C qualification or acceptance evidence; proposed H remains subject to independent review.

## Task A independent-review fix round 1

Ruling: `092869fdc4f746850115ab0a17bfc08dce490751` and its calibration are superseded
and are not H evidence. The new normal implementation commit
`0eee1f46e9f8ea3fa710cf531291c30472ef21e2` closes all seven Important review findings:
required JSON metric presence; every-EF-cell allocation bounds; overflow-safe finite aggregates;
host-independent C#/SQL/fixed/optional closure and embedded-resource equality; bounded teardown with
positive deletion witnesses; actual smoke/calibration exact-accounting/final-state enforcement; and
controlled qualification ancestry/catalog/H-caller-B-C byte refusal paths.

Task A review-fix verification: aggregate Task A slice 165/165; standalone packaging 19/19; locked
osx-arm64 restore clean; nonincremental host build 0 warnings/errors; both format verifiers, shell
syntax, diff check, lockfile boundary, forbidden pattern, forbidden production/Covenant/workflow
audits clean; real published Native AOT smoke exit 0 with all 36 cells. No production gate/epoch,
Covenant benchmark, workflow, spec, or plan bytes changed.

Task A review-fix calibration: exact clean proposed H
`0eee1f46e9f8ea3fa710cf531291c30472ef21e2`; calibration-only exit 0; artifact
`/private/tmp/grimoire-admission-calibration-0eee1f46e9f8.json`; 405,999 bytes; SHA-256
`5d0f4c484661721970048e71443530ebb4b1aaa073a68a95d4574d8ff714d62d`. Closed validation passed
the exact revision/session/profile, 36-cell phase totals/checksums/workers/allocation/callbacks,
zero-live final state, drain/reopen, finite 0/64/640 churn, Native AOT runtime, and exact sorted
1,641-entry catalog path/presence/digest map. This is not B/C qualification or acceptance evidence;
proposed H remains subject to independent review.

Deferred Minor review findings for final triage (not changed in fix round 1):

- Add a non-degenerate literal golden bootstrap fixture with unequal pair ratios.
- Strengthen the asymmetric completion-order test so worker one's real completion transition is
  established before worker zero is released.

## Task A independent-review fix round 2

Ruling: `0eee1f46e9f8ea3fa710cf531291c30472ef21e2` and its calibration are superseded
and are not H evidence. A real live worker held inside an operation reproduced the remaining finding
5 and the round-1 regression: after controller cancellation and the production five-second missed
join, releasing the worker crashed the test host with an unhandled `ObjectDisposedException` at
`WorkerLoop` because teardown had disposed `_allCompleted` while the worker could still reach it.

Ruling: worker shutdown now signals all workers and attempts every bounded join, but does not dispose
any worker-reachable wait handle until every join succeeds. A missed join leaves all handles open
and its recorded termination witness false. A later explicit bounded retry can positively join the
worker and only then attempt every handle disposal; no deferred reaper was added. The
measured-resource witness requires both positive worker termination and worker-connection disposal,
and both final-state sampling and benchmark-home deletion consume it. Primary cancellation remains
exit 130, runtime teardown still attempts pre-drain/provider/final-drain/pool clearing, and incomplete
lifecycle retains the owned home.

Task A round-2 implementation: normal commit
`f51ac3f84c3b408510e448311d7a5e15bdbc041e`. TDD evidence: the real delayed worker test first
aborted vstest through the reproduced unhandled exception, then GREEN 1/1; the absent measured
witness/coordinator contract produced compile RED, then the focused teardown set was GREEN 7/7 and
the complete persistent-worker class GREEN 16/16.

Task A round-2 verification: aggregate Task A slice 166/166; standalone packaging 19/19; locked
osx-arm64 restore clean; nonincremental host build 0 warnings/errors; both format verifiers, shell
syntax, diff check, lockfile boundary, forbidden host-pattern, and forbidden
production/Covenant/workflow audits clean; real published Native AOT smoke exit 0 with all 36 cells.

Task A round-2 calibration: exact clean proposed H
`f51ac3f84c3b408510e448311d7a5e15bdbc041e`; calibration-only exit 0; artifact
`/private/tmp/grimoire-admission-calibration-f51ac3f84c3b.json`; 405,946 bytes; SHA-256
`1f164b28cf26829380165c61dadefeb041d566d2541a072da04a227327e95a59`. Closed validation returned
true for exact revision/session/profile, 36-cell phase totals/checksums/workers/allocation/callbacks,
zero-live final state, drain/reopen, finite 0/64/640 churn, Native AOT runtime, and exact sorted
1,641-entry catalog path/presence/digest map. This is not B/C qualification or acceptance evidence;
proposed H remains subject to independent review.

Additional deferred Minor review finding for final triage (not changed in fix round 2):

- Ensure subprocess-timeout cleanup terminates and reaps the timed-out child before fixture cleanup.
- Extend the real delayed-worker regression's cleanup guard around its startup and controller-join
  assertions so an assertion failure cannot leave the deliberately blocked worker unreleased.

Task A: fix round 1/5 (6 original findings addressed, teardown witness remained open, one live-worker
handle-disposal Important introduced; commits `7c5c0c0d..37244b88`).

Task A: fix round 2/5 (remaining teardown/witness finding and live-worker handle-disposal regression
addressed; commits `37244b88..ed66cf55`).

Task A: complete (implementation H `f51ac3f84c3b408510e448311d7a5e15bdbc041e`, report tip
`ed66cf55a3ca038ccccb98273e884fed268c1170`, scoped re-review clean with four deferred Minors).

## Task B monitor characterization

Task B implementation: complete at proposed B
`18c7a2b2f779dd4a87a1d45c1a1e61fa924200ff` (base `8544401f`), pending independent review.
Report: `task-B-report.md`. No Task C candidate or benchmark edits were made.

TDD: deferred effect revocation first failed 5/5, then passed 5/5; abort exhaustion first failed
with 1 unchanged passing stage-two control, then passed 2/2; newly discovered ambient-history
retention first failed 4/4, then passed 4/4. Smallest corrections record lease-owned deferred
cancellation outside the monitor, check abort generation before mutating authority, and skip
released ambient predecessors during request/work acquisition and current-head disposal.

Characterization: 79 added cases. Focused clusters passed 5, 2, 20 (including retention), 28, and
24 cases. Final full gate/interceptor/request-scope slice passed 195/195 without skips. Fresh
`--no-restore --no-incremental` solution build passed with 0 warnings/errors; scoped dotnet format,
repository blank-line verification, and diff checks passed. H benchmark/build/dependency/native
inputs are byte-identical; eager request/work terminal tasks and the dormant generation signal
remain unchanged. Proposed B is a normal implementation commit; this ledger/report is separate
bookkeeping. Only independent review can establish the accepted exact B before Task C.

### Task B independent-review fix round 1

The original proposed B `18c7a2b2f779dd4a87a1d45c1a1e61fa924200ff` is superseded. Review
found one Important omission: an unpromoted old-generation request surviving a proven abort could
be promoted out of the reopened generation's census. The real G1 timeout/abort-to-G2 regression
first failed compile-clean because stale promotion returned success (1 failed, 0 passed), then
passed 1/1 after the sole production correction added the current-generation check before any
promotion or closure mutation. It also pins unchanged Ordinary admission, exact/foreign connection
refusal during the next close, and continued census ownership until old-request disposal.

New proposed B: `b0be2b4df8855e56f2dcfcaf155dfacddc51b88a`, a normal production/test/docs
commit pending independent re-review. Affected promotion/lifetime tests passed 27/27; the complete
gate/interceptor/request-scope slice passed 196/196 with no skips; fresh nonincremental solution
build passed with 0 warnings/errors; scoped format, blank-line, diff, and immutable-H audits passed.
Task B now adds 80 cases. Full evidence is appended to `task-B-report.md`; this report/ledger
bookkeeping remains separate from exact B. No Task C optimization was introduced.

Task B: fix round 1/5 complete. The same independent reviewer verified the stale unpromoted-request
finding is addressed before any promotion, census, owner, or phase mutation; the regression pins the
full G1 timeout/abort-to-G2 path and later drain; and no new Critical, Important, Minor, scope, or H
finding remains. Exact reviewed monitor baseline B is
`b0be2b4df8855e56f2dcfcaf155dfacddc51b88a`.

## Task C controller rulings

Ruling: the H callback metric retains its exact physical-open meaning. Removing the eager ordinary
request/work terminal tasks is proved by Task C structural/allocation tests, not by renaming or
changing H. Historical-churn timing is supporting evidence; executable membership/census tests are
the formal O(fixed shards + live leases + live opens) proof. A passing comparator alone is therefore
never a complete acceptance claim.

Ruling: keep physical-open tickets and `CurrentGeneration` on `_sync` in the first candidate. Move
only request/work admission, reclaimable membership, effect serialization, terminal release, and
stage-one zero waiting. Counts and fixed shards belong to each singleton gate instance across all
its never-reused epochs; they are not static. Keep ambient ancestry separate from intrusive shard
membership so released ambient history cannot retain whole lease/CTS state.

Ruling: introduce checked request/work counts and the closure-owned zero signal while the monitor
still protects both paths, then migrate requests, then zero-publication races, then work/effects.
This avoids an unprovable hybrid census. Publishing the exact zero signal requires an Interlocked
full-fence operation before count recheck; volatile release/acquire alone cannot prove absence of a
missed zero. Close publishes `Closing` and completes the full fixed-order shard scan before deriving
drained state or exposing a waiter.

Ruling: only `_sync` may nest into one captured shard; no path may take shard then `_sync`, hold two
shards, await under either lock, or invoke callbacks under either lock. Admission rechecks exact
epoch and phase inside its shard before checked CAS reservation and exact linking. Promotion validates,
marks, binds, unlinks, and decrements under one `_sync`-to-shard transaction. Ordinary release flags
read by monitor-backed finisher checks remain atomic. Old epochs never reactivate; survivors remain
in the process-wide census across abort/reclose; successful abort/reopen computes every checked value
and replacement publication object before mutating state and publishes the fresh epoch last.

## Task C1 implementation handoff

Task C1 implemented from `adf36a59d00cf28a1eb018eaca52f0f845078d8d` as normal implementation commit
`c50be99dca742751554a8c57566c522d80e294c0`; independent C1 review is clean. Exact reviewed B remains
`b0be2b4df8855e56f2dcfcaf155dfacddc51b88a`, and immutable H remains
`f51ac3f84c3b408510e448311d7a5e15bdbc041e`. This increment is not final candidate C.

Completed: checked instance-owned request/work censuses and lazy exact closure zero signal,
never-reused epochs, fixed reclaimable request shards, request release without `_sync`, atomic
ambient liveness, exact promotion, full request-shard scan before zero, and request-applicable
publication/isolation proofs. Work membership/effects, physical opens, maintenance state, and
`CurrentGeneration` remain monitor-backed for the next increment.

Strict compile-clean RED/GREEN evidence is recorded in `task-C1-evidence.md`, and the full handoff is
`task-C1-report.md`. Fresh solution build: zero warnings/errors. Gate/interceptor/request-scope:
227/227. Transition/coordinator/request middleware/reset recovery: 474 passed, 0 failed, 3 existing
Windows-only skips. Format, blank-line, staged/unstaged diff, immutable-H, 1,641-input catalog,
production allowlist, unchanged Task B tests, and ancestry audits passed. No acceptance benchmark
was run. Report, evidence, and ledger are committed separately from the implementation.

Task C1: complete with no Critical, Important, or Minor review findings. Independent review verified
the epoch/phase recheck, completed shard scan, `_sync`-to-shard promotion, exact CAS census/unlink,
atomic ambient liveness, full-fence signal publication, decrement-before-signal load, abort/reopen
isolation, hybrid monitor-work boundary, deterministic proof quality, and exact H/allowlist scope.
Work/effect sharding remains Task C2; the candidate is not yet final and has not been benchmarked.

## Task C2 implementation handoff

Task C2 implemented from clean start `68305e4157677194523c4950444347889e828510` as normal
implementation commit `09da72dad77dc5b829f688f33ed6b8562621aa8d`. Scoped C2 review and final
whole-candidate review are pending. This SHA is not yet an accepted benchmark candidate.

Completed: work admission and exact intrusive membership on the reviewed fixed shards; captured
epoch/shard scope and effect synchronization; unified full-shard close scan; pending revocation
through abort/reclose; terminal unlink/decrement before outside-shard zero signaling and callbacks;
and the complete request/work description in DESIGN. The physical-open path, cold maintenance
state, CurrentGeneration, public contracts, producers, H, and all benchmark/build/native inputs
remain unchanged. No C1 production assumption required correction.

Strict TDD captured 3 structural, 20 hot-path/frontier, 6 epoch/callback/ambient, and 1 compiled
signal-order RED failures before production changed. The coherent migration passed all 30 focused
cases and the then-complete 256-case slice. Further passing characterization added mixed internal
zero windows, real gate-object reachability after churn, 2,048 seeded model actions plus maintenance
orders, and exact sibling/successor disposal. C2 adds 42 tests and strengthens the candidate-only C1
compiled zero-order proof; every original B test stays byte-identical.

Final qualification: fresh nonincremental solution and benchmark-host builds each had 0 warnings
and errors; gate/interceptor/request-scope 269 passed; transition/coordinator/request/reset slice
474 passed with 3 existing Windows skips; benchmark ordinary/packaging contracts 119 passed.
Format, blank-line, staged/unstaged diff, immutable H, 1,641 catalog inputs, exact production
allowlist, B-test identity, candidate-only inventory, and ancestry audits passed. The two final
staged EOF blank-line corrections changed no executable statements. No acceptance pairs were run.

Full evidence and self-review are in `task-C2-evidence.md` and `task-C2-report.md`, committed with
this ledger separately from the implementation. Exact H remains
`f51ac3f84c3b408510e448311d7a5e15bdbc041e`; reviewed B remains
`b0be2b4df8855e56f2dcfcaf155dfacddc51b88a`.
