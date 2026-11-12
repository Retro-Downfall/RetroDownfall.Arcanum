using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Completed_member_award_is_not_a_primary_vote_but_the_primary_result_selects_once()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var setup = await ConvergenceFixtureAsync(f, GuildConvergenceKind.First, null, "text/plain", 0);

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        List<GuildQuestStored> members = [];

        for (int index = 0; index < 2; index++)
        {
            GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Member " + index, "Reviewer", "1",
                [new("review", "Review", "Review work", [], ["text/plain"], ["text/plain"])],
                ["text/plain"], ["text/plain"], new(false, false, false),
                new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

            var saved = await cards.UpsertCardAsync(new("award-no-extra-vote-" + index, null, null, null,
                new(card, "Review", [], RequestedToolsSpecified: true), new string('A', 64)), default);

            Assert.True(saved.IsSuccess, saved.Error.Message);

            var currentCohort = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);

            var currentRoot = await f.Store.GetAsync(setup.Owner, setup.Root.Quest.Id, default);

            Assert.True(currentCohort.IsSuccess, currentCohort.Error.Message);

            Assert.True(currentRoot.IsSuccess, currentRoot.Error.Message);

            Guid key = Guid.NewGuid();

            var member = await f.Store.IssueCohortMemberAsync(setup.Issuer, new(f.Generation, setup.Root.Quest.Id, null, key, key),
                setup.Cohort.Id, currentCohort.Value.Revision,
                new(key, currentRoot.Value.Quest.Revision, null, "Member", "Review", new(), CardId: saved.Value.Payload.Card.CardId), default);

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

        string workId = work.Value.Quest.Id.ToString("D").ToUpperInvariant();

        string issuerId = members[0].Quest.Id.ToString("D").ToUpperInvariant();

        string primaryId = members[1].Quest.Id.ToString("D").ToUpperInvariant();

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_work_awards WHERE QuestId='{workId}' AND PrimaryQuestId='{primaryId}'"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_cohort_member_quests a JOIN guild_cohort_members m ON m.Id=a.MemberId WHERE a.QuestId='{workId}' AND a.AssociationKindCode=2 AND m.PrimaryVotingQuestId='{primaryId}'"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_quest_budget c JOIN guild_budget_accounts a ON a.AccountId=c.AccountId JOIN guild_quest_budget p ON p.AccountId=a.ParentAccountId WHERE c.QuestId='{workId}' AND p.QuestId='{issuerId}'"));

        Assert.Equal(0L, await f.ScalarAsync($"SELECT count(*) FROM guild_quest_budget c JOIN guild_budget_accounts a ON a.AccountId=c.AccountId JOIN guild_quest_budget p ON p.AccountId=a.ParentAccountId WHERE c.QuestId='{workId}' AND p.QuestId='{primaryId}'"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections"));

        await CommitConvergenceResultAsync(f, setup.Owner, work.Value, "finished award work");

        var completedWork = await f.Store.GetAsync(setup.Owner, work.Value.Quest.Id, default);

        var primaryBefore = await f.Store.GetAsync(setup.Owner, members[1].Quest.Id, default);

        Assert.True(completedWork.IsSuccess, completedWork.Error.Message);

        Assert.Equal(QuestState.Completed, completedWork.Value.Quest.State);

        Assert.NotNull(completedWork.Value.Quest.ResultReceiptId);

        Assert.True(primaryBefore.IsSuccess, primaryBefore.Error.Message);

        Assert.Null(primaryBefore.Value.Quest.ResultReceiptId);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections"));

        var open = await setup.Cohorts.ReconcileAsync(setup.Owner, setup.Cohort.Id, default);

        Assert.True(open.IsSuccess, open.Error.Message);

        Assert.Null(open.Value.Decision);

        current = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await setup.Cohorts.CloseAsync(setup.Issuer, setup.Cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        CohortConvergenceDto waiting = await WaitForConvergenceAsync(setup.Cohorts, setup.Owner, setup.Cohort.Id,
            value => value.Decision is null);

        Assert.Null(waiting.Decision);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections"));

        await CommitConvergenceResultAsync(f, setup.Owner, members[1], "primary member result", retainedRun: winner);

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_cohort_contributions WHERE PrimaryVotingQuestId='{primaryId}' AND ResultSequence=1"));

        Assert.Equal(0L, await f.ScalarAsync($"SELECT count(*) FROM guild_cohort_contributions WHERE PrimaryVotingQuestId='{workId}'"));

        CohortConvergenceDto selected = await WaitForConvergenceAsync(setup.Cohorts, setup.Owner, setup.Cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Success, selected.Decision);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections WHERE ThroughSequence=1"));

        Assert.Equal(1L, await f.ScalarAsync($"""
            SELECT count(*) FROM guild_cohort_selections s
            JOIN guild_cohort_eval_candidates c ON c.CohortId=s.CohortId AND c.Generation=s.Generation
            JOIN guild_cohort_members m ON m.Id=c.MemberId
            WHERE s.KindCode=2 AND c.ResultSequence=s.ThroughSequence AND c.OutcomeCode=1
              AND m.PrimaryVotingQuestId='{primaryId}' COLLATE NOCASE
            """));

        var primaryAfter = await f.Store.GetAsync(setup.Owner, members[1].Quest.Id, default);

        Assert.True(primaryAfter.IsSuccess, primaryAfter.Error.Message);

        Assert.NotNull(primaryAfter.Value.Quest.ResultReceiptId);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions"));
    }
}
