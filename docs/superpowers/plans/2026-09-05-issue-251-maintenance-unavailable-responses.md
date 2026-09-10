# Issue #251 Stable Maintenance-Unavailable HTTP Responses Implementation Plan

**Goal:** Make the Grimoire admission gate the first thing a protected `/api` or `/v1` request meets,
so every such request either owns a request lease held through request-scope disposal or receives a
stable, sanitized `503` before any endpoint work — and fix the two defects that adding the first
production lease makes reachable.

**Architecture:** One path-selected admission step inside the existing pre-binding middleware, an
Infrastructure-owned scoped `IAsyncDisposable` holder that carries the lease past every
`OnCompleted` writer and the pooled `DbContext`, two source-generated refusal shapes shared by the
middleware and the exception handler, initiator promotion resolved inside the scoped erasure
coordinator, and a proven abort out of a timed-out stage one.

**Tech Stack:** .NET 10, C# 13, ASP.NET Core middleware and endpoint metadata, Native-AOT
source-generated JSON, EF Core SQLite/SQLCipher, xUnit, `TaskCompletionSource` barriers, Git,
GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-05-issue-251-maintenance-unavailable-responses-design.md`

## Global Constraints

- Work on `codex/issue-251-maintenance-unavailable-responses`, based on `grimoire-fixes` commit
  `4933fee0`, and merge only into `grimoire-fixes`.
- Follow RED → GREEN → REFACTOR in each task. Ordering tests use barriers and manual time, never
  sleeps. Tests that touch the real Grimoire carry `[SkippableFact]`/`[SkippableTheory]` and open with
  `Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason)`.
- Add no route, request/response DTO, CLI verb, configuration key, schema object, or migration. Add no
  `GrimoireRequestKind` member, no `CovenantResetPhase` member, no payload version.
- Do not mark any route `QuiesceableStream`, do not touch `SseStreamWriter`, and do not change the
  non-envelope framing table. Streaming quiescence is #252's.
- Every request admitted takes `GrimoireRequestKind.Finite` as a compile-time constant.
- `src/RetroDownfall.Arcanum.Api` may not contain the string literal `"Grimoire.MaintenanceUnavailable"`
  anywhere, including inside a comment.
- Preserve Native-AOT rules: both refusal bodies serialize through an explicit `JsonTypeInfo` from
  `ArcanumJsonContext.Default`; no new payload type is introduced.
- New test classes that construct or inject `ArcanumWebApplicationFactory` carry `[Collection("ApiHost")]`.
  Never mutate the shared collection fixture's `ServiceOverrides`/`SettingsOverride`.
- Tracker issue numbers may appear only in `docs/superpowers/**`, `README.md`,
  `docs/Arcanum.Engineering.md`, and `docs/Arcanum.OATH.md`.
- Zero Release build warnings. C# house style: one blank line after each line of code, file-scoped
  namespaces, positional records, primary constructors for DI.
- Run only child-scoped focused tests during development. Coverage, complete suites, Native AOT/IL,
  benchmark, native SQLCipher provenance, packaging, full-host, cross-platform, and parent-wide
  qualification remain #257's.

## File and responsibility map

- `src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs` — the one literal for the maintenance code.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs` — the
  exception's `Code` becomes the Core constant.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireRequestAdmissionScope.cs` — new scoped
  lazily-acquiring async-disposable lease holder.
- `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` — the
  holder's scoped registration.
- `src/RetroDownfall.Arcanum.Api/Security/GrimoireAdmissionExemptRouteMetadata.cs` — the two-route
  exemption marker.
- `src/RetroDownfall.Arcanum.Api/Middleware/GrimoireMaintenanceRefusal.cs` — the shared refusal writer
  used by both the middleware and the exception handler.
- `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs` — the admission step in both branches.
- `src/RetroDownfall.Arcanum.Api/Middleware/ArcanumExceptionHandler.cs` — the typed non-Error arm.
- `src/RetroDownfall.Arcanum.Api/Health/HealthEndpoints.cs`,
  `src/RetroDownfall.Arcanum.Api/Hosting/ServerLifecycleEndpoints.cs` — the exemption marker.
- `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs` — initiator promotion
  and the timed-out-stage-one abort.
- `tests/RetroDownfall.Arcanum.Tests/Api/Middleware/GrimoireRequestAdmissionTests.cs` — new, the probe-host
  admission surface.
- `tests/RetroDownfall.Arcanum.Tests/Api/Middleware/GrimoireMaintenanceRefusalTests.cs` — new, the refusal
  writer both surfaces share.
- `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireAdmissionRouteInventoryTests.cs` — new, the composed-host
  exemption inventory, the holder's registration, and the two source-ordering guards.
- `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireRequestAdmissionScopeTests.cs` — new, the holder's own
  contract.
- `tests/RetroDownfall.Arcanum.Tests/Api/Middleware/ArcanumExceptionHandlerTests.cs`,
  `tests/RetroDownfall.Arcanum.Tests/Api/ErrorCodeCatalogContractTests.cs`,
  `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureSameProcessTests.cs`,
  `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs`,
  `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCovenantResetRecoveryTests.cs` — extended.
- Documentation: `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `README.md`.

---

### Task 1: One literal for the maintenance code

**Files:** `Core/Primitives/ErrorCodes.cs`, `Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs`,
`tests/.../Api/ErrorCodeCatalogContractTests.cs`.

- [x] RED: add `Grimoire.MaintenanceUnavailable` to
      `Catalog_and_constant_table_both_carry_every_code_a_route_emits`'s `[InlineData]` list and observe
      it fail on the missing constant.
- [x] GREEN: add `public const string MaintenanceUnavailable = "Grimoire.MaintenanceUnavailable";` to
      `ErrorCodes.Grimoire` with an XML summary, and redefine
      `GrimoireMaintenanceUnavailableException.Code` as `ErrorCodes.Grimoire.MaintenanceUnavailable` so
      the literal exists once. Leave `ArcanumErrorMapper`'s single operand alone.
- [x] Run `ErrorCodeCatalogContractTests`, `ArcanumErrorMapperTests`, `CovenantErrorContractTests` GREEN.
- [x] `git commit -m "refactor: give the maintenance refusal one code and one literal"`.

---

### Task 2: The scoped lease holder

**Files:** new `Infrastructure/Data/GrimoireRequestAdmissionScope.cs`,
`Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`, plus tests in the new
`GrimoireRequestAdmissionTests`.

- [x] RED: tests asserting a freshly constructed holder has acquired nothing and reports no lease; that
      `TryAdmit` on an ordinary gate returns `true` and exposes the lease; that a second `TryAdmit`
      returns `true` without taking a second lease; that `TryAdmit` on a closing gate returns `false`
      and leaves no lease; that `DisposeAsync` releases exactly once and is idempotent; and that
      disposing a never-admitted holder does not throw.
- [x] GREEN: add the holder and register it `AddScoped` beside the gate.
- [x] Run the holder filter GREEN.
- [x] `git commit -m "feat: carry one request's admission for the whole of its scope"`.

---

### Task 3: The refusal writer and its two shapes

**Files:** new `Api/Middleware/GrimoireMaintenanceRefusal.cs`, plus tests.

- [x] RED: tests asserting `/api/**` produces `503` + `ApiResponse<string>` with code
      `Grimoire.MaintenanceUnavailable`, `IsSuccess: false`, no `data` member and a trace id; `/v1/**`
      produces `503` + `OpenAiErrorResponse` with type `service_unavailable` and code
      `grimoire_maintenance`; both carry the exact sanitized sentence; both carry
      `Cache-Control: no-store, private`, `Pragma: no-cache`, `Expires: 0` with `ETag` and
      `Last-Modified` removed; the raw JSON contains no path, owner, operation id, phase or type name;
      and a response that has already started is left untouched with `false` returned.
- [x] GREEN: add the writer. Both bodies go through `ArcanumJsonContext.Default.ApiResponseString` and
      `.OpenAiErrorResponse`; the code comes from `ErrorCodes.Grimoire.MaintenanceUnavailable`; the
      trace id is `Activity.Current?.Id ?? context.TraceIdentifier`.
- [x] Run the refusal filter GREEN.
- [x] `git commit -m "feat: give a maintenance window one answer on each surface"`.

---

### Task 4: The admission step in the pre-binding middleware

**Files:** new `Api/Security/GrimoireAdmissionExemptRouteMetadata.cs`, `Api/ApiBootstrapper.cs`,
`Api/Health/HealthEndpoints.cs`, `Api/Hosting/ServerLifecycleEndpoints.cs`, plus tests.

- [x] RED: probe-host tests asserting a closed gate refuses `/api/...` and `/v1/...` before the endpoint
      runs and with zero body bytes read; admits `/metrics`, `/apiary/...`, `/v10/...` and `/apiVersion`;
      refuses `/API/...` and `/V1/...`; leaves an unmatched `/api/...` path a `404` and a wrong method a
      `405`; and answers `401`, never `503`, for a bad key on a protected route.
- [x] RED: a test asserting an anonymous `/api` route with no API-key metadata is still refused, and one
      asserting the installation-reset 404-hide still wins on the hidden route.
- [x] RED: full-host inventory tests asserting exactly `GetHealth` and `QuitServer` carry
      `GrimoireAdmissionExemptRouteMetadata`, and that no other route does.
- [x] RED: an ordering test asserting the admission call precedes both
      `ApplyInstallationResetRecoveryAdmissionAsync` and `ApplyCovenantPreBindingPolicyAsync` in the
      authenticated branch, written so it cannot be satisfied by the anonymous branch's earlier call.
- [x] GREEN: add the marker, attach it to the two routes, add
      `ApplyGrimoireRequestAdmissionAsync`, and call it in both branches at the placements §3.1 fixes.
- [x] RED: a lifetime test with a later-created scoped async-disposable sentinel proving the lease is
      released after that sentinel, and a test proving an admitted request holds a closing gate open
      through endpoint completion and scope disposal.
- [x] GREEN: nothing further should be needed; if it is, the holder's registration order is wrong.
- [x] Run the admission, installation-reset, Covenant-boundary, metrics and API-surface filters GREEN.
- [x] `git commit -m "feat: refuse new protected work while maintenance owns the database"`.

---

### Task 5: The exception handler's typed arm

**Files:** `Api/Middleware/ArcanumExceptionHandler.cs`, `tests/.../Api/Middleware/ArcanumExceptionHandlerTests.cs`.

- [x] RED: tests asserting a `GrimoireMaintenanceUnavailableException` thrown from an endpoint answers
      the same `503` pair on both surfaces, is not logged at `Error`, logs no request path, and is not
      rewritten once the response has started; and that every other exception still logs at `Error` and
      answers `500`.
- [x] GREEN: add the typed arm ahead of the `LogError` call, reusing the Task 3 writer.
- [x] Run the exception-handler filter GREEN.
- [x] `git commit -m "fix: answer an expected refusal without calling it an internal error"`.

---

### Task 6: Initiator promotion

**Files:** `Infrastructure/Data/CovenantErasureCoordinator.cs`,
`Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`, new
`tests/.../Data/Covenant/CovenantErasureInitiatorPromotionTests.cs`, plus a full-host test.

- [x] RED: a full-host `[SkippableFact]` driving `POST /api/data/factory-reset` through the **real**
      `IDataRetentionService` on a real catalog, asserting the erasure completes rather than failing
      with `Grimoire.WorkDrainTimeout`. This fails before promotion is wired.
- [x] RED: focused tests asserting the coordinator promotes exactly the holder's lease paired with
      `ICovenantClosedPeriodLedgerConnection.Connection`; that a holder with no lease takes the
      one-argument path unchanged; and that the promoted `DbConnection` instance is the same object
      after the factory arm's close and the coordinator's reopen.
- [x] GREEN: give the coordinator the scoped holder and branch `BeginOrResumeExclusive` on whether a
      lease is present.
- [x] Run the promotion, erasure-coordinator, retention-endpoint and same-process filters GREEN.
- [x] `git commit -m "feat: let the request that asked for an erasure out of its own drain"`.

---

### Task 7: Abort a timed-out stage one

**Files:** `Infrastructure/Data/CovenantErasureCoordinator.cs`, plus tests.

- [x] RED: a test holding one unrelated request lease open past the drain checkpoint and asserting the
      erasure fails with `Grimoire.WorkDrainTimeout` **and** the gate is `Ordinary` afterwards, with a
      later request admitted. This fails today: the gate stays `Closing` for the life of the process.
- [x] RED: a test asserting a non-timeout drain failure still disposes the closing owner and does not
      attempt the abort.
- [x] GREEN: branch `CloseGrimoireAsync`'s drain failure on the timeout code and spend
      `AbortClosingAsync` with a proof drawn from the run's state.
- [x] Run the erasure and gate filters GREEN.
- [x] `git commit -m "fix: give a drain that timed out its way back to ordinary admission"`.

---

### Task 8: Documentation

**Files:** `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `README.md`.

- [x] DESIGN: §10.18 pipeline order, §11.3 the second pre-binding middleware, §11.9 the refusal pair,
      §11.12 the fourth dual-surface branch, §10.20.3 request middleware and maintenance responses
      delivered while stream quiescence and worker adoption stay deferred, §2.2 the second `503`
      producer, §13.7 a regression-catalog row.
- [x] API: new §8.31 owning the admission contract; §8.23 `503` semantics and the `/v1` type list;
      §8.29 decision order corrected to include both the installation-reset stage and the new one; §1
      health's success-envelope `503` disambiguated from a refusal `503`; §8.22 the `/metrics`
      exclusion as a consequence of segment-safe matching. Do not renumber existing sections.
- [x] README: the one sentence recording host-wide maintenance admission.
- [x] Run `DocumentationStructureTests`, `DocumentationIssueReferenceTests`,
      `ErrorCodeCatalogContractTests` and `CovenantApiDocumentationTests` GREEN.
- [x] `git commit -m "docs: record the answer a protected request gets during maintenance"`.

---

### What changed while implementing

Five decisions were taken during the work that this plan did not anticipate. Each is in the code with
its reasoning; they are collected here so the plan is not read as a description of something else.

1. **Admission requires a `RouteEndpoint`, not merely a non-null endpoint.** Routing answers a method
   mismatch with an endpoint of its own that no `Map` call produced, so a non-null test refused
   `POST /api/probe` with `503` instead of leaving it a `405`. Caught by the RED for that case.
2. **The holder is `IDisposable` as well as `IAsyncDisposable`.** A container scope disposed
   *synchronously* throws outright on a service that implements only the async interface, and eleven
   synchronous scopes exist under `src`. Releasing a request lease is synchronous work, so the second
   implementation is honest rather than a blocking wrapper. Pinned by its own test.
3. **Admission admits when no holder is registered.** A host that maps these endpoints without the
   Arcanum infrastructure stack has no Grimoire to protect, and the two pre-binding stages below
   answer an absent service the same way. That the composed host *does* register it is asserted by
   `The_composed_host_registers_the_admission_holder_per_request` rather than defended by a throw in
   the request path.
4. **The abort proof landed over the real composition, not the coordinator harness.** The harness's
   own gate could not be steered into a stage-one timeout without rebuilding it, so
   `Factory_erasure_that_cannot_drain_reopens_ordinary_admission` drives a real catalog instead. Its
   RED was confirmed by reverting the production branch and re-running.
5. **The lease is taken synchronously, and the in-flight refusal moved to the admission stage.**
   An adversarial review caught both. An `AsyncLocal` written inside an `async` method does not
   survive that method's return, so admitting behind an `await` discarded the gate's ordinary lifetime
   before the pipeline reached the endpoint — every request looked like it had none, and a promoted
   reset request would have been refused by its own transition. The admit is synchronous now. The
   in-flight arm moved for a second reason found the same way: the framework's exception middleware
   registers its cache-header clear through `OnStarting` before any handler runs, and those run
   last-registered-first, so a refusal written from the handler cannot keep #128's exact tuple. Both
   have pipeline-level tests, and both were confirmed by reintroducing the defect and watching them
   fail.

6. **`Grimoire.WorkDrainTimeout` became a documented, retryable `503`.** The abort in Task 7 turns a
   timed-out transition into something an operator can retry, so answering `500` from an unlisted code
   was no longer right. It is now on `ErrorCodes.Grimoire`, mapped, and in the §8.23 catalog.

7. **One unrelated build fix rides in this branch.** `scripts/verify-aot-il-warnings.sh` could not
   complete on any Homebrew-dotnet macOS host: `RetroDownfall.Arcanum.RegexAotSmoke` has never carried
   the keg-only openssl/brotli linker search paths or the lld `ld-path` that `RetroDownfall.Arcanum.Cli`
   carries, so its Native AOT link failed with `library 'ssl' not found` and took the IL gate down with
   it. Those arguments moved to `Directory.Build.props`, where every AOT publish gets them and a third
   AOT project cannot reintroduce the gap. It is pre-existing — that csproj has never contained a
   `LinkerArg` in its history — and unrelated to this issue; it is here because it is what let the gate
   run at all, and the gate is how this child's Native-AOT-safety claim is checked. It landed inside
   commit `8309fcba`, whose subject names only the request-lease fix.

8. **Two source-ordering guards, not one.** The existing guard indexes from the top of the file, which
   the anonymous branch's earlier call would satisfy however the authenticated one moved. The new
   ordering test searches from the API-key check, and a second test pins that the anonymous 404-hide
   still precedes admission.

---

### Task 9: Deliver

- [x] Build the solution in Release with zero warnings.
- [x] Run every filter this plan touched, plus the API suite, GREEN.
- [x] Mark this plan's tasks delivered.
- [x] Merge into `grimoire-fixes` with `--no-ff` as
      `Merge issue #251: return stable maintenance-unavailable HTTP responses`, push, delete the
      feature branch, and close #251.
