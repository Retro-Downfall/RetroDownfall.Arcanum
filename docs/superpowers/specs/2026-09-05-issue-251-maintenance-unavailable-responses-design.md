# Issue #251 Stable Maintenance-Unavailable HTTP Responses Design

**Status:** Approved for implementation.

**Branch:** `codex/issue-251-maintenance-unavailable-responses`, cut from `grimoire-fixes` at
`4933fee046a285da016f2c5b89002e80814502d6` (the #250 merge).

**Issue:** [#251 — Grimoire: return stable maintenance-unavailable HTTP responses](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/251)

**Parent design authority:** `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`,
§6.2 and §8. Where this document and the parent disagree, the parent governs except on the seven
points §1.3 records as deliberate departures.

## 1. Decision

### 1.1 What this child delivers

#245 built `IGrimoireConnectionAdmissionGate` and gave it request leases. #246 wired every physical
open to it. #247 through #250 taught the durable side to launch, journal, resume and recover an
offline transition. Through all of that, `TryAcquireRequestLease` has had **no production caller**:
the gate can refuse a request, and nothing has ever asked it to. An HTTP request that arrives during
an erasure therefore runs its endpoint, reaches SQLite, and is refused by the enrolment interceptor
with an exception that the unhandled-exception handler turns into a `500 Hub.Unhandled` — logged at
`Error`, with the request path in the log line.

This child makes the gate the first thing a protected request meets, and makes the refusal a stable,
sanitized, documented answer rather than an accident of where the exception happened to land.

Three things follow, and they are this child's whole content.

First, one admission step is added to the existing pre-binding middleware, selected by path and never
by API-key metadata: `/api` and `/v1`, segment-safe and case-insensitive, after the authentication
stage and before installation-reset admission, Covenant pre-binding, body reading, binding, and the
endpoint. A protected request either owns a request lease held through asynchronous request-scope
disposal, or it receives the documented `503` and never reaches an endpoint.

Second, the refusal has exactly two shapes and one wording, on whichever surface produces it. Under
`/api` it is a source-generated `ApiResponse<string>` at `503` carrying
`Grimoire.MaintenanceUnavailable`; under `/v1` it is the source-generated OpenAI error envelope at
`503` with type `service_unavailable`. Both carry the existing content-free sentence, no path, owner,
operation id, phase or native detail, and neither is logged at `Error`. The same pair is produced by
the exception handler for a gate that closes *after* a request was admitted, which is the window the
middleware alone cannot cover.

Third, the two things that break the moment a request holds a lease are fixed, because this child is
what makes them reachable. The erasure runs inside the request that asked for it, so the initiating
request is promoted out of its own stage-one drain; and a stage-one drain that times out is aborted
back to ordinary admission instead of leaving the gate closed for the life of the process.

### 1.2 The two defects this child must fix to be shippable

Both are latent today only because `TryAcquireRequestLease` has no caller. Adding the middleware
without fixing them replaces an intermittent Windows refusal with a certain, permanent outage.

**The initiator deadlocks its own drain.** `DataRetentionEndpoints.Apply` awaits
`IDataRetentionService.ApplyAsync` and only then builds a response
(`src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs:668`), so the whole erasure runs on
the request. That reaches `CovenantErasureCoordinator.CloseGrimoireAsync`, which calls the
one-argument `BeginOrResumeExclusive(owner)` (`:787`) and then `DrainRequestAndWorkAsync` (`:799`).
The drain snapshots the `Terminal` task of every live request lease
(`GrimoireConnectionAdmissionGate.cs:546`) — including the initiator's own, which cannot complete
until the endpoint returns. Every `POST /api/data/factory-reset` and every Covenant-scoped
`POST /api/data/memory/reset` would fail after the five-second drain checkpoint.

**A timed-out stage one strands the gate.** On drain failure the coordinator calls
`AbandonClosingAsync` (`:805`), which only disposes the closing owner; `ReleaseClosingOwner` clears
`Closure.ActiveClosingOwner` and nothing else (`GrimoireConnectionAdmissionGate.cs:1162`). `_state`
stays `GateState.Closing` and `_closure` stays set. The gate's only edge from a timed-out `Closing`
back to `Ordinary` is `AbortClosingAsync` (`:775`), which has zero production callers. From that
point `TryAcquireRequestLease` returns `false` forever — so this child's own middleware would answer
`503` to the entire installation until the process is restarted.

Neither defect has regression coverage. Every HTTP-level reset test substitutes
`FakeDataRetentionService`, so no test drives `MapArcanumEndpoints` into the real coordinator; both
defects would ship green.

### 1.3 Deliberate departures from the parent design

Seven decisions in this child differ from, or resolve silence in, the parent design. Each is recorded
here so the divergence is a decision rather than drift.

1. **Admission requires a matched endpoint.** Parent §8 says admission is "path-selected" and says
   nothing about an unmatched path. Selecting on path alone would refuse `/api/does-not-exist` with a
   `503` to a caller who presented no key at all — because a request that matched no endpoint takes
   the anonymous branch and is never authenticated — and would falsify the published guarantee that
   "`404`, `405` and `415` remain reachable without a key" (`docs/Arcanum.API.md:268`). A request
   with no matched endpoint also performs no endpoint work, so refusing it buys nothing. Admission
   therefore applies only when routing matched an endpoint.

2. **`GET /api/health` and `POST /api/server/quit` are exempt, by an explicit marker.** Parent §8
   grants no exemptions. Health already answers a closed gate correctly and better than admission
   could: `GrimoireLivenessProbe` catches the refusal and reports a component that names only the
   exception type (`GrimoireLivenessProbe.cs:143`), producing the documented `503` with
   `IsSuccess: true` and a populated report. An admission refusal replaces that operator-grade
   snapshot with `IsSuccess: false` and no data, which `arcanum doctor`, `arcanum watch health`, and
   the auto-launch probe all parse — and auto-launch reads a health `503` as "retry three seconds
   then do not spawn" (`ArcanumServeLauncher.cs:206`). Quit is the shutdown step of the CLI factory
   reset sequence; refusing it strands a reset between host-apply proof and offline continuation, and
   it opens no database. The exemption is a new `GrimoireAdmissionExemptRouteMetadata` carried by
   exactly those two routes and pinned by a bidirectional inventory test, rather than a reuse of
   `InstallationResetRecoveryApiRouteMetadata` — that marker means "admitted during installation-reset
   recovery", its third member is the factory-reset route, and the factory-reset route must *not* be
   exempt (see 3).

3. **The initiating reset request is promoted, never exempted.** Exempting it would leave it with no
   ordinary lifetime at all, and `HasLiveFinisherLifetimeWhileLocked`
   (`GrimoireConnectionAdmissionGate.cs:1005`) is what lets the initiator keep opening its own scoped
   connection while admission is `Closing`. A request with no lifetime is refused by its own erasure.
   Promotion is also the parent's own answer (§6.2: "The exact initiating reset/factory request is
   promoted out of the request drain"). Path selection cannot identify these requests in any case:
   `POST /api/data/memory/reset` closes the gate only when the bound body carries
   `MemoryResetScope.Covenant` (`DataRetentionService.cs:721`), which is unknown before binding.

4. **Promotion is resolved inside the coordinator, not threaded through `RunAsync`.** The superseded
   #239 plan proposed passing the scope down through `DataRetentionService`.
   `CovenantErasureCoordinator` is already scoped and already receives
   `ICovenantClosedPeriodLedgerConnection`, whose own remarks say it exists to be
   `BeginOrResumeExclusive`'s `scopedConnection`
   (`CovenantClosedPeriodLedgerConnection.cs:18`). The coordinator therefore takes the scoped
   admission holder directly and pairs the request's lease with that connection. Nothing changes in
   `DataRetentionService`, `RunAsync` grows no parameter, and a requestless startup recovery resolves
   a holder with no lease and behaves exactly as it does today.

5. **A timed-out stage one aborts back to ordinary admission.** The parent design does not say what
   happens after `Grimoire.WorkDrainTimeout`. Leaving the gate `Closing` is not a fail-closed posture,
   it is a fail-permanent one, and it is this child that makes it reachable. The coordinator therefore
   spends the gate's own proven-abort edge on exactly that error, with a proof drawn from the run's
   state: stage one never issued a closed lease and no phase ran, so no destructive effect occurred.
   Every other drain failure keeps today's dispose-only behaviour, because those arms mean the closure
   moved out from under this owner rather than timed out.

6. **No `Retry-After`.** The rate limiter emits one on both surfaces, but the immediate sibling in the
   same middleware — the installation-reset `409` — does not. A maintenance window's length is not
   knowable when the refusal is written: an erasure may compact a large catalog or park for manual
   recovery. A `Retry-After` a client treats as a promise and the host cannot keep is worse than none.

7. **The refusal carries the no-store tuple unconditionally.** `CovenantProtectedResponseHeaders`
   reaches a refusal nobody wrote only through the `OnStarting` hook that
   `CovenantRequestFeatures.MarkProtectedResponse` installs, and that runs in Covenant pre-binding —
   stage 3, after this child's stage 2. A maintenance `503` on one of #128's four lifecycle routes
   would otherwise ship with no `Cache-Control: no-store, private`, no `Pragma`, no `Expires`, and
   any `ETag` intact. Rather than branch on whether the route is protected, the refusal applies the
   tuple always: a cached maintenance `503` would outlive the window on any route.

## 2. Existing behaviour and cause

`UseArcanumApiKeyAuthentication` (`ApiBootstrapper.cs:639`) is the one `app.Use` delegate installed
by `MapArcanumEndpoints`. It lands between the framework's implicit `UseRouting` and `UseEndpoints`,
so the matched endpoint is known while the body is untouched. Inside it, today:

- anonymous branch: `HideRecoveryIneligibleAnonymousRouteAsync` (`:647`), then installation-reset
  admission for blocked routes (`:654`), then `next()`;
- authenticated branch: `ApiKeyAuthenticator.IsAuthorizedAsync` (`:671`), then
  `ApplyInstallationResetRecoveryAdmissionAsync` (`:674`), then `ApplyCovenantPreBindingPolicyAsync`
  (`:684`), then `next()`;
- otherwise `ApiKeyAuthenticator.Unauthorized` (`:697`).

Nothing consults the Grimoire gate. When admission is closed, an `/api` request runs its endpoint,
`CovenantConnectionEnrolmentInterceptor` throws `GrimoireMaintenanceUnavailableException` before the
native open, and `ArcanumExceptionHandler` logs it at `Error` with `{Path}` and the exception object
(`ArcanumExceptionHandler.cs:69`) before it even checks whether the response has started, then
answers `500 Hub.Unhandled` for `/api` or the unhandled-inference error for `/v1`.

Five endpoint-level sites throw the same exception directly (`MemoryEndpoints.cs:1519`,
`SessionDivinationEndpoints.cs:213`, `WorkspaceDivinationEndpoints.cs:314`,
`WizardIntelligenceProvider.cs:6287`, `GrimoireLivenessProbe.cs:109`), and `GET /metrics` reaches the
interceptor on every scrape because it counts sessions (`MetricsEndpoints.cs:32`). A pre-endpoint
refusal alone therefore cannot satisfy "sanitized and not logged at Error": the gate can close while a
request is already in flight, and `/metrics` is deliberately outside the admission surface.

`ArcanumErrorMapper` already maps the code to `503` (`:129`), and `docs/Arcanum.API.md:662` already
lists it on the `503` row. What is missing is the code's home in `ErrorCodes`, the two response
shapes, and the pipeline stage that produces them.

## 3. Approved architecture

### 3.1 One admission step, in both branches, keyed on path and endpoint

A new `ApplyGrimoireRequestAdmissionAsync(HttpContext)` follows the shape of its two siblings in the
same file: it returns `true` when it has written a refusal and the pipeline must stop.

It refuses when all of the following hold:

1. routing matched an endpoint (§1.3 departure 1);
2. that endpoint does not carry `GrimoireAdmissionExemptRouteMetadata` (§1.3 departure 2);
3. the request path is inside `/api` or `/v1` by
   `PathString.StartsWithSegments(..., StringComparison.OrdinalIgnoreCase)`; and
4. `GrimoireRequestAdmissionScope.TryAdmit(GrimoireRequestKind.Finite)` returned `false`.

`StartsWithSegments` is segment-safe by construction, which is the whole of the exclusion rule:
`/metrics` is outside both prefixes, `/apiary` is not inside `/api`, and `/v10` is not inside `/v1`.
Nothing is excluded by API-key metadata, so authenticated `/metrics` still bypasses admission and the
anonymous `POST /api/conclave/a2a/callbacks/{configId}` is still protected by path authority.

Placement, in both branches of the existing delegate:

- **anonymous branch** — after `HideRecoveryIneligibleAnonymousRouteAsync`, before the blocked-route
  installation-reset admission. The 404-hide keeps its precedence so the one route that must "stay
  indistinguishably unavailable" during an installation reset is unchanged. Outside a reset, that
  route takes the ordinary maintenance `503`, which is the correct answer to a peer: it says retry,
  and the peer's callback will be accepted when the window closes.
- **authenticated branch** — immediately after `IsAuthorizedAsync` succeeds and before
  `ApplyInstallationResetRecoveryAdmissionAsync`.

"After authentication" therefore means after the authentication *stage*, in whichever branch the
request took — not "after a successful key check", which has no meaning on an anonymous route. A wrong
key is still a `401` and never a `503`; authentication stays strictly first.

The resulting published order is: API-key authentication → Grimoire request admission →
installation-reset admission → Covenant pre-binding → body-size enforcement → binding → endpoint.
The existing source-text ordering guard
(`InstallationResetApiAdmissionTests.Recovery_gate_precedes_covenant_authority_and_parameter_binding_in_the_auth_middleware`)
still holds, and this child adds the two ordering assertions it does not make.

### 3.2 The scoped holder owns the lease across request-scope disposal

`GrimoireRequestAdmissionScope` is a new Infrastructure-owned scoped `IAsyncDisposable`:

- it **acquires nothing in its constructor**. Ninety-two child scopes exist under `src`, and an
  eagerly-acquiring holder would mint a phantom HTTP request lease from every background timer scope
  that happened to resolve it;
- `TryAdmit(GrimoireRequestKind)` acquires from the singleton gate, stores the lease, and returns
  whether the request was admitted. A second call on an already-admitted scope returns `true` without
  acquiring a second lease;
- `Lease` exposes the admitted lease for promotion, and is `null` for a startup, recovery, or
  background scope;
- `DisposeAsync` disposes the lease exactly once and never throws — a throw there escapes outside
  `UseArcanumExceptionHandler`.

The middleware resolves it from `HttpContext.RequestServices`, which makes it the **first** scoped
disposable the request creates: everything upstream resolves singletons (`ApiKeyAuthenticator`,
`InstallationResetApiAdmission`), and the rate limiter's rejection path is DI-free. The container
disposes scoped registrations in reverse creation order, so the holder is disposed **last** — after
the pooled `ArcanumDbContext` has been returned and after every `Response.OnCompleted` callback,
including the two that persist idempotency claims. That is exactly the parent's "held through
asynchronous disposal of its request scope", and it is why the lease is not released in a middleware
`finally`: a `finally` runs before `OnCompleted`, and a claim write after it would find no live
finisher lifetime and be refused.

The holder names no member `Open`, `OpenAsync`, `OpenConnection` or `OpenConnectionAsync` and
constructs no `DbConnection`, so it adds nothing to the production acquisition inventory.

`GrimoireRequestKind.Finite` is passed as a compile-time constant. `TryAcquireRequestLease` throws
`ArgumentOutOfRangeException` for an out-of-range kind, and an exception escaping admission would land
in the very handler this child exists to keep quiet. Metadata-selected `QuiesceableStream` is #252's.

### 3.3 Two refusal shapes, one wording

Both are written through an explicit source-generated `JsonTypeInfo`; no new payload type and no new
`[JsonSerializable]` entry is required.

- `/v1`: `OpenAiErrorResponse(new OpenAiErrorDetail(Message, "service_unavailable", Param: null,
  Code: "grimoire_maintenance"))` through `ArcanumJsonContext.Default.OpenAiErrorResponse` at
  `503`. The type is fixed by the parent; the code names the arm the way every sibling does
  (`installation_reset_in_progress`, `rate_limit_exceeded`, `embedding_provider_unavailable`).
- `/api`: `ApiResponse<string>.FromResult(Result<string>.Failure(new Error(code, Message)), traceId)`
  through `ArcanumJsonContext.Default.ApiResponseString` at `503`.

`Message` is the sentence already in the tree, unchanged:
"The Grimoire is temporarily unavailable while maintenance owns connection admission." It names no
path, owner, operation id, phase, generation or native detail.

`traceId` is `Activity.Current?.Id ?? context.TraceIdentifier` — the pattern the rate limiter, the
exception handler, `ApiKeyAuthenticator.Unauthorized`, `DataRetentionEndpoints` and
`SseConnectionResults` all use, so the refusal correlates with the trace an operator is already
following.

Before either body is written, the refusal applies the protected header tuple (§1.3 departure 7) and,
if the response has already started, writes nothing and lets the pipeline unwind.

### 3.4 One code, one literal

`Grimoire.MaintenanceUnavailable` becomes `ErrorCodes.Grimoire.MaintenanceUnavailable` in Core, and
`GrimoireMaintenanceUnavailableException.Code` is redefined as that constant, so the literal exists
exactly once in the tree. `ArcanumErrorMapper`'s single existing or-pattern operand is unchanged and
still resolves to `503`; adding a second operand with the same value would be a subsumption
diagnostic, and there is no reason to touch it.

Promoting the constant is what lets the repo's own invariant hold: `ErrorCodeCatalogContractTests`
asserts that every code a route emits appears in both the constant table and the §8.23 catalog, and a
code that lives only on an internal exception cannot be named in that theory. The catalog row already
exists at `docs/Arcanum.API.md:662`.

The Api project may not spell the literal anywhere — including in a comment, because the scanner reads
raw file text. Every Api reference goes through the constant.

### 3.5 The exception handler covers the requests admission cannot

Admission refuses requests that arrive **after** stage 1. A request admitted a moment before closure
is drained, and while it drains it can still touch SQLite and be refused. `ArcanumExceptionHandler`
therefore gains a typed arm ahead of its `LogError` call:

- if the exception is `GrimoireMaintenanceUnavailableException`, log once at `Debug` with a fixed
  message carrying no path and no exception object, and write the same two `503` shapes §3.3 defines;
- if the response has already started, return `false` and rewrite nothing — a stream that has emitted
  a frame cannot become a `503` envelope, and #252 owns ending those cleanly.

This is also what makes `/metrics` honest. It is outside the admission surface by design, it counts
sessions on every scrape, and without this arm every Prometheus scrape during a maintenance window
would be a `500` logged at `Error` with the request path — the precise pair of failures the acceptance
criterion forbids.

### 3.6 Initiator promotion

`CovenantErasureCoordinator` takes the scoped `GrimoireRequestAdmissionScope`. In
`CloseGrimoireAsync`, when the holder carries a lease it calls
`BeginOrResumeExclusive(owner, lease, _ledgerConnection.Connection)`; when it does not — startup
recovery, the reconciler, any background scope — it calls the one-argument overload exactly as today.

The gate's both-or-neither guard, its exact-instance check against its own live lease set, and its
resume-time equality check on the pair are unchanged and are what make this safe: a caller cannot
promote a foreign lease, a released one, an already-promoted one, or a different connection on resume.

`ICovenantClosedPeriodLedgerConnection.Connection` returns `_db.Database.GetDbConnection()` — the same
object the operation store issues its terminal compare-exchange on. The plain factory arm closes that
connection before entering the coordinator and the coordinator reopens it, so the design depends on
the `DbConnection` *instance* surviving a close/reopen inside one request scope. That is asserted
directly rather than assumed.

### 3.7 Aborting a timed-out stage one

`CovenantErasureCoordinator.CloseGrimoireAsync` distinguishes the drain's two failure classes. On
`Grimoire.WorkDrainTimeout` it calls `AbortClosingAsync` with a proof callback that reports the run's
own state — no closed lease was issued and no phase ran, so nothing destructive happened — and then
returns the timeout error. Ordinary admission is open again and the operator's retry can succeed. On
any other drain failure it disposes the closing owner as it does today, because those errors say the
closure is no longer this owner's to abort.

The gate refuses `AbortClosingAsync` unless the closure is the exact owner's and `StageOneTimedOut` is
set, so the narrow arm is the only one it will accept.

### 3.8 What the five streaming routes mean until #252

The five declared SSE routes and the three NDJSON execute streams are `/api` routes and take `Finite`
leases like everything else. A `Finite` lease is drained through completion and is never revoked — the
gate revokes only `QuiesceableStream` leases — so an erasure begun while a watcher is connected drains
for five seconds and then times out. With §3.7 in place that outcome is a refused erasure and an open
host, not a dead one, and it is the same fail-closed answer the epic already gives for a drain that
cannot complete. #252 marks those routes `QuiesceableStream` and ends them at a frame boundary, which
turns the refusal into a success. This is stated here so the intermediate state is a recorded
consequence rather than a surprise.

## 4. Scope boundaries

**In scope.** The admission step and its holder; the two refusal shapes and their wording; the Core
constant; the exception-handler arm; initiator promotion; the timed-out-stage-one abort; the route
exemption marker and its inventory; documentation; focused tests.

**Out of scope.** Stream quiescence and `QuiesceableStream` metadata (#252). Worker leases (#253,
#254, #255). The hosted-producer inventory (#256). Whole-host race proof, coverage, AOT/IL,
benchmarks, native provenance, packaging and parent-wide qualification (#257).

**Unchanged contracts.** No route, request or response DTO, CLI verb, configuration key, schema object
or migration changes. #128's eleven `/api/data*` route names, their `LifecycleManage` authority, the
`CovenantProtectedJsonResult<DataRetentionPlan>` preview handoff, `DataRetentionPlan` and
`DataRetentionApplyResult`, the no-store tuple, and the no-response-before-release rule are all
preserved. The no-response-before-release rule binds a destructive endpoint's own lease-bearing
response; a lease-less refusal written by admission before any endpoint ran is outside it, and this
document says so because on their face the two rules look like they collide.

## 5. Testing strategy

Every behaviour below is observed RED before the production change that makes it GREEN.

**Admission surface.** A probe host proves segment safety and selection: `/api/...` and `/v1/...`
refused, `/metrics`, `/apiary/...`, `/v10/...` and `/apiVersion` admitted, `/API/...` and `/V1/...`
refused (case-insensitive), an unmatched `/api/...` path still `404`, a wrong method still `405`, and
a bad key still `401` rather than `503`. A full-host inventory asserts the exemption marker is carried
by exactly `GetHealth` and `QuitServer`.

**Envelopes.** Typed deserialization of both bodies against the source-generated contracts, plus a
raw-string assertion that no path, owner, operation id, phase or type name appears. A recording
`ILoggerFactory` proves no `Error`-level entry.

**Lifetime.** A later-created scoped async-disposable sentinel proves the lease is released after it,
i.e. after request-scope disposal rather than in a middleware `finally`. A second admitted request
proves a closing gate waits for it. A response that has already started is not rewritten.

**Exception handler.** `GrimoireMaintenanceUnavailableException` from an endpoint becomes the same
`503` pair on both surfaces, is not logged at `Error`, and is not rewritten once the response started.

**Promotion.** A full host on a real catalog drives `POST /api/data/factory-reset` through the real
`IDataRetentionService` and asserts the erasure completes rather than timing out — the test that does
not exist today and is what makes the deadlock a caught defect rather than a shipped one. A focused
test asserts the promoted `DbConnection` instance survives the factory arm's close/reopen.

**Abort.** A drain forced to time out leaves the gate `Ordinary`, and a later request is admitted.

Coverage, complete suites, Native AOT/IL, benchmarks, native SQLCipher provenance, packaging,
cross-platform and parent-wide qualification remain #257's.

## 6. Documentation

`docs/Arcanum.DESIGN.md` records the new pipeline stage in §10.18 and §11.3, the refusal pair in
§11.9, the fourth dual-surface branch in §11.12, the delivered request middleware in §10.20.3, the
second `503` producer in §2.2, and a regression-catalog row in §13.7.

`docs/Arcanum.API.md` gains a new §8.31 owning the admission contract, extends the §8.23 `503`
semantics and the `/v1` type list, corrects the §8.29 decision order (which is already stale: it omits
the installation-reset stage that has shipped), disambiguates health's success-envelope `503` from a
refusal `503`, and records the `/metrics` exclusion as a consequence of segment-safe matching.

`README.md` gains the one sentence the parent's §11 asks for. It is one of the three documents allowed
to name a tracker issue; `Arcanum.DESIGN.md`, `Arcanum.API.md` and the other governed documents are
not, and `DocumentationIssueReferenceTests` enforces that.

## 7. Review and delivery

Focused suites are run per task. On a green tree the branch is merged into `grimoire-fixes` with
`--no-ff`, pushed, the feature branch deleted, and #251 closed. #257 owns the parent-wide
qualification.
