using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [SkippableFact]
    public async Task Cancel_after_a_completed_failing_workspace_check_preserves_the_live_question_and_verified_result()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string question = "Can I proceed using the verified compiler diagnosis?";

        const string answer = "Proceed using the verified diagnosis.";

        int checks = 0;

        StrategyCheckTool check = new(() =>
        {
            Interlocked.Increment(ref checks);

            return new TrustedStructuredToolResult(TrustedStructuredToolResultKind.WorkspaceCheck,
                """{"status":"failed","code":"check_failed","exitCode":1,"diagnostics":[{"severity":"error","code":"CS0029","message":"Cannot convert string to int."}]}""")
            {
                FailureEvidence = new(TurnFailurePhase.ToolExecution, TurnEffectState.Completed, TurnRecoveryAction.InspectWorkspace),
            };
        });

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall("workspace_check", "completed-failing-check", new() { ["profile"] = "dotnet-build" });

        chat.EnqueueStreamToolCall("ask_human", "retained-question", new() { ["question"] = question });

        await using var host = RecoveryHost(chat, DefaultProvider(), new InMemoryOsCredentialStore(), liveQuestion: true, otherTool: check);

        using HttpClient client = host.CreateAuthenticatedClient();

        Guid session = await CreateRecoverySessionAsync(client);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        CancellationToken ct = watchdog.Token;

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var request = RecoveryRequest("/api/intelligence/ping-stream", RepairRequest(session), keyed: false);

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(ct));

        IntelligenceEvent failed = await ReadRecoveryFrameAsync(reader,
            frame => frame is { Type: IntelligenceEventType.ToolResult, ToolCall.Name: "workspace_check" }, ct);

        Assert.Contains("CS0029", failed.Data, StringComparison.Ordinal);

        Assert.Equal(TurnEffectState.Completed, failed.FailureEvidence?.Effect);

        _ = await ReadRecoveryFrameAsync(reader,
            frame => frame is { Type: IntelligenceEventType.ToolCall, ToolCall.Name: "ask_human" }, ct);

        await cancel.CancelAsync();

        response.Dispose();

        SessionRecoveryCheckpoint? checkpoint = null;

        while (checkpoint is null)
        {
            checkpoint = await ReadRecoveryCheckpointAsync(client, session);

            if (checkpoint is null) await Task.Delay(25, ct);
        }

        Guid receipt = Assert.IsType<Guid>(checkpoint.InvocationId ?? checkpoint.LastVerifiedInvocationId);

        var status = await host.Services.GetRequiredService<IToolInvocationStore>().ReadAsync(session, receipt, ct);

        Assert.True(status.IsSuccess, status.IsFailure ? status.Error.Message : null);

        ToolInvocationEvidence invocation = Assert.IsType<ToolInvocationEvidence>(status.Value);

        Console.WriteLine($"Checkpoint={checkpoint.Kind}, next={checkpoint.NextAction}, receipt={receipt:D}, durable state={invocation.State}.");

        Assert.Equal(SessionRecoveryKind.AwaitingInput, checkpoint.Kind);

        Assert.Null(checkpoint.InvocationId);

        Assert.Equal(receipt, checkpoint.LastVerifiedInvocationId);

        Assert.Equal(ToolInvocationState.Completed, invocation.State);

        var exactReceipt = ToolInteractionReceiptDerivation.Derive(new(
            invocation.AssistantEntryId.ToString("D"), "completed-failing-check", invocation.Round, invocation.Call, "workspace_check"));

        Assert.Equal(receipt, exactReceipt.Id);

        Assert.Equal(exactReceipt.ResultEntryId, invocation.ResultEntryId);

        using var scope = host.Services.CreateScope();

        var entries = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

        var checkCall = await entries.GetEntryAsync(session, exactReceipt.CallEntryId, ct);

        var checkResult = await entries.GetEntryAsync(session, exactReceipt.ResultEntryId, ct);

        Assert.NotNull(checkCall);

        Assert.NotNull(checkResult);

        Assert.True(TurnContextGuards.IsPersistedToolExchange(checkCall, checkResult, session));

        Assert.Contains("CS0029", checkResult.Content, StringComparison.Ordinal);

        Assert.Equal(question, checkpoint.Question);

        Assert.Equal(TurnRecoveryAction.ProvideInput, checkpoint.NextAction);

        string? terminal = null;

        while (terminal is null)
        {
            terminal = await ReadRecoveryTerminalErrorAsync(host.Services, checkpoint.Id, ct);

            if (terminal is null) await Task.Delay(25, ct);
        }

        Assert.Equal(ErrorCodes.Hub.SessionRecoveryCancelled, terminal);

        chat.EnqueueText("I retained the check and the answer.");

        using HttpResponseMessage continued = await SendRecoveryPingAsync(client,
            RepairRequest(session) with { Prompt = answer, RecoveryCheckpointId = checkpoint.Id });

        Assert.Equal(HttpStatusCode.OK, continued.StatusCode);

        int historicalCheck = chat.LastBufferedMessages.ToList().FindIndex(message => message.Text == checkCall.Content);

        Assert.InRange(historicalCheck, 0, chat.LastBufferedMessages.Count - 2);

        Assert.Equal(checkResult.Content, chat.LastBufferedMessages[historicalCheck + 1].Text);

        Assert.Contains(chat.LastBufferedMessages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            result => result.CallId == "retained-question");

        Assert.Null(await ReadRecoveryCheckpointAsync(client, session));

        Assert.Equal(1, Volatile.Read(ref checks));

        Assert.Equal(terminal, await ReadRecoveryTerminalErrorAsync(host.Services, checkpoint.Id, ct));
    }
}
