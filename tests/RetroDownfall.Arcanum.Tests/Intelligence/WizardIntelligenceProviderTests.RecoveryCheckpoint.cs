using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [SkippableFact]
    public async Task Buffered_session_parks_the_pending_question_before_another_provider_call()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string question = "Which branch should the repair preserve?";

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall("ask_human", "retained-question", new() { ["question"] = question });

        chat.EnqueueText("The model continued after a synthetic denial.");

        await using var host = RecoveryHost(chat, DefaultProvider(), new InMemoryOsCredentialStore());

        using HttpClient client = host.CreateAuthenticatedClient();

        Guid sessionId = await CreateRecoverySessionAsync(client);

        PingRequest ping = RepairRequest(sessionId);

        using HttpResponseMessage response = await SendRecoveryPingAsync(client, ping);

        Assert.Equal(1, chat.BufferedCallCount);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponsePromptResponseDto);

        Assert.Equal(ErrorCodes.Hub.SessionRecoveryInputRequired, body?.Error?.Code);

        var checkpoint = Assert.IsType<SessionRecoveryCheckpoint>(body?.Error?.FailureEvidence?.RecoveryCheckpoint);

        Assert.Equal(question, checkpoint.Question);

        Assert.Equal(sessionId, checkpoint.SessionId);

        Assert.Equal(checkpoint, await ReadRecoveryCheckpointAsync(client, sessionId));
    }

    [SkippableTheory]
    [InlineData("ask_human", false)]
    [InlineData("ask_human", true)]
    [InlineData("recovery_effect", false)]
    public async Task Canceled_proposal_preserves_denial_without_admitting_new_work(string toolName, bool streaming)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using CancellationTokenSource caller = new();

        ScriptingChatClient chat = new();

        FunctionCallContent proposal = new("returned-proposal", toolName, new Dictionary<string, object?>
        {
            ["question"] = "Which branch should the repair preserve?",
        });

        async IAsyncEnumerable<ChatResponseUpdate> ReturnStreamingProposalAsync()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [proposal]);

            await caller.CancelAsync();
        }

        chat.EnqueueStreamResponder(_ => ReturnStreamingProposalAsync());

        chat.EnqueueBufferedResponder(async _ =>
        {
            await caller.CancelAsync();

            return new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, [proposal]));
        });

        bool dispatched = false;

        AIFunction effect = AIFunctionFactory.Create(() =>
        {
            dispatched = true;

            return "dispatched";
        }, "recovery_effect", "Test recoverable work.");

        await using var host = RecoveryHost(chat, DefaultProvider(), new InMemoryOsCredentialStore(),
            liveQuestion: streaming, otherTool: effect);

        using HttpClient client = host.CreateAuthenticatedClient();

        Guid session = await CreateRecoverySessionAsync(client);

        await using var services = host.Services.CreateAsyncScope();

        var provider = services.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>();

        PingRequest request = RepairRequest(session);

        ArcanumInvocationContext invocation = ArcanumInvocationContexts.ForSessionTurn(
            new DefaultHttpContext { RequestServices = services.ServiceProvider }, request, CanonicalCampaignContext.GlobalOnly);

        TurnIdempotencyAmbient.PublishIdentity(new string('A', 64), new string('B', 64));

        string? failureCode = null;

        try
        {
            if (streaming)
            {
                await foreach (IntelligenceEvent frame in provider.StreamPromptAsync(request, invocation, caller.Token))
                {
                    Assert.NotEqual(IntelligenceEventType.ToolCall, frame.Type);
                }
            }
            else
            {
                var outcome = await provider.ExecutePromptAsync(request, invocation, caller.Token);

                Assert.True(outcome.IsFailure);

                failureCode = outcome.Error.Code;
            }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }

        Assert.True(caller.IsCancellationRequested, failureCode);

        Assert.Equal(streaming ? 1 : 0, chat.StreamingCallCount);

        Assert.Equal(streaming ? 0 : 1, chat.BufferedCallCount);

        Assert.Contains(chat.LastChatOptions!.Tools!, tool => tool is AIFunction function && function.Name == toolName);

        Assert.False(dispatched);

        var checkpoint = Assert.IsType<SessionRecoveryCheckpoint>(await ReadRecoveryCheckpointAsync(client, session));

        Assert.Equal(SessionRecoveryKind.Interrupted, checkpoint.Kind);

        Assert.Equal(TurnRecoveryAction.ResumeSession, checkpoint.NextAction);

        Assert.Null(checkpoint.Question);

        Assert.Null(checkpoint.InvocationId);

        var entries = await services.ServiceProvider.GetRequiredService<IGrimoireRepository>().GetSessionEntriesAsync(session);

        Assert.NotNull(entries);

        Assert.Equal(toolName == "ask_human" && !streaming ? 1 : 0, entries.Count(entry =>
            entry.Content.Contains("ask_human is only available", StringComparison.Ordinal)));
    }

    [SkippableFact]
    public async Task Restarted_session_accepts_guidance_once_under_the_saved_route_and_workspace()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string question = "Which branch should the repair preserve?";

        const string guidance = "Preserve main and continue the repair.";

        const string originalCall = "repair-question-before-restart";

        await using RestartableArcanumProfileFixture profile = new();

        InMemoryOsCredentialStore credentials = new();

        ProviderSettings provider = DefaultProvider();

        string workspace = Directory.CreateDirectory(Path.Combine(profile.TempHome, "workspace")).FullName;

        string otherWorkspace = Directory.CreateDirectory(Path.Combine(profile.TempHome, "other-workspace")).FullName;

        ScriptingChatClient before = new();

        before.EnqueueToolCall("ask_human", originalCall, new() { ["question"] = question });

        Guid sessionId;

        SessionRecoveryCheckpoint pending;

        await using (var host = RecoveryHost(before, provider, credentials, profile))
        {
            using HttpClient client = host.CreateAuthenticatedClient();

            sessionId = await CreateRecoverySessionAsync(client);

            using HttpResponseMessage interrupted = await SendRecoveryPingAsync(client,
                RepairRequest(sessionId) with { WorkingDirectory = workspace });

            Assert.Equal(HttpStatusCode.Conflict, interrupted.StatusCode);

            pending = Assert.IsType<SessionRecoveryCheckpoint>(await ReadRecoveryCheckpointAsync(client, sessionId));

            Assert.Equal(question, pending.Question);
        }

        ScriptingChatClient after = new();

        after.EnqueueText("I continued on main.");

        await using var restarted = RecoveryHost(after, provider, credentials, profile);

        using HttpClient continuation = restarted.CreateAuthenticatedClient();

        Assert.Equal(pending, await ReadRecoveryCheckpointAsync(continuation, sessionId));

        Guid anotherSession = await CreateRecoverySessionAsync(continuation);

        PingRequest reply = RepairRequest(sessionId) with { Prompt = guidance, Model = null, RecoveryCheckpointId = pending.Id };

        using HttpResponseMessage wrongSession = await SendRecoveryPingAsync(continuation, reply with { SessionId = anotherSession });

        Assert.Equal(HttpStatusCode.Conflict, wrongSession.StatusCode);

        using HttpResponseMessage wrongWorkspace = await SendRecoveryPingAsync(continuation, reply with { WorkingDirectory = otherWorkspace });

        Assert.Equal(HttpStatusCode.Conflict, wrongWorkspace.StatusCode);

        using HttpResponseMessage changedRoute = await SendRecoveryPingAsync(continuation, reply with { Model = "unknown-model" });

        var changed = await changedRoute.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponsePromptResponseDto);

        Assert.Equal(ErrorCodes.Hub.SessionRecoveryRouteChanged, changed?.Error?.Code);

        Assert.Equal(0, after.BufferedCallCount);

        string acceptedKey = Guid.NewGuid().ToString("D");

        using HttpResponseMessage completed = await SendRecoveryPingAsync(continuation, reply, acceptedKey);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        using HttpResponseMessage duplicate = await SendRecoveryPingAsync(continuation, reply, acceptedKey);

        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        Assert.Equal(await completed.Content.ReadAsStringAsync(), await duplicate.Content.ReadAsStringAsync());

        using HttpResponseMessage consumed = await SendRecoveryPingAsync(continuation, reply);

        Assert.Equal(HttpStatusCode.Conflict, consumed.StatusCode);

        Assert.Equal(1, after.BufferedCallCount);

        Assert.Null(await ReadRecoveryCheckpointAsync(continuation, sessionId));

        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> providerInput = Assert.Single(after.AllBufferedCalls);

        FunctionCallContent call = Assert.Single(providerInput.SelectMany(message => message.Contents).OfType<FunctionCallContent>());

        FunctionResultContent result = Assert.Single(providerInput.SelectMany(message => message.Contents).OfType<FunctionResultContent>());

        Assert.Equal(originalCall, call.CallId);

        Assert.Equal(call.CallId, result.CallId);

        Assert.Equal(question, call.Arguments!["question"]);

        Assert.Equal(guidance, providerInput.Last(message => message.Role == ChatRole.User).Text);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_question_uses_the_existing_response_route_or_is_retained_when_cancelled(bool cancel)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string question = "Should the repair preserve main?";

        const string answer = "Preserve main.";

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall("ask_human", "live-question", new() { ["question"] = question });

        chat.EnqueueStreamTokens("Understood.");

        await using var host = RecoveryHost(chat, DefaultProvider(), new InMemoryOsCredentialStore(), liveQuestion: true);

        using HttpClient client = host.CreateAuthenticatedClient();

        Guid session = await CreateRecoverySessionAsync(client);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));

        using CancellationTokenSource cancelTurn = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);

        using var request = RecoveryRequest("/api/intelligence/ping-stream", RepairRequest(session), keyed: !cancel);

        using HttpResponseMessage streaming = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancelTurn.Token);

        using StreamReader reader = new(await streaming.Content.ReadAsStreamAsync(timeout.Token));

        IntelligenceEvent asked = await ReadRecoveryFrameAsync(reader,
            frame => frame is { Type: IntelligenceEventType.ToolCall, ToolCall.Name: "ask_human" }, timeout.Token);

        using JsonDocument args = JsonDocument.Parse(asked.ToolCall!.ArgumentsJson);

        string promptId = args.RootElement.GetProperty("promptId").GetString()!;

        Assert.NotEmpty(promptId);

        if (cancel)
        {
            await cancelTurn.CancelAsync();

            streaming.Dispose();

            SessionRecoveryCheckpoint? checkpoint = null;

            while (checkpoint is null)
            {
                checkpoint = await ReadRecoveryCheckpointAsync(client, session);

                if (checkpoint is null) await Task.Delay(25, timeout.Token);
            }

            Assert.Equal(SessionRecoveryKind.AwaitingInput, checkpoint.Kind);

            Assert.Equal(question, checkpoint.Question);

            Assert.Equal(TurnRecoveryAction.ProvideInput, checkpoint.NextAction);

            Assert.Equal(1, chat.StreamingCallCount);

            string? sourceTerminal = null;

            while (sourceTerminal is null)
            {
                sourceTerminal = await ReadRecoveryTerminalErrorAsync(host.Services, checkpoint.Id, timeout.Token);

                if (sourceTerminal is null) await Task.Delay(25, timeout.Token);
            }

            Assert.Equal(ErrorCodes.Hub.SessionRecoveryCancelled, sourceTerminal);

            chat.EnqueueText("The cancelled repair continued under the new claim.");

            using HttpResponseMessage continued = await SendRecoveryPingAsync(client,
                RepairRequest(session) with { Prompt = answer, RecoveryCheckpointId = checkpoint.Id });

            Assert.Equal(HttpStatusCode.OK, continued.StatusCode);

            Assert.Null(await ReadRecoveryCheckpointAsync(client, session));

            Assert.Equal(sourceTerminal, await ReadRecoveryTerminalErrorAsync(host.Services, checkpoint.Id, timeout.Token));

            Assert.Equal(1, chat.StreamingCallCount);

            Assert.Equal(1, chat.BufferedCallCount);

            return;
        }

        using HttpResponseMessage response = await client.PostAsync("/api/intelligence/human-response",
            JsonContent.Create(new SubmitHumanResponseRequest(promptId, answer), ArcanumJsonContext.Default.SubmitHumanResponseRequest),
            timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await ReadRecoveryFrameAsync(reader, frame => frame.Type == IntelligenceEventType.Result, timeout.Token);

        Assert.Equal(2, chat.StreamingCallCount);

        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> resumed = chat.AllStreamingCalls[1];

        FunctionResultContent result = Assert.Single(resumed.SelectMany(message => message.Contents).OfType<FunctionResultContent>());

        Assert.Equal("live-question", result.CallId);

        Assert.Contains(answer, Assert.IsType<string>(result.Result), StringComparison.Ordinal);

        Assert.Null(await ReadRecoveryCheckpointAsync(client, session));
    }

    [SkippableFact]
    public async Task Cancelled_dispatched_tool_requires_receipt_reconciliation_and_does_not_start_another_call()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall("recovery_effect", "dispatched-before-cancel");

        chat.EnqueueStreamTokens("This provider round must not run.");

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        AIFunction tool = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "unreachable";
        }, "recovery_effect", "an operation with no receipt");

        await using var host = RecoveryHost(chat, DefaultProvider(), new InMemoryOsCredentialStore(), otherTool: tool);

        using HttpClient client = host.CreateAuthenticatedClient();

        Guid session = await CreateRecoverySessionAsync(client);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));

        using CancellationTokenSource cancelTurn = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);

        using var request = RecoveryRequest("/api/intelligence/ping-stream", RepairRequest(session), keyed: false);

        using HttpResponseMessage streaming = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancelTurn.Token);

        using StreamReader reader = new(await streaming.Content.ReadAsStreamAsync(timeout.Token));

        await ReadRecoveryFrameAsync(reader, frame => frame is { Type: IntelligenceEventType.ToolCall, ToolCall.Name: "recovery_effect" }, timeout.Token);

        await started.Task.WaitAsync(timeout.Token);

        await cancelTurn.CancelAsync();

        streaming.Dispose();

        SessionRecoveryCheckpoint? checkpoint = null;

        while (checkpoint is null)
        {
            checkpoint = await ReadRecoveryCheckpointAsync(client, session);

            if (checkpoint is null) await Task.Delay(25, timeout.Token);
        }

        Assert.Equal(SessionRecoveryKind.ReconciliationRequired, checkpoint.Kind);

        Guid receipt = Assert.IsType<Guid>(checkpoint.InvocationId);

        using HttpResponseMessage lookup = await client.GetAsync($"/api/sessions/{session:D}/tool-invocations/{receipt:D}", timeout.Token);

        var invocation = await lookup.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseToolInvocationEvidence, timeout.Token);

        Assert.Equal(ToolInvocationState.Dispatched, invocation?.Data?.State);

        using HttpResponseMessage rejected = await SendRecoveryPingAsync(client,
            RepairRequest(session) with { Prompt = "Continue.", RecoveryCheckpointId = checkpoint.Id });

        var rejection = await rejected.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponsePromptResponseDto, timeout.Token);

        Assert.Equal(ErrorCodes.Hub.ToolReconciliationRequired, rejection?.Error?.Code);

        Assert.Equal(1, chat.StreamingCallCount);

        Assert.Equal(0, chat.BufferedCallCount);
    }

    [SkippableFact]
    public async Task Exact_key_reconnects_to_the_running_turn_and_replays_the_retained_result()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall("retained_work", "running-owner");

        chat.EnqueueStreamTokens("Completed by the original turn owner.");

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int calls = 0;

        AIFunction tool = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return "existing work result";
        }, "retained_work", "A tool with a controlled wait.");

        await using var host = RecoveryHost(chat, DefaultProvider(), new InMemoryOsCredentialStore(), otherTool: tool);

        using HttpClient client = host.CreateAuthenticatedClient();

        Guid session = await CreateRecoverySessionAsync(client);

        string key = Guid.NewGuid().ToString("D");

        string path = "/api/intelligence/ping-stream";

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));

        using CancellationTokenSource disconnect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);

        using var initial = RecoveryRequest(path, RepairRequest(session), key);

        using HttpResponseMessage first = await client.SendAsync(initial, HttpCompletionOption.ResponseHeadersRead, disconnect.Token);

        using StreamReader reader = new(await first.Content.ReadAsStreamAsync(timeout.Token));

        await ReadRecoveryFrameAsync(reader, frame => frame is { Type: IntelligenceEventType.ToolCall, ToolCall.Name: "retained_work" }, timeout.Token);

        await started.Task.WaitAsync(timeout.Token);

        await disconnect.CancelAsync();

        first.Dispose();

        using var duplicate = RecoveryRequest(path, RepairRequest(session), key);

        Task<HttpResponseMessage> response = client.SendAsync(duplicate, timeout.Token);

        try
        {
            Assert.False(response.IsCompleted);

            Assert.Equal(1, chat.StreamingCallCount);
        }
        finally
        {
            release.TrySetResult();
        }

        using HttpResponseMessage replay = await response;

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using StreamReader replayFrames = new(await replay.Content.ReadAsStreamAsync(timeout.Token));

        await ReadRecoveryFrameAsync(replayFrames, frame => frame.Type == IntelligenceEventType.Result, timeout.Token);

        Assert.Equal(2, chat.StreamingCallCount);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    private static async Task<IntelligenceEvent> ReadRecoveryFrameAsync(StreamReader reader, Func<IntelligenceEvent, bool> predicate, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;

            IntelligenceEvent? frame = JsonSerializer.Deserialize(line, ArcanumJsonContext.Default.IntelligenceEvent);

            if (frame is not null && predicate(frame)) return frame;
        }

        throw new InvalidOperationException("The requested event was not present in the response.");
    }

    private static ArcanumWebApplicationFactory RecoveryHost(ScriptingChatClient chat, ProviderSettings provider,
        InMemoryOsCredentialStore credentials, RestartableArcanumProfileFixture? profile = null,
        bool liveQuestion = false, AIFunction? otherTool = null)
    {
        var host = MemoryErasureRouteDriver.Host(credentials, profile, configure: settings =>
        {
            settings.DefaultModel = ModelName;
            settings.Providers = [provider];
            settings.Features = DefaultSettings().Features with { Embeddings = false, Saga = false, Covenant = false, Reasoning = false };
            settings.Cost.Budget.Enabled = false;
        });

        var previous = host.ServiceOverrides;

        host.ServiceOverrides = services =>
        {
            previous!(services);
            services.RemoveAll<IArcanumIntelligenceProvider>();
            services.AddScoped<IArcanumIntelligenceProvider>(services => services.GetRequiredService<WizardIntelligenceProvider>());
            services.RemoveAll<IChatClientFactory>();
            services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory(chat, provider));
            services.RemoveAll<IMcpConnectionManager>();
            services.AddSingleton<IMcpConnectionManager>(sp =>
            {
                FakeMcpConnectionManager mcp = new();

                mcp.Tools.Add(liveQuestion
                    ? AIFunctionFactory.Create((string question, string promptId, CancellationToken ct) =>
                        sp.GetRequiredService<IHumanPromptRegistry>().AwaitReservedAsync(promptId, ct), "ask_human", "Ask the user.")
                    : CreateMcpTool("ask_human"));

                if (otherTool is not null) mcp.Tools.Add(otherTool);

                return mcp;
            });
        };

        return host;
    }

    private static PingRequest RepairRequest(Guid sessionId) =>
        new("Start a repair and ask only the question needed before writing.", Model: ModelName,
            SessionId: sessionId, SkipSpellRouting: true, MaxOutputTokens: 64);

    internal static async Task<Guid> CreateRecoverySessionAsync(HttpClient client)
    {
        using HttpResponseMessage created = await client.PostAsync("/api/sessions",
            JsonContent.Create(new CreateSessionRequest(null, "Retained repair question"), ArcanumJsonContext.Default.CreateSessionRequest));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var session = await created.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        return Assert.IsType<SessionDetailDto>(session?.Data).Id;
    }

    internal static async Task<HttpResponseMessage> SendRecoveryPingAsync(HttpClient client, PingRequest ping, string? key = null)
    {
        using HttpRequestMessage request = RecoveryRequest("/api/intelligence/ping", ping, key);

        return await client.SendAsync(request);
    }

    internal static HttpRequestMessage RecoveryRequest(string path, PingRequest ping, string? key = null, bool keyed = true)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(ping, ArcanumJsonContext.Default.PingRequest),
        };

        if (keyed) request.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? Guid.NewGuid().ToString("D"));

        return request;
    }

    private static async Task<string?> ReadRecoveryTerminalErrorAsync(IServiceProvider services, Guid claimId, CancellationToken ct)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();

        var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        await using IGrimoireOrdinaryConnectionLease connection = opened.Value;

        await using var command = connection.Connection.CreateCommand();

        command.CommandText = "SELECT TerminalErrorCode FROM session_turn_claims WHERE ClaimId=$id";

        _ = command.Parameters.AddWithValue("$id", claimId.ToString("D").ToUpperInvariant());

        return await command.ExecuteScalarAsync(ct) as string;
    }

    internal static async Task<SessionRecoveryCheckpoint?> ReadRecoveryCheckpointAsync(HttpClient client, Guid sessionId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/sessions/{sessionId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        return Assert.IsType<SessionDetailDto>(body?.Data).RecoveryCheckpoint;
    }
}
