using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("pinned-signet")]
    [InlineData("primary-quest")]
    public async Task Award_primary_pinned_identity_mismatch_refuses_writes_without_poisoning_run(string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await OperatorAwardFixtureAsync(f);

        var award = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);

        Assert.True(award.IsSuccess, award.Error.Message);

        var work = await f.Store.GetAsync(setup.Owner, award.Value.QuestId, default);

        Assert.True(work.IsSuccess, work.Error.Message);

        var author = await BindCohortRunAsync(f, setup.Owner, work.Value);

        var current = await setup.Store.GetAsync(setup.IssuerCaller, setup.Offer.CohortId, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        AppendSharedNoteRequest initial = new(Guid.NewGuid(), current.Value.Revision, GuildNoteKind.Observation,
            null, "authorized associated work", null);

        var accepted = await setup.Store.AppendNoteAsync(author, NoteMutation(initial), setup.Offer.CohortId, initial, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        current = await setup.Store.GetAsync(setup.IssuerCaller, setup.Offer.CohortId, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        string[] tables = ["guild_cohort_ledger", "guild_events", "guild_content", "guild_work_mutations", "guild_dispatches", "guild_budget_effects"];

        Dictionary<string, long> before = [];

        foreach (string table in tables) before[table] = await f.ScalarAsync($"SELECT count(*) FROM {table}");

        string id = award.Value.Id.ToString("D").ToUpperInvariant();

        string wrongQuest = setup.Issuer.Quest.Id.ToString("D").ToUpperInvariant();

        string wrongSignet = setup.Issuer.Quest.SignetId.ToString("D").ToUpperInvariant();

        // Recreate only this durable association to simulate damaged metadata. All foreign keys
        // remain valid, and the immutable UPDATE guard is retained; no new authority is minted.
        await f.ExecuteAsync($"""
            BEGIN IMMEDIATE;
            CREATE TABLE fixture_primary_binding AS SELECT * FROM guild_work_awards WHERE Id='{id}';
            DELETE FROM guild_work_awards WHERE Id='{id}';
            INSERT INTO guild_work_awards(Id,OfferId,BidId,QuestId,MemberId,PrimaryQuestId,PrimarySignetId,PrimaryRevocationGeneration)
            SELECT Id,OfferId,BidId,QuestId,MemberId,
                {(boundary == "primary-quest" ? $"'{wrongQuest}'" : "PrimaryQuestId")},
                {(boundary == "pinned-signet" ? $"'{wrongSignet}'" : "PrimarySignetId")},PrimaryRevocationGeneration
            FROM fixture_primary_binding;
            COMMIT;
            """);

        AppendSharedNoteRequest attempt = initial with
        {
            IdempotencyKey = Guid.NewGuid(), ExpectedRevision = current.Value.Revision, Body = "must not be accepted with a mismatched primary",
        };

        var refused = await setup.Store.AppendNoteAsync(author, NoteMutation(attempt), setup.Offer.CohortId, attempt, default);

        Assert.True(refused.IsFailure);

        foreach (string table in tables) Assert.Equal(before[table], await f.ScalarAsync($"SELECT count(*) FROM {table}"));

        await f.ExecuteAsync($"""
            BEGIN IMMEDIATE;
            DELETE FROM guild_work_awards WHERE Id='{id}';
            INSERT INTO guild_work_awards SELECT * FROM fixture_primary_binding;
            DROP TABLE fixture_primary_binding;
            COMMIT;
            """);

        // The failed operation did not accept its identity or poison the retained live run.
        // Restoring the exact immutable association permits that same unaccepted request once.
        var restored = await setup.Store.AppendNoteAsync(author, NoteMutation(attempt), setup.Offer.CohortId, attempt, default);

        Assert.True(restored.IsSuccess, restored.Error.Message);

        Assert.Equal(before["guild_cohort_ledger"] + 1, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_ledger"));
    }
}
