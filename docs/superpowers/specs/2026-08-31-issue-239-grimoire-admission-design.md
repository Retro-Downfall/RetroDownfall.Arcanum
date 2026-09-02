# Issue #239: Host-wide Grimoire Admission and Offline Transitions

**Status:** Approved umbrella; #243/#244 integrated; #245/#246 delivered; #247–#257 pending.

**Branch:** `codex/issue-239-grimoire-admission`, cut from `origin/main` at
`988a469c765346132e5a2ea1bf3906519f6bdf00`.

**Issue:** [#239 — A Grimoire connection is enrolled in the Covenant drain only as a side effect](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/239)

**Supersedes:** The earlier #239 design in this file that kept erasure phase authority in the
Grimoire and temporarily reopened the exact scoped connection for checkpoint, renewal, and
reconciliation writes while ordinary admission was closed.

## 1. Objective

Make every operation that must transform the live Grimoire while normal use is stopped enter one
host-wide offline-transition boundary. The first production transition kinds are direct Covenant
reset and healthy-catalog factory erasure, including the database arm of a broader installation
reset.

The same infrastructure deliberately leaves a typed extension point for a future database
migration. No migration behavior, phase vocabulary, schema change, or placeholder migration kind is
implemented by this issue.

Once an offline transition begins closing the boundary, no ordinary API request, Entity Framework
scope, background worker, pooled handle, or raw SQLite opener may race the drain or reopen the
database. Existing finite or billable work reaches its known durable boundary before connection
admission closes. Explicitly classified unbounded streams stop at a complete frame boundary.

Closed-period phase authority lives in a small authenticated journal outside the Grimoire. The
journal is authoritative from its first verified publication until the reopened database operation
row is reconciled and the journal is retired. The coordinator never reopens the Grimoire merely to
write a checkpoint, heartbeat, or `ReconciliationRequired` state.

This fixes the intermittent Windows failure in which pooled or late native handles keep the database
or WAL/SHM files live during reset, while also giving the eventual database-migration path the same
crash-safe quiesce, transform, verify, reopen, and reconcile lifecycle.

## 2. Existing behavior and cause

The current connection drain can close handles it knows about, but enrolment is incidental and
closing a logical `SqliteConnection` may return a native handle to a pool rather than release the
file. The host can still create new work while erasure is draining:

- `/api` or `/v1` request scopes can resolve an `ArcanumDbContext`;
- `SessionAttachmentIndexingService`, `EntryWeavingService`, and `SagaExtractionService` can create
  new scopes and begin provider work;
- raw `SqliteConnection.Open` / `OpenAsync` paths can bypass EF interception;
- startup or recovery can open a pooled bootstrap handle; and
- source inventory that recognizes a factory declaration does not prove every acquisition call site
  is protected.

The original #239 design closed those admission gaps but kept phase checkpoints, durable lease
renewals, and closed-state reconciliation in `LongRunningOperations`. That creates a circular
dependency: the operation must prove the database physically closed, then reopen it to record that
proof. A later phase can recreate WAL/SHM files after proving them absent. Recovery must also mint
special authority to read the same database whose admission remains closed.

The revised design separates the concerns:

- the Grimoire admission gate controls process-local request, work, and connection lifetimes; and
- the authenticated offline-transition journal controls durable phase and recovery authority while
  the database is closed.

## 3. Approved architecture

### 3.1 One offline-transition lifecycle

`GrimoireOfflineTransition` is the shared lifecycle for a transformation that requires ordinary
database access to stop. Reset and migration are structurally the same lifecycle, but their effect
and proof policies remain distinct:

1. identify and bind the source database state;
2. publish durable transition intent;
3. stop and drain ordinary access;
4. perform a typed transformation;
5. prepare a private, owner-bound maintenance reopen;
6. verify the resulting database, filesystem, and live Covenant authority while ordinary admission
   remains closed;
7. reconcile its ordinary operation history through one exact terminal database CAS;
8. publish any required parent-workflow completion receipt;
9. retire the external journal; and
10. reopen ordinary admission.

A reset deliberately removes selected state and may regenerate the database's dataset generation.
The stable external installation identity remains unchanged. A future migration normally preserves
state while changing schema or representation. They share journal security, admission, recovery,
and retirement, not phase names or transformation logic.

### 3.2 One active slot per profile

One profile has exactly one active offline-transition slot. Direct Covenant reset, healthy-catalog
factory erasure, and any future database migration are mutually exclusive through that slot.

The slot consists of one fixed authenticated journal file outside the guarded Grimoire directory,
one profile-namespaced encryption key in `IOsCredentialStore`, and one profile-namespaced
anti-rollback anchor in `IOsCredentialStore`.

The journal path is a sibling of `ArcanumMaintenanceLock.LockPathFor(grimoireDirectory)`. Its leaf is
derived from that lock leaf with the distinct suffix `.grimoire-transition.active.json`. It is not a
SQLite `-wal`, `-shm`, or `-journal` sidecar. `CovenantResidualArtifacts` continues to mean artifacts
that must not survive erasure; the journal is excluded from that inventory by exact sibling path and
is proven separately as retained transition-control evidence.

The credential accounts are dedicated to this lifecycle and profile namespace. Ordinary reset
retains the stable key and a terminal anchor tombstone. A full installation reset may remove them
only during its final credential cleanup, after proving no active offline journal remains.

The running host already owns the exact installation maintenance lock. A transition borrows that
held instance and never reacquires or disposes it. Startup and offline recovery acquire the same lock
before journal inspection. The lock and journal slot together exclude a second process or operation.

### 3.3 Authenticated publication protocol

The journal specializes the existing installation-reset V2 and backup-restore V2 security pattern.
It does not use a plaintext blocker or `AtomicFile` alone.

The authenticated envelope contains its version, profile namespace digest, stable installation
identity, monotonically increasing slot epoch, operation id, transition kind, typed payload version,
monotonically increasing journal revision, previous-envelope digest, physical journal-location
digest, random AES-GCM nonce, ciphertext, and authentication tag.

All clear header fields are additional authenticated data. The location digest binds the exact
profile namespace, guarded-parent physical identity, and fixed child leaf. The encrypted payload is
strict source-generated JSON with unmapped members refused. Key material never enters the file,
database, log, DTO, or long-running-operation checkpoint.

The retained anchor contains slot epoch, state (`Active` or `Closed`), operation id, transition kind,
latest journal revision, and nullable envelope digest. Beginning a new operation is one compare-write
from the exact prior `Closed(E)` tombstone to `Active(E+1, operation, kind, revision 0)`, after proving
the canonical journal absent. The first operation compares from the exact provisioned closed genesis
anchor. A different operation cannot replace an active anchor. An exact duplicate against an active
same-operation anchor only validates and resumes its existing journal; it never starts over. A closed
same-operation tombstone never reopens that operation.

Publication order is fixed:

1. for a new operation, write and read back an `Active` revision-zero anchor first;
2. create an owner-only, no-follow, same-directory temporary file;
3. write and fsync the complete encrypted envelope;
4. atomically replace the canonical journal file;
5. reapply and verify owner-only permissions;
6. fsync the parent directory;
7. securely reopen the exact file, authenticate, decrypt, and compare the complete publication; and
8. compare-write and read back the OS anchor.

Normal updates write file revision `N+1` before advancing the anchor from `N` to `N+1`. Recovery
accepts only an exact anchor/file match or the one crash window where the file is exactly one chained
revision ahead of the anchor and names the anchor's digest as its predecessor. It advances that
anchor under the held maintenance lock before any effect. An `Active` revision-zero anchor with no
canonical journal fails closed on restart because deletion and pre-publication crash are
indistinguishable. Only the still-running publisher that performed the opening CAS may compare-close
that anchor after securely proving no canonical file was ever published and no temporary file can be
promoted. Every other behind, ahead, replayed, cross-profile, cross-installation, cross-epoch,
cross-operation, wrong-location, unknown-version, symlink, reparse, hard-link, case-alias,
temp-residue, or multiple-active condition fails closed.

Cancellation or failure after the atomic rename is `RecoveryRequired`; it is never reported as if
publication did not occur.

### 3.4 Typed handlers, not a universal payload

The shared journal store handles bytes, revisions, anchors, location identity, publication,
recovery, and retirement. It knows no Covenant tables or migration steps.

Each transition kind has an explicit Native-AOT-safe codec and handler registered in a closed
composition table. A handler validates its authenticated payload, determines the next phase,
resolves an interrupted in-flight phase, performs or replays one phase, verifies the transformed
database, and reconciles the database row without changing its immutable launch binding.

Handler outcomes are typed as `NotApplied`, `AppliedAndVerified`, `ReconciliationPending`, or
`KeepClosed`; ambiguous booleans are forbidden. Unknown kinds or payload versions keep readiness and
admission closed.

This issue registers handlers only for `CovenantReset` and `HealthyCatalogFactoryErasure`. A future
migration adds its own kind, payload, source/target schema identity, idempotency resolver, phase
handler, and verification policy. It reuses the journal and admission infrastructure without
changing Covenant phases. A test-only second handler proves the extension seam without shipping
speculative migration behavior. That second handler exists only in a registry constructed by its
test; no production discriminator, codec, DI registration, schema shape, or placeholder migration
kind is added.

### 3.5 Authority split with the database

Before closure, the ordinary database retains one operation row for API replay, request identity,
audit history, and terminal status. Its launch-binding fields are immutable. New launches use
`CovenantOfflineTransitionLaunchV4` or `DataRetentionFactoryTransitionLaunchV2` checkpoint JSON to
record operation id, kind, recovery policy, canonical effect digest, source and preselected target
generation/epoch tuple, and expected starting row revision. The coordinator creates a stable
generated request identity when the caller did not supply one. This is a checkpoint-codec version,
not a database schema change. Existing Covenant V3 and factory V1 decoders remain strict legacy-read
contracts but cannot authorize a new offline effect. Offline phases never rewrite the launch
checkpoint; the later terminal CAS changes only terminal status metadata and revision.

From the first verified journal publication through the exact terminal winner reread, any required
parent-workflow receipt publication, and journal retirement:

- the journal is the sole durable phase and disposition authority;
- no long-running-operation phase checkpoint is written;
- no database lease or heartbeat is renewed;
- no durable-owner reread is used as permission for the next offline effect; and
- `KeepClosed` is represented by the retained journal, not a database
  `ReconciliationRequired` write.

The held maintenance lock, exact journal revision, process-local admission owner, and adoption
interlock replace closed-period database leases. A crash releases process-local ownership while the
authenticated journal remains adoptable by the next process.

The journal does not replace the existing process-local Covenant exclusive lease. Both current
handlers declare that authority requirement. The initiating coordinator holds the exact
`ICovenantExclusiveOperationLease` before publishing the journal. Recovery authenticates the exact
operation/kind/effect launch binding, loads persisted Covenant facts through the recovery-only
authority handoff described in §7, reconstructs the matching `CovenantExclusiveRecoveryOwner`, and
reacquires the existing Covenant exclusive lease before reconstructing the Grimoire closed owner or
performing any handler effect. That exact lease is held through
`CovenantErasureTransition.PublishCommittedAsync` and the one Covenant disposition. A future
non-Covenant migration handler declares its own authority requirements; journal possession alone
never mints a Covenant lease.

After the owner-bound maintenance reopen and candidate verification, while ordinary admission is
still closed, the handler reads the operation row, validates its immutable id, kind, recovery policy,
launch binding, and expected revision, performs one exact terminal CAS to `Completed` or a proven
pre-effect `Failed`, and rereads the exact winner. An already matching terminal row is idempotent
success. A missing or conflicting row is never overwritten; the journal remains
`DatabaseReconciliationPending`, ordinary admission stays closed, and startup or manual recovery
must resolve it.

The database row is reconciliation evidence, not competing phase authority. Only the exact terminal
reread, any exact parent receipt acknowledgement, physical maintenance-lane closure, and the exact
one Covenant disposition permit journal retirement and then ordinary admission reopening.

### 3.6 Relationship to full installation reset

The broader installation-reset active record remains separate. It coordinates accepted roots,
credentials, host-tools markers, host shutdown, client mutation blocking, external remediation, and
offline filesystem cleanup beyond the Grimoire.

Its healthy-catalog database arm invokes a `HealthyCatalogFactoryErasure` offline transition and
supplies an optional typed parent-receipt sink. After exact database terminalization, the handler
publishes and rereads the exact operation/effect/completion receipt in the outer active record before
the Grimoire journal may enter `RetirementPending`. Direct reset and standalone factory erasure have
no parent sink. The two records never substitute for one another. The installation-reset record is
broader workflow authority; the Grimoire journal is the sole phase authority for the nested offline
database transformation.

On startup, evidence is resolved as a pair:

- **neither active:** normal launch-gap inspection and bootstrap rules apply;
- **installation-reset only, nested phase not started:** existing broader recovery may begin its
  nested transition later;
- **installation-reset only, exact nested completion receipt present:** the journal is already
  retired and the broader workflow continues;
- **installation-reset only, nested publication claimed without its exact receipt:** fail closed
  because the missing journal cannot be treated as never started;
- **Grimoire journal only with no parent binding:** dispatch direct reset or standalone factory
  erasure;
- **Grimoire journal only with a parent binding:** fail closed because a nested transition may not be
  downgraded to standalone work;
- **both active:** the journal must be `HealthyCatalogFactoryErasure` with a non-null parent binding
  that exactly matches the outer operation id, effect digest, nested phase, and expected receipt;
  missing/mismatched binding, standalone work, or nested `CovenantReset` fails closed; or
- **both active with the exact receipt already stored:** the journal may only be in its matching
  terminal/retirement suffix; finish exact retirement, then continue the broader workflow. An
  earlier journal phase conflicts with the receipt and fails closed.

A full installation reset may delete the journal key and closed anchor only in final credential
cleanup after it has proved the journal absent and retained the nested completion receipt.

## 4. Journal state model

### 4.1 Shared state

Every publication carries one shared lifecycle state:

- `Prepared` — launch binding is authenticated; ordinary admission has not necessarily closed;
- `Closing` — request/work draining or connection closing is in progress; leaving it requires an
  exact generation-bound closed/drain proof;
- `Applying` — a typed handler owns the next phase;
- `ReopenPrepared` — the disposition is durable and only an owner-bound maintenance reopen is
  permitted; ordinary admission remains closed;
- `Verifying` — the transformed candidate and live runtime authority are being proved through that
  private maintenance lane;
- `DatabaseReconciliationPending` — destructive effects and candidate verification must not repeat;
  only exact database terminalization, optional parent receipt publication, lane closure, and
  retirement remain;
- `KeepClosed` — the exact content-free blocker and resumable state remain authoritative; or
- `RetirementPending` — the database terminal row and any parent receipt are exact, all maintenance
  connections are physically closed, and only anchor/file retirement remains.

The legal shared transition graph is closed:

- `Prepared -> Closing -> Applying`;
- `Closing -> Closing` only to advance admission-denied, request/work-drained,
  open-attempts-resolved, handles/pools-closed, and closed-generation proof;
- `Closing -> Applying` requires proof that request/work drain, unresolved-open cleanup, enrolled
  handle closure, pool clearing, and the closed-generation lease all completed;
- `Closing -> ReopenPrepared` requires that same closed proof plus exact pre-effect rollback proof;
- `Applying -> Applying` for monotonically advancing typed in-flight/completed phases, then
  `Applying -> ReopenPrepared`;
- `ReopenPrepared -> Verifying`;
- `Verifying -> Verifying` only to advance private maintenance-open, candidate-proof, and runtime
  Covenant-authority in-flight/completed evidence;
- `Verifying -> DatabaseReconciliationPending` only after candidate and runtime-authority proof;
- `DatabaseReconciliationPending -> DatabaseReconciliationPending` only to advance the exact suffix
  proof from candidate verified, through database terminal winner, parent receipt or not-required,
  lane closed, Covenant disposition in flight, and Covenant disposition verified;
- `DatabaseReconciliationPending -> RetirementPending` only after that suffix proof is complete;
- `Closing` only after its exact closed-generation/drain proof, or any later pre-retirement state,
  may enter `KeepClosed` with an exact blocker and journaled blocked state; and
- `KeepClosed -> Closing`, `Applying`, `ReopenPrepared`, `Verifying`, or
  `DatabaseReconciliationPending` is legal only when the destination exactly equals the recorded
  blocked state, the handler-specific blocker-resolution predicate succeeds, and operation, kind,
  effect, terminal intent, slot epoch, closed generation, and typed in-flight evidence are
  unchanged.

Terminal intent is a separate monotonic sub-state. It starts `Undecided`, advances once to
`RollbackAndReopen` only after exact pre-effect proof or to `CommitAndReopen` only after all intended
effects and closure proofs, and never changes again. `Closing -> ReopenPrepared` selects rollback;
`Applying -> ReopenPrepared` selects commit. `KeepClosed` is a blocker state, not a terminal intent,
and entering or leaving it cannot select, clear, or change intent.

`RetirementPending` advances only to the matching `Closed` anchor and journal deletion. No backward
edge, state skip, second terminal-intent selection, same-revision payload change, or different
operation/kind edge is legal. Compare-write and secure reread enforce every edge. Illegal or replayed
transitions keep admission closed.

The encrypted typed payload includes the operation id and exact transition kind, canonical effect
digest, source and preselected target dataset generations, source and expected target
`AcceleratorEpoch`, `KeyReclamationEpoch`, and `EnvelopeKeyEpoch` values, immutable
database-operation launch binding and expected revision, optional parent-workflow receipt binding,
last completed typed phase, nullable in-flight phase and its typed before-state evidence, nullable
blocked state and handler-specific blocker-resolution evidence, terminal intent (`Undecided`,
`RollbackAndReopen`, or `CommitAndReopen`), nullable stable error code, and operation-bound compaction
staging leaf plus source, staging, destination, and original-backup physical identity digests when
atomic replacement is in flight. While database reconciliation is pending it also carries the
monotonic suffix-proof step and exact terminal-row, parent-receipt, lane-close, and Covenant-disposition
evidence accumulated so far.

No authored content, subject identity, path, credential, passphrase, key, live token, lease, handle,
connection, service object, provider response, or disclosure detail is permitted.

### 4.2 Effect publication rule

Every effect follows one protocol:

1. publish and verify an `InFlight` journal revision with the evidence needed to resolve ambiguity;
2. perform the effect;
3. inspect and prove whether the exact effect completed; and
4. publish the completed phase.

A crash before step 1 means the effect may not have begun. A crash after step 1 never permits blind
assumption. Repeatable effects may run again. An operation-specific resolver must classify an
ambiguous effect as exact-not-applied, exact-applied, or `KeepClosed`; it may not guess from the
absence of an exception.

### 4.3 Terminal retirement

Successful terminal ordering is:

1. publish `ReopenPrepared`;
2. privately reopen the candidate through one owner-bound, unpooled maintenance lane while ordinary
   admission stays closed;
3. verify the transformed candidate and publish/reconcile the live Covenant runtime-authority
   transition;
4. publish `DatabaseReconciliationPending` before the exact terminal database write;
5. perform and reread the exact database terminal CAS;
6. publish and reread any required parent-workflow completion receipt;
7. physically close every maintenance connection and prove the owner lane empty;
8. publish the typed Covenant-disposition effect in flight, spend and verify the exact
   `CommitAndReopen` or `RollbackAndReopen` disposition on the held Covenant lease, and publish that
   effect complete while Grimoire ordinary admission remains closed;
9. publish `RetirementPending`;
10. compare-write and verify the anchor as `Closed` for the exact final publication;
11. securely delete and prove absent the exact journal file;
12. retain the closed anchor tombstone and stable key; and
13. reopen ordinary Grimoire admission.

A crash after database terminalization but before retirement rereads the exact terminal winner and
finishes the parent receipt, lane-close proof, Covenant disposition, and retirement without repeating
transformation effects. A crash after the process-local Covenant disposition but before its completed
journal publication reconstructs a closed recovery lease and idempotently spends the same terminal
intent; it never repeats a database effect. A crash after the anchor becomes `Closed` permits deletion
of an exact file whose slot
epoch, operation, revision, and digest match that closed anchor; exact absence is already-retired
success. An earlier or different authentic envelope is replay and fails closed. The next operation
may compare-open a new slot epoch only after exact journal absence is proved.

## 5. Covenant erasure handler

### 5.1 Current transition kinds

Both current kinds use the existing `CovenantResetPhase` vocabulary and effect digest domains:

- `CovenantReset` runs the protected-family erasure path and skips factory continuation; and
- `HealthyCatalogFactoryErasure` runs the same protected erasure plus the existing ordinary
  healthy-catalog factory continuation at its declared boundary.

Before committing the launch row, inventory preparation reads the exact source generation/epoch
tuple, preselects the random target dataset generation and deterministic target epochs once, and
serializes them into `CovenantOfflineTransitionLaunchV4` or
`DataRetentionFactoryTransitionLaunchV2` at `InventoryPrepared`. The journal is then published and
verified from that launch binding before `BeginOrResumeExclusive` closes new request or work
admission. Exact duplicate begin for the same operation and publication is idempotent; a conflicting
active slot is refused.

The launch-row commit and first journal publication cannot be one atomic storage transaction. The
same-process coordinator owns that cutover and may close admission or perform no offline effect until
the journal is verified. On restart with no active journal, startup does not assume normality: after
safe database bootstrap and before readiness, a launch-gap adopter scans only exact nonterminal
`InventoryPrepared` Covenant V4/factory V2 bindings. One exact pre-effect row may publish its opening
journal and resume, or terminalize as proven pre-effect failure. A legacy V3/V1 row cannot mint a
journal. Multiple, malformed, conflicting, legacy, or post-`InventoryPrepared` rows without
authenticated journal evidence fail readiness closed. The
generic long-running-operation reconciler never invents offline-transition ownership from a looser
row shape.

### 5.2 Idempotent and convergent phases

The handler applies these recovery rules:

- protected artifact pages use existing transactional compare-delete and converge by rescanning
  remaining rows;
- managed-file work items resume from their durable prepared/proof/terminal state machines;
- factory ordinary-row deletion is one transaction that explicitly preserves and rereads the exact
  launch operation row and request/effect binding; it no longer renews a lease, updates a heartbeat,
  or advances the operation revision inside that transaction;
- factory filesystem quarantine cleanup converges by whole-root reinventory;
- handle drain and pool clearing are repeatable;
- WAL checkpoint/truncation and its proof are repeatable;
- `VACUUM` is convergent;
- empty-tier accelerator initialization is repeatable;
- SQLite residual sidecar absence proof is repeatable; and
- reopened candidate verification is a read-only repeatable proof whose completion is journaled only
  outside SQLite.

An effect group and its completed journal revision remain within the same process-local maintenance
lane/adoption interlock. A recovery adopter cannot take ownership between a successful effect and
publication of its completed phase.

### 5.3 Canonical transaction ambiguity

The canonical family transaction is not blindly idempotent because it stamps a new random dataset
generation and advances persisted accelerator and key epochs.

Before attempting it, the journal preselects and records the exact target dataset generation plus
the expected target `AcceleratorEpoch`, `KeyReclamationEpoch`, and `EnvelopeKeyEpoch`, then marks
`CanonicalApplied` in flight. The transaction accepts those values rather than generating a target
internally and refuses any source mismatch. Recovery resolves it as follows:

- the exact source generation and epoch tuple is unchanged: the transaction did not commit, so it
  may retry with the already journaled targets;
- the exact target generation and epoch tuple plus empty-family, preserved-authority, cursor, and
  integrity proof matches the intended transition: accept the phase as committed; or
- any other state: retain `KeepClosed` and require reconciliation.

The handler never reruns an ambiguous canonical commit merely because the journal lacks its
post-effect publication.

After storage candidate verification, the handler journals an in-flight runtime-authority phase and
invokes the existing `CovenantErasureTransition.PublishCommittedAsync` path before ordinary
admission opens. Recovery resolves or repeats that publication against the exact target dataset
generation and retired prior token families. This phase changes no persisted authority epoch and may
not expose ordinary work until its live authority proof is complete.

### 5.4 Compaction and replacement ambiguity

Before a compaction path that may atomically replace the database, the handler allocates a unique
operation-bound staging leaf and journals it with the exact pre-compaction destination and original
backup identities. After secure creation it journals the staging physical identity before export or
replacement. After export proof it also journals the exact staged replacement identity and encrypted
content digest before marking atomic replacement in flight. The existing fixed shared staging path is
not used for offline transitions.

Recovery may remove only staging owned by this operation. It accepts a changed destination only
when its physical/content identity is the exact journaled staged replacement and an exact SQLCipher
open proves integrity, compactness, empty-family state, preserved authority, original-backup identity,
and the journaled target dataset generation plus all three target epochs. A
`ReplacedButUnverified`, substituted destination, missing required original, wrong target tuple, or
unrelated staging artifact remains `KeepClosed` rather than rerunning replacement.

### 5.5 Dispositions

- **Commit and reopen:** final physical closure and residual proof pass; journal `ReopenPrepared`;
  privately reopen and verify; publish runtime authority; reconcile the database row and optional
  parent receipt; close the lane; spend and verify Covenant `CommitAndReopen`; retire the journal;
  then reopen ordinary admission.
- **Proven pre-effect rollback:** journal rollback intent; prove no destructive effect; privately
  reopen and verify the original candidate; mark the operation `Failed` without changing its launch
  binding; close the lane; spend and verify Covenant `RollbackAndReopen`; retire the journal; then
  reopen ordinary admission.
- **Keep closed or uncertain:** publish the exact phase, blocker code, and `KeepClosed`; release no
  ordinary admission and write no database status merely to describe the failure. Before releasing
  process-local ownership, spend the held Covenant lease's one `KeepClosed` disposition while the
  authenticated journal remains active; recovery later reconstructs fresh closed gate ownership from
  that journal.

Every post-publication exit must publish or preserve one of these outcomes. Falling out through
owner disposal is forbidden.

## 6. Host-wide admission

### 6.1 Independent process-local gate

Add singleton `IGrimoireConnectionAdmissionGate`, implemented by
`GrimoireConnectionAdmissionGate`. It is separate from Covenant health/eligibility:

- the Covenant gate establishes destructive-operation eligibility and protected authority; and
- the Grimoire gate controls process-local request, work, opening-attempt, and connection admission.

Its state and tickets are generation-based so authority issued before closure cannot become valid
after a later reopen. Only the exact offline-transition owner can begin, resume, disposition, or
reopen it.

For both current handlers, authority acquisition order is: held installation maintenance lock,
validated database launch binding, exact Covenant exclusive lease, verified journal publication,
then Grimoire closing/closed owner. Recovery authenticates the journal, consumes one exact
recovery-authority handoff to initialize the Covenant gate in a closed recovery posture, reconstructs
the Covenant lease from its exact recovery owner, and only then reconstructs the Grimoire owner.
Disposal runs only after the handler's one Covenant disposition and never substitutes one gate's
token for the other's.

### 6.2 Three ordinary lifetimes

The gate owns:

1. **Request leases** for protected `/api` and `/v1` requests, from before endpoint execution through
   asynchronous request-scope disposal.
2. **Work leases** for complete background work units, from before DI scope creation through provider
   disposition, database writes, and asynchronous scope disposal.
3. **Connection-opening tickets** for one physical native open attempt, from before SQLite is touched
   until opened, failed, or refused.

Stage 1 refuses new request/work leases and drains existing finite/billable work. Stage 2 closes
ordinary connection admission, revokes unresolved open attempts, waits for their exact cleanup,
closes enrolled handles, and clears pools. A native open that loses the generation race is physically
closed and refused before its ticket is released.

The exact initiating reset/factory request is promoted out of the request drain only after its
database launch binding and opening journal revision are durable. Promotion grants no general
database authority and cannot promote another request. Startup recovery is requestless.

### 6.3 External-effect frontier

A work lease owns an atomic external-effect-group frontier. Either maintenance revocation wins and
no provider call in that group begins, or effect start wins and reset waits through every provider
call and durable result required for that independently resumable group.

Maintenance revocation is never passed to a provider call or durable disposition after the effect
frontier wins. Existing host/client cancellation remains separate. Maintenance denial does not
increment retry counters, advance watermarks, classify provider failure, or create another billable
attempt.

### 6.4 EF and raw connection acquisition

Every production `ArcanumDbContext` options path installs the same singleton admission/enrolment
interceptor. It acquires before native open, revalidates immediately after open, enrolls the physical
handle with the drain, and releases exactly once across close, failure, disposal, and cancellation.

Every raw live-Grimoire opener uses one tested `GrimoireOrdinaryConnectionFactory` or a narrow
operation-specific maintenance factory. Maintenance connections are bound to owner, generation,
canonical path, mode, purpose, and one active lane; they are unpooled, one-shot, initialized, tracked,
and physically closed before lane release.

The closed-period journal design removes maintenance authority whose only purpose was database
checkpoint, owner-reread, heartbeat, or `ReconciliationRequired` I/O. Maintenance database opens
remain only where the transformation itself must inspect or change the database.

An exact bidirectional source inventory records file, enclosing member, path authority, and runtime
admission for every production open/acquisition call site. Broad directory or factory-name
exemptions are forbidden.

## 7. Startup and recovery

`GrimoireDatabaseHostedService` resolves external recovery evidence under the exact maintenance lock
before database bootstrap, API readiness, or affected hosted services. Startup has two deliberately
different database paths:

1. **Active external evidence.** Inspect the installation-reset/Grimoire-journal pair using only
   `IOsCredentialStore`, physical filesystem identity, secure bounded file I/O, and source-generated
   JSON. A narrow recovery unlocker may then resolve the configured database path and passphrase and
   validate an existing SQLCipher catalog, but it cannot install, restore, rekey, change KDF settings,
   apply schema, or create a database. Its unpooled probes are physically disposed before the typed
   handler enters its owner-bound maintenance lane. For Covenant kinds it also invokes a split
   read-only `CovenantRecoveryAuthorityBootstrapper`: this loads the minimum persisted authority and
   availability facts, verifies them against the authenticated launch/current-phase evidence, and
   returns a one-use opaque handoff that initializes `CovenantOperationGate` directly in a closed
   recovery posture. It publishes no database readiness, ordinary Covenant availability, or reusable
   token and performs no transition effect.
2. **No active journal.** Run the normal installation-reset recovery and database bootstrap. Before
   publishing readiness, run the exact launch-gap adopter from §5.1. An adopted row publishes its
   journal and returns to the active-evidence path; absence of an eligible row permits readiness.

The active-evidence outcomes are:

- **one exact active journal:** publish matching closed process-local ownership, keep readiness
  closed, dispatch the exact typed handler, and resume from completed/in-flight state;
- **database reconciliation pending:** do not repeat destructive phases; privately reopen and verify
  only as the journal requires, terminalize the exact database row, publish any parent receipt, and
  retire the journal;
- **retirement pending or closed anchor with exact final file:** perform only exact terminal cleanup;
  or
- **invalid, unknown, ambiguous, mismatched, multiple, or active-anchor-without-file evidence:** keep
  admission/readiness closed and report manual recovery.

The dual-record matrix in §3.6 decides whether installation-reset recovery or the nested transition
runs next. Recovery runs outside the generic bounded long-running-operation reconciliation pass.
Generic reconciliation skips only the exact operation named by authenticated evidence or adopted by
the strict launch-gap rule; non-erasure operations remain unchanged.

Before dispatching either current handler, startup consumes the verified recovery-authority handoff,
reconstructs and holds the exact Covenant exclusive recovery lease from the authenticated
operation/kind/effect binding, then reconstructs the matching Grimoire closed owner. The handoff is
valid only under the same held maintenance lock and exact journal revision; it cannot initialize an
ordinary gate or be replayed. Failure to load persisted facts or acquire either authority leaves
readiness closed and performs no handler effect.

`EntryWeavingService`, `SessionAttachmentIndexingService`, and `SagaExtractionService` remain behind
the database-readiness barrier. Cancellation or recovery failure leaves readiness closed.

## 8. API and stream behavior

Request pipeline order is fixed:

1. API-key authentication;
2. path-selected Grimoire request admission for segment-safe, case-insensitive `/api` and `/v1`;
3. installation-reset admission and Covenant prebinding; and
4. endpoint execution.

`/metrics`, `/apiary`, and `/v10` do not become protected by prefix accident. Anonymous or
peer-authenticated endpoints under `/api` remain protected by path authority.

New requests after stage 1 receive:

- `/api/**`: HTTP `503`, source-generated `ApiResponse<string>`, code
  `Grimoire.MaintenanceUnavailable`;
- `/v1/**`: HTTP `503`, source-generated OpenAI-compatible error with type
  `service_unavailable`.

Messages are fixed and sanitized. Expected maintenance refusal is not logged at Error level and never
includes paths, owners, operation ids, phases, native details, or stack traces.

Exactly five SSE routes are marked `GrimoireQuiesceableStream`:

- `/api/events/daemon`;
- `/api/events/mcp`;
- `/api/events/logs`;
- `/api/sessions/{id:guid}/stream`; and
- `/api/apprentices/{id:guid}/chronicle`.

On revocation they finish the frame already being written, begin no new frame, cancel and observe
producer work, dispose enumerators/scopes, and end the existing response normally. Finite and
billable streams are not maintenance-cancelled; stage 1 drains them through durable completion.

A bidirectional route inventory keys every streaming response by route pattern, endpoint member,
response framing, Grimoire authority, and one exact class:
`GrimoireQuiesceableStream`, `FiniteDrain`, `BillableDrain`, or `NoGrimoireAuthority`. The five routes
above are the complete positive quiesceable set. OpenAI SSE, native inference and web-research NDJSON,
and file/session content streams receive explicit finite or billable classifications. A new or
unclassified streaming route fails the inventory; prefix or folder exemptions are forbidden.

## 9. Background workers

### 9.1 Entry weaving

One work lease covers a complete tick and asynchronous scope disposal. One effect group covers its
embedding provider call plus every resulting canonical/optional-vector upsert. Denial returns
`DeferredForMaintenance` with no scope/provider/write. Entry weaving retries at its normal bounded
poll cadence and does not spin or enter the generic fault loop.

### 9.2 Attachment indexing

One work lease covers one dequeued request and any genuine failure-classification scope. One
sequential effect group covers each embedding batch plus append or durable failure classification.
Maintenance deferral retains the original pending identity and attempt. Reopen writes that exact
request directly to the bounded channel once; it does not call the deduplicating enqueue path.

### 9.3 Saga extraction

One work lease covers one pending extraction across all pages and scope disposal. One effect group
covers a page's model call, embeddings, memory inserts, suppression/partial-success disposition, and
watermark commit. If page 1 commits and page 2 loses the frontier, recovery resumes at page 2 without
rebilling page 1. The exact pending request/provenance is restored and re-signalled once after reopen.

All three workers distinguish maintenance deferral, genuine product failure, and host cancellation.
No detached next-generation waiter or delayed retry may survive shutdown unobserved.

### 9.4 Remaining hosted producers

An exact hosted-producer inventory also covers `WorkspaceIndexingService`,
`TapestryWeavingService`, `BatchProcessingService`, `UnseenServantService`, `ApprenticeService`, and
`DataRetentionSweepHostedService`, plus every startup, recovery, backup, and maintenance host that
can resolve a database scope, open the live catalog, or begin a provider/filesystem effect.

Each ordinary runtime producer receives the same work/effect-frontier contract at its smallest
independently resumable durable unit. A nonordinary producer instead has an exact owner-bound startup,
recovery, stopped-host, or maintenance classification and cannot run concurrently with the offline
transition. `InstallationResetRecoveryAwareHostedService<T>` readiness gating alone is not runtime
revocation. The bidirectional inventory fails on every new hosted producer or scope/open/effect path
without one of these runtime protections or exact classifications.

## 10. Testing strategy

Every behavioral change follows an observed RED, minimal GREEN, and focused review. Repository-wide
qualification is reserved for the final reviewed SHA.

### 10.1 Journal protocol

Tests cover:

- source-generated canonical round trip and strict unknown-member/version refusal;
- profile, installation, operation, kind, location, revision, and previous-digest binding;
- opening-anchor-before-file behavior;
- every crash boundary around temp write, file fsync, rename, directory fsync, secure reread, and
  anchor CAS/readback;
- exact one-file-revision-ahead convergence and refusal of every other skew;
- exact closed-epoch to next-active-epoch CAS, active same-operation retry, closed same-operation
  refusal, and `Active` revision-zero/no-file fail-closed behavior;
- every legal lifecycle edge and refusal of every illegal, skipped, reversed, cross-intent, or
  same-revision payload-changing edge, including the one-way terminal-intent sub-state;
- permission, no-follow, hard-link, identity-substitution, case-alias, stale-temp, and multi-active
  attacks;
- key one-shot zeroing and absence/mismatch behavior;
- closed-anchor-before-delete retirement, exact final-file cleanup, and earlier/different-file replay
  refusal; and
- test-only second-handler registration without production migration behavior.

### 10.2 Handler idempotency

Fault injection occurs before and after every in-flight publication, effect, proof, and completed
publication. Tests pin repeatable phases, canonical transaction resolution, compaction replacement
resolution, exact kind-specific factory continuation, absence of closed-period database bookkeeping,
operation-bound staging ownership, immutable launch-row preservation, runtime Covenant-authority
publication, `KeepClosed` authority and disposition, terminal database CAS winner, parent receipt and
Covenant-disposition ordering, reconciliation-pending retry, and retirement after an already-terminal
exact database row without repeating erasure.

### 10.3 Admission and runtime races

Deterministic barriers, not sleeps, prove request/work/physical-open versus both closing stages,
effect-group start versus revocation, lane ownership versus recovery adoption, exact initiator
promotion, EF/raw open cleanup, the five positive streams, finite/billable stream exclusions, and each
worker/hosted producer's protection or exact nonordinary classification, deferral, scope disposal,
reopen signal, and shutdown behavior.

### 10.4 Startup and full-host proof

Startup tests cover no journal, active phase, every in-flight phase, `KeepClosed`, database
reconciliation pending, retirement pending, exact terminal row, conflicting or missing row, unknown
handler, malformed evidence, opening anchor without a file, launch-row-before-journal adoption, and
the complete installation-reset/transition-journal pair matrix before readiness.

Full-host tests drive authenticated HTTP direct reset and healthy-catalog factory erasure. They hold
finite requests, billable streams, all five quiesceable streams, and a representative worker effect
across closure, then prove correct drain, physical handle absence, commit/reopen, keep-closed, and
next-generation behavior.

## 11. Documentation and compatibility

Implementation updates travel with code:

- `README.md` summarizes offline transitions and host-wide maintenance admission;
- `docs/Arcanum.DESIGN.md` replaces the stale same-database checkpoint model in Covenant sections
  10.20.3–10.20.6 and documents journal security, startup, workers, and acquisition inventory;
- `docs/Arcanum.API.md` documents stable `/api` and `/v1` maintenance `503` responses and stream
  quiescence; and
- exact credential-account and source-inventory contracts are updated.

There is no endpoint route, request/response DTO, CLI verb, user configuration key, or database
schema change. New launches use the V4/V2 immutable launch checkpoint codecs; existing Covenant V3
and factory V1 codecs remain strict legacy-read contracts and never gain missing target authority by
inference. There are no current installed users whose in-flight checkpoint rows require migration.
Ordinary non-Covenant long-running-operation checkpoints are unchanged.

## 12. Sub-issue and branch plan

### 12.0 Current #247 boundary

#247 now supplies the strict `CovenantOfflineTransitionLaunchV4` and
`DataRetentionFactoryTransitionLaunchV2` checkpoint codecs and their immutable
operation/kind/recovery/effect/source/target/starting-revision launch fields, the single projection
that turns either shape into the journal launch binding, the domain-separated
`DatabaseOperationLaunchBindingDigest` and the journal-binding constructor that derives it and the
expected row revision rather than accepting them, the unconditional refusal that stops a legacy V3/V1
checkpoint becoming a launch, and the exact terminal reconciler: one compare-exchange to `Completed`
or to a journal-proven pre-effect `Failed`, a winner reread under the same rules, an idempotent
already-terminal arm carrying the identical winner digest, and separate non-writing refusals for a
missing, conflicting, moved, or foreign-terminal row, none of which permits journal retirement.
Existing V3 and V1 decoders are untouched strict legacy reads; no production path writes V4/V2, and
the recovery window, both recovery handlers, and the pre-readiness adopter still pin 3 and 1. #247 is
**delivered** after focused RED/GREEN proof, bounded final review, a warning-free Release solution
build, changed-file style verification, and a clean branch diff. This boundary does not qualify the
parent or activate a journal handler.

#248 remains responsible for the typed Covenant reset/factory effect handler, canonical target binding,
compaction and sidecar recovery, factory-row preservation, runtime-authority publication, and removal
of the exact V3 adapter entries. #257 alone owns the final full-host, cross-platform, Native AOT, and
umbrella qualification on the reviewed final SHA.

Issue #239 remains the umbrella and closes only after every child is integrated and the final branch
is delivered. Create these separately reviewable children in dependency order:

1. #243 — authenticated fixed-slot journal, encryption, anchor, secure publication, and retirement;
2. #244 — closed typed lifecycle/state graph, codec registry, and test-only extension proof;
3. #245 — process-local request/work/open admission gate and atomic external-effect frontier;
4. #246 — EF/raw acquisition enforcement, physical-handle drain, maintenance factories, and exact
   source inventory;
5. #247 — V4/V2 immutable launch binding and exact terminal database reconciliation;
6. #248 — Covenant reset/factory typed effect handler, canonical target binding, compaction recovery,
   factory-row preservation, and runtime-authority publication;
7. #249 — installation-reset parent receipt, dual-record ordering, and final credential cleanup;
8. #250 — pre-readiness startup recovery, recovery-only unlock, and launch-gap adoption;
9. #251 — HTTP request admission and stable `/api`/`/v1` maintenance responses;
10. #252 — streaming-route inventory and exact five-stream frame-boundary quiescence;
11. #253 — Entry weaving work/effect lifetime;
12. #254 — attachment indexing deferral, pending-identity preservation, and exact reopen signal;
13. #255 — Saga page-boundary deferral, provenance preservation, and exact reopen signal;
14. #256 — remaining hosted-producer inventory, runtime protection, and nonordinary classifications;
    and
15. #257 — full-host races, documentation, one final review, and qualification.

Children 1 and 3 may proceed independently. Child 2 follows 1; child 4 follows 3; child 5 follows
1–4; children 6–8 follow 5 in order. Children 9–14 branch from the integrated gate/acquisition
foundation and remain independent of one another where their tests do not share files. Child 15
starts only after all prior children are integrated.

Child branches use `codex/`, merge into the umbrella feature branch, and are deleted after review.
The fully reviewed and qualified umbrella branch alone fast-forwards into `main`.

### 12.1 Feature Tracker compatibility

Creating these children is additive. Existing Feature Tracker issues keep their title, body, state,
state reason, labels, assignees, milestone, priority, size, estimate, iteration, and status. In
particular, closed issues #38, #39, #40, #50, #58, #87, #94, #102, #109, #111, and #118–#128 are
historical contracts and are neither reopened nor re-scoped. Open issue #242 remains an independent
Windows symptom report in `Ready`; the acquisition child references it as related evidence but does
not edit, reparent, close, or move it.

The only existing-issue relationship mutation is the explicitly requested addition of new children
to parent #239, which necessarily changes #239's derived sub-issue progress and updated timestamp.
#239's authored content and workflow fields remain unchanged until final delivery moves only #239 to
Done and closes it. Before/after canonical snapshots of all pre-existing issue fields and project
item values, excluding only those derived parent-link fields, must compare byte-for-byte.

## 13. Out of scope

- Implementing a database migration, migration phase enum, schema transformation, or public migration
  surface.
- Generalizing the journal into a framework for unrelated filesystem workflows.
- Moving the broader installation-reset active record into the offline-transition journal.
- Changing Covenant eligibility, disclosure accounting, effect digests, or protected erasure scope.
- Weakening physical connection drain, SQLCipher policy, WAL/SHM absence proof, or Native AOT JSON
  requirements.
- Retrying provider work because maintenance began.
- Adding an operator-configurable maintenance switch or public maintenance endpoint.

## 14. Review, qualification, and delivery

Each child receives one bounded read-only review. Critical and Important findings are fixed through a
focused RED/GREEN loop before integration. After all children are merged, perform one whole-branch
review and run the locally applicable qualification matrix once on the exact unchanged final SHA:

- warning-free restore and Release solution build;
- Compendium and The Forge tests;
- threshold coverage, which supplies the complete Arcanum test suite;
- fresh Native AOT/IL verification;
- Covenant benchmark gate;
- native SQLCipher manifest and `osx-arm64` provenance;
- formatting, shellcheck, workflow, packaging, generated-contract, source-inventory, and documentation
  gates;
- `git diff --check` and clean tracked status.

Push that exact feature SHA and manually dispatch CI. Windows x64 and Windows ARM64 are required
evidence for #239 even if one lane is otherwise nonblocking for beta. The verified SHA alone may be
fast-forwarded into `main`; no rebuild is needed after a byte-identical fast-forward.

After `main` is pushed, detach or move the feature worktree without deleting the two unrelated
issue-221 files, delete every #239 child/feature branch created for implementation, post the exact
verification evidence, close all completed child issues, close #239, and move #239's project item to
Done as the final mutation.
