# Issue #254 Attachment Indexing Work, Effect, and Queue-Identity Implementation Plan

**Goal:** Give `SessionAttachmentIndexingService` one work lease per dequeued request — held from
before its DI scope exists until after every scope that request opened has asynchronously disposed,
including a genuine failure classification's — and one sequential external-effect group per embedding
batch, so that a maintenance window defers the request with its exact pending identity and attempt
intact, re-signals that exact request once after reopen without going through the deduplicating
enqueue path, and is never reported as a product failure.

**Architecture:** A two-member `SessionAttachmentIndexDisposition` promoted to the first positional
member of `SessionAttachmentIndexOutcome`; the lease taken in `ProcessOneAsync` before the `try` and
passed into `ProcessAsync` as a parameter; the effect group scoped to `FlushBatchAsync`; a loop-owned
held list carrying the exact request and the predecessor of the generation observed before its
refusal; a directly awaited next-open-generation signal that restores held requests straight to
`_channel.Writer` with `TryWrite`; the reconciliation scope given a lease of its own.

**Tech Stack:** .NET 10, C# 13, `IGrimoireConnectionAdmissionGate`, `System.Threading.Channels`,
xUnit, `TaskCompletionSource` barriers, Git, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-05-issue-254-attachment-indexing-deferral-design.md`

## Global Constraints

- Work on `codex/issue-254-attachment-indexing-deferral`, based on `grimoire-fixes` commit
  `0123c1df`, and merge only into `grimoire-fixes`.
- RED → GREEN → REFACTOR per task. Barriers, never sleeps, for ordering.
- Add no route, DTO, CLI verb, configuration key, schema object, migration, `ErrorCodes` member,
  `GrimoireWorkKind` member, `GrimoireRequestKind` member, or `SessionAttachmentIndexStatus` member.
- Do not change the admission gate's behaviour, the `503` refusal shapes, or stream classification.
  The only gate change permitted is a new test.
- Do not change what attachment indexing extracts, chunks, embeds, or writes; its eligibility rules;
  its chunk provenance; its retrieval scoping; or its bounds. #45 owns those.
- Do not change the `attachment index` recovery handler or any part of #40's recovery policy.
- Never link `IGrimoireWorkLease.MaintenanceRevocation` into a token passed to the provider, to a
  repository call, or to extraction. Any `CreateLinkedTokenSource` combining it with the host token
  fails review.
- Directly await `WaitForNextOpenGenerationAsync` on the worker loop with the host stopping token;
  create no detached waiter or continuation.
- Introduce no shared worker vocabulary, page abstraction, or watermark abstraction; #255 must stay
  free. Do not raise the five-second work-drain checkpoint; that is #256's or #257's.
- Re-signal with `TryWrite`, never `WriteAsync` — the re-signal runs on the channel's only reader.
- Tracker issue numbers may appear only in `docs/superpowers/**`, `README.md`,
  `docs/Arcanum.Engineering.md`, and `docs/Arcanum.OATH.md`.
- Zero Release build warnings. C# house style: one blank line after each line of code, file-scoped
  namespaces, primary constructors for DI, XML `<remarks>` that explain *why*.
- Coverage, Native AOT/IL, benchmark, native SQLCipher provenance, packaging, full-host and
  cross-platform qualification remain #257's.

## File and responsibility map

- `src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexDisposition.cs` — new; the
  two-member disposition.
- `src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexProcessor.cs` — the outcome
  record's new member, the lease parameter, the per-batch effect group.
- `src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexingService.cs` — the gate
  dependency, the lease/scope ordering, the conditional `_pending` release, the held list, the
  top-of-loop re-signal, the reconciliation lease, the loop's deferral arms, and the missing
  `[ExcludeFromCodeCoverage]` reason.
- `tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIndexingAdmissionTests.cs` — new; every
  admission case.
- `tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIndexingQueueTests.cs` — updated
  constructions.
- `tests/RetroDownfall.Arcanum.Tests/Weave/SessionAttachmentIndexingTests.cs` and
  `SessionAttachmentIdentitySpellingTests.cs` — updated constructions only; no meaning changes.
- `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionGateTests.cs` — one new test for
  sequential effect groups on one lease.

## Tasks

### Task 1 — The disposition

- [x] RED: a test asserting an admitted request returns `SessionAttachmentIndexDisposition.Concluded`
      does not compile / fails.
- [x] GREEN: add the enum; make it the first positional member of `SessionAttachmentIndexOutcome`;
      add the single `DeferredForMaintenance` static; update every construction site.
- [x] Every existing attachment-indexing case still passes unchanged in meaning.

### Task 2 — Sequential effect groups are real

- [x] RED: a gate test that disposes one effect group and begins a second on the same lease while
      ordinary fails.
- [x] GREEN: none needed — the gate already supports it. The test pins the behaviour §3.4 depends on.

### Task 3 — One work lease per dequeued request

- [x] RED: a dequeued request against a closed gate creates no scope, opens no connection, calls no
      provider, writes nothing, and returns `DeferredForMaintenance`.
- [x] RED: an admitted request takes exactly one lease of kind `SessionAttachmentIndexing`.
- [x] RED: a closure begun from inside the request's scope disposal does not conclude its drain
      synchronously.
- [x] RED: a genuine failure's `MarkFailedAsync` scope is created while the lease is still held.
- [x] GREEN: inject `IGrimoireConnectionAdmissionGate`; take the lease before the `try`; declare it
      so reverse-order disposal releases every scope first.

### Task 4 — One sequential effect group per batch

- [x] RED: revocation winning the frontier makes zero provider calls, writes nothing past the pending
      mark, and returns `DeferredForMaintenance`.
- [x] RED: effect start winning makes the closure wait through the provider call and its append.
- [x] RED: a close landing between batch N and batch N+1 keeps batch N's chunks durable, makes no
      further provider call, and defers.
- [x] GREEN: pass the lease into `ProcessAsync`; open the group at the top of `FlushBatchAsync` and
      close it after whichever durable exit that batch takes.

### Task 5 — The identity, the attempt, and the re-signal

- [x] RED: after a deferral the attachment is still in `_pending`, the durable `AttemptCount` is
      unchanged, the durable status is not `Failed`, and the staged generation still exists.
- [x] RED: a `TryEnqueue` for that attachment during the deferral is still deduplicated.
- [x] RED: after reopen the exact request instance reaches the channel once, and a second iteration
      at the same generation does not write it again.
- [x] GREEN: read the generation before the lease attempt; hold the exact request; release `_pending`
      only on `Concluded`; directly await the actual reopen and re-signal with `TryWrite` at the top
      of the loop.

### Task 6 — The loop arms and the reconciliation scope

- [x] RED: a repeatedly deferred worker logs nothing at `Error` or `Warning`, creates no scopes, and
      calls no provider.
- [x] RED: a deferred reconciliation opens no scope.
- [x] GREEN: add the `Debug` arms; stop the batch loop on a deferral and skip its trailing
      reconciliation; give `ReconcileAndEnqueueAsync` its own lease.
- [x] The existing genuine-failure and host-cancellation paths still pass, pinning all three apart.

### Task 7 — The missing coverage reason

- [x] GREEN: `[ExcludeFromCodeCoverage]` on `SessionAttachmentIndexingService` gains the inline
      reason `Arcanum.DESIGN.md` §13.8 requires and every sibling already carries.

### Task 8 — Documentation

- [x] `docs/Arcanum.DESIGN.md` §21.8, §10.20.3, §13.7.
- [x] `docs/Arcanum.Engineering.md` per-issue paragraph after #253.
- [x] `README.md` local-first bullet.

### Task 9 — Verification

- [ ] Focused `SessionAttachmentIndexingAdmissionTests`, `SessionAttachmentIndexingTests`,
      `SessionAttachmentIndexingQueueTests`, `GrimoireConnectionAdmissionGateTests`,
      `EntryWeavingServiceTests`.
- [ ] Complete `RetroDownfall.Arcanum.Tests` and `RetroDownfall.Compendium.Tests` suites.
- [ ] Warning-free Release solution build; `git diff --check`; clean tracked status.
- [ ] Mutation-check each guard: releasing the lease before the scope, ignoring the effect-group
      refusal, releasing `_pending` on a deferral, and re-signalling through `TryEnqueue` must each
      fail a test that had previously passed.

## Delivery evidence

_To be completed on delivery._
