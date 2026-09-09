# Issue #256 Hosted Grimoire Producer Admission

**Status:** Approved.

**Branch:** `codex/issue-256-hosted-producer-admission`, cut from `grimoire-fixes` at
`6d50266a` (the #255 merge).

**Issue:** [#256 — Grimoire: protect every remaining hosted database/effect producer](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/256)

**Parent:** [#239 — A Grimoire connection is enrolled in the Covenant drain only as a side effect](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/239)

**Parent design authority:**
`docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`, especially
§6.2, §6.3, §6.4, §9.4, and §10.3. The worker precedents are issues #253, #254, and
#255. Where this child and the parent disagree, the parent governs, except for the explicit,
user-approved Batch accounting-page refinement in §1.4.

## 1. Decision

### 1.1 What this child delivers

Issue #256 completes the production adoption of the process-local Grimoire work lease and atomic
external-effect frontier. It covers the six services named by the issue and every additional
first-party hosted startup, recovery, backup, maintenance, database, provider, or filesystem path
found by the required exhaustive inventory.

The implementation has four inseparable parts:

1. a bidirectional, exact hosted-producer inventory tying every application `IHostedService`
   registration to every relevant scope, live-open, provider, and filesystem-effect call site;
2. explicit work admission for every ordinary runtime producer, acquired before its first DI scope
   and held through its durable disposition and asynchronous scope disposal;
3. exact owner-bound classification for nonordinary startup, recovery, stopped-host, and maintenance
   operations that cannot race an offline transition; and
4. deterministic race and resumption tests for each ordinary producer and exact-classification
   tests for every exception.

`InstallationResetRecoveryAwareHostedService<T>` is retained, but is not treated as runtime
protection. It makes one decision while a hosted service starts and delegates `StopAsync`; it cannot
revoke a producer that is already alive.

### 1.2 Why explicit producer boundaries

Three designs were considered.

**One lease around a whole service or whole poll loop** is simple, but an idle, long-lived host would
hold maintenance open indefinitely. It also gives no boundary at which a provider or filesystem
effect either wins atomically or does not start.

**A generic hosted-service wrapper or ambient interceptor** centralizes registration, but cannot
infer a queue identity, a durable page, a staged generation, a retention journal candidate, or the
database write that makes a provider result resumable. It would repeat the readiness-only mistake
the parent explicitly rejects.

**Explicit producer-local work and effect boundaries** are therefore approved. The common gate owns
only admission and atomicity. Each producer keeps its own local disposition and resumption state,
following #253–#255 rather than introducing a shared worker-outcome vocabulary.

### 1.3 Scope exclusions

The hosted-producer architecture itself adds no HTTP route, CLI verb, option, schema object, numbered
migration, public status, or JSON discriminator. It does not change retention policy, Tapestry
algorithms, Batch accounting policy, or long-running-operation recovery semantics.

Final qualification exposed user-approved adjacent defects that had to be repaired before #256 could
ship: Native AOT startup/runtime-package provenance and compiled-model compatibility, redirected
initial-run input handling, and factual per-model tool capability. Those repairs add the
`supportsTools` model field, `ClientTools.ModelUnsupported`, and additive capability projection on the
existing model-list DTO; they do not add a route or CLI verb. The declarative schema remains the
authority and gains no #256-specific object; documented v8/v9/v10 convergence corrects the existing
compiled-model/bootstrap contract rather than introducing a numbered runtime migration.

The five-second work-drain timeout remains unchanged. A provider effect that legitimately wins the
frontier may exceed it, in which case the transition fails safe with the existing
`Grimoire.WorkDrainTimeout`, performs no offline effect, and aborts closure under the parent design.

Issue #257 retains full-host races, Windows evidence, final Native AOT/cross-platform umbrella
qualification, and closure of #239. This child runs the complete locally applicable macOS checks for
its own reviewed merge, but does not claim #257's evidence or close #239.

### 1.4 Approved Batch accounting-page refinement

The parent asks for the smallest independently resumable durable effect unit. A Batch line has its own
dispatch marker and terminal response checkpoint, but it does not have independent accounting:
reservation, ambient accounting, and completion are shared by the existing 64-line page. Treating one
line as the complete effect unit would either release the frontier before the page's billable
disposition or require a broader redesign of accounting and concurrent effect authority.

The user therefore explicitly selected one external-effect group per existing 64-line accounting
page. This is the smallest unit that includes both provider results and their current durable billing
disposition. Line concurrency and line checkpoints remain unchanged; the page group is the admission
frontier around them. This child does not redesign Batch billing to manufacture a smaller unit.

## 2. Existing gap

The gate currently accepts only `SessionAttachmentIndexing`, `EntryWeaving`, and `SagaExtraction`.
Those are the three children already delivered. Every other hosted producer can still create a scope
or begin an external effect after closing starts, or can hold a handle that stage one never knew to
drain.

The current architecture checks are insufficient in two different ways:

- `CovenantResetBootstrapBarrierTests.GatedHostedServices` names only six registrations and proves
  startup order, not runtime admission; and
- `GrimoireConnectionAcquisitionInventoryTests` proves how low-level EF and raw SQLite opens are
  routed, but not that a hosted caller held a request, work, startup, recovery, stopped-host, or
  maintenance authority before reaching that route.

Several hosts are mixed rather than globally ordinary or globally exceptional. Batch startup
recovery is awaited before `BackgroundService.StartAsync`, while Batch ticks are ordinary runtime
work. `LongRunningOperationStartupHostedService.StartAsync` runs one awaited pre-readiness pass and
then launches a tracked periodic pass. MCP bootstrap may be awaited pre-readiness or deliberately
continue after startup. These require member-level classifications; a class-name allowlist would be
false evidence.

Two correctness bugs are also exposed by placing exact frontiers:

- Simulacrum advances the Apprentice checkpoint before it stamps spawned children and runs Shifting
  Fate, so a restart can permanently skip those post-effects; and
- nonblocking MCP bootstrap discards its `Task.Run`, while `StopAllAsync` does not await that host
  task, so startup may race shutdown and its exception/lifetime is not owned.

Both defects are fixed in this child, test first.

## 3. Exact hosted-producer inventory

### 3.1 One service entry, exact operation entries

The test project owns a `HostedGrimoireProducerInventory`. It has exactly one service entry for each
of the 23 application hosted-service registrations produced by `AddArcanumApiServices`. A service
entry contains one or more exact operation entries, because one service may mix lifecycle and
ordinary runtime paths.

An operation entry identifies:

- the production-relative file, fully qualified enclosing type, and exact member;
- each relevant scope, marked Grimoire acquisition route, provider call, or filesystem-effect call;
- one authority classification;
- an exact `GrimoireWorkKind` for ordinary hosted work; and
- exact proof evidence for a nonordinary classification.

The authority classifications are:

- `OrdinaryHostedWork` — must acquire its declared work kind before any scope and use an external
  effect group where the entry lists an external effect;
- `FiniteRequest` — reached only through the already-admitted finite request lifetime, which stage
  one drains through request-scope disposal;
- `PreReadinessStartup` — the Generic Host awaits the exact member before publishing readiness;
- `OwnerBoundRecovery` — runs only under the exact authenticated recovery owner and installation
  maintenance lock;
- `StoppedHost` — runs only after the serving host is stopped and under the exact stopped-host
  authority named by the entry;
- `OwnerBoundMaintenance` — uses the exact generation-bound maintenance lane from the parent design;
  and
- `EffectFree` — has no Grimoire scope/open and no provider/filesystem effect.

There is no broad `Startup`, `Recovery`, `Maintenance`, directory, namespace, factory, or wrapper
exemption. Proof names the exact owning member and authority path. A shutdown classification is
accepted only as an exact stopped-host path, never because a method is named `StopAsync`.

### 3.2 Bidirectional discovery and validation

The inventory follows the existing Roslyn acquisition inventory rather than relying on comments. Its
production validation builds the Core, Secrets, Infrastructure, Api, and Cli source compilations so
hosted registrations, endpoint-invoked hosted operations, live Backup command roots, and every
dependency-reachable first-party body share one semantic call graph.
Discovery covers:

- direct `AddHostedService` registrations and the closed generic argument to
  `AddInstallationResetRecoveryAwareHostedService<T>`;
- authored lifecycle roots for every discovered hosted service, exact externally invoked hosted
  operations such as workspace `QueueIndexNow`, and the explicit non-hosted Backup/recovery roots;
- `CreateScope` and `CreateAsyncScope` in hosted entry points and their catalogued collaborators;
- the marked ordinary, maintenance, and stopped-host connection routes already discovered by the
  connection-acquisition scanner;
- calls through the provider boundaries used by these producers; and
- filesystem opens, moves, replacements, deletes, and encrypted-blob publication used by these
  producers.

Validation is bijective. It reports independent failures for an unregistered catalog service, an
uncatalogued registered service, an uncatalogued discovered site, a stale site, duplicate service or
site identity, a wildcard/broad identity, missing proof, shared proof, an ordinary site without the
declared lease/effect frontier, and a nonordinary site whose evidence does not match its member.

The existing low-level connection inventory remains authoritative for physical-open routing. The new
inventory joins caller authority to those routes; it does not copy or weaken their 431-entry catalog.
In particular, `BackupDatabaseSnapshotter`'s source connection being labelled
`OrdinaryConnectionFactory` is not itself caller authority. The new entry must prove its finite
request or durable-operation caller and the held backup/Covenant read lifetime.

### 3.3 Closed work-kind vocabulary

`GrimoireWorkKind` gains the following non-zero values, while preserving the numeric values of the
three existing members:

- `WorkspaceIndexing`
- `TapestryWeaving`
- `BatchProcessing`
- `UnseenServant`
- `ApprenticeExecution`
- `DataRetentionSweep`
- `LoremasterSummarization`
- `A2ASendingLeaseRenewal`
- `CovenantMaintenance`
- `GrimoireSchemaTransition`
- `LongRunningOperationRecovery`
- `ProviderHealthProbe`
- `McpServerBootstrap`

The gate validates this exact closed list. There is no `Unknown`, `Other`, wildcard, or caller-supplied
string. External-effect groups remain intentionally untyped: their identity is the owning work lease
plus the exact source call site in the inventory, so no second runtime effect enum is introduced.

## 4. Ordinary work and deferral protocol

### 4.1 Ordering

Every ordinary operation follows this order:

1. read `CurrentGeneration` before attempting admission;
2. call `TryAcquireWorkLease` with the exact catalogued kind;
3. when admitted, declare the lease before every scope it owns so reverse disposal closes all scopes
   before releasing the work lifetime;
4. perform DB-only discovery or preparation under that lease;
5. immediately before one independently resumable provider/filesystem effect, call
   `TryBeginExternalEffectGroup`;
6. when the group wins, run the complete effect and its durable disposition without passing
   `MaintenanceRevocation` to the provider, filesystem, or compensation writes;
7. dispose the effect group, finish any DB-only suffix protected by the work lease, dispose every
   scope, and release the lease; and
8. only then delay, await reopening, or accept another unit.

If closure begins while a group is active, stage one waits for the group, its durable disposition,
and scope disposal. If closure wins before the group starts, `TryBeginExternalEffectGroup` returns
false and no external effect or effect-related durable record begins.

### 4.2 Maintenance deferral is not failure

A refused work lease or effect group is a local typed deferral, never an exception-shaped product
failure. It must not:

- increment an attempt or retry counter;
- begin or complete provider accounting;
- advance a watermark, cursor, generation, or completed-step checkpoint;
- write a provider failure, cancellation, `ReconciliationRequired`, or terminal status;
- discard an in-memory queue/dedup identity; or
- leave a waiter or task that shutdown does not own.

Queued and already-claimed work keeps its exact identity. The owning tracked task releases its scopes
and lease, awaits the next open generation with the host/execution cancellation token, and resumes the
same durable unit. Periodic work with no retained identity may simply return to its ordinary cadence.
The predecessor-generation rule from #254/#255 is retained where an exact reopen signal is required:
closing advances to generation G and reopening `Closed(G)` reports G, so a refused worker waits after
the predecessor of the generation observed before its attempt.

Host shutdown, operator cancellation, provider failure, and maintenance deferral remain four
different outcomes. Host/operator tokens keep their existing meaning. Maintenance revocation is an
admission race only and is never substituted for those tokens after the frontier wins.

### 4.3 DB-only work

A DB-only pass needs a work lease but no effect group. The work lease is already in stage one's census
and keeps its ambient ordinary connection lifetime valid through `Closing`. Adding a group around a
plain publication or cleanup would create a refusal point without protecting an external effect, the
same reason #254 does not group its final generation publication.

## 5. Producer boundaries

### 5.1 Workspace indexing

Scheduled workspace reconciliation and watcher-snapshot processing acquire `WorkspaceIndexing`
before their scopes. Discovery, signature reads, and orphan cleanup are DB-only work under the lease.

One changed file is the effect unit. Its group begins before `IndexFileAsync` opens the file and spans
the complete read, every page embedding, chunk insertion and metadata publication, and any rollback
of chunks inserted by that attempt. A page is not independently resumable today: any page failure or
cancellation removes every chunk inserted during the whole file. The design therefore does not
pretend an embedding page is a durable frontier.

If a watcher snapshot is deferred, its reconciliation bit and exact unprocessed path/action pairs are
merged back into `PendingWorkspaceChanges` and signalled once after reopening. A scheduled
reconciliation does not advance its due marker on deferral.

The on-demand endpoint is not request-bound today: it returns after discarding a `Task.Run` whose
body uses `ApplicationStopping`. That detached operation moves into `WorkspaceIndexingService` as
service-owned tracked work. It acquires `WorkspaceIndexing` like the scheduled paths, keeps its exact
workspace identity across maintenance, and is observed and drained by `StopAsync`. Shutdown first
atomically closes on-demand enqueue admission and then drains every task admitted before that
transition; a request racing or following the transition cannot publish a new task after the drain
snapshot. The endpoint retains its immediate-response contract; it asks the service to queue the
on-demand run rather than owning or discarding the task.

### 5.2 Tapestry weaving

One `TapestryWeaving` lease covers a bounded sweep scope. Startup cleanup, scope discovery,
superseded-generation cleanup, and removed-scope pruning are DB-only portions of that lease.

Each `TapestryScope` generation owns one sequential effect group. The group begins before
`TapestryWeaver.WeaveAsync` can call `BeginGenerationAsync` and spans leaf embeddings, model summaries,
summary embeddings, node/parent writes, atomic generation publication, or abandonment. The previous
complete generation remains current until publication. A refused group creates no staged generation
and leaves the scope eligible for the next sweep.

### 5.3 Batch processing

Batch startup recovery remains an exact `PreReadinessStartup` operation because
`BatchProcessingService.StartAsync` awaits `IBatchRecoveryService.ReconcileStrandedAsync` before
calling `base.StartAsync`.

Runtime discovery and each retained in-flight batch use `BatchProcessing` leases before their scopes.
The user-selected frontier is exactly one existing 64-line request/accounting page, not one line. A
page group begins before page preparation and `TurnAccountingHandle.BeginBatchAsync`, contains every
private line scope and the cancellation watcher, all bounded parallel provider calls, every terminal
line checkpoint, and `TurnAccountingHandle.CompleteAsync`, and ends only after those operations and
scopes have completed. This preserves current page accounting and line concurrency.

Advancing the encrypted input enumerator to materialize the next page is catalogued separately as
repeatable read-only source preparation under the batch work lease. It writes nothing, creates no
provider/accounting record, and has no durable disposition to pair with an effect group. This exact
classification preserves one effect frontier per existing page rather than adding a spurious final
group merely to discover end-of-stream. `EnsureOwnerOnlyDirectoryExists` is not preparation: it can
mutate the filesystem, so it moves from the pre-loop path into the artifact-publication group.

A closure between pages leaves completed line checkpoints and completed accounting durable. The next
page is refused before preparation, reservation, or dispatch. If the batch was already claimed
`InProgress`, its tracked `_inFlight` task retains the batch identity, releases all current scopes,
disposes the encrypted input enumerator/stream before releasing its work lease, waits without holding
a lease, reacquires after reopening, and creates a fresh enumerator. The fresh pass deterministically
rescans from the beginning and uses durable terminal line checkpoints to skip completed pages and
lines, so no provider call or accounting is replayed. No filesystem handle survives outside the work
lease. The task does not return normally and strand an `InProgress` row, reset it to a fresh paid
attempt, or depend on a process restart.

Encrypted output/error artifact construction and publication is a separate effect group. It spans
stage-file creation, checkpoint projection, encrypted completion, `File.Move`, owner-only permission
application, file-row creation, final Batch status publication, and compensation cleanup. Refusal
leaves line checkpoints authoritative so reopening can regenerate and publish the artifacts without
another provider call.

### 5.4 Unseen Servant

Hydration and idempotency cleanup each take an `UnseenServant` DB-only work lease before their scope.
They run after `Task.Yield`, so they are ordinary runtime work, not pre-readiness exceptions.
Maintenance denial does not stamp the cleanup cadence or log a fallback-to-memory warning.

One due daemon job is one effect group, spanning `IDaemonRunner.RunScheduledAsync`, the in-memory
completion record, and the durable watermark. The job's tracked `_activeJobTasks` entry retains the
same job/key when admission is denied, waits for reopening without a scope, and remains observable to
`StopAsync`. No watermark, success/failure result, or fresh jitter identity is produced by a denied
job.

### 5.5 Apprentice

Crash-recovery discovery occurs after `Task.Yield` and therefore uses an `ApprenticeExecution` DB-only
work lease. Each active Apprentice task is already tracked; it retains its execution generation and
queue/concurrency identity while waiting for admission.

The current whole-run scope is split into fresh, bounded scopes acquired only after a work lease.
There are three external-effect units:

- plan generation through durable plan/status publication;
- one serial step attempt through provider/tool execution, child stamping, optional Shifting Fate,
  and the completed/failure/escalation checkpoint; and
- one complete Simulacrum group, including all bounded branches, their child stamping, optional
  Shifting Fate, and the group checkpoint.

Simulacrum branches are not separate frontiers because they publish only as one group. Parallelism
remains bounded as today. A denied next unit leaves `CurrentStep`, the plan, completed tool-call
receipts, and the execution generation unchanged and resumes the same task after reopening.

The Simulacrum ordering defect is fixed by performing child stamping and optional Shifting Fate
before advancing `CurrentStep` and publishing the final group checkpoint, matching serial execution.
A regression test pins the ordering. This is a local checkpoint-order correction, not a new recovery
policy or checkpoint version.

### 5.6 Data retention sweep

The automatic host acquires `DataRetentionSweep` before its scope and passes the work authority only
through the internal hosted path; request-driven retention remains protected by its finite request
lifetime.

One prune candidate is the filesystem effect unit. Its group begins before the pending journal's
first external mutation and spans the identity-bound file/database mutation, proof, durable candidate
disposition, cursor movement attributable to that candidate, and journal clearing. A refusal before
the frontier leaves the pending candidate and cursor unchanged. A maintenance deferral is not
reported as cancellation, policy failure, or `ReconciliationRequired`.

### 5.7 Loremaster

The discovery sweep takes a `LoremasterSummarization` DB-only lease before its scope. Each queued
Session is one effect group spanning its exact source watermark, provider summary, and rollup write.
The group is disposed before the Session scope; the work lease remains held through scope disposal.

`CampaignLoggerQueue` no longer removes its pending marker merely by yielding an id. The consumer
concludes that identity only after success, a genuine terminal skip, or a genuine failure. A denied
lease/group retains and directly re-signals the exact Session once after reopening; it cannot use the
deduplicating intake path, which would observe the retained marker and write nothing.

### 5.8 DB-only periodic hosts

`A2ASendingLeaseRenewer` acquires `A2ASendingLeaseRenewal` before each renewal scope. Denial keeps all
held Sending identities and writes no failed/lost renewal classification.

`CovenantMaintenanceHostedService` acquires `CovenantMaintenance` before the scope for one bounded
three-sweep pass. Its name grants no maintenance exception; it is an ordinary live-catalog producer.

`GrimoireSchemaTransitionHostedService` acquires `GrimoireSchemaTransition` before one bounded journal
pass. Its schema journal makes the pass recoverable, but does not make it an offline-transition owner
or allow it to race Grimoire closure.

All three return to their normal cadence on denial and log no expected maintenance refusal as an
error.

### 5.9 Provider health probes

Each configured provider probe acquires a `ProviderHealthProbe` work lease and one effect group.
The group spans the outbound probe and publication of that observation to the in-memory tracker.
Denial leaves the provider's prior health state unchanged. A genuine probe failure retains its
existing unhealthy classification. The loop has no DI scope, but is still an external-effect
producer and must drain.

### 5.10 MCP bootstrap

Blocking MCP bootstrap remains `PreReadinessStartup`: `StartAsync` awaits initialization before the
host becomes ready. Nonblocking bootstrap is ordinary `McpServerBootstrap` work.

The admission boundary belongs to the manager's actual shared `_globalInitOperation`, not merely to
one caller's wait. `EnsureGlobalLoadedAsync` currently starts `RunGlobalInitOperationAsync` with
`CancellationToken.None` and then cancellation-wraps each caller with `WaitAsync`; a cancelled waiter
can therefore return and release any caller-owned frontier while the real task continues starting
processes and network clients. The shared operation is changed to own its work lease, effect group,
and manager/host lifetime until the real initializer is terminal. Blocking startup, nonblocking
startup, lazy request callers, and reload callers all join that same authority-bearing task; waiter
cancellation never releases its authority or creates a second initializer.

The hosted service stores and observes its bootstrap waiter rather than discarding `Task.Run`. A
denied shared operation waits for reopening under the manager's host lifetime. One manager-owned
lifecycle admission gate covers every start-capable path: the actual shared initializer plus direct
`StartAsync`, `RestartAsync`, and reload operations. Nested restart/reload logic uses core helpers so
one public operation owns one lifecycle admission. `StopAsync` atomically closes that admission,
cancels the manager lifetime, waits for every admitted start-capable operation and the actual
`_globalInitOperation`, and only then stops initialized servers. No start-capable path can publish a
process or client after shutdown begins, and calls after shutdown fail closed.

Global surface publication is revision-stable. An initializer captures the generation before it
projects the surface; after projection it publishes tagged with that captured generation only if the
current generation is still equal. Otherwise it discards the projection and retries. An invalidation
between scan and publication can therefore never bless a stale surface with a newer revision.
Repeated Stop and concurrent lazy initialization remain idempotent.

## 6. Recovery, backup, and exact nonordinary classifications

### 6.1 Long-running-operation reconciliation

The initial `LongRunningOperationStartupHostedService.StartAsync` pass is
`PreReadinessStartup`. Its periodic continuation is ordinary runtime work and cannot receive one
blanket pass-wide lease, because some recovery handlers are exact offline-transition owners and
would deadlock trying to close admission while holding their own ordinary work lease.

Each background page first takes a short `LongRunningOperationRecovery` discovery lease before the
outer scope and expired-row query. It materializes operation identities, then releases that scope and
lease before dispatch. Each materialized operation subsequently takes its own authority before its
private scope. This closes the currently unprotected outer scope without carrying an ordinary lease
into an owner-bound handler.

Runtime reconciliation therefore classifies each concrete recovery attempt exactly:

- an ordinary DB-only recovery takes `LongRunningOperationRecovery` before its per-operation scope;
- an ordinary provider/filesystem recovery begins one effect group before the per-operation scope and
  durable row-lease acquisition, then keeps it through the handler and durable settlement; and
- the two current offline-transition launch recoveries use only their exact authenticated
  owner/recovery path and never acquire ordinary work authority.

The classification key is operation kind **and checkpoint version**, plus authenticated journal and
owner evidence where the offline arm requires it. `DataRetentionMutation` V0 and V2 remain ordinary,
V4 is an owner-bound offline launch, and unsupported V1/V3 are refused from handler execution;
`DataRetentionFactoryReset` V0 remains ordinary while V2 is an owner-bound offline launch. Generic
periodic reconciliation leaves supported V4/V2 rows unchanged while they await the exact journal/owner
path; absence of owner evidence is not reported as an unsupported version. Genuinely unsupported
versions take DB-only ordinary authority solely to durably settle the row as
`ReconciliationRequired` with `UnsupportedCheckpointVersion`, without invoking a recovery handler.
The authenticated journal-startup and launch-gap paths pass an opaque full-owner capability to the
exact operation dispatch. There is no generally callable mint method. The abstract capability has
exactly two private, sealed, nested production issuers: one inside the journal startup path after a
verified handoff is consumed, and one inside launch-gap resumption accepting only the adopter's own
private-constructed owner token. Architecture tests pin that closed derived-type and construction
set, so a general borrower of the installation lock cannot issue evidence. Both issuers assert the
exact held installation lock and retain the complete `CovenantExclusiveRecoveryOwner` binding:
operation id, operation kind, and effect digest, plus the authenticated row's pre-claim
revision/kind/version fingerprint. Classification decodes the durable launch checkpoint and compares that complete binding
against the loaded row; a missing capability or a mismatched id, kind, version, or digest is refused
unchanged. The no-evidence exact-settlement entry is fail closed and cannot become an admission bypass.
The pre-bootstrap erasure-owner adopter continues
to refuse the retired mutation V3 shape before readiness; runtime durable settlement does not weaken
that half-erased-family invariant. A descriptor-level label cannot authorize both arms.

The generic discovery query excludes the two supported owner-bound tuples before applying its page
limit. Leaving them unchanged after an in-memory page filter would let a full head page of offline rows
permanently starve ordinary recoveries behind it. The pure classifier still pins the no-evidence
`OwnerBoundAwaitingExactOwner` decision, while the query makes that skip non-blocking for the backlog.

Every runtime dispatch uses an exact conditional lease claim over operation id plus the materialized
pre-claim revision, kind, and checkpoint version. The statement returns the claimed row atomically.
Failure changes no lease, revision, or attempt count; success must return the same identity/kind/version
at exactly `preClaimRevision + 1`, because lease acquisition itself advances the revision once, and is
then reclassified before a handler runs. The mutable lease revision is not part of the decoded
full-owner equality after that successful claim. This prevents an ordinary discovery result from
dispatching a row that changed into an owner-bound launch before acquisition, and applies equally to
installation-lock adoption.

The existing registry, handler, lease, checkpoint, settlement, priority, and #40 policy meanings do
not change. The new classification is an execution guard, not a recovery redesign. The inventory
tests require every descriptor exactly once, reject a default/wildcard classification, and prove the
offline owner set against the production offline-transition handler registry.

Starting the external group before `TryAcquireLeaseAsync` is intentional: that durable claim increments
`AttemptCount`. If closure won between the claim and a later frontier, the refusal would have changed
retry state and stranded a fresh lease without running its effect. A lost frontier therefore creates no
scope or durable claim; a winning frontier owns claim, handler, and settlement. The group is then
disposed before the private scope, while the work lease remains held through scope disposal. DB-only
claims need no group because their work lease itself drains the complete database-only unit.

### 6.2 Backup

`BackupService` and `BackupDatabaseSnapshotter` are not hosted services, but the parent explicitly
requires backup paths in this child. Production backup creation is a direct CLI operation, not an
HTTP finite request. Its live source read is classified at its exact caller chain:
`BackupCommands.Create` → `IGrimoireCliInitialization.RunExclusiveAsync` → the held installation
maintenance lock and client-mutation boundary → durable backup-operation ownership → the Covenant
installation read lease → snapshot completion. Snapshot staging, verification, and archive
publication are typed non-live destinations.

Restore safety-backup and installation-reset callers receive their own exact stopped-host or
owner-bound entries; no backup-wide exemption is inferred from the snapshotter type.

Architecture tests fail if the live-source snapshot path becomes reachable from a hosted or
unadmitted caller, or if its source open is reclassified from ordinary merely because its destination
is a snapshot. Stopped-host restore and installation-reset paths retain their exact existing factory
and owner proofs.

### 6.3 Exact lifecycle and effect-free services

The remaining hosted services are catalogued as follows:

- `GrimoireDatabaseHostedService` — exact installation-lock-bound startup, recovery, and stopped-host
  shutdown paths;
- `SessionAttachmentPendingGcHostedService` — awaited pre-readiness pending-file/row reconciliation;
- `FileEncryptionKeyBootstrapHostedService` — awaited pre-readiness credential-store bootstrap;
- `PidFileService` — exact process startup/stopped-host PID-file lifecycle, unrelated to the live
  Grimoire;
- `ArcanumSecurityStartupChecks` — awaited pre-readiness filesystem inspection;
- `ArcanumSettingsClampStartupLogger` — logging only;
- `CovenantFeatureConfigurationPublisher` — in-memory publication only.

`InstallationResetRecoveryAwareHostedService<T>` is catalogued once as supporting wrapper code,
outside the 23 unwrapped application-service entries. It has no independent scope/open or effect and
makes no claim of runtime revocation.

The inventory names the exact member and proof for each entry. Class names such as `Bootstrap`,
`Recovery`, `Maintenance`, and `HostedService` carry no authority by themselves.

## 7. Testing and TDD sequence

Every production change begins with one focused failing test whose failure is inspected before the
minimal implementation is written.

### 7.1 Inventory tests

The first RED is the 23-service bidirectional inventory against the current unprotected tree.
Independent fixture tests inject and assert each failure mode: new service, stale service, duplicate,
new scope, new ordinary-open route, new known provider call, new filesystem effect, broad identity,
missing/shared proof, wrong work kind, and an effect path with no frontier.

The existing connection acquisition and startup-order tests remain green. The obsolete six-item
claim in `CovenantResetBootstrapBarrierTests` is replaced or narrowed so it no longer describes a
partial list as every durable workload.

### 7.2 Gate and producer races

Gate tests admit and reject every exact new `GrimoireWorkKind`, preserve existing numeric values,
reject zero/unknown values, and keep sequential group behavior unchanged.

Each ordinary producer receives deterministic tests for:

- gate closed before admission: zero scope/open/provider/filesystem effect;
- closing after work admission but before the next frontier: deferral with exact identity/state;
- effect group winning: closure waits through provider/filesystem effect, durable disposition, and
  scope disposal;
- reopening: the same unit resumes once without duplicate provider billing, attempts, watermarks, or
  completed checkpoints;
- `KeepClosed`: no spin, false reopen, or unobserved waiter;
- host/operator cancellation: existing cancellation semantics, not maintenance classification; and
- genuine effect failure: existing failure semantics, proving deferral did not swallow it.

Concurrency tests use explicit barriers and task-completion sources, never timing sleeps. Where a
full-suite participant blocks synchronously at a checkpoint, the test owns a dedicated long-running
thread so it cannot starve the ThreadPool that must release it.

### 7.3 Focused regression tests

Additional RED/GREEN tests pin:

- Batch's single group per 64-line page, group start before accounting, group end after accounting,
  exact repeatable input-read classification, filesystem preparation inside the independent artifact
  frontier, retained `InProgress` identity, checkpoint resume, and full task drain;
- on-demand workspace indexing being service-owned, admission-protected, resumable, and atomically
  closed-before-drain against a concurrent enqueue;
- Unseen Servant and Apprentice startup work being ordinary after `Task.Yield`;
- outer long-running recovery discovery admission, kind-plus-version recovery classification, opaque
  full-owner capability validation including effect digest, and absence of offline-transition
  self-deadlock;
- Simulacrum child stamping and Shifting Fate before `CurrentStep`/checkpoint advance;
- Loremaster pending-marker retention and direct reopen signal;
- MCP shared-initializer admission through its actual terminal state, cancelled-waiter joining,
  stable-revision publication, task ownership, fault observation, direct start/restart/reload versus
  Stop races, and blocking-startup classification; and
- the backup live-source caller authority join.

## 8. Documentation and delivery

The implementation updates:

- `docs/Arcanum.DESIGN.md` §10.20.3 with the complete hosted work/effect inventory and exact producer
  boundaries;
- each affected service section and §13.7's regression catalog;
- `docs/Arcanum.Engineering.md` with the source-inventory obligation;
- the intentional public `README.md` background-maintenance paragraph, extending its #253–#255
  statement to the now-complete hosted-producer set; and
- the #239 design's delivery status without taking #257's full-host/final-qualification ownership.

The approved final-qualification repairs also update their owning documents:

- `docs/Arcanum.API.md` for factual model tool capability and unsatisfiable `tool_choice` errors;
- `docs/Arcanum.Command.Reference.md` for corrected initial-run input behavior; and
- `docs/Compendium.README.md` for `providers.models.supportsTools` and the 85% security-cache
  coverage floor selected by the user.

After focused tests and review, the feature SHA is qualified locally with:

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
./scripts/coverage.sh --threshold
./scripts/verify-aot-il-warnings.sh
./scripts/verify-native-sqlcipher.sh --rid osx-arm64
```

Only a green reviewed branch is merged into `grimoire-fixes`. The merged tree is verified, pushed,
and compared with `origin/grimoire-fixes`; the isolated worktree and feature branch are then removed.
Issue #256 is closed with the merge and verification evidence. Issue #239 remains open for #257.

## 9. Acceptance

Issue #256 is complete only when all of the following are true:

- every application hosted service and every qualifying operation site has one exact catalog entry;
- every ordinary host acquires its declared work lease before scope/open and uses the approved effect
  frontier;
- every exception has exact lifecycle/owner proof and cannot race an offline transition;
- maintenance denial produces no provider/filesystem effect, failure classification, retry movement,
  watermark/cursor movement, or identity loss;
- a winning effect drains through its durable disposition and scope disposal;
- Batch uses exactly one frontier per existing 64-line accounting page plus a separate artifact
  frontier;
- the Simulacrum and MCP lifecycle defects have failing regression tests before their fixes;
- all new and existing focused tests pass;
- the complete locally applicable verification set passes on the final reviewed merge;
- `grimoire-fixes` is pushed and matches its remote;
- the feature worktree/branch are deleted; and
- #256 is closed while #239 remains open.
