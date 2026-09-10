# Issue #252 Streaming-Route Inventory and Frame-Boundary Quiescence Design

**Status:** Approved for implementation.

**Branch:** `codex/issue-252-streaming-quiescence`, cut from `grimoire-fixes` at
`cae3a7f4e4d47e609aa2004e321f7840629e554b` (the #251 merge).

**Issue:** [#252 — Grimoire: inventory streams and quiesce the five declared SSE routes](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/252)

**Parent design authority:** `docs/superpowers/specs/2026-08-31-issue-239-grimoire-admission-design.md`,
§8. Where this document and the parent disagree, the parent governs except on the three points §1.3
records as deliberate departures.

## 1. Decision

### 1.1 What this child delivers

#245 built the gate and gave a request lease two kinds. #251 gave the gate its first production HTTP
consumer, and took `GrimoireRequestKind.Finite` for every matched, non-exempt `/api` and `/v1`
request — every one, including the five unbounded SSE watchers. A `Finite` lease is drained through
completion and is never revoked; only a `QuiesceableStream` lease has its `MaintenanceRevocation`
cancelled when the gate leaves `Ordinary`. #251's own §3.8 recorded the consequence rather than
hiding it: an erasure begun while a watcher is connected drains for five seconds, times out, and
refuses.

So the revocation machinery exists, is tested, and has no subscriber. Three things follow, and they
are this child's whole content.

First, the five declared SSE routes stop taking `Finite` leases. `TryAdmitGrimoireRequest` selects
the kind from the route rather than assuming one, and the five carry a marker that says which kind
they are. Nothing else about admission moves: the same stage, the same path selection, the same
holder, the same disposal ordering.

Second, those five routes learn to end. Revocation reaches the producer and never the frame writer,
so an in-progress frame completes and no next frame begins; the producer is cancelled and observed;
the enumerator and its scope are disposed; and the response ends normally with the terminal
`data: [DONE]` frame that already means "the server ended this deliberately" on every one of these
routes. Stage one then completes instead of timing out, because the request scope it was waiting on
is finally disposed.

Third, a bidirectional inventory keys every streaming response in `src/**` by route pattern,
endpoint member, framing, Grimoire authority, and one exact class. A streaming construct with no
catalog entry fails, and a catalog entry naming a construct that no longer exists fails. That is
what makes the five a closed set rather than five routes that happen to be marked today.

### 1.2 The defect this child must fix to be shippable

**Revocation on a shared token tears the frame it was meant to preserve.** Each of the five routes
today hands one token to both the producer and the frame writer. `EventEndpoints` builds
`streamCts` from `RequestAborted` and the endpoint token and passes its `ct` to
`eventBus.Subscribe<T>(ct)` *and* to `SseStreamWriter.StreamAsync(..., ct)`, which forwards it to
`writeFrameAsync` (`EventEndpoints.cs:73-97`, `SseStreamWriter.cs:133`). `SessionEndpoints` and
`ApprenticeEndpoints` do the same with `httpContext.RequestAborted`
(`SessionEndpoints.cs:1730-1735`, `ApprenticeEndpoints.cs:485-490`).

Linking maintenance revocation into that single token would cancel the `Response.Body.WriteAsync`
and `FlushAsync` inside `WriteSseJsonAsync` (`EventEndpoints.cs:355-357`) or `WriteEntrySseAsync`
(`SessionEndpoints.cs:2703-2705`) mid-call — a truncated `data:` line with no `\n\n`, which is
precisely the partial frame the acceptance criterion forbids. The two tokens have to be separate
before revocation may be linked to anything, and that separation is the load-bearing part of this
change rather than an implementation detail.

### 1.3 Deliberate departures from the parent design

Each is recorded here so the divergence is a decision rather than drift.

1. **Authority is a field beside the class, not a fourth class member.** The parent (§8) names one
   exact class per route from `GrimoireQuiesceableStream`, `FiniteDrain`, `BillableDrain`, or
   `NoGrimoireAuthority`, while the same sentence also lists Grimoire authority as its own key. Read
   as one enum those two disagree: `/api/events/logs` reads no database at all and is still in the
   complete positive quiesceable set, so it would have to be both `GrimoireQuiesceableStream` and
   `NoGrimoireAuthority`. This design keeps all four parent names and splits them the way the
   existing acquisition inventory already splits `GrimoirePathAuthority` from
   `GrimoireAcquisitionKind`: `GrimoireStreamAuthority` is `LiveGrimoire` or `NoGrimoireAuthority`,
   and `GrimoireStreamClass` is `GrimoireQuiesceableStream`, `FiniteDrain`, or `BillableDrain`. Both
   enums are fully populated by real routes; neither has a speculative member.

2. **A quiesced stream ends with `[DONE]`.** The parent says only "end the existing response
   normally". `[DONE]` is what these five already write when the host cancels them
   (`EventEndpoints.cs:100-104`, `SessionEndpoints.cs:1746-1751`, `ApprenticeEndpoints.cs:499-503`),
   and it is what both first-party parsers read as a deliberate end — `WatchSseParser.cs:206` and
   `SseFrameParser.cs:80`. Ending without it makes `WatchSseParser.cs:111-114` report "The stream
   disconnected before a `[DONE]` marker was received" and mark the end retryable, which reports an
   architectural refusal as a network fault: the exact confusion issue #239 exists to remove. It is
   a complete frame, so the acceptance criterion is met either way; this picks the one that does not
   lie.

   This is not the "false completion" OATH §7.10 (`Arcanum.OATH.md:521`) forbids. That rule governs
   a turn whose mutation commit failed and requires a terminal *error* on the stream that was
   reporting that turn — the inference streams, which are `BillableDrain` here and are never
   maintenance-cancelled. The five watcher routes commit no mutation and report no turn result, so
   `[DONE]` on one claims only that the server ended it, which is true.

3. **The inventory scans `src/**` and classifies the A2A SDK surface as one named entry.** The
   parent forbids prefix and folder exemptions, which this keeps. But `apiGroup.MapA2A(server,
   relative)` (`A2AServerEndpoints.cs:87`) is a third-party mapping from the `A2A.AspNetCore`
   package, and the agent card advertises `Streaming = true` (`A2AServerEndpoints.cs:137`) — so an
   `/api`-rooted streaming response exists whose writer is not first-party source and which no
   Roslyn scan over `src/**` can discover. Rather than let it be invisible, the catalog carries it
   as an explicit entry keyed on that `MapA2A` call site, classified `BillableDrain` with a
   third-party-framing proof. It cannot be quiesceable: ending a response at a frame boundary
   requires owning the writer, and Arcanum does not own this one.

## 2. Existing behaviour and cause

`TryAdmitGrimoireRequest` (`ApiBootstrapper.cs:820-855`) resolves the request's
`GrimoireRequestAdmissionScope` and ends on one line:

```csharp
// Finite is the only kind this stage takes. Marking the declared streaming routes
// quiesceable, so a transition can end them at a frame boundary, is a separate change; until
// it lands a live stream is drained through completion like any other finite request.
return admission.TryAdmit(GrimoireRequestKind.Finite);
```

The gate's only kind-sensitive path is in `BeginOrResumeExclusive`
(`GrimoireConnectionAdmissionGate.cs:425-436`): on the `Ordinary → Closing` edge it collects the
`Revocation` source of every request lease whose `Kind` is `QuiesceableStream`, and of every work
lease with no active effect group, and cancels them outside the lock
(`GrimoireConnectionAdmissionGate.cs:484-506`). A `Finite` lease is never in that set.

`DrainRequestAndWorkAsync` (`GrimoireConnectionAdmissionGate.cs:512-610`) is kind-blind. It awaits
the `Terminal` task of every live lease, and a request lease's `Terminal` completes only when the DI
request scope is disposed — for a streaming response, after the whole stream has finished and every
response-completion callback has run. Revocation does not shorten that wait by itself; it is the
signal that lets the stream choose to end, and the stream ending is what disposes the scope.

So the chain is complete except for its two ends. Nothing marks a route quiesceable, and nothing
observes `MaintenanceRevocation`. A transition attempted while `arcanum watch logs` is connected
waits out `_workDrainCheckpoint` and returns `Grimoire.WorkDrainTimeout`.

## 3. Approved architecture

### 3.1 One marker names a route's class, and the route declares it

`GrimoireStreamRouteMetadata` is an Api-owned endpoint-metadata marker carrying one
`GrimoireStreamClass`. The five SSE routes attach `GrimoireStreamRouteMetadata.Quiesceable` after
`.WithName(...)`; every other streaming route attaches the marker naming its own class.

The marker is a class rather than a bare enum in metadata because endpoint metadata is matched by
type: a bare enum would collide with any other enum a future route attached, and a marker type is
how `GrimoireAdmissionExemptRouteMetadata` and `InstallationResetRecoveryApiRouteMetadata` already
say what they say.

`TryAdmitGrimoireRequest` reads it and selects the kind:

```csharp
return admission.TryAdmit(
    endpoint.Metadata.GetMetadata<GrimoireStreamRouteMetadata>() is
        { Class: GrimoireStreamClass.GrimoireQuiesceableStream }
        ? GrimoireRequestKind.QuiesceableStream
        : GrimoireRequestKind.Finite);
```

`Finite` stays the default for an unmarked route, which is the safe answer: a finite lease is
drained through completion, so an unmarked streaming route is slow to drain rather than cut mid-frame.
The inventory is what stops it staying unmarked.

### 3.2 Two tokens, and only one of them may carry revocation

`GrimoireStreamQuiescence` is a per-request Api type resolved from the request's
`GrimoireRequestAdmissionScope`. It exposes exactly three members:

- `Revocation` — the lease's `MaintenanceRevocation`, or `CancellationToken.None` when this request
  holds no lease or holds a `Finite` one;
- `IsQuiescing` — whether that token is already signalled; and
- `LinkProducer(CancellationToken)` — a linked source combining the caller's own producer token with
  `Revocation`.

The rule the type exists to enforce is stated once, in its own remarks, and is the whole of §1.2's
fix: **the producer token carries revocation and the frame-write token never does.** A frame that
has begun writing must be allowed to finish writing, because the bytes already on the wire cannot be
withdrawn and a `data:` line with no terminating blank line is a protocol error rather than a short
stream.

### 3.3 `SseStreamWriter.StreamAsync` ends between frames

`StreamAsync` gains one parameter, `GrimoireStreamQuiescence quiescence`, and three behaviours:

1. the enumerator's token becomes `quiescence.LinkProducer(cancellationToken)`, so revocation stops
   the producer;
2. the loop tests `quiescence.IsQuiescing` before it starts a `MoveNextAsync` and again after each
   frame write returns, and breaks when it is set — so no next frame begins; and
3. `writeFrameAsync` and `WriteKeepAliveAsync` keep receiving the unrevoked `cancellationToken`, so
   an in-progress frame completes.

The existing `finally` is unchanged and already does the rest: `QuiesceAndDisposeAsync` cancels the
stream source, observes the outstanding move, and disposes the enumerator, in that order and for the
reason `Arcanum.DESIGN.md:1457` already records.

When the loop broke because of quiescence, `StreamAsync` writes the terminal `data: [DONE]` frame
before returning. It is written there rather than in each route because the writer is what owns
frame boundaries, and because five copies of a terminal write is five chances for one of them to
drift. `WriteDoneAsync` already writes on `CancellationToken.None` and swallows a dead socket, so it
is safe on the teardown path.

`SseStreamWriter.StreamAsync` has exactly five callers, and they are exactly the five quiesceable
routes. The `/v1` SSE mapper in `OpenAiV1Endpoints` and the NDJSON `InferenceExecuteWriter` are
separate writers and are untouched, which is what keeps a billable stream out of this path
structurally rather than by a runtime check.

### 3.4 The phases before the live loop stop at their own frame boundaries

Two of the five write frames before they reach `StreamAsync`, and a revocation that arrives during
those phases must be honoured the same way.

`/api/sessions/{id:guid}/stream` replays a bounded window of persisted Entries
(`SessionEndpoints.cs:1683-1708`), writes the `live` sentinel, then drains what the pump buffered.
`/api/apprentices/{id:guid}/chronicle` replays plan, escalation, and step-start frames
(`ApprenticeEndpoints.cs:425-478`), then drains its buffer. Each of those loops gains the same
between-frames test: finish the frame in hand, start no next one, and fall through to `StreamAsync`,
which observes `IsQuiescing`, starts nothing, disposes, and writes the one `[DONE]`. Falling through
rather than returning early is what keeps the terminal frame in one place.

The `live` sentinel and the `: connected` comment are skipped when quiescing, because each is itself
a complete frame and starting one is starting a next frame.

A revocation that arrives during the session route's Grimoire replay may instead surface as
`GrimoireMaintenanceUnavailableException` from the enrolment interceptor, since that replay reads
through EF. That is already correct behaviour and is left alone: the exception unwinds through the
route's existing catch, the admission stage cannot rewrite a response whose first byte has left
(`GrimoireMaintenanceRefusal.cs:61-66`), and the response ends after the last complete frame.

### 3.5 The bidirectional inventory

`GrimoireStreamingRouteInventory` mirrors `GrimoireConnectionAcquisitionInventory` because that is
the shape this repository already uses for "a new call site must be classified or fail".

A Roslyn scanner over every authored `.cs` file under `src` discovers a streaming construct at five
kinds of site:

- `SseStreamWriter.PrepareResponse(...)` invocation — one per SSE route;
- `InferenceExecuteWriter.WriteStreamAsync(...)` invocation — one per NDJSON inference route;
- an assignment of `Response.ContentType` to a literal beginning `text/event-stream` or
  `application/x-ndjson` — the two shared writers' own declarations plus the two routes that frame
  themselves inline;
- `Results.Stream(...)` invocation — the file-content responses; and
- `MapA2A(...)` invocation — the third-party streaming surface of §1.3 item 3.

The first two kinds are what give a per-route identity to a route that frames itself through a
shared writer. Without them the three NDJSON routes would collapse into one discovery at
`InferenceExecuteWriter.cs:61` and the catalog could not say which route was which — the same reason
the acquisition inventory discovers a marked route's invocations as well as its declaration.

Two constructs the scanner deliberately does not reach are recorded here so their absence is a
decision. `GET /metrics` renders its whole Prometheus body to a string and writes it once
(`MetricsEndpoints.cs:62-68`); it assigns no streaming content type, is mapped outside `/api`, and
holds no producer. `IdempotencyReplayResult` replays a cached body verbatim including its recorded
content type (`IdempotencyEndpointFilters.cs:1347-1353`), so a replayed hit on a streaming route
returns that media type as one buffered write; it reads the type from the cache rather than a
literal, produces nothing, and ends immediately. Neither is a live stream and neither can be
quiesced, because there is nothing running to stop.

Each discovery is normalised to a `StreamingIdentity` of relative path, enclosing type, enclosing
member with arity, construct kind, and a fingerprint, exactly as `AcquisitionIdentity` is. A
hand-authored catalog pairs each identity with its route pattern, endpoint name, framing, authority,
class, and — where the class is not `GrimoireQuiesceableStream` — an exact proof of why not.

`Validate` reports a typed failure for a discovery with no catalog entry, a catalog entry no scanner
finds, a duplicate on either side, a wildcard identity, a quiesceable entry whose route is not one
of the five, and a non-quiesceable entry with no proof. Both directions fail, which is what makes it
bidirectional rather than an allow-list.

The catalog's two shared writers — `SseStreamWriter.PrepareResponse` at `SseStreamWriter.cs:18` and
`InferenceExecuteWriter` at `InferenceExecuteWriter.cs:61` — are catalogued as writer declarations
rather than routes, the way the acquisition inventory catalogues a `MarkedRouteDeclaration`. A writer
serves whichever routes call it and has no route pattern of its own.

That makes fifteen discoveries: thirteen surfaces and two writer declarations.

### 3.6 The complete census

Twelve first-party streaming responses plus the one third-party surface. This is the catalog's
content and the reason "five" is exactly five.

| Route | Class | Authority | Framing |
|---|---|---|---|
| `GET /api/events/daemon` | `GrimoireQuiesceableStream` | `NoGrimoireAuthority` | SSE |
| `GET /api/events/mcp` | `GrimoireQuiesceableStream` | `NoGrimoireAuthority` | SSE |
| `GET /api/events/logs` | `GrimoireQuiesceableStream` | `NoGrimoireAuthority` | SSE |
| `GET /api/sessions/{id:guid}/stream` | `GrimoireQuiesceableStream` | `LiveGrimoire` | SSE |
| `GET /api/apprentices/{id:guid}/chronicle` | `GrimoireQuiesceableStream` | `LiveGrimoire` | SSE |
| `POST /api/intelligence/ping-stream` | `BillableDrain` | `LiveGrimoire` | NDJSON |
| `POST /api/spells/{name}/execute-stream` | `BillableDrain` | `LiveGrimoire` | NDJSON |
| `POST /api/prompts/{id:guid}/execute-stream` | `BillableDrain` | `LiveGrimoire` | NDJSON |
| `POST /api/web/research` | `BillableDrain` | `LiveGrimoire` | NDJSON |
| `POST /v1/chat/completions` (`stream: true`) | `BillableDrain` | `LiveGrimoire` | SSE |
| `GET /api/sessions/{id:guid}/attachments/{attachmentId:guid}/content` | `FiniteDrain` | `LiveGrimoire` | bytes |
| `GET /v1/files/{id}/content` | `FiniteDrain` | `LiveGrimoire` | bytes |
| A2A JSON-RPC server surface (`MapA2A`) | `BillableDrain` | `LiveGrimoire` | SDK-owned |

The three non-quiesceable classes are non-quiesceable for stated reasons rather than by omission. A
`BillableDrain` stream has already spent money by the time maintenance begins and reaches a durable
boundary the operator is paying for; cutting it would bill for an answer nobody receives, and the
parent forbids retrying provider work because maintenance began. A `FiniteDrain` stream ends on its
own in bounded time and holds no producer to cancel. Both are drained by stage one through
completion, which is what a `Finite` lease already does.

### 3.7 What a maintenance window looks like on the wire

For the five: the frame in flight completes, no further frame or keep-alive is written, a terminal
`data: [DONE]` is written, and the response body ends. HTTP status stays `200` because it was sent
with the first byte and cannot be revised — the `503` contract of API §8.31 applies to a request
refused before its endpoint ran, and a stream that had already started is not one.

For the rest: nothing changes. They drain to their own end, exactly as they do today.

## 4. Scope boundaries

**In scope.** The `GrimoireStreamClass` and `GrimoireStreamAuthority` vocabularies; the
`GrimoireStreamRouteMetadata` marker and its attachment to all thirteen catalogued surfaces; the
kind selection in `TryAdmitGrimoireRequest`; `GrimoireStreamQuiescence` and its registration; the
producer/frame token split and the between-frames stop in `SseStreamWriter.StreamAsync`; the
pre-live-phase stops in the session and chronicle routes; the bidirectional inventory, its scanner,
its catalog, and its tests; and the documentation of all of it.

**Out of scope.** Entry weaving (#253), attachment indexing (#254), Saga extraction (#255), the
remaining hosted producers (#256), and full-host race, cross-platform, Native-AOT, coverage, and
umbrella qualification (#257). No change to `/v1` SSE or NDJSON framing, to the OpenAI compatibility
surface, or to what a billable stream does when maintenance begins.

**Unchanged contracts.** No route, request or response DTO, CLI verb, configuration key, database
schema object, or migration. No new `GrimoireRequestKind`, `CovenantResetPhase`, or checkpoint
payload version. No change to the `503` refusal shapes, their wording, their headers, or the
admission stage's position in the pipeline. The `[DONE]` sentinel, the `: connected` comment, the
`live` sentinel, and the keep-alive comment keep their exact bytes.

## 5. Testing strategy

Every behaviour below is observed RED before the production change that makes it GREEN. Ordering is
proved with `TaskCompletionSource` barriers and manual time, never sleeps.

**Kind selection.** Probe-host tests asserting a route marked quiesceable takes a
`QuiesceableStream` lease and an unmarked route takes `Finite`; that an unmarked route is
unaffected; and that the marker is read from endpoint metadata rather than from the path.

**Frame boundary.** `SseStreamWriter` tests holding one `MoveNextAsync` behind a signal, revoking
mid-write, and asserting the in-flight frame's bytes arrive whole; that no frame and no keep-alive
follows the revocation; that the terminal `[DONE]` is the last thing written; and that the producer
token is cancelled while the frame token is not. The existing suite's technique of parking a move
behind a barrier is the model.

**Producer teardown.** Tests asserting the enumerator is disposed after the outstanding move is
observed, that a channel-backed producer's writer completes, and that no detached producer task
survives the response.

**Pre-live phases.** Tests asserting the session route stops replaying between Entries, skips the
`live` sentinel, and still ends with exactly one `[DONE]`; and the same for the chronicle route's
plan and escalation frames.

**Drain.** A gate-level test asserting a transition begun while a quiesceable lease is live now
completes stage one, where the same test with a `Finite` lease times out — the before-and-after of
#251 §3.8.

**Exclusions.** Tests asserting a `BillableDrain` and a `FiniteDrain` route take `Finite` leases,
receive no revocation, and are drained through completion.

**Inventory.** Tests asserting an injected unclassified streaming construct fails on its own; that a
catalog entry naming a vanished construct fails; that the discovered count equals the catalog count;
that exactly five entries are `GrimoireQuiesceableStream` and they are the five named routes; that
every non-quiesceable entry carries a proof; and that every catalogued route pattern exists in the
composed host's `EndpointDataSource`.

Full-host erasure races across all five streams at once, cross-platform behaviour, Native-AOT
publication, and coverage gates remain #257's.

## 6. Documentation

`docs/Arcanum.DESIGN.md` §10.7.1 gains the third SSE-writer invariant and loses the "two invariants"
count; §10.20.3 replaces the sentence that names this gap and the paragraph that says every request
takes a finite lease; §11.9's in-flight-refusal sentence is qualified for a response that has already
started; §11.16 and §5.7 record what a quiesced session and chronicle stream send; §4.4 adds the
watcher terminal condition; and §13.7 gains the inventory row.

`docs/Arcanum.API.md` §8.31 gains a subsection classifying every streaming route and stating the
five's frame-boundary contract; §8.11, §8.13, §8.16 and the §1 wire-shape table cross-reference it.

`README.md`'s local-first bullet notes that an open watcher no longer blocks a deletion.

`docs/Arcanum.Engineering.md` records the second bidirectional inventory beside the first.

Tracker issue numbers appear only in `docs/superpowers/**`, `README.md`,
`docs/Arcanum.Engineering.md`, and `docs/Arcanum.OATH.md`.

## 7. Review and delivery

Each task runs its own focused suites. On completion: a warning-free Release solution build, the
changed-file style check, the full `RetroDownfall.Arcanum.Tests` suite, one bounded review, then
`--no-ff` merge into `grimoire-fixes`, push, delete the child branch, and move #252 to Done. #257
alone owns parent-wide qualification.
