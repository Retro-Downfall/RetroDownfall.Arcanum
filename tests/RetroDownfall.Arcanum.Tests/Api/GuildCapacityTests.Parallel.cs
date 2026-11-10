using System.Collections.Concurrent;
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
using RetroDownfall.Arcanum.Tests.Support;
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

        ArcanumWebApplicationFactory factory = new()
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

        ParallelPhaseWatchdog watchdog = new(TimeProvider.System, TimeSpan.FromSeconds(60));

        Guid id = Guid.Empty;

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();

            GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Parallel parent", "Complete two independent branches",
                new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

            using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
                Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

            string issuedBody = await issued.Content.ReadAsStringAsync();

            Assert.True(issued.StatusCode == HttpStatusCode.Created, issuedBody);

            using JsonDocument accepted = JsonDocument.Parse(issuedBody);

            id = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

            await PhaseAsync("first physical branch and live sibling", async ct =>
            {
                await transport.FirstUnitHttp.Task.WaitAsync(ct);

                await transport.BothUnitsEntered.Task.WaitAsync(ct);

                _ = await Task.WhenAny(transport.Failed.Task, Task.Delay(500, ct));

                Assert.False(transport.Failed.Task.IsCompleted, transport.Failure);

                Assert.Equal(2, transport.Calls);

                await AssertParallelAccountingAsync(factory, settled: 1, exposed: 1, ct);
            });

            transport.Release.TrySetResult();

            await PhaseAsync("sibling physical call after first settlement", async ct =>
            {
                await transport.SecondUnitHttp.Task.WaitAsync(ct);

                Assert.False(transport.Failed.Task.IsCompleted, transport.Failure);

                Assert.Equal(3, transport.Calls);

                // Bills are inserted by SettleProviderEffectAsync, not preparation. Also require the
                // completed bill's exact Guild evidence/effect association; a pending row proves nothing.
                await AssertParallelAccountingAsync(factory, settled: 2, exposed: 1, ct);
            });

            transport.ReleaseSecond.TrySetResult();

            await PhaseAsync("group review after both branch settlements", async ct =>
            {
                await transport.ReviewHttp.Task.WaitAsync(ct);

                Assert.False(transport.Failed.Task.IsCompleted, transport.Failure);

                Assert.Equal(4, transport.Calls);

                await AssertParallelAccountingAsync(factory, settled: 3, exposed: 1, ct);
            });

            transport.ReleaseReview.TrySetResult();

            await PhaseAsync("durable result and final accounting", async ct =>
            {
                while (true)
                {
                    using HttpResponseMessage result = await client.GetAsync($"/api/guild/quests/{id:D}/result", ct);

                    if (result.StatusCode == HttpStatusCode.OK) break;

                    Assert.True(result.StatusCode == HttpStatusCode.NotFound, await result.Content.ReadAsStringAsync(ct));

                    Assert.False(transport.Failed.Task.IsCompleted, transport.Failure);

                    await Task.Delay(25, ct);
                }

                // One planner call, two unit calls and one shared Shifting Fate review.
                Assert.Equal(4, transport.Calls);

                using HttpResponseMessage content = await client.GetAsync($"/api/guild/quests/{id:D}/result/content", ct);

                Assert.Equal(HttpStatusCode.OK, content.StatusCode);

                using JsonDocument completed = JsonDocument.Parse(await content.Content.ReadAsStringAsync(ct));

                string finalBody = completed.RootElement.GetProperty("data").GetProperty("body").GetString()!;

                Assert.Contains("first unit finished", finalBody, StringComparison.Ordinal);

                Assert.Contains("second unit finished", finalBody, StringComparison.Ordinal);

                Assert.False(transport.Failed.Task.IsCompleted, transport.Failure);

                await AssertParallelAccountingAsync(factory, settled: 4, exposed: 0, ct);

                Assert.Equal(4, transport.OwnedCalls.Count);

                Assert.All(transport.OwnedCalls, call => Assert.True(call.IsCompletedSuccessfully));
            });
        }
        finally
        {
            // Release every fixture-owned pause before stopping/joining the actual host and its calls.
            // An assertion or observer watchdog must never leave physical work behind the isolated history.
            transport.ReleaseAll();

            try { await factory.DisposeAsync(); }
            finally { await transport.JoinOwnedCallsAsync(); }
        }

        async Task PhaseAsync(string name, Func<CancellationToken, Task> action)
        {
            try
            {
                await watchdog.ObserveAsync(action);
            }
            catch (TimeoutException)
            {
                string snapshot = await CapacitySnapshotAsync(factory, id);

                Assert.Fail($"Parallel phase '{name}' stalled; calls={transport.Calls}; turns={transport.Turns}; "
                    + $"runtime={transport.Failure}; {snapshot}");
            }
        }
    }

    [Fact]
    public async Task Parallel_phase_watchdog_allows_slow_successive_phases_without_spending_the_next_phase_budget()
    {
        ManualTimerTimeProvider clock = new();

        ParallelPhaseWatchdog watchdog = new(clock, TimeSpan.FromSeconds(20));

        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task firstObservation = watchdog.ObserveAsync(ct => first.Task.WaitAsync(ct));

        clock.Advance(TimeSpan.FromSeconds(15));

        first.TrySetResult();

        await firstObservation;

        TaskCompletionSource settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task secondObservation = watchdog.ObserveAsync(ct => settlement.Task.WaitAsync(ct));

        clock.Advance(TimeSpan.FromSeconds(15));

        settlement.TrySetResult();

        await secondObservation;

        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task Parallel_phase_watchdog_refuses_a_stall_without_canceling_the_owned_producer()
    {
        ManualTimerTimeProvider clock = new();

        ParallelPhaseWatchdog watchdog = new(clock, TimeSpan.FromSeconds(20));

        TaskCompletionSource settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task observation = watchdog.ObserveAsync(ct => settlement.Task.WaitAsync(ct));

        clock.Advance(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<TimeoutException>(() => observation);

        Assert.False(settlement.Task.IsCompleted);

        settlement.TrySetResult();

        await settlement.Task;

        Assert.Equal(0, clock.ActiveTimers);
    }

    private static async Task AssertParallelAccountingAsync(ArcanumWebApplicationFactory factory, long settled, long exposed, CancellationToken ct)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using var command = connection.Connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM BillableOperations";

        Assert.Equal(settled, await command.ExecuteScalarAsync(ct));

        command.CommandText = """
            SELECT count(*) FROM BillableOperations b
            JOIN guild_budget_evidence e ON lower(replace(e.ReceiptId,'-',''))=lower(replace(b.Id,'-',''))
            JOIN guild_budget_effects f ON f.EffectId=e.EffectId
            WHERE b.Status=0 AND e.Complete=1 AND f.StateCode=2
                AND lower(replace(f.SourceId,'-',''))=lower(replace(b.Id,'-',''))
                AND lower(replace(f.AccountingAssociationId,'-',''))=lower(replace(b.RunId,'-',''))
            """;

        Assert.Equal(settled, await command.ExecuteScalarAsync(ct));

        command.CommandText = "SELECT count(*) FROM guild_budget_effects WHERE StateCode IN(0,1)";

        Assert.Equal(exposed, await command.ExecuteScalarAsync(ct));
    }

    // Each causal phase owns its observation budget. Expiry cancels only the observer, while
    // fixture-owned provider work remains owned until release and host/runtime joining in finally.
    private sealed class ParallelPhaseWatchdog(TimeProvider clock, TimeSpan budget)
    {
        internal async Task ObserveAsync(Func<CancellationToken, Task> action)
        {
            using CancellationTokenSource deadline = new(budget, clock);

            try { await action(deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException("The parallel observation watchdog expired.");
            }
        }
    }

    private sealed class ParallelRuntime(WizardIntelligenceProvider wizard, ParallelTransport transport) : IGuildLocalInferenceRuntime
    {
        private IGuildLocalInferenceRuntime Actual => wizard;

        public bool IsAvailable => Actual.IsAvailable;

        public Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, CancellationToken ct) =>
            Actual.ExecuteAsync(execution, input, ct);

        public Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input,
            GuildLocalInferenceTurn turn, CancellationToken ct)
        {
            Task<Result<PromptTurnResult>> owned = RecordTurnAsync(execution, input, turn, ct);

            transport.OwnedCalls.Enqueue(owned);

            return owned;
        }

        private async Task<Result<PromptTurnResult>> RecordTurnAsync(GuildLocalInferenceExecution execution, GuildLocalExecutionInput input,
            GuildLocalInferenceTurn turn, CancellationToken ct)
        {
            int ordinal = Interlocked.Increment(ref transport.Turns);

            try
            {
                Task<Result<PromptTurnResult>> actual = Actual.ExecuteTurnAsync(execution, input, turn, ct);

                if (ordinal == 3) transport.BothUnitsEntered.TrySetResult();

                var result = await actual;

                if (result.IsFailure)
                {
                    transport.Failure = result.Error.Code + ": " + result.Error.Message;

                    transport.Failed.TrySetResult();
                }

                return result;
            }
            catch (Exception error)
            {
                transport.Failure = error.GetType().Name + ": " + error.Message;

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

        internal TaskCompletionSource SecondUnitHttp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReviewHttp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseReview { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ConcurrentQueue<Task> OwnedCalls { get; } = new();

        internal void ReleaseAll()
        {
            Release.TrySetResult();

            ReleaseSecond.TrySetResult();

            ReleaseReview.TrySetResult();
        }

        internal async Task JoinOwnedCallsAsync()
        {
            Task[] owned = OwnedCalls.ToArray();

            try { await Task.WhenAll(owned); }
            catch (Exception) when (owned.All(task => task.IsCompleted))
            {
                // The runtime diagnostic retains faults; observing them here still joins every task.
            }
        }

        internal TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int ordinal = Interlocked.Increment(ref _calls);

            if (ordinal == 2)
            {
                FirstUnitHttp.TrySetResult();

                await Release.Task.WaitAsync(ct);
            }

            if (ordinal == 3)
            {
                SecondUnitHttp.TrySetResult();

                await ReleaseSecond.Task.WaitAsync(ct);
            }

            if (ordinal == 4)
            {
                ReviewHttp.TrySetResult();

                await ReleaseReview.Task.WaitAsync(ct);
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
