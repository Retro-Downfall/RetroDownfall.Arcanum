# Task C1 implementation and qualification report

Completed for independent C1 review on 2026-09-07. This is not final candidate C and no
acceptance benchmark was run.

- Exact start: `adf36a59d00cf28a1eb018eaca52f0f845078d8d`.
- Immutable H: `f51ac3f84c3b408510e448311d7a5e15bdbc041e`.
- Reviewed monitor B: `b0be2b4df8855e56f2dcfcaf155dfacddc51b88a`.
- C1 implementation: `c50be99dca742751554a8c57566c522d80e294c0`.
- Implementation commit: `perf: introduce Grimoire request epochs and closure census`.
- Report/evidence/ledger commit follows the implementation normally; its exact SHA is recorded in
  the controller handoff rather than self-referenced in its own tree.
- Worktree: `/Users/mat/Source/apps/RetroDownfall.Arcanum/.worktrees/issue-256-hosted-producer-admission`.
- Branch: `codex/issue-256-hosted-producer-admission`.

The task brief, approved addendum, parent admission contract, complete gate, and all Task B
characterization partials were read before editing. TDD and verification skills guided the
checkpoint sequence. No subagents were spawned.

## Completed algorithm

The gate owns checked request/work `long` censuses across all of its generations. A checked CAS
loop reserves admission before any lease, membership, ambient, or output publication. Exact
membership removal decrements once. No ordinary request/work lease owns an eager Task/TCS.
Monitor-backed work still remains counted through both scope disposal and active effect release.

The gate owns sixteen fixed reference-type shards, selected by managed thread id without a
global selector write. Only request membership is sharded in C1. Requests capture immutable
epoch/shard identity, validate the exact current Ordinary epoch under their shard, reserve and
link there, and unlink/clear links/decrement there on disposal without taking `_sync`.
Ordinary ambient ancestry stays separate, prunes released prefixes, and has volatile release
visibility for the monitor-backed finisher check. Promotion validates exact current identity and
generation, marks, binds its connection, removes its node, and decrements in one `_sync` to
captured-shard transaction. Disposal cannot remove a sibling or decrement a promoted request twice.

Closing publishes the current epoch's Closing phase under `_sync` and joins every shard in fixed
order, including empty shards, holding only one at a time. Only the completed full scan plus the
still-monitor-protected work census may establish stage-one zero. Cancellation sources are
collected under locks and invoked after all locks are released. Retired epochs never reactivate;
checked generation arithmetic and replacement objects are prepared before abort/reopen mutation,
and the fresh Ordinary epoch is published last. Old unpromoted requests survive in the global
membership/census and cannot be promoted across generations.

One exact closure owns its lazy zero completion source. The real drain's publication suffix
uses `Interlocked.Exchange` before count recheck. Every terminal release decrements before loading
the current signal, rechecks both counts, and completes that exact signal at zero. This is the
full-fence publish/recheck protocol; volatile publication alone would be insufficient. Waiter
cancellation/timeouts never cancel shared state. Abort/reclose clears the published reference and
never reuses the prior closure's signal. Final success revalidates owner, closure, phase, and zero
under `_sync`. Signals are exact object identities, with no numeric signal sequence to exhaust;
numeric generation and census exhaustion fail before publishing mutations.

The owning DESIGN describes exactly this hybrid implementation and its pending review/performance
boundary. No public interface, timeout, producer, schema, configuration, benchmark, scheduler,
physical-open callback counter, or build/dependency/native input changed.

## RED / GREEN evidence

Detailed commands, inspected failures, and results are in `task-C1-evidence.md`.

| Checkpoint | RED | GREEN |
|---|---|---|
| Monitor-backed census and lazy zero | Six structural failures for eager lease TCS and absent census/signal state; allocation-only RED 2/2 at 328/336 bytes per warmed request/work cycle against a 280-byte ceiling | Full gate/interceptor/request-scope 204/204 |
| Epoch and request shards | Eight compile-clean failures: absent epoch/membership and real request acquire/release blocked by a held cold monitor | Full slice 212/212 |
| Closure identity, last release, callback locks, compiled fences | Nine added passing characterizations of the implemented protocol; no fabricated defect | Focused 9/9 |
| Internal zero-publication suffix | Three structural failures for the absent private production suffix before an unchanged extraction; a refactoring proof, not a behavior defect | Full slice 224/224 |
| Strong blocked-shard capture and overflow contention | Passing characterizations, no additional production change | Focused blocking slice 3/3; final full slice 227/227 |

The initial allocation ceiling was too loose and passed; it was tightened and observed failing
before production changed. The initial request-shard test draft had nullable Result compiler
errors, corrected before its inspected RED. Self-review corrected the overflow test's `out`
observation to pass the prepopulated variable directly into the real call. No result relies on a
compiler error, stale binary, sleep, or a task still running unobserved after cleanup.

Tests exercise real gates, private executable state through test-only reflection, actual shard
monitors, dedicated-thread barriers, and actual monitor-blocked state. Churn proves exact live
survivors, clear removed links, and an empty census without using GC timing. The internal zero
window invokes the same private production suffix as the real drain, under the actual cold
monitor, after a nonzero census observation; ordered/concurrent final release proves both sides
of publication. The compiled IL proof additionally pins full-fence publication before recheck,
decrement before signal load, and compare-exchange reservation. These are structural/local
allocation guards, not benchmark decision evidence.

## Fresh qualification

- Full solution build with `--no-restore --no-incremental --disable-build-servers -m:1`:
  succeeded, 0 warnings, 0 errors, 00:01:11.34, exit 0.
- Complete gate/interceptor/request-scope suite against those fresh binaries:
  227 passed, 0 failed, 0 skipped, exit 0. This preserves all 196 reviewed B cases and adds 31.
- Offline transition, erasure coordinator, HTTP request admission, reset recovery, and reset
  bootstrap barrier slice: 474 passed, 0 failed, 3 existing Windows-only journal skips on macOS,
  477 total, exit 0.
- Scoped `dotnet format --verify-no-changes --no-restore`: exit 0, no diagnostics or changes.
- Repository blank-line check: `Would change 0 file(s)`, exit 0.
- Unstaged and staged `git diff --check`: exit 0.
- Immutable-H inputs, exact production allowlist, H/B/C1 ancestry, and unchanged Task B test
  files: all audits passed. Of 1,641 catalog inputs, only the allowed gate source differs.

No six-pair B/C benchmark, full repository test suite, coverage run, native AOT qualification,
or native SQLCipher qualification is claimed for this increment.

## Self-review and remaining boundary

- Memory ordering: epoch phases and zero publication use Interlocked fences; fresh epoch references
  publish with Volatile.Write after all cold state is ready; ambient release uses Volatile access.
  Final-zero signals are loaded after decrement and owner validation is repeated after awaiting.
- Lock order: only `_sync` to one request shard nests. Request release never takes `_sync`.
  Every scan releases a shard before taking the next. Awaiting and consumer cancellation are
  outside all held monitors.
- Identity/overflow: no epoch/lease/signal reuse, checked CAS reservation, immutable captured
  request identity, exact intrusive unlink, promotion guard before mutation, checked abort/stage-two
  generation, and all replacement publication objects prepared before reopening authority.
- Reclamation: membership is removable and links are cleared; ambient ancestors carry no lease,
  intrusive links, effect, or CTS state. No append-only registry, static census, or pooling exists.
- Hybrid census: work remains monitor-owned, including its existing effect frontier and deferred
  revocation obligation. C1 changes only its terminal accounting. Physical opens, maintenance
  authorities, stages/dispositions, dormant generation waiting, and `CurrentGeneration` stay cold.
- Native AOT and measurement: no production reflection or new compiled file outside the allowlist;
  all instrumentation lives in tests. H, its callback meaning, and its acceptance arithmetic remain
  untouched. Local allocation guards do not establish benchmark acceptance.

No unresolved implementation defect is known from self-review. The worktree was clean after the
implementation commit, and report/evidence/ledger bookkeeping is committed separately. Independent
C1 review is required next; only afterward may C2 migrate work membership/effects and run its own
reviewed qualification. Final candidate C and acceptance measurement remain later work.
