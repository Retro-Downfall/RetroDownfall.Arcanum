# SDD ledger — plan: docs/superpowers/plans/2026-09-06-issue-256-turnstile-fast-path-addendum.md

Spec: docs/superpowers/specs/2026-09-06-issue-256-turnstile-fast-path-addendum.md
Worktree: /Users/mat/Source/apps/RetroDownfall.Arcanum/.worktrees/issue-256-hosted-producer-admission
Plan/spec approval commit: a5a5d230
Main plan: docs/superpowers/plans/2026-09-06-issue-256-hosted-producer-admission.md

## Task todo

- [ ] Task A: Add the admission benchmark without changing production
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
Task A implementation: architecture-breaker remediation in progress (base 671a7c89; implementer /root/task2_local_publication; strict TDD; no production gate or Covenant benchmark edits)
Task A preflight review (3 Important requirements gaps corrected before implementation: temp-home deletion ownership, process-wide counter boundary, and host-only AOT JSON roots)
Task A preflight rereview: approved (0 Critical/Important/Minor; implementation may proceed)

Ruling: The benchmark executable exclusively creates and owns a nonce-marked child under a fresh system-temporary parent, refuses a pre-set `ARCANUM_TEST_HOME`, and deletes only after exact canonical parent/prefix/non-link/marker validation; the script never deletes the database home — this makes direct invocation and qualification safe from user-path deletion — if wrong, cleanup fails closed and leaves an isolated temp directory rather than risking user data.

Ruling: Persistent workers use a lock-free armed/run/loop-complete/parked protocol so controller process-wide Gen0/contention reads occur strictly after arming and before any operation, then after every loop terminal and before monitored wakeup; no assumed harness subtraction — this makes the process metrics belong to the measured phase — if wrong, those metrics are invalid evidence and comparison exits 2.

Ruling: Native `--smoke` executes the real host `AdmissionBenchmarkJsonContext` against the embedded manifest, revision run, full six-pair bundle, and accepted/rejected reports, while ordinary tests pin exact pure-source links — this prevents an immutable H that fails only when qualification serializes an AOT-only payload — if wrong, the native smoke fails closed before H review.

## Task A independent whole-review remediation

Ruling: `659a3441d90aa25d5050cf8dd561ed3d88816edc` and its calibration are invalid and superseded, not H evidence. Independent review verified that the instrument ceiling-expanded non-divisible totals, replayed synthetic transition order, busy-spun the controller and completed workers, bracketed terminal callbacks only around throughput, truncated allocation ratios, excluded EF from the universal p99 gate, accepted incomplete/malformed input identity and derived math, had no linked watchdog, hard-coded live waiters to zero, accumulated historical churn, leaked direct connections, did not execute a real link attack, rewrote qualification cancellation to exit 2, and could hash empty toolchain output successfully.

Ruling: the prior no-event boundary is itself the measurement architecture-breaker. The corrected harness uses one maximum-size persistent worker set and precreated/warmed OS-backed wait handles. Arming precedes the process baseline; volatile run publication precedes actual stored worker start stamps; workers record allocation/timestamp terminals and actual completion stamps before blocking; the last completer emits exactly one completion signal; the blocked controller records process terminals and emits one shared release; workers then record actual parked stamps. Primary timing/allocation excludes handle calls. Process Gen0/contention includes the fixed nonallocating/non-`Monitor` rendezvous and remains diagnostic only.

Ruling: corrected identity uses a checked-in path/optionality-only catalog plus a manifest-pinned framed shape digest, immutable-content digest outside the two-file candidate allowlist, and complete sorted per-path presence/content digests. Runtime, xUnit tracked-file enumeration, and qualification `git ls-tree` derive the closure independently, including every embedded schema SQL file, so symmetric omission cannot pass.

Ruling: remediation requires a new normal proposed-H commit, new Native AOT smoke, and fresh calibration. No old calibration value, count, or artifact hash may be cited as valid.
