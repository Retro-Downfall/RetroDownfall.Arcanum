# SDD ledger — plan: docs/superpowers/plans/2026-09-06-issue-256-turnstile-fast-path-addendum.md

Spec: docs/superpowers/specs/2026-09-06-issue-256-turnstile-fast-path-addendum.md
Worktree: /Users/mat/Source/apps/RetroDownfall.Arcanum/.worktrees/issue-256-hosted-producer-admission
Plan/spec approval commit: a5a5d230
Main plan: docs/superpowers/plans/2026-09-06-issue-256-hosted-producer-admission.md

## Task todo

- [x] Task A: Add the admission benchmark without changing production
- [ ] Task B: Characterize all current gate invariants
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
