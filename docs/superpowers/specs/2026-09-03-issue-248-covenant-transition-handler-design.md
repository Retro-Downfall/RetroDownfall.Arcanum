# Issue #248 Journal-Driven Covenant Reset and Factory Erasure Design

**Status:** Approved for implementation.

**Branch:** `codex/issue-248-covenant-transition-handler`, cut from `grimoire-fixes` at
`9032574f0b8e0e2ad06d4a8b0b58e1cbb5e8b2d4` (the #247 merge).

**Issue:** [#248 — Grimoire: make Covenant reset and factory erasure journal-driven and idempotent](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/248)

**Parent design authority:** `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`.
Where this document and the parent disagree, the parent governs except on the four points
§1.2 records as deliberate departures.

## 1. Decision

### 1.1 What this child delivers

Direct Covenant reset and healthy-catalog factory erasure stop keeping their phase authority inside
the database they are erasing. Both become typed offline transitions whose durable phase authority is
the authenticated journal delivered by #243, driven through the closed lifecycle delivered by #244,
across the admission boundary delivered by #245/#246, bound to the immutable launch and terminal
reconciliation delivered by #247.

The circularity this removes is the whole point of the parent issue: an erasure cannot write "I have
proved this database closed" into that database, and it cannot renew a lease through a connection
whose absence it is about to prove.

### 1.2 Deliberate departures from the parent design

Four decisions in this child differ from, or resolve silence in, the parent design. Each is recorded
here so the divergence is a decision rather than drift.

1. **The V3 reset and V1 factory checkpoint shapes are removed, not retained as legacy reads.**
   Parent §3.5 and §11 keep `DataRetentionMutationCheckpointV3` and
   `DataRetentionFactoryResetCheckpointV1` as strict legacy-read contracts. They are deleted instead.
   The justification the parent itself supplies is decisive: there are no installed users whose
   in-flight checkpoint rows require migration, so a legacy read has nothing to read. A shape nothing
   writes and nothing can encounter is not a compatibility contract, it is a second way to describe a
   launch — which is exactly what §5.1's "a legacy row cannot mint a journal" exists to forbid.
   The ordinary non-Covenant arms are untouched: the payload-free legacy factory arm and the ordinary
   `ARCAMUT2` retention-mutation shapes are not offline transitions and keep their behavior and their
   `MinCheckpointVersion: 0` admission.

2. **Effects live beside the handler, not on it.** Parent §3.4 says "a handler ... performs or
   replays one phase". The handler interface #244 shipped is a codec and edge validator with no
   asynchronous member and no cancellation token. Rather than widen that interface — which would
   force the codec registry to carry live kernels and would break the test-only handler that exists
   to prove the extension seam — this child adds a second closed table,
   `GrimoireOfflineTransitionEffectHandlerRegistry`, keyed on the identical `(Kind, PayloadVersion)`
   pair. The codec registry stays a pure lookup. The extension seam #244 proves is unchanged: a future
   migration kind registers in both tables.

3. **The recovery window moves in this child.** Parent §12.0 records "the recovery window, both
   recovery handlers, and the pre-readiness adopter still pin 3 and 1" as #247's boundary, and assigns
   launch-gap adoption to #250. Because this child is the one that starts writing V4/V2 rows, it must
   also be the one that makes such a row admissible, or the first row it writes is unrecoverable. It
   moves the window and teaches the adopter to recognize the new versions; it does not implement the
   §5.1 launch-gap scan, which remains #250's.

4. **Generic reconciliation gains an authenticated-evidence skip.** Parent §7 requires it
   ("Generic reconciliation skips only the exact operation named by authenticated evidence") but
   assigns no child. It cannot be deferred: removing closed-period lease renewal means the operation
   row's lease lapses during a long erasure, and `LongRunningOperationStartupHostedService` reconciles
   on a sixty-second background interval, so a lapsed row would be adopted by the generic reconciler
   while the transition that owns it is still running. This child adds the process-local ownership
   claim that closes that race; the cross-process case is already closed by the installation
   maintenance lock and the journal anchor.

### 1.3 What this child does not deliver

The parent-workflow receipt and final credential cleanup (#249); pre-readiness startup recovery, the
recovery-only unlock, and the launch-gap adopter's scan (#250); HTTP maintenance responses (#251);
stream quiescence (#252); worker adoption (#253–#256); and whole-branch qualification (#257).

No endpoint route, request or response shape, CLI verb, configuration key, database schema, DDL
identity, numbered migration, or migration transition kind is added.

## 2. Scope

### 2.1 Included

- An exact source generation and epoch-tuple read, and one preselection of the target tuple, before
  the launch row commits.
- `CovenantOfflineTransitionLaunchV4` and `DataRetentionFactoryTransitionLaunchV2` as the two shapes
  a launch commits at `InventoryPrepared`, replacing V3 and V1.
- The recovery window, both recovery handlers, and the pre-readiness adopter widened to admit them.
- Production composition of the journal store, codec registry, effect registry, lifecycle store, and
  terminal reconciler, and the maintenance-lock borrow that reaches them.
- Every phase moved onto the §4.2 in-flight, effect, prove, complete publication protocol.
- The canonical transaction consuming the preselected target and refusing a source mismatch.
- Operation-bound compaction staging, the three journaled replacement evidence steps, and the exact
  replacement-ambiguity proof.
- Factory ordinary-row deletion preserving and rereading the exact launch row, with the in-transaction
  heartbeat, lease renewal, and revision advance removed.
- The exact Covenant exclusive lease held through runtime-authority publication and the one
  disposition, spent through a post-disposition finalizer.
- The six-step reconciliation suffix, journal retirement, and admission reopen, in the §4.3 order.
- Removal of the V3 maintenance adapter, its DI registrations, and its acquisition-inventory entries.
- Fault injection before and after every effect, proof, and publication boundary.

### 2.2 Excluded

- Any second phase vocabulary. `CovenantResetPhase` keeps its exact ten literal codes; no member is
  added, renamed, or renumbered, and no parallel enum declares the same name set.
- Any new `GrimoireOfflineTransitionState`, terminal intent, reconciliation step, handler outcome, or
  transition kind. The graph #244 closed stays closed.
- Any change to Covenant eligibility, disclosure accounting, effect-digest domains, protected erasure
  scope, the preservation set, or the route and result contracts frozen by #128.
- Any new payload version. Every payload member #248 needs already exists on the two V1 payloads.

## 3. Authority order

For both kinds, and for both first entry and recovery, authority is acquired in exactly this order,
and released in reverse:

1. the held installation maintenance lock, borrowed and never reacquired or disposed;
2. the validated database launch binding;
3. the exact Covenant exclusive lease;
4. the verified journal publication; then
5. the Grimoire closing and then closed owner.

A journal publication never mints a Covenant lease, and a Covenant lease never authorizes a
publication. `GateAdmission` — whose private constructor is today the structural proof that no gate is
acquired before a checkpoint commits — is extended to carry the typed publication, so the same
structural argument now covers both halves: the closing owner cannot be constructed without a
committed V4/V2 launch **and** a verified opening journal revision.

## 4. Launch binding and the preselected target

### 4.1 Reading the source

`CovenantErasureInventorySource` gains one reader that returns the complete source tuple —
`DatasetGeneration`, `AcceleratorEpoch`, `KeyReclamationEpoch`, `EnvelopeKeyEpoch` — in one statement
through its existing owned-snapshot helper, so no new acquisition construct appears. The factory arm
reads it under the still-live installation read lease, between the healthy-catalog proof and the
lease revalidation, so the tuple cannot move between the read and the commit.

A zero epoch, an epoch already at the ceiling its update trigger refuses to move past, an absent
singleton, and a malformed generation each refuse with `Covenant.IntegrityFailure` before anything is
committed.

### 4.2 Preselecting the target

The target dataset generation is a fresh cryptographically random value, refused if it equals the
source. Each target epoch is the successor of its own source epoch, compared member by member rather
than as a set — the rule #247 already enforces, restated here because this child is the first producer.

The preselection happens once, before the launch row commits, and never again. A resumed transition
reads its target from the journal, never recomputes it.

### 4.3 Committing the launch

`CovenantResetCheckpointInitiator` commits `CovenantOfflineTransitionLaunchV4` for a direct reset and
`DataRetentionFactoryTransitionLaunchV2` for a healthy-catalog factory erasure, each at
`InventoryPrepared`, each carrying the operation identity, ledger kind, recovery policy, canonical
effect digest, source tuple, preselected target tuple, and the row revision observed immediately
before the commit.

`StartingRevision` is read from a fresh row read taken immediately before the commit rather than from
the possibly stale instance the caller passed in. The revision the journal binds itself to is reread
after the commit and must be strictly past the one the launch recorded.

## 5. Journal-driven phases

### 5.1 The publication protocol

Every phase costs exactly two journal revisions, in this order: publish the in-flight phase with its
typed before-state evidence; perform the effect; prove whether the exact effect completed; publish the
completed phase. A crash before the first publication means the effect may not have begun. A crash
after it never permits a blind assumption — the phase's own resolver classifies the observed state.

Closing evidence advances monotonically as the gate stages complete, and the closed generation
recorded is the launch's exact source generation; any other value authorizes nothing.

### 5.2 Canonical transaction ambiguity

The canonical transaction accepts the journaled target rather than generating one, and its
`UPDATE covenant_state` carries the full source tuple in its `WHERE` clause, so a mismatch affects zero
rows and refuses rather than stamping a target over a database nobody established the state of. It
reads the resulting generation back inside the same transaction and returns it, so the caller has a
read-back proof rather than a tautology.

Recovery classifies the observed state through the three-answer classifier #247 delivered: the exact
source tuple is exactly-not-applied and may retry against the already-journaled target; the exact
target tuple, together with empty-family, preserved-authority, cursor and integrity proof, is
exactly-applied; everything else is ambiguous and parks `KeepClosed`.

The factory reseed arm — which inserts a canonical singleton with epoch literals when none exists —
refuses under an offline transition. A launch requires every source epoch above zero, so a launch
structurally cannot describe a database with no singleton, and an arm that could run anyway would
stamp epochs no launch committed to.

### 5.3 Compaction and replacement

An offline transition allocates a staging leaf bound to its operation and slot epoch, valid under the
journal's own strict leaf predicate, and never uses the fixed shared staging path. The three
replacement evidence steps are journaled in the order the lifecycle validator already enforces: the
base identities before creation, the staging physical identity after secure creation, and the staged
content digest after export proof — only then may the compaction phase begin in flight.

Recovery removes only staging owned by this operation. A changed destination is accepted only when its
physical and content identity is the exact journaled staged replacement **and** a SQLCipher open proves
integrity, compactness, empty-family state, preserved authority, original-backup identity, the
journaled target generation, and all three target epochs. A replaced-but-unverified outcome, a
substituted destination, a missing required original, a wrong target tuple, and an unrelated staging
artifact each park `KeepClosed` rather than rerunning replacement.

### 5.4 Factory ordinary-row deletion

The ordinary-row deletion transaction no longer renews a lease, writes a heartbeat, or advances the
operation revision. It instead preserves the exact launch row and its request and effect binding, and
rereads both inside the same transaction: the surviving row must carry the exact launch digest, the
exact checkpoint version and reference, and the exact revision the journal bound itself to. A mismatch
refuses; nothing repairs.

The "did the ordinary continuation run" fact moves out of a phase-window comparison and into the
journal's own one-way sub-state, advanced in a single evidence-only publication at its exact boundary.

### 5.5 Runtime authority

After the candidate is verified through the private maintenance lane and while ordinary admission is
still closed, the handler publishes the runtime-authority evidence, invokes the existing committed
transition publication, and publishes its completion. The three verification booleans — lane opened,
candidate verified, runtime authority verified — are the in-flight and completed protocol for this
phase; the parent's "journals an in-flight runtime-authority phase" is satisfied by the monotone
advance rather than by a new evidence shape, because a new shape would require a new payload version
and reopen a closed graph for no additional proof.

This phase changes no persisted authority epoch and may not expose ordinary work until its live proof
is complete.

## 6. Maintenance acquisition

The journal-era maintenance capability carries owner, generation, path, mode, purpose, and lane, as
the parent's §6.4 already requires. Today's factory hard-codes the canonical path and a read-write
mode because only one purpose existed; this child makes path and mode properties of the issued
capability, derived by the gate from the canonical path and — for staging — the journaled leaf. A
caller never supplies a path, a connection string, a passphrase, or a mode as a free value.

`CovenantMaintenanceConnectionPurpose` gains the two members the V3 lane had and the journal era
lacked, and the gate's closed validation moves with them. Each purpose gets one narrow factory method
that binds its own path, mode, and purpose, and carries an acquisition-route marker with a
repository-unique name.

The restriction the V3 capability carried — that only a Covenant reset or a healthy-catalog factory
erasure may perform destructive maintenance — is re-imposed explicitly at the journal-driven entry
gate rather than being lost with the adapter.

Every direct drain call inside the canonical transaction and the storage-health kernels is removed:
stage two of the admission gate owns the physical drain, and a kernel that drained for itself would be
proving a property about a file the gate had already proved.

## 7. Dispositions, reconciliation, and retirement

Terminal ordering is the parent's §4.3 order, unchanged: publish reopen-prepared; reopen privately
through one owner-bound unpooled lane; verify the candidate and publish runtime authority; publish
database-reconciliation-pending; perform and reread the exact terminal compare-exchange; satisfy the
parent receipt or record that none is required; close the lane and prove it empty; publish the
disposition in flight, spend and verify the one Covenant disposition, publish it complete; publish
retirement-pending; retire the journal; then reopen ordinary admission.

The disposition is spent through a post-disposition finalizer, so the journal can never retire past a
disposition that did not happen. Every post-publication exit publishes or preserves one outcome:
falling out through owner disposal is forbidden, and the three paths that can do so today each reach
an explicit `KeepClosed`.

`KeepClosed` publishes the exact phase and blocker code into the journal and writes no database status
merely to describe the failure. The held lease's one `KeepClosed` disposition is spent while the
journal is still active, before process-local ownership is released, so recovery can reconstruct fresh
closed ownership from that journal.

Every blocker recomputes its expected-state digest from the state actually observed and never copies
the stored one forward — a proof that echoes the value it is being checked against asserts equality
with itself.

## 8. Reconciliation exclusion

A live offline transition claims its operation identity in a process-local singleton for the life of
the transition, and the generic reconciler skips exactly the claimed identity. The claim is released
on every exit, including failure and `KeepClosed`. Nothing else is skipped, and no non-erasure
operation changes behavior.

## 9. TDD strategy

Every behavioral change follows an observed RED, a minimal GREEN, and a focused review. Repository-wide
qualification remains #257's.

Fault injection uses the seams #243 already established — a recording and fail-before-step file store
and anchor store, driven from a second store instance over the same durable root, recovered by a fresh
instance. The coordinator gains the same shape as a constructor-supplied seam with a production no-op,
never as an optional method parameter.

The crash matrix runs one case per boundary: before and after each effect, each proof, each journal
publication, the committed-transition publication, each reconciliation step, the Covenant disposition,
and retirement. Each case injects into a second coordinator over the same durable state and recovers
with a fresh one, and the boundary name travels as the assertion message.

Deterministic barriers, never sleeps. Tests that reach a real SQLCipher database are skippable and
join the existing serialized collections, because pool clearing is process-global.

## 10. Verification and delivery

Focused child-scoped tests through the RED/GREEN loop, then a bounded review, a warning-free Release
solution build, changed-file style verification, and a clean branch diff. Coverage, the complete
suites, Native AOT and IL verification, the benchmark gate, native SQLCipher provenance, packaging,
full-host, and cross-platform qualification remain #257's on the final reviewed SHA.

Documentation travels with the code: `docs/Arcanum.DESIGN.md` §10.20.3–§10.20.6, the status paragraph
and Covenant sections of `docs/Arcanum.Engineering.md`, `docs/Arcanum.API.md` §8.20's checkpoint
sentences, and the one `Covenant.ManualRecoveryRequired` narration cell in
`docs/Arcanum.Command.Reference.md`. `README.md` and `docs/Compendium.README.md` do not change: this
child adds no public front-page behavior and no configuration key.
