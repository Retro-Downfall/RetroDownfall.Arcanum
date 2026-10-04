using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The Wizard turn lifecycle: typed failures on every exit, the run status each exit records, and the
/// cleanup a cancelled turn leaves behind.
/// </summary>
public sealed partial class WizardIntelligenceProviderTests
{
    /// <summary>
    /// R-050: a buffered turn that cannot begin its Grimoire turn because another turn holds the
    /// Session reports the writer's typed <c>Hub.SessionTurnBusy</c> (409), not the generic
    /// <c>Hub.Error</c> the drain falls back to when the attempt sets no terminal result.
    /// </summary>
    [Fact]
    public async Task ExecutePromptAsync_SessionTurnBusy_ReturnsTypedConflict()
    {
        Guid sessionId = Guid.Parse("05000000-0000-0000-0000-000000000050");

        SessionTurnConcurrencyGate turnGate = new();

        GrimoireTurnWriter holdingWriter = new(
            new FakeGrimoireRepository(),
            new FakeSessionTurnBeginStore(),
            new SessionEventHub(NullLogger<SessionEventHub>.Instance),
            NullLogger<GrimoireTurnWriter>.Instance,
            sessionTurnGate: turnGate);

        Result<GrimoireTurnWriter.TurnHandle> held = await holdingWriter.BeginBufferedAssistantReplyAsync(
            BaseRequest() with { SessionId = sessionId },
            InvocationContexts.AttendedSession(),
            "first turn",
            ModelName,
            CancellationToken.None);

        Assert.True(held.IsSuccess);

        ScriptingChatClient chat = new();

        chat.EnqueueText("never sent");

        WizardIntelligenceProvider wizard = CreateWizard(chat, sessionTurnGate: turnGate);

        try
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
                BaseRequest() with
                {
                    Prompt = "second turn",
                    SessionId = sessionId,
                    SkipSpellRouting = true,
                    DisableMcpTools = true,
                },
                InvocationContexts.AttendedSession(),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Hub.SessionTurnBusy, result.Error.Code);

            Assert.Equal(0, chat.BufferedCallCount);
        }
        finally
        {
            _ = await holdingWriter.ResolveInterruptedAndMarkFinalizedAsync(
                held.Value,
                streamedContent: null,
                CancellationToken.None);
        }
    }

    /// <summary>
    /// R-054: a streamed input-guardrail refusal carries the guardrail's typed code in the error
    /// frame's <c>Data</c>, as the buffered route already returns it.
    /// </summary>
    [Fact]
    public async Task StreamPromptAsync_GuardrailInputBlocked_CarriesGuardrailCode()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("should not be reached");

        ArcanumSettings settings = ConfigureGuardrails(
            DefaultSettings(),
            enabled: true,
            detectPii: true);

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            guardrailsPipeline: CreateGuardrailsPipeline(settings));

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with
            {
                Prompt = "Email me at alice@example.com",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            });

        IntelligenceEvent error = Assert.Single(events);

        Assert.Equal(IntelligenceEventType.Error, error.Type);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, error.Data);

        Assert.Equal(0, chat.StreamingCallCount);
    }

    /// <summary>
    /// R-054: a streamed turn that cannot begin because another turn holds its Session carries
    /// <c>Hub.SessionTurnBusy</c> in the error frame, so a streaming client can tell a busy Session
    /// from a failed one.
    /// </summary>
    [Fact]
    public async Task StreamPromptAsync_SessionTurnBusy_CarriesSessionTurnBusyCode()
    {
        Guid sessionId = Guid.Parse("05000000-0000-0000-0000-000000000054");

        SessionTurnConcurrencyGate turnGate = new();

        GrimoireTurnWriter holdingWriter = new(
            new FakeGrimoireRepository(),
            new FakeSessionTurnBeginStore(),
            new SessionEventHub(NullLogger<SessionEventHub>.Instance),
            NullLogger<GrimoireTurnWriter>.Instance,
            sessionTurnGate: turnGate);

        Result<GrimoireTurnWriter.TurnHandle> held = await holdingWriter.BeginStreamedAssistantReplyAsync(
            BaseRequest() with { SessionId = sessionId },
            InvocationContexts.AttendedSession(),
            "first turn",
            ModelName,
            CancellationToken.None);

        Assert.True(held.IsSuccess);

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("never sent");

        WizardIntelligenceProvider wizard = CreateWizard(chat, sessionTurnGate: turnGate);

        try
        {
            List<IntelligenceEvent> events = await CollectStreamAsync(
                wizard,
                BaseRequest() with
                {
                    Prompt = "second turn",
                    SessionId = sessionId,
                    SkipSpellRouting = true,
                    DisableMcpTools = true,
                });

            IntelligenceEvent error = Assert.Single(
                events,
                static evt => evt.Type == IntelligenceEventType.Error);

            Assert.Equal(ErrorCodes.Hub.SessionTurnBusy, error.Data);

            Assert.Equal(0, chat.StreamingCallCount);
        }
        finally
        {
            _ = await holdingWriter.ResolveInterruptedAndMarkFinalizedAsync(
                held.Value,
                streamedContent: null,
                CancellationToken.None);
        }
    }

    /// <summary>
    /// R-273: a turn that ends on a provider failure records its run as <c>Failed</c>. The status
    /// started out as <c>Abandoned</c> and only a few exits overwrote it, so most failures read as
    /// if the caller had walked away.
    /// </summary>
    [Fact]
    public async Task ExecutePromptAsync_ProviderFailure_CompletesRunAsFailed()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueException(new InvalidOperationException("the provider refused the request"));

        RecordingTurnRunWriter runs = new();

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            turnRunWriter: runs,
            budgetReservationService: new RecordingBudgetReservationService());

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "fail", SkipSpellRouting = true, DisableMcpTools = true },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        (Guid _, InferenceRunStatus status) = Assert.Single(runs.CompletedRuns);

        Assert.Equal(InferenceRunStatus.Failed, status);
    }

    /// <summary>
    /// R-273: a turn the caller cancels records its run as <c>Abandoned</c>, the one exit that
    /// status is reserved for.
    /// </summary>
    [Fact]
    public async Task ExecutePromptAsync_Cancelled_CompletesRunAsAbandoned()
    {
        using CancellationTokenSource caller = new();

        ScriptingChatClient chat = new();

        chat.EnqueueBufferedResponder(async token =>
        {
            await caller.CancelAsync();

            token.ThrowIfCancellationRequested();

            return new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, "unreachable"));
        });

        RecordingTurnRunWriter runs = new();

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            turnRunWriter: runs,
            budgetReservationService: new RecordingBudgetReservationService());

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "cancel", SkipSpellRouting = true, DisableMcpTools = true },
            InvocationContexts.AttendedSession(),
            caller.Token));

        (Guid _, InferenceRunStatus status) = Assert.Single(runs.CompletedRuns);

        Assert.Equal(InferenceRunStatus.Abandoned, status);
    }

    /// <summary>
    /// R-273: a turn that answers records its run as <c>Completed</c>.
    /// </summary>
    [Fact]
    public async Task ExecutePromptAsync_Success_CompletesRunAsCompleted()
    {
        ScriptingChatClient chat = new();

        chat.EnqueueText("an answer");

        RecordingTurnRunWriter runs = new();

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            turnRunWriter: runs,
            budgetReservationService: new RecordingBudgetReservationService());

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "answer", SkipSpellRouting = true, DisableMcpTools = true },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        (Guid _, InferenceRunStatus status) = Assert.Single(runs.CompletedRuns);

        Assert.Equal(InferenceRunStatus.Completed, status);
    }

    /// <summary>
    /// R-271: once a tool has returned, its effect has happened, so recording the interaction is
    /// bookkeeping that must survive the client disconnecting right after. It ran on the cancellable
    /// inference token and was lost with the turn.
    /// </summary>
    [Fact]
    public async Task StreamPromptAsync_ClientCancelsAfterToolReturns_StillRecordsToolInteraction()
    {
        const string toolName = "finish_then_disconnect";

        Guid sessionId = Guid.Parse("27100000-0000-0000-0000-000000000001");

        using CancellationTokenSource caller = new();

        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = sessionId,
            ThrowWhenCancelled = true,
        };
        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall(toolName, "call-271");

        chat.EnqueueStreamTokens("never reached");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(AIFunctionFactory.Create(
            () =>
            {
                caller.Cancel();

                return "tool effect done";
            },
            toolName,
            "completes its effect, then the client disconnects"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, grimoire: grimoire, mcp: mcp);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (IntelligenceEvent _ in wizard.StreamPromptAsync(
                BaseRequest() with { Prompt = "run the tool", SkipSpellRouting = true },
                InvocationContexts.AttendedSession(),
                caller.Token))
            {
            }
        });

        FakeGrimoireRepository.RecordedToolInteraction recorded = Assert.Single(grimoire.ToolInteractions);

        Assert.Equal(sessionId, recorded.SessionId);

        Assert.Equal(toolName, recorded.ToolName);

        Assert.Contains("tool effect done", recorded.Result, StringComparison.Ordinal);
    }

    /// <summary>
    /// R-271: a cancellation that lands after the answer is finalized does not undo the answer, so
    /// the Session projection still counts the turn and the run is still recorded as completed.
    /// </summary>
    [Fact]
    public async Task StreamPromptAsync_CancelAfterFinalize_StillMarksRunCompleted()
    {
        Guid sessionId = Guid.Parse("27100000-0000-0000-0000-000000000002");

        using CancellationTokenSource caller = new();

        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = sessionId,
            ThrowWhenCancelled = true,
        };
        grimoire.OnFinalize = caller.Cancel;

        ScriptingChatClient chat = new();

        chat.EnqueueStreamTokens("finalized ", "answer");

        RecordingTurnRunWriter runs = new();

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            grimoire: grimoire,
            turnRunWriter: runs,
            budgetReservationService: new RecordingBudgetReservationService());

        try
        {
            await foreach (IntelligenceEvent _ in wizard.StreamPromptAsync(
                BaseRequest() with { Prompt = "answer", SkipSpellRouting = true, DisableMcpTools = true },
                InvocationContexts.AttendedSession(),
                caller.Token))
            {
            }
        }
        catch (OperationCanceledException)
        {
            // The caller is gone; what matters is what the turn left behind.
        }

        Assert.Equal("finalized answer", grimoire.LastFinalizedContent);

        Assert.Equal(sessionId, grimoire.LastIncrementedSessionId);

        (Guid _, InferenceRunStatus status) = Assert.Single(runs.CompletedRuns);

        Assert.Equal(InferenceRunStatus.Completed, status);
    }

    /// <summary>
    /// R-277: a caller that cancels mid-stream leaves nothing in flight. The scripted provider
    /// cancels the caller itself right after its first token, so the cancellation lands at one known
    /// point rather than racing a wall-clock timer, and the turn must then have resolved its assistant
    /// entry with the partial answer, closed its run as abandoned, handed back its unspent
    /// reservation, and released the Session for the next turn.
    /// </summary>
    [Fact]
    public async Task StreamPromptAsync_CancelledMidStream_ResolvesTurnReleasesLeaseAndClosesRun()
    {
        Guid sessionId = Guid.Parse("27700000-0000-0000-0000-000000000001");

        using CancellationTokenSource caller = new();

        SessionTurnConcurrencyGate turnGate = new();

        FakeGrimoireRepository grimoire = new() { FixedSessionId = sessionId };

        ScriptingChatClient chat = new();

        chat.EnqueueStreamResponder(token => TokenThenCallerCancels("tok", caller, token));

        RecordingTurnRunWriter runs = new();

        RecordingBudgetReservationService reservations = new();

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            grimoire: grimoire,
            turnRunWriter: runs,
            budgetReservationService: reservations,
            sessionTurnGate: turnGate);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (IntelligenceEvent _ in wizard.StreamPromptAsync(
                BaseRequest() with { Prompt = "cancel", SkipSpellRouting = true, DisableMcpTools = true },
                InvocationContexts.AttendedSession(),
                caller.Token))
            {
            }
        });

        // The assistant entry was resolved with what had streamed, not left empty and in flight.
        // Whether the consumer saw the token frame before the cancellation reached it is a race
        // between the producer and the projection channel, so only the durable side is asserted.
        Assert.Equal("tok", grimoire.LastFinalizedContent);

        // The run closed exactly once, as abandoned, and the unspent reservation went back.
        (Guid _, InferenceRunStatus status) = Assert.Single(runs.CompletedRuns);

        Assert.Equal(InferenceRunStatus.Abandoned, status);

        Assert.True(reservations.WasReleased);

        Assert.Equal(0, reservations.ReconcileCount);

        // The Session lease is free: the next turn on the same Session is admitted.
        GrimoireTurnWriter nextWriter = new(
            new FakeGrimoireRepository(),
            new FakeSessionTurnBeginStore(),
            new SessionEventHub(NullLogger<SessionEventHub>.Instance),
            NullLogger<GrimoireTurnWriter>.Instance,
            sessionTurnGate: turnGate);

        Result<GrimoireTurnWriter.TurnHandle> next = await nextWriter.BeginBufferedAssistantReplyAsync(
            BaseRequest() with { SessionId = sessionId },
            InvocationContexts.AttendedSession(),
            "next turn",
            ModelName,
            CancellationToken.None);

        Assert.True(next.IsSuccess);

        Assert.True(await nextWriter.ResolveInterruptedAndMarkFinalizedAsync(
            next.Value,
            streamedContent: null,
            CancellationToken.None));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TokenThenCallerCancels(
        string token,
        CancellationTokenSource caller,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, token);

        await caller.CancelAsync();

        cancellationToken.ThrowIfCancellationRequested();
    }
}
