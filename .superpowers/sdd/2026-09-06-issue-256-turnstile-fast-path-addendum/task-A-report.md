# Task A report: Grimoire-admission benchmark instrument

## Status

DONE

The dedicated benchmark instrument is implemented without changing
`GrimoireConnectionAdmissionGate.cs`, adding an epoch candidate, or changing production admission
behavior. The reviewed commit should treat
`659a3441d90aa25d5050cf8dd561ed3d88816edc` as the proposed `H`. The earlier
`6acacc3c9fa36e538380fbb357d49c15b8576505` is superseded because calibration exposed and the next
normal commit fixed a script variable-collision bug. No commit was amended.

The one run at proposed `H` was calibration only. It is not baseline, candidate, qualification, or
acceptance evidence.

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
- A persistent dedicated-thread harness using an explicit lock-free
  `armed -> run -> loop-complete -> parked` protocol. Process Gen0/contention baselines and terminals
  contain no monitor/event/log/probe calls. Ordered-probe callbacks replay only after every worker is
  parked, from preallocated volatile epoch state that checks the real transitions.
- The nine required workload bodies over the real internal gate. Direct workers reuse closed,
  non-pooled connections. The EF workload uses the real SQLCipher initializer, real admission
  lifecycle/interceptor, `ArcanumDbContextOptionsConfigurator`, and
  `AddDbContextPool(..., poolSize: 32)` while concurrency remains controlled only by the worker
  harness.
- Final close/drain/rollback-and-reopen checks, fresh request/open checks, zero harness-owned live
  state, zero ordinary terminal-callback materialization, and predeclared 0/64/640 historical-churn
  observations. Churn timing is behavioral support only; it is not claimed as the formal O(live)
  proof reserved for Task C.
- An executable-owned nonce-marked temporary database home. A pre-set `ARCANUM_TEST_HOME` is refused
  before any home or `ArcanumPaths` use. Cleanup requires the exact canonical temp parent/prefix,
  child name, non-link roots, process/session/nonce/path marker, deletes only the marked child, then
  deletes only the empty parent. The published smoke runs controlled negative safety checks.
- A dedicated operator script with closed smoke/calibrate/qualify modes. It publishes with
  `RestoreLockedMode=true`, runs the apphost directly, uses commit archives rather than switching the
  caller checkout, publishes B and C once, executes exact `B,C; C,B; B,C; C,B; B,C; C,B` process
  order, preserves 0/1/2/130, and deletes only its validated script workspace. It never deletes the
  executable-owned database home.
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
  after every worker is parked; the measured protocol uses only preallocated epoch state and
  lock-free operations.
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

## Verification evidence

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

Clean proposed-H calibration-only run:

```text
./scripts/benchmark-grimoire-admission.sh \
  --calibrate \
  --revision 659a3441d90aa25d5050cf8dd561ed3d88816edc \
  --out /private/tmp/arcanum-grimoire-admission-659a3441-calibration.json
exit 0
```

The atomic calibration file is 294,329 bytes and has SHA-256
`a369f468e451cb9f0b5a6d947693ef9d6edf27f16df22f87b06f21d104c7ec58`. A closed `jq -e`
validation confirmed:

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
- No tracked addendum spec or plan was edited.
- The host is outside `RetroDownfall.Arcanum.slnx` and xUnit compile-links exactly the five approved
  pure files—never `Program.cs`, JSON generation, composition, or the real workload bed.
- No ThreadPool task-per-operation, reflection JSON, post-hoc threshold/cell choice, hidden JIT
  evidence, global producer queue, reopen-capacity behavior, or new production phase was introduced.
- The EF pool size is retention only; it is not used as admission or worker capacity.
- Qualification mode exists and is deterministically tested but was not run because Task A has no
  reviewed candidate and must make no acceptance claim.

## Concerns

No blocking concern. Native AOT publish continues to report the already-known third-party EF and
DependencyModel warnings; the host emitted no first-party IL/AOT warning and the published workload
completed successfully. The proposed-H calibration is one process and therefore must not be used as
performance-decision evidence.
