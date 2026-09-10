# Task A: Add the admission benchmark without changing production behavior

## Purpose

Create the committed, immutable benchmark instrument `H` used later to compare the reviewed monitor
baseline `B` with an optional admission candidate `C`. This task changes no
`GrimoireConnectionAdmissionGate` behavior and produces no acceptance claim. Its first run is smoke
and calibration only.

## Binding architecture

- The gate is an admission turnstile, never a scheduler. The benchmark must not add a global queue,
  reopen capacity, fourth phase, or new production admission behavior.
- Create a dedicated Grimoire-admission Native AOT executable outside
  `RetroDownfall.Arcanum.slnx`. Do not edit, reuse, import, or route through the Covenant benchmark
  host, manifest, baseline, comparison arithmetic, or `scripts/benchmark-covenant.sh`.
- Name the new assembly `RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks`. Add exactly that one
  narrow `InternalsVisibleTo` grant to Infrastructure and update the existing IVT inventory test;
  Core needs no new grant. Production gate source and behavior are forbidden in Task A.
- Initialize `SqliteNativeRuntime.Instance` before every SQLite open. The representative database
  workload must compose the real internal gate/drain/lifecycle/native initializer,
  `ArcanumDbContextOptionsConfigurator`, and `AddDbContextPool(..., poolSize: 32)` path; the value 32
  mirrors production retention and is never treated as a concurrency semaphore.
- Use source-generated JSON only. The executable must be AOT- and trim-compatible and fail closed at
  runtime unless `RuntimeFeature.IsDynamicCodeSupported` is false.
- Do not add timing assertions to xUnit. Ordinary tests validate schema, arithmetic, orchestration,
  failure handling, and invariants with deterministic inputs.

## Required benchmark surface

Add a manifest, source-generated JSON context, immutable result contracts, persistent-worker
harness, deterministic comparison/orchestration logic, an operator script, and ordinary tests. Use
repository naming/style and keep the project outside the solution.

Use the exact file/composition/test reconnaissance in
`.superpowers/sdd/2026-09-06-issue-256-turnstile-fast-path-addendum/task-A-recon.md`; it is binding
implementation detail where it does not conflict with this brief or the approved spec. In
particular, compile-link only its five named pure harness/model sources into Arcanum.Tests; never
link `Program.cs`, JSON source generation, or the real composition bed into xUnit. Give the benchmark
project its own locked restore graph and checked-in `packages.lock.json`.

The host must measure all of these distinct operations:

1. finite request acquire/dispose;
2. quiesceable request acquire/read-token/dispose;
3. DB-only work acquire/dispose;
4. work acquire plus effect-group acquire/dispose plus work dispose;
5. physical-open ticket acquire/fail-or-terminal/dispose;
6. nested request plus physical-open success path;
7. `CurrentGeneration` read;
8. predeclared mixed contested operation id `ordinary.mixed`; and
9. representative pooled EF scope/open/query/dispose.

Run direct-gate workloads at concurrency `1`, `2`, `8`, and
`Environment.ProcessorCount`. `ordinary.mixed@Environment.ProcessorCount` is the one predeclared
material-improvement cell; no post-hoc cell selection is allowed.

Keep these phases separate so setup does not pollute measurement:

- warmup;
- bundled latency sampling producing p50/p95/p99;
- uninstrumented throughput/allocation/Gen0/diagnostic-contention measurement; and
- final leak/drain/invariant validation.

Worker/task/barrier construction and logging happen outside measured windows. Use persistent workers
implemented as dedicated reusable `Thread` instances, and deterministic start/finish barriers; do
not use the ThreadPool or create one task per operation. Pin the operation
bundle, warmup, iteration, and sample counts in the manifest. Smoke may use an explicit smaller
manifest/mode but may not silently change the full schema.

The phase boundary uses an explicit `armed -> run -> loop-complete -> parked` protocol over one
maximum-size persistent worker set. All kernel-backed `EventWaitHandle`, `AutoResetEvent`, and
`ManualResetEvent` instances are created, materialized, and warmed before measurement; the harness
must not use `ManualResetEventSlim`, `Monitor`, tasks, lazy handles, or per-phase registrations.
Workers wake and announce `armed` before the controller records process-wide Gen0/contention
baselines. They read a volatile run epoch; only after the baseline does the controller publish that
epoch. Each active worker records an actual monotonic start stamp, then its timestamp/allocation
baseline, executes its exact quotient/remainder partition, records allocation/timestamp terminals,
records an actual completion stamp, and blocks on the precreated shared release. The last completer
signals exactly one precreated completion handle, waking the blocked controller. The controller then
records process-wide terminals and signals exactly one shared release. Every worker records its
actual parked stamp after release. The completion rendezvous is outside primary throughput and
per-thread allocation but inside process-wide Gen0/contention diagnostics; those diagnostics are
therefore explicitly inclusive of this fixed, nonallocating, non-`Monitor` boundary and remain
diagnostic only. Reset is outside measurement and must have no lost-wakeup path. Tests use stored,
preallocated real stamps and replay probe callbacks only after every worker is parked; they prove the
actual asymmetric completion order and that the controller and early completers genuinely block.

Record operations/second, p50/p95/p99, bytes/op, Gen0, diagnostic contention, success/failure counts,
and final live request/work/open/effect/waiter state. Missing, duplicate, non-finite, negative,
overflowed, or zero denominators that cannot be compared are invalid evidence, not a performance
failure.

Profile iteration values are exact cell-wide totals, never per-worker multipliers or ceiling-rounded
approximations. Evidence records exact warmup operations, latency bundles and operations, throughput
operations, success/failure counts, raw allocated bytes and their operation denominator, and the
deterministic expected checksum. Direct allocation comparison uses overflow-safe rational arithmetic,
not truncated bytes/op. The terminal-callback baseline brackets every ordinary warmup, latency, and
throughput phase in the cell.

The host owns a profile-linked watchdog and forwards one linked token through every worker and
maintenance operation. External/process cancellation exits 130; an internal deadline or deadlock is
invalid evidence and exits 2. The operator gives each child a hard parent-side deadline as the final
backstop. Normal completion has no unbounded participant join.

## Exact revision and evidence binding

The result/comparison schema must bind:

- exact Git SHA and clean-tree state;
- measurement session id and pair index/order;
- workload, harness, project/build, dependency/lockfile, toolchain, and native-SQLCipher digests;
- checked compiled-source digest set;
- RID, process architecture, OS architecture/version, runtime, SDK;
- CPU identity/logical count, stopwatch frequency, GC mode, and dynamic-code state; and
- operation/concurrency identity and every metric.

Add one checked-in, path-exact compiled-source difference allowlist for the later `B`/`C` candidate.
It contains exactly these paths:

- `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`
- `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionEpoch.cs`

The epoch file may be absent at B and present at C. The allowlist must never name a directory, wildcard, test
tree, benchmark file, project file, package file, native asset, generated output, or broad production
glob. The comparator/orchestrator refuses if:

- either revision is not an exact clean commit;
- `B` is not an ancestor of `C`;
- harness, manifest, comparison, project/build, dependency, toolchain, environment, or native inputs
  differ;
- a compiled source outside the exact allowlist differs;
- session/pair metadata are missing, duplicated, or mismatched; or
- either process crashes, is cancelled, or omits required operations/metrics.

Task A tests the refusal rules with controlled, hand-derived fixtures. It does not yet create or call
a candidate.

Input identity is independently closed at two levels. A checked-in normalized catalog contains its
own path, every selected benchmark/Core/Infrastructure/Secrets C# input, every embedded schema SQL
resource, the manifest and script, `Directory.Build.props`, the exact project/build/lock/native
inputs, and the optional epoch slot. It contains paths and optionality only, never content digests.
The manifest pins a length-framed digest of that exact catalog shape. Each run records the catalog
shape digest, one immutable-content digest excluding the two allowlisted candidate paths, and the
complete sorted path/presence/content-digest map. Hash framing includes path length and bytes,
presence, and content length and bytes. Absolute paths, backslashes, dot segments, missing or extra
entries, unsorted or duplicate entries, and malformed nonempty lowercase 64-hex digests are invalid.
The host enumerator and xUnit's tracked-file derivation are structurally independent; qualification
independently derives each revision's set with `git ls-tree`, so equal omissions in B and C still
fail.

## Later comparison arithmetic, implemented and tested now

The deterministic comparator must support six independent Native AOT process pairs in exact order:
`B,C`; `C,B`; `B,C`; `C,B`; `B,C`; `C,B`. Only those paired B/C results are acceptance evidence.
Bootstrap pair-level ratios, never in-process latency samples treated as independent runs. Pin the
bootstrap seed/algorithm/replicate count in source or manifest and test against literal fixtures.

Acceptance requires all of the following:

- no single-thread p50 regression beyond 5%;
- no p99 regression beyond 10%;
- the representative pooled-EF confidence interval remains within its checked-in non-regression
  bound;
- no direct-gate bytes/op increase;
- no representative EF allocation regression beyond 5%;
- no ordinary-path terminal-waiter allocation;
- both the point throughput ratio and deterministic one-sided 95% paired-bootstrap lower confidence
  bound are at least `1.20` for the predeclared `ordinary.mixed` logical-processor cell; and
- maintenance work remains O(live leases + live opens), never O(historical admissions).

Compare zero allocation, Gen0, or contention values without division. Contention is diagnostic, not
an acceptance gate when implementations use different synchronization primitives.

Exit codes are exact:

- `0`: valid smoke or accepted comparison;
- `1`: valid measured rejection;
- `2`: invalid or unmeasurable evidence; and
- `130`: cancellation.

## Native AOT script and CI

Add a dedicated script that publishes the dedicated host in Release Native AOT, runs the published
binary directly, and preserves the exit-code contract. Do not use `dotnet run` as benchmark evidence.

The executable—not the script—owns one process-private benchmark home. Its first code path refuses
with exit 2 if `ARCANUM_TEST_HOME` is already set, then creates a fresh system-temporary parent and a
fresh nonce-named child, writes an exclusive ownership marker containing the nonce, session id, PID,
and canonical child path, sets `DOTNET_ENVIRONMENT=Testing` and `ARCANUM_TEST_HOME` to that child, and
only then permits any `ArcanumPaths` type to initialize. Cleanup occurs only after provider/pool/drain
disposal and only if canonical parent/prefix, child name, non-link root, and exact marker still match;
otherwise it leaves the directory and returns invalid evidence rather than deleting. It may delete
only that marked child and its now-empty parent. Controlled tests prove a pre-existing directory,
pre-set environment home, default/user home, missing or changed marker, symlink/reparse root, and
path mismatch are never deleted. The script never deletes the benchmark home; it cleans only its own
`mktemp` archive/publish workspace after exact path/prefix validation.

Use `git archive` into two
isolated temporary source trees for B/C qualification; never switch the caller's checkout. Publish B
and C once each and reuse their immutable binaries for all twelve independent process executions.
Use an isolated output directory and deterministic cleanup. The full operator mode must be able to
record results without claiming baseline/candidate status at `H`.

Qualification additionally requires `--harness H`, proves exact H, H ancestor B, and B ancestor C,
and byte-compares the complete immutable instrument across H, caller, B, and C. H is supplied by the
operator and is never embedded in its own recursively hashed source.

Update CI so a macOS shipping-RID lane explicitly builds/publishes and executes a short Native AOT
smoke. The smoke must cover concurrent admission, a physical-open terminal path, pooled EF, and final
maintenance drain/leak checks. Merely compiling the project is insufficient. Update existing CI
inventory/architecture tests if they require every outside-solution compiled project/script to be
declared.

## TDD order

For each increment, name the production break the test catches, write the smallest real test, run it,
and record the expected RED before implementation. At minimum use these increments:

1. manifest/result JSON round trip and closed operation/concurrency identity;
2. persistent-worker accounting and phase separation;
3. deterministic comparison arithmetic and malformed/mismatched evidence refusal;
4. exact source/input/revision/session binding;
5. real direct-gate smoke and final leak/drain validation;
6. real pooled EF/SQLCipher smoke after explicit native initialization;
7. actual host-owned JSON/AOT schema self-test and source-link purity;
8. script behavior/exit propagation using controlled inputs; and
9. CI/inventory contract executing the dedicated native smoke.

The published host's `--smoke` path must execute a schema self-test through the actual
`AdmissionBenchmarkJsonContext`: round-trip the embedded manifest, one revision run, a complete
literal six-pair evidence bundle, and both accepted and rejected comparison reports, then validate
their exact types/counts. A missing qualification-only JSON root makes smoke exit 2. The packaging/CI
contract proves this self-test is part of the directly executed Native AOT smoke. Tests also pin that
both project files compile the same exact five pure source paths and that those linked sources contain
no conditional-compilation branch, partial augmentation, reflection JSON, or host-only generated
member dependency.

Do not test prose or grep for one magic line. Exercise parsers, comparator results, process exit codes,
or existing CI-structure validators.

## Completion and report

The implementation and calibration previously proposed at `659a3441d90aa25d5050cf8dd561ed3d88816edc`
are invalid and superseded. Independent review found ceiling-expanded counts, synthetic transition
evidence, measured busy-spinning, incomplete callback/allocation accounting, non-closed input
identity, incomplete comparator validation, unbounded cancellation, hard-coded waiter evidence,
cumulative churn, undisposed direct connections, incomplete link-attack proof, and shell exit/input
capture defects. No measurement from that revision is evidence for any later baseline or candidate.
A corrected normal commit becomes a new proposed H only after all remediation tests and Native AOT
smoke pass; it then receives a fresh calibration-only run.

- Run focused tests after every RED/GREEN increment, then the complete new benchmark test slice, a
  clean build of the new project, Native AOT publish/run smoke, `git diff --check`, and relevant CI
  inventory tests.
- Self-review for hidden JIT execution, task-per-operation allocation, output-schema ambiguity,
  post-hoc thresholds, and any accidental Covenant asset or production-gate edit.
- Do not claim an exact maintenance-node visit count: Task A cannot expose it without a forbidden
  production-gate edit. Record historical-churn timing only as support; Task C's structural tests are
  the formal O(live) proof.
- Commit the benchmark harness independently. The exact reviewed commit becomes `H` only after task
  review; smoke/calibration at `H` is not baseline or decision evidence.
- Write the full implementation/TDD/verification report to
  `.superpowers/sdd/2026-09-06-issue-256-turnstile-fast-path-addendum/task-A-report.md`.
- Do not dispatch subagents. Return `DONE`, `DONE_WITH_CONCERNS`, `NEEDS_CONTEXT`, or `BLOCKED`, commit
  SHA(s), one-line tests, and concerns.
