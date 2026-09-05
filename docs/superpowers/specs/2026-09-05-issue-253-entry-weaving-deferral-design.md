# Issue #253 Entry Weaving Work and Effect Lifetime Design

**Status:** Approved for implementation.

**Branch:** `codex/issue-253-entry-weaving-deferral`, cut from `grimoire-fixes` at
`e5ff0f97` (one commit past the #252 merge `e8820721`).

**Issue:** [#253 — Grimoire: defer Entry weaving safely across offline transitions](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/253)

**Parent design authority:** `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`,
§6.2, §6.3 and §9.1. Where this document and the parent disagree, the parent governs except on the
one point §1.3 records as a deliberate departure.

## 1. Decision

### 1.1 What this child delivers

#245 built `IGrimoireConnectionAdmissionGate` and gave it three ordinary lifetimes: request leases,
work leases, and connection-opening tickets. #251 and #252 subscribed the first of those. The
**work** lease and its external-effect frontier have been built, tested, and documented since #245
and have had no production consumer at all — `TryAcquireWorkLease` and `TryBeginExternalEffectGroup`
are reachable today only from `GrimoireConnectionAdmissionGateTests`.

This child gives them their first one. `EntryWeavingService` is chosen first because it is the
simplest of the three background workers the parent names: it has no durable queue identity to
preserve (#254) and no page watermark to resume from (#255). Its whole durable state is the
`LEFT JOIN entry_embeddings … WHERE ee."EntryId" IS NULL` selection, which means a tick that does
nothing is already indistinguishable from a tick that never ran.

Three things follow, and they are this child's entire content.

**First, one work lease per tick, taken before the tick's DI scope exists and released only after
that scope has asynchronously disposed.** The lease is what puts the worker into stage one's drain
set. Without it, a transition's stage one does not wait for Entry weaving at all: it waits only for
request leases, and the worker's already-open physical handle is discovered later, in stage two's
`ICovenantConnectionDrain`, after ordinary admission has already been declared closed for the
generation.

**Second, one atomic external-effect group per batch, spanning the embedding provider call and every
resulting write.** The group's span is wider than it first appears, and §2.2 records why: the
provider call is not the only durable effect inside `IWeaveService.EmbedBatchAsync`.

**Third, a returned `DeferredForMaintenance` outcome that is neither completion nor failure.** Today
a tick that meets a closed gate throws, is logged at `Error` with its stack, and takes the
one-second fault backoff instead of the configured cadence — once per second for the whole window.

### 1.2 What this child does not deliver

No route, request or response DTO, CLI verb, option, output field, exit code, configuration key,
JSON contract, schema object, migration, or `ErrorCodes` member. No new `GrimoireWorkKind` member —
`EntryWeaving = 2` already exists. No change to the gate itself, to `GrimoireRequestKind`, to the
`503` refusal shapes, or to stream classification. No change to what Entry weaving selects, embeds,
or writes, and no change to its idempotency model or its poll cadence.

Attachment indexing (#254), Saga extraction (#255), and the remaining hosted producers (#256) keep
their own children. This child adds no shared worker-deferral vocabulary for them to inherit: the
outcome type is local to Entry weaving, and a common shape may be extracted later if one of them
proves it is genuinely the same shape rather than assumed to be.

### 1.3 Deliberate departure from the parent

The parent's §9.1 says Entry weaving "retries at its normal bounded poll cadence and does not spin
or enter the generic fault loop". It does not say how a deferred tick learns admission reopened, and
the superseded #239 plan asked for a next-open-generation continuation in the attachment-indexing
and Saga tasks but deliberately not in the Entry weaving one.

This child registers **no** generation waiter. `WaitForNextOpenGenerationAsync` exists and is
tested, but subscribing to it here would add precisely the detached waiter #253's own scope forbids,
and would inherit a hazard the worker has no way to answer: under `KeepClosed` the next-open signal
is never completed, so a waiter parked on it survives until host shutdown. Entry weaving already has
a code-owned bounded poll; a deferred tick simply returns and the existing interval delay runs. The
cost is that imprinting resumes up to one poll interval after a window closes rather than
immediately, which for a background indexer is not a cost worth a waiter.

## 2. Current behaviour and cause

### 2.1 A closed gate is reported as a product failure, once a second

`RunTickAsync` creates its scope and resolves the pooled `ArcanumDbContext` without touching SQLite,
so nothing refuses it there. The refusal arrives at the first `OpenConnectionAsync`:
`db.Database.OpenConnectionAsync` raises `CovenantConnectionEnrolmentInterceptor.ConnectionOpeningAsync`
→ `GrimoireOrdinaryConnectionLifecycle.BeginOpen` → `AcquireOrdinaryOpen`, which throws
`GrimoireMaintenanceUnavailableException` when the gate is `Closed`, or `Closing` with no live
ordinary lifetime on the current async flow.

`SqliteBusyRetry` does not swallow it — its only catch is `when (IsBusyOrLocked(ex))` — so it
propagates out of `RunTickAsync` into `ExecuteAsync`'s catch-all, which logs at `Error` with the
exception and then takes the **one-second** backoff rather than the configured interval, because the
interval delay sits inside the `try` after the tick and is skipped by the throw.

So a maintenance window today produces one `Error`-level line per second, each carrying a stack
trace, for the whole window — an expected, deliberate refusal reported as a product fault.

There is a second, quieter half. `AcquireOrdinaryOpen` throws only for a connection that is not yet
open. Once the tick's first fetch has opened it, EF keeps it open for the rest of the tick, so a
gate that closes **mid-tick** refuses none of the subsequent upserts; they proceed against an
already-enrolled handle, and the closure has to wait for it in stage two — having never waited for
it in stage one, because the worker holds no work lease.

### 2.2 The provider call is not the only effect in the group

`WeaveService.EmbedBatchAsync` finds no ambient `TurnAccountingHandle` on a background tick, so it
creates a **second** DI scope of its own, resolves `ITurnRunWriter` and `IBudgetReservationService`
from it, and calls `TurnAccountingHandle.BeginAsync`. That writes an `InferenceRuns` row through
`TurnRunWriter.StartRunAsync` — raw SQL over a real `ArcanumDbContext` connection — **before** any
provider I/O. Its `finally` then calls `CompleteAsync` with `CancellationToken.None`, a second
durable write **after** the provider call, already deliberately uncancellable.

Two consequences decide this design:

1. The effect group must open **before** `EmbedBatchAsync` and stay open through it. The accounting
   `StartRun`/`Complete` pair is part of the "complete durable disposition" maintenance must wait
   through, not just the `entry_embeddings` upserts. A group opened after the provider returned
   would leave the billable call and its run row outside the frontier entirely.
2. Because the work lease's ordinary lifetime is an `AsyncLocal` established on the tick's async
   flow, it flows into that second scope. Taking the lease first is therefore what makes the
   accounting writes admissible through a `Closing` window — nothing else would.

### 2.3 A scope leak on the same path

`TurnAccountingHandle.BeginAsync` awaits `StartRunAsync` outside any `try`, and `EmbedBatchAsync`
calls `BeginAsync` before its own `try`. A `GrimoireMaintenanceUnavailableException` from that write
therefore escapes with `accountingScope` never disposed. The `begin.IsFailure` arm disposes it; the
throwing arm does not.

This is pre-existing and is not reachable from Entry weaving once the work lease is held. It is
fixed here anyway, because it sits on the exact path this child re-times and the fix is one `try`.

## 3. Design

### 3.1 The outcome

```csharp
internal enum EntryWeavingTickOutcome : byte
{
    Woven = 1,
    DeferredForMaintenance = 2,
}
```

Two members, both non-zero, so a default-initialized outcome cannot read as "the tick ran". `Woven`
covers every admitted tick including the ones that find nothing pending or reject a batch — those
are already indistinguishable to the caller and stay so. `DeferredForMaintenance` says the tick was
refused admission and performed no provider call and no write.

The enum is local to Entry weaving by decision, not by accident. The parent's §9.3 requires all
three workers to distinguish maintenance deferral, genuine product failure, and host cancellation;
for this worker the other two already exist and are unchanged — a genuine failure still throws into
the `Error` + one-second-backoff arm, and host cancellation is still `OperationCanceledException`
with `stoppingToken` signalled. The new member completes the three-way distinction rather than
restating it.

No `ErrorCodes` member is added. `ErrorCodes.Grimoire.MaintenanceUnavailable` already exists and its
own summary already says "a deliberate, temporary refusal the caller should retry, never a product
failure"; a deferral that never reaches a wire surface needs no code of its own.

### 3.2 The ordering inside a tick

1. The Weave availability guard, unchanged, before anything else. It performs no I/O.
2. `TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, …)`. Denied → return
   `DeferredForMaintenance`. **No scope is created, no connection is opened, no provider is called.**
3. Create the tick's `AsyncServiceScope` and resolve `ArcanumDbContext`.
4. Fetch the pending selection. This is a read, inside the lease and outside the effect group.
5. Nothing pending → return `Woven`.
6. `TryBeginExternalEffectGroup`. Denied → return `DeferredForMaintenance`. A scope exists and a read
   ran, but **no provider call and no write**.
7. Inside the group: `EmbedBatchAsync`, then every resulting upsert.
8. Dispose the group, then the scope, then the lease — in that order.

Step 8 is the whole reason the lease is declared before the scope. Two `await using` declarations in
one block dispose in reverse order, so declaring the lease first and the scope second releases the
scope — and with it the pooled `ArcanumDbContext` and its enrolled handle — while the lease is still
held. Releasing the lease in a `finally` around the scope would invert that and let stage one
conclude the worker had drained while its connection was still going back.

### 3.3 What revocation may and may not touch

`lease.MaintenanceRevocation` is read **only** by `TryBeginExternalEffectGroup`, which the gate does
internally. It is never linked into the token passed to `EmbedBatchAsync`, to an upsert, or to the
fetch. The parent's §6.3 is explicit: once the effect frontier is won, maintenance waits through the
group and its durable disposition without delivering cancellation into it. A
`CreateLinkedTokenSource` combining `MaintenanceRevocation` with the host token anywhere in this
worker fails review.

The host `stoppingToken` keeps its existing meaning throughout and is the only token the tick passes
down.

### 3.4 The loop

`ExecuteAsync` gains one arm: a `DeferredForMaintenance` return logs once at `Debug` and falls
through to the **configured interval** delay, which is where a normal return already goes. It does
not throw, so it cannot reach the `Error` arm or the one-second backoff. Nothing else about the loop
changes — no new timer, no continuation, no waiter, no state carried between ticks.

### 3.5 Consequence this design accepts

Stage one's work-drain checkpoint is a code-owned five seconds, forwarded twice from
`ProductionOpeningAttemptTimeout` into the gate's full constructor. A real embedding provider round
trip can exceed that. So a transition begun while an effect group is open can reach
`Grimoire.WorkDrainTimeout`.

That is the intended answer and not a regression. It is the same answer a billable stream already
gives, for the same reason: an effect the provider is already charging for must reach its durable
disposition. The drain failure is fail-safe — no closed lease is issued, no phase runs, nothing is
erased, the gate reopens through the proven abort, and the operator gets a retryable `503`. Raising
the checkpoint would be a shared decision across all three workers and belongs to #256 or #257, not
here. This child records the consequence rather than pre-empting it.

### 3.6 What a future migration inherits from this

The parent's §3.1 says reset and migration are structurally the same lifecycle, and §3.4 says a
migration adds its own kind, payload, and handler while reusing the journal and admission
infrastructure unchanged. This child is the first worker-side half of that reuse, so its shape is
the template the remaining workers follow and the thing a migration will actually depend on.

The property that makes it reusable is a negative one, and it is worth stating so a later change
does not quietly spend it: **the worker never learns which transition is running.** Its whole
coupling is `TryAcquireWorkLease` and `TryBeginExternalEffectGroup`. It does not read the transition
kind, the operation id, the Covenant lease, the journal, or the phase; it does not distinguish a
direct reset from a healthy-catalog factory erasure, and it will not distinguish either from a
migration. `DeferredForMaintenance` says maintenance owns admission, not what maintenance is doing,
and its log line says the same. A migration therefore needs no change here at all — it closes the
same gate through the same owner, and every adopted worker stands down for it exactly as written.

Two things a migration should expect to revisit rather than inherit:

- **The drain checkpoint.** §3.5 records that five seconds can be shorter than an embedding round
  trip, and that a reset answers a `Grimoire.WorkDrainTimeout` with a retryable `503`. A migration
  is likelier to run at startup or upgrade time, where "ask the operator to retry" is a worse
  answer than it is for a reset an operator just requested. The checkpoint is code-owned and
  already a separate constructor knob from the opening-attempt timeout, so raising it for a
  worker-bearing process is a one-line change — but it is a shared decision across every adopted
  worker, which is why it belongs to #256 or #257 rather than to this child.
- **Resume latency.** §1.3 declines a next-open-generation waiter, so a deferred tick resumes on the
  ordinary poll interval. That is right for a background indexer and would still be right after a
  migration. A producer whose work is user-visible on reopen may want the waiter instead, and the
  hazard §1.3 names — the signal is never completed under `KeepClosed` — is the thing such a
  producer has to answer before taking one.

## 4. Testing strategy

RED before GREEN for every behavioural claim. Barriers, not sleeps.

**Lease and ordering.** A tick against a gate closed for maintenance creates no scope, opens no
connection, calls no provider, writes nothing, and returns `DeferredForMaintenance`. A tick against
an open gate takes exactly one lease of kind `EntryWeaving`. The lease is still held at the moment
the tick's scope disposes — proved by a scope whose disposal observes the gate still counting the
work lifetime, not by asserting on ordering after the fact.

**Effect frontier.** Revocation winning the race means zero provider calls and zero writes, and
still returns `DeferredForMaintenance`. Effect start winning means the closure waits through the
provider call and every upsert; a transition that begins mid-group does not conclude stage one until
the group and the scope are both released.

**Loop behaviour.** A repeatedly deferred worker makes no `Error` log, increments nothing, and ticks
at the configured cadence rather than the one-second fault cadence — the mirror of the existing
`ExecuteAsync_TickThrowsRepeatedly_BacksOffInsteadOfTightLooping` test, which stays as the genuine
failure case so the two paths are pinned apart.

**Regression.** Every existing `EntryWeavingServiceTests` case keeps its current meaning; the fixture
gains an open gate rather than having its expectations rewritten.

## 5. Documentation

Owning documents updated in the same change set:

- `docs/Arcanum.DESIGN.md` §21.6 — the admission contract on the weaving tick, replacing the bare
  "failures retry next tick" with the three-way distinction, and stating that the per-row upsert
  granularity and the per-tick effect group are orthogonal.
- `docs/Arcanum.DESIGN.md` §10.20.3 — the sentence deferring worker adoption to the children is
  narrowed to the workers that have not adopted; the gate's first work consumer is described beside
  its first HTTP consumer.
- `docs/Arcanum.DESIGN.md` §13.7 — one regression-catalog row for the new coverage.
- `docs/Arcanum.Engineering.md` — one status paragraph in the per-issue run, after #252.
- `README.md` — one sentence on the local-first bullet, in the voice of the #251 and #252 clauses.

No issue number appears in `Arcanum.DESIGN.md`, `Arcanum.API.md`, `Arcanum.Command.Reference.md`, or
`Compendium.README.md`. `Arcanum.API.md` needs no change: no route, wire shape, status code, or
error code is added. `Compendium.README.md` must not change: no configuration key is added.
