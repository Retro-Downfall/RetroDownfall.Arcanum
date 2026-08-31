# Issue #239: Host-wide Grimoire Admission During Covenant Erasure

**Status:** Approved design, pending implementation.

**Branch:** `codex/issue-239-grimoire-admission`, cut from `origin/main` at
`988a469c765346132e5a2ea1bf3906519f6bdf00`.

**Issue:** [#239 — A Grimoire connection is enrolled in the Covenant drain only as a side effect](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/239)

## 1. Objective

Make Covenant erasure own a host-wide Grimoire maintenance boundary. Once an erasure begins closing
that boundary, no ordinary API request, Entity Framework scope, background worker, or raw SQLite
opener may acquire a live handle that can race the drain or reopen the database during destructive
maintenance.

The erasure keeps explicit narrow authority over its one exact scoped database connection and over
one-shot owner/path/mode/purpose-bound maintenance opens required by its phase machine. Every
ordinary live-Grimoire open is refused before SQLite is touched, or is closed and refused if it loses
the opening race. Existing work that may already have called an external provider is drained before
connection admission closes so a maintenance transition cannot cause duplicate billable work.

This fixes the intermittent Windows refusal in which a pooled handle reopened the Grimoire after the
Covenant drain had closed known handles, leaving WAL/SHM state or blocking a later exclusive
transaction.

## 2. Existing behavior and cause

Current `main` already contains the first #239 prerequisite: every EF-managed open is enrolled by
`CovenantConnectionEnrolmentInterceptor`, and `CovenantConnectionDrain` distinguishes a stuck handle
from a correctly closed handle that a live host later reopened. That makes the failure observable,
but it does not close the reopen window.

The remaining window is host lifecycle rather than drain bookkeeping:

- an API request can resolve and open an `ArcanumDbContext` while erasure is draining;
- `SessionAttachmentIndexingService`, `EntryWeavingService`, and `SagaExtractionService` can create a
  new scope and reopen the Grimoire during erasure;
- raw `SqliteConnection.Open` / `OpenAsync` paths do not pass through an EF interceptor;
- the existing source inventory classifies opener declarations, not every production acquisition
  call site;
- pooled disposal is not physical closure, so one missed or late handle can keep the live database
  and its sidecars resident.

Closing known handles remains necessary, but is not sufficient. Admission must be refused at the
point where every new live-Grimoire handle is acquired.

## 3. Approved decisions

### 3.1 Host-wide gate

Add a singleton `IGrimoireConnectionAdmissionGate`, implemented by
`GrimoireConnectionAdmissionGate`. It is independent of `ICovenantOperationGate`:

- the Covenant gate establishes durable destructive-operation ownership and health;
- the Grimoire gate controls process-local database work and connection admission;
- ordinary Grimoire availability must not become coupled to unrelated Covenant health failures.

The gate has one ordinary state, a closing transition, and an exclusive state. Its state changes are
generation-based so a ticket issued before closure cannot become valid merely because admission was
later reopened.

### 3.2 Narrow maintenance authorities

The exclusive owner may issue a non-serializable maintenance permit bound by reference identity to
the erasure scope's existing `DbConnection`. The permit:

- authorizes only that exact connection object;
- is valid only for the current exclusive owner and generation;
- may cover repeated close/reopen cycles of that one object during checkpoint and factory-reset
  continuation;
- cannot be copied into a DTO, stored in a checkpoint, inferred from an operation id, or used to
  authorize a newly constructed connection through ordinary admission;
- is revoked before the exclusive owner is disposed.

Coordinator reads, durable checkpoints, and factory-reset continuation use the exact scoped
connection permit. Lease renewal preserves the existing operation-ledger invariant: it runs on
`LongRunningOperationStore`'s independent, unpooled connection rather than racing or joining a
transaction on the scoped EF handle.

During Grimoire exclusivity, renewal requires a distinct one-shot ticket issued only for the active
exclusive owner's durable operation. It authorizes one policy-initialized open, one exact-owner CAS
renewal, and physical close. It cannot be pooled, reused, or converted into ordinary connection
authority. Renewal admission and connection-sensitive erasure steps share a maintenance-I/O lane,
so renewal cannot overlap canonical transactions, handle drain, WAL checkpoint, compaction/database
replacement, integrity/sidecar proof, or reopen verification. Before entering a potentially long
lane, the coordinator renews for the normal durable interval; iterative storage work yields at its
existing idempotent boundaries so renewal can occur between steps.

Some single SQLite/filesystem steps (`VACUUM`, `sqlcipher_export`, canonical transaction, or atomic
replacement/recovery) are not safely interruptible merely to heartbeat and can exceed that interval.
Lane entry and expired-lease adoption therefore share one process-local adoption interlock. Lane
entry takes the interlock, revalidates durable ownership, and keeps it through the SQLite/filesystem
step and any overrun checkpoint/`KeepClosed` decision. Recovery takes that same interlock, proves no
matching live lane owns it, and performs the durable adoption CAS before releasing it. If adoption
wins first, the incumbent revalidation fails and the sensitive step never starts; if lane entry wins,
adoption cannot steal the operation mid-step.

If the step returns after its durable lease expired, the coordinator records or preserves the
idempotent phase boundary, selects `KeepClosed`, and performs no next destructive step before
releasing the interlock. After a process crash the interlock and live owners no longer exist, so
ordinary durable recovery may adopt the expired operation. Failure to obtain or complete renewal
before a step starts likewise cancels erasure and leaves admission closed. No renewal handle may
survive its ticket.

Erasure and proof code also creates fresh unpooled connections through
`ICovenantMaintenanceConnectionFactory`. Classification in a source inventory is not authority to
open them. Each factory acquisition therefore consumes a distinct non-serializable, one-shot
maintenance-open capability bound to:

- the active exclusive owner and admission generation;
- the canonical Grimoire path identity;
- the requested read-only or read-write mode;
- the named erasure/proof purpose that is currently legal in the phase machine.

The factory returns a tracked maintenance connection lease rather than a bare unaccounted handle.
The lease proves policy initialization and physical closure, and it must be disposed before its
phase releases the maintenance-I/O lane. A capability cannot be reused, widened to another path or
mode, or presented by ordinary code. The gate refuses Grimoire disposition while any such lease or
one-shot renewal ticket remains live.

### 3.3 Admission lifetimes

The gate exposes three ordinary lifetimes:

1. **Connection tickets** cover one physical open attempt. Stage 2 boundedly waits for every ticket
   issued before closure to report opened, failed, or refused. It does not wait for the resulting
   connection's full logical lifetime; the Covenant drain closes successfully opened handles after
   all native open attempts have resolved.
2. **HTTP request leases** cover `/api` and `/v1` requests that can reach the Grimoire, from before
   endpoint execution and response start through asynchronous request-scope disposal. Finite and
   billable requests are allowed to finish; new requests are refused as soon as closing begins.
   Explicitly classified unbounded watch streams observe cooperative maintenance revocation, finish
   their current frame, and end so a passive client cannot hold the gate forever.
3. **Background work leases** cover a complete worker unit, from before DI scope creation through
   its final durable database action or provider-result disposition. They are denied as soon as
   exclusive acquisition begins and are drained before connection admission closes.

The one HTTP request that initiates a reset or healthy-catalog factory erasure would otherwise wait
for its own lease. After the durable checkpoint and Covenant owner are established,
`BeginOrResumeExclusive` atomically promotes that exact request lease into an owner-matched initiator
token and removes it from the ordinary request-drain count. Promotion also binds only that request
scope's exact unopened/open `DbConnection` to the scoped maintenance permit. The token grants no
ordinary request authority after stage 2, cannot promote another request or connection, and its
later scope disposal cannot reopen either gate. Startup recovery and non-HTTP initiators use no
promotion.

A background lease also owns an atomic independently resumable external-effect-group frontier.
`TryBeginExternalEffectGroup` is linearized with revocation under the gate's state lock: either
revocation wins and no provider call in that group may start, or effect-start wins and erasure must
wait until the group's durable disposition completes. One group may contain several provider calls
when no intermediate result is durably resumable. A linked cancellation token alone is not
sufficient to decide whether an external request was sent.

Separating them prevents deadlock: erasure waits for native open attempts rather than arbitrary EF
scope lifetimes, then lets the drain close enrolled handles. It does wait for already-admitted HTTP
requests and the three billable or checkpointed background workflows to reach their known durable
boundaries before closing connection admission.

### 3.4 No schema or durable gate journal

The Covenant long-running-operation checkpoint remains the sole durable recovery authority. The
Grimoire gate stores no new database row, file, or schema object. Startup recovery reconstructs the
process-local gate state from the existing Covenant operation kind, owner, checkpoint, and effect
digest.

Bootstrap performs the handoff without an ordinary refused open. The startup install connection
adopts the durable Covenant owner and places that owner plus the existing operation identity in the
process-local bootstrap barrier. Before API readiness or affected workers are released, recovery
resolves an unopened `ArcanumDbContext`, binds that exact `GetDbConnection()` object to the adopted
owner, and only then reads the ledger and resumes the coordinator. No operation id is trusted merely
because a caller supplied it; the binding must match the owner adopted from the durable checkpoint.

## 4. Admission protocol

### 4.1 EF-managed opens

The Grimoire connection interceptor acquires a connection ticket in `ConnectionOpening` /
`ConnectionOpeningAsync`, before the provider invokes SQLite.

- If admission is closed and the connection does not match the maintenance permit, it throws
  `GrimoireMaintenanceUnavailableException` before native open.
- `ConnectionOpened` / `ConnectionOpenedAsync` revalidates the ticket's generation before enrolling
  the SQLite handle in `ICovenantConnectionDrain`.
- If closure won the race while the native open was in progress, the interceptor closes the newly
  opened handle, releases all admission/drain state, and throws the same maintenance exception.
- `ConnectionFailed`, `ConnectionClosed`, and `ConnectionDisposed`, including asynchronous forms,
  release their corresponding state exactly once.
- Multiple logical registrations for one physical connection remain reference counted; one holder
  cannot remove another holder's drain protection.

Resolving any production `ArcanumDbContext` therefore installs the admission/enrolment lifecycle
independently of whether that scope also resolves `ILongRunningOperationStore`,
`ICovenantConnectionSource`, or any other service.

### 4.2 Raw opens

Every raw opener of the live Grimoire must use the same gate explicitly. That includes raw opens of
the EF connection in `CovenantConnectionSource` and repository readback paths that do not traverse
EF interception. `LongRunningOperationStore`'s normal `Database.OpenConnectionAsync` is
EF-intercepted; only its independent unpooled renewal path is a raw opener.

The one-shot renewal path is a named raw-open capability rather than an ordinary admission. Its
ticket and maintenance-I/O lane must be visible at the construction, open, CAS update, and physical
close call sites.

Fresh live-Grimoire connections created for canonical erasure, compaction, integrity, sidecar proof,
or reopen verification must consume the owner/generation/path/mode/purpose-bound maintenance-open
capability at the factory call. Source classification alone is not sufficient runtime permission.

Openers for staged backup files, imported candidates, design-time tooling, native-runtime probes, or
startup bootstrap are not silently treated as live ordinary opens. Each must be classified by the
source inventory with its concrete path authority and reason.

### 4.3 Closing race

Grimoire exclusivity proceeds through a resumable closing owner and a closed lease:

1. `BeginOrResumeExclusive` validates the same durable owner and returns a closing owner. It
   atomically promotes an optional exact owner-matched initiating request lease, immediately refuses
   new HTTP request and background work leases, signals revocation to background work that has not
   begun an external effect group, and drains every other request/work lease.
2. `CloseConnectionAdmission` advances the admission generation, refuses new ordinary connection
   tickets, revokes tickets whose physical open has not completed, and boundedly waits for all those
   opening attempts to reach opened, failed, or refused. Only then does it return the closed
   exclusive lease that can authorize physical drain and maintenance I/O.

Ordinary connection tickets remain available during stage 1. Existing request/work leases need their
normal scoped connection to finish a provider-result disposition, and no handle drain or destructive
step has begun yet. After their asynchronous scopes dispose, stage 2 closes ordinary admission.
New HTTP requests already receive maintenance `503` responses at stage 1 because request admission
is a separate lifetime acquired before endpoint execution.

An open that began before stage 2 may complete at the native layer, but its post-open generation
check immediately closes and refuses it. Stage 2 does not complete until that callback has released
the opening ticket, so the later Covenant-drain snapshot cannot miss an unresolved native open. An
already open enrolled handle is then closed by `ICovenantConnectionDrain`. A later correct reopen is
impossible until the exclusive owner explicitly reopens admission.

### 4.4 Failure behavior

The gate is fail-closed after connection admission has closed:

- owner mismatch, invalid transition, or double disposition is rejected;
- a bounded work-drain timeout prevents destructive phases from starting;
- a bounded opening-attempt timeout prevents handle drain and exclusive SQLite I/O from starting;
- a connection-drain, sidecar-proof, publication, checkpoint, or disposition failure leaves
  Grimoire admission closed and recoverable under the durable Covenant operation;
- `KeepClosed` never reopens Grimoire admission;
- cancellation does not best-effort reopen an uncertain catalog.

A bounded timeout during stage 1 leaves the gate in its recoverable closing transition: new HTTP and
background work remains denied, ordinary connection admission remains available to a finishing
lease, and no destructive phase begins. A timeout waiting for an opening attempt leaves ordinary
admission closed and still prevents destructive work. Recovery may resume the same transition, or
may abandon it only after the durable Covenant owner proves that no destructive effect occurred.
Once the closed lease has been issued, no request/work lease or unresolved ordinary open remains by
invariant and every failure is fail-closed.

Once the Covenant exclusive lease has been acquired, every return path explicitly dispositions it.
Failure to begin or finish Grimoire closing may reopen both gates only through the existing proven
pre-erasure abort path. Owner uncertainty, an invalid phase, or any possible destructive effect uses
`KeepClosed`; disposing either owner by fall-through is forbidden.

## 5. Covenant erasure lifecycle

`CovenantErasureCoordinator` owns both exclusive lifetimes in this order:

1. acquire or resume the durable Covenant exclusive owner using the existing operation id, kind,
   owner, checkpoint, and effect digest;
2. begin or resume the Grimoire closing owner, refusing new HTTP/background work and draining their
   leases after atomically promoting an owner-matched initiating request, when present;
3. close ordinary connection admission and wait for every unresolved physical open attempt;
4. quiesce the Grimoire writer;
5. serialize against maintenance/renewal opens, close enrolled handles, and clear SQLite pools;
6. execute the existing preflight, erasure, sidecar-proof, publication, and reopen-verification
   phases;
7. dispose the durable Covenant owner using the requested disposition;
8. finalize the operation and Grimoire owner using the disposition-dependent ordering below.

The erasure scope binds its exact EF connection to the maintenance permit before step 5. That object
is used for coordinator reads, durable checkpoints, factory-reset continuation, and any pre-close
reconciliation transition. The gate also arms the separate one-shot renewal and
maintenance-factory authorities for this exact durable owner. Every connection-sensitive storage
step owns the maintenance-I/O lane; renewal occurs between such steps and no renewal or maintenance
handle may survive a lane release.

Finalization must never make the durable operation terminal before the last required physical proof:

- **`CommitAndReopen`:** while the operation row is still recoverable, physically close the exact
  scoped connection, release its permit, drain/clear pools, and perform the final residual sidecar
  proof. Then dispose the Grimoire owner with `CommitAndReopen`. Only after ordinary admission is
  open does the narrowly typed finalizer write `Completed` through an ordinary connection. WAL/SHM
  created by that post-reopen write is normal live-host state. A crash before the terminal CAS leaves
  the existing `ReopenedVerified` checkpoint recoverable and safe to finalize idempotently.
- **`KeepClosed` or uncertain disposition:** while the exact permit is still valid, write or retain a
  recoverable `ReconciliationRequired` erasure row—never `Completed` or terminal `Failed`. Then
  physically close the exact connection, release its permit, drain/clear pools, perform the required
  residual proof, and dispose the Grimoire owner with `KeepClosed`. A crash or proof failure at any
  point leaves a row that bootstrap recognizes as requiring closed-gate recovery.
- **Proven pre-erasure abort:** reopen both gates through the existing abort path first, then write
  terminal `Failed` through ordinary admission. No uncertain/destructive outcome may use this arm.

The shared adoption interlock remains held across each final proof/disposition sequence. In
`CommitAndReopen` it spans Grimoire reopening and the `Completed` CAS; in a proven abort it spans
reopening and the `Failed` CAS; in `KeepClosed` it spans the `ReconciliationRequired` CAS and final
close/proof. This prevents an expired lease from being adopted in the brief ordinary-admission gap
before terminal state is durable. The interlock is released only after the CAS succeeds or fails into
the explicitly recoverable checkpoint outcome. A process crash naturally drops the interlock while
leaving the nonterminal row adoptable.

Revoking a permit is not physical closure. The gate refuses final disposition while the exact
permitted connection is open or while any renewal, factory, or opening ticket remains live.
`CovenantErasureCoordinator.RunAsync` does not report completion until the applicable ordering and
typed finalizer action have run. This keeps terminal-state policy in `DataRetentionService` while
making the durable row a trustworthy startup signal.

Recovery/adoption uses the shared interlock from section 3.2 rather than a check-then-CAS. Lane entry
and expired-lease adoption are mutually exclusive and each revalidates durable ownership after it
wins. Owner mismatch is an integrity failure; absence of the interlock/live owners after restart
permits normal durable adoption. An overrun step never uses live ownership as permission to continue
to the next phase.

Normal success completes Covenant disposition first, then reopens Grimoire admission. If Covenant
disposition is uncertain or requests `KeepClosed`, Grimoire remains closed. Reopening the process
gate before durable disposition would expose an uncertain catalog and is forbidden.

Startup recovery follows the same ordering before the three affected hosted workers are released by
the bootstrap barrier and before API readiness. It uses the adopted-owner/exact-unopened-connection
handoff in section 3.4. It creates no new recovery record: the existing Covenant checkpoint decides
whether the gate must be reacquired and whether completion may reopen it.

## 6. Background-worker behavior

### 6.1 Common work-lease rule

`SessionAttachmentIndexingService`, `EntryWeavingService`, and `SagaExtractionService` acquire a
background work lease before creating their service scope. The lease remains held through database
reads, any provider call, the final durable state transition, and asynchronous disposal of the
scope. It releases exactly once on success, refusal, cancellation, or fault. Stage 2 cannot begin
while a scoped `ArcanumDbContext` from one of these work units is still disposing.

When exclusive acquisition begins:

- a worker calls `TryBeginExternalEffectGroup` immediately before each independently resumable
  provider-effect group; if revocation wins that atomic race, it defers without calling a provider in
  that group;
- a worker whose effect group has begun keeps its guard and lease until the whole group reaches its
  durable result or existing durable retry classification; erasure waits rather than manufacturing
  a retry that may double-bill;
- once effect-start wins, the maintenance-revocation token is not passed to either the provider call
  or its required durable disposition; existing caller/host cancellation policy remains separate;
- maintenance refusal itself never increments a product retry counter, advances a watermark,
  records a terminal failure, or triggers another provider call;
- cancellation and maintenance refusal remain distinguishable from genuine provider or content
  failure.

This guarantee is scoped to maintenance: beginning erasure introduces no additional provider retry
or billing attempt. It does not claim universal exactly-once billing when a provider or network
fails ambiguously under the worker's pre-existing policy.

Deferral is non-spinning. A denied or revoked unit remains recoverable at the same attempt and waits
for the gate's next-open-generation signal or its existing bounded durable reconciliation cadence.
It must not immediately requeue against the same closed generation. Maintenance refusal bypasses
the workers' generic failure paths.

### 6.2 Attachment indexing

Both the channel-consumer path and `ReconcileAndEnqueueAsync` acquire a work lease before opening a
scope. An item refused before provider work remains pending and is signalled only after admission
reopens or the normal reconciliation interval elapses. The queue does not spin, mark it failed, or
consume an automatic retry attempt.

One request may contain several embedding batches. Each successful append/replace is a durable
resume boundary. If revocation wins before the next batch's atomic effect frontier, processing
defers at the same attempt; if effect-start wins, that embedding result is appended before the lease
may observe maintenance revocation.

### 6.3 Entry weaving

A revoked tick returns as a deferred tick. It does not open a scope, invoke embeddings, or mutate
weaving progress. The tick has one embedding-batch frontier; once effect-start wins, its provider
call, every resulting upsert, and asynchronous scope disposal complete before erasure proceeds.

### 6.4 Saga extraction

A revoked extraction preserves its watermark, attempt count, `_pending` eligibility, and next
eligible work. Saga extraction has no reconciliation poll, so the refused consumer registers one
next-open-generation continuation that re-signals the existing pending id exactly once without
passing through `EnqueueExtraction`'s already-pending no-op. It neither spins while closed nor waits
for a new user turn. Maintenance does not enter the generic retry-delay path.

The atomic effect group is one extraction page, beginning with its LLM request. Because the LLM
response has no separate durable copy before memory embedding and persistence, a page whose group
has begun completes the LLM request, all memory-embedding requests, durable writes, and
`SetWatermark` before it completes the guard or releases the lease. Revocation may defer before the
next page, never between provider calls inside a billed page.

## 7. API behavior

Add the stable error code `Grimoire.MaintenanceUnavailable`. The internal maintenance exception
carries no database path, operation id, owner id, checkpoint, or native SQLite detail.

A gate-aware request-admission boundary runs before `/api` and `/v1` endpoint execution and before
response bytes. Its request-scoped lease is resolved before other scoped services and disposed after
the asynchronous request scope. A request arriving after stage 1 begins is refused at this boundary
and therefore still has an unstarted response.

Finite and billable admitted requests—including an in-flight OpenAI-compatible inference stream—keep
their lease through durable completion. Explicitly inventoried unbounded daemon, MCP, log,
session-watch, and apprentice Chronicle (`/api/apprentices/{id}/chronicle`,
`SseEventTypes.Chronicle`) streams instead link their wait loop to a maintenance-revocation token.
On closing, they finish the current complete SSE/NDJSON frame, end the existing response without
trying to rewrite its status, and dispose their request scope. The bounded stage-1 timeout remains
the fail-safe for a non-cooperative client or handler.

The boundary and `ArcanumExceptionHandler` map refusal before the unhandled-exception path:

- `/api/**` returns HTTP `503` with the normal source-generated `ApiResponse` error envelope and
  code `Grimoire.MaintenanceUnavailable`;
- `/v1/**` returns HTTP `503` with the documented OpenAI-compatible error envelope and type
  `service_unavailable`;
- the exception is expected maintenance flow, not an error-level unhandled exception;
- no stack trace or internal reason is returned to the client.

Maintenance does not rewrite a response that has already started. By invariant, a finite/billable
stream is drained before stage 2, a quiesceable unbounded stream ends cooperatively at a frame
boundary, and a refused stream has not executed its endpoint or written headers. Tests cover all
three cases rather than relying on an exception handler to replace an in-progress stream.

The endpoint route set and request/response DTO set do not otherwise change.

## 8. Acquisition-site enforcement

Extend the existing production-opener source inventory from declaration coverage to acquisition-site
coverage. The contract scans production C# sources for connection `Open` / `OpenAsync` and
maintenance-factory acquisition calls and requires every match to be one of:

- EF-managed and protected by the registered lifecycle interceptor;
- a live raw opener that explicitly acquires a Grimoire admission ticket and drain enrolment;
- an exclusive-maintenance factory acquisition that consumes the matching one-shot runtime
  capability as an argument;
- a non-live database or startup/design-time probe with a named path rationale.

The inventory records file and enclosing method, not merely the type that declared an opener. A new
call site using an already known factory therefore fails the contract until its admission or explicit
classification is reviewed. Broad directory exemptions, filename-only wildcards, and comments that
claim safety without a corresponding API call are not sufficient.

The known Covenant maintenance call sites—including disclosure writing, erasure inventory, healthy
catalog erasure guard, canonical erasure transactions, local storage health, and host-tools marker
reset—must each receive the correct narrow classification and corresponding runtime admission: an
ordinary tracked ticket or an exclusive one-shot capability. Tests compare the declared inventory
to the actual source matches in both directions so stale exemptions fail too.

A separate composition contract enumerates every production `ArcanumDbContext` options path,
including pooled and non-pooled registrations, and proves it receives the same singleton gate,
drain, and lifecycle interceptor. Nullable or missing admission dependencies are allowed only in a
named design-time/bootstrap composition that cannot serve ordinary product work. Resolving a
different service in the scope is never a substitute for this options-level protection.

The HTTP contract inventory also names every unbounded streaming endpoint that may cooperatively
end on maintenance. An unlisted `/api` or `/v1` request is finite/drained by default; code cannot
silently opt a billable inference stream into cooperative cancellation.

## 9. TDD strategy

### 9.1 Focused baseline

The clean feature branch ran the existing drain, erasure coordinator, exception-boundary, attachment
queue, entry-weaving, and saga-extraction clusters before edits: 122 tests passed. Repository-wide
qualification is reserved for the completed reviewed tree.

### 9.2 Gate RED/GREEN cycles

Write focused tests before production code for:

- ordinary admission and refusal;
- revocation and bounded completion-wait of an in-flight physical open, proving exclusive SQLite I/O
  cannot begin while the native attempt is unresolved;
- exact-connection maintenance authorization;
- one-shot owner/generation/path/mode/purpose factory authorization and tracked physical close;
- exact-owner, one-shot unpooled lease renewal and maintenance-I/O exclusion;
- generation and owner mismatch;
- one-shot close/disposition;
- `KeepClosed` and failed-disposition behavior;
- startup adopted-owner handoff and exact recovery-connection binding;
- successful `Completed` CAS only after final physical proof and Grimoire reopen;
- `KeepClosed` retaining `ReconciliationRequired` through final close/proof and restart;
- the two-winner adoption-interlock race: lane-first blocks adoption, while adoption-first prevents
  the incumbent sensitive step from starting;
- adoption blocked across Grimoire reopen until the final `Completed`/`Failed` CAS;
- an overrun connection-sensitive step selecting `KeepClosed` without entering its next phase;
- final exact-handle physical close, pool drain, and residual proof on the correct side of each
  disposition-dependent ledger transition;
- bounded work-lease drain and provider-effect completion;
- lease release after asynchronous DI-scope disposal;
- an atomic revocation-versus-effect-group-start race with exactly one permitted outcome;
- owner-matched initiating-request promotion for direct reset and factory erasure, excluding that
  one lease while every other request still drains.

That race has two acceptable results only: revocation wins with zero provider calls in the group and
unchanged progress/retry state, or group-start wins and exclusive acquisition cannot reach
connection closure until every required provider call, durable disposition, and scope disposal for
that resumable group finishes.

Each new production behavior must first have an observed failure for the intended missing behavior.

### 9.3 Interceptor and raw-open cycles

Add deterministic interceptor tests for pre-open refusal, close-during-open, post-open physical
closure, enrollment, and exactly-once cleanup across failure/close/dispose callbacks. Add raw opener
tests proving that the EF-bypassing paths acquire the same admission protection. Composition tests
prove every production pooled/non-pooled options path installs the singleton gate and drain without
depending on side-effect service resolution.

The source-inventory test must first fail on at least one existing unclassified acquisition call site
before the inventory and corresponding production gate calls are completed.

### 9.4 Coordinator and worker cycles

Coordinator tests prove closing-owner versus closed-lease ordering, exact-handle/factory authority,
stage-1 unwind, opening-attempt wait, recovery handoff, disposition-dependent ledger finalization,
adoption/lane linearization, success reopening, and every safe closed outcome. One deterministic
test per named worker proves its specific
defer/requeue behavior and that maintenance consumes neither progress nor provider retry/billing
state. Attachment tests cover both queue consumption and reconciliation scope creation. Race tests
pin each worker's durable effect-group boundary and prove deferral waits for a new admission
generation rather than spinning against the closed one. Saga close/reopen coverage proves the same
pending extraction runs without another user turn.

### 9.5 API cycles

Request-admission and exception-boundary tests first expect the new `/api` and `/v1` `503` contracts
and sanitized bodies before endpoint execution. A never-ending watch-SSE test proves closing signals
a complete-frame termination and reaches stage 2 without client disconnect. A billable in-flight
stream test proves it is awaited through durable completion rather than maintenance-cancelled.
Full-host tests then race ordinary API/background access with Covenant reset and healthy-catalog
factory erasure, proving SQLite is not reached after admission closes.

## 10. Documentation changes

Implementation updates travel with code:

- `README.md` describes maintenance refusal at the Grimoire lifecycle boundary;
- `docs/Arcanum.DESIGN.md` records admission, enrollment, erasure ordering, recovery, worker draining,
  and acquisition inventory alongside Covenant sections 10.20.4–10.20.6;
- `docs/Arcanum.API.md` documents the `/api` and `/v1` `503` shapes.

No CLI verb, configuration key, schema object, or endpoint route is added, so
`Arcanum.Command.Reference.md` and `Compendium.README.md` require no contract change unless the
implementation discovers a direct contradiction.

## 11. Out of scope

- Changing Covenant eligibility, disclosure accounting, effect digests, or durable checkpoint
  schema.
- Replacing the physical connection drain or weakening WAL/SHM absence proof.
- Suspending every hosted service; only workflows that can open the live Grimoire must use the
  relevant admission lifetime.
- Retrying provider work merely because maintenance began.
- Adding an operator-configurable maintenance switch or public maintenance endpoint.
- Treating backup candidates, imported databases, or design-time databases as the live Grimoire
  without path-specific evidence.

## 12. Review and verification

After TDD implementation, request one bounded read-only review of the complete branch diff. Resolve
all Critical and Important findings before qualification, beginning observable fixes with a focused
failing test.

Inspect repository wrappers, then run the locally applicable final matrix once on the reviewed,
merged feature tree:

- Release solution build with build servers disabled, single-node MSBuild, and zero errors/warnings;
- every test project, with the threshold coverage run supplying the complete Arcanum suite where
  applicable;
- fresh Native AOT/IL verification;
- native SQLCipher provenance for `osx-arm64`;
- documentation, source-inventory, formatting, generated-contract, shell, workflow, and packaging
  gates required by the current CI contract;
- `git diff --check` and clean tracked status.

Only after all gates are green may the branch be fast-forwarded into `main`, pushed, deleted, and
GitHub issue #239 closed and moved to Done.
