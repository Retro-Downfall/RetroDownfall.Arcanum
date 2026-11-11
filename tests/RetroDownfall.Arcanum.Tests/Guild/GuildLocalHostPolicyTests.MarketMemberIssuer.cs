using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Native_award_of_member_offer_uses_member_parent_and_exactly_one_member_funding_path()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 0);
        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);
        List<GuildQuestStored> members = [];

        for (int index = 0; index < 2; index++)
        {
            GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Member " + index, "Reviewer", "1",
                [new("review", "Review", "Review work", [], ["text/plain"], ["text/plain"])],
                ["text/plain"], ["text/plain"], new(false, false, false),
                new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));
            var saved = await cards.UpsertCardAsync(new("member-issuer-" + index, null, null, null,
                new(card, "Review", [], RequestedToolsSpecified: true), new string('A', 64)), default);
            Assert.True(saved.IsSuccess, saved.Error.Message);

            var cohort = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);
            var root = await f.Store.GetAsync(setup.Owner, setup.Root.Quest.Id, default);
            Assert.True(cohort.IsSuccess, cohort.Error.Message);
            Assert.True(root.IsSuccess, root.Error.Message);
            Guid key = Guid.NewGuid();
            var member = await f.Store.IssueCohortMemberAsync(setup.Issuer, new(f.Generation, setup.Root.Quest.Id, null, key, key),
                setup.Cohort.Id, cohort.Value.Revision,
                new(key, root.Value.Quest.Revision, null, "Member", "Review", new(), CardId: saved.Value.Payload.Card.CardId), default);
            Assert.True(member.IsSuccess, member.Error.Message);
            members.Add(member.Value);
        }

        var issuer = await BindCohortRunAsync(f, setup.Owner, members[0]);
        var winner = await BindCohortRunAsync(f, setup.Owner, members[1]);
        var current = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);
        Assert.True(current.IsSuccess, current.Error.Message);

        PostWorkOfferRequest post = new(Guid.NewGuid(), current.Value.Revision, "review", "Review work", null, new("text/plain"));
        string postDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(post, GuildJsonContext.Default.PostWorkOfferRequest)));
        var offer = await setup.Cohorts.PostOfferAsync(issuer, new(post.IdempotencyKey, postDigest), setup.Cohort.Id, post, default);
        Assert.True(offer.IsSuccess, offer.Error.Message);

        SubmitBidRequest request = new(Guid.NewGuid(), offer.Value.Revision, "I can help", null);
        string bidDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.SubmitBidRequest)));
        var bid = await setup.Cohorts.SubmitBidAsync(winner.Execution!, new(request.IdempotencyKey, bidDigest), offer.Value.Id, request, default);
        Assert.True(bid.IsSuccess, bid.Error.Message);

        current = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);
        Assert.True(current.IsSuccess, current.Error.Message);
        AwardWorkRequest awardRequest = new(Guid.NewGuid(), offer.Value.Revision, current.Value.Revision, bid.Value.Id);
        string awardDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(awardRequest, GuildJsonContext.Default.AwardWorkRequest)));
        var award = await setup.Cohorts.AwardAsync(setup.Owner, new(awardRequest.IdempotencyKey, awardDigest), offer.Value.Id, awardRequest, default);
        Assert.True(award.IsSuccess, award.Error.Message);

        var work = await f.Store.GetAsync(setup.Owner, award.Value.QuestId, default);
        Assert.True(work.IsSuccess, work.Error.Message);
        Assert.Equal(members[0].Quest.Id, work.Value.Quest.ParentQuestId);
        Assert.Equal(members[1].Quest.AgentId, work.Value.Quest.AgentId);

        string child = award.Value.QuestId.ToString("D").ToUpperInvariant();
        string issuingQuest = members[0].Quest.Id.ToString("D").ToUpperInvariant();
        string winningQuest = members[1].Quest.Id.ToString("D").ToUpperInvariant();
        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_quest_budget c JOIN guild_budget_accounts a ON a.AccountId=c.AccountId JOIN guild_quest_budget p ON p.AccountId=a.ParentAccountId WHERE c.QuestId='{child}' AND p.QuestId='{issuingQuest}'"));
        Assert.Equal(0L, await f.ScalarAsync($"SELECT count(*) FROM guild_quest_budget c JOIN guild_budget_accounts a ON a.AccountId=c.AccountId JOIN guild_quest_budget p ON p.AccountId=a.ParentAccountId WHERE c.QuestId='{child}' AND p.QuestId='{winningQuest}'"));
        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_work_awards WHERE QuestId='{child}'"));
    }
}
