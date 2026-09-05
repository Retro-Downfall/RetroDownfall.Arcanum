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
- `tests/RetroDownfall.Arcanum.Tests/Api/Middleware/GrimoireRequestAdmissionTests.cs` — new.
- `tests/RetroDownfall.Arcanum.Tests/Api/Middleware/ArcanumExceptionHandlerTests.cs`,
  `tests/RetroDownfall.Arcanum.Tests/Api/ErrorCodeCatalogContractTests.cs`,
  `tests/RetroDownfall.Arcanum.Tests/Api/InstallationResetApiAdmissionTests.cs` — extended.
- `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureInitiatorPromotionTests.cs` — new.
- Documentation: `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `README.md`.

---

### Task 1: One literal for the maintenance code

**Files:** `Core/Primitives/ErrorCodes.cs`, `Infrastructure/Data/GrimoireConnectionAdmissionContracts.cs`,
`tests/.../Api/ErrorCodeCatalogContractTests.cs`.

- [ ] RED: add `Grimoire.MaintenanceUnavailable` to
      `Catalog_and_constant_table_both_carry_every_code_a_route_emits`'s `[InlineData]` list and observe
      it fail on the missing constant.
- [ ] GREEN: add `public const string MaintenanceUnavailable = "Grimoire.MaintenanceUnavailable";` to
      `ErrorCodes.Grimoire` with an XML summary, and redefine
      `GrimoireMaintenanceUnavailableException.Code` as `ErrorCodes.Grimoire.MaintenanceUnavailable` so
      the literal exists once. Leave `ArcanumErrorMapper`'s single operand alone.
- [ ] Run `ErrorCodeCatalogContractTests`, `ArcanumErrorMapperTests`, `CovenantErrorContractTests` GREEN.
- [ ] `git commit -m "refactor: give the maintenance refusal one code and one literal"`.

---

### Task 2: The scoped lease holder

**Files:** new `Infrastructure/Data/GrimoireRequestAdmissionScope.cs`,
`Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`, plus tests in the new
`GrimoireRequestAdmissionTests`.

- [ ] RED: tests asserting a freshly constructed holder has acquired nothing and reports no lease; that
      `TryAdmit` on an ordinary gate returns `true` and exposes the lease; that a second `TryAdmit`
      returns `true` without taking a second lease; that `TryAdmit` on a closing gate returns `false`
      and leaves no lease; that `DisposeAsync` releases exactly once and is idempotent; and that
      disposing a never-admitted holder does not throw.
- [ ] GREEN: add the holder and register it `AddScoped` beside the gate.
- [ ] Run the holder filter GREEN.
- [ ] `git commit -m "feat: carry one request's admission for the whole of its scope"`.

---

### Task 3: The refusal writer and its two shapes

**Files:** new `Api/Middleware/GrimoireMaintenanceRefusal.cs`, plus tests.

- [ ] RED: tests asserting `/api/**` produces `503` + `ApiResponse<string>` with code
      `Grimoire.MaintenanceUnavailable`, `IsSuccess: false`, no `data` member and a trace id; `/v1/**`
      produces `503` + `OpenAiErrorResponse` with type `service_unavailable` and code
      `grimoire_maintenance`; both carry the exact sanitized sentence; both carry
      `Cache-Control: no-store, private`, `Pragma: no-cache`, `Expires: 0` with `ETag` and
      `Last-Modified` removed; the raw JSON contains no path, owner, operation id, phase or type name;
      and a response that has already started is left untouched with `false` returned.
- [ ] GREEN: add the writer. Both bodies go through `ArcanumJsonContext.Default.ApiResponseString` and
      `.OpenAiErrorResponse`; the code comes from `ErrorCodes.Grimoire.MaintenanceUnavailable`; the
      trace id is `Activity.Current?.Id ?? context.TraceIdentifier`.
- [ ] Run the refusal filter GREEN.
- [ ] `git commit -m "feat: give a maintenance window one answer on each surface"`.

---

### Task 4: The admission step in the pre-binding middleware

**Files:** new `Api/Security/GrimoireAdmissionExemptRouteMetadata.cs`, `Api/ApiBootstrapper.cs`,
`Api/Health/HealthEndpoints.cs`, `Api/Hosting/ServerLifecycleEndpoints.cs`, plus tests.

- [ ] RED: probe-host tests asserting a closed gate refuses `/api/...` and `/v1/...` before the endpoint
      runs and with zero body bytes read; admits `/metrics`, `/apiary/...`, `/v10/...` and `/apiVersion`;
      refuses `/API/...` and `/V1/...`; leaves an unmatched `/api/...` path a `404` and a wrong method a
      `405`; and answers `401`, never `503`, for a bad key on a protected route.
- [ ] RED: a test asserting an anonymous `/api` route with no API-key metadata is still refused, and one
      asserting the installation-reset 404-hide still wins on the hidden route.
- [ ] RED: full-host inventory tests asserting exactly `GetHealth` and `QuitServer` carry
      `GrimoireAdmissionExemptRouteMetadata`, and that no other route does.
- [ ] RED: an ordering test asserting the admission call precedes both
      `ApplyInstallationResetRecoveryAdmissionAsync` and `ApplyCovenantPreBindingPolicyAsync` in the
      authenticated branch, written so it cannot be satisfied by the anonymous branch's earlier call.
- [ ] GREEN: add the marker, attach it to the two routes, add
      `ApplyGrimoireRequestAdmissionAsync`, and call it in both branches at the placements §3.1 fixes.
- [ ] RED: a lifetime test with a later-created scoped async-disposable sentinel proving the lease is
      released after that sentinel, and a test proving an admitted request holds a closing gate open
      through endpoint completion and scope disposal.
- [ ] GREEN: nothing further should be needed; if it is, the holder's registration order is wrong.
- [ ] Run the admission, installation-reset, Covenant-boundary, metrics and API-surface filters GREEN.
- [ ] `git commit -m "feat: refuse new protected work while maintenance owns the database"`.

---

### Task 5: The exception handler's typed arm

**Files:** `Api/Middleware/ArcanumExceptionHandler.cs`, `tests/.../Api/Middleware/ArcanumExceptionHandlerTests.cs`.

- [ ] RED: tests asserting a `GrimoireMaintenanceUnavailableException` thrown from an endpoint answers
      the same `503` pair on both surfaces, is not logged at `Error`, logs no request path, and is not
      rewritten once the response has started; and that every other exception still logs at `Error` and
      answers `500`.
- [ ] GREEN: add the typed arm ahead of the `LogError` call, reusing the Task 3 writer.
- [ ] Run the exception-handler filter GREEN.
- [ ] `git commit -m "fix: answer an expected refusal without calling it an internal error"`.

---

### Task 6: Initiator promotion

**Files:** `Infrastructure/Data/CovenantErasureCoordinator.cs`,
`Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`, new
`tests/.../Data/Covenant/CovenantErasureInitiatorPromotionTests.cs`, plus a full-host test.

- [ ] RED: a full-host `[SkippableFact]` driving `POST /api/data/factory-reset` through the **real**
      `IDataRetentionService` on a real catalog, asserting the erasure completes rather than failing
      with `Grimoire.WorkDrainTimeout`. This fails before promotion is wired.
- [ ] RED: focused tests asserting the coordinator promotes exactly the holder's lease paired with
      `ICovenantClosedPeriodLedgerConnection.Connection`; that a holder with no lease takes the
      one-argument path unchanged; and that the promoted `DbConnection` instance is the same object
      after the factory arm's close and the coordinator's reopen.
- [ ] GREEN: give the coordinator the scoped holder and branch `BeginOrResumeExclusive` on whether a
      lease is present.
- [ ] Run the promotion, erasure-coordinator, retention-endpoint and same-process filters GREEN.
- [ ] `git commit -m "feat: let the request that asked for an erasure out of its own drain"`.

---

### Task 7: Abort a timed-out stage one

**Files:** `Infrastructure/Data/CovenantErasureCoordinator.cs`, plus tests.

- [ ] RED: a test holding one unrelated request lease open past the drain checkpoint and asserting the
      erasure fails with `Grimoire.WorkDrainTimeout` **and** the gate is `Ordinary` afterwards, with a
      later request admitted. This fails today: the gate stays `Closing` for the life of the process.
- [ ] RED: a test asserting a non-timeout drain failure still disposes the closing owner and does not
      attempt the abort.
- [ ] GREEN: branch `CloseGrimoireAsync`'s drain failure on the timeout code and spend
      `AbortClosingAsync` with a proof drawn from the run's state.
- [ ] Run the erasure and gate filters GREEN.
- [ ] `git commit -m "fix: give a drain that timed out its way back to ordinary admission"`.

---

### Task 8: Documentation

**Files:** `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `README.md`.

- [ ] DESIGN: §10.18 pipeline order, §11.3 the second pre-binding middleware, §11.9 the refusal pair,
      §11.12 the fourth dual-surface branch, §10.20.3 request middleware and maintenance responses
      delivered while stream quiescence and worker adoption stay deferred, §2.2 the second `503`
      producer, §13.7 a regression-catalog row.
- [ ] API: new §8.31 owning the admission contract; §8.23 `503` semantics and the `/v1` type list;
      §8.29 decision order corrected to include both the installation-reset stage and the new one; §1
      health's success-envelope `503` disambiguated from a refusal `503`; §8.22 the `/metrics`
      exclusion as a consequence of segment-safe matching. Do not renumber existing sections.
- [ ] README: the one sentence recording host-wide maintenance admission.
- [ ] Run `DocumentationStructureTests`, `DocumentationIssueReferenceTests`,
      `ErrorCodeCatalogContractTests` and `CovenantApiDocumentationTests` GREEN.
- [ ] `git commit -m "docs: record the answer a protected request gets during maintenance"`.

---

### Task 9: Deliver

- [ ] Build the solution in Release with zero warnings.
- [ ] Run every filter this plan touched, plus the API suite, GREEN.
- [ ] Mark this plan's tasks delivered.
- [ ] Merge into `grimoire-fixes` with `--no-ff` as
      `Merge issue #251: return stable maintenance-unavailable HTTP responses`, push, delete the
      feature branch, and close #251.
