# Issue #252 Streaming-Route Inventory and Frame-Boundary Quiescence Implementation Plan

**Goal:** Give the five declared SSE routes a `QuiesceableStream` lease and teach them to end at a
complete frame boundary when maintenance revokes it, so a Covenant erasure begun while a watcher is
connected drains instead of timing out — and close the set with a bidirectional inventory that fails
on any streaming route it has never been told about.

**Architecture:** One endpoint-metadata marker naming each streaming route's class; a kind selection
in the existing admission stage that reads it; a per-request quiescence type that splits the producer
token from the frame-write token; a between-frames stop and one terminal `[DONE]` inside
`SseStreamWriter.StreamAsync`; the same between-frames stop in the two routes that write frames
before the live loop; and a Roslyn scanner plus hand-authored catalog that fails in both directions.

**Tech Stack:** .NET 10, C# 13, ASP.NET Core endpoint metadata and middleware, Native-AOT
source-generated JSON, `Microsoft.CodeAnalysis.CSharp` syntax analysis in tests, xUnit,
`TaskCompletionSource` barriers, Git, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-05-issue-252-streaming-quiescence-design.md`

## Global Constraints

- Work on `codex/issue-252-streaming-quiescence`, based on `grimoire-fixes` commit `cae3a7f4`, and
  merge only into `grimoire-fixes`.
- Follow RED → GREEN → REFACTOR in each task. Ordering tests use `TaskCompletionSource` barriers and
  manual time, never sleeps.
- Add no route, request/response DTO, CLI verb, configuration key, schema object, or migration. Add
  no `GrimoireRequestKind` member, no `CovenantResetPhase` member, no checkpoint payload version.
- Do not change the `503` refusal shapes, their wording, their headers, or the admission stage's
  position in the pipeline. #251's contract is fixed.
- Do not change `/v1` SSE or NDJSON framing. `OpenAiV1Endpoints` and `InferenceExecuteWriter` gain no
  quiescence behaviour — a billable stream is never maintenance-cancelled.
- The exact bytes of `data: [DONE]\n\n`, `: keep-alive\n\n`, `: connected\n\n`, and
  `data: {"type":"live"}\n\n` are unchanged.
- Revocation may reach a producer token and may never reach a frame-write token. Any new
  `CreateLinkedTokenSource` that combines them fails review.
- Preserve Native-AOT rules: no new payload type, no reflection-based serialization, endpoint
  handlers stay RDG-compatible.
- New test classes that construct or inject `ArcanumWebApplicationFactory` carry
  `[Collection("ApiHost")]`. Never mutate the shared collection fixture's overrides.
- Tracker issue numbers may appear only in `docs/superpowers/**`, `README.md`,
  `docs/Arcanum.Engineering.md`, and `docs/Arcanum.OATH.md`.
- Zero Release build warnings. C# house style: one blank line after each line of code, file-scoped
  namespaces, positional records, primary constructors for DI.
- Run only child-scoped focused tests during development. Coverage, complete suites, Native AOT/IL,
  benchmark, native SQLCipher provenance, packaging, full-host, cross-platform, and parent-wide
  qualification remain #257's.

## File and responsibility map

- `src/RetroDownfall.Arcanum.Api/Streaming/GrimoireStreamRouteMetadata.cs` — new; the class
  vocabulary, the authority vocabulary, and the endpoint marker.
- `src/RetroDownfall.Arcanum.Api/Streaming/GrimoireStreamQuiescence.cs` — new; the per-request
  producer/frame token split.
- `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs` — kind selection in `TryAdmitGrimoireRequest`;
  the quiescence type's scoped registration.
- `src/RetroDownfall.Arcanum.Api/Streaming/SseStreamWriter.cs` — the linked producer token, the
  between-frames stop, and the one terminal `[DONE]`.
- `src/RetroDownfall.Arcanum.Api/Streaming/EventEndpoints.cs` — the marker and the quiescence
  argument on all three event routes; the skipped `: connected` comment.
- `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs` — the marker on the stream route, the
  replay and buffered-drain stops, the skipped `live` sentinel, and the marker on the attachment
  content route.
- `src/RetroDownfall.Arcanum.Api/Conclave/ApprenticeEndpoints.cs` — the marker on the chronicle
  route, the replay and buffered-drain stops.
- `src/RetroDownfall.Arcanum.Api/Intelligence/IntelligenceEndpoints.cs`,
  `src/RetroDownfall.Arcanum.Api/Tower/PromptEndpoints.cs`,
  `src/RetroDownfall.Arcanum.Api/Tower/SpellExecutionEndpoints.cs`,
  `src/RetroDownfall.Arcanum.Api/Intelligence/WebWorkflowEndpoints.cs`,
  `src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs`,
  `src/RetroDownfall.Arcanum.Api/OpenAiV1FilesEndpoints.cs`,
  `src/RetroDownfall.Arcanum.Api/A2A/A2AServerEndpoints.cs` — the non-quiesceable markers.
- `tests/RetroDownfall.Arcanum.Tests/Support/GrimoireStreamingRouteInventory.cs` — new; the scanner,
  the identity, the catalog, and the validator.
- `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireStreamingRouteInventoryTests.cs` — new; the
  bidirectional inventory suite.
- `tests/RetroDownfall.Arcanum.Tests/Api/Streaming/SseStreamWriterQuiescenceTests.cs` — new; the
  frame-boundary contract.
- `tests/RetroDownfall.Arcanum.Tests/Api/Middleware/GrimoireRequestAdmissionTests.cs` — extended;
  kind selection and the drain difference.
- `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireAdmissionRouteInventoryTests.cs` — extended; the
  composed-host marker inventory.
- Documentation: `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`,
  `docs/Arcanum.Engineering.md`, `README.md`.

---

### Task 1: The class vocabulary and the route marker

**Files:** new `Api/Streaming/GrimoireStreamRouteMetadata.cs`, plus tests in the new
`GrimoireStreamingRouteInventoryTests`.

- [ ] RED: tests asserting `GrimoireStreamClass` declares exactly `GrimoireQuiesceableStream`,
      `FiniteDrain`, and `BillableDrain` with literal codes; that `GrimoireStreamAuthority` declares
      exactly `LiveGrimoire` and `NoGrimoireAuthority`; that neither has a zero member; and that
      `GrimoireStreamRouteMetadata` exposes its class and cannot be constructed with an undefined one.
- [ ] GREEN: add the two enums and the marker, each with the summary/remarks the house voice
      requires, and the four cached singletons the routes attach.
- [ ] Run the new filter GREEN.
- [ ] `git commit -m "feat: name what each streaming route is, on the route itself"`.

---

### Task 2: Admission selects the kind from the route

**Files:** `Api/ApiBootstrapper.cs`, `tests/.../Api/Middleware/GrimoireRequestAdmissionTests.cs`.

- [ ] RED: probe-host tests asserting a route carrying the quiesceable marker takes a
      `QuiesceableStream` lease; that a route carrying a `FiniteDrain` or `BillableDrain` marker takes
      `Finite`; that an unmarked route takes `Finite`; and that an exempt route still takes nothing.
      The lease kind is observed through the gate rather than inferred from behaviour.
- [ ] RED: a test asserting that beginning a transition revokes the quiesceable lease's
      `MaintenanceRevocation` and leaves a finite lease's token unsignalled.
- [ ] GREEN: replace the constant `GrimoireRequestKind.Finite` with the metadata-driven selection.
      Keep the method synchronous — the `AsyncLocal` lifetime rule its remarks record is unchanged.
- [ ] Run `GrimoireRequestAdmissionTests`, `GrimoireAdmissionRouteInventoryTests`,
      `GrimoireRequestAdmissionScopeTests` GREEN.
- [ ] `git commit -m "feat: let a route say which admission kind it needs"`.

---

### Task 3: The producer/frame token split

**Files:** new `Api/Streaming/GrimoireStreamQuiescence.cs`, `Api/ApiBootstrapper.cs`, plus tests.

- [ ] RED: tests asserting a request with no lease reports `IsQuiescing` false and a `Revocation` that
      is never signalled; that a request holding a quiesceable lease reports the lease's token; that a
      request holding a finite lease reports an unsignalled token even after a transition begins; that
      `LinkProducer` returns a source cancelled by either the caller's token or revocation; and that
      the type never exposes a token that combines revocation with a caller's frame token.
- [ ] GREEN: add the type and register it scoped beside `GrimoireRequestAdmissionScope`.
- [ ] Run the new filter GREEN.
- [ ] `git commit -m "feat: separate the token that stops a producer from the one that writes a frame"`.

---

### Task 4: `SseStreamWriter` ends between frames

**Files:** `Api/Streaming/SseStreamWriter.cs`, new
`tests/.../Api/Streaming/SseStreamWriterQuiescenceTests.cs`.

- [ ] RED: a test parking one `MoveNextAsync` behind a barrier, revoking while a frame write is in
      flight, and asserting the in-flight frame's bytes arrive whole and terminated.
- [ ] RED: tests asserting no frame and no keep-alive is written after revocation; that the terminal
      `data: [DONE]\n\n` is the last thing written and is written exactly once; that revocation before
      the first `MoveNextAsync` writes `[DONE]` and no frame at all; that the producer's token is
      cancelled while the frame token is not; and that the enumerator is disposed only after the
      outstanding move is observed.
- [ ] RED: a test asserting an ordinary end-of-stream (producer completes on its own) still writes no
      `[DONE]` from the writer, so the existing routes' cancellation arms keep their meaning.
- [ ] GREEN: add the `GrimoireStreamQuiescence` parameter; link the producer token; test
      `IsQuiescing` before each `MoveNextAsync` and after each frame write; keep `writeFrameAsync` and
      `WriteKeepAliveAsync` on the unrevoked token; write the terminal frame on the quiesced break
      only. Apply the same treatment to the heartbeat-free branch.
- [ ] Run `SseStreamWriterQuiescenceTests` and the existing `SseStreamWriterTests` GREEN.
- [ ] `git commit -m "fix: finish the frame in hand, then stop"`.

---

### Task 5: The three event routes

**Files:** `Api/Streaming/EventEndpoints.cs`, plus tests.

- [ ] RED: probe/integration tests asserting each of `/api/events/daemon`, `/api/events/mcp`, and
      `/api/events/logs` carries the quiesceable marker; that a revoked stream ends with `[DONE]` and
      no partial frame; and that the `: connected` comment is not written when the request is already
      quiescing at entry.
- [ ] GREEN: attach the marker to all three; resolve `GrimoireStreamQuiescence`; pass it to
      `StreamAsync`; guard the `: connected` write.
- [ ] Run the event-route filter GREEN.
- [ ] `git commit -m "feat: end the three event streams at a frame boundary"`.

---

### Task 6: The session stream and its pre-live phases

**Files:** `Api/Tower/SessionEndpoints.cs`, plus tests.

- [ ] RED: tests asserting the route carries the quiesceable marker; that replay stops between
      Entries when revocation arrives mid-replay and writes no partial Entry frame; that the `live`
      sentinel is skipped when quiescing; that the buffered drain is skipped; that exactly one
      `[DONE]` ends the response; and that the pump task is cancelled and observed.
- [ ] GREEN: attach the marker; link revocation into `pumpCts`; add the between-frames tests to both
      replay loops and the buffered drain; guard the sentinel; pass quiescence to `StreamAsync`.
      Fall through to `StreamAsync` rather than returning early, so the terminal frame stays in one
      place.
- [ ] Run `SessionEndpointTests` and the new session quiescence filter GREEN.
- [ ] `git commit -m "feat: stop a session stream between entries, not inside one"`.

---

### Task 7: The apprentice chronicle and its replay

**Files:** `Api/Conclave/ApprenticeEndpoints.cs`, plus tests.

- [ ] RED: tests asserting the route carries the quiesceable marker; that the plan, escalation, and
      step-start replay frames stop at a boundary; that the buffered drain is skipped; that exactly
      one `[DONE]` ends the response; and that `pumpTask` is cancelled and awaited.
- [ ] GREEN: attach the marker; link revocation into `pumpCts`; add the between-frames tests; pass
      quiescence to `StreamAsync`.
- [ ] Run `ApprenticeEndpointTests` and the new chronicle filter GREEN.
- [ ] `git commit -m "feat: stop a chronicle between frames, not inside one"`.

---

### Task 8: The non-quiesceable markers

**Files:** `Api/Intelligence/IntelligenceEndpoints.cs`, `Api/Tower/PromptEndpoints.cs`,
`Api/Tower/SpellExecutionEndpoints.cs`, `Api/Intelligence/WebWorkflowEndpoints.cs`,
`Api/OpenAiV1Endpoints.cs`, `Api/OpenAiV1FilesEndpoints.cs`, `Api/Tower/SessionEndpoints.cs`,
`Api/A2A/A2AServerEndpoints.cs`, plus tests.

- [ ] RED: tests asserting each of the eight non-quiesceable surfaces carries a marker naming its
      class; that each takes a `Finite` lease; that none receives revocation when a transition begins;
      and that a billable stream in flight is drained through completion rather than cut.
- [ ] GREEN: attach `BillableDrain` to the five inference/research surfaces and the A2A mapping, and
      `FiniteDrain` to the two content routes. No handler behaviour changes.
- [ ] Run the exclusion filter GREEN.
- [ ] `git commit -m "feat: say why a stream is not quiesceable, on the stream"`.

---

### Task 9: The bidirectional inventory

**Files:** new `tests/.../Support/GrimoireStreamingRouteInventory.cs`, new
`tests/.../Api/GrimoireStreamingRouteInventoryTests.cs`.

- [ ] RED: a test injecting a synthetic uncatalogued streaming construct and asserting it fails on its
      own with `UncataloguedDiscovery`; and one asserting a catalog entry naming a construct the
      scanner no longer finds fails with `StaleCatalogEntry`.
- [ ] RED: tests asserting duplicates on either side fail; that a wildcard identity fails; that a
      quiesceable entry naming a route outside the five fails; and that a non-quiesceable entry with
      no proof fails.
- [ ] RED: tests asserting the scanner discovers exactly fifteen constructs; that the catalog has
      exactly fifteen entries; that exactly five are `GrimoireQuiesceableStream` and they are the five
      named route patterns; and that every catalogued route pattern resolves to a real endpoint in the
      composed host's `EndpointDataSource`.
- [ ] GREEN: add the scanner, the identity, the failure vocabulary, the catalog, and the validator.
- [ ] Run the inventory filter GREEN.
- [ ] `git commit -m "test: close the streaming surface against a route nobody classified"`.

---

### Task 10: The drain that now completes

**Files:** `tests/.../Api/Middleware/GrimoireRequestAdmissionTests.cs`.

- [ ] RED: a barrier-driven test asserting that a transition begun while a quiesceable stream is live
      completes stage one, where the same scenario with a finite lease reports
      `Grimoire.WorkDrainTimeout`. This is the before-and-after #251 §3.8 predicted, and it is the
      test that proves the whole child.
- [ ] GREEN: no production change expected; if one is needed the earlier tasks were incomplete.
- [ ] Run the admission filter GREEN.
- [ ] `git commit -m "test: prove an open watcher no longer refuses an erasure"`.

---

### Task 11: Documentation

**Files:** `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `docs/Arcanum.Engineering.md`,
`README.md`.

- [ ] DESIGN §10.7.1: the third SSE-writer invariant; correct the "two invariants" count.
- [ ] DESIGN §10.20.3: replace the sentence naming this gap and the finite-lease paragraph.
- [ ] DESIGN §11.9: qualify the in-flight-refusal sentence for a response that has already started.
- [ ] DESIGN §11.16 and §5.7: what a quiesced session and chronicle stream send.
- [ ] DESIGN §4.4: the watcher's new terminal condition.
- [ ] DESIGN §13.7: the inventory row.
- [ ] API §8.31: the streaming classification subsection and the frame-boundary contract; cross-refs
      from §8.11, §8.13, §8.16 and the §1 wire-shape table.
- [ ] Engineering: the second bidirectional inventory beside the first.
- [ ] README: an open watcher no longer blocks a deletion.
- [ ] `git commit -m "docs: record what a maintenance window does to a stream"`.

---

### Task 12: Review and delivery

- [ ] Warning-free Release solution build.
- [ ] `./scripts/align-csharp-blanklines.sh` over changed files; no diff.
- [ ] Full `RetroDownfall.Arcanum.Tests` suite GREEN.
- [ ] One bounded read-only review; fix Critical and Important findings.
- [ ] Merge `--no-ff` into `grimoire-fixes`, push, delete the child branch.
- [ ] Move issue #252 to Done and close it.
