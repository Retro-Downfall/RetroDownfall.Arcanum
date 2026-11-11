using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Offer_without_an_eligible_recipient_is_refused_without_creating_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 0);

        Guid key = Guid.NewGuid();

        PostWorkOfferRequest request = new(key, setup.Cohort.Revision, "review", "Review this work", null, new("text/plain"));

        string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.PostWorkOfferRequest)));

        var result = await setup.Cohorts.PostOfferAsync(setup.Issuer, new(key, digest), setup.Cohort.Id, request, default);

        Assert.Equal(ErrorCodes.Guild.NoEligibleBidder, result.Error.Code);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
    }

    [Fact]
    public async Task Offer_freezes_eligible_card_and_exact_replay_creates_no_additional_offer()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 0);

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Reviewer", "Reviewer", "1",
            [new("review", "Review", "Review work", [], ["text/plain"], ["text/plain"])],
            ["text/plain"], ["text/plain"], new(false, false, false),
            new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

        var saved = await cards.UpsertCardAsync(new("market-reviewer", null, null, null,
            new(card, "Review work", [], RequestedToolsSpecified: true), new string('A', 64)), default);

        Assert.True(saved.IsSuccess, saved.Error.Message);

        Guid memberKey = Guid.NewGuid();

        var member = await f.Store.IssueCohortMemberAsync(setup.Issuer,
            new(f.Generation, setup.Root.Quest.Id, null, memberKey, memberKey), setup.Cohort.Id, setup.Cohort.Revision,
            new(memberKey, setup.Root.Quest.Revision, null, "Reviewer", "Review", new(), CardId: saved.Value.Payload.Card.CardId), default);

        Assert.True(member.IsSuccess, member.Error.Message);

        var current = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Guid key = Guid.NewGuid();

        PostWorkOfferRequest request = new(key, current.Value.Revision, "review", "Review this work", null, new("text/plain"));

        string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.PostWorkOfferRequest)));

        var result = await setup.Cohorts.PostOfferAsync(setup.Issuer, new(key, digest), setup.Cohort.Id, request, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        var replay = await setup.Cohorts.PostOfferAsync(setup.Issuer, new(key, digest), setup.Cohort.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.Equal(result.Value.Id, replay.Value.Id);
        Assert.True(replay.Value.Receipt!.Replayed);
        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_offers"));
        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_offer_recipients"));

        string distinctKey = Guid.NewGuid().ToString("D").ToUpperInvariant();
        string distinctReceipt = Guid.NewGuid().ToString("D").ToUpperInvariant();
        await Assert.ThrowsAsync<SqliteException>(() => f.ExecuteAsync($"""
            INSERT INTO guild_work_mutations(PrincipalKindCode,PrincipalId,ActionCode,ParentId,IdempotencyKey,RequestDigest,ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc)
            SELECT PrincipalKindCode,'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',ActionCode,ParentId,'{distinctKey}',RequestDigest,ResultId,'{distinctReceipt}',AcceptedRevision,AcceptedAtUtc
            FROM guild_work_mutations WHERE ActionCode=1 LIMIT 1;
            """));
        await Assert.ThrowsAsync<SqliteException>(() => f.ExecuteAsync($"""
            INSERT INTO guild_work_mutations(PrincipalKindCode,PrincipalId,ActionCode,ParentId,IdempotencyKey,RequestDigest,ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc)
            SELECT PrincipalKindCode,PrincipalId,ActionCode,ParentId,'{distinctKey}',RequestDigest,ResultId,'{distinctReceipt}',AcceptedRevision,'2026-01-01T00:00:00+00:00'
            FROM guild_work_mutations WHERE ActionCode=1 LIMIT 1;
            """));

        Guid malformedKey = Guid.NewGuid();
        PostWorkOfferRequest malformedReplay = request with { IdempotencyKey = malformedKey };
        string malformedDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(malformedReplay, GuildJsonContext.Default.PostWorkOfferRequest)));
        string malformedId = malformedKey.ToString("D").ToUpperInvariant();
        string malformedReceipt = Guid.NewGuid().ToString("D").ToUpperInvariant();
        await f.ExecuteAsync($"""
            INSERT INTO guild_work_mutations(PrincipalKindCode,PrincipalId,ActionCode,ParentId,IdempotencyKey,RequestDigest,ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc)
            SELECT PrincipalKindCode,PrincipalId,ActionCode,ParentId,'{malformedId}',X'{malformedDigest}',ResultId,'{malformedReceipt}',AcceptedRevision,'2026-01-01 00:00:00.0000000Z'
            FROM guild_work_mutations WHERE ActionCode=1 LIMIT 1;
            """);
        var refusedMalformed = await setup.Cohorts.PostOfferAsync(setup.Issuer,
            new(malformedKey, malformedDigest), setup.Cohort.Id, malformedReplay, default);
        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, refusedMalformed.Error.Code);

        var afterFirst = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);
        Assert.True(afterFirst.IsSuccess, afterFirst.Error.Message);
        Guid secondKey = Guid.NewGuid();
        PostWorkOfferRequest secondPost = new(secondKey, afterFirst.Value.Revision, "review", "Review another item", null, new("text/plain"));
        string secondDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(secondPost, GuildJsonContext.Default.PostWorkOfferRequest)));
        var second = await setup.Cohorts.PostOfferAsync(setup.Issuer, new(secondKey, secondDigest), setup.Cohort.Id, secondPost, default);
        Assert.True(second.IsSuccess, second.Error.Message);
        GuildRevisionRequest withdraw = new(Guid.NewGuid(), second.Value.Revision);
        string withdrawDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(withdraw, GuildJsonContext.Default.GuildRevisionRequest)));
        var withdrawn = await setup.Cohorts.WithdrawOfferAsync(setup.Issuer, new(withdraw.IdempotencyKey, withdrawDigest), second.Value.Id, withdraw, default);
        Assert.True(withdrawn.IsSuccess, withdrawn.Error.Message);
        Assert.Equal(GuildWorkOfferState.Withdrawn, withdrawn.Value.State);
        var withdrawReplay = await setup.Cohorts.WithdrawOfferAsync(setup.Issuer, new(withdraw.IdempotencyKey, withdrawDigest), second.Value.Id, withdraw, default);
        Assert.True(withdrawReplay.IsSuccess, withdrawReplay.Error.Message);
        Assert.True(withdrawReplay.Value.Receipt!.Replayed);

        var beforeClose = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);
        Assert.True(beforeClose.IsSuccess, beforeClose.Error.Message);
        var closed = await setup.Cohorts.CloseAsync(setup.Issuer, setup.Cohort.Id, new(Guid.NewGuid(), beforeClose.Value.Revision), default);
        Assert.True(closed.IsSuccess, closed.Error.Message);
        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_offers WHERE StateCode=4"));

        await Assert.ThrowsAsync<SqliteException>(() => f.ExecuteAsync(
            "UPDATE guild_work_offers SET BidSequence=BidSequence+1 WHERE StateCode=4"));

        var closedReplay = await setup.Cohorts.PostOfferAsync(setup.Issuer, new(key, digest), setup.Cohort.Id, request, default);
        Assert.True(closedReplay.IsSuccess, closedReplay.Error.Message);
        Assert.Equal(result.Value.Id, closedReplay.Value.Id);
        Assert.True(closedReplay.Value.Receipt!.Replayed);
    }

    [Fact]
    public async Task Award_commits_one_child_and_exact_replay_survives_cohort_close()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 0);

        GuildStore cards = new(f.Connections, f.Gate, TimeProvider.System);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Award reviewer", "Reviewer", "1",
            [new("review", "Review", "Review work", [], ["text/plain"], ["text/plain"])],
            ["text/plain"], ["text/plain"], new(false, false, false),
            new(GuildAgentSourceKind.DeclaredGlobal, null, null, true, null));

        var saved = await cards.UpsertCardAsync(new("award-reviewer", null, null, null,
            new(card, "Review work", [], RequestedToolsSpecified: true), new string('A', 64)), default);

        Assert.True(saved.IsSuccess, saved.Error.Message);

        Guid memberKey = Guid.NewGuid();

        var member = await f.Store.IssueCohortMemberAsync(setup.Issuer,
            new(f.Generation, setup.Root.Quest.Id, null, memberKey, memberKey), setup.Cohort.Id, setup.Cohort.Revision,
            new(memberKey, setup.Root.Quest.Revision, null, "Reviewer", "Review", new(), CardId: saved.Value.Payload.Card.CardId), default);

        Assert.True(member.IsSuccess, member.Error.Message);

        var memberRun = await BindCohortRunAsync(f, setup.Owner, member.Value);
        var cohort = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);
        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        Guid offerKey = Guid.NewGuid();
        PostWorkOfferRequest post = new(offerKey, cohort.Value.Revision, "review", "Review this work", null, new("text/plain"));
        string postDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(post, GuildJsonContext.Default.PostWorkOfferRequest)));
        var offer = await setup.Cohorts.PostOfferAsync(setup.Issuer, new(offerKey, postDigest), setup.Cohort.Id, post, default);
        Assert.True(offer.IsSuccess, offer.Error.Message);

        Guid bidKey = Guid.NewGuid();
        SubmitBidRequest bidRequest = new(bidKey, offer.Value.Revision, "I can review", null);
        string bidDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(bidRequest, GuildJsonContext.Default.SubmitBidRequest)));
        var bid = await setup.Cohorts.SubmitBidAsync(memberRun.Execution!, new(bidKey, bidDigest), offer.Value.Id, bidRequest, default);
        Assert.True(bid.IsSuccess, bid.Error.Message);

        cohort = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);
        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        Guid key = Guid.NewGuid();
        AwardWorkRequest request = new(key, offer.Value.Revision, cohort.Value.Revision, bid.Value.Id);
        string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.AwardWorkRequest)));
        var awarded = await setup.Cohorts.AwardAsync(setup.Issuer, new(key, digest), offer.Value.Id, request, default);
        Assert.True(awarded.IsSuccess, awarded.Error.Message);

        var work = await f.Store.GetAsync(setup.Owner, awarded.Value.QuestId, default);
        Assert.True(work.IsSuccess, work.Error.Message);
        Assert.Equal(setup.Root.Quest.Id, work.Value.Quest.ParentQuestId);
        Assert.Equal(member.Value.Quest.AgentId, work.Value.Quest.AgentId);
        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));
        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_member_quests WHERE AssociationKindCode=2"));

        cohort = await setup.Cohorts.GetAsync(setup.Issuer, setup.Cohort.Id, default);
        Assert.True(cohort.IsSuccess, cohort.Error.Message);
        var closed = await setup.Cohorts.CloseAsync(setup.Issuer, setup.Cohort.Id, new(Guid.NewGuid(), cohort.Value.Revision), default);
        Assert.True(closed.IsSuccess, closed.Error.Message);

        var replay = await setup.Cohorts.AwardAsync(setup.Issuer, new(key, digest), offer.Value.Id, request, default);
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.Equal(awarded.Value.QuestId, replay.Value.QuestId);
        Assert.True(replay.Value.Receipt!.Replayed);
        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));
    }
}
