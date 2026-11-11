using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Scheduled_completion_retains_claim_while_child_work_is_unfinished()
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

        GuildQuestDto quest = issued.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        Guid key = Guid.NewGuid();

        var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, key, default);

        Assert.True(run.IsSuccess, run.Error.Message);

        var current = await store.GetAsync(caller, quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var child = await store.IssueAsync(run.Value, new(f.Generation, quest.Id, null, key, key), quest.Id,
            new(key, current.Value.Quest.Revision, null, "Pending child", "Finish before parent", null), default);

        Assert.True(child.IsSuccess, child.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationOwnership ownership = new();

        CompletionBoundaryEngine engine = new(quest.Id);

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
            ownership, TimeProvider.System, NullLogger<GuildLocalQuestScheduler>.Instance, new SchedulerReadiness(false), f.Gate);

        using (scheduler)
        {
            await scheduler.StartAsync(default);

            scheduler.Schedule(quest);

            scheduler.Schedule(child.Value.Quest);

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

            try
            {
                await engine.ParentReturned.Task.WaitAsync(deadline.Token);

                await engine.ChildEntered.Task.WaitAsync(deadline.Token);

                Assert.True(ownership.IsClaimed(quest.OperationId));

                Assert.True(ownership.IsClaimed(child.Value.Quest.OperationId));

                Assert.Equal(0L, await f.ScalarAsync($"SELECT count(*) FROM guild_result_receipts WHERE QuestId='{quest.Id.ToString("D").ToUpperInvariant()}'"));

                engine.ReleaseChild.TrySetResult();

                while (await f.ScalarAsync($"SELECT count(*) FROM guild_result_receipts WHERE QuestId='{quest.Id.ToString("D").ToUpperInvariant()}'") == 0)
                    await Task.Delay(20, deadline.Token);

                Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_result_receipts WHERE QuestId='{child.Value.Quest.Id.ToString("D").ToUpperInvariant()}'"));
            }
            finally
            {
                engine.ReleaseChild.TrySetResult();

                await scheduler.StopAsync(default);
            }
        }

        Assert.False(ownership.IsClaimed(quest.OperationId));

        Assert.False(ownership.IsClaimed(child.Value.Quest.OperationId));
    }

    private sealed class CompletionBoundaryEngine(Guid parentId) : IGuildLocalExecutionEngine
    {
        internal TaskCompletionSource ParentReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ChildEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseChild { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<PromptTurnResult>> ExecuteGuildAsync(GuildLocalExecutionBinding binding,
            GuildLocalExecutionInput input, GuildLocalInferenceExecution execution, IGuildLocalInferenceRuntime runtime,
            IGuildLocalEngineWait waitForInput, CancellationToken cancellationToken)
        {
            if (binding.QuestId == parentId) ParentReturned.TrySetResult();
            else
            {
                ChildEntered.TrySetResult();

                await ReleaseChild.Task.WaitAsync(cancellationToken);
            }

            return new PromptTurnResult("completed", null);
        }
    }
}
