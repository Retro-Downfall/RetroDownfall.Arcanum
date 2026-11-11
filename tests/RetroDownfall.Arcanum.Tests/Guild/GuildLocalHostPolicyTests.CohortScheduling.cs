using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public void Production_Guild_registration_supplies_the_Cohort_tool_and_reconciliation_store()
    {
        ServiceCollection services = new();

        services.AddGuildLocalExecution();

        ServiceDescriptor registration = Assert.Single(services, item => item.ServiceType == typeof(GuildCohortStore));

        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Local_primary_produces_exact_designated_final_result_for_cohort(bool recoverClosed, bool malformed)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://provider.example.test/v1", Models = [new() { Name = "model" }] }];

        var caller = await f.OperatorAsync();

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(), TimeProvider.System, new PortAdmission(), f.Readiness);

        GuildIssueRootRequest request = f.Request with
        {
            Grant = f.Request.Grant with { Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] },
        };

        var root = await store.IssueAsync(caller, new(f.Generation, null, caller.InvocationId,
            request.IdempotencyKey, request.IdempotencyKey), request, default);

        Assert.True(root.IsSuccess, root.Error.Message);

        var run = await BindCohortRunAsync(f, caller, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, store,
            new(), f.Policy, TimeProvider.System);

        var cohort = await cohorts.CreateAsync(run, new(Guid.NewGuid(), root.Value.Quest.Id, "Execution",
            new(GuildConvergenceKind.First, null), new("text/plain"), null), default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        Guid key = Guid.NewGuid();

        var member = await store.IssueCohortMemberAsync(run, new(f.Generation, root.Value.Quest.Id, null, key, key),
            cohort.Value.Id, cohort.Value.Revision, new(key, root.Value.Quest.Revision, null, "Member", "Contribute", new()), default);

        Assert.True(member.IsSuccess, member.Error.Message);

        if (recoverClosed)
        {
            var closed = await cohorts.CloseAsync(run, cohort.Value.Id, new(Guid.NewGuid(), 2), default);

            Assert.True(closed.IsSuccess, closed.Error.Message);
        }

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LongRunningOperationOwnership ownership = new();

        Assert.True(ownership.TryClaim(root.Value.Quest.OperationId, out Guid creatorClaim));

        SchedulerEngine engine = new();

        if (malformed) engine.Output = "\ud800";

        GuildLocalQuestScheduler? scheduler = null;

        ServiceCollection registration = new();

        registration.AddSingleton(store);

        registration.AddSingleton(cohorts);

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

        using GuildLocalQuestScheduler owner = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            f.Connections, ownership, TimeProvider.System, NullLogger<GuildLocalQuestScheduler>.Instance,
            new SchedulerReadiness(false), f.Gate);

        scheduler = owner;

        engine.Release.TrySetResult();

        await scheduler.StartAsync(default);

        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));

            while (await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts") == 0)
                await Task.Delay(20, deadline.Token);

            Assert.Equal(malformed ? 0L : 1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions WHERE OutcomeCode=1"));

            Assert.Equal(malformed ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions WHERE OutcomeCode=6"));

            Assert.Equal(1, engine.Calls);

            Assert.Equal(malformed ? 0L : 1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions v JOIN guild_content c ON c.ContentId=v.ContentId WHERE CAST(c.Body AS TEXT)='scheduled public output'"));

            if (recoverClosed)
            {
                while (await f.ScalarAsync("SELECT StateCode FROM guild_cohorts") != (long)GuildCohortState.Completed)
                    await Task.Delay(20, deadline.Token);
            }
            else
            {
                Assert.Equal((long)GuildCohortState.Open, await f.ScalarAsync("SELECT StateCode FROM guild_cohorts"));
            }
        }
        finally
        {
            await scheduler.StopAsync(default);

            ownership.Release(root.Value.Quest.OperationId, creatorClaim);
        }
    }
}
