# Covenant Verification and Documentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the implemented Covenant contract under Native AOT, real SQLCipher, deterministic structural tests, measured performance gates, full product suites, independent review, and complete owning documentation.

**Architecture:** Deterministic unit and integration tests enforce semantics in ordinary CI. A tiny checked-in Native AOT benchmark host measures the exact production path on a fingerprinted workload. Shipping-RID smoke binaries verify Unicode, serialization, native loading, and disabled/enabled seams. Repository documents and machine-readable inventories are updated from the final behavior, then one clean feature commit is integrated and pushed.

**Tech Stack:** .NET 10 Native AOT, xUnit, shell and PowerShell verification scripts, Cobertura coverage, GitHub Actions, PCG32 bootstrap analysis, Markdown and JSON contract inventories, Git.

## Global Constraints

- Begin measured-gate work only after Plans 01 through 04 expose stable production seams. Structural contract tests may be written earlier and must remain deterministic.
- Performance tests never run under coverage, debugger, or ordinary parallel test load. Ordinary tests assert structure, query plans, command counts, workload fingerprints, and gate math.
- The benchmark host calls the exact production context-provider, store, prompt, provider-freeze, sensitivity, and disclosure-acknowledgement path. It cannot duplicate those algorithms.
- Do not weaken a threshold, exclude production code from coverage, add a skip, or update a literal expectation until the implementation and approved specification justify the change.
- Store no machine secret, absolute personal path, raw Covenant content, API key, or unredacted provider payload in a report or checked-in fixture.
- Record every witnessed red and green command through `scripts/record-covenant-evidence.sh`. Every `Run` command below is the payload passed after the recorder's `--` separator once the recorder exists. The Task 1 red run that proves the recorder absent is captured from terminal output and imported as the recorder's first bounded record immediately after its own green implementation. The recorder writes append-only NDJSON to `artifacts/covenant/issue-74/tdd/commands.ndjson` with task, phase, exact command, exit code, expected failure reason or green assertion, UTC timestamp, base commit, and candidate-tree fingerprint. It writes final command summaries under `artifacts/covenant/issue-74/verification/` and refuses any output path outside the exact issue-74 evidence root.
- Keep `artifacts/covenant/issue-74/tdd/`, `native/`, `benchmark/`, and `verification/` repository-local and ignored. CI uploads those same content-free or synthetic-workload artifacts. No file below that root may be staged. The checked-in workload manifest, accepted content-free benchmark baseline, schema/workload fingerprints, and `docs/Arcanum.DESIGN.md` outcome summary are the only committed evidence.
- The benchmark baseline binds a deterministic `BenchmarkInputFingerprint` over every build, production, native, workload, fixture, runner, and gate input while excluding the baseline file itself, ignored evidence, Git metadata, and documentation. This prevents a self-referential baseline digest. A final pre-commit gate report separately binds the current base commit plus a `CandidateTreeFingerprint` covering every repository-relative path, mode, and byte digest in the complete approved change. Integration must prove the staged tree and final commit tree match that second fingerprint. Immutable-commit CI reports additionally record the final commit ID.
- Absolute wall-clock qualification occurs only on the approved `Mac17,4`, Apple M5 10-core, 16 GiB, macOS 27 arm64, .NET 10.0.10 reference profile while connected to external power with Low Power Mode disabled. Other machines enforce structural and allocation gates and report wall-clock observations without claiming merge qualification.
- Local native execution covers the current shipping RID. Source, hash, SBOM, dependency, and reproducibility inventories cover all five assets locally where supported. Native runtime, compatibility, testfixture, and AOT smoke evidence for all five RIDs comes from required native CI runners and must be green before `main` advances.
- Apply the verification-before-completion skill before any success claim, commit, merge, or push.

---

## Task 1: Create the reproducible benchmark workload manifest

**Files:**

- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj`
- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/Program.cs`
- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/covenant-workload-v1.json`
- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantBenchmarkJsonContext.cs`
- Create: `scripts/record-covenant-evidence.sh`
- Modify: `.gitignore`
- Modify: `tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj`
- Add project: `RetroDownfall.Arcanum.slnx`
- Create: `tests/RetroDownfall.Arcanum.Tests/Performance/CovenantBenchmarkContractTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Packaging/CovenantEvidenceScriptTests.cs`

- [ ] Write tests named `Workload_v1_has_the_approved_occupancy`, `Workload_v1_history_and_tools_have_literal_digests`, `Workload_v1_sensitivity_transition_is_literal`, and `Workload_v1_fingerprint_is_stable`. Add recorder tests named `Recorder_appends_one_bounded_record_for_each_command`, `Recorder_rejects_paths_outside_issue_74`, `Recorder_never_records_environment_secrets`, and `Issue_74_evidence_paths_are_ignored`.
- [ ] Assert exactly 64 Global Confirmed, 64 Campaign Confirmed, 32 Campaign Proposed, three 4,096-byte rendered sections, 48 one-part messages with the exact eight four-message and eight two-message cycles, 32 1,024-byte text parts, eight 2,048-byte tool-argument parts, eight 2,048-byte tool-result parts, 65,536 total part bytes, 24 ordered tools, 32,768 canonical schema bytes, a 16,384-byte ordinary system prompt, pinned `o200k_base` from `Microsoft.ML.Tokenizers` 2.0.0 with its literal token count, 32 labeled artifacts, the literal eight-to-nine generation Bloom transition, 4,096-byte pages, one Pending-to-Begun claim, and 59,999 completed exact disclosure receipts.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantBenchmarkContractTests|FullyQualifiedName~CovenantEvidenceScriptTests"`. Expected: compilation or file-not-found failure because the benchmark project, manifest, evidence recorder, and ignore rules do not exist. Record the command and expected failure without capturing environment values.
- [ ] Add the Native AOT console project, source-generated workload and validation DTOs, literal UUIDs, timestamps, ASCII payload generators, every provider option, expected token count, component hashes, logical database digest, and overall workload fingerprint. Add a test-project `ProjectReference` to the benchmark project and declare `<InternalsVisibleTo Include="RetroDownfall.Arcanum.Tests" />` in the benchmark project so Tasks 2 through 4 can test internal statistics, gate, fixture, runner, report, and baseline types without making them product API. Create `Program.cs` now with an AOT-safe `Main` that accepts only `--validate-workload`, loads the embedded manifest through `CovenantBenchmarkJsonContext`, validates every literal and digest, writes one source-generated JSON validation document, and returns 0. Missing or unknown arguments return 2.
- [ ] Add the strict evidence recorder and exact `.gitignore` entries. Bound each field and record only the explicit command arguments passed after `--`; never enumerate the process environment. Make concurrent append safe, flush before returning, preserve the wrapped command's exit code, and reject symlink or traversal escapes from `artifacts/covenant/issue-74`.
- [ ] Rerun the focused command. Expected: every count, byte length, digest, token expectation, recorder contract, and ignore assertion passes.
- [ ] Add the project to the solution and run `dotnet build src/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj`. Expected: AOT-compatible build succeeds with no first-party trim warning.

## Task 2: Implement exact measurement and gate mathematics

**Files:**

- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantBenchmarkMeasurements.cs`
- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantBenchmarkStatistics.cs`
- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantBenchmarkGate.cs`
- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantPcg32.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Performance/CovenantBenchmarkStatisticsTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Performance/CovenantBenchmarkGateTests.cs`

- [ ] Write literal nearest-rank percentile, PCG32 sequence, randomized interleaving, paired-batch resampling, 10,000-replicate ratio interval, exact rounding, control-noise, negative-correction, no-clamping, no-sample-discard, strict absolute-ceiling boundary, bootstrap, schema mismatch, and comparable-regression vectors. Comparable vectors must prove that failure requires both observed candidate p95 ratio `> 1.10` and 95-percent interval lower bound `> 1.05`; equality at either threshold does not satisfy that conjunction, while every absolute ceiling remains independently authoritative.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantBenchmarkStatisticsTests|FullyQualifiedName~CovenantBenchmarkGateTests"`. Expected: compilation fails because statistics and gate types are absent.
- [ ] Implement nearest-rank calculations over every sample, PCG32 seed `0x415243414E554D74`, randomized paired ten-sample batch ordering, 10,000 paired-batch bootstrap replicates, p95 ratios, 2.5th and 97.5th nearest-rank interval bounds, the exact `1.10` and `1.05` conjunction, and fail-closed workload, schema, baseline-version, and fingerprint comparison.
- [ ] Implement exact noise failures: control `p95 - p5 > 8 KiB`, control median absolute deviation `> 2 KiB`, or more than one percent negative paired corrections. Preserve raw enabled, empty-harness, ordinary-baseline, and Covenant-incremental distributions separately, clamp no value, and discard no sample.
- [ ] Rerun the focused command. Expected: every literal math and decision vector passes.
- [ ] Run the same focused command ten times through the evidence recorder. Expected: all ten source-generated decision documents have the same digest and every run passes.

## Task 3: Exercise the exact production benchmark seams

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj`
- Modify: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/Program.cs`
- Modify: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantBenchmarkJsonContext.cs`
- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantBenchmarkRunner.cs`
- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantBenchmarkFixture.cs`
- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantBenchmarkReport.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Performance/CovenantStructuralPerformanceTests.cs`

- [ ] Add red structural tests for one canonical command, `LIMIT 161`, scoped indexes, one history-plus-sensitivity command, zero N+1 labels, no duplicate prompt or fragment strings, one short SQLite snapshot, one linear linker pass, no FTS5/compiler/model/background work in materialization, exact operation leases and pre-dispatch revalidation, no retry database or secret-store calls, and disclosure acknowledgement before the dispatch seam.
- [ ] Add untimed large-shape cases for 5,000 Session messages, 10,000 stateless messages, 256 tools, and 1,024 content parts with bounded small payloads. Add maximum-byte compiler and prompt cases proving at most one final UTF-16 prompt plus bounded descriptors and no retained second full-content copy.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~CovenantStructuralPerformanceTests`. Expected: fixture and seam assertions fail because the benchmark runner does not exist.
- [ ] Add direct project references from the benchmark host to `RetroDownfall.Arcanum.Api` and `RetroDownfall.Arcanum.Infrastructure`, retaining the existing dependency direction and their transitive Core reference. The fixture must instantiate the exact production schema installer, initialized SQLite connection factory, Covenant context provider/store/linker, prompt builder, provider-call freezer, sensitivity calculator, and `DisclosureGroupCommitter`; it may fake only the final network and operating-system boundaries.
- [ ] Build warmed encrypted fixtures through those production services. Use a closed scenario enum for pure linker/render, warm load/link/render, admission, enabled end-to-end, disabled stateless, disabled untainted Session, disabled tainted Session, empty-tail disclosure, 59,999-row disclosure, eight-writer disclosure, and one 256-row bounded fold. Disable background workers and automatic compaction during every hot sample. Prove the 60,000th append contains no fold work, then measure fold separately.
- [ ] Lock the sample matrix in tests and source-generated reports: pure linker/render uses 25 warmups and 500 measured samples; warm load/link/render uses 25 warmups and 200 sequential measurements; admission uses 200 measurements; enabled end-to-end uses 25 warmups and 200 measurements; eight-writer disclosure uses exactly 200 acknowledged receipts per writer. Every report records the configured and observed counts and rejects a missing or extra sample.
- [ ] Measure synchronous code with `GC.GetAllocatedBytesForCurrentThread` plus thread-identity proof. Measure Task-based cases in isolated processes using `GC.GetTotalAllocatedBytes(precise: true)` after full GC quiescence and an immediately adjacent byte-identical control for every sample batch. Preserve raw, control, ordinary-baseline, corrected, and incremental distributions without clamping or discarding values.
- [ ] Extend the AOT-safe `Program` with `--measure --output <validated-path> --candidate-tree <64-hex-fingerprint>` and `--evaluate --candidate <report> --baseline <report-or-bootstrap> --output <validated-path>`. Register every command, scenario, measurement, machine-profile, raw-sample, summary, gate-decision, and error DTO in `CovenantBenchmarkJsonContext`. Unknown, duplicate, missing, oversized, or path-escaping arguments return 2; a measured gate failure returns 1; success returns 0. Reports contain repository-relative or opaque identities only.
- [ ] Rerun the focused command. Expected: every production-seam, command-count, query-plan, sample-count, allocation-boundary, and data-shape assertion passes.
- [ ] Run `dotnet run --project src/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj -- --validate-workload`. Expected: one source-generated JSON report with the approved workload fingerprint and no additional output.

## Task 4: Add the benchmark command and absolute gates

**Files:**

- Create: `scripts/benchmark-covenant.sh`
- Create: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/baselines/covenant-workload-v1-mac17-4.json`
- Modify: `src/RetroDownfall.Arcanum.Covenant.Benchmarks/CovenantBenchmarkJsonContext.cs`
- Modify: `.github/workflows/ci.yml`
- Extend: `tests/RetroDownfall.Arcanum.Tests/Packaging/ContinuousIntegrationWorkflowTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Packaging/CovenantBenchmarkScriptTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Performance/CovenantBenchmarkGateTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Performance/CovenantBenchmarkBaselineTests.cs`

- [ ] Add script contract tests for `--gate`, `--verify-staged-tree`, `--verify-commit-tree`, current-RID Native AOT publish and execution, candidate-tree fingerprinting, temporary merge-base worktree isolation, workload-v1 bootstrap, comparable mode, missing-host and fingerprint-mismatch refusal, validated cleanup, raw report retention, exact evidence paths, reference-machine and power qualification, non-reference timing behavior, and nonzero failure propagation.
- [ ] Add strict boundary tests for every approved gate: pure linker p95 `<250 us`; warm load/link/render p95 `<5 ms`; enabled provider stage p95 `<8 ms`; pure allocation p95/max `<64/72 KiB`; warm allocation p95/max `<256/288 KiB`; enabled raw allocation p95/max `<384/448 KiB`; enabled incremental allocation p95/max `<256/288 KiB`; disabled stateless median/p95 `<10/25 us` and maximum incremental allocation `<1 KiB`; disabled untainted Session byte identity, zero optional store commands, zero tools, and indexed sensitivity `LEFT JOIN`; disabled tainted Session p95 `<6 ms` and raw allocation p95/max `<256/288 KiB`; uncontended `synchronous=FULL` acknowledgement p95 `<4 ms` for both empty-tail and 59,999-row fixtures; eight writers times 200 receipts with acknowledgement p95 `<6 ms`, throughput `>=1,500/s`, and WAL growth `<=16 MiB`; and a separate 256-row fold p95 `<25 ms` with no append-transaction fold work. Test exact equality at every strict ceiling as failure and equality at inclusive floors or caps as success.
- [ ] Add baseline tests proving the v1 artifact contains no raw sample, secret, absolute path, or Covenant content; binds workload version and fingerprint, schema fingerprint, reference profile, toolchain, non-self-referential `BenchmarkInputFingerprint`, gate schema, every approved threshold and accepted summary; and has a source-generated canonical digest. The input fingerprint covers the benchmark's complete build and execution inputs and excludes only the baseline itself, ignored evidence, Git metadata, and documentation. A merge-base without a manifest is legal only for this reviewed v1 bootstrap. Once v1 exists, a missing host, baseline, workload, schema, or fingerprint fails closed.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantBenchmarkScriptTests|FullyQualifiedName~ContinuousIntegrationWorkflowTests|FullyQualifiedName~CovenantBenchmarkGateTests|FullyQualifiedName~CovenantBenchmarkBaselineTests"`. Expected: tests fail because the script, complete gate matrix, baseline contract, and CI job are absent.
- [ ] Implement a strict shell script that publishes and executes the current-RID AOT host, computes the canonical benchmark-input and candidate-tree fingerprints, fingerprints the environment, validates the workload, selects v1 bootstrap or comparable mode, co-runs matching revisions in randomized interleaved ten-sample batches, invokes the exact gate, and cleans only a temporary worktree whose path, marker, and expected revision it has revalidated. Write raw samples and the gate document below `artifacts/covenant/issue-74/benchmark/`; the baseline binds only `BenchmarkInputFingerprint`, final reports record both fingerprints plus the base commit, and an immutable-commit CI run additionally records the candidate commit.
- [ ] Implement machine qualification as data, not an operator assertion. Exact `Mac17,4`, Apple M5 10-core, 16 GiB, macOS 27 arm64, .NET 10.0.10, external power, and Low Power Mode disabled sets `ReferenceTimingQualified=true` and enforces wall-clock ceilings. Every other profile sets it false, enforces structural and allocation gates, reports timing, returns a typed non-reference result, and cannot create or replace the accepted baseline.
- [ ] Add the pinned `covenant-performance` CI job with the approved reference-runner label, SDK, AOT toolchain, workload, power checks, candidate-tree and commit binding, raw synthetic sample upload, gate report upload, and stable required-check name. Ensure the workflow runs for pull requests, `codex/**` branch pushes, main pushes, and explicit dispatch, always checking the triggering immutable commit. The job validates the checked-in v1 baseline and fails closed on profile drift. CI artifacts use the master plan's exact benchmark evidence directory and remain uncommitted.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantBenchmarkScriptTests|FullyQualifiedName~ContinuousIntegrationWorkflowTests|FullyQualifiedName~CovenantBenchmarkGateTests"`. Expected: script, gate, and workflow contracts pass before the accepted repository baseline exists.
- [ ] Run `./scripts/benchmark-covenant.sh --gate`. Expected on the exact reference machine: every structural, allocation, concurrency, comparative, and wall-clock gate passes, and the script writes the content-free accepted v1 baseline plus ignored raw evidence. Expected elsewhere: all portable gates pass, the report says `ReferenceTimingQualified=false`, no baseline is created, and integration remains blocked until the same non-self-referential `BenchmarkInputFingerprint` is qualified on the approved reference machine.
- [ ] After the approved reference machine has produced the accepted artifact for the exact `BenchmarkInputFingerprint`, run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantBenchmarkScriptTests|FullyQualifiedName~ContinuousIntegrationWorkflowTests|FullyQualifiedName~CovenantBenchmarkGateTests|FullyQualifiedName~CovenantBenchmarkBaselineTests"`. Expected: the accepted baseline at `src/RetroDownfall.Arcanum.Covenant.Benchmarks/baselines/covenant-workload-v1-mac17-4.json` is canonical, content-free, benchmark-input-bound, and the complete Task 4 suite is green.

## Task 5: Lock shipping-RID Native AOT behavior

**Files:**

- Create: `src/RetroDownfall.Arcanum.Covenant.AotSmoke/RetroDownfall.Arcanum.Covenant.AotSmoke.csproj`
- Create: `src/RetroDownfall.Arcanum.Covenant.AotSmoke/Program.cs`
- Create: `src/RetroDownfall.Arcanum.Covenant.AotSmoke/CovenantAotSmokeJsonContext.cs`
- Link as copied publish content: `tests/RetroDownfall.Arcanum.Tests/TestData/Covenant/Unicode17/NormalizationTest.nfc.bin`
- Modify: `RetroDownfall.Arcanum.slnx`
- Modify: `scripts/verify-native-sqlcipher.sh`
- Modify: `scripts/verify-aot-il-warnings.sh`
- Create: `scripts/verify-covenant-rid-evidence.sh`
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/private-beta-release.yml`
- Modify: `.github/workflows/build-windows-x64.yml`
- Modify: `.github/workflows/release-macos-arm64.yml`
- Create: `tests/RetroDownfall.Arcanum.Tests/Packaging/CovenantAotSmokeContractTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Packaging/CovenantShippingRidEvidenceTests.cs`

- [ ] Link the exact checked-in `NormalizationTest.nfc.bin` corpus into the smoke project as copied publish content. Load its bytes without runtime discovery or reflection and pass them to the sole `CovenantCompilerCorpus.Run(ReadOnlySpan<byte>)` entry point. Contract tests pin the project item, publish inclusion, exact file length and hash, runner row and assertion counts, and aggregate, and refuse absent, truncated, corrupt, or substituted corpus content.
- [ ] Add tests that the smoke host covers compiler golden corpus, canonical digests, source-generated API and MCP serialization, disabled stateless byte identity, enabled provider-stage preparation, SQLCipher load, encrypted create/reopen, wrong-key rejection, compatibility-fixture open, cipher pragmas, FTS secure-delete, rank-1 integrity, dynamic extension refusal, source-generated report output, and exact candidate commit/tree binding.
- [ ] Assert the shipping RID inventory is exactly `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, and `win-x64`; both native verification scripts keep all five in their closed inventories; `verify-native-sqlcipher.sh --all` verifies every asset and executes runtime checks only for the current RID, while its CI-only `--rid <shipping-rid>` runs target-native compatibility and testfixture checks; `verify-aot-il-warnings.sh` exposes matching `--current-rid` and CI-only `--rid <shipping-rid>` execution modes; and the CI matrix maps each RID to a native runner that executes, rather than cross-compiles, the smoke binary and SQLCipher testfixture.
- [ ] Add evidence-verifier tests that accept exactly five source-generated reports named `osx-arm64.json`, `osx-x64.json`, `linux-x64.json`, `linux-arm64.json`, and `win-x64.json`; require one common candidate commit, tree fingerprint, workload fingerprint, schema fingerprint, native manifest digest, and smoke schema; require zero first-party IL/AOT warning and every runtime/compatibility/testfixture assertion green; and reject duplicates, missing RIDs, stale commits, unknown fields, absolute paths, or raw content.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantAotSmokeContractTests|FullyQualifiedName~CovenantShippingRidEvidenceTests"`. Expected: tests fail because the smoke project, explicit local/CI modes, five-RID matrix, and evidence verifier are absent.
- [ ] Add the source-generated smoke host with closed `--smoke --rid`, `--verify-evidence --directory --expected-commit --expected-tree`, and validation modes. Register its report, aggregate, and bounded error DTOs in `CovenantAotSmokeJsonContext`; unknown, duplicate, missing, oversized, or path-escaping arguments return 2. Modify `verify-native-sqlcipher.sh` to separate all-asset inventory/rebuild checks from current-RID execution and add the closed CI `--rid` execution mode. Modify `verify-aot-il-warnings.sh` so `--current-rid` publishes and executes the host-RID smoke binary and rejects first-party trim/AOT warnings, while `--rid` accepts only one of the five closed shipping RIDs and is used on that RID's native CI runner. Retain all five values in both scripts' inventories; no single local invocation claims to have executed foreign-RID binaries.
- [ ] Add the required `covenant-native-aot` five-RID CI matrix using the existing shipping runner mapping and stable per-RID required-check names. Ensure it runs for pull requests, `codex/**` branch pushes, main pushes, and explicit dispatch against the triggering immutable commit. Each job runs native SQLCipher provenance/runtime/compatibility/testfixture checks, `verify-aot-il-warnings.sh --rid`, and the AOT smoke binary, then uploads its report as `artifacts/covenant/issue-74/native/<rid>.json`. Wire the same smoke step into release workflows so release and pre-integration behavior cannot diverge.
- [ ] Implement `verify-covenant-rid-evidence.sh` as a strict argument and path wrapper around the smoke host's source-generated `--verify-evidence` mode. It accepts the evidence directory plus expected immutable candidate commit and tree fingerprint, emits one content-free aggregate verification document, and returns nonzero on any mismatch. It does not parse JSON through reflection, `jq`, or shell text matching.
- [ ] Rerun the focused command. Expected: smoke project, workflow inventory, execution-mode, and evidence-schema tests pass.
- [ ] Run `./scripts/verify-aot-il-warnings.sh --current-rid`. Expected: the current-RID AOT publish and smoke execution pass with zero first-party warnings. Record this local result without claiming the other four runtime executions; Task 11 owns their immutable-commit CI evidence.

## Task 6: Enforce coverage and test-category contracts

**Files:**

- Modify: `tests/RetroDownfall.Arcanum.Tests/coverage.runsettings`
- Modify: `scripts/coverage.sh`
- Modify: `scripts/coverage_threshold.py`
- Modify: `scripts/coverage_threshold.ps1`
- Modify: `scripts/coverage_threshold_test.py`
- Extend: `tests/RetroDownfall.Arcanum.Tests/Performance/PerfCategoryExclusionTests.cs`
- Extend: `tests/RetroDownfall.Arcanum.Tests/Performance/ArcanumPerfBaselineTests.cs`

- [ ] Add red tests proving benchmark and smoke harness assemblies are excluded, production Covenant assemblies are included, wall-clock tests carry the Perf category, and no Covenant production namespace is globally excluded.
- [ ] Run `python3 scripts/coverage_threshold_test.py` and `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~PerfCategoryExclusionTests|FullyQualifiedName~ArcanumPerfBaselineTests"`. Expected: new coverage, category, and baseline assertions fail.
- [ ] Update runsettings, tiered thresholds, exclusion lists, and shell/PowerShell parity without lowering existing production thresholds.
- [ ] Rerun both focused commands. Expected: script parity, category, and baseline tests pass.
- [ ] Run `./scripts/coverage.sh --threshold`. Expected: every tier passes and Covenant production coverage appears in the report.

## Task 7: Update architecture, API, CLI, configuration, and inventories

**Files:**

- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.API.md`
- Modify: `docs/Arcanum.Command.Reference.md`
- Modify: `docs/Arcanum.CHAT-LOOP.md`
- Modify: `docs/Compendium.README.md`
- Modify: `docs/Arcanum.README.md`
- Create: `docs/Arcanum.COVENANT.ROADMAP.md`
- Modify: `docs/Arcanum.ConstraintInventory.json`
- Modify: `docs/Arcanum.CommandMap.json`
- Create: `tests/RetroDownfall.Arcanum.Tests/Documentation/DocumentationContractTests.cs`
- Extend: `tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs`

- [ ] Add red documentation tests for every route name, CLI command, config key, error family, schema tier, LRO kind, MCP tool, sensitivity boundary, reset status, native RID, benchmark report section, chat-loop authority transition, and inherited #75 through #78 contract. Add tests named `Machine_readable_covenant_inventories_parse`, `Changed_covenant_docs_follow_repository_style`, and `Changed_covenant_doc_links_resolve`; the style test rejects U+2014 in every changed Covenant document, and the inventory tests parse JSON rather than inferring validity from `git diff --check`.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~DocumentationContractTests|FullyQualifiedName~CliSurfaceTests"`. Expected: inventory and documentation assertions fail.
- [ ] Document the implemented architecture, exact persistence and prompt contracts, threat model, degradation, local erasure boundary, provider disclosure, API bodies/statuses, CLI confirmation and JSON shapes, `Arcanum:Features:Covenant`, diagnostics, recovery, benchmark schema, workload fingerprint, and accepted baseline contract. Update `Arcanum.CHAT-LOOP.md` with claim, maintenance, snapshot, attempt, tool, publication, and taint transitions. Task 10 writes the final measured outcome summary after review remediation.
- [ ] Add `Arcanum.COVENANT.ROADMAP.md` with the exact approved inheritance: #75 consumes immutable versions and compact committed-turn receipts for temporal validity, dependency-aware supersession, transformation receipts, and counterfactual credit assignment; #76 adopts canonical Campaign binding for Saga and Lexicon typed scopes and later scope masks while semantic retrieval remains discovery-only; #77 makes Campaign summaries revisioned compiled derived objects with source receipts, generation identity, and a Session-bound rollup revision; #78 targets exact immutable versions and compiled hashes for review, confirmation, correction, forget, pin, scope masks, and keyed suppression fingerprints. Keep RAPTOR-style hierarchy in the later discovery and summarization plane, with no path to Confirmed authority.
- [ ] Record the six approved prerequisite and research issue contracts in the roadmap: reusable raw-SQL feature-schema evolution with resumable backfills; Dynamic Context Injection v2 with secure provider-cacheable prefix measurements; typed Covenant operational defaults that exclude security-policy authority; a counterfactual memory evaluation lab; least-authority subagent delegation capsules with provenance; and bitemporal validity plus dependency-aware durable-memory claims. Task 11 reconciles each contract with an idempotent GitHub issue.
- [ ] Update machine-readable constraint and command maps from the final surfaces, preserving deterministic ordering and formatting.
- [ ] Rerun the focused command. Expected: all documentation and inventory assertions pass.
- [ ] Run `dotnet format RetroDownfall.Arcanum.slnx --verify-no-changes --no-restore`, run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~DocumentationContractTests`, run `bash -n scripts/benchmark-covenant.sh scripts/record-covenant-evidence.sh scripts/verify-covenant-rid-evidence.sh scripts/verify-native-sqlcipher.sh scripts/verify-aot-il-warnings.sh`, and run `git diff --check`. Expected: C# formatting is stable, machine-readable inventories parse, changed Covenant docs contain no em dash or broken local link, shell scripts parse, and Git reports no whitespace error or conflict marker.

## Task 8: Run focused fault-domain suites

**Files:**

- No production files.
- Record content-free results under `artifacts/covenant/issue-74/verification/`. Task 10 copies only schema/workload fingerprints, report digests, aggregate pass counts, documented skip counts, and final outcomes into `docs/Arcanum.DESIGN.md`.

- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "(FullyQualifiedName~Covenant|FullyQualifiedName~SqlCipher|FullyQualifiedName~GrimoireSchema|FullyQualifiedName~LongRunningOperationRecoveryRegistry)&Category!=Perf"`. Expected: zero failures and no wall-clock benchmark test under ordinary parallel load.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "(FullyQualifiedName~WizardIntelligenceProvider|FullyQualifiedName~TurnEngine|FullyQualifiedName~GrimoireTurnWriter|FullyQualifiedName~Mcp|FullyQualifiedName~Ward)&Category!=Perf"`. Expected: zero failures.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "(FullyQualifiedName~Backup|FullyQualifiedName~DataRetention|FullyQualifiedName~Campaign|FullyQualifiedName~Session)&Category!=Perf"`. Expected: zero failures.
- [ ] Run `dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --filter "Category!=Perf"` and `dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj --filter "Category!=Perf"`. Expected: both complete with zero failures and only their documented skips.
- [ ] After each command, verify the evidence recorder wrote the exact command, exit code, aggregate pass/skip/fail counts, base commit, and candidate-tree fingerprint to the issue-74 evidence root.
- [ ] If any command fails, apply the systematic-debugging skill, add the smallest reproducing red test, fix the production cause, and rerun every affected focused command.

## Task 9: Run the complete local green gate and bind the candidate tree

**Files:**

- No production files unless a failing gate exposes a defect.

- [ ] Run `dotnet format RetroDownfall.Arcanum.slnx --verify-no-changes --no-restore` and `dotnet build RetroDownfall.Arcanum.slnx`. Expected: formatting is stable, build exits zero, and there is no new first-party warning.
- [ ] Run `dotnet test RetroDownfall.Arcanum.slnx --filter "Category!=Perf"`. Expected: all Arcanum, Compendium, and The Forge ordinary tests pass with only the repository's documented skips.
- [ ] Run `python3 scripts/coverage_threshold_test.py` and `bash -n scripts/benchmark-covenant.sh scripts/record-covenant-evidence.sh scripts/verify-covenant-rid-evidence.sh scripts/verify-native-sqlcipher.sh scripts/verify-aot-il-warnings.sh`. Expected: threshold parity and shell syntax pass.
- [ ] Run `./scripts/verify-native-sqlcipher.sh --all`. Expected: all five checked-in assets pass source, signature/hash, SBOM, runtime-target, dynamic-dependency, and reproducible-build inventory gates; only the current-RID binary is executed locally for runtime, compatibility, and testfixture assertions. The report names which checks are asset-wide and which are host-executed.
- [ ] Run `./scripts/benchmark-covenant.sh --gate`. Expected: workload-v1 and schema fingerprints match, every structural/allocation/disclosure/comparative gate passes, and the ignored report binds the current benchmark-input and candidate-tree fingerprints. `ReferenceTimingQualified=true` plus every wall-clock gate is required for merge qualification; a non-reference result is portable evidence only and keeps integration blocked pending approved-reference evidence for the same `BenchmarkInputFingerprint`.
- [ ] Run `./scripts/coverage.sh --threshold`. Expected: every tier passes and Covenant production code is included.
- [ ] Run `./scripts/verify-aot-il-warnings.sh --current-rid`. Expected: the current-RID publish and smoke binary execute with zero first-party IL/AOT warning. Five-RID native execution remains a Task 11 required-CI gate.
- [ ] Run `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter FullyQualifiedName~DocumentationContractTests`, `git diff --check`, and `git status --short --branch`. Expected: inventories parse, changed Covenant docs and links satisfy repository style, only the approved issue #74 change set is present, and ignored evidence does not appear in status.
- [ ] Run `git check-ignore artifacts/covenant/issue-74/tdd/commands.ndjson artifacts/covenant/issue-74/native artifacts/covenant/issue-74/benchmark artifacts/covenant/issue-74/verification` and `git ls-files 'artifacts/covenant/issue-74/**'`. Expected: every evidence path is ignored and the tracked-file query prints nothing. Fail if any evidence path is staged or tracked.
- [ ] Persist content-free build, ordinary-test, native, benchmark, coverage, AOT, documentation, lint, diff, and status summaries under `artifacts/covenant/issue-74/verification/`. Verify their common candidate-tree fingerprint and base commit match the benchmark gate report.

## Task 10: Complete independent review and remediate findings

**Files:**

- Modify only files implicated by verified findings.

- [ ] Request one specification-compliance review against the approved design and all five plans.
- [ ] Request one code-quality review focused on dependency direction, clarity, concurrency, query shape, and Native AOT.
- [ ] Request one .NET security review focused on authentication ordering, authority laundering, SQL parameters, cursor/token crypto, sensitive logging, stream buffering, external egress, reset, and restore.
- [ ] Request one performance review focused on hot-path allocations, command count, lock ordering, group commit, outbox/rebuild boundedness, and benchmark validity.
- [ ] Request one repository-integration review focused on complete diff scope, unrelated user work, generated native assets, source manifests, SBOMs, package locks, solution/project references, workflow triggers and required-check names, ignored evidence, machine-readable inventories, documentation links, and fast-forward safety.
- [ ] For each actionable finding, verify the claim against code and contract, add a focused failing test, implement the smallest correction, and rerun the affected fault-domain suite.
- [ ] Rerun Task 9 in full. Expected: the complete local green gate remains green after review remediation and produces one coherent evidence set.
- [ ] Update only the benchmark evidence section of `docs/Arcanum.DESIGN.md` with content-free schema, workload, baseline, and benchmark-input fingerprints, report digests, aggregate command outcomes, documented skips, reference-profile qualification, and explicit local-current-RID versus required post-commit five-RID CI status. Do not embed `CandidateTreeFingerprint`, because the document is part of that tree. Include no raw samples, local path, machine secret, content, API key, or provider payload.
- [ ] Rerun the Task 7 documentation tests and lint commands, then rerun Task 9 in full without changing documentation afterward. Expected: the final candidate-tree fingerprint covers the completed documentation, every local gate is green, all review findings are resolved, and the ignored evidence set matches that final fingerprint.

## Task 11: Integrate one clean feature commit and push main

**Files:**

- No new files beyond the approved implementation.

- [ ] Inspect `git diff --stat`, `git diff --name-status`, and the complete diff. Confirm no unrelated user work, secret, local path, binary outside the native manifest, or temporary result is included.
- [ ] Fetch `origin/main` without modifying either worktree. Verify local `main`, `origin/main`, the feature branch merge base, and the base commit recorded by the final Task 9 evidence are identical. If any differs, stop integration, synchronize the feature work onto the new base without discarding user work, and rerun Tasks 9 and 10 in full.
- [ ] Verify GitHub issues #73 through #78 still contain exactly one `approved-covenant-design-2026-08-14` marker and that #74 remains open. Verify #75 through #78 retain the exact roadmap inheritance from Task 7.
- [ ] Reconcile the six prerequisite and research issues idempotently. Search by a stable Covenant follow-up marker before creating anything, create only absent issues, link each as a #73 subissue or tracked child, and record the resulting URLs in #73. The six exact scopes are raw-SQL feature-schema evolution with resumable backfills; Dynamic Context Injection v2; typed operational defaults excluding security authority; counterfactual memory evaluation; least-authority subagent delegation capsules; and bitemporal validity plus dependency-aware claims.
- [ ] Verify `git check-ignore` accepts every issue-74 evidence path, `git ls-files 'artifacts/covenant/issue-74/**'` prints nothing, and `git diff --cached --name-only -- 'artifacts/covenant/issue-74/**'` prints nothing. Confirm the checked-in workload manifest and accepted content-free v1 baseline are present in the approved change set.
- [ ] Stage only the approved issue #74 files. Inspect `git diff --cached --check`, `git diff --cached --stat`, `git diff --cached --name-status`, and the complete cached diff. Run `./scripts/benchmark-covenant.sh --verify-staged-tree artifacts/covenant/issue-74/benchmark/gate.json`. Expected: the staged tree exactly matches the final measured candidate-tree fingerprint and contains no ignored evidence or unrelated path.
- [ ] Commit once with subject `feat(covenant): add governed durable memory` and body lines `Refs #74` and `Refs #73, #75, #76, #77, #78`. Use no GitHub closing keyword. Run `./scripts/benchmark-covenant.sh --verify-commit-tree artifacts/covenant/issue-74/benchmark/gate.json`. Expected: the immutable feature commit tree matches the measured candidate tree exactly.
- [ ] Push only `codex/issue-74-covenant` to origin first. Wait for the required ordinary CI, `covenant-performance`, and five `covenant-native-aot` RID jobs to complete successfully for that exact commit. A queued or running check is incomplete. Download their artifacts into the ignored benchmark, native, and verification directories and run `verify-covenant-rid-evidence.sh` with the expected commit and tree fingerprint. Expected: the reference profile is qualified, all five exact RID reports are present and green, all report identities agree, and remote evidence contains no forbidden data.
- [ ] If any immutable-commit check fails, leave `main` unchanged and #74 open. Return to the smallest failing TDD slice, preserve a single squashed feature commit on the unmerged branch, rerun Tasks 9 and 10, and repeat every required remote check for the replacement commit.
- [ ] Fetch `origin/main` again and prove it still equals the verified base. Switch local `main` and run `git merge --ff-only codex/issue-74-covenant`; a merge commit is forbidden. If fast-forward is impossible, stop, reconcile the new base, and rerun the full verification and review sequence.
- [ ] On fast-forwarded local `main`, rerun the solution build, workload validation, current-RID AOT smoke, native manifest verification, candidate commit/tree evidence check, and `git status --short --branch`. Expected: the immutable verified commit is unchanged and the main worktree is clean apart from ignored evidence.
- [ ] Push `main` to `origin` without force. Verify remote `main` equals local `main`, then wait for every required main check to complete successfully. A check that merely started does not satisfy this gate.
- [ ] Add a content-free completion comment to #74 with the main commit, required-check URLs, five-RID aggregate evidence digest, benchmark/reference evidence digest, coverage outcome, and documentation links. Update #73 with #74 completion and the six follow-up issue links. Keep #75 through #78 open with their approved inherited contracts.
- [ ] Close #74 explicitly only after pushed `main`, remote commit equality, every required check, implementation, documentation, native assets, and evidence are verified. Confirm no commit message or earlier automation closed it prematurely.
