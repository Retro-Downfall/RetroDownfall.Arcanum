using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildCapacityTests
{
    [SkippableFact]
    public async Task Native_parallel_units_wait_for_owned_physical_call_settlement_without_canceling_a_live_sibling()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        using ParallelTransport transport = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = settings.Features.Apprentices = true;

                settings.Execution.MaxConcurrentApprentices = 1;

                settings.Execution.MaxConcurrentApprenticeBranches = 2;

                settings.DefaultModel = "gpt-5.4-mini-2026-03-17";

                settings.Providers = [new() { Name = "qualified", Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://api.openai.com/v1", Models = ["gpt-5.4-mini-2026-03-17"] }];

                return settings;
            },
            ServiceOverrides = services =>
            {
                services.AddHttpClient("OpenAiCompatibleProvider").ConfigurePrimaryHttpMessageHandler(() => transport);

                services.AddScoped<IGuildLocalInferenceRuntime>(provider =>
                    new ParallelRuntime(provider.GetRequiredService<WizardIntelligenceProvider>(), transport));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Parallel parent", "Complete two independent branches",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        string issuedBody = await issued.Content.ReadAsStringAsync();

        Assert.True(issued.StatusCode == HttpStatusCode.Created, issuedBody);

        using JsonDocument accepted = JsonDocument.Parse(issuedBody);

        Guid id = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        try
        {
            await transport.FirstUnitHttp.Task.WaitAsync(deadline.Token);

            await transport.BothUnitsEntered.Task.WaitAsync(deadline.Token);

            // The first real HTTP call is deliberately held after dispatch. The sibling has
            // entered the real Wizard; it must wait for settlement instead of refusing that live effect.
            _ = await Task.WhenAny(transport.Failed.Task, Task.Delay(500, deadline.Token));

            Assert.False(transport.Failed.Task.IsCompleted, transport.Failure);

            Assert.Equal(2, transport.Calls);

            transport.Release.TrySetResult();

            while (true)
            {
                using HttpResponseMessage result = await client.GetAsync($"/api/guild/quests/{id:D}/result", deadline.Token);

                if (result.StatusCode == HttpStatusCode.OK) break;

                Assert.True(result.StatusCode == HttpStatusCode.NotFound, await result.Content.ReadAsStringAsync(deadline.Token));

                await Task.Delay(25, deadline.Token);
            }

            // One planner call, two parallel unit calls, then one Shifting Fate review for
            // the completed group (the shared engine does not review each branch separately).
            Assert.Equal(4, transport.Calls);

            using HttpResponseMessage content = await client.GetAsync($"/api/guild/quests/{id:D}/result/content", deadline.Token);

            Assert.Equal(HttpStatusCode.OK, content.StatusCode);

            using JsonDocument completed = JsonDocument.Parse(await content.Content.ReadAsStringAsync(deadline.Token));

            string finalBody = completed.RootElement.GetProperty("data").GetProperty("body").GetString()!;

            Assert.Contains("first unit finished", finalBody, StringComparison.Ordinal);

            Assert.Contains("second unit finished", finalBody, StringComparison.Ordinal);

            Assert.False(transport.Failed.Task.IsCompleted, transport.Failure);

            await using AsyncServiceScope verification = factory.Services.CreateAsyncScope();

            var opened = await verification.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, deadline.Token);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using var connection = opened.Value;

            await using var command = connection.Connection.CreateCommand();

            command.CommandText = "SELECT count(*) FROM BillableOperations";

            Assert.Equal(4L, await command.ExecuteScalarAsync(deadline.Token));

            command.CommandText = "SELECT count(*) FROM guild_budget_effects WHERE StateCode IN(0,1)";

            Assert.Equal(0L, await command.ExecuteScalarAsync(deadline.Token));
        }
        finally
        {
            transport.Release.TrySetResult();
        }
    }

    private sealed class ParallelRuntime(WizardIntelligenceProvider wizard, ParallelTransport transport) : IGuildLocalInferenceRuntime
    {
        private IGuildLocalInferenceRuntime Actual => wizard;

        public bool IsAvailable => Actual.IsAvailable;

        public Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, CancellationToken ct) =>
            Actual.ExecuteAsync(execution, input, ct);

        public async Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input,
            GuildLocalInferenceTurn turn, CancellationToken ct)
        {
            if (Interlocked.Increment(ref transport.Turns) == 3) transport.BothUnitsEntered.TrySetResult();

            try
            {
                var result = await Actual.ExecuteTurnAsync(execution, input, turn, ct);

                if (result.IsFailure)
                {
                    transport.Failure = result.Error.Code + ": " + result.Error.Message;

                    transport.Failed.TrySetResult();
                }

                return result;
            }
            catch (Exception error)
            {
                transport.Failure = error.GetType().Name;

                transport.Failed.TrySetResult();

                throw;
            }
        }
    }

    private sealed class ParallelTransport : HttpMessageHandler
    {
        internal int Turns;

        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal string Failure = "none";

        internal TaskCompletionSource FirstUnitHttp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource BothUnitsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int ordinal = Interlocked.Increment(ref _calls);

            if (ordinal == 2)
            {
                FirstUnitHttp.TrySetResult();

                await Release.Task.WaitAsync(ct);
            }

            string content = ordinal == 1
                ? "[{\"index\":1,\"description\":\"First branch\",\"isParallel\":true},{\"index\":2,\"description\":\"Second branch\",\"isParallel\":true}]"
                : ordinal == 2 ? "first unit finished" : ordinal == 3 ? "second unit finished" : "No changes.";

            string quoted = JsonSerializer.Serialize(content, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.String);

            string json = $$$"""{"id":"parallel-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"gpt-5.4-mini-2026-03-17","choices":[{"index":0,"message":{"role":"assistant","content":{{{quoted}}}},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
