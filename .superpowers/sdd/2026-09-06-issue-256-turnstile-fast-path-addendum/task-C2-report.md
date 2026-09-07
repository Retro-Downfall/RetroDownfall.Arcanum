# Task C2 implementation and qualification report

Completed for independent C2 review on 2026-09-07. Scoped C2 review and final whole-candidate review
must both be clean before this implementation can become the proposed exact benchmark candidate C.
No acceptance pairs were run and no performance acceptance is claimed.

- Exact clean start: `68305e4157677194523c4950444347889e828510`.
- Immutable H: `f51ac3f84c3b408510e448311d7a5e15bdbc041e`.
- Reviewed monitor B: `b0be2b4df8855e56f2dcfcaf155dfacddc51b88a`.
- Reviewed C1 implementation: `c50be99dca742751554a8c57566c522d80e294c0`.
- C2 implementation: `09da72dad77dc5b829f688f33ed6b8562621aa8d`.
- Normal commit: `perf: migrate Grimoire work lifetimes to admission shards`.
- Worktree: `/Users/mat/Source/apps/RetroDownfall.Arcanum/.worktrees/issue-256-hosted-producer-admission`.
- Branch: `codex/issue-256-hosted-producer-admission`.

This report/evidence/ledger commit follows the implementation; its exact SHA is supplied in the
controller handoff rather than self-referenced in its own tree. TDD and verification skills guided
the work. The approved addendum/plan, parent gate contract, task briefs, reviewed reports, complete
gate, and B/C1 test coverage were read before implementation. No subagents were spawned.

## Complete work algorithm

Work leases now capture the current epoch and one of the already reviewed, fixed gate-owned
shards. Under that shard they revalidate the exact epoch and Ordinary phase, checked-reserve the
work census, link an exact intrusive member, and publish the separate ambient lifetime. The old
monitor-owned work HashSet is removed. Work generation comes from its immutable captured epoch;
the gate reference comes from its separate immutable ambient identity.

Scope release atomically marks ambient death before waiting for the captured shard. Under the
shard it records scope disposal and unlinks only when no active effect remains. Effect start uses
that same shard, exact current epoch, live linked identity, no cancellation, and an empty group
slot. Exact group release clears only its own active identity, captures pending cancellation, and
performs terminal unlink/decrement when the scope is already disposed. Removed links are cleared.
Zero signaling and callbacks occur after shard exit, with terminal accounting and signal completion
before cancellation. A throwing callback cannot prevent drain.

Closing still publishes its phase under `_sync` and joins every shard in fixed order. Each shard
now contributes both its live request and work lists to the same full scan. Active effect winners
retain a pending revocation obligation; work without an active group contributes an immediate
cancellation source. Only completion of the full scan permits stage-one zero. Old work survivors
stay linked/count globally across abort and reclose, keep pending revocation, and cannot start a
new effect in another epoch. Reopening never resets their membership or census.

C1 request admission/disposal/promotion, atomic ambient ancestry, epoch publication, and full-fence
closure-zero publication remain unchanged. Physical opens, `CurrentGeneration`, maintenance lanes,
owner selection, stage two, abort/disposition, `KeepClosed`, and the dormant generation signal remain
under `_sync`. `MaterializedTerminalCallbacks` retains exactly its physical-open meaning.

The owning DESIGN now describes the complete request/work algorithm and the pending independent
review/performance decision. It makes no acceptance claim.

## Evidence

Full commands and inspected assertions are in `task-C2-evidence.md`.

| Checkpoint | RED | GREEN |
|---|---|---|
| Work membership, churn, independent census, overflow | 3 compile-clean structural failures on absent work membership/links | Included in 30/30 focused GREEN |
| Four cold-monitor-independent work operations; sixteen work-kind shard frontiers | 20 failures: four expected held-monitor timeouts, sixteen missing shard identities | Included in 30/30 focused GREEN |
| Captured admission epoch, callback reentry/abort, ambient death before unlink | 6 compile-clean structural failures on absent captured shard identity | Included in 30/30 focused GREEN |
| Compiled terminal-before-signal, outside-shard order | 1 compile-clean failure before outside-lock work signaling existed | Included in 30/30 focused GREEN |
| Complete B/C1 gate slice after coherent migration | No fabricated additional RED | 256/256 |
| Mixed internal zero windows, bounded model, graph reclamation | Passing characterization; no new production edit | 12/12 |
| Exact scope/successor-group unlink and repeated disposal | Passing characterization; no new production edit | 3/3 |

The 42 new C2 cases preserve all 196 B and 31 C1 cases. Churn traverses real gate-owned membership
and object reachability after 4,000 admissions per zero/few-survivor case. The model exercises 2,048
seeded ordinary actions plus controlled maintenance sequences. Actual monitors and prestarted
LongRunning participants force concurrency boundaries. GC timing, sleeps, source-text assertions,
public diagnostics, and production test seams do not supply the proof.

Self-review fixed the test-only probe handshake to dispose the probe before publishing its shard;
final qualification includes that correction. Staged diff verification caught and removed two
trailing blank EOF lines after qualification; no executable code changed afterward.

## Qualification

- Fresh nonincremental solution build: 0 warnings/errors, exit 0, 00:01:04.71.
- Fresh nonincremental unchanged benchmark-host build: 0 warnings/errors, exit 0, 00:00:17.64.
- Final gate/interceptor/request-scope suite: 269 passed, 0 failed, 0 skipped.
- Transition/coordinator/request middleware/reset slice: 474 passed, 0 failed, 3 existing
  Windows-only journal skips on macOS.
- Unchanged benchmark ordinary/packaging contract suite: 119 passed, 0 failed, 0 skipped.
- Scoped format verification, production and changed-test blank-line checks, staged/unstaged diff
  checks, immutable H, 1,641-input catalog, exact production allowlist, B characterization identity,
  candidate-only test inventory, and ancestry audits passed.

No full repository suite, coverage, Native AOT runtime/IL qualification, native SQLCipher
qualification, or six-pair benchmark acceptance is claimed for this increment.

## Self-review

- Memory ordering: epoch identity/phase is rechecked under the captured shard, checked census
  reservation is unchanged, terminal decrement precedes the published-signal load, and the exact
  closure publication still uses the reviewed Interlocked store-load fence before count recheck.
- Lock graph: only `_sync` to one shard nests. No ordinary work operation takes `_sync`; close
  releases each shard before taking another. Every await and consumer callback is outside locks.
- Overflow/ABA: reservation precedes lease/member/ambient/output publication. Epoch, lease, and
  effect identities are never reused. Scope/group disposal uses atomic guards plus exact active
  identity; stale/duplicate disposal cannot decrement a sibling or release a successor.
- Cancellation/disposition: pending revocation survives epoch retirement and immediate reclose.
  Terminal accounting completes before consumer cancellation; sources are not disposed while
  callbacks may be captured. Old effect winners retain the original durable-disposition obligation.
- Retention: membership is removable, links clear on unlink, and the complete gate graph has only
  exact live ordinary members after churn. Ambient history stores no lease, CTS, or effect group.
- Allocation/contention: no request/work terminal waiter was reintroduced and the existing warmed
  allocation guard passes. Shard selection adds no shared write. The exact two global census CAS
  locations still incur potential cache-line contention; correctness tests cannot decide whether
  the whole candidate meets performance thresholds. The unchanged H experiment owns that decision.
- AOT/scope: all new reflection and executable-IL inspection is test-local. Only the allowlisted gate
  production source changed. No public contract, producer, schema, benchmark, build, native,
  dependency, timeout, scheduler, or configuration input changed.
- Rejection reversibility: B-relative candidate tests remain the six separate candidate-only
  partials. A later measured rejection can restore the gate byte-for-byte to B and remove those
  partials while preserving every B characterization and the bound rejection evidence.

No unresolved implementation defect is known from self-review. The implementation commit was clean
before this separate bookkeeping update. Independent C2 and whole-candidate review are next.
