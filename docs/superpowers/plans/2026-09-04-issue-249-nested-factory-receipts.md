# Issue #249 Nested Factory Erasure and Installation-Reset Receipts Implementation Plan

**Goal:** Make a healthy-catalog factory erasure launched by a full installation reset carry an exact
parent receipt in the outer record, resolve the two records as a pair at startup, and remove the
transition slot's two credentials only in final credential cleanup after exact proof.

**Architecture:** Fill the parent-receipt seam #244 shaped and #248 left null. The outer active record
gains a typed nested receipt at payload version 3; a resolver reads that record under the held
maintenance lock and returns a bound sink, no parent, or a refusal, so first entry and recovery take
the same path; the reconciliation suffix publishes and rereads the receipt and records a digest
recomputed from the reread; a pure resolver interprets the two records as the eight-arm matrix and the
host runs it before bootstrap; and the existing ordered restore-credential cleanup gains two
proof-gated compare-removal phases and a new terminal.

**Tech Stack:** .NET 10, C# 13, EF Core SQLite/SQLCipher, Microsoft.Data.Sqlite, xUnit, deterministic
`TaskCompletionSource` barriers, Git, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-04-issue-249-nested-factory-receipts-design.md`

## Global Constraints

- Work on `codex/issue-249-nested-factory-receipts`, based on `grimoire-fixes` commit `d2bac882`,
  and merge only into `grimoire-fixes`.
- Follow RED → GREEN → REFACTOR in each task. Ordering tests use barriers and manual time, never
  sleeps. SQLite pool tests stay in the existing serialized collections.
- Add no `GrimoireOfflineTransitionState`, terminal intent, reconciliation step, handler outcome, or
  transition kind. Add no `CovenantResetPhase` member. Add no offline-transition payload version.
- Codes 1–4 of `InstallationResetRestoreCredentialCleanupPhase` keep their exact numeric values.
- Name new transition-side types `GrimoireOfflineTransition*`, never `Covenant*`; name new
  installation-reset-side types `InstallationReset*` or `FullInstallationReset*`.
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
- Tracker issue numbers may appear only in `docs/superpowers/**`, `README.md`,
  `docs/Arcanum.Engineering.md`, and `docs/Arcanum.OATH.md`.

---

### Task 1: The nested receipt shape and payload version 3

**Files:** `InstallationResetActivePersistence.cs`, `InstallationResetActiveStore.cs`,
`InstallationResetActiveRecordAuthenticator.cs`, plus their tests.

- [x] RED: a test asserting `InstallationResetNestedTransitionReceiptV1` round-trips through the
      source-generated context, refuses an unmapped member, refuses `Version != 1`, refuses an empty
      nested operation id, refuses digests present at `Claimed`, and refuses digests absent at
      `Completed`.
- [x] GREEN: add `InstallationResetNestedTransitionPhase` and the receipt record; register both on
      `InstallationResetActiveJsonContext`.
- [x] RED: a test asserting an authenticated record at payload version 3 round-trips and that a
      version-2 payload decodes as a strict legacy read and re-seals as 3 before its next effect.
- [x] GREEN: add `InstallationResetActivePayloadV3` carrying the receipt and split `PayloadVersion` 3
      from `LegacyPayloadVersion` 2. **Changed during implementation.** The plan said to move
      `EnvelopeVersion` to 3 with new associated-data and digest domains. The envelope format did not
      change — only the plaintext inside it did — so bumping it would have retired a byte-exact
      authenticated shape that is still correct, and forced a second AAD domain to live alongside the
      first for no fact either could carry. The payload version alone moves, and it is authenticated
      by being inside the AEAD ciphertext.
- [x] GREEN: every new nullable member is omitted from the wire when null, because the authenticated
      open re-serializes the plaintext byte for byte and a member that always appeared would refuse
      every record sealed before it existed.
- [x] GREEN: thread the receipt through `FromRecord`, `ToRecord`, a `CopyNestedTransitionReceipt`
      helper, and `InstallationResetActiveRecord`.
- [x] GREEN: add the `ValidatePayload` arm for the receipt.
- [x] RED: a test asserting `IsMonotonicTransition` refuses removing, regressing, or substituting the
      receipt, and `SamePayload` compares it.
- [x] GREEN: add both rules.
- [x] `git add` the changed source and test files; `git commit -m "feat: carry a nested transition receipt on the installation-reset record"`.

---

### Task 2: The claim published before the nested apply

**Files:** `InstallationResetService.cs`, `InstallationResetActiveStore.cs`, plus their tests.

- [x] RED: a test asserting the offline healthy-catalog arm publishes a `Claimed` receipt with a
      stable nested operation id before it calls the data service, and reuses the same id on a
      resumed attempt rather than minting a second one.
- [x] GREEN: mint the nested operation id, publish the claim, and pass it as
      `DataRetentionApplyRequest.RequestedOperationId` so the offline arm stops launching anonymously.
- [x] RED: a test asserting a workspace-scope reset publishes no claim.
- [x] GREEN: gate the claim on the healthy-catalog factory arm.
- [x] `git add` the changed files; `git commit -m "feat: claim the nested factory erasure before it starts"`.

---

### Task 3: The binding digest, the resolver, and the sink

**Files:** new `GrimoireOfflineTransitionParentReceipt.cs`, `GrimoireOfflineTransitionPhaseAuthority.cs`,
`ServiceCollectionExtensions.cs`, plus their tests.

- [x] RED: a test asserting the binding digest is domain-separated, covers the four members in order,
      and changes when any one of them changes.
- [x] GREEN: add the digest helper.
- [x] RED: tests asserting the resolver answers no-parent for an absent outer record and for one with
      no receipt, a bound sink for a matching claim, and a content-free refusal for a mismatched
      operation, a `CovenantReset` kind, a mismatched effect digest, and an unauthenticatable record.
- [x] GREEN: add `IGrimoireOfflineTransitionParentReceiptResolver`, its production implementation over
      the active store, and the typed sink; register both.
- [x] RED: a test asserting `OpeningPayload` mints a non-null `ParentReceiptBindingDigest` for a
      parent-bound healthy-catalog erasure and null otherwise.
- [x] GREEN: give the phase authority the resolver and replace the literal null.
- [x] `git add` the changed files; `git commit -m "feat: resolve a parent-receipt sink from the outer record"`.

---

### Task 4: Publish, reread, and stop echoing the binding

**Files:** `GrimoireOfflineTransitionPhaseSession.cs`, `CovenantErasureCoordinator.cs`, plus tests.

- [x] RED: a test asserting `RecordParentReceiptAsync` refuses a null digest when the binding is
      non-null, refuses a non-null digest when the binding is absent, and records the supplied digest
      rather than the binding.
- [x] GREEN: change the signature to take the recomputed digest and drop the copy-forward.
- [x] RED: a test asserting the suffix publishes the `Completed` receipt after the terminal winner
      reread and before `ParentReceiptSatisfied`, and that a replay rereads without advancing the
      outer envelope revision a second time.
- [x] GREEN: add the publish-and-reread to the suffix through the sink.
- [x] RED: a test asserting a recomputed digest that does not equal the binding parks `KeepClosed` at
      the exact step and repeats no effect.
- [x] GREEN: add the park arm.
- [x] `git add` the changed files; `git commit -m "feat: publish and reread the exact outer completion receipt"`.

---

### Task 5: The eight-arm evidence matrix

**Files:** new `InstallationResetNestedTransitionEvidence.cs`, plus its tests.

- [x] RED: a table test over all eight arms plus the cross-record terminal-winner disagreement,
      asserting the exact outcome for each and one content-free refusal for every fail-closed arm.
- [x] GREEN: add the pure resolver and its outcome enum.
- [x] `git add` the changed files; `git commit -m "feat: resolve installation-reset and journal evidence as one pair"`.

---

### Task 6: Run the matrix before bootstrap

**Files:** `IInstallationResetStartupRecovery.cs`, `GrimoireDatabaseHostedService.cs`, plus tests.

- [x] RED: a test asserting `RecoverBeforeBootstrapAsync` recovers the journal under the same held
      lock and returns the matrix outcome.
- [x] GREEN: give the startup recovery the journal store and run the matrix.
- [x] RED: a test asserting the host refuses to bootstrap on each fail-closed arm and keeps readiness
      closed on each active-journal arm.
- [x] GREEN: act on the outcome before `GrimoireDatabaseBootstrapper.EnsureInitializedAsync`.
- [x] `git add` the changed files; `git commit -m "feat: resolve the record pair before database bootstrap"`.

---

### Task 7: The transition-slot terminal proof

**Files:** `GrimoireOfflineTransitionJournalAnchorStore.cs`, new
`GrimoireOfflineTransitionJournalAnchorStore.FullResetTerminal.cs`, plus tests.

- [x] RED: tests asserting a projection is produced for `NeverTransitionedAbsence` and `ClosedAnchor`,
      and refused for an `Active` anchor, a present file, a key with no anchor, and a `Claimed`
      receipt.
- [x] GREEN: add `GrimoireOfflineTransitionFullResetTerminalProjectionV1` and the proof.
- [x] `git add` the changed files; `git commit -m "feat: prove the transition slot terminal before removal"`.

---

### Task 8: Removal surfaces and the extended ordered cleanup

**Files:** `GrimoireOfflineTransitionJournalKeyProvider.cs`,
`GrimoireOfflineTransitionJournalAnchorStore.cs`, `InstallationResetRestoreCredentialCleanup.cs`,
`FullInstallationResetMarkerPairResetContracts.cs`, `FullInstallationResetTerminalContinuation.cs`,
plus tests.

- [x] RED: tests asserting each removal surface compare-removes against an expected value, refuses a
      changed value, and reads an already-absent account as absent.
- [x] GREEN: add `RemoveAndVerifyAbsent` to the key provider and the anchor store.
- [x] RED: tests asserting the extended phase order, that codes 1–4 are unchanged, that the terminal
      is `TransitionCredentialsVerifiedAbsent`, and that a record resumed at 4 finishes the pair.
- [x] GREEN: extend the phase enum, `OrderedSteps`, the checkpoint tail, and the continuation's
      short-circuit.
- [x] RED: a test asserting the ordinary catalog filter still excludes both accounts.
- [x] GREEN: leave the filter untouched and amend the closed deletion inventory to name exactly the
      one new production source.
- [x] `git add` the changed files; `git commit -m "feat: remove the transition pair in final credential cleanup"`.

---

### Task 9: Fault injection at every new boundary

**Files:** the test projects only.

- [x] RED/GREEN: one case per boundary listed in spec §7, each injecting into a second coordinator or
      continuation over the same durable state and recovering with a fresh one, with the boundary name
      as the assertion message.
- [x] `git add` the changed test files; `git commit -m "test: cover every new receipt and cleanup crash boundary"`.

---

### Task 10: Documentation

**Files:** `docs/Arcanum.DESIGN.md`, `docs/Arcanum.Engineering.md`, `docs/Arcanum.API.md`,
`docs/Arcanum.Command.Reference.md`, `docs/Arcanum.OATH.md`, `docs/ArcanumOATH.Human.md`,
`docs/Arcanum.Design.Human.md`, `docs/Arcanum.DEBUGGING.Human.md`.

- [x] Update every section named in spec §9, in the owning document's own voice and without tracker
      issue numbers where they are forbidden.
- [x] Confirm `README.md` and `docs/Compendium.README.md` need no change, and that no setting
      descriptor was added.
- [x] `git add` the changed docs; `git commit -m "docs: record the nested receipt, evidence matrix, and cleanup order"`.

---

### Task 11: Review, build, and merge

- [x] Bounded read-only review of the whole branch diff; fix Critical and Important findings through a
      focused RED/GREEN loop. Five landed: the new checkpoint tail had to be omitted from the wire
      when null or every record sealed before it would fail its authenticated open; the pair compared
      the receipt's replay key against the journal's ledger operation id, which never match; the
      crossing window between publishing the receipt and recording the journal step was read as a
      disagreement; a reset that claimed nothing beside an unbound journal was refused rather than
      read as standalone work; and the resolver could not reach the active store in the composition
      that registers the phase authority. A sixth fix stops an apply failure retiring a record whose
      claim has not reported, which would strand the journal bound to it.
- [x] `dotnet build RetroDownfall.Arcanum.slnx -c Release` with zero warnings.
- [x] `./scripts/align-csharp-blanklines.sh --check` over the changed files; `git diff --check`.
- [x] Merge into `grimoire-fixes`, push, delete the child branch, and mark issue #249 done.
