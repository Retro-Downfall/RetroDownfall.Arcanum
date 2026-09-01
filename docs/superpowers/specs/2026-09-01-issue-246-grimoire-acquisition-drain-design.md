# Issue #246 Grimoire Acquisition and Physical-Drain Design

**Status:** In progress — final child qualification pending.

**Parent:** GitHub issue #239.

**Depends on:** Delivered issue #245 at `a1160e88fde6970d0940cb02872e1061158169f9`.

**Delivery branch:** `codex/issue-246-grimoire-acquisition-drain` into `grimoire-fixes`.

## 1. Decision

Issue #246 completes the process-local acquisition layer that issue #245 deliberately left as a
primitive. Every serving Entity Framework or raw acquisition of the live Grimoire is admitted before
native SQLite work, revalidated after native open, enrolled for physical draining, and represented in
an exact bidirectional source inventory. Journal-era closed-period database access is possible only
through a one-shot maintenance capability bound to the exact transition owner, closed generation,
canonical path, mode, purpose, and active maintenance lane. The still-active V3 same-database path is
an explicit, per-call-site transition boundary whose temporary authority is defined in section 7.3
and whose removal owner is #248.

The existing `CovenantConnectionEnrolmentInterceptor` is inherited partial baseline. It remains in
place and is audited and hardened from new failing tests where behavior is missing; it is not deleted
and rewritten merely to recreate historical RED evidence.

The implementation uses a gate-centered acquisition design:

1. the existing EF interceptor remains the serving EF enforcement point;
2. a single `GrimoireOrdinaryConnectionFactory` owns every serving raw live-Grimoire open;
3. `GrimoireConnectionAdmissionGate.CloseConnectionAdmissionAsync` owns the complete stage-two
   ordering from open-ticket resolution through registered-handle closure and pool clearing;
4. a capability-consuming Grimoire maintenance factory owns every new journal-driven maintenance
   open; and
5. an exact authored-source catalog proves both that every acquisition is declared and that every
   declaration still names one real acquisition.

Per-call-site ticket helpers are rejected because they permit lifecycle drift. Provider-wide SQLite
interception is rejected because bootstrap, staging, archive, design-time, and live catalog opens have
different authorities and cannot safely share one global policy.

## 2. Scope

### 2.1 Included

- Audit every serving `ArcanumDbContext` options path and require exactly one interceptor wired to the
  process-wide admission gate and physical drain.
- Route every serving raw acquisition of the live Grimoire through one ordinary factory.
- Close the generation race after native open before initialization or use of the connection.
- Enroll successful ordinary physical handles until their actual owning lifetime ends.
- Compose stage-two closure as admission close, unresolved-open terminalization, enrolled-handle
  closure, pool clearing, and closed-lease issuance.
- Add an owner-, generation-, path-, mode-, purpose-, and lane-bound maintenance factory whose return
  value owns physical closure reporting.
- Carry exact live-lock-derived one-shot authority into the two stopped-host installation-reset
  acquisition paths.
- Prevent a closed lease from dispositioning while a maintenance lane, capability, scoped permit,
  open attempt, or physical handle remains live.
- Replace the ambient `ICovenantMaintenanceConnectionFactory` API with exact operation-specific
  legacy V3 entry points plus the new capability-bound Grimoire factory.
- Add an exact bidirectional acquisition-callsite inventory with individual classifications.
- Update `docs/Arcanum.DESIGN.md`, the approved #239 umbrella status, and this child specification
  with the delivered boundary. The root README is the curated public GitHub front page and is not
  maintained as part of implementation delivery.

### 2.2 Excluded

- V4/V2 launch codecs, immutable launch fields, and terminal operation-row reconciliation: #247.
- Journal-driven Covenant reset/factory handlers, effect publication, compaction ambiguity handling,
  closed-period checkpoint removal, and production handler activation: #248.
- Installation-reset parent receipts and final credential cleanup: #249.
- Pre-readiness recovery dispatch and launch-gap adoption: #250.
- HTTP request middleware and stable maintenance responses: #251.
- Stream quiescence: #252.
- Entry weaving, attachment indexing, Saga, and remaining hosted-producer work admission: #253-#256.
- Parent-wide full-host and cross-platform qualification: #257.
- Any API route, request or response DTO, CLI verb, configuration key, schema object, numbered
  migration, or data backfill.
- Any edit, state transition, project-field change, reparenting, closure, or resolution claim for
  issue #242.

## 3. Inherited baseline

Issue #245 supplies `IGrimoireConnectionAdmissionGate`, generation-bound request/work/open tickets,
exact closing and closed owners, maintenance lanes, one-shot maintenance capabilities, tracked
maintenance handles, and gate dispositions. The current branch also contains an earlier EF overlay:

- `ArcanumDbContextOptionsConfigurator` attaches `CovenantConnectionEnrolmentInterceptor`;
- both serving `AddDbContext` and `AddDbContextPool` registrations pass the same singleton gate and
  drain;
- the interceptor acquires before provider open, revalidates after provider open, initializes and
  enrolls only an admitted handle, closes a race loser, and releases on failure, close, disposal, and
  cancellation; and
- focused tests already cover much of that lifecycle.

That baseline does not deliver #246. At least twelve serving raw call sites bypass EF interception,
the gate does not compose the physical drain before returning a closed lease, the current Covenant
maintenance factory accepts unrestricted caller-selected opens, and the existing source inventories
use file-level exemptions rather than exact call-site equality.

Issue #245 also does not prevent a zero-handle active maintenance lane from surviving closed-lease
disposition. Adding that guard and proving its ordering from RED is new #246 behavior, not inherited
baseline.

## 4. Serving EF acquisition

Every serving `ArcanumDbContext` options path has exactly one
`CovenantConnectionEnrolmentInterceptor`. Its `_admissionGate`, `_drain`, lifecycle, and initializer
references are the process-wide service instances for that composition. The host pool, CLI
composition, and API test-host replacement are positive serving paths. Those compositions also prove
that `SqliteNativeRuntime.Instance.Initialize()` completes before any serving EF provider open.

The following are not serving EF paths and remain individually catalogued rather than broadly
exempted:

- `ArcanumDbContextFactory.CreateDbContext` for design-time scratch creation;
- `ArcanumDbContext.OnConfiguring` as the named non-DI fallback;
- `InstallationResetExistingGrimoire.ExecuteAsync` under an authenticated stopped-host lock; and
- pre-readiness bootstrap paths that run before the serving gate is available.

`InstallationResetExistingGrimoire` does not retain its current implicit lock assertion. The outer
installation-reset boundary mints an exact stopped-host authority only after the live
`ArcanumMaintenanceLock` passes `AssertHeldFor` on the Grimoire directory and carries that authority
explicitly through plan, identity-read, evidence-read, and apply paths into `ExecuteAsync`. Its
unpooled EF open consumes the authority before provider open. A call that cannot carry the exact live
lock-derived authority is refused with zero native open.

Local installation-reset planning becomes explicitly stopped-host-only. A new internal
`IGrimoireCliStoppedHostInitialization` boundary leaves the general public CLI-initialization contract
unchanged, acquires the existing exclusive installation and client-mutation locks, and passes an
operation-scoped `IStoppedHostGrimoireAuthorityIssuer` into its callback. `InstallationFactoryResetCommand`
uses that boundary before invoking a new internal locked-plan entry point on `InstallationResetService`;
the service carries the issuer through workspace resolution, data planning, database identity, and
host-tools evidence reads and mints a separate one-shot capability for each open. The existing public
`PlanAsync` entry cannot perform one of those local database reads without the issuer.

Lock contention is the deliberate outcome when a host is running: local `--dry-run`, confirmation
planning, and apply planning fail before a local provider open and instruct the operator to stop the
host. #246 does not add an API fallback, endpoint, or DTO and does not treat an authenticated HTTP
plan as stopped-host authority. Once the host is stopped, the lock-derived local plan is authoritative;
the command does not revalidate that plan by contacting the now-absent host.

The locked planner returns an internal, non-serialized `StoppedHostInstallationResetPlan` containing
the public `InstallationResetPlan` plus the exact local `DataRetentionPlan.Covenant` disclosure. A
Global or All plan without that disclosure is refused. The CLI writes that local disclosure at
confirmation and does not call `IInstallationResetOnlinePlanValidator`, `BindOnlineDataPlan`, or
`CreateHostHandoff` for a fresh stopped-host plan.

Fresh Global and All apply likewise use the already-supported local under-lock data path. The apply
boundary skips its pre-lock database pair read and online factory-reset call, stops or confirms absence
of the host, reacquires the exact installation lock, and calls `ApplyOfflineAsync` with no host handoff.
Its existing client-coordination lease remains in force. `InstallationResetService`
revalidates the marker pair, replans through the lock-derived issuer, compares the exact confirmed plan
ID, publishes the active record, and invokes the local `IInstallationResetDataService.ApplyAsync`
branch. Existing authenticated active records or host handoffs keep their strict recovery semantics;
the fresh stopped-host route does not reinterpret them.

`IGrimoireOrdinaryConnectionLifecycle`, implemented as the singleton
`GrimoireOrdinaryConnectionLifecycle`, becomes the shared provenance owner used by both the
interceptor and ordinary raw factory. It retains one weak lifecycle state per physical `DbConnection`,
the matching gate generation, explicit native-open state, and reference-counted drain enrollment.
Multiple logical holders may borrow the same admitted connection; one holder's release must not
unregister another holder. Further helper extraction beyond this required shared lifecycle is allowed
only when a failing test demonstrates behavior that would otherwise be duplicated incorrectly;
cosmetic deduplication is not part of #246.

## 5. Serving raw ordinary acquisition

### 5.1 Factory contract

`IGrimoireOrdinaryConnectionFactory`, implemented by `GrimoireOrdinaryConnectionFactory`, is the only
serving raw-live-Grimoire opener. It and the EF interceptor use the singleton lifecycle above. The
factory supports two explicit internal shapes:

- acquire an already-constructed scoped `SqliteConnection`; or
- construct a fresh live-Grimoire connection from one closed internal request kind whose path and
  connection-string policy are owned by the factory rather than supplied as arbitrary caller text.

Both shapes return an `IGrimoireOrdinaryConnectionLease`. The lease exposes the admitted connection
and owns the exact open ticket, drain enrollment, and connection lifetime selected by the request.
Callers cannot receive a successful raw connection while discarding its admission lifetime.

An already-open scoped connection is accepted only when the shared lifecycle proves that the EF
interceptor or an extant ordinary lease admitted that exact physical open in the current generation.
The factory then creates a reference-counted borrow lease without claiming a second native open. An
already-open connection with missing, stale, failed, or different-generation provenance is refused
without use; it is never retroactively declared admitted. A closed scoped connection follows the full
ticket-before-open protocol below.

The factory protocol is:

1. validate the closed internal request before provider construction;
2. initialize `SqliteNativeRuntime.Instance` before any factory-owned provider construction and
   before every native open;
3. ask the shared lifecycle to borrow an already-admitted current-generation open or begin one new
   admitted physical open;
4. for a new open, acquire `IGrimoireConnectionOpenTicket` before native open;
5. perform the one native open attempt;
6. immediately call `RevalidateAfterNativeOpen` before initializer, command, or caller use;
7. if revalidation loses the generation race, call `CloseAsync`, clear that exact
   `SqliteConnection` pool, observe the connection closed, and only then report
   `MarkRefusedAfterOpen`;
8. initialize and verify the accepted SQLCipher connection through the existing centralized
   initializer when the selected request requires it;
9. enroll the physical handle with the process-wide drain;
10. report `MarkOpened`; and
11. return the lease.

Failure before native open reports `MarkFailed` and disposes the ticket. Failure or cancellation after
native open follows the same close-then-exact-pool-clear sequence, terminalizes the ticket once, and
removes any enrollment once. Native-runtime initialization failure performs zero provider open and
returns no connection.
Lease disposal physically closes a factory-owned fresh connection and releases its enrollment. A
lease that opened a previously closed scoped connection closes that raw open and releases its
enrollment while leaving final context disposal to the scoped owner. A borrow lease over an already
admitted EF open releases only its additional reference; the EF interceptor remains responsible for
the physical open it admitted and the drain may still close that registered handle during stage two.

`CovenantConnectionSource` preserves its existing bare-connection consumer contract by retaining its
factory-issued scoped lease as an instance field. It returns only that lease's connection and releases
the lease from `Dispose`; no downstream caller can accidentally discard the admission lifetime.

Closed admission uses the existing `GrimoireMaintenanceUnavailableException`. Stable HTTP mapping is
not added here; #251 owns that boundary.

### 5.2 Positive raw migration

The initial positive serving-raw inventory includes these current bypass classes and members:

- `GrimoireLivenessProbe.ExecuteProbeAsync`;
- `WizardIntelligenceProvider.JoinWorkspaceChunkMetadataAsync`;
- `MemoryEndpoints.OpenConnectionAsync`;
- `SessionDivinationEndpoints.JoinSessionMetadataAsync`;
- `WorkspaceDivinationEndpoints.JoinWorkspaceChunksAsync`;
- `CovenantCampaignScopeProbe.HasDeletionEventAsync`;
- `CovenantConnectionSource.GetOpenCoreConnectionAsync`;
- `CovenantDisclosureWriter.OpenVerifiedAsync`, retaining its long-lived ordinary lease until writer
  close or disposal;
- `GrimoireRepository.TurnCommit.CommitWithinImmediateTransactionAsync`;
- `LongRunningOperationStore.RenewLeaseAsync` for its ordinary live heartbeat;
- `SessionEntryPersistence.ReadProbeOnFreshConnectionAsync`;
- `SessionEntryPersistence.ReadReceiptOnFreshConnectionAsync`; and
- `EmbeddingsResetService.PurgeLabeledKindAsync`.

The inventory test, not this prose list, is authoritative after implementation. If source discovery
finds another serving raw live-Grimoire acquisition, it joins the factory migration in this child; it
does not receive a temporary exemption.

Live-database reads in backup or diagnostics code are ordinary serving acquisitions unless an existing
retained authority proves the host is stopped or pre-readiness. An archive, extracted generation,
snapshot destination, compaction staging file, or restore staging file is not the live Grimoire and is
catalogued under its exact path authority.

## 6. Physical drain and closed proof

`CloseConnectionAdmissionAsync` becomes the sole stage-two process-local closure operation. Its
ordering is fixed:

1. verify the exact closing owner and completed stage-one request/work drain;
2. advance the generation and close ordinary admission;
3. request refusal of every unresolved open ticket;
4. await every ticket's explicit terminal callback;
5. invoke the existing process-wide singleton `ICovenantConnectionDrain.DrainAsync` and require
   success; that one drain owner
   snapshots and closes every enrolled handle, calls `SqliteConnection.ClearAllPools()` after handle
   closure and never before, and rejects any enrolled handle never observed physically closed;
6. while holding the gate lock that serializes maintenance/adoption interlock acquisition, verify the
   same owner, closed generation, empty unresolved-open set, successful drain, and empty interlock;
   and
7. reserve and issue `IGrimoireExclusiveClosedLease` before releasing that lock.

The returned closed lease is the process-local proof of this ordering. #246 does not add a competing
journal evidence type. #244 owns the lifecycle evidence shape consumed later, and #248 owns WAL
truncation, sidecar absence, compaction, candidate verification, and effect publication.

The process-local closed lease composes with, and does not replace, #124's existing Covenant
eligibility/exclusive-operation lease. The offline-transition owner must hold both exact authorities in
the order defined by the parent design. The process-local drain also does not replace #126's residual
storage proof: #126 proves physical database/sidecar state after acquisition is closed, while #246
proves only that this process has no admitted ordinary handle or pool able to race that proof.

A drain error, timeout, or cancellation issues no closed lease and never silently reopens admission.
The exact owner may retry the repeatable stage-two closure or a later journal-driven handler may retain
`KeepClosed`. No caller may bypass stage two by invoking the drain directly for a new offline
transition.

`ICovenantConnectionDrain` remains the only reference-counted, process-wide enrolment registry; #246
does not create a second Grimoire drain interface or implementation. Composition tests prove that the
EF interceptor, ordinary factory, and stage-two gate receive that exact singleton. Pool-clear tests,
including exact-pool clearing for refused or failed opens, remain serialized because they mutate
provider pool state and `SqliteConnection.ClearAllPools()` is process-global.

## 7. Maintenance acquisition

### 7.1 Capability-bound factory

`IGrimoireMaintenanceConnectionFactory` is the authoritative journal-era factory. It accepts one
`IGrimoireMaintenanceConnectionCapability` and the exact `IGrimoireMaintenanceIoLane` that caused the
closed owner to issue it. The requested canonical path, mode, and purpose are fixed by a narrow
operation-specific method; callers do not pass arbitrary authority text.

Before constructing a provider connection, the factory consumes the capability against:

- `lane.Owner`;
- `lane.Generation`;
- the factory-owned canonical path;
- the method's fixed `CovenantMaintenanceConnectionMode`;
- the method's fixed `CovenantMaintenanceConnectionPurpose`; and
- the exact lane instance.

A mismatch returns a typed failure with zero provider construction and zero native-open calls. A
successful consume is one-shot.

The factory calls `SqliteNativeRuntime.Instance.Initialize()` before provider construction or open.
Initialization failure performs zero native open, reports the consumed handle not opened exactly
once, and returns no lease.

The factory returns an `IGrimoireMaintenanceConnectionLease` rather than a bare
`SqliteConnection`. The lease owns both the unpooled connection and
`IGrimoireTrackedMaintenanceHandle`. The factory reports `ReportNotOpened` only when provider open has
not started. Immediately before native open it reports `ReportOpenStarted`. Any later failure or
cancellation physically disposes the connection before `ReportPhysicallyClosed`. Successful lease
disposal physically closes first and reports closure exactly once.

Every maintenance connection is unpooled, one-shot, initialized, and physically closed before its lane
can release. #246 implements only fixed-purpose operations against the injected canonical live
Grimoire path. Compaction, staging, archive, and other side-file connection methods remain absent
until #247/#248 can supply their typed immutable launch-bound target identity; a caller-selected path
is not a temporary substitute. No passphrase or unrestricted connection string leaves the factory.

### 7.2 Lane and disposition invariants

A maintenance lane revokes its unused one-shot capabilities when disposal begins, waits for all
tracked handles to report terminal physical state, and only then releases the maintenance/adoption
interlock.

Lane emptiness has two exact proof boundaries. Stage-two closed-lease issuance requires the
maintenance/adoption interlock to be empty, which proves the closed generation begins with no lane.
After maintenance begins, successful completion of `IGrimoireMaintenanceIoLane.DisposeAsync` is the
only proof that the owner lane and all its handles are empty. Closed-lease disposition requires that
completed release and cannot infer it from a zero handle count.

`IGrimoireExclusiveClosedLease.CompleteAsync` refuses while any of the following belongs to its
closure:

- an unresolved ordinary open;
- an active scoped connection permit;
- an unused live one-shot authority;
- a live tracked maintenance handle; or
- an active or disposing maintenance lane whose release has not completed.

Recovery adoption and a maintenance lane remain mutually exclusive. A factory blocked on physical
close retains the lane and prevents adoption; an adopter that owns the interlock prevents provider
construction by a competing maintenance acquisition.

### 7.3 V3 transition boundary

The existing V3 same-database reset remains the active runtime until #247/#248 replace it. #246 must
not add temporary closed-period heartbeat, checkpoint, or revision authority that #248 is assigned to
remove. New journal-era transitions cannot invoke the physical drain directly; stage two is their only
path.

The ambient `ICovenantMaintenanceConnectionFactory.Open*` API is nevertheless removed. The current
coordinator mints a sealed, one-shot `CovenantV3MaintenanceCapability` only from the exact live #124
`ICovenantExclusiveOperationLease`. It binds the recovery owner, operation, and one fixed maintenance
purpose. Existing V3 call sites are split into exact operation-specific legacy methods that require
that capability and return an unpooled tracked lease; callers cannot select an arbitrary path, mode,
or purpose. Each legacy acquisition is individually catalogued as `LegacyV3Maintenance`, names the
exact consuming member and #248 as its removal owner, and cannot be used by a new caller without a
failing inventory test. Existing V3 direct drain calls are catalogued the same way and are the only
temporary exception to journal-era stage-two ownership. Pre-close health or inventory reads that are
ordinary serving work move to the ordinary factory instead of the legacy maintenance adapter.

Only call chains already holding the exact #124 exclusive lease qualify for this adapter.
`CovenantDisclosureWriter` is ordinary serving work and migrates to the ordinary factory with a
retained long-lived lease. `HostToolsMarkerPairResetDatabase` is a stopped-host installation-reset
open and uses the separate lock-bound contract in section 7.4. Any other ambient-factory consumer
found without #124 authority must likewise migrate to its real ordinary or exact non-serving route;
it cannot manufacture a V3 capability.

The capability-bound factory is the only maintenance factory available to the journal-driven handler
that #248 will activate. #248 removes the exact legacy V3 entries when it adapts the kernels and
eliminates closed-period database status writes. This temporary boundary is explicit per call site,
not a directory, type-name, factory-name, or `Pooling=false` exemption.

### 7.4 Stopped-host installation-reset acquisition

`IStoppedHostGrimoireConnectionAuthority` is a sealed, one-shot authority minted only from the exact
live `ArcanumMaintenanceLock` after `AssertHeldFor` succeeds for the guarded Grimoire directory. It is
bound to the canonical database path, read-only or read-write mode, installation-reset operation,
and one fixed purpose. It has no ordinary gate generation or maintenance lane because the serving
host is absent; the live OS lock is the mutually exclusive owner proof.

The internal CLI planning boundary and the outer apply/reset methods that already receive the held
lock mint and pass this authority explicitly. `InstallationResetExistingGrimoire.ExecuteAsync`
consumes one for each unpooled EF open. `HostToolsMarkerPairResetCoordinator` passes the same live lock
into its database-open boundary, which mints a separate one-shot authority for
`HostToolsMarkerPairResetDatabase.OpenAsync`. That database uses a narrow stopped-host factory
returning an unpooled tracked lease; the session owns the lease and physically disposes it. No opener
obtains the held lock from ambient process state, and no authority can survive disposal of the lock
from which it was minted.

These exact call chains are catalogued as `StoppedHostRecovery`, not `LegacyV3Maintenance`. Their
tests prove wrong lock identity, wrong root, wrong path, wrong mode, reuse, and disposed-lock attempts
perform zero provider open.

## 8. Exact acquisition inventory

`GrimoireConnectionAcquisitionInventoryTests` owns a finite catalog of every authored production
acquisition. Each entry contains:

- normalized repository-relative file;
- enclosing type and member;
- one construct fingerprint that resolves to exactly one authored call site;
- target path authority;
- acquisition kind; and
- runtime admission route or exact non-serving proof.

The closed path-authority set is:

- `LiveGrimoire`;
- `StoppedHostGrimoire`;
- `PreReadinessGrimoire`;
- `ShutdownGrimoire`;
- `ArchiveOrSnapshot`;
- `RestoreOrCompactionStaging`;
- `DesignTimeScratch`; and
- `NativeRuntimeValidation`; and
- `NotGrimoire` for one exact syntax candidate proven not to acquire a database connection.

The closed acquisition-kind set is:

- `ServingEfOrdinary`;
- `ServingRawOrdinary`;
- `JournalMaintenance`;
- `LegacyV3Maintenance`;
- `BootstrapOrShutdown`;
- `StoppedHostRecovery`;
- `StagingOrArchive`; and
- `DesignTimeOrNativeValidation`; and
- `NonGrimoireCandidate` for one exact broad-scanner match with an authored negative proof.

The test project adds an exact test-only `Microsoft.CodeAnalysis.CSharp` dependency at version
`5.9.0` with `PrivateAssets=all`. The scanner parses each authored source with Roslyn syntax trees; it
does not use reflection, a semantic compilation, or production runtime code.

Direct discovery is deliberately syntactic and conservative. It finds authored `UseSqlite` and
`AddDbContext*` option paths, invocation expressions whose terminal member is `Open`, `OpenAsync`,
`OpenConnection`, or `OpenConnectionAsync`, and `DbConnection`/`SqliteConnection` object creation.
Unrelated same-named constructs discovered by that closed syntax vocabulary receive their own exact
non-Grimoire catalog classification rather than being silently filtered by guessed type semantics.

Approved indirect acquisition routes use an internal, compile-time-only
`GrimoireConnectionAcquisitionRouteAttribute` on the exact factory/source method declaration. The
syntax scanner derives the marked method name and arity, discovers every matching invocation, and
requires each marked route method name to be repository-unique and an exact catalog entry for the
declaration and each call. Direct `DbConnection`/`SqliteConnection` construction and provider-open
identities remain direct-catalog-covered and do not receive route markers. A marker instead binds each
concrete opaque lease/session boundary—`IGrimoireOrdinaryConnectionLease`,
`IGrimoireMaintenanceConnectionLease`, `IStoppedHostGrimoireConnectionLease`,
`ICovenantV3MaintenanceConnectionLease`, or `HostToolsMarkerPairResetDatabaseSession`—including those
types recursively wrapped by `Task<T>`, `ValueTask<T>`, or `Result<T>`. A helper that can only return a
failure is not an acquisition boundary; an already-owned `BorrowCoreConnection` is likewise not one.
The attribute is never inspected by production runtime code and creates no reflection or AOT dependency.

Marker coverage applies only to concrete method or local-function acquisition implementations that
have a body. Declaration-only interface, abstract, and partial contracts are excluded because they
contain no executable acquisition route for the syntax scanner to resolve. This is the syntax-only
resolution of the approved text's duplicate-name contradiction: it does not exempt an executable
route, broad directory/type grouping, or a concrete call site from its exact catalog identity.

### 8.1 Ledger ruling

**Ruling:** direct bare-connection routes are direct-catalog-covered; markers are only opaque
lease/session boundaries. The cost of a mistaken rule is a missed indirect route, bounded by the
bidirectional direct/marker equality proof.

Discovery identities use normalized file, syntax-derived enclosing type/member declaration, syntax
kind, normalized callee or constructed-type text, arity, and a normalized construct fingerprint rather
than line number so ordinary formatting does not invalidate the catalog. The scanner never claims
symbol or overload resolution that syntax alone cannot provide.

Non-serving path labels are backed by named evidence, not accepted on assertion alone:

- `StoppedHostGrimoire` requires the explicit one-shot authority from section 7.4 and its retained
  live `ArcanumMaintenanceLock` to pass `AssertHeldFor` on the guarded Grimoire directory;
- `PreReadinessGrimoire` requires the exact `GrimoireDatabaseBootstrapper` entry path under the held
  installation lock before `IGrimoireDbReadiness.MarkReady`, with a call-order test pinning that path;
- `ShutdownGrimoire` requires the exact hosted-service shutdown entry point under the still-held
  installation lock, with a sole-caller inventory assertion;
- `ArchiveOrSnapshot` and `RestoreOrCompactionStaging` require the existing typed staging/snapshot
  path authority and an assertion that the target is not the canonical live Grimoire path;
- `DesignTimeScratch` requires the design-time factory and its non-product temporary target; and
- `NativeRuntimeValidation` requires `SqliteNativeRuntimeValidator`'s isolated probe target and never
  the live Grimoire path; and
- `NotGrimoire` requires an exact per-fingerprint negative proof naming the non-database API or
  resource. It cannot be granted by directory, namespace, receiver-variable name, or a wildcard over
  multiple candidates.

The suite asserts all directions:

1. every discovered acquisition equals one catalog entry;
2. every catalog entry resolves to exactly one discovered acquisition;
3. every `ServingEfOrdinary` entry reaches the shared interceptor composition;
4. every `ServingRawOrdinary` entry reaches `GrimoireOrdinaryConnectionFactory`;
5. every `JournalMaintenance` entry reaches the capability-bound factory;
6. every legacy or non-serving entry names one exact path authority and proof; and
7. every `NonGrimoireCandidate` resolves to one broad-scanner candidate and one exact negative proof;
   and
8. no directory, namespace, type, factory name, or pooled/unpooled property exempts multiple call
   sites.

Scanner unit tests inject an unlisted acquisition, a stale catalog record, and a misclassified live
acquisition and observe independent failures. Production source is never modified by a mutation test.

## 9. Error and cancellation behavior

- Refusal before native ordinary open performs zero provider open calls.
- A generation-race loser is closed, its exact provider pool is cleared, and only then is its ticket
  terminalized and refusal returned.
- Ordinary initializer failure or cancellation closes, clears the exact provider pool, and releases
  enrollment and ticket state exactly once.
- Stage-two drain failure leaves admission closed under the exact owner and issues no closed lease.
- Maintenance capability mismatch performs zero provider construction or open.
- Maintenance construction failure before native open reports not-opened once.
- Maintenance failure or cancellation after open-start physically closes and reports closed once.
- Lane disposal cannot manufacture physical closure and waits for the real handle terminal signal.
- Closed-lease disposition with a live lane or handle is a lifecycle conflict and cannot reopen
  ordinary admission.
- Expected maintenance refusal remains content-free at this layer. #251 owns stable HTTP envelopes.
- Stopped-host authority mismatch, reuse, or disposed-lock observation performs zero provider open.

## 10. TDD strategy

New production behavior is written only after a focused test has failed for the expected missing
guarantee. Existing EF behavior is retained when its current test already proves it; new EF production
changes require a new RED first.

The implementation proceeds in these test-first slices:

1. exact inventory discovery and bidirectional-catalog RED over the current unclassified raw and
   ambient maintenance acquisitions;
2. serving EF composition/runtime audit, adding RED only for a missing singleton or lifecycle proof;
3. ordinary raw factory refusal, post-open generation loss, initializer failure, cancellation,
   enrollment, and exact disposal RED/GREEN cycles;
4. stage-two ordering RED proving ticket terminalization precedes handle drain, pool clearing precedes
   closed-lease issuance, and late EF/raw opens cannot recreate a live race;
5. closed-lease/lane RED proving disposition refuses an active lane even with zero handles;
6. maintenance factory owner/generation/path/mode/purpose/lane mismatch, one-shot, open failure,
   cancellation, and physical-close RED/GREEN cycles;
7. stopped-host authority propagation and wrong-lock/path/mode/reuse RED/GREEN cycles;
8. stopped-host CLI planning contention and success RED/GREEN cycles proving the running-host path
   performs no local provider open and the held-lock path never contacts the host API;
9. fresh stopped-host Global/All apply RED/GREEN cycles proving changed replans or Covenant
   disclosures fail before active publication, marker-pair revalidation uses the reacquired exact lock,
   the client-coordination lease spans local apply, no online validator/API/handoff is reached, and
   authenticated active records or historical handoffs cannot enter or be reinterpreted by the fresh
   path;
10. exact production call-site migration until the permanent inventory test turns GREEN; and
11. documentation and scope assertions.

All ordering tests use deterministic barriers, `TaskCompletionSource`, controlled time, or explicit
seams. They do not use sleeps. Pool tests use the existing serialized SQLite pool collection.

## 11. Verification and delivery

Focused tests run after each RED/GREEN slice with `--disable-build-servers -m:1`. Repository-wide
qualification is reserved for #257 on the final reviewed umbrella SHA. The #246 child tree receives:

- focused admission, interceptor, raw factory, drain, maintenance factory, composition, and inventory
  tests;
- a Release solution build with zero warnings and zero errors;
- changed-file C# blank-line verification;
- `git diff --check`;
- a requirements audit against issue #246 and this specification; and
- per-task review plus one bounded read-only review of the #246 child branch.

Threshold coverage, the complete Arcanum suite it supplies, fresh Native AOT/IL evidence, the Covenant
benchmark, native SQLCipher provenance, full-host, packaging, and cross-platform matrices are not
duplicated here because the approved umbrella assigns them to #257's unchanged final SHA. A focused
#246 failure may justify one diagnostic command but does not turn that diagnostic into parent-wide
qualification evidence.

Task 12 owns final child review and the Release build gate. Until those gates complete, #246 remains
**in progress — final child qualification pending** and no delivered/Done claim, issue closure, project
transition, merge, push, or branch deletion follows from this implementation task. #247 retains V4/V2
launch binding and terminal database reconciliation; #248 retains typed Covenant handler activation,
compaction and sidecar recovery, effect publication, and V3-adapter removal; and #257 retains the
final umbrella qualification. Issue #239 remains open and in progress. Issue #242 remains unchanged.
