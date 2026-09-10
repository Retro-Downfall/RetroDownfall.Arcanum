# Task A report: Grimoire-admission benchmark instrument

## Status

DONE — INDEPENDENT-REVIEW FIX ROUND 2 COMPLETE

The review-fixed benchmark instrument is committed at
`f51ac3f84c3b408510e448311d7a5e15bdbc041e`. This new normal commit supersedes
`0eee1f46e9f8ea3fa710cf531291c30472ef21e2` as proposed H and is not yet the independently accepted
immutable H. A fresh calibration-only run from the exact clean round-2 commit completed
successfully. It is instrument calibration only; it is not baseline/candidate evidence and makes
no performance acceptance claim.

The implementations at `0eee1f46e9f8ea3fa710cf531291c30472ef21e2`,
`092869fdc4f746850115ab0a17bfc08dce490751`, and
`659a3441d90aa25d5050cf8dd561ed3d88816edc` are not H. Their calibrations are invalid and superseded
and must never be used as baseline, candidate, qualification, acceptance, or later calibration
evidence. The earlier `6acacc3c9fa36e538380fbb357d49c15b8576505` remains superseded as well. No
commit was amended.

Independent whole review found an architecture-breaking measurement defect: the controller and
early-completing workers busy-spin during the measured process interval, while the ordered probe
replays a synthetic ideal order rather than actual transition stamps. The same review verified
ceiling-expanded non-divisible totals; incomplete profile/count/checksum enforcement; terminal
callback bracketing that omits warmup and latency; truncated allocation comparison; EF exclusion
from the universal p99 gate; non-finite derived-math gaps; a dynamically discovered, incomplete
input identity that omits every schema SQL resource and accepts matching malformed digests; no
linked watchdog; hard-coded waiter evidence; cumulative historical-churn points; undisposed direct
connections; no executed symlink/reparse cleanup attack; qualification cancellation rewritten to
exit 2; and a shell pipeline that accepts the digest of empty toolchain output.

The corrected design uses one maximum-size persistent worker set with precreated and warmed
kernel-backed wait handles, real preallocated monotonic transition stamps, exact quotient/remainder
work partitions, and a blocked controller/completed-worker rendezvous. One last-completer signal
occurs after every worker timing/allocation terminal; the controller then records process terminals
and emits one shared release. Primary timing and per-thread allocation exclude handle calls.
Process-wide Gen0/contention deliberately includes this fixed nonallocating/non-`Monitor`
rendezvous and remains diagnostic only.

The corrected evidence design adds exact phase totals, worker counts, and checksums; rational
allocation numerators; finite overflow-safe comparison; every-cell p99; and a two-level
independently derived input catalog. Strict RED/GREEN remediation, complete Task A verification,
real Native AOT smoke, and clean-commit calibration are now complete.

## What was added

- A dedicated outside-solution executable named
  `RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks`, configured for Native AOT, trimming,
  workstation/non-concurrent GC, invariant globalization, and a project-local locked restore graph.
- One exact Infrastructure `InternalsVisibleTo` grant for that assembly and an updated exact-set
  inventory assertion. Core received no grant.
- A closed manifest with nine operations, four symbolic concurrency cells, fixed smoke and
  qualification profiles, fixed thresholds, fixed xorshift64star bootstrap inputs, and the exact
  two-file source-difference allowlist.
- Immutable source-generated JSON contracts for one run, the complete six-pair evidence bundle,
  and accepted/rejected comparison reports. Published `--smoke` executes a real schema self-test for
  every root.
- One maximum-size persistent dedicated-thread harness using precreated and warmed OS-backed wait
  handles around an explicit `armed -> run -> loop-complete -> parked` protocol. Workers record real
  monotonic transition stamps. The last completer wakes the blocked controller exactly once; the
  controller records the diagnostic process terminal and releases all completed workers once.
  Ordered-probe callbacks replay actual stored order only after every worker is parked. Primary
  worker timing/allocation excludes handle calls; process Gen0/contention includes the fixed
  rendezvous and is diagnostic only.
- The nine required workload bodies over the real internal gate. Direct workers reuse closed,
  non-pooled connections. The EF workload uses the real SQLCipher initializer, real admission
  lifecycle/interceptor, `ArcanumDbContextOptionsConfigurator`, and
  `AddDbContextPool(..., poolSize: 32)` while concurrency remains controlled only by the worker
  harness.
- Final close/drain/rollback-and-reopen checks, fresh request/open checks, zero harness-owned live
  state, zero ordinary terminal-callback materialization, and predeclared 0/64/640 historical-churn
  observations. Churn timing is behavioral support only; it is not claimed as the formal O(live)
  proof reserved for Task C.
- A checked-in, sorted path/optionality catalog containing all 1,641 selected inputs, including all
  383 embedded schema SQL files, all selected benchmark/Core/Infrastructure/Secrets C# files,
  project/build/lock/native inputs, its own path, and the optional epoch slot. The manifest pins the
  framed catalog-shape digest. Each run records the shape digest, immutable-content digest outside
  the exact two-file candidate allowlist, and every per-path presence/content digest.
- An executable-owned nonce-marked temporary database home. A pre-set `ARCANUM_TEST_HOME` is refused
  before any home or `ArcanumPaths` use. Cleanup requires the exact canonical temp parent/prefix,
  child name, non-link roots, process/session/nonce/path marker, deletes only the marked child, then
  deletes only the empty parent. The published smoke runs controlled negative safety checks.
- A linked runtime/profile watchdog covering all phases, maintenance, final validation, and bounded
  participant shutdown. External cancellation exits 130; internal expiry/deadlock is invalid
  evidence with exit 2. Final validation owns a real next-open-generation waiter and proves it is
  pending while closed and completes at the exact reopen generation.
- A dedicated operator script with closed smoke/calibrate/qualify modes. It publishes with
  `RestoreLockedMode=true`, runs the apphost directly, uses commit archives rather than switching the
  caller checkout, publishes B and C once, executes exact `B,C; C,B; B,C; C,B; B,C; C,B` process
  order, requires exact H/B/C ancestry and byte-identical immutable instrument inputs, bounds every
  child process, preserves 0/1/2/130, and deletes only its validated script workspace. It never
  deletes the executable-owned database home. Every helper-owned POSIX shell variable is
  mechanically namespaced to prevent caller-state corruption.
- An unconditional macOS CI clean build and direct Native AOT smoke beside the existing AOT checks.

## TDD record

Each increment began with a compile-clean behavioral or structural RED.

1. Manifest/result closure: RED for the absent contracts, manifest, and source-generation shape;
   GREEN 9/9. Named test-construction breaks corrected before the genuine RED were invalid
   `Contains`/`EndsWith` overloads and array reference equality.
2. Persistent workers: RED for the absent worker harness; GREEN 4/4. An explicit ref-delegate lambda
   signature was corrected before the compile-clean RED.
3. Comparator: RED for the absent comparator/evidence contracts; GREEN 17/17. Test-only namespace
   collision with `Environment` and an internally inconsistent percentile fixture were corrected.
4. Evidence binding: RED for the absent validator; GREEN 19/19 initially and 20/20 after the
   discovered historical-churn omission. A duplicate-source fixture found an implementation throw
   in `ToDictionary`; validation now fails closed before dictionary construction. Later self-review
   added same-revision source-map equality across all six processes and a regression fixture.
5. Direct-gate bed: RED for the absent host bed; GREEN 1/1 over all seven direct operations with two
   concurrent workers, final close/drain/reopen, exact accounting, and zero callback materialization.
6. Representative database bed: RED for the absent real composition; GREEN 1/1 using initialized
   SQLCipher, pooled EF, `SELECT 1`, `cipher_version`, and final drain. Self-review removed an
   accidental double-dispose of a context already owned by its DI scope.
7. AOT/JSON host: RED for the absent outside-solution host; GREEN 1/1 structural contract plus the
   later published runtime proof. Cross-platform backslash extraction and two over-broad purity
   assertions were corrected in the test. Host-only compilation then caught and fixed a bad named
   argument and an invalid `SelectMany` overload.
8. Script: compile-clean RED because the script did not exist; GREEN 6/6 for publish/direct-run,
   malformed mode, and exact 0/1/2/130 propagation. Controlled qualification orchestration made it
   7/7. Calibration exposed POSIX global function-variable collision after the first proposed-H
   workload; the apphost correctly refused to overwrite the publish directory and emitted no false
   evidence. A focused regression was added before the fix, making the slice 8/8. A real publish also
   caught that `dotnet publish` requires `-p:RestoreLockedMode=true`, not restore's
   `--locked-mode` switch.
9. CI/inventory: RED for the absent exact IVT and absent unconditional macOS build/smoke; GREEN 4/4
   focused and 26/26 across the complete CI/IVT slices.

Additional integration findings fixed before proposed H:

- Probe callbacks originally occurred inside the measured process interval. They now occur only
  after every worker is parked; the measured primary timing/allocation window uses preallocated
  atomic state, while the process diagnostic window includes only the fixed precreated-handle
  rendezvous.
- Qualification profile iterations were initially multiplied by worker count. They are now
  deterministically partitioned across workers, so each symbolic cell executes the pinned total
  workload while still creating all configured workers.
- A parallel clean host build and test competed for the same referenced-project outputs and caused
  an MSBuild copy failure. Qualification commands were thereafter run sequentially; this was an
  orchestration collision, not a source failure.
- `dotnet restore --use-lock-file` initially emitted untracked lockfiles into Core, Infrastructure,
  and Secrets. Those generated files were removed. A subsequent exact
  `dotnet restore <benchmark-project> --locked-mode` succeeded through every project reference and
  left only the benchmark-local lockfile; a packaging assertion now pins that boundary.

## Architecture-breaker remediation TDD record

The remediation used focused compile-clean RED before each implementation cluster:

1. Exact accounting and synchronization: RED fixtures exposed ceiling-expanded totals, synthetic
   transition order, and CPU-consuming controller/early-worker waits. GREEN uses quotient/remainder
   partitioning and the precreated kernel-wait rendezvous. A suspected shutdown lost wakeup was
   reproduced and traced to two stale test assumptions after exact-total semantics—one zero-work
   worker and one unreachable throw index—not to a signal race; the fixtures were corrected without
   weakening timing or blocking assertions.
2. Measurement and comparison: RED fixtures exposed incomplete warmup/latency/throughput callback
   bracketing, derived-only allocation comparison, non-finite/overflow arithmetic, and the EF p99
   exception. GREEN records raw numerators/denominators, compares exact allocation rationals, rejects
   non-finite derived math, brackets the full cell, and applies p99 to every cell.
3. Closed evidence identity: RED fixtures exposed missing schema/build/native inputs, malformed and
   empty digests, missing/extra/unsorted/absolute paths, and symmetric catalog drift. GREEN pins the
   1,641-path catalog shape independently in the host, xUnit through `git ls-files`, and qualification
   through `git ls-tree`, with immutable-content and complete per-path digests.
4. Bounded lifecycle: RED fixtures exposed missing runtime/profile watchdog propagation, hard-coded
   waiter evidence, cumulative churn, undisposed direct connections, and a cleanup test that never
   exercised a real link. GREEN links cancellation through every phase and maintenance boundary,
   owns a real next-generation waiter, creates fresh 0/64/640 gates, disposes every direct
   connection, and executes a real symlink/sentinel attack in the published host.
5. Operator shell: RED fixtures exposed qualification exit-130 rewriting, a toolchain pipeline that
   could hash empty input, an unbounded child, and POSIX function-global variable collisions. GREEN
   stages and validates machine inputs without masking commands, bounds every child, preserves 130,
   verifies H/B/C/catalog/instrument identity, and mechanically enforces function-specific variable
   namespaces. The deterministic fake-deadline fixture completes and maps expiry to exit 2.
6. Final self-review found one more honest-input gap: changing the declared worker count for a cell
   still passed exact counts. The new `wrong-worker-count` fixture failed with valid evidence, then
   GREEN bound every cell to its manifest concurrency or the recorded logical processor count.

## Independent-review fix round 1 TDD record

The seven Important review findings were closed one cluster at a time with a focused behavioral RED
before the corresponding implementation change:

1. Required JSON metrics: the reviewer-proven omitted-property acceptance was preserved as RED.
   `[JsonRequired]` now closes every cell metric and the final-state and historical-churn metric
   records. The 9 cell, 7 final-state, and 5 churn omission cases are GREEN and fail during JSON
   parsing rather than defaulting to zero.
2. EF allocation: `ef.pooled@two` with a doubled exact allocation numerator was accepted at RED and
   rejected at GREEN. The 5% rational allocation gate now applies to every EF concurrency cell.
3. Derived arithmetic: six finite `double.MaxValue` mixed ratios overflowed the old mean at RED.
   Incremental overflow-safe means and explicit finite aggregate validation are GREEN.
4. Native-host closure: a published host initially accepted source-root drift. It now enumerates C#
   roots, schema SQL, fixed project/build/lock/native inputs, and the optional epoch independently of
   the checked catalog, then matches the exact embedded SQL resource set. The published positive,
   extra-C#, extra-SQL, and missing-SQL cases are GREEN.
5. Teardown: deterministic delegate/TCS fixtures proved the earlier drain-first short circuit and
   incomplete worker cleanup. The independent 10-second cleanup deadline now always attempts
   pre-drain, provider disposal, final drain, and pool clearing; worker teardown attempts every
   wake, bounded join, and handle disposal; incomplete lifecycle retains the home; cancellation
   remains exit 130. Final state is sampled only after measured connections are disposed.
6. Host invariants: the inherited focused baseline was 94 passed and 6 RED cases for wrong/missing
   churn, wrong final live/reopen state, and terminal waiters. The shared validator now enforces the
   exact 36-cell set, phase totals, workers, successes, checksums, allocation fractions, zero
   failures/callbacks, exact final-live/drain/reopen state, and exact finite 0/64/640 churn. The
   complete comparison slice is GREEN 36/36, and the actual Native AOT smoke invokes the validator.
7. Qualification refusal paths: the former empty-success Git/cmp fakes were replaced by a complete
   controlled 1,641-path revision snapshot and the system `cmp`. The positive case, exit-130 case,
   H ancestry refusal, extra-C# refusal, extra-SQL refusal, H/B byte refusal, H/C byte refusal, and
   caller-byte refusal are all GREEN after being driven RED individually.

The two round-1 Minor review items were deliberately not changed and remain recorded for final
triage in `progress.md`: a non-degenerate literal bootstrap golden fixture, and a stronger real
worker-one completion transition before the asymmetric test releases worker zero. Round 2 also
defers the newly reported subprocess-timeout cleanup Minor.

## Independent-review fix round 2 TDD record

Round 2 verified that finding 5 remained open and that the round-1 worker-disposal change added a
process crash. The replacement regression uses one real persistent worker held inside the actual
operation by deterministic barriers. It cancels and joins the controller, exercises the production
five-second worker join deadline, inspects every wait handle still reachable by the live worker,
then releases and positively joins the worker before retrying cleanup. Against
`0eee1f46e9f8ea3fa710cf531291c30472ef21e2`, the focused test process aborted at RED with exit 1
because the test host crashed on an unhandled `ObjectDisposedException` from
`WorkerLoop` calling `_allCompleted.Set()` after the failed join had disposed that handle.

GREEN splits shutdown into observable stages. Dispose still signals every worker and attempts every
bounded join, but it does not dispose any worker-reachable wait handle unless every join succeeds.
A missed join leaves a false recorded termination witness and all handles open. A later explicit
bounded retry may join the now-terminated worker and only then attempts every handle disposal. No
reaper or deferred process cleanup was added. The exact live-worker regression is GREEN 1/1 and the
complete persistent-worker lifecycle class is GREEN 16/16.

The measured-resource witness cluster was then driven RED separately: compilation failed because
there was no worker-termination witness, no harness termination result, and final-state sampling
accepted an unqualified disposal action (`CS0246`, `CS1061`, and `CS8030`). GREEN adds a pure witness
requiring both positive worker termination and worker-connection disposal. The workload bed records
termination only after all harness joins succeed, preserves an existing primary exception or
cancellation if teardown is also incomplete, and Program makes both final-state sampling and home
deletion consume that witness. A late worker exit that was not positively joined cannot change the
recorded false witness or authorize deletion. The focused teardown/witness set is GREEN 7/7.

Self-review confirmed that all prior teardown attempts remain: runtime teardown still attempts
pre-drain, provider disposal, final drain, and pool clearing under the independent cleanup deadline;
worker teardown attempts every signal and every join, and after successful joins attempts every
handle disposal. External cancellation remains the primary exit 130. Incomplete worker lifecycle
retains the marked benchmark home. No production gate/epoch, Covenant benchmark, workflow, spec,
plan, catalog shape, or candidate allowlist bytes changed.

## Final round-2 verification evidence

Review-fixed proposed-H implementation:

```text
f51ac3f84c3b408510e448311d7a5e15bdbc041e
fix(grimoire): retain worker teardown resources on timeout
```

Focused and aggregate verification on the exact implementation bytes:

```text
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --no-restore -m:1 \
  --filter FullyQualifiedName~GrimoireAdmissionPersistentWorkerTests
Passed: 16, Failed: 0, Skipped: 0

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --no-restore -m:1 \
  --filter 'FullyQualifiedName~GrimoireAdmission|FullyQualifiedName~ContinuousIntegrationWorkflowTests|FullyQualifiedName~InternalsVisibleToInventoryTests'
Passed: 166, Failed: 0, Skipped: 0

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --no-restore -m:1 \
  --filter FullyQualifiedName~GrimoireAdmissionBenchmarkPackagingTests
Passed: 19, Failed: 0, Skipped: 0

dotnet restore \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  --locked-mode -r osx-arm64
Succeeded; all projects were up to date and no production lockfile was created.

dotnet build \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  -c Debug --no-restore --no-incremental -m:1
Build succeeded, 0 warnings, 0 errors.

dotnet format tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --verify-no-changes --no-restore \
  --include tests/RetroDownfall.Arcanum.Tests/Benchmarks/GrimoireAdmissionPersistentWorkerTests.cs
Succeeded.

dotnet format \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  --verify-no-changes --no-restore
Succeeded.

sh -n scripts/benchmark-grimoire-admission.sh
git diff --check
lockfile-boundary, forbidden-host-pattern, and forbidden production/Covenant/workflow audits
Succeeded.

./scripts/benchmark-grimoire-admission.sh --smoke
exit 0
Grimoire admission Native AOT smoke passed (36 cells).
```

The Native AOT publish emitted only third-party EF/DependencyModel analysis and local linker
environment warnings. No first-party warning was emitted.

Fresh calibration-only run from the exact clean round-2 proposed H:

```text
./scripts/benchmark-grimoire-admission.sh \
  --calibrate \
  --revision f51ac3f84c3b408510e448311d7a5e15bdbc041e \
  --out /private/tmp/grimoire-admission-calibration-f51ac3f84c3b.json
exit 0

shasum -a 256 /private/tmp/grimoire-admission-calibration-f51ac3f84c3b.json
1f164b28cf26829380165c61dadefeb041d566d2541a072da04a227327e95a59
```

The artifact is 405,946 bytes. A closed `jq -e` validation returned true for the exact revision,
clean tree, calibration session, role H, qualification profile, exit 0, 36 unique cells, exact
20,000 warmup + 64,000 latency + 1,000,000 throughput operations per cell, exact workers,
1,084,000 successes and zero failures, hand-derived deterministic checksums, exact allocation
fractions, finite ordered metrics, zero terminal callbacks, zero live resources/waiters, successful
drain/reopen, finite successful 0/64/640 churn, Native AOT environment, pinned catalog shape, and the
exact sorted 1,641-entry catalog path/presence/digest map. The artifact remains outside the
repository and is calibration-only, not B/C qualification or acceptance evidence.

## Superseded round-1 verification evidence

The evidence below belongs to `0eee1f46e9f8ea3fa710cf531291c30472ef21e2`, which round 2
superseded after reproducing the live-worker teardown crash. It is retained only as history and is
not valid H evidence.

Review-fixed proposed-H implementation:

```text
0eee1f46e9f8ea3fa710cf531291c30472ef21e2
test(grimoire): close admission benchmark review gaps
```

Focused and aggregate checks on the exact implementation bytes:

```text
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --no-restore -m:1 \
  --filter 'FullyQualifiedName~GrimoireAdmission|FullyQualifiedName~ContinuousIntegrationWorkflowTests|FullyQualifiedName~InternalsVisibleToInventoryTests'
Passed: 165, Failed: 0, Skipped: 0

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --no-restore -m:1 \
  --filter FullyQualifiedName~GrimoireAdmissionBenchmarkPackagingTests
Passed: 19, Failed: 0, Skipped: 0

dotnet restore \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  --locked-mode -r osx-arm64
Succeeded; all projects were up to date and no production lockfile was created.

dotnet build \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  -c Debug --no-restore --no-incremental -m:1
Build succeeded, 0 warnings, 0 errors.

dotnet format tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --verify-no-changes --no-restore --include <six Task A test files>
Succeeded.

dotnet format \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  --verify-no-changes --no-restore
Succeeded.

sh -n scripts/benchmark-grimoire-admission.sh
git diff --check
forbidden production/Covenant/workflow-file audit
forbidden host-pattern and lockfile-boundary audits
Succeeded.

./scripts/benchmark-grimoire-admission.sh --smoke
exit 0
Grimoire admission Native AOT smoke passed (36 cells).
```

The Native AOT publish emitted only third-party EF/DependencyModel analysis and local linker
environment warnings. No first-party warning was emitted, and the published host completed all 36
cells with its exact invariant validator.

Fresh calibration-only run from the exact clean new proposed H:

```text
./scripts/benchmark-grimoire-admission.sh \
  --calibrate \
  --revision 0eee1f46e9f8ea3fa710cf531291c30472ef21e2 \
  --out /private/tmp/grimoire-admission-calibration-0eee1f46e9f8.json
exit 0

shasum -a 256 /private/tmp/grimoire-admission-calibration-0eee1f46e9f8.json
5d0f4c484661721970048e71443530ebb4b1aaa073a68a95d4574d8ff714d62d
```

The artifact is 405,999 bytes. A closed `jq -e` validation confirmed the exact revision, clean
tree, calibration session, role H, qualification profile, exit 0, 36 unique operation/concurrency
cells, exact 20,000 warmup + 64,000 latency + 1,000,000 throughput operations per cell, exact
manifest/environment workers, 1,084,000 successes and zero failures per cell, independently
derived deterministic checksums, exact allocation fractions, finite ordered metrics, zero terminal
callbacks, zero live resources and waiters, successful drain/reopen, finite successful 0/64/640
churn observations, dynamic code disabled, the pinned catalog-shape digest, and the exact sorted
1,641-entry catalog path/presence/digest map. The artifact remains outside the repository and is
calibration-only, not B/C qualification or acceptance evidence.

## Superseded pre-fix verification evidence

The evidence below belongs to `092869fdc4f746850115ab0a17bfc08dce490751`, which independent
review rejected. It is retained only as history and is not valid H evidence.

Corrected docs and architecture-breaker ruling:

```text
ea2aabe5 docs(grimoire): invalidate flawed admission calibration
```

Corrected proposed-H implementation:

```text
092869fdc4f746850115ab0a17bfc08dce490751
test(grimoire): harden admission benchmark evidence
```

Focused and aggregate checks on the corrected implementation:

```text
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --no-restore -m:1 \
  --filter FullyQualifiedName~GrimoireAdmissionBenchmarkPackagingTests
Passed: 12, Failed: 0, Skipped: 0

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --no-restore -m:1 \
  --filter 'FullyQualifiedName~GrimoireAdmission|FullyQualifiedName~ContinuousIntegrationWorkflowTests|FullyQualifiedName~InternalsVisibleToInventoryTests'
Passed: 124, Failed: 0, Skipped: 0

dotnet restore \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  --locked-mode -r osx-arm64
Succeeded; all projects up to date and no production project lockfile was created.

dotnet build \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  -c Debug --no-restore --no-incremental -m:1
Build succeeded, 0 warnings, 0 errors.

dotnet format tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --verify-no-changes --no-restore \
  --include \
    tests/RetroDownfall.Arcanum.Tests/Benchmarks/GrimoireAdmissionBenchmarkComparisonTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Benchmarks/GrimoireAdmissionBenchmarkEvidenceTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Benchmarks/GrimoireAdmissionBenchmarkManifestTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Benchmarks/GrimoireAdmissionBenchmarkSmokeTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Benchmarks/GrimoireAdmissionPersistentWorkerTests.cs \
    tests/RetroDownfall.Arcanum.Tests/Packaging/GrimoireAdmissionBenchmarkPackagingTests.cs
Succeeded.

dotnet format \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  --verify-no-changes --no-restore
Succeeded.

sh -n scripts/benchmark-grimoire-admission.sh
git diff --check
Succeeded.

./scripts/benchmark-grimoire-admission.sh --smoke
exit 0
Grimoire admission Native AOT smoke passed (36 cells).
```

The published smoke exercised the embedded manifest/catalog byte check, source-generated JSON roots,
real SQLCipher pooled-EF bed, effective `ConcurrentGC` runtime configuration, real waiter transition,
and symlink/sentinel safety test. Publish output contained third-party EF/DependencyModel analysis and
local linker-environment warnings only; the host completed successfully.

Fresh calibration-only run from the exact clean proposed H:

```text
./scripts/benchmark-grimoire-admission.sh \
  --calibrate \
  --revision 092869fdc4f746850115ab0a17bfc08dce490751 \
  --out /private/tmp/grimoire-admission-calibration-092869fdc4f7.json
exit 0

shasum -a 256 /private/tmp/grimoire-admission-calibration-092869fdc4f7.json
81fbea3ba095b03389dbef67f7e8422ae767c398789c52371631aa92825bcbe5
```

The artifact is 405,969 bytes. A closed `jq -e` validation confirmed the exact revision, clean tree,
role H, qualification profile, exit 0, 36 unique operation/concurrency cells, exact 20,000 warmup +
64,000 latency + 1,000,000 throughput operations per cell, exact manifest/environment worker counts,
1,084,000 successes and zero failures per cell, deterministic checksums, exact allocation fractions,
finite ordered latency values, zero materialized terminal callbacks, zero live resources/waiters,
successful drain/reopen, fresh successful 0/64/640 churn observations, dynamic code disabled, the
pinned catalog-shape digest, 1,641 sorted unique input entries, lowercase 64-hex digests for every
present input, and only the allowlisted epoch slot absent. The artifact remains outside the
repository and is calibration-only, not B/C qualification or acceptance evidence.

## Superseded verification evidence

Everything in this section records what ran before the architecture-breaking defects were found. It
does not validate H and none of its calibration data is reusable.

Focused and aggregate checks:

```text
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --filter FullyQualifiedName~GrimoireAdmission --no-restore
Passed: 65, Failed: 0, Skipped: 0

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --filter 'FullyQualifiedName~InternalsVisibleToInventoryTests|FullyQualifiedName~ContinuousIntegrationWorkflowTests' \
  --no-restore
Passed: 26, Failed: 0, Skipped: 0

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  --filter FullyQualifiedName~GrimoireAdmissionBenchmarkPackagingTests --no-restore
Passed: 8, Failed: 0, Skipped: 0

dotnet restore \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  --locked-mode
Succeeded; only the benchmark-local packages.lock.json exists.

dotnet build \
  tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
  -c Debug --no-incremental --no-restore
Build succeeded, 0 warnings, 0 errors.

dotnet format <benchmark-project> --no-restore --verify-no-changes --verbosity minimal
Succeeded.

git diff --check
Succeeded.

sh -n scripts/benchmark-grimoire-admission.sh
Succeeded.
```

Published smoke on the exact proposed-H source:

```text
./scripts/benchmark-grimoire-admission.sh --smoke
exit 0
Grimoire admission Native AOT smoke passed (36 cells).
```

The publish emitted only the repository's existing third-party EF/DependencyModel IL/AOT warnings
and linker environment warnings; no first-party IL/AOT warning was emitted.

Invalidated former calibration-only run:

```text
./scripts/benchmark-grimoire-admission.sh \
  --calibrate \
  --revision 659a3441d90aa25d5050cf8dd561ed3d88816edc \
  --out /private/tmp/arcanum-grimoire-admission-659a3441-calibration.json
exit 0
```

The former artifact and its hash are intentionally not retained as valid evidence. Its structural
checks passed only under the superseded schema:

- revision `659a3441d90aa25d5050cf8dd561ed3d88816edc`, clean tree, role `H`, profile
  `qualification`;
- exactly 36 operation/concurrency cells;
- every cell has zero failures and zero terminal-callback materialization;
- final live request/work/open/effect/waiter counts are all zero;
- final drain and reopen both succeeded;
- exact historical-churn points 0, 64, and 640 all drained successfully; and
- run exit code 0.

This calibration file is diagnostic only and intentionally remains outside the repository.

## Scope and self-review

- No production gate source was edited.
- No candidate epoch file was added.
- No Covenant benchmark/core performance asset or script was edited or reused.
- No approved addendum spec or plan was edited. The Task A brief, progress ledger, and report record
  the independent review, invalidation, corrected boundary, and evidence.
- The host is outside `RetroDownfall.Arcanum.slnx` and xUnit compile-links exactly the five approved
  pure files—never `Program.cs`, JSON generation, composition, or the real workload bed.
- No ThreadPool task-per-operation, reflection JSON, post-hoc threshold/cell choice, hidden JIT
  evidence, global producer queue, reopen-capacity behavior, or new production phase was introduced.
- The EF pool size is retention only; it is not used as admission or worker capacity.
- Qualification mode exists and is deterministically tested but was not run because Task A has no
  reviewed candidate and must make no acceptance claim.

## Concerns

No implementation blocker remains. `f51ac3f84c3b408510e448311d7a5e15bdbc041e` is deliberately
called proposed H until independent review accepts it as the immutable instrument. The round-2 fix
required no production gate behavior, epoch candidate, Covenant benchmark asset, workflow edit, or
wider two-file candidate allowlist. Calibration is valid only as an instrument sanity check; no B/C
performance claim exists yet. The Native AOT publish continues to emit known third-party
EF/DependencyModel and local linker-environment warnings; the first-party host build is warning-free.
Three Minor items remain deferred: the literal non-degenerate bootstrap golden, the stronger
asymmetric worker-one completion transition, and subprocess-timeout cleanup.
