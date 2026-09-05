# Issue #249 Nested Factory Erasure and Installation-Reset Receipts Design

**Status:** Approved for implementation.

**Branch:** `codex/issue-249-nested-factory-receipts`, cut from `grimoire-fixes` at
`d2bac882bb86d795494b062f99b4d74e1ab83acc` (the #248 merge).

**Issue:** [#249 — Grimoire: integrate nested factory erasure with installation-reset receipts](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/249)

**Parent design authority:** `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`.
Where this document and the parent disagree, the parent governs except on the four points §1.2
records as deliberate departures.

## 1. Decision

### 1.1 What this child delivers

A healthy-catalog factory erasure launched by a full installation reset stops being indistinguishable
from a standalone one. The outer installation-reset record and the inner offline-transition journal
each keep their own authority, and neither may stand in for the other: the outer record names the
nested operation before it starts and holds its completion receipt afterwards, while the journal
remains the sole phase authority for the transformation itself.

Three things follow from that, and they are this child's whole content.

First, the parent-receipt seam #244 shaped and #248 left unfilled is filled. Today
`GrimoireOfflineTransitionPhaseAuthority` mints every journal binding with
`parentReceiptBindingDigest: null`, so every production transition takes the "no parent required"
arm and `RecordParentReceiptAsync` copies the binding digest into the evidence that is checked
against it. This child supplies a real binding at journal open, a real publication into the outer
record after the exact terminal database compare-exchange, and a reread whose digest is recomputed
from what the outer record actually holds.

Second, startup resolves the two records as a pair. The eight-arm matrix the parent design states in
§3.6 becomes an executable decision over authenticated evidence, and the host runs it under the same
held installation maintenance lock before database bootstrap. Four of its arms fail closed.

Third, the transition slot's two credentials stop being unconditionally retained. A full installation
reset may remove `grimoire-transition-journal-anchor-{ns}` and `grimoire-transition-journal-key-{ns}`
in its final credential cleanup, after it has proved the journal file absent, read the anchor `Closed`,
and observed the nested completion receipt retained. Every other path — ordinary credential cleanup,
a direct Covenant reset, a standalone factory erasure, a family reinitialize, and an unattested
installation reset — still retains both byte for byte.

### 1.2 Deliberate departures from the parent design

Four decisions in this child differ from, or resolve silence in, the parent design. Each is recorded
here so the divergence is a decision rather than drift.

1. **The outer active record moves to `InstallationResetActivePayloadV3`.** Parent §3.6 says only that
   the outer arm "supplies an optional typed parent-receipt sink" and does not say where the receipt
   is stored. The nearest house precedent — #122's `FullInstallationResetRemediationClaimV1` — is a
   nullable member appended to the V2 payload under the same envelope version. This child instead
   bumps the payload and envelope to 3, retains 2 as a strict decode-only legacy contract, and
   migrates an authenticated V2 record forward before its next effect, exactly as `MigrateLegacyV1Async`
   already migrates a plaintext V1. The reason is that the receipt is not an optional annotation on a
   record that would otherwise be complete: once a nested transition exists, the absence of a receipt
   is itself load-bearing evidence — arm four of §3.6's matrix fails closed on a claim without one —
   and a member that older readers silently ignore cannot carry a fact whose absence is a refusal.

2. **The sink is resolved from the outer record, not handed down from the caller.** Parent §3.6 says
   the installation-reset arm "supplies" the sink, which reads as a parameter passed inward. A passed
   parameter cannot survive a crash: recovery in a fresh process has no caller, and the journal it
   resumes already carries a non-null `ParentReceiptBindingDigest` it must satisfy. This child makes
   the sink a resolver that reads the outer authenticated record under the held maintenance lock and
   returns the bound sink, the absence of a parent, or a refusal. First entry and recovery therefore
   take the identical path, and "a parent-bound journal without its outer record" is a state the
   resolver can name rather than one only a caller's absence would imply.

3. **Startup wiring lands here rather than with #250.** Parent §12.0 assigns pre-readiness dispatch
   and the recovery-only unlock to #250, and this child does not implement either. It does wire the
   matrix into `InstallationResetStartupRecovery.RecoverBeforeBootstrapAsync` and
   `GrimoireDatabaseHostedService`, because a matrix nothing calls proves nothing about the host, and
   the four fail-closed arms are refusals that must exist before a journal-driven erasure can crash in
   the field. What the host does on the three admitting arms is to keep readiness closed and report
   manual recovery; replacing that refusal with dispatch remains #250's.

4. **The restore-credential cleanup phase vocabulary is extended in place.** Parent §3.6 assigns the
   transition pair's removal to "final credential cleanup" without saying whether that is the existing
   ordered removal or a sibling of it. This child extends
   `InstallationResetRestoreCredentialCleanupPhase` rather than adding a parallel type, so one enum
   still describes how far the one final cleanup has got. Codes 1 through 4 keep their exact numeric
   values — the enum serializes as a number inside an in-flight active record — and the terminal moves
   from code 4 to a new code 7. A record resumed at 4 therefore reads as "the restore trio is gone and
   the transition pair is still owed", which is what such a record actually means.

### 1.3 What this child does not deliver

Pre-readiness dispatch of a resolved handler, the recovery-only unlock, the split
`CovenantRecoveryAuthorityBootstrapper`, and the §5.1 launch-gap adopter scan (#250); HTTP
maintenance responses (#251); stream quiescence (#252); worker adoption (#253–#256); and
whole-branch qualification (#257).

It adds no endpoint route, request or response shape, CLI verb, configuration key, database schema,
DDL identity, numbered migration, migration transition kind, or public Covenant contract. It moves no
part of the broader installation-reset workflow into the offline-transition journal, and it adds no
attestation authority from #121 and no host-tools marker authority from #122.

## 2. Scope

### 2.1 Included

- `InstallationResetNestedTransitionReceiptV1` and its two-value monotonic phase, carried on the
  authenticated installation-reset active record.
- `InstallationResetActivePayloadV3` and envelope version 3, with version 2 retained as a strict
  decode-only legacy contract migrated forward before its next effect.
- The nested claim published by the installation-reset database arm before the nested apply, and the
  stable nested operation identity that arm now supplies.
- The domain-separated parent-receipt binding digest, computable at journal open and recomputable
  from an observed outer record.
- `IGrimoireOfflineTransitionParentReceiptResolver` and the typed sink it returns, reached identically
  on first entry and on recovery.
- A non-null `ParentReceiptBindingDigest` at journal open for a parent-bound healthy-catalog factory
  erasure, and the refusal of a parent binding on any other kind.
- The publish-and-reread of the exact completion receipt in the reconciliation suffix, between the
  terminal winner reread and `ParentReceiptSatisfied`, with the recorded digest recomputed from the
  reread rather than copied from the binding.
- Idempotent replay of that publication: an already-exact receipt is reread and never republished, and
  the outer envelope revision does not advance a second time.
- The complete eight-arm none/one/both evidence matrix as a pure resolver over authenticated evidence,
  including the journal-already-retired suffix arm and the four fail-closed arms.
- That matrix wired into `InstallationResetStartupRecovery` under the held maintenance lock and acted
  on by `GrimoireDatabaseHostedService` before database bootstrap.
- `GrimoireOfflineTransitionFullResetTerminalProjectionV1` and the anchor-store proof that produces
  it, taken before the first removal of the final cleanup.
- Removal surfaces on the transition key provider and anchor store, and the two new ordered
  compare-removal phases and terminal phase on the existing restore-credential cleanup.
- The amended closed inventory of production sources permitted to name a transition-account deletion.
- Fault injection at each new boundary: before and after the outer publication, before and after the
  reread, and before and after the journal's own parent step.

### 2.2 Excluded

- Any new `GrimoireOfflineTransitionState`, terminal intent, reconciliation step, handler outcome, or
  transition kind. The graph #244 closed stays closed, and the `ParentReceiptSatisfied` step this
  child fills already exists at its exact position.
- Any new offline-transition payload version. `GrimoireOfflineTransitionBinding.ParentReceiptBindingDigest`
  already exists on the two V1 payloads.
- Any new `CovenantResetPhase` member, and any second enum declaring that name set.
- Any change to Covenant eligibility, disclosure accounting, effect-digest domains, protected erasure
  scope, the preservation set, or the route and result contracts frozen by #128.
- Any relaxation of the ordinary credential catalog's exclusion filter. The transition pair stays
  excluded from `CollectOrdinaryAccounts`; the new removal is a separate proof-gated path.
- Any removal of the three restore-journal accounts by a nested transition, and any removal of the
  transition pair by a path other than an attested full installation reset's final cleanup.

## 3. Authority order

The nested transition acquires authority in the order #248 fixed, unchanged. This child adds one
authority the parent workflow holds and the child borrows, and it is acquired before the journal:

1. the held installation maintenance lock, borrowed and never reacquired or disposed;
2. the authenticated outer installation-reset record, read under that lock, whose nested claim names
   this operation;
3. the validated database launch binding;
4. the exact Covenant exclusive lease;
5. the verified journal publication, now carrying the parent-receipt binding digest; then
6. the Grimoire closing and then closed owner.

An outer record never mints a journal, and a journal never authorizes a write to the outer record
beyond the one receipt its own binding digest names. The two records never substitute for one
another: the outer record is broader workflow authority and the journal is the sole phase authority
for the nested database transformation, exactly as parent §3.6 requires.

## 4. The receipt

### 4.1 What the outer record carries

`InstallationResetNestedTransitionReceiptV1` records the nested transition as the outer workflow sees
it, and nothing else:

```
byte Version                                        // exactly 1
Guid NestedOperationId                              // never Guid.Empty
InstallationResetNestedTransitionPhase Phase        // Claimed = 1, Completed = 2
CovenantDigest? NestedEffectDigest                  // null at Claimed, valid at Completed
CovenantDigest? TerminalWinnerDigest                // null at Claimed, valid at Completed
```

It carries no path, credential, passphrase, key, generation, epoch, lease, handle, connection, count,
subject identity, or disclosure detail. The outer operation id is not repeated on it: the receipt
lives inside the payload whose `OperationId` is that value, and a second copy would be a second place
for the two to disagree.

`Claimed` is published before the nested apply begins and is the fact that makes arm four of §3.6's
matrix decidable — a claim without its receipt is a nested transition that started and cannot be
treated as never started. `Completed` is published by the nested handler after the exact terminal
database compare-exchange. The sub-state is one-way: `Claimed -> Completed` with the same
`NestedOperationId`, and a `Completed` receipt never changes again. Absent, `Claimed`, and `Completed`
are three distinct facts, and no edge skips, reverses, or substitutes one for another.

### 4.2 The binding digest

`ParentReceiptBindingDigest` is SHA-256 under
`arcanum.grimoire.offline-transition.parent-receipt-binding.v1` over, in order: the outer operation
id as RFC-4122 bytes, the nested operation id as RFC-4122 bytes, the nested canonical effect digest,
and the single byte of `InstallationResetNestedTransitionPhase.Completed`.

Every one of those four is known at journal open, so the binding is committed before any effect. Every
one of them is also readable from an observed outer record whose receipt has reached `Completed`, so
the same value is recomputable afterwards from a different source at a different time. That is what
makes the comparison the lifecycle validator already performs — recorded evidence digest equals
committed binding digest — a proof rather than an identity.

The terminal winner digest is deliberately outside the preimage. It cannot be known at journal open,
and a binding that included it could not be committed before the effect it is meant to bind. It is
recorded on the receipt as evidence and cross-checked against the journal's own
`DatabaseTerminalWinnerDigest` by the evidence matrix, which is the one place both records are in
hand.

A receipt still at `Claimed` recomputes to a different digest and therefore cannot satisfy a binding.
That is the mechanism, not a separate rule.

### 4.3 The resolver and the sink

`IGrimoireOfflineTransitionParentReceiptResolver.ResolveAsync` takes the held maintenance lock, the
transition kind, the nested effect digest, and — on a resume — the binding digest the journal already
committed to. It reads the outer record and answers with one of three things:

- **no parent** — the outer record is absent, or present and carrying no nested receipt at all. The
  transition is standalone. `ParentReceiptBindingDigest` stays null and the suffix records
  `ParentReceiptNotRequired`;
- **a bound sink** — the outer record carries a receipt whose `NestedOperationId` is exactly this
  operation. The sink exposes the binding digest §4.2 defines and the one publication §4.4 describes;
  or
- **a refusal** — content-free `Covenant.ManualRecoveryRequired`. The refusing cases are a claim on a
  transition whose kind is `CovenantReset`, a `Completed` receipt whose recorded nested effect digest
  is not this launch's, an outer record that cannot be authenticated, a resume whose recomputed
  binding does not equal the one the journal committed to, and a resume carrying a committed binding
  with no outer record at all.

The resolver reads; it never repairs, never creates an outer record, and never advances one on the
resolve path. Because it reads rather than receives, first entry and recovery reach the same answer
from the same evidence, which is the property a handed-down parameter cannot have. The last refusal
above is exactly §5's "journal only, parent binding present" arm reached from inside the transition
rather than from startup.

The nested operation the receipt names is the request identity the outer arm mints and passes as the
nested apply's `RequestedOperationId`, so the outer record's claim names something the operation
ledger also knows, and a resumed reset replays the one nested operation rather than starting a second.

### 4.4 Publish and reread

The publication sits in the reconciliation suffix, after the exact terminal winner has been reread and
recorded, and before `ParentReceiptSatisfied` is published. Its steps are fixed:

1. read the outer record under the held lock and authenticate it;
2. if its receipt is already `Completed` with this nested operation, this launch's effect digest, and
   this transition's terminal winner digest, publish nothing — the outer envelope revision does not
   advance a second time;
3. otherwise compare-advance the receipt from the exact `Claimed` state to `Completed`, carrying the
   nested effect digest and the terminal winner digest;
4. reread the outer record afresh and authenticate it again; and
5. recompute the binding digest from the receipt that reread returned.

Step 5 is the point of the whole sequence. `RecordParentReceiptAsync` no longer derives evidence from
the binding it is about to be checked against; it takes the recomputed digest as an argument and
refuses a null one when the binding is non-null, and a non-null one when the binding is absent. A
mismatch between the recomputed digest and the committed binding parks `KeepClosed` at the exact
suffix step with a content-free blocker: the database is already terminal, the effects must not repeat,
and the honest answer is that the two records disagree about what happened.

Step 2 exists because publishing into the outer record advances its envelope revision and digest, and
#122 §6 makes every authority bound to the previous revision stale at that moment. A replay that
republished an identical receipt would invalidate the outer workflow's own authority for no new fact.

## 5. The evidence matrix

`InstallationResetNestedTransitionEvidence.Resolve` is a pure function of two already-authenticated
recovery states — the installation-reset active record and the offline-transition journal — and
returns one outcome. It performs no I/O, mutates nothing, and is the only place the pair is
interpreted.

- **neither active:** `NeitherActive`. Normal launch-gap inspection and bootstrap rules apply.
- **installation-reset only, no nested receipt:** `NestedNotStarted`. The broader recovery may begin
  its nested transition later.
- **installation-reset only, receipt `Completed`:** `NestedRetired`. The journal is already retired
  and the broader workflow continues.
- **installation-reset only, receipt `Claimed`:** fail closed. A claim without its receipt cannot be
  read as a transition that never started, and the journal that would have said otherwise is gone.
- **journal only, no parent binding:** `StandaloneTransition`. A direct reset or standalone factory
  erasure is dispatched.
- **journal only, parent binding present:** fail closed. A nested transition may not be downgraded to
  standalone work.
- **both active, reset claimed nothing, journal names no parent:** `StandaloneTransition`. Two
  authorities over separate work may be open at once — a broader reset in its own phases beside an
  erasure the running host started for itself — and neither of them says otherwise. A journal that
  does name a parent here still fails closed.
- **both active and claimed:** `NestedBound`, and only when the journal's kind is
  `HealthyCatalogFactoryErasure` and its parent binding is non-null and exactly equals the digest
  recomputed from the outer record's operation id, the receipt's nested operation id, and the
  journal's own effect digest. A missing binding, a mismatched binding, and a nested `CovenantReset`
  each fail closed.
- **both active, receipt already `Completed`:** `NestedReceiptStoredRetirementSuffix`, and only when
  the journal names the same terminal winner the receipt reports and is in
  `DatabaseReconciliationPending`, `RetirementPending`, or `KeepClosed`.

The second of those is tested by the terminal winner rather than by the phase. The completion receipt
is published after the terminal winner is journaled and before the journal records its own parent
step, so the state between those two writes is one §4.4's ordering guarantees will occur; demanding
the later phase would classify the crossing window itself as a disagreement. A parked journal is
admitted for the same reason: parking is the resumable state and what remains of it is still only the
suffix. A journal that recorded no terminal winner, or a different one, cannot have produced the
receipt and fails closed.

The two records are not tied together by comparing operation identities. The receipt names the
identity the nested apply was requested under, and the operation ledger mints a separate durable
identity for the operation it then creates, so those values never match. The binding digest is the
tie, and both sides derive it from the same claim and different halves of the evidence.

Every fail-closed arm produces the same content-free `Covenant.ManualRecoveryRequired` refusal. The
arm is not named in the message: which of eight states an installation is in is exactly the kind of
detail the parent design keeps out of operator-visible text.

### 5.1 Where it runs

`InstallationResetStartupRecovery.RecoverBeforeBootstrapAsync` already asserts the held installation
maintenance lock and recovers the outer record. It now also recovers the journal under that same lock,
runs the matrix, and returns the outcome on `InstallationResetStartupRecoveryState`.

`GrimoireDatabaseHostedService` acts on the outcome before `GrimoireDatabaseBootstrapper` runs. A
fail-closed outcome refuses startup with the existing content-free idiom. `NeitherActive`,
`NestedNotStarted`, and `NestedRetired` proceed exactly as they do today. `StandaloneTransition`,
`NestedBound`, and `NestedReceiptStoredRetirementSuffix` each keep readiness closed and report that
manual recovery is required, because an active journal means the database is mid-transformation and
this child does not dispatch a handler. Turning those three refusals into dispatch is #250's, and the
outcome type is shaped so that it can be.

`InstallationStartupProbe.ReadActiveResetAsync` is the lock-free path and cannot read the journal,
because the journal requires the lock. It keeps its present behavior and is not given a matrix result
it could not have computed honestly. The one production host that uses it does so only when no startup
recovery service was composed at all.

## 6. Final credential cleanup

### 6.1 The proof

`GrimoireOfflineTransitionJournalAnchorStore` gains a terminal proof that mirrors
`BackupRestoreJournalAnchorStore.ProveFullResetTerminal`. Under the held installation maintenance lock
it proves the canonical journal file and its three siblings absent through the existing
`ProveAbsentDurably` primitive, reads the anchor, and requires it `Closed`. It then reads the current
value of each of the two accounts and projects
`GrimoireOfflineTransitionFullResetTerminalProjectionV1`: the arm, the profile namespace digest, the
installation id, the closed anchor's slot epoch, operation, revision and envelope digest, the two
account value digests, and one domain-separated terminal evidence digest over all of them.

Two arms and no third: `NeverTransitionedAbsence`, where no anchor, no key and no file exist, and
`ClosedAnchor`, where the anchor is `Closed` and the file is absent. An `Active` anchor, an anchor
without a file that is not closed, a key with no anchor, a present file, and any observable ambiguity
each produce no projection and block removal. That is the existing rule of the slot restated for this
purpose, not a new one: a key with no anchor is already durable evidence of a genesis that began.

The proof additionally requires the outer record's nested receipt to be absent or `Completed`. A
`Claimed` receipt blocks: the reset has not finished the nested transition it started, and the
credentials that could finish it are exactly what is about to be removed.

Both this projection and the existing restore-trio projection are persisted into the active record
before the first removal of the whole cleanup, because a resumed removal cannot re-derive a digest
from a credential set that has since changed shape.

### 6.2 The order

`InstallationResetRestoreCredentialCleanupPhase` becomes:

```
AnchorRemoved = 1
JournalKeyRemoved = 2
InstallationIdentityRemoved = 3
RestoreCredentialsVerifiedAbsent = 4        // was VerifiedAbsent; same code
TransitionAnchorRemoved = 5
TransitionKeyRemoved = 6
TransitionCredentialsVerifiedAbsent = 7     // the terminal
```

Codes 1 through 4 keep their exact numeric values and their exact meanings. The transition pair is
removed anchor first and key second, the same reverse-of-authorization order the restore trio uses and
for the same reason: once the anchor is gone no surviving journal can authenticate, so no partially
removed state can be mistaken for a transition in progress.

The transition pair is removed after the restore trio rather than before it. The transition journal
seeds its installation identity from the restore journal's identity account, which phase 3 removes —
but the removals are compare-removals against digests projected while every account was present, and
a compare-removal needs no identity. A pass resumed after phase 3 therefore still finishes correctly
from the persisted projection, which is the property that decides the order.

Each removal is a compare-removal: the account's current value must reproduce the digest the
projection recorded for that exact account name. A value that changed since the proof means something
wrote to the slot after it was declared terminal, and the honest answer is to stop rather than delete
whatever is there now. Each phase is idempotent — an already-absent account is read as absent and the
pass advances — and `TransitionCredentialsVerifiedAbsent` requires a fresh read proving all five
accounts gone.

### 6.3 What does not change

`InstallationResetCredentialCatalog.CollectOrdinaryAccounts` keeps excluding the transition pair, so
no ordinary cleanup, Covenant reset, family reinitialize, or unattested installation reset can name
them. The removal added here is reachable only from `FullInstallationResetTerminalContinuation`, which
runs only on the attested arm and only after the managed-file inventory is terminal.

The closed inventory that today asserts no production source names a transition-account factory
together with a delete is amended to name exactly the one file that now does, and to keep failing on
any other. A broad prefix or folder exemption is not accepted.

## 7. Fault injection

Every new boundary gets one case, and the boundary name travels as the assertion message:

- before and after the outer `Completed` publication;
- before and after the reread and digest recomputation;
- before and after `ParentReceiptSatisfied`;
- between the two transition-account removals, and between the last restore removal and the first
  transition removal; and
- after `TransitionKeyRemoved` and before `TransitionCredentialsVerifiedAbsent`.

The journal boundaries use the recording and fail-before-step file and anchor stores #243 established,
driven from a second store instance over the same durable root and recovered by a fresh one. The outer
publication boundaries use a sink double that wraps the production sink and fails after its inner
call, which needs no new production seam and no widening of `CovenantErasureFaultBoundary`.

## 8. TDD strategy

Every behavioral change follows an observed RED, a minimal GREEN, and a focused review. Ordering
proofs use deterministic barriers or a manual time provider, never sleeps. Tests that reach a real
SQLCipher database stay skippable and join the existing serialized collections, because pool clearing
is process-global.

The pins that currently assert the absent-parent world are changed deliberately rather than
incidentally, and each gains its bound-parent counterpart:
`GrimoireOfflineTransitionLaunchBindingTests`, `GrimoireOfflineTransitionPhaseSessionTests`,
`GrimoireOfflineTransitionLifecycleTests`, `GrimoireOfflineTransitionLifecycleStoreTests`,
`GrimoireOfflineTransitionDatabaseReconcilerTests`,
`CovenantOfflineTransitionLaunchTerminalStoreTests`, the shared
`LocalOfflineTransitionPhaseAuthority` double, and
`GrimoireOfflineTransitionJournalKeyLeaseCallSiteTests`.

The matrix is proved as a table over all eight arms plus the cross-record winner disagreement, and
again through the host to prove the four refusals reach startup.

Repository-wide qualification remains #257's.

## 9. Verification and delivery

Focused child-scoped tests through the RED/GREEN loop, then a bounded review, a warning-free Release
solution build, changed-file style verification, and a clean branch diff. Coverage, the complete
suites, Native AOT and IL verification, the benchmark gate, native SQLCipher provenance, packaging,
full-host, and cross-platform qualification remain #257's on the final reviewed SHA.

Documentation travels with the code. `docs/Arcanum.DESIGN.md` §5.4.7, §10.20.3, §10.20.12, §10.20.13,
§10.20.14, §11.2.1 and §13.7; the status paragraphs and the credential, installation-reset and
Covenant sections of `docs/Arcanum.Engineering.md`; `docs/Arcanum.API.md` §8.20 and §8.23;
`docs/Arcanum.Command.Reference.md`'s `serve` startup-admission paragraph and `data factory-reset`
narration cell; `docs/Arcanum.OATH.md` §2.1, §11.5, §15.3 and §16;
`docs/ArcanumOATH.Human.md` §9 and §11; `docs/Arcanum.Design.Human.md` §8 and §12; and
`docs/Arcanum.DEBUGGING.Human.md`'s breakpoint map and recipes.

`README.md` and `docs/Compendium.README.md` do not change: this child adds no public front-page
behavior and no configuration key. The dated review records are append-only and are not edited.
Issue #242 is not edited, transitioned, reparented, closed, or claimed as resolved.
