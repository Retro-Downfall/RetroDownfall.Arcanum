# U04 (Arcanum.Api, Intelligence (4)) — reliability review

Unit: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs` (8,904 lines; the single file of U04).
Tree: worktree `.claude/worktrees/codebase-review` at `ac0d2d8e`, read-only. Probes ran in a scratch worktree
`scratchpad/review/wt/U04-reliability` (detached at `ac0d2d8e`, removed afterwards).

Note on inputs: `scratchpad/review/units.json` was missing when this run started (the scratchpad was empty). The unit was
recovered from the session transcript of the unit-generation step, which printed `U04 8904 Arcanum.Api | Intelligence (4) | 1 files`;
the only Api file of exactly 8,904 lines is `WizardIntelligenceProvider.cs`. The sibling partial
`WizardIntelligenceProvider.ContextPreview.cs` belongs to a different unit and was not reviewed.

Production composition facts the findings rely on (verified):

- `IProviderHealthTracker` is always registered (`ServiceCollectionExtensions.AddArcanumResilience`, line 1830, called from
  `AddArcanumInfrastructure`, which `ApiBootstrapper` line 470 calls). `ActivatorUtilities.CreateInstance<WizardIntelligenceProvider>`
  (ApiBootstrapper 650) fills the optional parameter, so **every production turn runs the resilience paths**:
  `ExecutePromptWithFallbackAsync` (buffered) and the commit-gated candidate loop in `StreamPromptCoreAsync` (streaming, lines 1193-1559).
  The non-resilience paths (lines 705-749 and 1085-1191) are reached only by tests that omit the tracker.
- `CovenantDispatchGate` and `CampaignRollupTurnPreparer` are registered scoped (ApiBootstrapper 587, 642), so session-backed
  tool-capable turns in a canonical context are live-eligible for a durable turn claim (`requireProtectedToolPersistence`,
  WizardIntelligenceProvider 1789-1794; `CampaignRollupTurnPreparer.IsLiveEligible`).

## Findings

### F1 (Minor) — Claimed turns store the generic `Hub.SessionTurnRestoredInterrupted`/409 terminal outcome for most failures, so an idempotent retry replays 409 instead of the turn's real error; buffered and streaming disagree

Where: `WizardIntelligenceProvider.cs` 3759-3764 (buffered provider failure), 3297-3302 (buffered context/cost gate), 3339-3344
(buffered Covenant gate); also every other early resolution that passes no `interruptionOutcome`: 2032, 2305, 2365, 2737, 2820, 2870,
3033, 4627, the `finally` at 4861-4867 (structured-output failure, finalize failure, exceptions), `ResolveDeferredTurnAsync` 1581, and
`CampaignRollupTurnSnapshot.DisposeAsync` (`CampaignRollupTurnPreparer.cs` 144-148) for a claim that never began.

What is wrong: only one site records the turn's own typed error in the claim — the shared `inferenceError` block at 4298-4313, which
builds `SessionTurnClaimOutcome.RestoredInterrupted(terminalInferenceError.Code, ResolveStatusCode(code), [])`. In buffered mode the
three early resolves above run first and mark the handle finalized, so the later typed call is a no-op (`ResolveInterruptedAndMarkFinalizedAsync`
returns on `IsFinalized`). `GrimoireTurnWriter.ResolveInterruptedAsync` (206-207) then substitutes the default
`RestoredInterrupted(Hub.SessionTurnRestoredInterrupted, 409)` and completes the claim `Discarded(Hub.SessionTurnRestoredInterrupted, 409)`.
A retry under the same Idempotency-Key reaches the claim (API.md line 294: claimed turns "bypass generic response-body replay and check the claim's authoritative native answer on every retry"), and
`CampaignRollupTurnPreparer.ReplayAsync` (700-703) returns `new Error(lease.Claim.Outcome?.TerminalErrorCode …)` — i.e. 409
"restored interrupted" for a turn that actually failed with a provider error, `Hub.ContextBudgetExceeded`, `Budget.Exceeded`, a guardrail
block, etc. `SessionTurnClaimOutcome.RestoredInterrupted` is documented as the recovery outcome ("Recovery found the turn interrupted"),
and DESIGN.md line 2230 (durable claims) says the writer "commits the terminal result or typed interrupted outcome".
The schema comment on `session_turn_claims` says Discarded/RestoredInterrupted "reconstruct their typed error envelope from these
immutable public fields".

Proof (probe P2, executed): a claimed session turn (real `SessionTurnClaimStore` via the Task7 fixture) whose provider throws
`InvalidOperationException`; run once buffered and once streaming, each followed by a retry under the same accepted identity:

```
buffered:  result=Hub.Error claim=Hub.SessionTurnRestoredInterrupted/409 replay=Hub.SessionTurnRestoredInterrupted
streaming: result=Hub.Error claim=Hub.Error/500                         replay=Hub.Error
```

Failure scenario: an API client issues a buffered `POST /api/intelligence/ping` with an Idempotency-Key on a claimed Session turn (the
documented continue-then-replay disconnect policy, API.md line 402, exists for exactly this retry; the first-party CLI and Forge do not
send a key for turns, so they are not affected);
the provider returns an error (or the context/cost gate refuses). The client's transport drops before it reads the 500/400/429 and it
retries with the same key: it gets 409 `Hub.SessionTurnRestoredInterrupted` ("This turn ended before a complete reply was produced")
for that key forever, instead of the original typed error. The same request streamed replays the correct code. Guardrail, attachment,
spell-routing, client-tool and accounting failures replay 409 in both modes (by trace: those sites call
`ResolveInterruptedAndMarkFinalizedAsync`, or leave it to the `finally`, with no outcome and then exit).

Fix: pass the typed outcome everywhere the turn resolves — one local helper `ResolveFailedTurnAsync(Error error, string? partial)` that
builds `SessionTurnClaimOutcome.RestoredInterrupted(error.Code, ArcanumErrorMapper.ResolveStatusCode(error.Code), [])`; drop the three
buffered early resolves (3297, 3339, 3759) and let the 4298 block resolve (it already applies the deferral rule); have `FailureFrameAsync`
remember the error so the `finally` resolves with it; give `CampaignRollupTurnSnapshot` a `RecordTerminal(Error)` the wizard sets before
the scope disposes. Add a test per mode asserting the claim's `TerminalErrorCode` equals the returned error code. Effort: M.

### F2 (Minor) — In production the "Finishing protected memory maintenance..." status reaches the streaming client only after the maintenance has run; the only test exercises a path production never uses

Where: `WizardIntelligenceProvider.cs` 1819-1832 (status + acknowledgement) interacting with the commit gate 1365-1405 and
`IsPreCommitStreamingEvent` 1635-1639; test `WizardIntelligenceProviderTests.LongRestStatus.cs` (first test).

What is wrong: API.md line 401 (streaming protected-maintenance status frame) promises one `status` frame "before the first paid protected work" and a wait "up to 5 seconds for
the writer to flush it". The iterator yields the Status and acknowledges in the `finally` of that yield, i.e. when its consumer asks for
the next frame. In production the consumer is the resilience loop of `StreamPromptCoreAsync`, which classifies `Status` as pre-commit,
puts it in `preCommitBuffer` and immediately calls `MoveNextAsync` again — so the preparer is released at once and the protected
maintenance runs while the status sits in the buffer. The buffer is flushed only when the provider commits (first answer token) or the
attempt fails. The 5-second hold therefore never waits for the client, and the client sees the status after the maintenance it announces.
The existing test builds the wizard without a health tracker, so it runs the non-resilience path and passes. This is the general
shape of the wizard suite: 7 of the 240 `CreateWizard(...)` call sites in `WizardIntelligenceProviderTests*.cs` pass a health tracker,
so almost every streaming scenario there is verified on a path production never takes (only `WizardIntelligenceProviderFallbackTests`
targets the resilience loop).

Proof (probe P1, executed): the existing test body, run with and without `healthTracker: new ProviderHealthTracker(...)`:
```
resilience=False: sendsWhenStatusArrived=0 totalSends=3 statusIndex=1 firstTokenIndex=5
resilience=True:  sendsWhenStatusArrived=3 totalSends=3 statusIndex=1 firstTokenIndex=5
order (both) = Status,Status,SessionBound,ConversationBound,Context,Token,Result
```

Without the tracker (the path the existing test covers) the client is told before any of the 3 paid maintenance sends; with it
(production) the client is told after all 3. The frame order is identical, which is why an order-only assertion cannot see the defect;
only the timing relative to the paid work differs.

Fix: treat this status as client-visible before commit — e.g. yield it through the gate unbuffered (a `Status` that carries a
"flush-now" marker, or a separate pre-commit-but-emit set), or move the acknowledgement out of the inner iterator into the outer
loop after it has yielded the buffered frame. Re-run the LongRestStatus test with a health tracker (or make `CreateWizard` default to
one, matching production). Effort: S.

### F3 (Minor) — Native `responseFormat` is matched case-insensitively for the provider wire but case-sensitively for host validation, so `"JSON_SCHEMA"` skips strict schema enforcement silently

Where: `WizardIntelligenceProvider.cs` 8159-8191 (`ApplyInferenceParameters` lower-cases), 3077 and 4325 (`request.ResponseFormat is "json_schema"`).

What is wrong: the native routes bind `PingRequest.ResponseFormat` verbatim (no normalisation in `CanonicalPingResolution` or the bounds
validator; only `/v1` and the CLI normalise). A value such as `"JSON_SCHEMA"` with a `strict: true` schema sends the schema to the provider
but skips Arcanum's buffered strict mode, the validation, and the corrective retry; an invalid answer returns as success with no warning.

Proof (probe P3, executed):
```
json_schema: success=True calls=2 text={"name":"accepted"} warnings=1
JSON_SCHEMA: success=True calls=1 text={"attempt":0} warnings=0
```

The lower-case request was validated, corrected and warned; the upper-case one returned the schema-invalid first answer as a clean
success. (The first-party CLI and `/v1` normalise the value; the Forge Scriptorium passes it trimmed but not lower-cased, though without a
schema; the defect is reachable from any native API client.)

Fix: normalise once (trim + lower-invariant) at the top of the turn, or compare with `string.Equals(..., OrdinalIgnoreCase)` at 3077 and
4325; or reject unknown spellings in `PingRequestBoundsValidator`. Effort: S.

## Checked and found sound

- Fallback ownership of the Grimoire turn (DESIGN §10.7.2): `ShouldDeferTurnToNextCandidate` (1599) and the outer retry predicates
  (909-911 buffered, 1449-1454 streaming) agree (`canFallBack == !isLastAttempt`, connectivity, uncommitted, no text); adoption at 1919
  skips finalized handles; `ResolveDeferredTurnAsync` runs from both loops' `finally`. Traced every early exit of both loops. Existing
  `WizardIntelligenceProviderFallbackTests` (29) pass.
- Lease and enumerator disposal on every branch of both streaming paths (lines 1121, 1173-1178, 1306-1311, 1337-1339, 1356-1358,
  1386-1389, 1425-1427, 1470-1472, 1517-1522); router/extractor fast-model leases disposed in `finally` (5493, 5653).
- Cancellation: caller OCE is filtered on `callerToken` everywhere it is rethrown; provider timeouts (OCE with caller live) are classified
  as connectivity (ProviderFailureClassifier); post-finalize bookkeeping, tool-interaction persistence, usage recording and cleanup all
  use `CancellationToken.None` (3454, 3788, 4111, 4220, 4304, 4738, 4748, 4770, 4838).
- Abandoned tool calls: bounded 3 s grace with timer cancellation, fault observer for a still-running task, exactly-once late recording
  (4155-4236); documented limitation for tools that outlive the grace (DESIGN 1725).
- `PumpWardAndHumanEventsAsync`: single held human wait, no busy spin after cancellation or channel completion, ward channel completed
  in the tool task's own `finally`.
- AsyncLocal handling across yields (R-008): ambients captured on `TurnAmbientSet` and re-applied inside `ProcessWithLiveWardsAsync`;
  `TurnAccountingAmbient` published and read within one iterator segment (2318-2456 has no yield) so auxiliary usage is recorded.
- Buffered terminal coverage: every buffered `yield break` sets `classification.BufferedTerminal` (enumerated every failure exit reachable in buffered mode).
- Accounting status in the `finally` (4817-4848): Failed/Abandoned/Completed classification matches the comments; completion errors are
  swallowed with a type-only log.
- Covenant scope ownership: one scope per logical run, disposed after the deferred-turn backstop in both buffered and streaming paths.
- Restart loops are bounded: ordinary-cache fallback at most once per attempt (2566-2586), no-tools restart at most once (3735).
- Integer arithmetic: saturating clamps for usage, token estimates and covenant pressure (8223-8298, 8740, 3163).
- Scrying base64 is validated before the direct-attachment decode at 2111 (`ScryingValidator` via `PingRequestPreflightValidator`).
- Attachment re-preparation on a fallback candidate is idempotent (`PersistNewWithOutcomeAsync` de-duplicates).
- RAG/Lexicon/Tapestry/Saga retrieval degrade on failure and rethrow only caller cancellation; SQL in `JoinWorkspaceChunkMetadataAsync`
  and `GetTotalChunksByPathAsync` is parameterised and bounded by the clamped `maxChunks`.
- `GrimoireTurnWriter.TryFinalizeAssistantEntryCoreAsync` throws after commit only through `MemoryDelegationPublication.Token`, which is
  `CancellationToken.None` unless delegation protection is admitted, so the "Completed once durable" rule (DESIGN 1753) holds for
  ordinary turns.

Considered and not reported: the spell-routing fast-model lease resolution (5443) is not wrapped like Lexicon's (5590-5652), but
`ChatClientFactory`/`ProviderApiKeyResolver` report credential problems as results and `ConfigurationValidator` rejects an unknown
`FastModel`, so no realistic throw was found. A pre-commit *exception* escaping the inner attempt is classified by the outer loop with
`IsConnectivityFailure(ex)` while the inner did not defer the turn; the only reachable source found was the MCP manager's process-lifetime
token during shutdown, so it is noted, not reported.

## Probes run

1. `ReviewProbeU04_P1…` (new test, scratch worktree): protected maintenance status timing with/without health tracker. Result above.
2. `ReviewProbeU04_P2…` (new test): claim terminal code and replay code, buffered vs streaming. Result above.
3. `ReviewProbeU04_P3…` (new test): `"json_schema"` vs `"JSON_SCHEMA"` with a strict schema. Result above.
4. Baseline: `WizardIntelligenceProviderFallbackTests` + `WizardIntelligenceProviderTests.LongRestStatus*` in the shared review worktree:
   32 passed, 0 failed. `GrimoireTurnWriterTests` + `CampaignRollupTurnSnapshotTests`: 44 passed. Every
   `Intelligence.WizardIntelligenceProvider*` test class: 468 passed, 0 failed (so the findings are not caught by any existing test).
   `SessionTurnClaim*` + `LongRestTurnPreparation*`: 91 passed.

The scratch worktree was removed after the probes; the shared review worktree was not modified (`git status` clean).

Probe source: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.ReviewProbeU04.cs` in the scratch worktree
(reproduced in the appendix below).

## Appendix — probe source

```csharp
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Tests.LongRest;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

// REVIEW PROBES (U04 reliability). Each probe asserts the behaviour the code exhibits today; a pass
// demonstrates the defect described in its name.
public sealed partial class WizardIntelligenceProviderTests
{
    [Fact]
    public async Task ReviewProbeU04_P1_production_streaming_path_tells_the_client_about_protected_maintenance_only_after_it_ran()
    {
        List<string> report = [];

        foreach (bool resilience in new[] { false, true })
        {
            await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

            await Task7ParkAsync(f);

            await using Task7ProtectedPreparation h = await Task7ProtectedPreparation.CreateAsync(f);

            Task7SuccessfulMaintenanceResponses(f, h);

            ScriptingChatClient chat = new();

            chat.EnqueueText("{\"entities\":[]}");

            chat.EnqueueStreamTokens("answer after protected maintenance");

            WizardIntelligenceProvider wizard = CreateWizard(chat, f.Options.CurrentValue,
                new FakeGrimoireRepository { Session = new Session { Id = h.Request.SessionId!.Value } }, db: f.H.Saga.Context,
                ordinaryConnections: FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), campaignRollups: h.Preparer,
                claimedBeginStore: h.Begin, claims: h.Claims, turnCommitter: h.Repository,
                healthTracker: resilience ? new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance) : null);

            ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(f.Invocation.Surface, f.Scope,
                f.Invocation.Attendance, f.Invocation.ContextPolicy, ToolPolicy.NoTools, f.Invocation.ReadAuthorityEpoch).Value;

            int sendsWhenStatusArrived = -1;

            int framesBeforeStatus = 0;

            List<IntelligenceEvent> events = [];

            await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(h.Request, invocation, CancellationToken.None))
            {
                if (evt.Type == IntelligenceEventType.Status && evt.Message == ProtectedMaintenanceStatus)
                {
                    sendsWhenStatusArrived = f.Handler.SendCount;

                    framesBeforeStatus = events.Count;
                }

                events.Add(evt);
            }

            Assert.Contains(events, static evt => evt.Type == IntelligenceEventType.Status && evt.Message == ProtectedMaintenanceStatus);

            Assert.True(f.Handler.SendCount > 0);

            int firstToken = events.FindIndex(static e => e.Type == IntelligenceEventType.Token);

            report.Add($"resilience={resilience}: sendsWhenStatusArrived={sendsWhenStatusArrived} totalSends={f.Handler.SendCount} statusIndex={framesBeforeStatus} firstTokenIndex={firstToken} order={string.Join(",", events.Select(e => e.Type))}");

            if (!resilience)
            {
                // The path the existing LongRestStatus test covers: told before any paid maintenance send.
                Assert.Equal(0, sendsWhenStatusArrived);
            }
            else
            {
                // The production path (IProviderHealthTracker is always registered): every paid
                // maintenance send has already happened when the client first sees the status.
                Assert.True(sendsWhenStatusArrived == f.Handler.SendCount && sendsWhenStatusArrived > 0,
                    $"resilience: sends when status arrived {sendsWhenStatusArrived}, total sends {f.Handler.SendCount}, frames before status {framesBeforeStatus}, kinds {string.Join(",", events.Select(e => e.Type))}");
            }
        }

        Assert.Fail("REPORT " + string.Join(" | ", report));
    }

    [Fact]
    public async Task ReviewProbeU04_P3_native_response_format_case_variant_skips_host_schema_validation()
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """, AdHocJson.Options);

        List<string> report = [];

        foreach (string format in new[] { "json_schema", "JSON_SCHEMA" })
        {
            ScriptingChatClient chat = new();

            chat.EnqueueText("""{"attempt":0}""");

            chat.EnqueueText("""{"name":"accepted"}""");

            WizardIntelligenceProvider wizard = CreateWizard(chat);

            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
                BaseRequest() with
                {
                    Prompt = "return structured output",
                    ResponseFormat = format,
                    ResponseFormatJsonSchema = StrictJsonSchema(schema),
                    SkipSpellRouting = true,
                    DisableMcpTools = true,
                },
                InvocationContexts.AttendedSession(),
                CancellationToken.None);

            report.Add($"{format}: success={result.IsSuccess} calls={chat.BufferedCallCount} text={(result.IsSuccess ? result.Value.Text : result.Error.Code)} warnings={(result.IsSuccess ? result.Value.Warnings.Count : -1)} responseFormatSent={chat.LastChatOptions?.ResponseFormat?.GetType().Name}");
        }

        Assert.Fail("REPORT " + string.Join(" | ", report));
    }

    [Fact]
    public async Task ReviewProbeU04_P2_buffered_claimed_turn_records_the_generic_interruption_code_not_the_turn_error()
    {
        Dictionary<string, (string ResultCode, string? ClaimCode, long? ClaimStatus, string? ReplayCode)> observed = [];

        foreach (bool streaming in new[] { false, true })
        {
            await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

            f.Options.Set(f.Options.CurrentValue with { Features = f.Options.CurrentValue.Features with { Lexicon = false } });

            await using Task7ProtectedPreparation h = await Task7ProtectedPreparation.CreateAsync(f);

            f.Handler.InsideSend = () =>
            {
                f.Handler.ChatText = "{\"summary\":\"Accepted summary.\"}";

                return Task.CompletedTask;
            };

            ScriptingChatClient chat = new();

            chat.EnqueueException(new InvalidOperationException("scripted provider failure"));

            chat.EnqueueImmediateStreamFailure(new InvalidOperationException("scripted provider failure"));

            WizardIntelligenceProvider wizard = CreateWizard(chat, f.Options.CurrentValue,
                new FakeGrimoireRepository { Session = new Session { Id = h.Request.SessionId!.Value } }, db: f.H.Saga.Context,
                ordinaryConnections: FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), campaignRollups: h.Preparer,
                claimedBeginStore: h.Begin, claims: h.Claims, turnCommitter: h.Repository);

            ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(f.Invocation.Surface, f.Scope,
                f.Invocation.Attendance, f.Invocation.ContextPolicy, ToolPolicy.NoTools, f.Invocation.ReadAuthorityEpoch).Value;

            byte[] body = JsonSerializer.SerializeToUtf8Bytes(h.Request, ArcanumJsonContext.Default.PingRequest);

            string resultCode;

            string? replayCode = null;

            try
            {
                TurnIdempotencyAmbient.PublishIdentity(Convert.ToHexString(SHA256.HashData("U04 probe key"u8)),
                    Convert.ToHexString(SHA256.HashData(body)));

                if (!streaming)
                {
                    Result<PromptTurnResult> first = await wizard.ExecutePromptAsync(h.Request, invocation, CancellationToken.None);

                    Assert.True(first.IsFailure);

                    resultCode = first.Error.Code;

                    Result<PromptTurnResult> replay = await wizard.ExecutePromptAsync(h.Request, invocation, CancellationToken.None);

                    replayCode = replay.IsFailure ? replay.Error.Code : "success";
                }
                else
                {
                    List<IntelligenceEvent> events = [];

                    await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(h.Request, invocation, CancellationToken.None)) events.Add(evt);

                    IntelligenceEvent error = Assert.Single(events, static e => e.Type == IntelligenceEventType.Error);

                    resultCode = error.Data ?? "(none)";

                    List<IntelligenceEvent> replayEvents = [];

                    await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(h.Request, invocation, CancellationToken.None)) replayEvents.Add(evt);

                    replayCode = replayEvents.FirstOrDefault(static e => e.Type == IntelligenceEventType.Error)?.Data ?? "no-error-frame";
                }
            }
            finally
            {
                TurnIdempotencyAmbient.Clear();
            }

            await using SqliteCommand command = f.H.Connection.CreateCommand();

            command.CommandText = "SELECT TerminalErrorCode, TerminalHttpStatus FROM session_turn_claims WHERE SessionId = $session;";

            command.Parameters.AddWithValue("$session", h.Request.SessionId!.Value.ToString("D").ToUpperInvariant());

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync(), "no claim row");

            observed[streaming ? "streaming" : "buffered"] = (resultCode,
                reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), replayCode);
        }

        string report = string.Join(" | ", observed.Select(static kv => $"{kv.Key}: result={kv.Value.ResultCode} claim={kv.Value.ClaimCode}/{kv.Value.ClaimStatus} replay={kv.Value.ReplayCode}"));

        // Streaming stores the turn's own error; buffered stores the generic restored-interrupted code.
        Assert.True(observed["streaming"].ClaimCode == observed["streaming"].ResultCode, report);

        Assert.True(observed["buffered"].ClaimCode == ErrorCodes.Hub.SessionTurnRestoredInterrupted
            && observed["buffered"].ResultCode != ErrorCodes.Hub.SessionTurnRestoredInterrupted, report);

        Assert.Fail("REPORT " + report);
    }
}
```
