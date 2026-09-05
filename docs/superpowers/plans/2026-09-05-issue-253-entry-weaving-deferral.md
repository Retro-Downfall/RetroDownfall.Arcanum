# Issue #253 Entry Weaving Work and Effect Lifetime Implementation Plan

**Goal:** Give `EntryWeavingService` one work lease per tick, held from before its DI scope exists
until after that scope has asynchronously disposed, and one atomic external-effect group spanning
the embedding provider call and every resulting write — so a tick either never begins or reaches its
complete durable disposition exactly once, and a maintenance window is reported as a deferral rather
than as a product failure logged once a second.

**Architecture:** A two-member `EntryWeavingTickOutcome` returned by `RunTickAsync`; the lease
declared before the scope so reverse-order disposal releases the scope first; the effect group
opened after the pending fetch and before `EmbedBatchAsync`, closed after the last upsert; one new
`Debug` arm in `ExecuteAsync` that falls through to the existing configured-interval delay.

**Tech Stack:** .NET 10, C# 13, `IGrimoireConnectionAdmissionGate`, xUnit,
`TaskCompletionSource` barriers, Git, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-05-issue-253-entry-weaving-deferral-design.md`

## Global Constraints

- Work on `codex/issue-253-entry-weaving-deferral`, based on `grimoire-fixes` commit `e5ff0f97`, and
  merge only into `grimoire-fixes`.
- RED → GREEN → REFACTOR per task. Barriers, never sleeps, for ordering.
- Add no route, DTO, CLI verb, configuration key, schema object, migration, `ErrorCodes` member,
  `GrimoireWorkKind` member, or `GrimoireRequestKind` member.
- Do not change the admission gate itself, the `503` refusal shapes, or stream classification.
- Do not change what Entry weaving selects, embeds, or writes; do not change its cadence or its
  idempotency model.
- Never link `IGrimoireWorkLease.MaintenanceRevocation` into a token passed to the provider, to an
  upsert, or to the fetch. Any `CreateLinkedTokenSource` combining it with the host token fails
  review.
- Register no next-open-generation waiter. §1.3 of the spec records why.
- Tracker issue numbers may appear only in `docs/superpowers/**`, `README.md`,
  `docs/Arcanum.Engineering.md`, and `docs/Arcanum.OATH.md`.
- Zero Release build warnings. C# house style: one blank line after each line of code, file-scoped
  namespaces, primary constructors for DI, XML `<remarks>` that explain *why*.
- Coverage, Native AOT/IL, benchmark, native SQLCipher provenance, packaging, full-host and
  cross-platform qualification remain #257's.

## File and responsibility map

- `src/RetroDownfall.Arcanum.Infrastructure/Hosting/EntryWeavingTickOutcome.cs` — new; the two-member
  outcome.
- `src/RetroDownfall.Arcanum.Infrastructure/Hosting/EntryWeavingService.cs` — the gate dependency,
  the lease/scope ordering, the effect group, the returned outcome, the loop's deferral arm.
- `src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs` — the accounting-scope leak on a
  throwing `BeginAsync` (spec §2.3).
- `tests/RetroDownfall.Arcanum.Tests/Weave/EntryWeavingServiceTests.cs` — extended; the gate in the
  fixture and the new admission cases.

## Tasks

### Task 1 — The outcome type

- [x] RED: a test asserting `RunTickAsync` returns `EntryWeavingTickOutcome.Woven` for an admitted
      tick does not compile / fails.
- [x] GREEN: add `EntryWeavingTickOutcome`; return `Woven` from every existing exit.
- [x] Every existing `EntryWeavingServiceTests` case still passes unchanged in meaning.

### Task 2 — One work lease per tick, released after scope disposal

- [x] RED: a tick against a gate whose admission is closed creates no scope, opens no connection,
      calls no provider, writes nothing, and returns `DeferredForMaintenance`.
- [x] RED: an admitted tick takes exactly one lease of kind `EntryWeaving`.
- [x] RED: a closure begun mid-tick does not conclude its request/work drain until the tick's scope
      has disposed.
- [x] GREEN: inject `IGrimoireConnectionAdmissionGate`; declare the lease before the scope.

### Task 3 — One atomic effect group

- [x] RED: revocation winning the frontier makes zero provider calls and zero writes and returns
      `DeferredForMaintenance`.
- [x] RED: effect start winning makes the closure wait through the provider call and every upsert.
- [x] GREEN: open the group after the fetch and before `EmbedBatchAsync`; close it after the last
      upsert.

### Task 4 — The loop arm

- [x] RED: a repeatedly deferred worker logs nothing at `Error` and ticks at the configured cadence,
      not the one-second fault cadence.
- [x] GREEN: add the `Debug` arm; fall through to the existing interval delay.
- [x] `ExecuteAsync_TickThrowsRepeatedly_BacksOffInsteadOfTightLooping` still passes, pinning the
      genuine-failure path apart from the deferral path.

### Task 5 — The accounting-scope leak

- [x] RED: a `BeginAsync` that throws leaves no undisposed scope.
- [x] GREEN: dispose the accounting scope on the throwing path.

### Task 6 — Documentation

- [x] `docs/Arcanum.DESIGN.md` §21.6, §10.20.3, §13.7.
- [x] `docs/Arcanum.Engineering.md` per-issue paragraph after #252.
- [x] `README.md` local-first bullet.

### Task 7 — Verification

- [ ] Focused `EntryWeavingServiceTests`.
- [ ] Complete `RetroDownfall.Arcanum.Tests` and `RetroDownfall.Compendium.Tests` suites.
- [ ] Warning-free Release solution build; `git diff --check`; clean tracked status.
