# Issue #256 Turnstile Fast-Path Addendum Plan

> Execute before Tasks 3-13 of the main issue #256 plan. Use strict RED -> GREEN -> REFACTOR and a
> fresh review before producer migration.

**Goal:** Prove bounded, queue-free producer resumption and decide by controlled measurement whether
the ordinary Grimoire admission monitor should be replaced by a reclaimable sharded epoch census.

**Spec:**
`docs/superpowers/specs/2026-09-06-issue-256-turnstile-fast-path-addendum.md`

## Task A: Add the admission benchmark without changing production

- [ ] Add a separate outside-solution Native AOT Grimoire-admission host, manifest, source-generated
  JSON context, result contracts, persistent-worker harness, and deterministic comparator. Do not
  edit or reuse the Covenant benchmark host, manifest, baseline, comparison arithmetic, or script.
- [ ] Measure finite and quiesceable request acquire/dispose, DB-only work acquire/dispose, work plus
  effect-group lifetime, physical-open ticket acquire/fail/dispose, nested request/open, and one
  representative pooled EF scope/open/query/dispose case. Measure `CurrentGeneration` explicitly.
- [ ] Predeclare `ordinary.mixed` at logical-processor concurrency as the material-improvement cell.
  Run concurrency 1, 2, 8, and logical-processor count; record p50/p95/p99, throughput, bytes/op,
  Gen0, and diagnostic contention in separate latency and uninstrumented measurement phases. Pin
  per-operation bundle and iteration counts. Keep timing checks outside xUnit.
- [ ] Pin workload/schema/digests, exact compiled-difference allowlist, environment/session matching,
  zero-metric handling, comparison arithmetic, exit codes, and leak/drain validation in ordinary
  tests.
- [ ] Pin that the dedicated verification script publishes and runs the benchmark as Native AOT;
  the process fails closed at runtime when dynamic code is supported, and CI runs a short native
  concurrency/open-ticket/pooled-EF/drain smoke rather than merely compiling the host.
- [ ] Commit the benchmark harness independently so the exact same harness can run against both gate
  implementations; record that exact commit as `H`.
- [ ] Smoke and calibrate the committed harness at `H`; do not call those measurements baseline or
  decision evidence.

## Task B: Characterize all current gate invariants

- [ ] Add deterministic request/work/open admission-versus-close tests.
- [ ] Pin exact request promotion, nested/non-LIFO/flowed `AsyncLocal` lifetime behavior, stale revoked
  work across proven abort and later close, checked exhaustion, and exactly-once disposal.
- [ ] Pin both effect-frontier winners, group-first/scope-first disposal, sequential groups, callback
  failures outside locks, and revoke-before-next-group during closing.
- [ ] Pin native-open races, exact finisher authority, mandatory physical close after a lost
  generation, and publish-then-recheck zero-waiter behavior.
- [ ] If a characterization exposes a current invariant defect, observe its RED, implement the
  smallest correction, rerun the affected cluster, and obtain fresh review before rebaselining. Do
  not defer a correctness defect to the optional performance candidate.
- [ ] Confirm the complete gate suite is GREEN before any optional hot-path candidate changes.
- [ ] Record this reviewed unchanged-monitor tip as exact baseline `B`; `B` must descend from harness
  commit `H`, and no earlier measurement is acceptance evidence.

## Task C: Implement the benchmark-gated hot path, if justified

- [ ] Write RED structural/allocation tests for no append-only history, reclaimable shard membership,
  no ordinary terminal waiter, and exact process-wide census.
- [ ] Add a never-reused three-phase `AdmissionEpoch`, fixed reclaimable request/work shards, checked
  process-wide censuses, and closure-owned zero signals. Keep closed-period ownership under `_sync`.
- [ ] Move request admission/release first; run the request/gate slice and review.
- [ ] Move work admission, selective revocation, effect start/release, and terminal census; run both
  race orderings and disposal permutations and review.
- [ ] Move physical-open tickets to exact atomic terminal state and unresolved-open census only if the
  benchmark identifies that path as material; preserve post-open refusal and drain ordering.
- [ ] Run the full gate/interceptor/request/transition slices plus state-machine stress/model tests.
- [ ] Obtain fresh spec and quality review, then commit the provisional candidate as exact clean
  commit `C` before any acceptance measurement.
- [ ] Define candidate `C` as a descendant of `B`. The runner requires clean exact commits, ancestor
  relation, identical benchmark/build/dependency/native inputs, and only the checked-in admission
  source allowlist changed in the compiled tree.
- [ ] Run six independent Native AOT process pairs in counterbalanced `B,C`; `C,B`; `B,C`; `C,B`;
  `B,C`; `C,B` order on one bound measurement session, and bootstrap pair-level results. If any
  acceptance threshold fails, create and review a restoration commit that removes candidate
  production/candidate-only tests, leaves the production gate byte-identical to `B`, and retains the
  benchmark, characterization tests, and bound rejection evidence.
- [ ] If accepted, retain `C` and commit only bound evidence/documentation; if rejected, retain the
  reviewed restoration and its bound evidence. Rerun exact-tip qualification after either path.

## Task D: Prove queue-free bounded reopen in every producer task

Tasks 3-13 keep their existing order and production ownership. In each task:

- [ ] identify the producer's real concurrency owner and whether resumption is slot-retaining or
  queue-returning;
- [ ] acquire the work lease before the first owned scope and release all resources before waiting;
- [ ] ensure queued identities behind occupied slots never create generation waiters;
- [ ] prove zero/one/two waiters create no gate scheduler and resume once;
- [ ] prove a 10x backlog resumes no more than the producer's configured slots;
- [ ] prove a queue-returning identity cannot become runnable again while admission remains closed;
  an active channel instead retains exactly one producer-owned generation waiter and never
  dequeue/refuse/requeue spins;
- [ ] prove `KeepClosed`, immediate reclose, cancellation, and shutdown preserve exact identity with
  no duplicate waiter, attempt, billing, watermark, or checkpoint;
- [ ] add an honest process-wide top-level limit to Workspace indexing while preserving per-workspace
  deduplication; and
- [ ] keep Batch at exactly one external-effect frontier per existing 64-line accounting page plus
  one artifact-publication frontier.

## Task E: Prove maintenance classification

- [ ] In Task 14, encode the direct CLI backup chain as exact stopped-host executable evidence:
  command -> CLI exclusive initialization -> installation lock/client-mutation boundary -> durable
  backup/Covenant read lease -> SQLite snapshot/archive completion.
- [ ] Prove the reverse caller set of `IBackupService.CreateAsync` and reject a new unadmitted hosted
  caller.
- [ ] Prove current direct restore remains in the stopped-host installation-lock workflow and that
  its safety snapshot derives from that exact owner.
- [ ] Prove current in-host reset/healthy-catalog erasure reaches `BeginOrResumeExclusive` only
  through the exact coordinator authority chain and existing closed operation identity.
- [ ] Specify a future incompatible/offline schema migration as requiring the existing
  `SchemaRepair` owner/journal protocol; do not claim a current production reverse caller exists.
- [ ] Prove the current online schema backfill is ordinary DB-only hosted work.
- [ ] Add no gate-owned queue, fourth phase, queue threshold/configuration, copied producer capacities,
  or speculative maintenance capability hierarchy.

## Qualification

After every task-specific review is clean, run the main issue #256 final-review and exact-tip
qualification workflow. The final report must state whether the hot-path candidate shipped or was
correctly rejected by measurement, and must report base/candidate benchmark evidence alongside the
required build, test, coverage, AOT, and native SQLCipher checks.
