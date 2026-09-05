# Issue #250 Pre-Readiness Offline-Transition Recovery Implementation Plan

**Goal:** Turn #249's three journal-active refusals into a resumption that runs under the held
installation maintenance lock before database bootstrap, and resolve the launch-gap crash before
readiness rather than after it.

**Architecture:** A validate-only unlock opens the existing SQLCipher catalog; a read-only authority
bootstrapper loads the minimum persisted Covenant facts over it and verifies them against the
authenticated journal; a one-use handoff initializes the operation gate into a closed recovery
posture and adopts the durable owner; a lease adoption that asserts the held lock makes this process
the row's owner; and the registered typed recovery handler is dispatched through the generic
reconciler's own per-operation settle. The same settle, under the same adoption, resumes one exact
adopted launch-gap row inside the bootstrap before readiness is marked.

**Tech Stack:** .NET 10, C# 13, EF Core SQLite/SQLCipher, Microsoft.Data.Sqlite, xUnit, deterministic
`TaskCompletionSource` barriers, Git, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-05-issue-250-pre-readiness-transition-recovery-design.md`

## Global Constraints

- Work on `codex/issue-250-pre-readiness-transition-recovery`, based on `grimoire-fixes` commit
  `7bc235a2`, and merge only into `grimoire-fixes`.
- Follow RED → GREEN → REFACTOR in each task. Ordering tests use barriers and manual time, never
  sleeps. SQLite pool tests stay in the existing serialized collections.
- Add no `GrimoireOfflineTransitionState`, terminal intent, reconciliation step, handler outcome, or
  transition kind. Add no `CovenantResetPhase` member. Add no offline-transition or installation-reset
  payload version. Add no `CovenantErasureFaultBoundary` member.
- Do not change `LongRunningOperationReconciler.ReconcileAsync`'s discovery predicate, ordering,
  paging, concurrency, budget, or per-operation protocol. The exact-operation entry point reuses the
  extracted body; it does not alter it.
- Name new transition-side types `GrimoireOfflineTransition*` or `Grimoire*`; name new Covenant-side
  types `Covenant*`. `Ward` is taken by the tool-call audit engine and may not be reused.
- New compensation and cleanup paths use `CancellationToken.None`.
- No new `IFoo? foo = null` constructor default under `src/`.
- Preserve public `/api` and `/v1` contracts, CLI verbs, configuration, schema, and migrations.
- Preserve Native-AOT rules: every new payload type gets a `[JsonSerializable]` entry and
  `[JsonUnmappedMemberHandling(Disallow)]`. No reflection-based `JsonSerializer`.
- Zero Release build warnings. C# house style: one blank line after each line of code, file-scoped
  namespaces, positional records, primary constructors for DI.
- Run only child-scoped focused tests during development. Coverage, complete suites, Native AOT/IL,
  benchmark, native SQLCipher provenance, packaging, full-host, cross-platform, and parent-wide
  qualification remain #257's.
- Do not edit, transition, reparent, close, or make a resolution claim for issue #242.
- Tracker issue numbers may appear only in `docs/superpowers/**`, `README.md`,
  `docs/Arcanum.Engineering.md`, and `docs/Arcanum.OATH.md`.

---

### Task 1: The recovery-only unlock

**Files:** new `Infrastructure/Hosting/GrimoireRecoveryOnlyUnlock.cs`, plus its tests.

- [x] RED: tests asserting the unlock refuses an absent database file, an existing database with no
      KDF sidecar, a database with a *pending* KDF sidecar, an unreadable sidecar, and a passphrase
      that does not open the catalog — each with the content-free `Covenant.ManualRecoveryRequired`
      refusal and no file created.
- [x] GREEN: add `IGrimoireRecoveryOnlyUnlock`, `GrimoireRecoveryOnlyUnlock`, and the
      `GrimoireRecoveryUnlockedCatalog` async-disposable handle. Resolve the passphrase from the
      existing sidecar and active secret only. Open with `SqliteOpenMode.ReadWrite`, `Pooling = false`,
      initialize through `CovenantSqliteConnectionMode.ReadOnly`, prove `SELECT 1`.
- [x] RED: a test asserting a successful unlock publishes the derived passphrase to
      `IGrimoireDbPassphraseSource`, and that disposal physically closes and clears the pools.
- [x] GREEN: publish the passphrase and implement disposal.
- [x] RED: a test asserting the unlock asserts the held installation lock for the guarded root and
      refuses a lock held for another root.
- [x] GREEN: assert the lock.
- [x] `git add` and `git commit -m "feat: open an existing catalog for recovery and nothing else"`.

---

### Task 2: The authority bootstrapper and its one-use handoff

**Files:** new `Infrastructure/Security/CovenantRecoveryAuthorityBootstrapper.cs`, plus its tests.

- [x] RED: a test asserting the bootstrapper reads the authority row, envelope state, persisted
      availability, and the named launch row over an unlocked catalog and writes nothing (proved by a
      before/after row-and-revision comparison).
- [x] GREEN: add `CovenantRecoveryAuthorityBootstrapper.LoadAsync` over the four existing projections.
- [x] RED: a table asserting all six verification refusals — a missing or wrong-version launch row, a
      launch binding digest that is not the journal's, an effect digest that is not the journal's, an
      exclusive operation the effect-handler registry does not allow for the journal's kind, a row
      revision behind the launch's starting revision, and a persisted dataset generation that is
      neither the journal's source nor its target.
- [x] GREEN: add the verification.
- [x] RED: a test asserting an unpermitted host-tools runtime policy refuses rather than warns.
- [x] GREEN: add the gate.
- [x] RED: tests asserting `CovenantClosedRecoveryHandoff.ConsumeAsync` initializes the runtime
      provider and adopts the durable recovery owner, refuses a second consumption, refuses a
      different guarded root, refuses a journal revision that has moved, publishes no readiness, and
      leaves ordinary lease acquisition refused afterwards.
- [x] GREEN: add the handoff with its `Interlocked` one-use claim.
- [x] `git add` and `git commit -m "feat: verify the persisted authority against the journal that names it"`.

---

### Task 3: Lease adoption under the installation lock

**Files:** `Infrastructure/Data/LongRunningOperationStore.cs`, new
`Infrastructure/Operations/ILongRunningOperationMaintenanceLeaseAdoption.cs`, plus tests.

- [x] RED: a test asserting the ordinary `TryAcquireLeaseAsync` still refuses an unexpired lease, and
      that the new adoption takes it — same row, same state predicate, one revision advance.
- [x] GREEN: extract the shared UPDATE, add the adoption with the expiry predicate omitted, and expose
      it through the narrow Infrastructure interface implemented by the concrete store.
- [x] RED: a test asserting the adoption asserts the held installation lock and refuses without one.
- [x] GREEN: assert the lock before the write.
- [x] RED: a test asserting a terminal row is still not adoptable, and that the admitted
      `ReconciliationRequired` kinds and terminal codes are exactly the ordinary path's.
- [x] GREEN: share the predicate rather than restating it.
- [x] `git add` and `git commit -m "feat: adopt one operation's lease from a provably dead owner"`.

---

### Task 4: The exact-operation settle

**Files:** `Infrastructure/Operations/LongRunningOperationReconciler.cs`, plus tests.

- [x] RED: a test asserting `SettleExactlyAsync` dispatches the registered handler for one named
      operation, rereads the row, transitions it on `CancellationToken.None`, and classifies the four
      durable outcomes.
- [x] GREEN: extract the generic pass's per-operation body and add `SettleExactlyAsync` over it with
      the lease already adopted.
- [x] RED: a test asserting `SettleExactlyAsync` skips an operation this process has already claimed.
- [x] GREEN: keep the shared `ownership.IsClaimed` check in the extracted body.
- [x] RED: a characterization test asserting the generic pass's discovery, phases, paging, concurrency
      and outcomes are unchanged across the refactor.
- [x] GREEN: refactor without behavior change.
- [x] `git add` and `git commit -m "refactor: settle one named operation through the pass's own protocol"`.

---

### Task 5: The pre-bootstrap dispatcher

**Files:** new `Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionStartupRecovery.cs`, plus
tests.

- [x] RED: a test asserting `NeitherActive`, `NestedNotStarted`, and `NestedRetired` return
      `NoActiveJournal` and perform no unlock, no load, and no dispatch.
- [x] GREEN: add the dispatcher and its three-valued outcome.
- [x] RED: a table over `StandaloneTransition`, `NestedBound`, and
      `NestedReceiptStoredRetirementSuffix` asserting the exact step order — unlock, load and verify,
      physical close, consume, adopt lease, settle — with a recording double proving the order and
      proving the unlock is closed before the handler is dispatched.
- [x] GREEN: implement the order.
- [x] RED: tests asserting a refusal from any step returns the content-free
      `Covenant.ManualRecoveryRequired` failure, performs no later step, and leaves the gate with no
      adopted owner where the failure preceded consumption.
- [x] GREEN: add the short-circuits.
- [x] RED: a test asserting a handler outcome that is not terminal maps to the failure rather than to
      `Resumed`.
- [x] GREEN: map the outcomes.
- [x] `git add` and `git commit -m "feat: resume an authenticated transition before the database opens"`.

---

### Task 6: Host wiring

**Files:** `Infrastructure/Hosting/GrimoireDatabaseHostedService.cs`,
`Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`, plus tests.

- [x] RED: a test asserting a host started over an active journal now resumes it and goes on to
      bootstrap and readiness, where today it throws.
- [x] GREEN: call the dispatcher between the reset recovery and the bootstrap, delete
      `InstallationResetHostStartupAdmission.LeavesTransitionUnfinished`, and proceed on `Resumed`.
- [x] RED: a test asserting a parked journal and each fail-closed matrix arm still refuse startup with
      the same sentence, and that readiness is marked failed.
- [x] GREEN: throw on the dispatcher's failure.
- [x] RED: a registration test asserting every new component is composed exactly once with the
      expected lifetime, and that the lock-free probe path reaches none of them.
- [x] GREEN: register the components.
- [x] `git add` and `git commit -m "feat: let the host finish the transition it refused to start over"`.

---

### Task 7: Launch-gap resumption before readiness

**Files:** `Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`, new
`Infrastructure/Data/Covenant/CovenantOfflineTransitionLaunchGapResumption.cs`, plus tests.

- [x] RED: a test asserting a committed launch row with no journal is resumed to a terminal durable
      state *before* readiness is marked, rather than left for the generic pass.
- [x] GREEN: add the resumption and call it after the install connection closes and before
      `readiness.MarkReady()`.
- [x] RED: a test asserting the resumption runs only with a held installation lock and only for
      exactly one adopted owner.
- [x] GREEN: gate it on both.
- [x] RED: tests asserting a second adoptable row, a legacy row, a malformed row, and a resumption
      that does not reach a terminal state each fail readiness closed.
- [x] GREEN: add the refusals.
- [x] RED: a test asserting an ordinary retention mutation row is untouched by the resumption.
- [x] GREEN: keep the adopter's ordinary-mutation early-out authoritative.
- [x] `git add` and `git commit -m "feat: close the launch gap before the host says it is ready"`.

---

### Task 8: The acceptance table and the fresh-process proof

**Files:** test files only.

- [x] RED/GREEN: a table over the eight acceptance cases the issue names — active,
      reconciliation-pending, retirement-pending, malformed, missing, conflicting, dual-record, and
      launch-gap — asserting each resolves before readiness and each ambiguity fails closed.
- [x] RED/GREEN: a skippable serialized integration test that crashes a real encrypted Grimoire inside
      the closed period, starts a fresh process over it, and proves the pre-bootstrap path resumes the
      transition, retires the journal, reopens admission, and reaches readiness — with the old token
      families rejected and the dataset generation moved.
- [x] RED/GREEN: fault-injection cases at each boundary §8 of the spec names.
- [x] `git add` and `git commit -m "test: prove every ending of a transition that crossed a restart"`.

---

### Task 9: Documentation

**Files:** `docs/Arcanum.DESIGN.md`, `docs/Arcanum.Engineering.md`,
`docs/Arcanum.Command.Reference.md`, `docs/Arcanum.OATH.md`, `docs/ArcanumOATH.Human.md`,
`docs/Arcanum.Design.Human.md`, `docs/Arcanum.DEBUGGING.Human.md`.

- [x] DESIGN §10.20.3 and §10.20.4: the recovery-only unlock, the verified authority handoff, the
      pre-bootstrap dispatch, and the launch gap closing before readiness. §13.7: the new coverage.
- [x] Engineering: the issue #250 status paragraph, and the amendments the #249 and #244 paragraphs
      need where they say startup dispatch is a later child's work.
- [x] Command Reference: the `serve` startup-admission paragraphs, which currently say an active
      journal keeps readiness closed.
- [x] OATH §2.1 (the delivered table), §15.3, §16; and the §15.3 correction the spec §10 records.
- [x] The three human documents, in their own registers.
- [x] `git add` and `git commit -m "docs: record the recovery that runs before anything opens"`.

---

### Task 10: Verification and delivery

- [x] Warning-free Release solution build.
- [x] The complete `RetroDownfall.Arcanum.Tests` suite green (13,583 passed, 59 skipped), and
      `RetroDownfall.Compendium.Tests` green (181 passed).
- [x] Changed-file style check and a clean branch diff.
- [x] Merge `--no-ff` into `grimoire-fixes`, push, delete the feature branch, close issue #250.
