# Issue #254 Attachment Indexing Work, Effect, and Queue-Identity Design

**Status:** Approved.

**Branch:** `codex/issue-254-attachment-indexing-deferral`, cut from `grimoire-fixes` at
`0123c1df` (the #253 merge).

**Issue:** [#254 — Grimoire: defer attachment indexing without losing queue identity](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/254)

**Parent design authority:** `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`,
§6.2, §6.3, §9.2 and §9.3. **Sibling precedent:**
`docs/superpowers/specs/2026-09-05-issue-253-entry-weaving-deferral-design.md`. Where this document
and the parent disagree, the parent governs, except on the two points §1.3 records as deliberate.

## 1. Decision

### 1.1 What this child delivers

#253 gave the admission gate's work lease and external-effect frontier their first production
consumer. Entry weaving was chosen first because it has no durable state to preserve across a
deferral. This child adopts the second worker, and it is the first one that does.

`SessionAttachmentIndexingService` has three properties Entry weaving does not have, and every
decision below follows from one of them:

- **A pending queue identity.** A dequeued request is a `SessionAttachmentIndexRequest` held in an
  in-memory bounded channel, and its presence is tracked by a separate `_pending` set keyed on
  `AttachmentId` alone. Losing either half loses the request.
- **An attempt counter.** `Attempt` is carried on the request, persisted verbatim into
  `session_attachment_index_state.AttemptCount`, and incremented in exactly one place — the
  automatic-retry path a failure classification leads to.
- **A resumable, partially durable generation.** One request can produce many embedding batches. The
  chunks a batch appends are durable immediately, and `NextChunkIndex` lets a later run skip them
  without paying for them again.

Five things follow, and they are this child's entire content.

**First, one work lease per dequeued request, taken before the request's DI scope exists and
released only after every scope that request opened has asynchronously disposed** — including the
second scope a genuine failure classification opens. The lease is what puts the worker into stage
one's drain set at all.

**Second, one sequential external-effect group per embedding batch**, spanning the provider call and
the append or durable failure classification that batch produces. Groups are taken one after another
from the same lease, which the gate permits and §3.4 records.

**Third, a deferral that keeps the exact pending identity and the exact attempt.** Today a closed
gate reaches `ProcessOneAsync`'s request-level failure handler, which stamps `Failed`, destroys the
staged generation, drops the `_pending` key, and re-enqueues at `Attempt + 1`. All four are forbidden
by the parent's §6.3.

**Fourth, a re-signal of that exact request, once, after reopen, written straight to the bounded
channel.** The deduplicating enqueue path cannot carry it: `TryEnqueue` returns `true` *without
writing* when `_pending` already holds the attachment, which retaining the identity guarantees.

**Fifth, the same protection for the reconciliation scope**, which opens a DI scope and a Grimoire
transaction every reconciliation period, on transition-to-enabled, and after every drained batch.

### 1.2 What this child does not deliver

No route, request or response DTO, CLI verb, option, output field, exit code, configuration key, JSON
contract, schema object, migration, or `ErrorCodes` member. No new `GrimoireWorkKind` member —
`SessionAttachmentIndexing = 1` already exists. No new member on the public, JSON-serialized
`SessionAttachmentIndexStatus`: the five attachment statuses on the wire are #45's and stay exactly
five. No change to the gate itself, to `GrimoireRequestKind`, to the `503` refusal shapes, or to
stream classification.

No change to what attachment indexing extracts, chunks, embeds, or writes; to its eligibility rules;
to its chunk provenance; to its retrieval scoping; or to its bounds — #45 owns all of that. No change
to the `attachment index` recovery handler or to any part of #40's durable recovery policy. A
maintenance deferral is deliberately a *different* state from a crash-interrupted attempt, and
nothing here re-homes or re-classifies the second.

Saga extraction (#255) and the remaining hosted producers (#256) keep their own children. This child
adds no shared worker vocabulary for them to inherit: the disposition enum is local to attachment
indexing, for the reason §3.1 records. It introduces no page or watermark abstraction, and it does
not raise the shared five-second work-drain checkpoint — that decision is #256's or #257's.

### 1.3 Deliberate departures

**The loop directly awaits the next open generation.** The parent's §9.2 says "Reopen writes that
exact request directly to the bounded channel once." A generation comparison alone cannot identify
that edge: the gate increments its generation when it enters `Closed` and does not increment again
when the exact closed lease restores ordinary admission. Comparing `CurrentGeneration` can therefore
write while admission is still closed, re-defer with the closed generation, and strand the request
after reopen.

The worker instead reads generation G before the refused lease attempt, retains G - 1 as the
wait-after floor, and directly awaits
`IGrimoireConnectionAdmissionGate.WaitForNextOpenGenerationAsync` with that floor and the host
stopping token. The predecessor is load-bearing because a refusal can occur in either `Closing(G)`
or `Closed(G)`, and reopening `Closed(G)` reports G rather than G + 1. The gate returns immediately
only while ordinary, so the predecessor cannot turn a still-closing or closed gate into a false
reopen; if reopen wins before waiter registration, ordinary generation G is already strictly beyond
the floor and completes immediately. There is no detached continuation. Under `KeepClosed` the loop
remains parked with the exact `_pending` identity until recovery reopens ordinary admission or host
shutdown cancels the wait; it does not dequeue later work that the same closed gate would refuse.

**The reconciliation scope is protected here, not deferred to #256.** The issue's bullets name only
"one dequeued request". Reconciliation is not one, and on a strict reading belongs to the
remaining-hosted-producer child. It is protected here anyway, for two reasons. It is the exact
mechanism #239's own comment names as the leading unexplained candidate — an unenrolled pooled handle
opened on a short cadence inside the erasure window — and #254 is the child that touches this worker.
And without it the acceptance criterion is only half true: a dequeued request would defer cleanly
while the same loop, seconds later, reported the same expected refusal as a fault. #256 keeps the
inventory, the classification, and every other producer.

## 2. Current behaviour and cause

### 2.1 A closed gate is classified as a product failure, and destroys paid work

`ProcessOneAsync` creates its `AsyncServiceScope` and resolves `SessionAttachmentIndexProcessor`
without touching SQLite. The refusal arrives at the processor's first repository call — `SetPendingAsync`,
whose `OpenConnectionAsync` raises `CovenantConnectionEnrolmentInterceptor.ConnectionOpeningAsync` →
`GrimoireOrdinaryConnectionLifecycle.BeginOpen` → `AcquireOrdinaryOpen`, which throws
`GrimoireMaintenanceUnavailableException` when the gate is `Closed`, or `Closing` with no live
ordinary lifetime on the current async flow.

Nothing catches it as what it is. It reaches `ProcessOneAsync`'s catch-all, which performs, in order,
every one of the four things the parent's §6.3 forbids on a maintenance denial:

1. logs a `Warning` with a stack trace for an expected, deliberate refusal;
2. calls `MarkFailedAsync` → `MarkWithoutIndexAsync`, a **durable failure classification** that also
   runs `DeleteAttachmentGenerationsExceptAsync` and `ClearPendingGenerationAsync` — **destroying the
   staged generation**, so a request refused after batch 40 of 50 re-embeds all forty next time;
3. drops the `_pending` key in the `finally`, losing the queue identity; and
4. builds `ShouldRetry: true`, which after a five-second delay **increments the attempt** and
   re-enqueues.

That fourth step is also the only reason the request survives at all today, and it survives as a
different request than the one that was deferred.

There is a second, quieter half, and it is the one #239 is actually about. `AcquireOrdinaryOpen`
throws only for a connection that is not yet open. A gate that closes **mid-request** refuses none of
the writes that follow the first one; they proceed against an already-enrolled handle, and stage two
meets that handle having never waited for it in stage one, because the worker holds no work lease.

### 2.2 The reconciliation scope is the unenrolled opener #239 could not place

`ReconcileAndEnqueueAsync` creates a DI scope, resolves `SessionAttachmentIndexRepository`, and opens
a Grimoire transaction that performs four mutations before its pending SELECT. It runs on transition
to enabled, on every reconciliation period, **and after every drained batch**.

None of it holds a lease, and the scope resolves `ArcanumDbContext` without resolving
`ILongRunningOperationStore` — which is precisely the shape #239's body describes and its comment
names as the leading candidate for the recurrence after `61c794fb`. This child establishes the answer
the parent's scope asks for: yes, this worker can open the Grimoire inside an erasure window, from
two independent places, and the second one is not on the dequeued-request path at all.

During a window today it throws into the same catch-all, producing one `Warning` with a stack trace
per reconciliation period for the length of the window.

### 2.3 The deduplicating enqueue path cannot carry a re-signal

`TryEnqueue` is the only public way into the queue, and it does four things a re-signal must not do:

```csharp
if (!_pending.TryAdd(request.AttachmentId, 0))
{
    return true;
}
```

An attachment already in `_pending` is swallowed and reported as **success**. Retaining the pending
identity across a deferral — which §9.2 requires — therefore guarantees that any re-signal routed
through `TryEnqueue` is silently discarded. It also re-reads the feature flags and refuses when they
are off; it writes with `TryWrite` and, on a full channel, *removes the pending key and returns
false*, losing the identity it was asked to preserve; and it swallows exceptions by removing the key.

This is why the parent says "it does not call the deduplicating enqueue path" rather than leaving the
mechanism open.

### 2.4 What reconciliation cannot recover

Reconciliation is not a fallback for a lost deferral. `ReconcileAndFindPendingAsync` selects only
attachments whose state row is absent or whose status is `Pending` or `Stale`, and the `Stale`
promotion rewrites only rows currently `Indexed`. A request whose durable row today says `Failed` —
which is what §2.1's classification writes — is never re-selected. And what it produces is a
*reconstructed* request from the durable row, not the exact one that was deferred.

## 3. Design

### 3.1 The disposition

```csharp
internal enum SessionAttachmentIndexDisposition : byte
{
    Concluded = 1,
    DeferredForMaintenance = 2,
}
```

Two members, both non-zero, so a default-initialized disposition cannot read as "the request ran".
It becomes the first positional member of the existing internal outcome record:

```csharp
internal sealed record SessionAttachmentIndexOutcome(
    SessionAttachmentIndexDisposition Disposition,
    SessionAttachmentIndexStatus Status,
    bool ShouldRetry);
```

First and required rather than trailing with a default, because a call site that silently inherits
`Concluded` is the exact leak the type exists to close: `Concluded` is what lets the attempt counter
advance.

A deferral is one shape, exposed as a single static so it cannot be spelled two ways:
`Disposition = DeferredForMaintenance`, `Status = Pending`, `ShouldRetry = false`. `Pending` is the
honest status rather than a filler — the durable row says `Pending`, the queue identity is still
pending, and this member is informational in production (`ProcessOneAsync` reads only `ShouldRetry`).
`ShouldRetry = false` is equally literal: the automatic-retry path is the attempt-increment path, and
a deferral must not take it.

The enum is local to attachment indexing by decision. #253 kept `EntryWeavingTickOutcome` local so
that this child and #255 would not inherit a vocabulary guessed at before either was written; this
child is the second worker and the first with durable state, and the two shapes are already
different — Entry weaving reports a whole tick, this reports one request's disposition, and Saga will
report a page. Extracting a common type from two samples would still be a guess about the third.
#255's own boundary is explicit that it must not be forced to adopt one.

No `ErrorCodes` member is added. `ErrorCodes.Grimoire.MaintenanceUnavailable` already exists and a
deferral never reaches a wire surface.

### 3.2 The ordering inside one dequeued request

1. Read `admissionGate.CurrentGeneration` **before** attempting the lease, and keep it. §3.5 records
   why the read must precede the refusal rather than follow it.
2. `TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, …)`. Denied → hold the request and
   return `DeferredForMaintenance`. **No scope is created, no connection is opened, no provider is
   called, nothing is classified, and the attempt does not move.**
3. `await using IGrimoireWorkLease lease` — declared before everything below, so reverse-order
   disposal releases every scope first.
4. The existing `try`: create the request's `AsyncServiceScope`, resolve the processor, and call
   `ProcessAsync(request, lease, stoppingToken)`.
5. The existing catches: a genuine failure opens its own `MarkFailedAsync` scope. That scope is
   inside the lease, which is what §9.2 requires and what makes its writes admissible through a
   `Closing` window.
6. `_pending` is released only when the disposition is `Concluded`.
7. Automatic retry, unchanged, reachable only from `Concluded`.

The lease is passed to `ProcessAsync` as a parameter rather than injected. The processor is
registered by convention as a scoped service, so a new constructor dependency would compile and fail
only at `GetRequiredService` on a path no test drives today. A parameter cannot be mis-registered,
and it also makes the "one lease per request, not one per scope" rule visible at the call site.

### 3.3 The ordering inside the processor

`ProcessAsync` keeps its shape. Two writes precede the first batch and stay outside every group:
`SetPendingAsync` and `BeginReplaceAsync`. They are Grimoire writes with no external effect, they are
admissible because the lease's ordinary lifetime is an `AsyncLocal` on this flow, and there is no
provider call for them to frontier.

The group is exactly `FlushBatchAsync`. It opens before `weave.EmbedBatchAsync` and closes after
whichever of that batch's three durable exits is taken — the append, the provider-failure mark, or
the dimension-mismatch mark. A refused group returns the deferral straight out of the local function;
both call sites already propagate a non-null return unchanged, so no control flow is restructured.

`CompleteReplaceAsync` — the publication flip after the last batch — deliberately takes **no group of
its own**, and the reason is worth stating because the opposite reading is available. The group is an
atomic frontier in front of an external effect; publication has none. The work lease already makes
maintenance wait through it, because stage one waits on the lease's terminal and the lease is not
released until every scope has disposed, and because the gate cannot reach `Closed` while a lease is
held — stage two runs only after the drain succeeds. So a group there would add no waiting and would
add one refusal point, whose only consequence is that a fully embedded generation stays unpublished
until a later run re-extracts it for free. That is a cost with no matching benefit.

### 3.4 One lease, many groups

The gate refuses a group when `lease.ActiveEffectGroup is not null`, and group disposal nulls that
slot. Nothing on the lease records that it has *had* a group, and the idempotency counter lives on
the group rather than the lease, so a disposed group cannot poison its successor. Between groups the
lease stays in the census, because `CompleteWorkLeaseIfDrainedWhileLocked` returns early while the
scope has not disposed.

So sequential groups from one lease are exactly what the gate supports, and three consequences are
designed for rather than discovered:

- **Between groups the lease is revocable.** `BeginOrResumeExclusive` collects revocation sources
  from work leases with no active group. A refused group N+1 therefore means "defer, having durably
  completed batches 1..N" — not an error, and not a rollback.
- **A close begun during group N waits for group N and refuses group N+1.** A many-batch attachment
  cannot starve a transition.
- **No test pins the sequential case.** The gate's own suite covers concurrent-refused,
  post-`Closing`-refused, stale-generation-refused and fresh-generation-allowed, but never
  dispose-then-begin-again on one lease while `Ordinary`. This child depends on that behaviour and
  adds the test, in the gate's own suite where the behaviour lives.

### 3.5 Retaining the identity, and re-signalling it once

A deferred request is moved to a **held list** owned by the loop — the single reader — carrying the
exact `SessionAttachmentIndexRequest` instance and the predecessor of the generation observed at
step 1. Its `_pending` key is deliberately *not* released, so it remains the identity token that
suppresses duplicates while it waits.

At the top of every loop iteration, before the worker waits for channel work, a non-empty held list
causes the loop to await `WaitForNextOpenGenerationAsync` directly. After that actual-open signal,
eligible held entries are written straight to `_channel.Writer`, oldest first. Three details are
load-bearing:

- **The generation is read before the lease attempt, and the retained wait floor is its predecessor.**
  Reading after would race a reopen that had already happened and record the *new* generation as the
  one to wait past, which is a lost wakeup. Retaining G itself also loses `Closed(G) → Ordinary(G)`,
  whether the waiter registers before or after reopen. Waiting after G - 1 lets the gate's ordinary-
  state check distinguish a completed reopen from the still-closing or fully-closed states.
- **The write is `TryWrite`, not `WriteAsync`.** The channel is bounded with `FullMode.Wait`, and the
  re-signal runs on the loop that is the channel's only reader. Awaiting a write on a full channel
  would deadlock the reader against itself. Once reopen is observed, the reader temporarily removes
  already-bounded later requests, writes the deferred prefix before them, and writes the later
  requests back in their original order. If the channel fills, the unwritten suffix remains ordered
  in service-owned memory and later `TryWrite` passes refill the room normal dequeue progress makes.
- **It is once per reopen, not once per iteration.** A deferred entry leaves the held list when that
  reopen pass writes it or retains it in the ordered unwritten suffix; it is never routed through
  `TryEnqueue`. Producers share the channel-write critical section and cannot jump ahead of that
  suffix.

Held entries do not survive the process. That is correct rather than tolerated: the durable row still
says `Pending`, reconciliation re-selects it after restart, and `_pending` is in-memory too, so the
identity that was suppressing duplicates dies with the thing it was protecting. #40's recovery policy
already reaches the same conclusion for this operation kind — restart idempotently.

### 3.6 The loop, and the reconciliation scope

`ExecuteAsync` gains a deferral arm and one guard. The existing channel/reconciliation wait retains
its deliberate single-outstanding-waiter discipline; the actual-reopen wait is a separate direct
await made only while the held list is non-empty.

A deferred request logs once at `Debug` naming the attachment, and the batch loop stops dequeuing:
once maintenance owns admission, every remaining request will be refused too, and continuing would
convert the queue into the held list one refused lease at a time. The trailing reconciliation is
skipped on that path for the same reason. Later intake remains bounded in the channel throughout the
closed period, and the reopen reordering keeps the original deferred request ahead of it.

`ReconcileAndEnqueueAsync` takes a work lease of the same kind before its own scope and returns
`SessionAttachmentIndexDisposition`. Refused, it logs at `Debug` and does nothing — no scope, no
transaction, no enqueue.

Neither maintenance arm throws. Per-request product failures are classified inside
`ProcessOneAsync`, still within that request's work lease, and any automatic retry delay follows only
after the lease has been released. The hosted loop's catch-all is therefore reserved for scheduler or
reconciliation faults, with its `Warning` and one-second backoff. Host cancellation keeps its present
meaning as an `OperationCanceledException` with `stoppingToken` signalled. The three-way distinction
§9.3 requires is therefore completed rather than restated.

### 3.7 What revocation may and may not touch

`lease.MaintenanceRevocation` is read **only** by `TryBeginExternalEffectGroup`, which the gate does
internally. It is never linked into the token passed to `EmbedBatchAsync`, to any repository call, or
to extraction. A `CreateLinkedTokenSource` combining it with the host token anywhere in this worker
fails review — and here that rule has teeth beyond §6.3, because a non-shutdown
`OperationCanceledException` reaching `ProcessOneAsync` is caught by an arm that marks `Failed` and
increments the attempt. Linking revocation would turn every mid-request window into the exact
outcome this child exists to prevent.

The host `stoppingToken` keeps its existing meaning throughout and remains the only token passed
down.

### 3.8 Consequences this design accepts

**The work-drain checkpoint can still be exceeded.** Five seconds is code-owned and a real embedding
round trip can be longer, so a transition begun while a group is open can report
`Grimoire.WorkDrainTimeout`. That is fail-safe and is the same answer a billable stream already gets;
raising it is a shared decision across every adopted worker and belongs to #256 or #257. This child
adds a second worker that can produce it and changes nothing about it.

**`SetPendingAsync` still un-scopes older versions before any success.** When the request is the
latest bound version of its logical key, `SetPendingAsync` nulls `RetrievalScope` on every other
version's chunks *before* anything is embedded. A window landing between that write and a refused
first group therefore leaves the logical key retrievable by nothing until a later run publishes. This
is pre-existing #45 behaviour — an ordinary provider failure leaves exactly the same state, and
`MarkWithoutIndexAsync` does not restore the scope either — and moving the write would change what
#45 owns. It is recorded rather than fixed, and the deferral makes it recoverable in the one way that
matters: the request keeps its identity and comes back, where today it would come back as a `Failed`
row reconciliation never re-selects.

**A full queue is reordered only after actual reopen.** During the closed period the first refused
request is held and every later request remains in the bounded channel. After reopen the single
reader drains those later requests only long enough to write the deferred prefix ahead of them. Any
tail that does not fit stays service-owned and bounded by the work that was already admitted; normal
dequeue progress creates the room later `TryWrite` passes use.

### 3.9 What a future migration inherits from this

#253 recorded that the property making the weaving frontier reusable is a negative one: the worker
never learns which transition is running. That holds here unchanged and is worth restating because
this child had more opportunity to break it. Attachment indexing reads `TryAcquireWorkLease`,
`TryBeginExternalEffectGroup`, `CurrentGeneration`, and `WaitForNextOpenGenerationAsync`, and nothing
else. It does not read the transition kind, the operation id, the Covenant lease, the journal, or the
phase. A migration closes the same gate through the same owner and this worker stands down for it
exactly as written.

The one thing this child adds to that inheritance is the shape a producer with durable queue state
uses: hold the exact work item, keep its identity token, await the gate's actual-open signal with the
host token on the worker loop, and re-signal past the deduplicating intake. `KeepClosed` parks that
loop until recovery or host cancellation instead of turning a close-generation increment into a
false reopen.

## 4. Testing strategy

RED before GREEN for every behavioural claim. Barriers, never sleeps, for ordering — and where a real
gate is needed, the `EntryWeavingServiceTests.OpenGate()` shape (`new(TimeProvider.System)`) rather
than the gate suite's private manual-clock factory.

A new `SessionAttachmentIndexingAdmissionTests` holds the admission cases. The existing DB-backed
`SessionAttachmentIndexingTests` is 1,225 lines of #45 behaviour that must keep its current meaning,
and the DB-free `SessionAttachmentIndexingQueueTests` drives statics against an empty container that
cannot resolve a processor. Neither is the right home, and neither is rewritten.

**Lease and ordering.** A dequeued request against a closed gate creates no scope, opens no
connection, calls no provider, writes nothing, classifies nothing, and returns
`DeferredForMaintenance`. An admitted request takes exactly one lease of kind
`SessionAttachmentIndexing`. The lease is still held at the moment the request's scope disposes —
proved by a drain started from inside scope disposal not completing synchronously, the same
deterministic probe #253 used. A genuine failure outside a batch effect group's durable disposition
opens its `MarkFailedAsync` scope while the same lease is still held. A maintenance outcome is
retained only after its processing scope disposes successfully; a disposal failure concludes
normally, releases the identity, and admits the incremented retry.

**Effect frontier.** Revocation winning means zero provider calls and zero writes past the pending
mark. Effect start winning means the closure waits through the provider call and the append it
earned. A close landing between batch N and batch N+1 leaves batch N's chunks durable, makes no
further provider call, and returns `DeferredForMaintenance` — proved through `FakeWeaveService`'s
existing `BeforeEmbedBatchAsync` hook, which receives the batch number.

**Queue identity and attempt.** After a deferral the attachment is still in `_pending`, the durable
`AttemptCount` is unchanged, the durable status is not `Failed`, and the staged generation still
exists. A `TryEnqueue` for that attachment during the deferral is still deduplicated. After the gate
reopens, the exact request instance — same `SessionId`, same `Attempt` — appears on the channel
exactly once, and a second loop iteration at the same generation does not write it again. Later
requests remain bounded while admission is closed and retain FIFO processing order after reopen,
including when the first re-signal pass fills the channel and retains an unwritten suffix.

**Loop behaviour.** A repeatedly deferred worker logs nothing at `Error` or `Warning`, creates no
scopes, calls no provider and increments nothing. A deferred reconciliation opens no scope. A
provider exceptions, append exceptions, and provider-originated cancellation are durably classified
before their effect group disposes; other request-level failures are handled inside
`ProcessOneAsync`; and host cancellation propagates without either classification or retry, pinning
all three apart.

**Gate.** One work lease begins a second effect group after disposing its first, while ordinary — the
sequential case §3.4 depends on and the gate suite does not currently cover.

**Regression.** Every existing `SessionAttachmentIndexingTests` and `SessionAttachmentIndexingQueueTests`
case keeps its meaning; only the outcome record's construction changes.

## 5. Documentation

Owning documents updated in the same change set:

- `docs/Arcanum.DESIGN.md` §21.8 — the admission contract on the attachment queue, in the "Queue and
  lifecycle" paragraph, beside the existing retry and reconciliation sentences it qualifies.
- `docs/Arcanum.DESIGN.md` §10.20.3 — the work-capability paragraph is no longer about one consumer;
  it names the second and what having durable queue state changed.
- `docs/Arcanum.DESIGN.md` §13.7 — one regression-catalog row for the new coverage.
- `docs/Arcanum.Engineering.md` — one status paragraph in the per-issue run, after #253.
- `README.md` — the local-first bullet's background-indexing clause, extended in the voice of the
  #251, #252 and #253 clauses.

No issue number appears in `Arcanum.DESIGN.md`, `Arcanum.API.md`, `Arcanum.Command.Reference.md`, or
`Compendium.README.md`. `Arcanum.API.md` needs no change: no route, wire shape, status code, or error
code is added, and the five wire attachment statuses are unchanged. `Compendium.README.md` must not
change: no configuration key is added.
