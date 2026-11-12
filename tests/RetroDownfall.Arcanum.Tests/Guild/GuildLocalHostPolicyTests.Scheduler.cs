using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(false, false, true, false, false, false, false)]
    [InlineData(false, false, false, true, false, false, false)]
    [InlineData(false, false, false, false, true, false, false)]
    [InlineData(false, false, false, false, true, true, false)]
    [InlineData(false, false, false, false, false, false, true)]
    public async Task Local_scheduler_recovers_committed_start_and_coalesces_wakes_before_rebinding_live_work(bool disable, bool unavailable, bool maintenance, bool initiallyDisabled, bool initiallyCanceled, bool canceledAncestor, bool stopWhileRunning)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://provider.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext caller = await f.OperatorAsync();

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(), TimeProvider.System, new PortAdmission(), f.Readiness);

        var issued = await store.IssueAsync(caller, new(f.Generation, null, caller.InvocationId, caller.InvocationId,
            f.Request.IdempotencyKey), f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        if (canceledAncestor)
        {
            var root = issued.Value.Quest;

            var recovery = await f.Policy.CreateRecoveryAsync(caller, root.Id, root.OperationId, root.SignetRevision, default);

            Assert.True(recovery.IsSuccess, recovery.Error.Message);

            var execution = await f.Authority.RebindAsync(recovery.Value, default);

            Assert.True(execution.IsSuccess, execution.Error.Message);

            Guid key = Guid.NewGuid();

            var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, key, default);

            Assert.True(run.IsSuccess, run.Error.Message);

            var current = await store.GetAsync(caller, root.Id, default);

            Assert.True(current.IsSuccess, current.Error.Message);

            var child = await store.IssueAsync(run.Value, new(f.Generation, root.Id, null, key, key), root.Id,
                new(key, current.Value.Quest.Revision, null, "Pending child", "Must stop with parent", null), default);

            Assert.True(child.IsSuccess, child.Error.Message);

            issued = await store.GetAsync(caller, root.Id, default);

            Assert.True(issued.IsSuccess, issued.Error.Message);
        }

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationOwnership ownership = new();

        SchedulerEngine engine = new();

        TestCapturingLogger<GuildLocalQuestScheduler> logger = new();

        GuildLocalQuestScheduler? scheduler = null;

        ServiceCollection registration = new();

        registration.AddSingleton(store);

        registration.AddSingleton(f.Policy);

        registration.AddSingleton(f.Authority);

        registration.AddSingleton<ILongRunningOperationStore>(new LongRunningOperationStore(db, f.Connections));

        registration.AddSingleton<IGuildLocalExecutionEngine>(engine);

        registration.AddSingleton<IGuildLocalInferenceRuntime>(new SchedulerInference());

        registration.AddSingleton<IGuildLocalQuestScheduler>(_ => scheduler!);

        registration.AddSingleton(ownership);

        registration.AddSingleton(TimeProvider.System);

        registration.AddScoped<GuildLocalPort>();

        await using ServiceProvider services = registration.BuildServiceProvider();

        scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings, f.Connections,
            ownership, TimeProvider.System, logger, new SchedulerReadiness(unavailable), f.Gate);

        // These waits observe owned lifecycle transitions. Their watchdog only diagnoses a stuck
        // fixture; it is not a ten-second performance requirement shared by every phase.
        TimeSpan watchdog = TimeSpan.FromMinutes(2);

        async Task<string> DescribeSchedulerAsync()
        {
            try
            {
                return $"Available={scheduler.IsAvailable}; Background={scheduler.ExecuteTask?.Status}; "
                    + $"BackgroundError={scheduler.ExecuteTask?.Exception?.GetBaseException().GetType().Name}; "
                    + $"EngineCalls={engine.Calls}; Claimed={ownership.IsClaimed(issued.Value.Quest.OperationId)}; "
                    + $"Attempts={await f.ScalarAsync("SELECT COALESCE(MAX(AttemptCount),0) FROM LongRunningOperations")}; "
                    + $"Leases={await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE LeaseOwner IS NOT NULL")}; "
                    + $"OperationState={await f.ScalarAsync("SELECT COALESCE(MAX(State),-1) FROM LongRunningOperations")}; "
                    + $"OperationRevision={await f.ScalarAsync("SELECT COALESCE(MAX(Revision),-1) FROM LongRunningOperations")}; "
                    + $"Bindings={await f.ScalarAsync("SELECT count(*) FROM guild_local_executions")}; "
                    + $"Dispatches={await f.ScalarAsync("SELECT count(*) FROM guild_dispatches")}; "
                    + $"Results={await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts")}; "
                    + $"Cancellations={await f.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations")}; "
                    + string.Join(" | ", logger.Entries.Select(entry => $"{entry.Level}: {entry.Message}"));
            }
            catch (Exception error)
            {
                return $"Scheduler diagnostics unavailable ({error.GetType().Name}).";
            }
        }

        async Task AwaitSignalAsync(Task signal, string phase, bool stopping = false)
        {
            try
            {
                if (!stopping && scheduler.ExecuteTask is { } background)
                {
                    await Task.WhenAny(signal, background).WaitAsync(watchdog);

                    if (!signal.IsCompleted)
                        throw new InvalidOperationException("The scheduler exited before the required transition.");
                }

                await signal.WaitAsync(watchdog);
            }
            catch (Exception error)
            {
                throw new Xunit.Sdk.XunitException($"Scheduler phase '{phase}' failed ({error.GetType().Name}): {error.Message}\n{await DescribeSchedulerAsync()}");
            }
        }

        async Task AwaitStateAsync(Func<Task<bool>> condition, string phase)
        {
            using CancellationTokenSource watchdogCancellation = new(watchdog);

            try
            {
                while (!await condition())
                {
                    if (scheduler.ExecuteTask?.IsCompleted == true)
                        throw new InvalidOperationException("The scheduler exited before the required durable transition.");

                    await Task.Delay(20, watchdogCancellation.Token);
                }
            }
            catch (Exception error)
            {
                throw new Xunit.Sdk.XunitException($"Scheduler phase '{phase}' failed ({error.GetType().Name}): {error.Message}\n{await DescribeSchedulerAsync()}");
            }
        }

        using (scheduler)
        {
            Assert.False(scheduler.IsAvailable);

            if (initiallyCanceled)
            {
                GuildCancelQuestRequest cancel = new(Guid.NewGuid(), issued.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

                var accepted = await store.RequestCancellationAsync(caller,
                    new(cancel.IdempotencyKey, GuildQuestIntent.Compute(issued.Value.Quest.Id, cancel).Value),
                    issued.Value.Quest.Id, cancel, default);

                Assert.True(accepted.IsSuccess, accepted.Error.Message);
            }

            if (initiallyDisabled) f.Settings.CurrentValue.Features.Guild = false;

            await scheduler.StartAsync(default);

            try
            {
                if (initiallyDisabled || initiallyCanceled)
                {
                    Assert.Equal(!initiallyDisabled, scheduler.IsAvailable);

                    await AwaitStateAsync(async () => await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts") == (canceledAncestor ? 2L : 1L),
                        "initial cancellation receipts");

                    Assert.Equal(0, engine.Calls);

                    Assert.Equal(canceledAncestor ? 2L : 1L, await f.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations"));

                    Assert.Equal(canceledAncestor ? 2L : 1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts WHERE Closed=1"));

                    return;
                }

                if (unavailable)
                {
                    Assert.False(scheduler.IsAvailable);

                    Assert.Equal(0, engine.Calls);

                    return;
                }

                Assert.True(scheduler.IsAvailable);

                await AwaitSignalAsync(engine.Entered.Task, "committed start enters engine");

                Assert.True(ownership.IsClaimed(issued.Value.Quest.OperationId));

                if (stopWhileRunning)
                {
                    Task stopped = scheduler.StopAsync(default);

                    await AwaitSignalAsync(engine.CancellationObserved.Task, "stop reaches engine");

                    Assert.False(stopped.IsCompleted);

                    Assert.True(engine.InvocationToken.IsCancellationRequested);

                    Assert.True(ownership.IsClaimed(issued.Value.Quest.OperationId));

                    engine.Release.TrySetResult();

                    await AwaitSignalAsync(stopped, "stop joins engine", stopping: true);

                    Assert.False(ownership.IsClaimed(issued.Value.Quest.OperationId));

                    Assert.False(scheduler.IsAvailable);

                    return;
                }

                if (maintenance)
                {
                    var begun = f.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(150));

                    Assert.True(begun.IsSuccess, begun.Error.Message);

                    await using IGrimoireClosingOwner closing = begun.Value;

                    Task<Result> drained = f.Gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

                    try
                    {
                        await AwaitSignalAsync(engine.CancellationObserved.Task, "maintenance reaches engine");

                        Assert.False(drained.IsCompleted);

                        Assert.True(engine.InvocationToken.IsCancellationRequested);

                        engine.Release.TrySetResult();

                        await AwaitSignalAsync(drained, "maintenance joins engine");

                        Assert.True((await drained).IsSuccess);
                    }
                    finally
                    {
                        // Release before the closing owner is disposed, including assertion failures.
                        engine.Release.TrySetResult();

                        await drained;
                    }

                    return;
                }

                long revision = await f.ScalarAsync("SELECT Revision FROM guild_signets");

                Assert.True(scheduler.IsAvailable);

                for (int i = 0; i < 20; i++) Assert.False(scheduler.TrySchedule(issued.Value.Quest));

                Assert.True(scheduler.IsAvailable);

                Assert.Equal(revision, await f.ScalarAsync("SELECT Revision FROM guild_signets"));

                Assert.Equal(1, engine.Calls);

                if (disable)
                {
                    f.Settings.CurrentValue.Features.Guild = false;

                    Assert.False(scheduler.IsAvailable);

                    await AwaitStateAsync(async () => await f.ScalarAsync("SELECT CancellationRequested FROM guild_quests") != 0,
                        "disabled Guild requests cancellation");

                    Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations"));
                }

                engine.Release.TrySetResult();

                if (!disable)
                {
                    await AwaitStateAsync(async () => await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts") != 0,
                        "engine completion commits receipt");
                }

                Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM Apprentices"));

                Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations"));
            }
            finally
            {
                engine.Release.TrySetResult();

                await scheduler.StopAsync(default);
            }

            Assert.False(scheduler.IsAvailable);

            Assert.False(ownership.IsClaimed(issued.Value.Quest.OperationId));

            Assert.Equal(1, engine.Calls);
        }
    }

    private sealed class SchedulerReadiness(bool unavailable) : IGuildLocalHostReadiness
    {
        public Result RequireExecutionReady() => unavailable
            ? Result.Failure(new(ErrorCodes.Guild.Unavailable, "Fixture host protection is unavailable.")) : Result.Success();

        public Task<Result> EnsureReadyAsync(CancellationToken cancellationToken) => Task.FromResult(RequireExecutionReady());
    }

    private sealed class SchedulerInference : IGuildLocalInferenceRuntime
    {
        public bool IsAvailable => true;

        public Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution,
            GuildLocalExecutionInput input, CancellationToken cancellationToken) => throw new InvalidOperationException("The fixture engine owns this turn.");

        public Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution,
            GuildLocalExecutionInput input, GuildLocalInferenceTurn turn, CancellationToken cancellationToken) => throw new InvalidOperationException("The fixture engine owns this turn.");
    }

    private sealed class SchedulerEngine : IGuildLocalExecutionEngine
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Calls;

        internal string Output { get; set; } = "scheduled public output";

        internal CancellationToken InvocationToken;

        public async Task<Result<PromptTurnResult>> ExecuteGuildAsync(GuildLocalExecutionBinding binding,
            GuildLocalExecutionInput input, GuildLocalInferenceExecution execution, IGuildLocalInferenceRuntime runtime,
            IGuildLocalEngineWait waitForInput, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);

            InvocationToken = cancellationToken;

            using CancellationTokenRegistration cancellation = cancellationToken.Register(() => CancellationObserved.TrySetResult());

            Entered.TrySetResult();

            await Release.Task;

            return new PromptTurnResult(Output, null);
        }
    }
}
