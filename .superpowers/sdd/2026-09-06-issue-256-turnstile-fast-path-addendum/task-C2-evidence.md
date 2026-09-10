# Task C2 execution evidence

- Exact clean start: `68305e4157677194523c4950444347889e828510`.
- Immutable H: `f51ac3f84c3b408510e448311d7a5e15bdbc041e`.
- Reviewed B: `b0be2b4df8855e56f2dcfcaf155dfacddc51b88a`.
- Reviewed C1 implementation: `c50be99dca742751554a8c57566c522d80e294c0`.
- C2 implementation: `09da72dad77dc5b829f688f33ed6b8562621aa8d`.

No acceptance benchmark was run. This commit awaits scoped C2 and whole-candidate review.

## Command convention

All commands ran from the issue-256 worktree. Focused compile/test commands used:

```text
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
  --no-restore --disable-build-servers -m:1 --filter '<filter>'
  --logger 'console;verbosity=minimal'
```

Runs against already freshly compiled binaries instead used `--no-build --no-restore` in place
of the build options. Tests and builds used authorized local MSBuild/test IPC access. No compiler
error, build-launch failure, stale binary, sleep, or abandoned participant supplied RED evidence.

## Structural census RED

Filter:

```text
FullyQualifiedName~Work_membership_reclaims_|FullyQualifiedName~Work_overflow_contention_
```

Compile-clean RED: 3 failed, 0 passed, exit 1. The zero/three-survivor churn cases rejected absent
intrusive work links. The contention case reached the real checked work-count reservation and then
rejected absent work membership on the gate-owned shards. Production was still C1.

The tests require exact live identities after 4,000 work admissions/disposals per churn case,
cleared removed links, independent gate membership/census, no gate-owned work HashSet, and exact
zero after survivors release. The contention case races two dedicated admissions for the final
available count, checks one successful identity and one overflow, and checks each participant's
ambient depth and output. C1's prepopulated-output exhaustion cases remain intact.

## Hot operations and frontier RED

Filter, using `--no-build --no-restore` against that same freshly compiled C1 gate:

```text
FullyQualifiedName~Work_hot_operations_|FullyQualifiedName~Work_captured_shard_
```

RED: 20 failed, 0 passed, exit 1. All four actual work operations (acquire, scope release, effect
begin, exact group release) reached their bounded completion wait while a dedicated participant
held `_sync`; each failed with the expected timeout and completed after cleanup released it.
The sixteen work-kind frontier cases rejected absent immutable work shard identity. Their eventual
GREEN exercises both orders per kind using the actual captured monitor: a winning group holds its
shard through Closing publication, or effect validation blocks at the shard until after Closing.

## Captured epoch, callback, and ambient RED

Filter:

```text
FullyQualifiedName~Work_admission_captured_|FullyQualifiedName~Work_revocation_callback_|FullyQualifiedName~Work_scope_release_publishes_
```

Compile-clean RED: 6 failed, 0 passed, exit 1, all rejecting absent captured work-shard identity.
These are structural migration REDs, not claims of six pre-existing B correctness defects.

The two admission cases force a dedicated thread to block on its real selected shard after
capturing an epoch. Closing or a proven abort retires that admission before the thread unblocks.
The ambient case requires shared release visibility before blocked unlink, and independently
refuses a finisher open while census membership still keeps stage one pending.

The three callback cases cover immediate revocation, deferred effect revocation, and deferred
revocation through abort/reopen/immediate reclose with old and new work beside each other. A
prestarted dedicated reader reenters `CurrentGeneration` and the affected shard during the callback.
The callback checks that neither monitor is held by its caller, checks terminal count and exact
zero completion before throwing, and cannot prevent successful drain or cause repeated cancellation.

## Compiled zero-order RED

Filter:

```text
FullyQualifiedName~Zero_signal_compiled_protocol_
```

Compile-clean RED: 1 failed, 0 passed, exit 1. The strengthened candidate-only C1 executable-IL
test rejected the missing outside-lock signal call on `ReleaseWorkScope`. It now follows both
work release callers through the shared terminal helper: exact decrement occurs in that helper,
while signal publication/zero recheck remain after monitor exit. The existing Interlocked
publication-before-recheck and CAS reservation assertions remain.

## Coherent migration GREEN

The production migration followed all preceding REDs. It removed `_workLeases`, added intrusive
work lists to the existing shards, captured work epoch/shard identity, migrated admission and all
scope/effect state transitions to the captured shard, scanned requests and work together during
Closing, and moved work zero signaling outside the shard before cancellation callbacks.

The union of the four filters above passed 30/30 with the compile/test prefix, exit 0.
The complete gate/interceptor/request-scope slice then passed 256/256, 0 skips, exit 0, using
`--no-build --no-restore`. Every reviewed B and C1 case was retained.

No production correction or further production refactor followed this GREEN.

## Expanded passing characterization

Filter:

```text
FullyQualifiedName~Mixed_work_release_|FullyQualifiedName~Bounded_work_epoch_model_|FullyQualifiedName~Work_membership_reclaims_
```

Passed 12/12, exit 0, with no production edit. Six mixed schedules release a request and the final
work scope or effect before, during, and after entry to the real internal zero-publication suffix.
Four model seeds each run four cycles with 128 ordinary actions: 2,048 seeded acquire/release/effect
actions, plus promotion, canceled shared wait, timeout, proven abort, old/new epoch membership,
immediate reclose, stale signal completion, shuffled repeated disposal, and stage-two disposition.
The two churn cases gained a read-only traversal of the actual gate-owned object graph, proving
there is no historical shadow registry beyond the exact live shard membership.

Filter `FullyQualifiedName~Work_scope_and_successor_` then passed 3/3, exit 0. These cases preserve
the exact surviving sibling through group-first, scope-first, and concurrent disposal, and prove
old-group replay cannot clear the successor or leave removed intrusive links.

Self-review tightened the selected-shard handshake: the probe must be disposed before publishing
its shard to the coordinator. Otherwise a coordinator could hold that shard while the probe was
still trying to release it, preventing the intended admission-at-shard handshake. This was a test
ordering correction, with no observed production failure. Final qualification below includes it.

## Fresh qualification

Source was frozen for:

```text
dotnet build RetroDownfall.Arcanum.slnx
  --no-restore --no-incremental --disable-build-servers -m:1
Build succeeded. 0 Warning(s), 0 Error(s). Elapsed 00:01:04.71. Exit 0.

dotnet build tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj
  --no-restore --no-incremental --disable-build-servers -m:1
Build succeeded. 0 Warning(s), 0 Error(s). Elapsed 00:00:17.64. Exit 0.
```

Final tests used `dotnet test` with `--no-build --no-restore` and the same console logger:

| Filter | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `FullyQualifiedName~GrimoireConnectionAdmissionGateTests\|FullyQualifiedName~GrimoireConnectionAdmissionInterceptorTests\|FullyQualifiedName~GrimoireRequestAdmissionScopeTests` | 269 | 0 | 0 |
| `FullyQualifiedName~GrimoireTransitions\|FullyQualifiedName~CovenantErasureCoordinatorTests\|FullyQualifiedName~GrimoireRequestAdmissionTests\|FullyQualifiedName~DataRetentionCovenantResetRecoveryTests\|FullyQualifiedName~CovenantResetBootstrapBarrierTests` | 474 | 0 | 3 |
| `FullyQualifiedName~GrimoireAdmissionBenchmark` | 119 | 0 | 0 |

Each exited 0. The three existing Windows-only journal tests skip on macOS. The benchmark filter
includes ordinary comparator/evidence/manifest/managed-smoke and packaging contracts; it is not a
Native AOT acceptance measurement or six-pair run.

Scoped `dotnet format RetroDownfall.Arcanum.slnx --verify-no-changes --no-restore --include ...
--verbosity minimal` passed, exit 0, for the gate and `ZeroSignal`, `WorkShards`, `WorkEpochRaces`,
and `WorkModel` test partials. Repository and explicit changed-test blank-line checks each reported
`Would change 0 file(s)`, exit 0. Staging exposed a trailing blank EOF line in two new test files;
only those blank lines were removed. Their blank-line check and both staged/unstaged diff checks
then passed. No executable code or test statement changed after the builds and final tests.

## Identity and scope audits

- H's entire benchmark project, all linked benchmark tests, packaging tests, orchestration script,
  workflows, NativeSqlCipher project/assets, project/props/targets/lock/solution files,
  `global.json`, and `NuGet.Config` compare byte-for-byte equal (`git diff --exit-code H -- ...`, 0).
- The unchanged catalog has 1,641 entries. Intersecting H-relative changed paths with every catalog
  path yields only `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`.
- That gate is also the sole B-relative changed production source. The optional epoch file is
  absent. No producer, public contract, schema, timeout, configuration, build, or native input changed.
- Every original B gate test partial, interceptor test, and request-scope test remains byte-identical
  to B. C1 census/request tests remain unchanged; only its candidate-only zero-order proof expanded.
- H is an ancestor of B; B and the exact C2 start are ancestors of the implementation commit.
- Candidate-only test inventory relative to B is the six added `Census`, `RequestShards`,
  `ZeroSignal`, `WorkShards`, `WorkEpochRaces`, and `WorkModel` partials. C2 adds 42 cases:
  23 work-shard, 9 work-epoch race, and 10 mixed/model cases. C1 adds 31; 196 B cases are preserved.

The implementation was committed normally as `09da72dad77dc5b829f688f33ed6b8562621aa8d`, with a
clean worktree afterward. This evidence, report, and ledger update form a separate bookkeeping commit.
