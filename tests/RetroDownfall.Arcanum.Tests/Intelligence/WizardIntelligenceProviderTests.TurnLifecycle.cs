using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

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
}
