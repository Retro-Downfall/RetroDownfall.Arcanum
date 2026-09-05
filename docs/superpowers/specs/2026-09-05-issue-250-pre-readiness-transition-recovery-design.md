# Issue #250 Pre-Readiness Offline-Transition Recovery Design

**Status:** Approved for implementation.

**Branch:** `codex/issue-250-pre-readiness-transition-recovery`, cut from `grimoire-fixes` at
`7bc235a2a065cecd4ddd96931e42b27c1b48f6b4` (the #249 merge).

**Issue:** [#250 — Grimoire: recover authenticated offline transitions before readiness](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/250)

**Parent design authority:** `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`,
§5.1 and §7. Where this document and the parent disagree, the parent governs except on the four
points §1.2 records as deliberate departures.

## 1. Decision

### 1.1 What this child delivers

#249 taught startup to read the two maintenance records as one pair and to refuse when the pair says
a database transformation is unfinished. That refusal is correct and is the wrong place to stop: an
installation whose erasure crashed cannot start at all until somebody finishes the erasure, and
nothing in the shipped product finishes it. This child turns the three admitting journal-active
answers into a resumption.

Three things follow, and they are this child's whole content.

First, the host acquires the authority a crashed transition held, before it opens the database for
ordinary use. A recovery-only unlock validates the existing SQLCipher catalog and may do nothing
else; a read-only authority bootstrapper loads the minimum persisted Covenant facts over it and
verifies them against the authenticated journal; and a one-use handoff initializes the operation gate
directly into a closed recovery posture. The exact Covenant exclusive lease and the Grimoire closed
owner are reconstructed from that posture before any handler work begins.

Second, the resolved typed handler runs before bootstrap, and the host then continues. A transition
that reaches `CommitAndReopen` or a proven pre-effect `RollbackAndReopen` has retired its journal and
reopened ordinary admission, so the same start proceeds into `GrimoireDatabaseBootstrapper` and
publishes readiness exactly as an ordinary start does. A transition that parks, and every fail-closed
arm of the pair matrix, refuses startup with the content-free idiom #249 established.

Third, the launch-gap crash the parent design names in §5.1 stops being resolved after readiness. A
committed launch row whose journal was never published is a pre-effect crash, and today it is adopted
before readiness but *resumed* by the generic reconciler afterwards — on a ten-second startup budget,
with the host already serving, and with the resumption free to spill into the background while
requests arrive. This child resumes exactly one such row inside the bootstrap, before readiness is
marked, and fails readiness closed on every shape that is not exactly one resumable row.

### 1.2 Deliberate departures from the parent design

Four decisions in this child differ from, or resolve silence in, the parent design. Each is recorded
here so the divergence is a decision rather than drift.

1. **The typed handler is dispatched through the registered recovery handler, not through the
   coordinator directly.** Parent §7 says startup "dispatch[es] the exact typed handler" without
   saying through which seam. `ILongRunningOperationRecoveryHandler` is already that seam: it is what
   maps a durable kind to `DataRetentionService.RecoverMutationAsync` or `RecoverFactoryResetAsync`,
   which are the two things that know how to build a healthy-catalog factory erasure's required
   ordinary continuation and how to translate a `CovenantErasureCompletion` into a durable outcome.
   Reaching past it to `CovenantErasureCoordinator` would put a second copy of both in the startup
   path, and the copy that runs only after a crash is the copy that drifts unobserved.

2. **The lease is adopted under the held installation maintenance lock rather than waited out.**
   `TryAcquireLeaseAsync` admits a row only once its lease has expired, because in the ordinary world
   an unexpired lease means somebody may still be working. Pre-readiness recovery is not that world:
   the host holds the installation maintenance lock `FileShare.None` for its whole lifetime and fails
   startup without it, so an unexpired lease on this installation is provably a dead process's. This
   child adds one narrow adoption that asserts the held lock before it writes, rather than blocking
   startup for the remainder of a lease nobody holds or, worse, dispatching a handler that
   `IsActiveCheckpoint` will then refuse because the row names an owner this process is not.

3. **Success continues into ordinary bootstrap in the same start.** Parent §7 describes the recovery
   outcomes but not what the host does after a successful one. Keeping readiness closed regardless
   would make "resolve before readiness" half true: the transition would be resolved and the operator
   would still need a second `serve`. A retired journal and reopened ordinary admission are exactly
   the state an ordinary start expects, so the start proceeds. A parked or refused outcome does not.

4. **The launch-gap adopter is the existing owner adopter plus a resumption, not a second scanner.**
   Parent §5.1 describes a "launch-gap adopter" as though it were a new component. The scan it
   describes already exists as `CovenantErasureStartupRecoveryOwnerAdopter`, which admits exactly the
   two launch versions, refuses a second row, and refuses every legacy shape. What does not exist is
   the resumption: the adopted owner is handed to generic reconciliation, which runs after readiness.
   This child adds the pre-readiness resumption beside the adoption and leaves the scan where it is,
   because a second scanner over the same rows would be a second answer to one question.

### 1.3 What this child does not deliver

HTTP maintenance responses (#251); stream quiescence (#252); worker adoption (#253–#256); and
whole-branch qualification (#257). It does not add the lifecycle edge that would release a slot whose
operation is already terminal, which #248 left open and #249 deliberately did not add.

It adds no endpoint route, request or response shape, CLI verb, configuration key, database schema,
DDL identity, numbered migration, migration transition kind, or public Covenant contract. It adds no
`GrimoireOfflineTransitionState`, terminal intent, reconciliation step, handler outcome, transition
kind, `CovenantResetPhase` member, offline-transition payload version, or installation-reset payload
version. It changes no Covenant eligibility, disclosure accounting, effect-digest domain, protected
erasure scope, preservation set, or the route and result contracts frozen by #128.

## 2. Scope

### 2.1 Included

- `GrimoireRecoveryOnlyUnlock`: the validate-only opener of an existing SQLCipher catalog, and the
  five things it structurally cannot do.
- `CovenantRecoveryAuthorityBootstrapper`: the read-only load of the minimum persisted Covenant
  authority and availability facts, and their verification against the authenticated journal.
- `CovenantClosedRecoveryHandoff`: the one-use, lock-bound, revision-bound token that initializes
  `CovenantOperationGate` into a closed recovery posture and publishes no ordinary readiness,
  availability, or reusable token.
- `ILongRunningOperationMaintenanceLeaseAdoption`: one lease adoption that asserts the held
  installation maintenance lock before it writes.
- `GrimoireOfflineTransitionStartupRecovery`: the pre-bootstrap dispatcher over the three
  journal-active outcomes, its three-valued result, and its refusals.
- `GrimoireDatabaseHostedService` wiring: dispatch replacing #249's refusal, and continuation into
  ordinary bootstrap on success.
- `CovenantOfflineTransitionLaunchGapResumption`: the post-bootstrap, pre-readiness resumption of one
  exact adopted launch row, and the readiness refusal on every other shape.
- The generic long-running-operation reconciliation boundary: one exact-operation settle reusing the
  generic pass's own protocol, and the proof that the periodic pass is otherwise byte-identical.
- Fault injection at each new boundary, and a fresh-process proof over a real encrypted Grimoire.

### 2.2 Excluded

- Any change to `LongRunningOperationReconciler.ReconcileAsync`'s discovery predicate, ordering,
  paging, concurrency, budget, or per-operation protocol. The exact-operation entry point reuses the
  same body; it does not alter it.
- Any second recovery framework from #40, and any change to restore recovery ordering from #111.
  Coexistence stays explicit under the shared maintenance lock.
- Any relaxation of the two launch versions the recovery window, both handlers, and the adopter
  admit. A legacy V3/V1 row still cannot mint a journal.
- Any change to what a non-erasure durable operation does at startup.
- Any new Covenant envelope purpose, token family, authority epoch, or availability transition.

## 3. Authority order

Pre-readiness recovery acquires authority in exactly this order, and every step refuses rather than
proceeding on the previous one's absence:

1. the held installation maintenance lock, taken by the hosted service for the host's lifetime and
   borrowed here, never reacquired and never disposed;
2. the authenticated pair — the installation-reset active record and the offline-transition journal —
   resolved by #249's `InstallationResetStartupRecovery` under that lock;
3. the recovery-only unlock of the existing catalog, whose probes are unpooled and physically closed
   before anything else opens the database;
4. the verified minimum persisted Covenant authority and availability facts;
5. the one-use closed-recovery handoff, and through it the adopted durable recovery owner;
6. the exact Covenant exclusive lease, resumed from that owner; then
7. the Grimoire closing and then closed owner, taken by the handler itself.

Nothing in steps 3 and 4 may write. Nothing in steps 1 through 6 may perform a transition effect. The
journal remains the sole phase authority throughout; none of this changes what phase the transition
is at, and none of it may be used to decide that on the journal's behalf.

## 4. The recovery-only unlock

### 4.1 What it may do

`GrimoireRecoveryOnlyUnlock.OpenExistingAsync` takes the held installation lock, the guarded
directory, and the database path, and returns an open, unpooled, initialized connection to an
existing catalog — or a content-free `Covenant.ManualRecoveryRequired` refusal.

It resolves the passphrase through exactly one path: an existing database file with an existing KDF
sidecar, whose salt is combined with the already-persisted active secret. That is the only
combination in which a passphrase can be derived without changing anything.

The connection is opened with `SqliteOpenMode.ReadWrite` and `Pooling = false` and is initialized
through `CovenantSqliteConnectionMode.ReadOnly`, which never attempts a journal-mode change. It runs
one `SELECT 1` to prove the key opens the catalog, and it is physically closed and its pools cleared
before it is handed back as closed. `SqliteNativeRuntime.Instance.Initialize()` runs first, as it must
before any connection in this repository.

The unlock also publishes the derived passphrase to `IGrimoireDbPassphraseSource`, because the handler
that follows opens the same catalog through the ordinary factory and the bootstrap that would
otherwise have set it has not run.

### 4.2 What it structurally cannot do

Five refusals, each on evidence rather than on a flag:

- **it cannot create a database.** `SqliteOpenMode.ReadWrite` is not `ReadWriteCreate`, and the file's
  absence is checked before the connection string is built;
- **it cannot install schema.** It resolves no `GrimoireSchemaInstaller`, holds no initialization
  context, and executes no statement but `SELECT 1` and the four reads §5.1 names;
- **it cannot rekey or change KDF settings.** A present *pending* KDF sidecar is a refusal rather than
  something to recover: a pending salt means an interrupted `PRAGMA rekey`, and completing one is a
  mutation of the catalog this path exists to leave alone;
- **it cannot upgrade a legacy database.** An existing database with no KDF sidecar refuses. The
  legacy upgrade path derives from the master API key and then rekeys, which is the previous refusal
  by another name; and
- **it cannot restore.** It converges no rename topology, reads no restore journal, and moves no root.

An absent database file is a refusal here rather than a fall-through, and that is the point of the
whole component: a journal that names an active transition over a catalog that is not there is
evidence that disagrees with itself, and the ordinary bootstrap would answer it by creating a fresh
empty database.

## 5. The authority bootstrapper and its handoff

### 5.1 What it loads

`CovenantRecoveryAuthorityBootstrapper.LoadAsync` reads four things over the unlocked connection and
nothing else:

- `covenant_authority_state`, for the installation identity, authority epoch, current master key
  version, recovery envelope epoch, and host-tools state — the same projection
  `CovenantAuthorityStartupReconciler` already reads;
- `covenant_envelope_state`, for the envelope key epoch and dataset generation, and only when the
  canonical tier answers;
- `covenant_state`, for the persisted dataset generation, canonical sequence, applied tuple, and
  accelerator epoch, through the existing `CovenantPersistedAvailabilityPublisher` projection; and
- the one `LongRunningOperations` row the journal's binding names, through the existing
  `CovenantErasureStartupRecoveryOwnerAdopter` projection rules.

It writes nothing, and it holds the connection for no longer than those four reads.

### 5.2 What it verifies

Loading is not the point; agreement is. The bootstrapper refuses unless every one of these holds:

- the launch row exists, is one of the two current launch versions, carries the exact checkpoint
  reference for its kind and identity, and decodes;
- the launch's projected binding digest equals the journal's `DatabaseOperationLaunchBindingDigest`;
- the launch's canonical effect digest equals the journal binding's `EffectDigest`;
- the exclusive operation the launch names is the one the effect-handler registry says the journal's
  kind is allowed to be;
- the row's revision is at or ahead of the launch's recorded starting revision, which is the floor
  rule §10.20.3 already states, never an equality; and
- the persisted dataset generation is **exactly one of** the journal binding's source or target
  generation, and when it is the target, the persisted epoch tuple is the journal's target tuple.

That last one is the load-bearing check and the reason the facts are verified rather than merely
read. A dataset generation that is neither the source this transition was planned against nor the
target it preselected means the catalog under this journal is not the catalog the journal describes,
and publishing its authority would let the handler resume against a database it has no binding to.

The host-tools runtime policy is consulted first and is not advisory, exactly as it is in the ordinary
reconciler: a process the startup gate has not permitted derives no envelope key and publishes no
authority snapshot, and here that is a refusal rather than a warning, because the whole purpose of
this path is to obtain the authority a handler then spends.

### 5.3 The handoff

`CovenantClosedRecoveryHandoff` is the one-use result. It carries the authority snapshot, the
availability facts, the reconstructed `CovenantExclusiveRecoveryOwner`, the projected
`CovenantErasureCheckpointState`, and the durable operation identity — and three binding values that
say which world it was minted in: the held lock's guarded root, the journal envelope's slot epoch and
revision, and the journal envelope's digest.

`ConsumeAsync` initializes the closed recovery posture and may be called exactly once, enforced by an
`Interlocked` claim rather than by a flag a caller could read and race. It refuses when the guarded
root it is presented with is not the one it was minted under, when the journal has since advanced
past the exact revision it names, and when it has already been consumed. Consumption does three
things in order and stops on the first failure:

1. derives the envelope key generation and initializes `CovenantRuntimeGenerationProvider` with the
   authority snapshot and the verified availability snapshot;
2. calls `CovenantOperationGate.AdoptDurableRecoveryOwner` with the reconstructed owner, no scope, and
   no historical-campaign cleanup; and
3. returns the owner and checkpoint the caller needs, and nothing else.

It publishes no database readiness, calls no `PublishReadiness`, and mints no token any later caller
could reuse. Ordinary Covenant leases are refused for the whole of the recovery window because the
adopted owner closes the installation slot, which is the posture the parent design asks for: the facts
are present precisely so that the one exclusive lease can be resumed, and present facts behind a
closed gate are not availability.

## 6. Dispatch before bootstrap

### 6.1 The dispatcher

`GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync` takes the held lock, the
guarded directory, the database path, and #249's evidence outcome, and answers one of three things:

- `NoActiveJournal` — the outcome was `NeitherActive`, `NestedNotStarted`, or `NestedRetired`, and
  this pass did nothing at all;
- `Resumed` — the handler ran and the durable operation reached `Completed`, `Failed`, or
  `Abandoned`; or
- a content-free `Covenant.ManualRecoveryRequired` failure — every other ending.

For `StandaloneTransition`, `NestedBound`, and `NestedReceiptStoredRetirementSuffix` it performs, in
order: the recovery-only unlock; the authority load and verification; the physical close of the
unlock and its pool clear; the handoff consumption; the lease adoption; the exact-operation settle;
and the mapping above.

The order of the last two matters and is not interchangeable. The lease is adopted *before* the
handler is dispatched because `IsActiveCheckpoint` compares the row's `LeaseOwner` with the owner the
handler is given, and a handler dispatched under an owner the row does not name is refused by the
coordinator after the gate has already been closed against an adopted owner — which strands the
installation in exactly the posture this pass exists to leave.

### 6.2 The lease adoption

`ILongRunningOperationMaintenanceLeaseAdoption.AdoptUnderInstallationLockAsync` is the same compare-
update `TryAcquireLeaseAsync` performs, minus the expiry predicate, and it asserts the caller's held
installation lock for the guarded root before it writes. The state predicate is unchanged: a terminal
row is still not adoptable, and a `ReconciliationRequired` row is adoptable on exactly the kinds and
terminal codes the ordinary path already admits.

It lives on the concrete `LongRunningOperationStore` behind a narrow Infrastructure interface rather
than on Core's `ILongRunningOperationStore`, because the evidence it requires is an
`ArcanumMaintenanceLock`, which Core cannot see. One implementation, one SQL statement shared with the
ordinary acquisition by construction rather than by copy.

### 6.3 The exact-operation settle

The handler dispatch reuses the generic reconciler's own per-operation protocol —
`RecoverOneAsync`, the post-handler reread, the revision-bound `TryTransitionAsync` on
`CancellationToken.None`, and the outcome classification — through a new
`LongRunningOperationReconciler.SettleExactlyAsync(operationId, ownerId, cancellationToken)`. The
generic pass is refactored to call the same extracted body and is otherwise untouched: same discovery
predicate, same two startup phases, same paging, same concurrency, same skip on a process-local claim.

`SettleExactlyAsync` differs from the generic pass in exactly two ways, and both are properties of
knowing which operation is meant: it takes the operation by identity rather than by expiry discovery,
and it takes an already-adopted lease rather than acquiring one. Everything else — including the
`ownership.IsClaimed` skip — is the same code.

### 6.4 What the host does with the answer

`GrimoireDatabaseHostedService` calls the dispatcher immediately after
`InstallationResetStartupRecovery.RecoverBeforeBootstrapAsync` and before
`GrimoireDatabaseBootstrapper.EnsureInitializedAsync`, on the same borrowed lock.

- `NoActiveJournal` proceeds exactly as it does today.
- `Resumed` proceeds into ordinary bootstrap and publishes readiness. The journal has retired and
  ordinary admission has reopened, which is the state an ordinary start expects; the bootstrap's own
  owner adoption then finds a terminal row and adopts nothing.
- A failure throws the same `InvalidOperationException` shape #249's refusal throws, with the same
  fixed sentence. Which of the endings it was stays out of the message.

The existing `LeavesTransitionUnfinished` refusal is deleted rather than kept beside the dispatcher.
Two answers to one question is the shape this whole branch exists to avoid, and a refusal that
survived beside a resumption would fire first and make the resumption unreachable.

The lock-free `InstallationStartupProbe` path is unchanged and reaches none of this. It holds no lock,
so it can neither read the journal nor unlock the catalog, and a recovery it could not have performed
honestly is worse than no recovery.

## 7. The launch gap

### 7.1 What the gap is

The launch-row commit and the first journal publication cannot be one atomic storage transaction. A
crash between them leaves a committed, nonterminal launch row and no journal. Nothing destructive has
happened: the journal is published and verified before `BeginOrResumeExclusive` closes admission, so a
row with no journal is a transition that never closed anything.

That row is safe to resume — the phase authority's `OpenOrResumeAsync` opens a fresh journal from the
committed launch, which is what it is for — and it is not safe to leave. Left alone it is a
nonterminal durable operation that blocks every later data-retention operation.

### 7.2 What runs, and where

`CovenantOfflineTransitionLaunchGapResumption` runs inside `GrimoireDatabaseBootstrapper`, after the
install connection is closed and before `readiness.MarkReady()`. It runs only when an installation
lock is held, exactly as the protected-maintenance recovery beside it does, and only when the
recovery-owner adopter above it adopted exactly one owner.

The scan itself is unchanged: `CovenantErasureStartupRecoveryOwnerAdopter` already admits only the two
current launch versions with their exact checkpoint references and recovery policies, already returns
the ordinary-mutation early-out rather than an owner for a row that closed nothing, already refuses a
second adoptable row, and already refuses every legacy, malformed, and mis-policied shape. What is
added is that its adopted owner is now resumed here rather than left for the generic pass.

The resumption is the same `SettleExactlyAsync` §6.3 defines, under the same lease adoption §6.2
defines, and it is dispatched after the install connection has physically closed because the handler
closes the Grimoire and drains every enrolled handle — including one this bootstrap would otherwise
still be holding.

### 7.3 The refusals

- **more than one adoptable row:** already a refusal in the adopter, and it stays one. Readiness fails
  closed.
- **a legacy or malformed row:** already a refusal. Readiness fails closed.
- **a resumption that does not reach a terminal durable state:** readiness fails closed with
  `ProtectedRecoveryUnavailable`. A transition that parked has closed admission behind it, and
  publishing readiness over it would open the catalog to every pool, worker and endpoint that waits
  on that signal.
- **an active journal:** unreachable here. This pass runs only after the host proved under the lock
  that no journal is active, and §6 handled it if one was.

### 7.4 What stops running after readiness

Nothing is removed from the generic pass. The adopted operation is terminal by the time
`LongRunningOperationStartupHostedService` runs, so the generic pass finds nothing to do about it;
while the resumption is in flight the coordinator's own process-local claim keeps the generic pass off
the row, which is the mechanism #248 built for exactly this. The startup budget, the background
interval, and every other kind's behaviour are untouched.

## 8. Fault injection

Every new boundary gets one case, and the boundary name travels as the assertion message:

- before and after the recovery-only unlock's single probe;
- before and after the authority load, and on each of the six verification refusals;
- before and after handoff consumption, and on a second consumption;
- before and after the lease adoption;
- before and after the handler dispatch, and on each of the four durable outcomes; and
- between the launch-gap adoption and its resumption.

The journal-side boundaries reuse the recording and fail-before-step file and anchor stores #243
established, driven from a second store instance over the same durable root and recovered by a fresh
one. The dispatcher's own boundaries use a seam on the dispatcher rather than a widening of
`CovenantErasureFaultBoundary`, which describes phases inside a run rather than the steps that reach
one.

## 9. TDD strategy

Every behavioral change follows an observed RED, a minimal GREEN, and a focused review. Ordering
proofs use deterministic barriers or a manual time provider, never sleeps. Tests that reach a real
SQLCipher database stay skippable and join the existing serialized collections, because pool clearing
is process-global.

Three existing pins change deliberately rather than incidentally, and each gains its counterpart:

- `GrimoireDatabaseHostedServiceTests`' proof that an active journal refuses startup becomes a proof
  that an active journal is *resumed*, beside a new proof that a parked one still refuses;
- `CovenantErasureStartupRecoveryOwnerAdopterTests` keeps every refusal and gains the resumption; and
- `CovenantErasureFreshProcessRecoveryTests` keeps its post-bootstrap adoption proof and gains a
  sibling that crashes *inside* the closed period and proves the pre-bootstrap path resumes it.

The eight acceptance cases the issue names are proved as a table over the dispatcher: active,
reconciliation-pending, retirement-pending, malformed, missing, conflicting, dual-record, and
launch-gap. Ambiguous evidence fails closed in every one of them.

Repository-wide qualification remains #257's.

## 10. Verification and delivery

Focused child-scoped tests through the RED/GREEN loop, then a bounded review, a warning-free Release
solution build, changed-file style verification, and a clean branch diff. Coverage, the complete
suites, Native AOT and IL verification, the benchmark gate, native SQLCipher provenance, packaging,
full-host, and cross-platform qualification remain #257's on the final reviewed SHA.

Documentation travels with the code. `docs/Arcanum.DESIGN.md` §10.20.3, §10.20.4 and §13.7; the status
paragraphs and the Covenant section of `docs/Arcanum.Engineering.md`;
`docs/Arcanum.Command.Reference.md`'s `serve` startup-admission paragraphs; `docs/Arcanum.OATH.md`
§2.1, §15.3 and §16; `docs/ArcanumOATH.Human.md` §11; `docs/Arcanum.Design.Human.md` §8; and
`docs/Arcanum.DEBUGGING.Human.md`'s breakpoint map.

One documentation correction travels with this change and is not caused by it. `Arcanum.OATH.md`
§15.3 states that `NestedReceiptStoredRetirementSuffix` is admitted "only while the journal sits in
`RetirementPending` or in `DatabaseReconciliationPending` at or past `ParentReceiptSatisfied`". That
is the rule as first drafted for #249; the delivered rule, stated correctly in `Arcanum.DESIGN.md`
§10.20.3 and implemented in `InstallationResetNestedTransitionEvidence`, tests the terminal winner
rather than the step and admits `KeepClosed` as well. The OATH sentence is corrected to match the code.

`README.md` and `docs/Compendium.README.md` do not change: this child adds no public front-page
behavior and no configuration key. `docs/Arcanum.API.md` does not change: no route, payload, or error
code moves. The dated review records are append-only and are not edited. Issue #242 is not edited,
transitioned, reparented, closed, or claimed as resolved.
