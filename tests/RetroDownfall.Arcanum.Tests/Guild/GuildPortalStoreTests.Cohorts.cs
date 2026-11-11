using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Forged_cohort_address_is_refused_as_unauthorized()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var result = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id,
            Progress() with { Address = new(GuildCircleAddressKind.Cohort, null, Guid.NewGuid(), null) }, default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Cohort_fanout_freezes_exact_members_and_withdrawal_refuses_queued_read()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var rootDriver = await f.DriverAsync(root);

        var first = await f.ChildAsync(root, rootDriver);

        root = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

        var second = await f.ChildAsync(root, rootDriver);

        root = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

        var late = await f.ChildAsync(root, rootDriver);

        var firstDriver = await f.DriverAsync(first);

        var secondDriver = await f.DriverAsync(second);

        Guid cohort = Guid.NewGuid(), account = Guid.NewGuid(), firstMember = Guid.NewGuid(), secondMember = Guid.NewGuid(), lateMember = Guid.NewGuid();

        await f.ExecuteAsync($"""
            INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
            VALUES('{account:D}',1,'{cohort:D}','{account:D}',1);
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
            VALUES('{cohort:D}','{root.Quest.Id:D}','{root.Quest.Id:D}','cohort',1,'text/plain','X','{account:D}',1,1,2);
            INSERT INTO guild_cohort_members(Id,CohortId,AgentId,PrimaryVotingQuestId,AdmissionSequence,StateCode)
            VALUES('{firstMember:D}','{cohort:D}','{first.Quest.AgentId:D}','{first.Quest.Id:D}',1,1),
                  ('{secondMember:D}','{cohort:D}','{second.Quest.AgentId:D}','{second.Quest.Id:D}',2,1);
            INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
            VALUES('{firstMember:D}','{first.Quest.Id:D}',1,1,1),('{secondMember:D}','{second.Quest.Id:D}',1,1,1);
            """.ToUpperInvariant().Replace("TEXT/PLAIN", "text/plain").Replace("'X'", "'" + GuildSignetGrantJson.Encode(f.Host.Grant).Value + "'"));

        GuildPublishRequest request = Progress() with { Address = new(GuildCircleAddressKind.Cohort, null, cohort, null) };

        var published = await f.Portal.PublishAsync(f.RunCaller(firstDriver), first.Quest.Id, request, default);

        Assert.True(published.IsSuccess, published.Error.Message);

        Assert.Equal(1L, await f.CountAsync("guild_portal_cohort_audience"));

        var claim = await f.Portal.ClaimAsync(secondDriver, default);

        Assert.True(claim.IsSuccess, claim.Error.Message);

        var body = await f.Portal.ReadClaimAsync(claim.Value, default);

        Assert.True(body.IsSuccess, body.Error.Message);

        await f.ExecuteAsync($"""
            UPDATE guild_cohorts SET MembershipVersion=3,Revision=Revision+1 WHERE Id='{cohort:D}';
            INSERT INTO guild_cohort_members(Id,CohortId,AgentId,PrimaryVotingQuestId,AdmissionSequence,StateCode)
            VALUES('{lateMember:D}','{cohort:D}','{late.Quest.AgentId:D}','{late.Quest.Id:D}',3,1);
            INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
            VALUES('{lateMember:D}','{late.Quest.Id:D}',1,1,1);
            """.ToUpperInvariant().Replace("TEXT/PLAIN", "text/plain").Replace("'X'", "'" + GuildSignetGrantJson.Encode(f.Host.Grant).Value + "'"));

        Assert.Equal(1L, await f.CountAsync("guild_portal_cohort_audience"));

        var replay = await f.Portal.PublishAsync(f.RunCaller(firstDriver), first.Quest.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(published.Value.MessageId, replay.Value.MessageId);

        var lateDriver = await f.DriverAsync(late);

        var lateClaim = await f.Portal.ClaimAsync(lateDriver, default);

        Assert.True(lateClaim.IsFailure);

        await f.ExecuteAsync($"""
            UPDATE guild_cohorts SET MembershipVersion=4,Revision=Revision+1 WHERE Id='{cohort:D}';
            UPDATE guild_cohort_members SET StateCode=2,RemovalSequence=4 WHERE Id='{secondMember:D}';
            """.ToUpperInvariant().Replace("TEXT/PLAIN", "text/plain").Replace("'X'", "'" + GuildSignetGrantJson.Encode(f.Host.Grant).Value + "'"));

        var revoked = await f.Portal.ReadClaimAsync(claim.Value, default);

        Assert.True(revoked.IsFailure);

        var application = await f.Portal.ApplyAsync(claim.Value, default);

        Assert.True(application.IsFailure);
    }
    [Fact]
    public async Task Cohort_event_pages_include_own_lifecycle_but_exclude_nested_cohort_events()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var member = await f.ChildAsync(root, driver);

        var memberDriver = await f.DriverAsync(member);

        var nestedIssuer = await f.ChildAsync(member, memberDriver);

        Guid own = Guid.NewGuid(), nested = Guid.NewGuid(), otherNested = Guid.NewGuid(), ownAccount = Guid.NewGuid(),
            nestedAccount = Guid.NewGuid(), otherAccount = Guid.NewGuid(), memberId = Guid.NewGuid();

        string grant = GuildSignetGrantJson.Encode(f.Host.Grant).Value;

        await f.ExecuteAsync($"""
            INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
            VALUES('{ownAccount:D}',1,'{own:D}','{ownAccount:D}',1),('{nestedAccount:D}',1,'{nested:D}','{nestedAccount:D}',1),
                  ('{otherAccount:D}',1,'{otherNested:D}','{otherAccount:D}',1);
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
            VALUES('{own:D}','{root.Quest.Id:D}','{root.Quest.Id:D}','own',1,'text/plain','X','{ownAccount:D}',1,1,0),
                  ('{nested:D}','{root.Quest.Id:D}','{member.Quest.Id:D}','nested',1,'text/plain','X','{nestedAccount:D}',1,1,0),
                  ('{otherNested:D}','{root.Quest.Id:D}','{nestedIssuer.Quest.Id:D}','other nested',1,'text/plain','X','{otherAccount:D}',1,1,0);
            UPDATE guild_cohorts SET MembershipVersion=1,ActiveMemberCount=1,Revision=Revision+1 WHERE Id='{own:D}';
            INSERT INTO guild_cohort_members(Id,CohortId,AgentId,PrimaryVotingQuestId,AdmissionSequence,StateCode)
            VALUES('{memberId:D}','{own:D}','{member.Quest.AgentId:D}','{member.Quest.Id:D}',1,1);
            INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
            VALUES('{memberId:D}','{member.Quest.Id:D}',1,1,1);
            """.ToUpperInvariant().Replace("TEXT/PLAIN", "text/plain").Replace("'X'", "'" + grant + "'"));

        foreach ((Guid objectId, Guid issuer, GuildEventKind kind) in new[]
        {
            (own, root.Quest.Id, GuildEventKind.CohortChanged),
            (nested, member.Quest.Id, GuildEventKind.CohortChanged),
            (otherNested, nestedIssuer.Quest.Id, GuildEventKind.CohortChanged),
            (own, root.Quest.Id, GuildEventKind.MembershipChanged),
        })
        {
            var appended = await f.Authority.InQuestTransactionAsync<long>(root.Quest.Id,
                (connection, transaction, _, ct) => GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    root.Quest.Id, issuer, kind, objectId, 1, null, DateTimeOffset.UtcNow, ct), default);

            Assert.True(appended.IsSuccess, appended.Error.Message);
        }

        List<GuildChangeEventDto> seen = [];

        string? cursor = null;

        do
        {
            var page = await f.Portal.ReadEventsAsync(f.Caller, new(root.Quest.Id, null, own, cursor, 1), default);

            Assert.True(page.IsSuccess, page.Error.Message);

            seen.AddRange(page.Value.Items);

            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        Assert.Contains(seen, item => item.EventKind == GuildEventKind.CohortChanged && item.ObjectId == own);

        Assert.Contains(seen, item => item.EventKind == GuildEventKind.MembershipChanged && item.ObjectId == own);

        Assert.DoesNotContain(seen, item => item.ObjectId == nested || item.ObjectId == otherNested);

        var peerPage = await f.Portal.ReadEventsAsync(f.RunCaller(memberDriver), new(root.Quest.Id, null, own, null, 100), default);

        Assert.True(peerPage.IsSuccess, peerPage.Error.Message);

        Assert.Contains(peerPage.Value.Items, item => item.EventKind == GuildEventKind.CohortChanged && item.ObjectId == own);

        Assert.DoesNotContain(peerPage.Value.Items, item => item.ObjectId == nested || item.ObjectId == otherNested);

        var replay = await f.Portal.ReadEventsAsync(f.RunCaller(memberDriver), new(root.Quest.Id, null, null, null, 100), default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Contains(replay.Value.Items, item => item.EventKind == GuildEventKind.CohortChanged && item.ObjectId == own);

        Assert.Contains(replay.Value.Items, item => item.ObjectId == nested);

        Assert.DoesNotContain(replay.Value.Items, item => item.ObjectId == otherNested);
    }

}
