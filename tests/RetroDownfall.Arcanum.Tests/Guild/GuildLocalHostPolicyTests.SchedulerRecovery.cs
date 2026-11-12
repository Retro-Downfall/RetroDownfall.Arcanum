using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Scheduler_retries_a_failed_pre_dispatch_commit_without_a_stale_operation_lease()
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

        await f.ExecuteAsync("""
            CREATE TRIGGER fixture_fail_pre_dispatch BEFORE INSERT ON guild_dispatches
            BEGIN SELECT RAISE(ABORT, 'fixture pre-dispatch failure'); END;
            """);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationOwnership ownership = new();

        SchedulerEngine engine = new();

        ManualTimerTimeProvider cadence = new();

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
            ownership, cadence, NullLogger<GuildLocalQuestScheduler>.Instance, new SchedulerReadiness(false), f.Gate);

        using (scheduler)
        {
            await scheduler.StartAsync(default);

            try
            {
                using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(1));

                while (await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE AttemptCount > 0") == 0)
                    await Task.Delay(20, watchdog.Token);

                while (ownership.IsClaimed(issued.Value.Quest.OperationId))
                    await Task.Delay(20, watchdog.Token);

                Assert.Equal(0, engine.Calls);

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_dispatches"));

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE LeaseOwner IS NOT NULL"));

                await f.ExecuteAsync("DROP TRIGGER fixture_fail_pre_dispatch");

                cadence.Advance(TimeSpan.FromSeconds(1));

                await engine.Entered.Task.WaitAsync(TimeSpan.FromMinutes(1));

                Assert.Equal(1, engine.Calls);

                Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_dispatches WHERE EvidenceCode=1"));

                engine.Release.TrySetResult();

                using CancellationTokenSource receiptWatchdog = new(TimeSpan.FromMinutes(1));

                while (await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts") == 0)
                    await Task.Delay(20, receiptWatchdog.Token);

                Assert.Equal(1, engine.Calls);
            }
            finally
            {
                engine.Release.TrySetResult();

                await scheduler.StopAsync(default);
            }
        }
    }
    [Theory]
    [InlineData("foreign")]
    [InlineData("revision")]
    [InlineData("dispatch")]
    [InlineData("prepared")]
    [InlineData("expired")]
    [InlineData("cancelling")]
    [InlineData("prior-effect")]
    [InlineData("cleanup-failure")]
    public async Task Pre_dispatch_lease_cleanup_refuses_unproven_or_foreign_ownership(string scenario)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(), TimeProvider.System, new PortAdmission(), f.Readiness);

        var issued = await store.IssueAsync(caller, new(f.Generation, null, caller.InvocationId, caller.InvocationId,
            f.Request.IdempotencyKey), f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = scenario == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-10) : DateTimeOffset.UtcNow;

        var lease = await operations.TryAcquireLeaseAsync(issued.Value.Quest.OperationId, "fixture-owner", now, now.AddMinutes(2), default);

        Assert.True(lease.Acquired);

        Guid dispatch = issued.Value.Quest.Id;

        if (scenario is "dispatch" or "prepared")
        {
            string id = dispatch.ToString("D").ToUpperInvariant();

            string run = issued.Value.Quest.RunId.ToString("D").ToUpperInvariant();

            await f.ExecuteAsync($"""
                INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
                VALUES('{id}','{id}',zeroblob(32),0,'{run}',{(scenario == "prepared" ? 1 : 2)},1,'2026-11-05T00:00:00.0000000Z','2026-11-05T00:00:00.0000000Z')
                """);
        }

        if (scenario == "prior-effect")
        {
            string quest = issued.Value.Quest.Id.ToString("D").ToUpperInvariant();

            string prior = Guid.NewGuid().ToString("D").ToUpperInvariant();

            string run = issued.Value.Quest.RunId.ToString("D").ToUpperInvariant();

            string effect = Guid.NewGuid().ToString("D").ToUpperInvariant();

            await f.ExecuteAsync($"""
                INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
                VALUES('{prior}','{quest}',zeroblob(32),0,'{run}',3,1,'2026-11-05T00:00:00.0000000Z','2026-11-05T00:00:00.0000000Z');
                INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,StateCode,Revision)
                SELECT '{effect}',AccountId,'{quest}','{prior}','{run}',0,'{effect}',2,1 FROM guild_budget_accounts WHERE OwnerId='{quest}';
                """);

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects"));
        }

        if (scenario == "cancelling")
            await f.ExecuteAsync($"UPDATE LongRunningOperations SET State={(int)LongRunningOperationState.Cancelling} WHERE Id='{issued.Value.Quest.OperationId:N}'");

        if (scenario == "cleanup-failure")
            await f.ExecuteAsync("CREATE TRIGGER fixture_cleanup_failure BEFORE UPDATE ON LongRunningOperations BEGIN SELECT RAISE(ABORT, 'fixture cleanup failure'); END;");

        try
        {
            var result = await store.ReleaseUnexposedLocalLeaseAsync(issued.Value.Quest.RootQuestId, issued.Value.Quest.Id,
                issued.Value.Quest.OperationId, dispatch, scenario == "foreign" ? "another-owner" : "fixture-owner",
                scenario == "revision" ? lease.Operation.Revision + 1 : lease.Operation.Revision, default);

            if (scenario == "prior-effect")
                Assert.True(result.IsSuccess && result.Value);
            else
                Assert.True(result.IsFailure || !result.Value);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) when (scenario == "cleanup-failure")
        {
            // The caller contains cleanup exceptions; either failure representation retains the lease.
        }

        Assert.Equal(scenario == "prior-effect" ? 0L : 1L,
            await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE LeaseOwner='fixture-owner'"));
    }

}
