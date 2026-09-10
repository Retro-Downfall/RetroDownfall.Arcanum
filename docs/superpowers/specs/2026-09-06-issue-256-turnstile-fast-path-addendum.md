# Issue #256 Turnstile Fast-Path and Bounded Reopen Addendum

**Status:** Approved on 2026-09-06 at the explicit issue #256 design checkpoint.

**Applies to:**
`2026-09-06-issue-256-hosted-producer-admission-design.md` and the parent issue #239
admission design. This addendum refines ordinary-path performance and reopen behavior; every parent
generation, promotion, drain, effect-frontier, connection-open, `KeepClosed`, and owner-bound
maintenance invariant remains authoritative.

## 1. Decision

The Grimoire admission gate is a turnstile, not a job scheduler.

During ordinary operation, request, work, and physical-open admission must pay only the minimum
process-local synchronization that makes entry linearizable against maintenance. They do not await a
permission service, touch SQLite to ask permission, join a fairness queue, or allocate maintenance
waiter state.

Maintenance closes the turnstile, revokes eligible work, and pays the cost of census, cancellation,
drain, connection fencing, and reopen. A correctness-preserving hot-path optimization ships only when
a controlled benchmark proves a material benefit without a single-thread, allocation, tail-latency,
or representative EF regression. Otherwise the reviewed monitor implementation remains.

## 2. Reopen owns no central queue

The gate retains one dormant next-open-generation signal. It does not add an always-present FIFO, a
`ReopeningControlled` phase, cross-kind priorities, a copied capacity catalog, or a central producer
scheduler.

Retained work satisfies exactly one producer-local contract:

1. **Slot-retaining:** the tracked task keeps the producer's already-counted concurrency slot while
   disposing every Grimoire lease, scope, iterator, stream, provider handle, and filesystem handle;
   it then awaits the dormant generation signal and retries the same identity.
2. **Queue-returning:** the task atomically restores the exact identity to that producer's existing
   bounded queue/dedup state and exits. This is valid only when the queue or cadence cannot make the
   identity runnable again while admission remains closed. A later bounded consumer owns the retry.

An active channel that would immediately dequeue the restored item is not a queue-returning owner: its
single producer-owned pump retains one generation waiter instead. No closed-generation path may
dequeue, fail admission, requeue, and repeat.

Only slot-retaining work becomes a generation waiter. Work behind occupied slots remains a durable
record or producer-queue item and is not awakened. Periodic passes with no retained identity return to
their ordinary cadence and do not wait. Therefore a reopen cohort cannot exceed concurrency that the
same producer already enforces during ordinary operation.

The zero-, one-, and two-waiter cases create no gate scheduler. A synthetic backlog must prove that
only the configured producer slots resume while the remainder stays in the producer queue. If future
telemetry proves that the sum of valid producer capacities overloads one shared resource, that future
change must first introduce a real background-capacity authority applied during ordinary operation
as well as reopen. A reopen-only magic threshold would hide the same overload during an ordinary
burst and is forbidden.

The EF `DbContext` pool size is cache retention, not a concurrency semaphore. CPU count, ThreadPool
availability, SQLite busy state, and waiter count are likewise not truthful cross-producer capacity
authorities.

## 3. Producer ownership requirements

Every issue #256 producer test proves its actual resumption form and capacity owner.

- Batch keeps each claimed in-flight batch inside `MaxConcurrentBatches`; queued database rows do
  not become tasks. One maintenance effect frontier remains exactly one existing 64-line accounting
  page, with a separate artifact-publication frontier.
- Apprentice keeps the exact active execution generation and its `ApprenticeConcurrencyGate` slot.
- Unseen Servant keeps only claimed jobs within its configured running-job bound; other jobs remain
  durable.
- Long-running recovery keeps only its bounded reconciler workers; undispatched rows remain durable.
- Single-consumer channels and loops have at most one waiting consumer for their retained lane.
- Workspace indexing receives an honest process-wide top-level bound in addition to its per-workspace
  deduplication; an unbounded set of detached on-demand tasks is forbidden.

The tracked owner observes every waiter during shutdown. `KeepClosed` creates no duplicate waiter,
and an immediate second close either counts the awakened unit or refuses it while the exact identity
remains owned.

## 4. Conditional ordinary-path optimization

The cold maintenance authority state machine remains under its existing `_sync` monitor. Owner
selection, initiating-request promotion, stage transitions, maintenance I/O lanes, one-shot
authorities, abort, closed disposition, and reopen are deliberately rare and are not rewritten for
novelty. `CurrentGeneration` leaves that monitor only if the selected candidate can prove the same
coherent epoch/generation observation; it is measured explicitly rather than changed incidentally.

If the benchmark justifies a hot-path change, use a never-reused `AdmissionEpoch` reference with only
`Ordinary`, `Closing`, and `Closed` phases, process-wide request/work/open censuses, and reclaimable
fixed shards for live request/work leases. Admission locks one shard, rechecks the exact epoch and
ordinary phase, publishes the lease and census entry, then exits. Disposal removes the exact node
from its captured shard and decrements exactly once. No append-only registry, pooled identity, hazard
pointer, or lock-free reclamation protocol is permitted.

Maintenance publishes `Closing` under the cold lock and scans shards in one fixed lock order. Effect
start and maintenance scan serialize on the owning work shard, preserving the binary rule: either
the group wins and maintenance drains it through durable disposition, or maintenance wins and no
effect begins. Revocation callbacks run only after locks are released.

Per-lease terminal tasks may be replaced by one exact closure-level zero waiter only with the
publish-then-recheck protocol: install the waiter, re-read the census, and let the exact decrement to
zero complete that closure's waiter. Abort/retry cannot reuse a prior closure's signal.

Physical-open tickets may use a checked process-wide unresolved-open census and exact atomic terminal
state instead of a live set. Post-native-open revalidation and final open publication must still
compare epoch, generation, and phase, and any loser must physically close before terminal release.
The exact ambient finisher and initiating-request promotion rules remain unchanged.

Process-wide counts deliberately include a revoked old lease that survives a proven abort, so a later
maintenance close still sees it. Numeric generation and counters fail closed on exhaustion; epoch
objects and lease identities are never reused.

## 5. Backup, reset, restore, and schema work

No second SQLite backup algorithm is introduced.

- Direct CLI backup already excludes a serving host with the installation maintenance lock and
  client-mutation boundary. It keeps the existing SQLite online-backup API and Covenant installation
  read lease. Issue #256 proves this stopped-host caller chain; it does not add an unused gate mode.
- A future in-host online snapshot is ordinary admitted finite/work activity and may coexist with
  SQLite writers. Its complete snapshot/archive unit and Covenant read lease must finish before an
  exclusive replacement can drain.
- Direct restore remains a stopped-host installation-lock workflow, including its owner-bound safety
  snapshot; it does not enter the live host's Grimoire two-stage close.
- Covenant reset and healthy-catalog erasure retain the existing authenticated journal, closed
  `CovenantExclusiveOperation`, Covenant lease, two-stage Grimoire close, and owner-bound maintenance
  lane.
- The current schema-transition hosted service performs bounded online backfill and is ordinary
  DB-only work. A genuinely incompatible/offline migration uses the existing `SchemaRepair`
  operation identity and full exclusive transition protocol.

`CovenantExclusiveRecoveryOwner` already binds operation id, closed persisted operation kind, and
effect digest, and the only production Grimoire-close caller is the verified coordinator chain.
Issue #256 enforces that topology with executable bidirectional inventory rather than duplicating the
operation vocabulary in speculative wrapper hierarchies.

## 6. Acceptance

Correctness tests use deterministic barriers, never sleeps, and cover admission versus close,
promotion, ambient lifetime flow, stale leases across abort/reclose, both effect-frontier outcomes,
both group/scope disposal orders, missed-zero prevention, native-open races, `KeepClosed`, host
cancellation, exact identity retention, and producer-capacity reopen bounds.

A dedicated Release Native AOT benchmark has three exact revisions. `H` adds and smoke-tests the
instrument without changing the gate. Task B then characterizes and, if necessary, corrects the
current monitor; its reviewed tip is `B`, the final unchanged-monitor baseline. `C` is the optional
hot-path candidate and must descend from `B`. Only counterbalanced `B`/`C` runs are decision evidence;
early `H` measurements are calibration, because a Task B correctness fix must never be reported as
an optimization gain or regression.

The separate harness uses persistent workers and measures request, work, effect, physical-open
ticket, nested request/open, one predeclared mixed contested operation, and a representative pooled
EF operation at concurrency 1, 2, 8, and logical-processor count. Warmup, bundled latency sampling,
uninstrumented throughput/allocation/Gen0/contention measurement, and final invariant/leak validation
are separate phases. Worker/task/barrier creation and logging stay outside measured windows. Timing
assertions do not enter xUnit.

The orchestrator refuses an experiment unless `B` and `C` are exact clean commits in one measurement
session, `B` is an ancestor of `C`, and all harness, manifest, comparison, project/build, dependency,
toolchain, environment, and native-SQLCipher identities match. Compiled-source differences are an
exact checked-in admission-file allowlist, never a production glob. Results bind the two SHAs,
session and pair order, workload/harness/dependency/native digests, RID and process/OS architecture,
OS/runtime/SDK, CPU identity and logical count, stopwatch frequency, and GC/dynamic-code state.
Missing, duplicate, non-finite, mismatched, crashed, or unmeasurable evidence fails closed.

The optimization ships only when checked-in deterministic comparison arithmetic applied to at least
five independent process pairs shows every requirement below. Qualification uses six pairs in the
counterbalanced order `B,C`; `C,B`; `B,C`; `C,B`; `B,C`; `C,B`, and bootstraps pair-level results
rather than treating samples from one process as independent evidence.

- no single-thread p50 regression beyond 5%;
- no p99 regression beyond 10%;
- the representative end-to-end EF confidence interval stays inside the checked-in non-regression
  bound;
- no direct-gate bytes/op increase, no representative EF allocation regression beyond 5%, and no
  no-maintenance terminal-waiter allocation;
- both the observed throughput ratio and the deterministic one-sided 95% paired-bootstrap lower
  confidence bound are at least 1.20 in the predeclared `ordinary.mixed` logical-processor cell; and
- maintenance work remains O(live leases + live opens), never O(all historical admissions).

Zero allocation, Gen0, or contention values are compared without division. Monitor contention remains
diagnostic rather than a gate when the two implementations use different synchronization primitives.
The dedicated host is Native AOT-published and run directly; it fails when dynamic code is supported,
and CI executes a short native smoke covering concurrency, open tickets, pooled EF, and final
maintenance drain. A valid rejected candidate exits 1, invalid evidence exits 2, cancellation exits
130, and accepted comparison or valid smoke exits 0.

Candidate source is reviewed and committed as exact `C` before measurement. If every condition is
met, `C` is retained. If any condition is not met, a reviewed restoration commit removes the
candidate production implementation and candidate-only tests while preserving the benchmark,
characterization tests, and bound rejection evidence; the restored production gate must be
byte-identical to `B`. Retaining the current monitor is then the required result.
