using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Scheduler_repairs_terminal_accounting_when_disabled_without_starving_later_work(int oldState)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, _, _, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 2);

        await CommitConvergenceResultAsync(f, owner, members[0], "first", recordBilling: false);

        await CommitConvergenceResultAsync(f, owner, members[1], "second", recordBilling: false);

        GuildQuestStored[] ordered = members.OrderBy(value => value.Quest.Id.ToString("D"), StringComparer.Ordinal).ToArray();

        string blocked = ordered[0].Quest.Id.ToString("D").ToUpperInvariant();

        string eligible = ordered[1].Quest.Id.ToString("D").ToUpperInvariant();

        await f.ExecuteAsync($"DELETE FROM guild_portal_outbox WHERE SourceKindCode=2 AND QuestId IN ('{blocked}','{eligible}')");

        await f.ExecuteAsync($"UPDATE guild_quests SET AccountingStateCode={oldState},Revision=Revision+1 WHERE Id IN ('{blocked}','{eligible}')");

        await f.ExecuteAsync($"DELETE FROM guild_budget_evidence WHERE EffectId IN (SELECT EffectId FROM guild_budget_effects WHERE QuestId='{blocked}')");

        f.Settings.CurrentValue.Features.Guild = false;

        ServiceCollection registration = new();

        registration.AddSingleton(f.Store);

        registration.AddSingleton(f.Policy);

        await using ServiceProvider services = registration.BuildServiceProvider();

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            f.Connections, new LongRunningOperationOwnership(), TimeProvider.System,
            NullLogger<GuildLocalQuestScheduler>.Instance, new SchedulerReadiness(false), f.Gate);

        await scheduler.DiscoverPendingAsync(default);

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_quests WHERE Id='{eligible}' AND AccountingStateCode=0"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_quests WHERE Id='{blocked}' AND AccountingStateCode={oldState}"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_portal_outbox WHERE QuestId='{eligible}' AND SourceKindCode=2"));

        long eventCount = await f.ScalarAsync("SELECT count(*) FROM guild_events");

        using GuildLocalQuestScheduler restarted = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            f.Connections, new LongRunningOperationOwnership(), TimeProvider.System,
            NullLogger<GuildLocalQuestScheduler>.Instance, new SchedulerReadiness(false), f.Gate);

        await restarted.DiscoverPendingAsync(default);

        Assert.Equal(eventCount, await f.ScalarAsync("SELECT count(*) FROM guild_events"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_portal_outbox WHERE QuestId='{eligible}' AND SourceKindCode=2"));
    }
    [Fact]
    public async Task Scheduler_advances_a_bounded_primary_key_frontier_past_settled_history()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, _, _, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 101);

        GuildQuestStored last = members.OrderBy(value => value.Quest.Id.ToString("D"), StringComparer.Ordinal).Last();

        await CommitConvergenceResultAsync(f, owner, last, "last", recordBilling: false);

        string target = last.Quest.Id.ToString("D").ToUpperInvariant();

        await f.ExecuteAsync($"DELETE FROM guild_portal_outbox WHERE SourceKindCode=2 AND QuestId='{target}'");

        await f.ExecuteAsync($"UPDATE guild_quests SET AccountingStateCode=0,Revision=Revision+1 WHERE Id<>'{target}'");

        await f.ExecuteAsync($"UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1 WHERE Id='{target}'");

        await using (var connection = await f.OpenAsync())
        {
            await using var plan = connection.CreateCommand();

            plan.CommandText = "EXPLAIN QUERY PLAN " + GuildLocalQuestScheduler.AccountingRecoveryFrontierQuery;

            plan.Parameters.AddWithValue("$after", "");

            List<string> details = [];

            await using (var reader = await plan.ExecuteReaderAsync())
                while (await reader.ReadAsync()) details.Add(reader.GetString(3));

            Assert.Contains(details, value => value.Contains("SEARCH guild_quests", StringComparison.OrdinalIgnoreCase)
                && value.Contains("Id>?", StringComparison.OrdinalIgnoreCase));
        }

        f.Settings.CurrentValue.Features.Guild = false;

        ServiceCollection registration = new();

        registration.AddSingleton(f.Store);

        registration.AddSingleton(f.Policy);

        await using ServiceProvider services = registration.BuildServiceProvider();

        using GuildLocalQuestScheduler scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            f.Connections, new LongRunningOperationOwnership(), TimeProvider.System,
            NullLogger<GuildLocalQuestScheduler>.Instance, new SchedulerReadiness(false), f.Gate);

        await scheduler.DiscoverPendingAsync(default);

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_quests WHERE Id='{target}' AND AccountingStateCode=2"));

        await scheduler.DiscoverPendingAsync(default);

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_quests WHERE Id='{target}' AND AccountingStateCode=0"));
    }

}
