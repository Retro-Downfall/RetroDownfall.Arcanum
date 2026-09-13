# Task 7 slice 2 report

## Scope and base

- Base: `104d90f5d02fd28b466f6f86ee0c2309a859fe20`.
- Scope: real two-host authenticated V4/V2 nonterminal recovery, the startup-only provisional host-tools prerequisite, exact held-lock `ErasureIncomplete` adoption, valid fail-closed fresh-runtime tiers, and post-schema/pre-readiness disclosure-writer restoration.
- Explicitly out of scope: terminal-row suffix completion, retained-finalizer restart, Git delivery, issue closure, and Ollama.

## Implemented contract

- `GrimoireOfflineTransitionRecoveryEvidence` now binds the authenticated envelope installation UUID as well as binding, slot epoch, revision, and digest.
- Active-journal startup samples the OS marker before recovery-only SQLite open, verifies that installation UUID against the durable authority singleton after unlock, and derives a fresh provisional host-tools policy. The actual process policy remains unpublished until ordinary bootstrap and acts only as a deny veto during recovery.
- Authority Load and one-shot Consume both require the exact provisional policy; Load independently verifies the installation UUID, and Consume rechecks an actual denial immediately before publishing availability or deriving keys.
- Held-lock exact-fingerprint adoption alone admits current V4/V2 `ReconciliationRequired/Covenant.ErasureIncomplete`; ordinary, classified, discovery, unrelated, drifted, and terminal paths remain unchanged.
- Fresh runtime unavailable tiers carry `Covenant.Unavailable` without inventing schema facts.
- Authenticated coordinator commit and proved rollback keep the disclosure writer cold. An exact resumed startup verdict asks ordinary bootstrap to reopen the same singleton only after schema/identity/authority and every recovery boundary, and before either readiness signal.
- Real host-2 recovery pauses after real adoption with both gates Closed and zero ordinary effects, then converges V4 and V2 with exact retirement and host-2-local generation publication.

## TDD and mutation record

The complete chronology is in `task-7-report.md`. The production gaps were each observed as focused REDs: unpublished real host policy blocked Load; `ErasureIncomplete` blocked adoption; null initial diagnostics blocked candidate projection; and pre-schema writer reopen blocked verified recovery. Restored mutations killed provisional classification, held-lock adoption, initial diagnostics, exact identity, the post-secret real-policy veto, authenticated coordinator writer deferral, and both sides of the bootstrap writer-order window. All were restored before final verification.

Post-commit self-review tightened two existing requirements without changing production: the parked V4/V2 rows now directly prove no active parent reset publication, and the terminal V2 row re-reads and exact-matches the installation identity from `covenant_authority_state`. The focused two-host V4/V2 plus writer-failure selection passed 3/3 after those assertions were added.

## Verification

- `dotnet test ... --filter '<startup/classifier/authority/adoption/runtime selection>'`: 72/72 passed.
- `dotnet test ... --filter '<V4/V2 full-host + writer failure + bootstrap writer authorities>'`: 5/5 passed.
- `dotnet test ... --filter '<32 phase boundaries + live-host reopen>'`: 33/33 passed in 1m05s.
- `dotnet test ... --filter 'FullyQualifiedName~GrimoireConnectionAcquisitionInventoryTests'`: 35/35 passed after its exact catalog RED/correction.
- Combined DI aliases, CLI/full-host composition, connection inventory, typed dispatch, and phase authorities: 73/73 passed.
- `dotnet restore RetroDownfall.Arcanum.slnx --nologo`: restored the six missing isolated-worktree asset files; 7/13 projects were already current.
- `dotnet build RetroDownfall.Arcanum.slnx -c Release --no-restore --nologo`: succeeded with 0 warnings and 0 errors.

## Self-review

- No real process/static host-tools publication occurs during recovery, and an actual published denial cannot be bypassed.
- Both real gates are Closed before the durable row is adopted; no ordinary connection or provider/effect starts at the pause.
- V2 assertions are entry-point-specific: factory catalog/files are erased, parent binding remains null, and no parent receipt is published.
- Host-1 and host-2 generations are not compared as a shared process value; host 2 is checked only against its own captured baseline.
- The second-host test owns cleanup from task creation onward and cannot detach startup while the shared profile is disposed.
- No terminal row was made adoptable and no terminal-suffix behavior was introduced.
