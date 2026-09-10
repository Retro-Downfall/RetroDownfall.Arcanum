# Task C1 execution evidence

Start: `adf36a59d00cf28a1eb018eaca52f0f845078d8d`.
Reviewed B: `b0be2b4df8855e56f2dcfcaf155dfacddc51b88a`.
Immutable H: `f51ac3f84c3b408510e448311d7a5e15bdbc041e`.

No acceptance benchmark is run. Work membership/effect synchronization remains monitor-backed.

## Census checkpoint RED

Command prefix throughout focused compile runs:

```text
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
  --no-restore --disable-build-servers -m:1 --filter '<filter>'
  --logger 'console;verbosity=minimal'
```

First filter: `FullyQualifiedName~Ordinary_lifetime_|FullyQualifiedName~Census_exhaustion_|FullyQualifiedName~Mixed_census_|FullyQualifiedName~Cancelled_drain_call_`.
Compile-clean RED: 6 failed, 2 passed, exit 1. The two owned-object cases reached and rejected
the real eager request/work TCS. Count/mixed cases rejected absent instance census fields; the
shared waiter case rejected absent zero-signal state. Production was still byte-identical to B.

The initial 360 bytes/cycle allocation ceiling already passed, so it was tightened before production
edits to 280 bytes/cycle. Allocation-only filter `FullyQualifiedName~Ordinary_lifetime_allocation_`
then failed compile-clean 2/2, exit 1: request 328,000 and work 336,000 allocated bytes over 1,000
warmed acquire/dispose cycles. Worker creation, warmup, assertions, and reflection are outside the
measured region. This is a local structural guard, not Native AOT acceptance measurement.

The production changes following RED introduce checked CAS reservation, exact decrement on removal,
one lazy closure TCS, an Interlocked publication fence before count recheck, and remove request/work
terminal tasks. Both sets remain under `_sync` for this checkpoint.

GREEN: full gate/interceptor/request-scope filter passed 204/204, exit 0. This includes all 196
reviewed baseline tests and the eight census cases. The allocation ceiling passed after removing
the eager terminal machinery. No sharding had been introduced at this checkpoint.

## Request epoch/shard checkpoint

RED filter: `FullyQualifiedName~Request_membership_reclaims_|FullyQualifiedName~Request_admission_and_release_do_|FullyQualifiedName~Closing_publication_must_|FullyQualifiedName~Request_waiting_at_|FullyQualifiedName~Retired_epoch_|FullyQualifiedName~Promotion_and_request_disposal_`.

The first test build found a nullable `Result<T>` declaration error in the new test; that compiler
failure was corrected and discarded as TDD evidence. The subsequent compile-clean run failed
8/8, exit 1. Seven assertions rejected absent epoch/shard state. The actual held-cold-monitor
test reached its bounded wait because request admission still needed `_sync`; its monitor holder
was released in `finally`. Production epoch/shard edits followed only this inspected RED.

GREEN: full gate/interceptor/request-scope filter passed 212/212, exit 0. The captured request
shard owns exact intrusive membership, and removed links are cleared. Closing joins every shard,
including an intentionally held empty final shard, before declaring stage-one zero. Old epochs
retire permanently; fresh epochs publish last on abort/reopen. Promotion holds `_sync` then the
captured request shard. Ambient objects remain separate with volatile liveness. Work membership
and effect transitions remain on `_sync`.

## Closure identity and publication checkpoint

Nine added characterization cases passed 9/9, exit 0, with no additional production change:
pre-drain release with no waiter allocation; all three final request/work release schedules;
old-signal completion after timeout/abort/reclose; shared ambient death before blocked unlink;
request cancellation callback reentry through both monitors; and executable IL call ordering for
the full publication fence, release ordering, and checked count CAS.

To force the internal gap after the drain's nonzero observation, the exact existing publication
suffix was extracted into the private production `PublishStageOneZeroWhileLocked` helper. Three
structural RED cases first failed compile-clean at the absent helper, exit 1. This was a
refactoring proof, not a discovered behavior defect. The tests then enter the real suffix under
the real cold monitor and force release before, after, and concurrent with publication. The
full slice passed 224/224, exit 0, after extraction; the compiled fence proof follows that actual
production helper. No public API or callback-based production test hook was introduced.

Self-review strengthened the overflow test to pass a prepopulated typed output variable directly
to the production `out` parameter, so an early output mutation would be observed even on throw.
The request monitor test now observes its acquiring task in cleanup. Blocked-shard tests use an
explicit before-call handshake and the dedicated thread's actual monitor-wait state to prove the
old epoch was captured before closing/abort rather than relying on scheduling luck.

The strengthened blocking/admission filter passed 3/3, exit 0. A final pair of passing
characterizations contends two real admissions for `long.MaxValue - 1`: exactly one reserves the
last count, the other throws without wrapping, and exact disposal restores the real empty census.

## Final frozen-source qualification

```text
dotnet build RetroDownfall.Arcanum.slnx --no-restore --no-incremental --disable-build-servers -m:1
Build succeeded. 0 Warning(s), 0 Error(s). Elapsed 00:01:11.34. Exit 0.

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
  --no-build --no-restore
  --filter 'FullyQualifiedName~GrimoireConnectionAdmissionGateTests|FullyQualifiedName~GrimoireConnectionAdmissionInterceptorTests|FullyQualifiedName~GrimoireRequestAdmissionScopeTests'
  --logger 'console;verbosity=minimal'
Passed 227, failed 0, skipped 0, total 227. Exit 0.

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
  --no-build --no-restore
  --filter 'FullyQualifiedName~GrimoireTransitions|FullyQualifiedName~CovenantErasureCoordinatorTests|FullyQualifiedName~GrimoireRequestAdmissionTests|FullyQualifiedName~DataRetentionCovenantResetRecoveryTests|FullyQualifiedName~CovenantResetBootstrapBarrierTests'
  --logger 'console;verbosity=minimal'
Passed 474, failed 0, skipped 3, total 477. Exit 0.
```

The three skips are the existing Windows-only journal file ACL/delete/exchange tests on macOS.

```text
dotnet format RetroDownfall.Arcanum.slnx --verify-no-changes --no-restore
  --include src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs
  tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.Census.cs
  tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.RequestShards.cs
  tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.ZeroSignal.cs
  --verbosity minimal
Exit 0. No diagnostics or changes.

python3 scripts/align_csharp_blanklines.py --repo . --check
Would change 0 file(s). Exit 0.

git diff --check
git diff --cached --check
Exit 0. No diagnostics.
```

Immutable H audit: the full benchmark project, benchmark test directory and packaging test,
benchmark script, workflows, NativeSqlCipher assets, all project/props/targets/lock/solution inputs,
`global.json`, and `NuGet.Config` are byte-identical to H. Intersecting all changed H-relative paths
with the 1,641-line immutable input catalog yields exactly the allowlisted
`src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`. That is also the
only B-relative changed production source. H is an ancestor of B, and B is an ancestor of C1.
Every original Task B gate test/partial remains byte-identical to B. The optional epoch file is
absent; its private type is contained in the allowlisted gate source.

Implementation committed normally as `c50be99dca742751554a8c57566c522d80e294c0`.
This evidence file, final report, and ledger are a separate bookkeeping commit.
