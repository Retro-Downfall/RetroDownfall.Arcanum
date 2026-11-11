using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("guild_quests")]
    [InlineData("guild_work_awards")]
    [InlineData("guild_envelopes")]
    [InlineData("commit")]
    public async Task Award_fault_rolls_back_every_participant_and_same_key_retries_once(string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync();
        var setup = await OperatorAwardFixtureAsync(f);

        string[] tables = ["guild_quests", "guild_signets", "guild_budget_accounts", "guild_quest_budget",
            "guild_cohort_member_quests", "guild_work_awards", "guild_work_mutations", "guild_quest_start_intents",
            "guild_portal_outbox", "guild_events", "guild_envelopes", "guild_inbox"];
        Dictionary<string, long> before = [];
        foreach (string table in tables) before[table] = await f.ScalarAsync($"SELECT count(*) FROM {table}");

        if (boundary == "commit")
        {
            await f.ExecuteAsync("""
                CREATE TABLE fixture_award_commit_fault(MissingQuest TEXT REFERENCES guild_quests(Id) DEFERRABLE INITIALLY DEFERRED);
                CREATE TRIGGER fixture_award_fault AFTER INSERT ON guild_work_awards
                BEGIN INSERT INTO fixture_award_commit_fault(MissingQuest) VALUES('not-a-quest'); END;
                """);
        }
        else
        {
            await f.ExecuteAsync($"CREATE TRIGGER fixture_award_fault AFTER INSERT ON {boundary} BEGIN SELECT RAISE(ABORT,'fixture award rollback'); END;");
        }

        var failed = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);
        Assert.True(failed.IsFailure);

        foreach (string table in tables) Assert.Equal(before[table], await f.ScalarAsync($"SELECT count(*) FROM {table}"));
        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_offers WHERE StateCode=1"));

        await f.ExecuteAsync(boundary == "commit" ? "DROP TRIGGER fixture_award_fault; DROP TABLE fixture_award_commit_fault;" : "DROP TRIGGER fixture_award_fault;");

        var accepted = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);
        Assert.True(accepted.IsSuccess, accepted.Error.Message);
        var replay = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.Equal(accepted.Value.QuestId, replay.Value.QuestId);
        Assert.True(replay.Value.Receipt!.Replayed);
        Assert.Equal(before["guild_quests"] + 1, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
        Assert.Equal(before["guild_work_awards"] + 1, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));
    }
}
