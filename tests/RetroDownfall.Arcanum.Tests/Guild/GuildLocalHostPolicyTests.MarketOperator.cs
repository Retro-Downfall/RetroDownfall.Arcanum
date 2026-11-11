using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Native_operator_awards_under_actual_issuer_and_preserves_member_and_funding_lineage()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await OperatorAwardFixtureAsync(f);

        var award = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);

        Assert.True(award.IsSuccess, award.Error.Message);

        var issued = await f.Store.GetAsync(setup.Owner, award.Value.QuestId, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        Assert.Equal(setup.Issuer.Quest.Id, issued.Value.Quest.ParentQuestId);

        Assert.Equal(setup.Primary.Quest.AgentId, issued.Value.Quest.AgentId);

        Assert.Equal(setup.Bid.MemberId, award.Value.MemberId);

        string child = award.Value.QuestId.ToString("D").ToUpperInvariant();
        string primary = setup.Primary.Quest.Id.ToString("D").ToUpperInvariant();
        string member = setup.Bid.MemberId.ToString("D").ToUpperInvariant();
        string primarySignet = setup.Primary.Quest.SignetId.ToString("D").ToUpperInvariant();
        string issuer = setup.Issuer.Quest.Id.ToString("D").ToUpperInvariant();

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_work_awards a JOIN guild_cohort_member_quests m ON m.QuestId=a.QuestId AND m.MemberId=a.MemberId WHERE a.QuestId='{child}' AND a.PrimaryQuestId='{primary}' AND a.MemberId='{member}' AND a.PrimarySignetId='{primarySignet}' AND m.AssociationKindCode=2"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_quest_budget child JOIN guild_budget_accounts account ON account.AccountId=child.AccountId JOIN guild_cohorts cohort ON cohort.AllocationAccountId=account.ParentAccountId WHERE child.QuestId='{child}' AND cohort.Id='{setup.Offer.CohortId.ToString("D").ToUpperInvariant()}' AND cohort.IssuerQuestId='{issuer}'"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_quests WHERE Id='{child}' AND IssuerKindCode=1 AND IssuerIdentity='{issuer}'"));

        string principal = setup.Owner.PrincipalId!.Value.ToString("D").ToUpperInvariant();

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_work_mutations WHERE PrincipalKindCode=1 AND PrincipalId='{principal}' AND ActionCode=5 AND ResultId='{award.Value.Id.ToString("D").ToUpperInvariant()}'"));

        var current = await setup.Store.GetAsync(setup.IssuerCaller, setup.Offer.CohortId, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await setup.Store.CloseAsync(setup.IssuerCaller, setup.Offer.CohortId, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        var replay = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(award.Value.QuestId, replay.Value.QuestId);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));
    }

    [Fact]
    public async Task Copied_native_operator_is_rejected_and_cannot_submit_member_bid()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await OperatorAwardFixtureAsync(f);

        GuildCallerContext copied = new(GuildCallerKind.Operator, setup.Owner.PrincipalId!.Value, setup.Owner.InvocationId, setup.Owner.InstallationGeneration);

        var award = await setup.Store.AwardAsync(copied, setup.Mutation, setup.Offer.Id, setup.Request, default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, award.Error.Code);

        SubmitBidRequest bid = new(Guid.NewGuid(), setup.Offer.Revision, "Impersonation", null);

        string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(bid, GuildJsonContext.Default.SubmitBidRequest)));

        var submission = await setup.Store.SubmitBidAsync(null!, new(bid.IdempotencyKey, digest), setup.Offer.Id, bid, default);

        Assert.Equal(ErrorCodes.Guild.InvalidRequest, submission.Error.Code);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_bids"));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("primary")]
    public async Task Native_operator_award_rechecks_revoked_issuer_and_primary(string subject)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await OperatorAwardFixtureAsync(f);

        GuildQuestStored target = subject == "issuer" ? setup.Issuer : setup.Primary;

        var current = await f.Store.GetAsync(setup.Owner, target.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Guid key = Guid.NewGuid();

        var revoked = await f.Authority.RevokeAsync(setup.Owner, new(key, new string('A', 64)), current.Value.Quest.SignetId,
            new(key, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

        Assert.True(revoked.IsSuccess, revoked.Error.Message);

        var award = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, award.Error.Code);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_member_quests WHERE AssociationKindCode=2"));
    }

    [Fact]
    public async Task Concurrent_awards_mint_one_child_and_exact_winner_replay()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await OperatorAwardFixtureAsync(f);

        AwardWorkRequest other = setup.Request with { IdempotencyKey = Guid.NewGuid() };

        string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(other, GuildJsonContext.Default.AwardWorkRequest)));

        GuildMutationIdentity otherMutation = new(other.IdempotencyKey, digest);

        long accounts = await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts");
        long quests = await f.ScalarAsync("SELECT count(*) FROM guild_quests");

        var results = await Task.WhenAll(setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default),
            setup.Store.AwardAsync(setup.Owner, otherMutation, setup.Offer.Id, other, default));

        var winner = Assert.Single(results, result => result.IsSuccess);

        Assert.Single(results, result => result.IsFailure);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));

        Assert.Equal(accounts + 1, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(quests + 1, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        bool first = results[0].IsSuccess;

        var replay = await setup.Store.AwardAsync(setup.Owner, first ? setup.Mutation : otherMutation, setup.Offer.Id,
            first ? setup.Request : other, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(winner.Value.QuestId, replay.Value.QuestId);

        Assert.True(replay.Value.Receipt!.Replayed);
    }

    [Fact]
    public async Task Concurrent_award_and_close_commit_exactly_one_and_loser_allocates_nothing()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await OperatorAwardFixtureAsync(f);

        long accounts = await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts");
        long quests = await f.ScalarAsync("SELECT count(*) FROM guild_quests");

        GuildRevisionRequest close = new(Guid.NewGuid(), setup.Request.ExpectedCohortRevision);

        Task<Result<WorkAwardDto>> awardTask = setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);
        Task<Result<CohortDto>> closeTask = setup.Store.CloseAsync(setup.IssuerCaller, setup.Offer.CohortId, close, default);

        await Task.WhenAll(awardTask, closeTask);

        var award = await awardTask;
        var closed = await closeTask;

        Assert.NotEqual(award.IsSuccess, closed.IsSuccess);

        Assert.Equal(award.IsSuccess ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));

        Assert.Equal(accounts + (award.IsSuccess ? 1 : 0), await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(quests + (award.IsSuccess ? 1 : 0), await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        if (award.IsSuccess)
        {
            var replay = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(award.Value.QuestId, replay.Value.QuestId);

            Assert.True(replay.Value.Receipt!.Replayed);
        }
    }

    private static async Task<(GuildCallerContext Owner, GuildCallerContext IssuerCaller, GuildQuestStored Issuer, GuildQuestStored Primary, GuildCallerContext PrimaryCaller, GuildCohortStore Store,
        WorkOfferDto Offer, WorkBidDto Bid, AwardWorkRequest Request, GuildMutationIdentity Mutation)> OperatorAwardFixtureAsync(Fixture f)
    {
        var setup = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 0);

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Operator award reviewer", "Reviewer", "1",
            [new("review", "Review", "Review work", [], ["text/plain"], ["text/plain"])],
            ["text/plain"], ["text/plain"], new(false, false, false),
            new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

        var saved = await cards.UpsertCardAsync(new("operator-award-reviewer", null, null, null,
            new(card, "Review work", [], RequestedToolsSpecified: true), new string('A', 64)), default);

        Assert.True(saved.IsSuccess, saved.Error.Message);

        Guid memberKey = Guid.NewGuid();

        var member = await f.Store.IssueCohortMemberAsync(setup.Issuer,
            new(f.Generation, setup.Root.Quest.Id, null, memberKey, memberKey), setup.Cohort.Id, setup.Cohort.Revision,
            new(memberKey, setup.Root.Quest.Revision, null, "Reviewer", "Review", new(), CardId: saved.Value.Payload.Card.CardId), default);

        Assert.True(member.IsSuccess, member.Error.Message);

        GuildCallerContext memberRun = await BindCohortRunAsync(f, setup.Owner, member.Value);

        var cohort = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        PostWorkOfferRequest post = new(Guid.NewGuid(), cohort.Value.Revision, "review", "Review this work", null, new("text/plain"));

        string postDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(post, GuildJsonContext.Default.PostWorkOfferRequest)));

        var offer = await setup.Cohorts.PostOfferAsync(setup.Issuer, new(post.IdempotencyKey, postDigest), setup.Cohort.Id, post, default);

        Assert.True(offer.IsSuccess, offer.Error.Message);

        SubmitBidRequest bidRequest = new(Guid.NewGuid(), offer.Value.Revision, "I can review", null);

        string bidDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(bidRequest, GuildJsonContext.Default.SubmitBidRequest)));

        var bid = await setup.Cohorts.SubmitBidAsync(memberRun.Execution!, new(bidRequest.IdempotencyKey, bidDigest), offer.Value.Id, bidRequest, default);

        Assert.True(bid.IsSuccess, bid.Error.Message);

        cohort = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        AwardWorkRequest request = new(Guid.NewGuid(), offer.Value.Revision, cohort.Value.Revision, bid.Value.Id);

        string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.AwardWorkRequest)));

        return (setup.Owner, setup.Issuer, setup.Root, member.Value, memberRun, setup.Cohorts, offer.Value, bid.Value, request, new(request.IdempotencyKey, digest));
    }
}
