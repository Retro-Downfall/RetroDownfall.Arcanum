using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Cohort_fanout_second_recipient_fault_rolls_back_every_accepted_row_and_retries()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver rootDriver = await f.DriverAsync(root);

        GuildQuestStored first = await f.ChildAsync(root, rootDriver);

        GuildQuestStored second = await f.ChildAsync(root, rootDriver);

        GuildQuestStored third = await f.ChildAsync(root, rootDriver);

        GuildQuestDriver sender = await f.DriverAsync(first);

        Guid cohort = Guid.NewGuid(), account = Guid.NewGuid();

        Guid firstMember = Guid.NewGuid(), secondMember = Guid.NewGuid(), thirdMember = Guid.NewGuid();

        string grant = GuildSignetGrantJson.Encode(f.Host.Grant).Value;

        await f.ExecuteAsync($"""
            INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
            VALUES('{account:D}',1,'{cohort:D}','{account:D}',1);
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
            VALUES('{cohort:D}','{root.Quest.Id:D}','{root.Quest.Id:D}','cohort',1,'text/plain','X','{account:D}',1,1,3);
            INSERT INTO guild_cohort_members(Id,CohortId,AgentId,PrimaryVotingQuestId,AdmissionSequence,StateCode)
            VALUES('{firstMember:D}','{cohort:D}','{first.Quest.AgentId:D}','{first.Quest.Id:D}',1,1),
                  ('{secondMember:D}','{cohort:D}','{second.Quest.AgentId:D}','{second.Quest.Id:D}',2,1),
                  ('{thirdMember:D}','{cohort:D}','{third.Quest.AgentId:D}','{third.Quest.Id:D}',3,1);
            INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
            VALUES('{firstMember:D}','{first.Quest.Id:D}',1,1,1),('{secondMember:D}','{second.Quest.Id:D}',1,1,1),
                  ('{thirdMember:D}','{third.Quest.Id:D}',1,1,1);
            """.ToUpperInvariant().Replace("TEXT/PLAIN", "text/plain").Replace("'X'", "'" + grant + "'"));

        long contentBefore = await f.CountAsync("guild_content");

        long eventsBefore = await f.CountAsync("guild_events");

        await f.ExecuteAsync("""
            CREATE TRIGGER fixture_second_inbox_fault BEFORE INSERT ON guild_inbox
            WHEN (SELECT count(*) FROM guild_inbox WHERE EnvelopeId=NEW.EnvelopeId)>0
            BEGIN SELECT RAISE(ABORT,'second-recipient fault'); END;
            """);

        GuildPublishRequest request = Progress() with { Address = new(GuildCircleAddressKind.Cohort, null, cohort, null) };

        var failed = await f.Portal.PublishAsync(f.RunCaller(sender), first.Quest.Id, request, default);

        Assert.True(failed.IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_cohort_audience"));

        Assert.Equal(0L, await f.CountAsync("guild_inbox"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_streams"));

        Assert.Equal(contentBefore, await f.CountAsync("guild_content"));

        Assert.Equal(eventsBefore, await f.CountAsync("guild_events"));

        await f.ExecuteAsync("DROP TRIGGER fixture_second_inbox_fault");

        var retried = await f.Portal.PublishAsync(f.RunCaller(sender), first.Quest.Id, request, default);

        Assert.True(retried.IsSuccess, retried.Error.Message);

        Assert.Equal(1L, await f.CountAsync("guild_envelopes"));

        Assert.Equal(2L, await f.CountAsync("guild_portal_cohort_audience"));

        Assert.Equal(2L, await f.CountAsync("guild_inbox"));

        Assert.Equal(contentBefore + 1, await f.CountAsync("guild_content"));

        Assert.Equal(eventsBefore + 1, await f.CountAsync("guild_events"));
    }
}
