# Issue #257: Full-Host Grimoire Transition Proof and Qualification

**Status:** Approved in chat on 2026-09-11.

**Parent:** GitHub issue #239, "A Grimoire connection is enrolled in the Covenant drain only as a side effect."

**Delivery branch:** `grimoire-fixes`, fast-forwarded to `main` commit
`a381a5f7d7edbb471cb081c0303ca68e94e20b26` before this work began.

## 1. Objective

Issue #257 is the final integration and qualification child of #239. Issues #243 through #256 have
already delivered the authenticated offline-transition journal, typed lifecycle, host-wide admission,
connection acquisition enforcement, recovery, HTTP refusal, streaming quiescence, and hosted-producer
protection. This issue must prove those pieces compose in the real host, repair only defects the proof
exposes, publish the owning documentation, and qualify one unchanged reviewed SHA across every required
platform.

The delivered result must establish that an authenticated direct Covenant reset and an authenticated
healthy-catalog factory erasure can each close the running host without allowing a request, physical
open, stream, or hosted effect to cross the transition incorrectly. It must also establish the three
legal endings: commit and reopen, proven pre-effect rollback and reopen, and `KeepClosed` with recovery
ownership retained.

Completing #257 completes the remaining contract of #239. The original three concerns in #239 are now
represented by already-delivered children: #246 owns explicit EF/raw acquisition and the call-site
inventory; #253 through #256 own hosted-producer admission and deferral; #257 owns the integrated proof.

## 2. Current state and known discrepancies

### 2.1 Dependency and branch state

GitHub reports fifteen children under #239. Issues #243 through #256 are closed with reason
`COMPLETED`; #257 is the only open child. Issue #242 remains an independent Windows symptom report and
issue #265 remains a future .NET 11 compatibility cleanup. Neither is a dependency or part of this
change.

At task start, remote `grimoire-fixes` was a strict ancestor of `main` and was three commits behind it.
The branch was fast-forwarded to the exact `main` SHA above before the #257 worktree was created. The
primary checkout remains on its older local `grimoire-fixes` ref with unrelated unstaged documentation
work and is not an implementation workspace.

The original #257 wording required the complete #239 tree to remain off `main` until this child had
qualified it. That state no longer exists: the #243-#256 tree had already been merged and released when
this task began. On 2026-09-11 the issue owner explicitly approved the non-destructive replacement
criterion in chat: start from current `main`, add only the #257 follow-up tree, freeze one reviewed and
qualified SHA, and fast-forward that identical SHA through `grimoire-fixes` to `main`. Rewriting
published history is prohibited. Before delivery, record this accepted deviation in #257 so the issue's
GitHub record and the implemented acceptance criterion agree.

### 2.2 Missing integrated proof

Lower-level suites already prove individual admission, connection, transition, recovery, stream, and
worker contracts. They do not compose an authenticated transition request with a real hosted SQLCipher
catalog and concurrent request/open/stream/worker participants. The planned
`tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.cs` artifact does not exist.

### 2.3 `KeepClosed` mismatch to prove RED-first

The approved #239 design says that a `KeepClosed` ending releases no ordinary admission, spends the
Covenant lease's `KeepClosed` disposition, leaves the authenticated journal active, and lets startup
reconstruct closed gate ownership. `CovenantErasureCoordinator.GrimoireDispositionFor` currently maps
every non-commit Covenant disposition, including `KeepClosed`, to `RollbackAndReopen`. A later comment
in the same coordinator says a parked journal and reopened gate would disagree and therefore the park
must take the gate with it.

This is a static inconsistency, not yet accepted as a production defect. A focused test must first
demonstrate that the current runtime reopens ordinary admission after a `KeepClosed` outcome. Only that
observed RED authorizes changing the coordinator.

### 2.4 Documentation drift

`docs/Arcanum.DESIGN.md` still describes the authenticated transition journal foundation as not wired
into startup or a handler, even though later sections and the current implementation document and run
typed startup recovery. The root README was rewritten on `main`, so its #257 update must fit that current
public orientation rather than restore the former maintenance paragraph verbatim.

## 3. Non-negotiable invariants

The implementation and tests preserve these existing contracts:

1. `ICovenantOperationGate` remains the durable destructive-operation authority.
   `IGrimoireConnectionAdmissionGate` remains process-local live-Grimoire admission.
2. Authentication runs before Grimoire admission. Admission runs before installation-reset recovery,
   Covenant pre-binding, body-size enforcement, model binding, and endpoint execution.
3. Ordinary requests use request leases; hosted durable units use work leases; native opens use opening
   tickets; provider/filesystem work begins only inside an external-effect group.
4. Stage one closes new request/work admission and drains already-admitted request/work/effect
   lifetimes. Stage two closes physical-open admission, resolves in-flight opens by generation, drains
   enrolled handles, and clears pools before closed authority is issued.
5. A physical open that loses the generation race is closed and accounted for before maintenance gains
   exclusive ownership. Source classification is never runtime authority.
6. Maintenance refusal is expected control flow. `/api/**` returns a sanitized source-generated
   `ApiResponse<string>` with code `Grimoire.MaintenanceUnavailable`; `/v1/**` returns the OpenAI error
   type `service_unavailable` and code `grimoire_maintenance`. Neither leaks owner, operation, phase,
   generation, path, or native detail, and neither is logged at Error.
7. The five declared quiesceable SSE routes finish the frame in progress and stop between frames.
   Finite and billable streams that already won admission drain normally and are never maintenance-
   cancelled.
8. Maintenance denial is deferral, not failure. A refused hosted unit creates no scope, calls no
   provider, mutates no file or database row, consumes no attempt or billing, records no failure, and
   advances no watermark.
9. `CommitAndReopen` opens exactly the next generation after verification, database reconciliation,
   optional parent receipt, Covenant disposition, and journal retirement.
10. Proven pre-effect `RollbackAndReopen` terminalizes the operation as failed without applying the
    destructive effect, retires the journal, and reopens the original generation's successor.
11. `KeepClosed` publishes the exact blocker and resume binding, spends both closures as `KeepClosed`,
    retains the authenticated journal and owner, and admits no ordinary request, work, or physical open.
12. Startup authenticates and adopts the exact active journal before readiness. It may reopen only
    after the handler converges, exact database/parent reconciliation succeeds, and retirement completes.
13. V4 Covenant and V2 factory launch payloads remain the only current write contracts. V3/V1 remain
    strict legacy-read contracts and never infer missing target authority.
14. No public endpoint, route shape, DTO, CLI verb, configuration key, database schema object, or
    migration is added.

## 4. Chosen approach

### 4.1 Alternatives considered

The chosen approach is a deterministic in-process full-host harness built on
`ArcanumWebApplicationFactory`. It uses the production ASP.NET pipeline, production coordinator,
SQLCipher database, pooling/enrolment path, hosted services, and durable journal while replacing only
external providers and clocks/barriers needed to make the races deterministic.

A collection of additional component tests was rejected as the primary proof. It would be faster, but
the existing component suites are already strong and another collection would not establish that the
real middleware, scopes, streams, workers, coordinator, and database share one admission gate.

An external black-box process suite was also rejected as the primary proof. It would increase realism
at the cost of nondeterministic process scheduling, fragile port coordination, slow fault injection,
and little ability to prove negative effects. External-process proof remains appropriate for the final
Native AOT Ollama qualification, where the published executable itself is the subject.

### 4.2 Harness structure

Create `GrimoireMaintenanceAdmissionTests` under the API tests and keep its support types adjacent in
focused files if the harness would otherwise obscure the scenarios. Extend
`ArcanumWebApplicationFactory` with a test-owned restartable-profile fixture rather than teaching
production code about tests. That fixture separates host disposal from final profile cleanup and gives
successive factory instances the exact same profile root, SQLCipher database and passphrase, managed-
file root, authenticated journal and anchors, installation identity, and API credential. Only the first
host seeds the database; every recovery host preserves all existing profile bytes and may not reseed or
overwrite the catalog. Every host receives the same `IOsCredentialStore` object and retained SQLCipher
passphrase source, not merely the same account names. Host-only disposal stops that host and clears its
pools without deleting or reseeding the profile. Only the fixture owner may delete the shared resources,
after the last recovery host exits.

The harness must:

- create the restartable isolated profile and an authenticated HTTP client for each host instance;
- set `Execution.MaxSseConnections` and `Execution.MaxSseConnectionsPerType` to exactly `8` through
  `SettingsOverride` before host construction, then prove all five quiesceable streams reached their
  barriers before launching maintenance;
- resolve the one production `IGrimoireConnectionAdmissionGate` shared by middleware, EF options,
  raw-open factories, transition coordination, streams, and hosted services;
- expose deterministic barriers through existing seams such as `ServiceOverrides`,
  `CovenantErasureFaultSeam`, `GrimoireScopedConsumerTestSeam`,
  `IGrimoireOrdinaryConnectionFactoryTestSeam.BeforeNativeOpenAsync`, a test EF
  `DbConnectionInterceptor`, provider doubles, manual time, and frame-aware stream sources;
- decorate the admission gate to observe stage-one and stage-two closure without changing its
  decisions, decorate the selected finite endpoint with `GrimoireScopedConsumerTestSeam` or a database
  command interceptor, and install explicit worker scope/effect/provider and per-route
  producer/enumerator/terminal/disposal observers;
- capture logs and assert transition/refusal paths produce neither Error nor unexpected Warning events;
- initiate both transitions only through their authenticated HTTP endpoints;
- observe durable records through fresh admitted scopes when open and through the exact owner/recovery
  path while closed;
- use `HttpCompletionOption.ResponseHeadersRead` for every streaming request;
- bound every wait and report the state/barrier that failed, but never use a sleep as synchronization;
  and
- own and dispose all clients, responses, enumerators, scopes, host services, temporary files, and
  credentials even when an assertion fails.

The harness is test infrastructure, not a new production abstraction. New production seams are allowed
only if a failing full-host test proves that the existing seam cannot deterministically observe a
load-bearing boundary. Such a seam must be inert when unset, internal, AOT-safe, and separately tested.

## 5. Full-host scenario matrix

The suite avoids a redundant Cartesian explosion while ensuring both transition entry points traverse
the complete shared lifecycle. Parameterization may share a scenario only when assertions remain
specific enough to identify which entry point and boundary failed.

### 5.1 Commit-and-reopen races for both entry points

Run one full participant cluster for direct Covenant reset and one for healthy-catalog factory erasure.
Before starting the authenticated transition request, establish:

- an admitted `GET /api/grimoire/stats` request paused by a database command interceptor after request
  admission and before its final Grimoire result is materialized;
- a finite `GET /api/sessions/{id:guid}/attachments/{attachmentId:guid}/content`
  (`DownloadSessionAttachment`) response backed by a controlled blocking plaintext stream;
- a billable `POST /v1/chat/completions` streaming inference paused after its provider effect begins;
- active readers on all five quiesceable SSE routes, each paused after one complete data frame;
- one queued `SessionAttachmentIndexingService` unit paused inside its real provider effect group; and
- one ordinary physical open paused before the native open: the direct-reset cluster starts an EF
  operation from a test-owned scope with no request/work lease and uses a test `DbConnectionInterceptor`
  registered after `CovenantConnectionEnrolmentInterceptor`, so the production interceptor has already
  acquired the opening ticket; the factory-erasure cluster pauses a raw `OpenFreshAsync` operation at
  `IGrimoireOrdinaryConnectionFactoryTestSeam.BeforeNativeOpenAsync`.

Raise the factory's default SSE limits before host construction, wait until all five route observers
confirm the following exact states, and only then launch maintenance:

| Route | Deterministic frame trigger | Revocation boundary | Required terminal/disposal proof |
| --- | --- | --- | --- |
| `/api/events/daemon` | injected daemon event | after the complete event frame | one `[DONE]`; producer, enumerator, and request scope disposed |
| `/api/events/mcp` | injected MCP event | after the complete event frame | one `[DONE]`; producer, enumerator, and request scope disposed |
| `/api/events/logs` | injected captured log event | after the complete event frame | one `[DONE]`; producer, enumerator, and request scope disposed |
| `/api/sessions/{id:guid}/stream` | persisted replay/live sentinel | after the complete sentinel frame | one `[DONE]`; producer, enumerator, and request scope disposed |
| `/api/apprentices/{id:guid}/chronicle` | buffered chronicle event | after the complete event frame | one `[DONE]`; producer, enumerator, and request scope disposed |

The transition must not pass stage one until the finite request, attachment download, billable stream,
and worker effect reach their durable dispositions and release their leases/scopes. The controlled
attachment stream must transmit its complete plaintext payload and dispose normally; the billable
stream must finish its provider answer, accounting, audit, terminal frame, and response normally.

After the decorated gate observes stage-one closure, a new authenticated `/api/grimoire/stats` request
and a new authenticated `/v1/models` request must receive their exact maintenance envelopes. The
database interceptor and endpoint observer must prove the `/api` request never reaches SQLite or its
handler; an OpenAI handler observer must prove the `/v1` request never executes. A newly signalled
attachment-indexing unit must remain in its original queue/deferred identity, while scope, effect,
provider, mutation, attempt/billing, failure-record, and watermark observers all remain unchanged.

Each quiesceable stream must finish the current complete frame, emit no partial or subsequent data
frame, observe its prior revocation token cancelled, stop its producer, dispose its enumerator and
request scope, emit exactly one route-owned `[DONE]`, and end cleanly.

The physical-open observer must prove its opening ticket exists before the native-open barrier is
released. When stage two changes generation, the losing native handle must be closed, its ticket must
reach one terminal disposition and never be reused, an exact pool clear must occur, enrolled-handle
count must reach zero, and no catalog `-wal` or `-shm` file may remain before closed authority issues.

The transition then commits, privately verifies the reopened catalog, publishes runtime authority,
reconciles the database, proves the standalone factory launch has no parent receipt, retires the journal,
and opens the next generation. Fresh ordinary HTTP work, a fresh work lease, and a fresh physical open
must report that generation and succeed. Interface-specific stale-state assertions replace any generic
notion of a stale capability:
the five prior stream revocation tokens stay cancelled; a retained prior-generation work lease cannot
begin a new effect group; the losing opening ticket remains terminal and cannot be reused; every owner
maintenance capability is refused after its disposition; and fresh acquisitions carry only the new
generation.

### 5.2 Proven pre-effect rollback

Run a compact parameterized host scenario for both authenticated entry points:
`POST /api/data/memory/reset` with its V4 Covenant launch and
`POST /api/data/factory-reset` with its standalone V2 factory launch and database reconciliation.
Because this is a healthy-catalog route rather than a nested installation-reset handoff, assert its
`ParentReceiptBindingDigest` is null and no parent receipt is reconciled. Inject a deterministic,
kind-specific failure after closed proof but before the first destructive effect. Assert that no
canonical or managed-file mutation lands, the operation reaches the exact failed terminal row and
digest, both gates spend `RollbackAndReopen` exactly once, the journal retires, the original dataset is
readable, and fresh ordinary work is admitted only in the next generation.

The existing phase/fault matrix already covers every crash boundary; this scenario proves the HTTP host
and admission postcondition for both payload kinds rather than duplicating that matrix.

### 5.3 `KeepClosed`

First drive a direct reset through its authenticated route and inject an uncertainty that is ineligible
for pre-effect rollback. This focused test asserts the process-local gate postcondition against the
current coordinator and must fail before production code changes. Once green, run the same compact
host-level outcome for healthy-catalog factory erasure with its V2 launch and kind-specific database
reconciliation evidence. The standalone factory launch must retain a null
`ParentReceiptBindingDigest` and produce no parent receipt.

The green contract is:

- the route returns the existing typed failure without internal detail;
- the operation remains `ReconciliationRequired` rather than being falsely terminalized;
- the journal records the exact `KeepClosed` phase, blocker, resume state, and binding;
- the Covenant and Grimoire closures both spend `KeepClosed` exactly once;
- new request, work, opening-ticket, `/api`, and `/v1` attempts remain refused; and
- owner disposal does not reopen the gate.

The minimal expected production correction is to preserve `KeepClosed` when translating the Covenant
disposition to the Grimoire closure. The test result, not this expectation, decides the edit.

### 5.4 Startup recovery, retirement, and next generation

Run restart recovery for both parked outcomes, preserving the exact test-owned profile between factory
instances. While the first host still runs, prove authenticated `/api` and `/v1` work remains refused;
then dispose only that host and remove the injected first-host fault without changing its journal,
credentials, database, managed files, or installation identity.

Start the second host on an observed task with a deterministic recovery barrier positioned after journal
authentication, owner adoption, and closed-gate reconstruction but before convergence. Because ASP.NET
cannot serve HTTP while startup is blocked, do not claim a concurrent HTTP refusal from this second
host. Instead assert that its startup task, readiness, and ordinary bootstrap all remain incomplete at
the barrier. Release the barrier and supply the exact kind-specific evidence: V4 direct-reset resume
binding for Covenant reset, or V2 factory launch plus database reconciliation for factory erasure.

The standalone factory recovery must retain a null `ParentReceiptBindingDigest`; it performs no parent-
receipt reconciliation. Existing nested installation-reset suites remain authoritative for the optional
parent path and are not duplicated here.

Recovery must converge the same transition, perform exact database reconciliation, spend the recorded
disposition, retire the journal by closed-anchor-before-delete ordering, and only then complete startup,
report readiness, and open the next generation. A fresh authenticated request, work lease, and physical
open must succeed after startup. Missing, foreign, malformed, or mismatched
records remain fail-closed; existing lower-level authentication matrices remain authoritative and are
not recopied into the host test.

## 6. Focused TDD sequence

Implementation proceeds in strict red-green-refactor cycles:

1. Add the same-process/full-host `KeepClosed` postcondition and run only that test. Record the observed
   reopen failure.
2. Make the smallest coordinator correction and rerun the test green. Temporarily reverting the fix
   must make the regression test fail again before restoration.
3. Add the shared full-host harness and direct-reset commit cluster. Observe the missing integration
   proof or concrete defect, then make only the correction it requires.
4. Add the healthy-factory commit cluster and repeat the cycle.
5. Add rollback and `KeepClosed` for each entry point, then restart recovery, retirement, and next-
   generation cases for each parked payload kind, one behavior at a time.
6. Add or strengthen exact credential/inventory/documentation tests wherever the current contract lacks
   a bidirectional assertion.
7. Fix adjacent defects only when a deterministic reproduction demonstrates them. Obviously missing
   coverage discovered while changing a boundary travels with that boundary.

No production edit may be justified only by inspection. Every behavioral edit has an observed failing
test. Test-only harness changes may be added incrementally to reach the next observable RED.

## 7. Documentation and inventories

The code change and owning documentation travel together:

- `README.md` gains a concise public-facing explanation that destructive Grimoire transitions close
  host-wide admission, drain admitted work, recover from authenticated durable evidence, and fail closed.
- `docs/Arcanum.DESIGN.md` removes stale foundation-only wording and records the final lifecycle,
  ordering, `KeepClosed`, startup, worker, connection-acquisition, full-host test, and qualification
  contracts in the owning Covenant/persistence/testing sections.
- `docs/Arcanum.API.md` confirms the authenticated ordering, stable `/api` and `/v1` maintenance `503`
  shapes, exact exemptions, five route/frame-boundary quiescence, and billable-stream drain behavior.
- Credential documentation/tests pin the profile-scoped account names
  `grimoire-transition-journal-key-{profile digest}` and
  `grimoire-transition-journal-anchor-{profile digest}`, their separation from restore/reset accounts,
  ordinary-cleanup exclusion, and final-reset cleanup order.
- `GrimoireOfflineTransitionJournalKeyLeaseCallSiteTests`,
  `InstallationResetCredentialCatalogTests`,
  `GrimoireOfflineTransitionFullResetTerminalTests`, and
  `GrimoireOfflineTransitionJournalAuthenticationTests` are the concrete credential contract suites;
  add the smallest missing bidirectional assertion there instead of creating a parallel inventory.
- `GrimoireAdmissionRouteInventoryTests`, `GrimoireStreamingRouteInventoryTests`,
  `GrimoireConnectionAcquisitionInventoryTests`, and `HostedGrimoireProducerInventoryTests` remain
  bidirectional and exact. Any discovered drift is corrected only after its inventory test fails.

The docs explicitly state that #257 changes no public route, wire DTO, CLI surface, configuration key,
schema/DDL, or migration contract.

## 8. Review and qualification

After focused implementation tests and documentation/inventory checks are green, request one bounded
read-only review of the complete `origin/main...HEAD` diff against #257, #239, and the approved parent
design. Every Critical or Important finding must be resolved. A behavioral correction starts with a
focused failing test; the complete review repeats after any material correction.

The reviewed tree freezes the current CI workflow and each invoked wrapper. Record the .NET SDK, RID,
and versions of `rg`, `jq`, `lld`, Python, shellcheck, Go, and actionlint. Then run this exact local macOS
matrix without changing the SHA between commands (the AOT and shipping wrappers each allocate a fresh
temporary artifacts path internally):

```bash
export DOTNET_ROOT=/private/tmp/arcanum-dotnet-10.0.401
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_NOLOGO=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true
export ARCANUM_TEST_OS_CREDENTIAL_STORE=true
test "$(dotnet --version)" = "10.0.401"
dotnet tool restore
dotnet restore RetroDownfall.Arcanum.slnx
dotnet build RetroDownfall.Arcanum.slnx -c Release --no-restore --disable-build-servers -m:1
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1
./scripts/packaging/macos/common_test.sh
python3 -m unittest scripts/coverage_threshold_test.py
./scripts/coverage.sh --threshold
./scripts/verify_aot_il_warnings_test.sh
./scripts/verify-aot-il-warnings.sh
dotnet build tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj -c Debug --no-incremental -p:RestoreLockedMode=true --disable-build-servers -m:1
./scripts/benchmark-grimoire-admission.sh --smoke
dotnet build tests/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj -c Debug --no-incremental --disable-build-servers -m:1
./scripts/benchmark-covenant.sh --gate --record /private/tmp/arcanum-issue-257-covenant-benchmark-run.json
./scripts/verify-shipping-publish.sh --rid osx-arm64
./scripts/verify-native-sqlcipher.sh --rid osx-arm64
python3 -m unittest discover -s scripts/tests -p 'test_*.py'
python3 scripts/align_csharp_blanklines.py --repo . --check
find scripts -name '*.sh' -print0 | xargs -0 shellcheck -x -P SCRIPTDIR
"$(go env GOPATH)/bin/actionlint"
git diff --check origin/main...HEAD
git status --short --branch
```

Install missing macOS prerequisites with `brew install shellcheck go`, then install the CI-pinned
actionlint with `go install github.com/rhysd/actionlint/cmd/actionlint@v1.7.12`. Do not silently omit a
tool gate. Development may use the machine's .NET SDK 10.0.400, but the frozen local qualification
matrix must use an official task-local .NET SDK 10.0.401 under
`/private/tmp/arcanum-dotnet-10.0.401`, with that executable verified and selected for every `dotnet`
command without modifying the system installation. This matches the current CI SDK; record both SDK
path and version in Local evidence.

The production workspace-check surface is a capability branch, not an unconditional local pass. If a
preflight confirms the macOS containment/runtime chain and a root-owned, group/world-nonwritable
`/opt/dotnet/dotnet`, run the following and require every selected test to execute with zero skips:

```bash
ARCANUM_REQUIRE_MACOS_WORKSPACE_CHECK=true dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Macos_ci_requires_real_workspace_check_production_surface|FullyQualifiedName~MacOsSandbox_DeniesLaunchServicesBrokerEscape|FullyQualifiedName~Real_dotnet_build_uses_seeded_assets|FullyQualifiedName~Real_dotnet_lint_uses_seeded_project_state|FullyQualifiedName~Real_dotnet_test_uses_seeded_assets"
```

If that trusted preflight is unavailable, record the leg as locally inapplicable rather than passed and
require the dedicated `macOS workspace-check runtime` CI job as its evidence. A zero exit with skipped
tests is never a passing local workspace-check result. The separately executed
`verify-native-sqlcipher.sh --rid osx-arm64` supplies local native-asset evidence; the CI-only
`Ci_has_packaged_sqlcipher_native_asset` guard is not misreported as a local workspace-check assertion.

The coverage command is the sole full Arcanum-suite run; documentation, generated-contract, route,
connection, stream, credential, and hosted-producer tests execute inside it. Classify every status entry
after the matrix and remove only task-generated outputs such as the named Covenant benchmark receipt.

Every command must exit zero, and restore, build, test, publish, AOT, native, benchmark, formatting, and
lint output must contain zero errors and zero warnings. Where an established verifier explicitly
classifies dependency diagnostics separately, only that verifier's documented allowlist applies; record
the classification and reject every unclassified diagnostic. A skipped platform gate is evidence only
when its current workflow contract marks it intentionally inapplicable; Windows x64 and Windows ARM64
are not optional for #239.

Push the exact reviewed and locally qualified feature SHA, manually dispatch `.github/workflows/ci.yml`
(`CI`) for that branch, and wait for all seven current job names to reach terminal `success`:

- `Native SQLCipher asset status`
- `Build, test, coverage`
- `macOS workspace-check runtime`
- `Windows test suite`
- `Windows test suite (arm64)`
- `Native AOT IL and executable gates`
- `Formatting and shell lint`

Record the exact run ID, head SHA, job names, conclusions, and the native-asset output governing each
platform lane. The Windows x64 and ARM64 full Arcanum runs must execute the new same-process/full-host
tests and each shipping-publish gate; neither may be skipped for #239 evidence. Their results also
provide evidence relevant to #242, but this task does not edit, reparent, move, or close #242.

Any source, test, documentation, workflow, or build-input change after the bounded review invalidates
that review and the entire local matrix. Any CI repair creates a new SHA and therefore requires a new
bounded complete-diff review and a complete local-matrix rerun before redispatch. Delivery may use only
the final SHA whose review, local matrix, and CI evidence all match exactly.

## 9. Delivery

The historical parent plan expected the entire umbrella feature tree to reach `main` only after #257,
but the already-delivered #243-#256 tree was merged and released before this final child. Published
history will not be rewritten. The intent of the exact-SHA rule is preserved as follows:

1. Start #257 from the current `main` only after fetching both branches and confirming the cached
   `origin/grimoire-fixes` and `origin/main` remote-tracking refs resolve to the same exact base. The
   remote `refs/heads/grimoire-fixes` update that established that equality occurs before worktree
   creation; never mutate a remote-tracking ref directly. Leave the older local `grimoire-fixes` branch
   checked out in the dirty primary checkout untouched until step 8.
2. Post the issue owner's approved acceptance deviation from Section 2.1 to #257 before delivery; do
   not reinterpret or silently omit the original wording.
3. Freeze one reviewed, locally qualified, CI-green feature SHA.
4. Push the feature SHA directly to remote `refs/heads/grimoire-fixes` as a fast-forward and verify the
   remote ref resolves exactly to that SHA; do not move the dirty checkout's local branch.
5. Delete the task-created remote feature branch only after the remote target is proven; remove its
   local branch and worktree after no remaining verification needs them.
6. Retain that immutable 40-character feature SHA as `delivered_sha`, then fast-forward `main` to it
   without rebuilding or creating a merge commit.
7. Verify both the GitHub ref and a fresh fetch resolve `main` to that SHA.
8. After the delivered `main` ref is proven, delete remote `grimoire-fixes`. Before touching the local
   branch, record the primary checkout's porcelain path/status inventory, the binary diff digest for
   all tracked changes, and SHA-256 digests for every untracked file (currently two). Confirm the final
   `main` has identical index blobs for every tracked changed path (currently six deletions) and no
   tracked collision with any untracked path. Only then ask Git to move that checkout to `main`,
   re-record the same inventory and digests, and require exact equality before deleting local
   `grimoire-fixes`. If any preflight, checkout, or comparison fails, stop and preserve the local branch
   and dirty checkout exactly; remote cleanup is independent and may still proceed.
9. Post concise evidence under four explicit headings—`Local`, `AOT`, `Native`, and `CI`—including the
   commands or workflow jobs, conclusions, exact SHA, and run ID where applicable. Then close #257 with
   reason `COMPLETED`, followed by #239 with reason `COMPLETED`. GitHub currently exposes no project item
   for either issue; closing as completed is the available Done mutation unless a tracker item becomes
   visible before delivery.

Feature-branch and worktree cleanup is limited to artifacts created by this task. Deleting the
pre-existing `grimoire-fixes` umbrella branch is separately and explicitly authorized by the user's
request, subject to the dirty-checkout safety rule above. The two pre-existing prunable detached
worktree registrations and every unrelated branch remain outside scope.

## 10. User-requested post-delivery Ollama proof

This proof is a separate user-requested post-delivery verification, not part of #257 or #239's issue
acceptance contract. After delivered `main`, issue closure, and branch cleanup are verified, create a
new clean detached worktree at the freshly fetched and verified delivered SHA. The primary checkout's
unrelated documentation bytes must never enter this build. Run the following outside containment so
loopback Ollama and the published apphost can communicate; substitute no ambient output path or source
checkout:

```bash
set -euo pipefail
: "${delivered_sha:?retain the immutable SHA recorded during Section 9 delivery}"
git -C /Users/mat/Source/apps/RetroDownfall.Arcanum fetch origin main
test "$(git -C /Users/mat/Source/apps/RetroDownfall.Arcanum rev-parse refs/remotes/origin/main)" = "$delivered_sha"
qualification_parent="$(mktemp -d /private/tmp/arcanum-issue-257-ollama.XXXXXX)"
case "$qualification_parent" in
  /private/tmp/arcanum-issue-257-ollama.*) ;;
  *) exit 1 ;;
esac
qualification_tree="$qualification_parent/tree"
qualification_root="$qualification_parent/output"
cleanup_qualification() {
  original_status=$?
  trap - EXIT
  cd /private/tmp
  if test -d "$qualification_tree"; then
    git -C /Users/mat/Source/apps/RetroDownfall.Arcanum worktree remove "$qualification_tree"
  fi
  rm -rf -- "$qualification_parent"
  exit "$original_status"
}
trap cleanup_qualification EXIT
git -C /Users/mat/Source/apps/RetroDownfall.Arcanum worktree add --detach "$qualification_tree" "$delivered_sha"
cd "$qualification_tree"
test "$(git rev-parse HEAD)" = "$delivered_sha"
test -z "$(git status --porcelain=v1)"
test "$(shasum -a 256 /Users/mat/Downloads/stop-sign.jpg | awk '{print $1}')" = "88d2994d07000a2c3bb31f307c536c6a9c45731d9d002c33bd8407817f269cb0"
mkdir -p "$qualification_root"
dotnet publish src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -c Release -r osx-arm64 --self-contained true --artifacts-path "$qualification_root/artifacts" --disable-build-servers -m:1 -o "$qualification_root/publish" 2>&1 | tee "$qualification_root/publish.log"
if rg --no-config -n -i '(^|[[:space:]:])warning([[:space:]:]|$)' "$qualification_root/publish.log"; then
  exit 1
else
  scan_status=$?
fi
test "$scan_status" -eq 1
./scripts/verify-local-ollama-aot.sh --executable "$qualification_root/publish/RetroDownfall.Arcanum.Cli" --image /Users/mat/Downloads/stop-sign.jpg --model gemma4:e4b --endpoint http://127.0.0.1:11434/v1/ --configuration Release
```

Capture and scan the publish log for zero warnings before invoking the verifier. The verifier inputs
are:

- executable: the freshly published apphost;
- endpoint: `http://127.0.0.1:11434/v1/`;
- model: `gemma4:e4b`;
- image: `/Users/mat/Downloads/stop-sign.jpg`, whose required SHA-256 is
  `88d2994d07000a2c3bb31f307c536c6a9c45731d9d002c33bd8407817f269cb0`.

The existing verifier must prove three persisted conversation turns, corrected attachment-version
selection, restart persistence, encrypted-at-rest attachment data, and independent stop-sign vision
recognition. It must emit the exact v1 receipt and pass without modifying the user's normal Arcanum
profile or Ollama configuration.

If this final proof exposes a defect attributable to #257 or a violation of #239/#257 acceptance,
immediately reopen both issues, create a new `codex/` repair branch from delivered `main`, reproduce it
RED, fix it through TDD, and repeat the entire review/local-matrix/CI/exact-SHA delivery before closing
the issues and rerunning this proof. An unrelated Arcanum inference defect remains a separate TDD repair
and does not retroactively reopen these Grimoire issues, though it still prevents the overall request
from being called complete. If the failure is demonstrably external to Arcanum (for example, Ollama or
model availability), keep the issue acceptance result distinct, diagnose the environment in scope, and
do not claim the overall user task is complete until the requested proof succeeds or the user resolves
an external blocker.

## 11. Completion criteria

Issue #257 and parent #239 are complete only when all of the following are true:

- both authenticated transition entry points pass their deterministic full-host race clusters;
- commit, rollback, `KeepClosed`, recovery, retirement, and next-generation contracts pass;
- all exact credential, route, stream, connection, and hosted-producer inventories pass;
- owning public and canonical documentation matches the implementation;
- one bounded final review has no unresolved Critical or Important finding;
- the complete local matrix passes on one unchanged SHA with zero error or warning diagnostics under
  the qualification rule above;
- CI passes on that same SHA, including Windows x64 and Windows ARM64;
- the issue evidence distinguishes `Local`, `AOT`, `Native`, and `CI` results;
- `origin/main` resolves to that SHA;
- task-created feature and remote `grimoire-fixes` branches are removed, and local `grimoire-fixes` is
  removed unless Git's dirty-checkout protection requires preserving it;
- GitHub reports #257 and #239 closed as completed.

The overall user request is complete only after those issue criteria and the fresh delivered-main
Native AOT Ollama multi-turn/vision verifier both pass against `gemma4:e4b` and the pinned stop-sign
image.
