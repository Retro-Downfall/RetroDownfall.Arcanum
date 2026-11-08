using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("WorkspacePathPolicy")]
public sealed partial class GuildCapacityTests
{
    [SkippableFact]
    public async Task Native_blocking_delegation_releases_shared_capacity_and_resumes_parent_at_capacity_one()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        using DelegatingTransport transport = new();

        using CapturedLogProvider logs = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                settings.Execution.MaxConcurrentApprentices = 1;

                settings.DefaultModel = "gpt-5.4-mini-2026-03-17";

                settings.Providers = [new() { Name = "qualified", Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://api.openai.com/v1", Models = ["gpt-5.4-mini-2026-03-17"] }];

                return settings;
            },
            ServiceOverrides = services =>
            {
                services.AddHttpClient("OpenAiCompatibleProvider").ConfigurePrimaryHttpMessageHandler(() => transport);

                services.AddLogging(builder => builder.AddProvider(logs).AddFilter<CapturedLogProvider>(static (_, _) => true));

                services.AddScoped<IGuildLocalInferenceRuntime>(provider =>
                    new RecordingRuntime(provider.GetRequiredService<WizardIntelligenceProvider>(), transport));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Delegating parent", "Use a child to produce the answer",
            new([], [new(GuildToolSourceKind.BuiltIn, "arcanum", "delegate_task")], [],
                new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [GuildDelegationKind.Local], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        string issuedBody = await issued.Content.ReadAsStringAsync();

        Assert.True(issued.StatusCode == HttpStatusCode.Created, issuedBody);

        using JsonDocument accepted = JsonDocument.Parse(issuedBody);

        Guid id = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        try
        {
            await transport.DelegationRequested.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await using AsyncServiceScope diagnostic = factory.Services.CreateAsyncScope();

            var diagnosticOpened = await diagnostic.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, default);

            Assert.True(diagnosticOpened.IsSuccess, diagnosticOpened.Error.Message);

            await using var diagnosticConnection = diagnosticOpened.Value;

            await using var diagnosticCommand = diagnosticConnection.Connection.CreateCommand();

            diagnosticCommand.CommandText = "SELECT group_concat(State || ':' || coalesce(TerminalErrorCode,'none')) FROM (SELECT State,TerminalErrorCode FROM LongRunningOperations LIMIT 8)";

            string snapshot = Convert.ToString(await diagnosticCommand.ExecuteScalarAsync()) ?? "none";

            Assert.Fail($"Parent never reached delegation. Calls={transport.Calls}; operations={snapshot}; runtime={transport.RuntimeDiagnostic}; "
                + string.Join("\n", logs.Entries.Where(entry => entry.Category.Contains("Guild", StringComparison.Ordinal)
                    || entry.Category.Contains("ModelCall", StringComparison.Ordinal)).Select(entry => entry.Message + " " + entry.Exception)));
        }

        // Establish the prerequisite: the tool call was accepted and its child reached the
        // real engine, before blaming an absent child provider call on shared capacity.
        await using (AsyncServiceScope prerequisite = factory.Services.CreateAsyncScope())
        {
            var childOpened = await prerequisite.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, deadline.Token);

            Assert.True(childOpened.IsSuccess, childOpened.Error.Message);

            await using var childConnection = childOpened.Value;

            await using var child = childConnection.Connection.CreateCommand();

            child.CommandText = "SELECT count(*) FROM guild_quests q JOIN LongRunningOperations o ON o.Id=q.OperationId WHERE q.ParentQuestId=$parent AND o.State=1";

            child.Parameters.AddWithValue("$parent", id.ToString("D").ToUpperInvariant());

            try
            {
                while (!Equals(1L, await child.ExecuteScalarAsync(deadline.Token)))
                    await Task.Delay(25, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                child.CommandText = "SELECT group_concat(State || ':' || coalesce(TerminalErrorCode,'none')) FROM (SELECT o.State,o.TerminalErrorCode FROM guild_quests q JOIN LongRunningOperations o ON o.Id=q.OperationId LIMIT 8)";

                string snapshot = Convert.ToString(await child.ExecuteScalarAsync()) ?? "none";

                Assert.Fail($"Delegation did not establish a running child. Calls={transport.Calls}; operations={snapshot}; toolResult={transport.ToolResultDiagnostic}");
            }
        }

        await transport.ChildCalled.Task.WaitAsync(deadline.Token);

        transport.AllowChildReturn.TrySetResult();

        while (true)
        {
            using HttpResponseMessage result = await client.GetAsync($"/api/guild/quests/{id:D}/result", deadline.Token);

            if (result.StatusCode == HttpStatusCode.OK) break;

            Assert.True(result.StatusCode == HttpStatusCode.NotFound, await result.Content.ReadAsStringAsync(deadline.Token));

            await Task.Delay(25, deadline.Token);
        }

        using HttpResponseMessage content = await client.GetAsync($"/api/guild/quests/{id:D}/result/content", deadline.Token);

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await content.Content.ReadAsStringAsync(deadline.Token));

        Assert.Equal("parent finished", body.RootElement.GetProperty("data").GetProperty("body").GetString());

        Assert.Equal(5, transport.Calls);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, deadline.Token);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using var command = connection.Connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM Apprentices";

        Assert.Equal(2L, await command.ExecuteScalarAsync(deadline.Token));

        command.CommandText = "SELECT count(*) FROM LongRunningOperations WHERE Kind='guild-quest'";

        Assert.Equal(2L, await command.ExecuteScalarAsync(deadline.Token));

        command.CommandText = "SELECT count(*) FROM BillableOperations";

        Assert.Equal(5L, await command.ExecuteScalarAsync(deadline.Token));

        command.CommandText = "SELECT count(*) FROM guild_result_receipts";

        Assert.Equal(2L, await command.ExecuteScalarAsync(deadline.Token));

        command.CommandText = "SELECT count(*) FROM guild_result_applications";

        Assert.Equal(1L, await command.ExecuteScalarAsync(deadline.Token));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed class RecordingRuntime(WizardIntelligenceProvider wizard, DelegatingTransport transport) : IGuildLocalInferenceRuntime
    {
        private IGuildLocalInferenceRuntime Actual => wizard;

        public bool IsAvailable => Actual.IsAvailable;

        public Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, CancellationToken ct) =>
            RecordAsync(() => Actual.ExecuteAsync(execution, input, ct));

        public Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input,
            GuildLocalInferenceTurn turn, CancellationToken ct) => RecordAsync(() => Actual.ExecuteTurnAsync(execution, input, turn, ct));

        private async Task<Result<PromptTurnResult>> RecordAsync(Func<Task<Result<PromptTurnResult>>> action)
        {
            try
            {
                var result = await action();

                if (result.IsFailure) transport.RuntimeDiagnostic = result.Error.Code + ": " + result.Error.Message;

                return result;
            }
            catch (Exception error)
            {
                transport.RuntimeDiagnostic = error.GetType().Name + ": " + error.Message;

                throw;
            }
        }
    }

    private sealed class DelegatingTransport : HttpMessageHandler
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal string ToolResultDiagnostic = "none";

        internal string RuntimeDiagnostic = "none";

        internal TaskCompletionSource DelegationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ChildCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowChildReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                using JsonDocument payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));

                foreach (JsonElement item in payload.RootElement.GetProperty("messages").EnumerateArray())
                {
                    if (item.TryGetProperty("role", out JsonElement role) && role.GetString() == "tool")
                    {
                        string diagnostic = item.GetRawText();

                        ToolResultDiagnostic = diagnostic[..Math.Min(512, diagnostic.Length)];
                    }
                }
            }

            int ordinal = Interlocked.Increment(ref _calls);

            if (ordinal == 2) DelegationRequested.TrySetResult();

            if (ordinal == 3)
            {
                ChildCalled.TrySetResult();

                await AllowChildReturn.Task.WaitAsync(cancellationToken);
            }

            string message = ordinal switch
            {
                1 => """{"role":"assistant","content":"[{\"index\":1,\"description\":\"Delegate a child task and summarize its answer\"}]"}""",
                2 => """{"role":"assistant","content":null,"tool_calls":[{"id":"call-child","type":"function","function":{"name":"delegate_task","arguments":"{\"prompt\":\"child task\",\"max_tokens\":10000000}"}}]}""",
                3 => """{"role":"assistant","content":"child finished"}""",
                _ => """{"role":"assistant","content":"parent finished"}""",
            };

            string json = $$$"""{"id":"capacity-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"gpt-5.4-mini-2026-03-17","choices":[{"index":0,"message":{{{message}}},"finish_reason":"{{{(ordinal == 2 ? "tool_calls" : "stop")}}}"}],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}""";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(json) };
        }
    }
}
