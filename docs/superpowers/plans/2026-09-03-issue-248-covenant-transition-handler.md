# Issue #248 Journal-Driven Covenant Reset and Factory Erasure Implementation Plan

**Goal:** Make direct Covenant reset and healthy-catalog factory erasure idempotent typed
offline-transition handlers whose durable phase authority lives outside the closed Grimoire.

**Architecture:** Keep the existing Covenant phase kernels as the effect bodies and keep #244's
handler as a pure codec and edge validator. Add a second closed effect-handler table keyed on the
same `(Kind, PayloadVersion)` pair, and one journal-driven coordinator that owns the drive loop:
authority acquisition, the two-revision publication protocol per phase, the reconciliation suffix,
the one disposition, and retirement. Replace the V3 maintenance adapter with gate-issued
path-and-mode-bound capabilities.

**Tech Stack:** .NET 10, C# 13, EF Core SQLite/SQLCipher, Microsoft.Data.Sqlite, xUnit, deterministic
`TaskCompletionSource` barriers, Git, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-03-issue-248-covenant-transition-handler-design.md`

## Global Constraints

- Work on `codex/issue-248-covenant-transition-handler`, based on `grimoire-fixes` commit `9032574f`,
  and merge only into `grimoire-fixes`.
- Follow RED → GREEN → REFACTOR in each task. Ordering tests use barriers and manual time, never
  sleeps. SQLite pool tests stay in the existing serialized collections.
- Add no `CovenantResetPhase` member and no second enum declaring its name set. Add no
  `GrimoireOfflineTransitionState`, terminal intent, reconciliation step, handler outcome, or kind.
- Add no payload version. Every member needed already exists on the two V1 payloads.
- Name new coordinator and effect types `GrimoireOfflineTransition*`, never `Covenant*` — the
  unsupplied-optional-parameter gate scopes on that prefix.
- New compensation and cleanup paths use `CancellationToken.None`.
- No new `IFoo? foo = null` constructor default under `src/`.
- Preserve public `/api` and `/v1` contracts, CLI verbs, configuration, schema, and migrations.
- Preserve Native-AOT rules: every new payload, evidence, and enum type gets a `[JsonSerializable]`
  entry and `[JsonUnmappedMemberHandling(Disallow)]`.
- Zero Release build warnings. C# house style: one blank line after each line of code, file-scoped
  namespaces, positional records, primary constructors for DI.
- Run only child-scoped focused tests during development. Coverage, complete suites, Native AOT/IL,
  benchmark, native SQLCipher provenance, packaging, full-host, cross-platform, and parent-wide
  qualification remain #257's.
- Do not edit, transition, reparent, close, or make a resolution claim for issue #242.

---

### Task 1: Source tuple, preselection, and the V4/V2 launch

**Files:** `CovenantErasureInventorySource.cs`, `CovenantResetCheckpointInitiator.cs`,
`DataRetentionService.cs`, `DataRetentionService.FactoryActivation.cs`, plus their tests.

- [x] RED: a test asserting `CovenantErasureInventorySource` can read the complete source tuple, and
      refuses a zero epoch, a ceiling epoch, an absent singleton, and a malformed generation.
- [x] GREEN: add `ReadOfflineTransitionSourceStateAsync` through the existing owned-snapshot helper so
      no new acquisition construct appears.
- [x] RED: a test asserting preselection produces successor epochs member by member and refuses a
      target generation equal to the source.
- [x] GREEN: add the preselection helper to the initiator.
- [x] RED: tests asserting a direct reset commits `CovenantOfflineTransitionLaunchV4` and a factory
      apply commits `DataRetentionFactoryTransitionLaunchV2`, each at `InventoryPrepared`, each
      carrying the tuple and the pre-commit revision.
- [x] GREEN: change `PrepareAsync`'s encoder delegate to carry the launch inputs; read
      `StartingRevision` from a fresh row read; replace both encoder bodies.
- [x] GREEN: the factory arm reads the tuple under the still-live read lease.
- [x] Update the reread projections in both service call sites to decode the launch.

### Task 2: Remove the V3 and V1 checkpoint shapes and open the window

**Files:** `CovenantRecoveryCheckpoints.cs`, `LongRunningOperationRecoveryRegistry.cs`, both recovery
handlers, `CovenantErasureStartupRecoveryOwnerAdopter.cs`, `CovenantErasureCoordinator.cs`,
`CovenantPublicContractInventory.cs`, `DataRetentionService.cs`, `DataRetentionService.FactoryReset.cs`.

- [x] RED: a test asserting a row at the removed versions is refused content-free rather than throwing.
- [x] GREEN: delete `DataRetentionMutationCheckpointV3` and `DataRetentionFactoryResetCheckpointV1`,
      their encode and decode arms, and their serialization registrations. Keep the ordinary
      non-Covenant arms and `MinCheckpointVersion: 0`.
- [x] GREEN: remove their declarations from the public contract inventory and the durable-shape list.
- [x] GREEN: window to 4 and 2; both handlers' supported version to match; the adopter recognizes the
      new versions; the coordinator's exact-checkpoint check moves with them.
- [x] Update the four pinned window assertions and the adopter's seeded rows.

### Task 3: Effect-handler registry and the journal-driven coordinator skeleton

**Files:** new `GrimoireOfflineTransitionEffectHandlerRegistry.cs`,
`GrimoireOfflineTransitionCoordinator.cs`, `GrimoireOfflineTransitionLifecycleStore.cs`,
`ServiceCollectionExtensions.cs`.

- [x] RED: a test asserting the effect registry is closed over exactly the two current kind/version
      pairs and refuses duplicates, zero versions, and unregistered keys.
- [x] GREEN: add the registry and the two effect handlers. One handler class registered twice rather
      than two classes: the kinds differ in configuration, not behaviour. Rather than delegating to the
      kernels, the handler owns the two facts that actually vary by kind — which durable operation the
      kind is, and whether it owes the ordinary factory continuation.
- [x] RED: a test asserting a typed bound-begin builds the payload with the epoch the store allocates.
- [x] GREEN: add `BeginBoundAsync` to the lifecycle store, keeping `BeginAsync` unchanged.
- [x] GREEN: register the journal store, both registries, the lifecycle store, the reconciler, and the
      coordinator; extend the architecture-boundary registration inventory.
- [x] GREEN: borrow the held maintenance lock in both service paths and seed the profile identity once.

### Task 4: Extend `GateAdmission` to carry the verified publication

**Files:** `CovenantResetCheckpointInitiator.cs`, tests.

- [x] RED: a test asserting the closing owner cannot be constructed without both a committed launch and
      a verified opening journal revision.
- [x] GREEN: extend the nested private-constructor type to carry the typed publication.

### Task 5: Journal-era maintenance capabilities and the drain move

**Files:** `GrimoireConnectionAdmissionContracts.cs`, `GrimoireConnectionAdmissionGate.cs`,
`GrimoireMaintenanceConnectionFactory.cs`, `CovenantCanonicalErasureTransaction.cs`,
`CovenantLocalErasureStorageHealth.cs`, the acquisition inventory and its tests.

- [x] RED: tests asserting each purpose issues a capability bound to its own path, mode, and purpose,
      and that a caller cannot supply a path.
- [x] GREEN: add the two missing purposes and the gate validation; make path and mode capability
      properties derived by the gate; add one narrow factory method per purpose with a
      repository-unique acquisition-route marker.
- [x] GREEN: re-impose the operation restriction at the journal-driven entry gate, through the effect
      table: the journal names a kind, the table names that kind's operation, and a run claiming a
      different one is refused before the ladder starts.
- [x] GREEN: delete the direct drain calls from the canonical transaction and the storage kernels.
- [x] Rewrite the journal-maintenance contract inventory test to name the exact new methods and call
      sites, and recompute the expected acquisition count.

### Task 6: Canonical target binding

**Files:** `CovenantCanonicalErasureTransaction.cs`, `CovenantErasureTransition.cs`,
`CovenantErasureCoordinator.cs`, `CovenantLocalErasureStorageHealth.cs`, tests.

- [x] RED: a test asserting the transaction stamps the journaled target rather than minting one.
- [x] RED: a test asserting a source mismatch affects zero rows and refuses.
- [x] GREEN: accept the target, parameterize the epochs, extend the `WHERE` clause with the full source
      tuple, read the generation back inside the transaction, refuse on zero rows.
- [x] RED: a test asserting the reseed arm refuses under an offline transition.
- [x] GREEN: refuse it.
- [x] GREEN: add the missing epoch to the verified candidate state, its SELECT, and its shape validation.
- [x] RED/GREEN: the three-answer classifier drives the recovery arm — retry, accept, or park.

### Task 7: Two-revision phase publication and the closed-period write removal

**Files:** `GrimoireOfflineTransitionCoordinator.cs`, `CovenantErasureCoordinator.cs`,
`DataRetentionService*.cs`, new digest calculators, tests.

- [x] RED: a test asserting each phase publishes in-flight with before-state evidence, then completed.
- [x] GREEN: the drive loop; the evidence-digest calculators following the two existing house patterns.
- [x] RED: a test asserting no checkpoint, heartbeat, lease renewal, or reconciliation-required write
      happens between the first journal publication and the terminal reread.
- [x] GREEN: delete the in-closure checkpoint writer, the reopened-verified checkpoint, the encoder,
      and the lifecycle-failure transition; unwrap the lease maintainer from the offline segment.
- [x] GREEN: publish closing evidence monotonically with the launch's exact source generation.

### Task 8: Reconciliation exclusion

**Files:** new ownership singleton, `LongRunningOperationReconciler.cs`, `ServiceCollectionExtensions.cs`.

- [x] RED: a test asserting the generic reconciler skips exactly a claimed operation and nothing else.
- [x] GREEN: the process-local claim, released on every exit including failure and parked outcomes.

### Task 9: Operation-bound staging and replacement ambiguity

**Files:** `CovenantResidualArtifacts.cs`, `CovenantLocalErasureStorageHealth.cs`, coordinator, tests.

- [x] RED: a test asserting the leaf is operation-bound and valid under the journal's leaf predicate.
- [~] RED: a test asserting recovery removes only this operation's staging. **Not taken.** The sweep
      stays class-wide, and deliberately: the installation lock admits one process and the admission
      gate admits one closed period within it, so an export staging file that is not the caller's is
      abandoned litter — and litter that is a complete encrypted copy of the database being erased is
      the last thing a privacy erasure may leave behind. Narrowing the sweep would have preserved it.
      The leaf's purpose is to let a resumed run ask whether the candidate on disk is its own, which
      the journal's recorded identity settles; it was never to protect the file from the sweep.
- [x] GREEN: the leaf minter (gate-derived from the closing owner, never caller-supplied) and the
      ownership predicate.
- [x] RED/GREEN: the three journaled replacement steps in the validator's exact order.
- [x] RED/GREEN: the ambiguity refusals park rather than rerun — a plan made against a different
      database, a candidate that is not the recorded file, one whose contents are not what was proven,
      one that is gone from a destination that does not carry it, and a replacement that reached its
      phase without the evidence to install it.

### Task 10: Factory ordinary-row preservation

**Files:** `DataRetentionService.FactoryReset.cs`, coordinator, tests, acquisition inventory.

- [x] RED: a test asserting the deletion transaction writes no heartbeat, renews no lease, and advances
      no revision.
- [x] RED: a test asserting the surviving row is proved to be the exact launch and refuses on mismatch.
- [x] GREEN: delete the in-transaction renewal block; add the preserve-and-reread; strengthen the
      reconcile proof; update the inventory fingerprint for the changed signature.
- [x] RED/GREEN: the continuation flag moves from the phase window into the journal's one-way sub-state.

### Task 11: Runtime authority, dispositions, suffix, retirement

**Files:** coordinator, `CovenantExclusiveDisposition.cs`, `CovenantErasureCoordinator.cs`, tests.

- [x] RED: a test asserting runtime authority is published while admission is still closed.
- [x] RED: tests asserting the six suffix steps publish in order, one revision each.
- [x] RED: a test asserting the journal cannot retire past a disposition that did not happen.
- [x] GREEN: the suffix loop, the post-disposition finalizer, the parent-receipt-not-required branch.
- [x] RED/GREEN: the three no-disposition escapes each reach an explicit parked outcome.
- [x] RED/GREEN: a parked outcome writes no database status and spends its disposition while the
      journal is still active.

### Task 12: Fault injection matrix

**Files:** coordinator, new crash-matrix test.

- [x] GREEN: the constructor-supplied fail-before-step seam with a production no-op.
- [x] RED/GREEN: one case per boundary, injected into a second coordinator over the same durable state
      and recovered with a fresh one, boundary name as the assertion message.

### Task 13: Remove the V3 maintenance adapter

**Files:** `CovenantV3MaintenanceConnectionFactory.cs`, `CovenantV3MaintenanceCapability.cs`,
`ServiceCollectionExtensions.cs`, the acquisition inventory and its tests, architecture-boundary tests.

- [x] GREEN: delete the adapter types and their three registrations; move both kernel constructors onto
      the journal-era factory.
- [x] GREEN: delete the catalog entries, the helper, the validation arms, and the two enum members;
      retire the assertions that require them to be non-empty.
- [x] GREEN: recompute the expected acquisition count exactly.

### Task 14: Documentation

- [x] `docs/Arcanum.DESIGN.md` §10.20.3–§10.20.6, in the document's own voice, naming no tracker issue.
- [x] `docs/Arcanum.Engineering.md` status paragraph and the Covenant reset sentences; do not rename the
      provider-retention heading.
- [x] `docs/Arcanum.API.md` §8.20 checkpoint sentences only.
- [x] `docs/Arcanum.Command.Reference.md` the one narration cell; no first cell, no heading.
- [x] `README.md` and `docs/Compendium.README.md` unchanged.

### Task 15: Review, integrate, deliver

- [ ] Bounded review of the whole child diff.
- [ ] Warning-free Release solution build; changed-file style verification; clean branch diff.
- [ ] Child-scoped focused suites green.
- [ ] Merge into `grimoire-fixes`, push, delete the child branch, close #248.
