using System.Text;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Cohort_peers_with_the_same_root_cannot_publish_or_claim_across_their_cohort()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver rootDriver = await f.DriverAsync(root);

        List<GuildQuestStored> children = [];

        for (int i = 0; i < 4; i++)
        {
            root = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

            children.Add(await f.ChildAsync(root, rootDriver));
        }

        GuildQuestDriver[] drivers = [];

        foreach (GuildQuestStored child in children)
        {
            drivers = [.. drivers, await f.DriverAsync(child)];
        }

        Guid first = Guid.NewGuid(), second = Guid.NewGuid();

        string grant = GuildSignetGrantJson.Encode(f.Host.Grant).Value;

        async Task InstallCohort(Guid cohort, int start)
        {
            Guid account = Guid.NewGuid(), memberA = Guid.NewGuid(), memberB = Guid.NewGuid();

            await f.ExecuteAsync($"""
                INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
                VALUES('{account:D}',1,'{cohort:D}','{account:D}',1);
                INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
                VALUES('{cohort:D}','{root.Quest.Id:D}','{root.Quest.Id:D}','cohort',1,'text/plain','X','{account:D}',1,1,2);
                INSERT INTO guild_cohort_members(Id,CohortId,AgentId,PrimaryVotingQuestId,AdmissionSequence,StateCode)
                VALUES('{memberA:D}','{cohort:D}','{children[start].Quest.AgentId:D}','{children[start].Quest.Id:D}',1,1),
                      ('{memberB:D}','{cohort:D}','{children[start + 1].Quest.AgentId:D}','{children[start + 1].Quest.Id:D}',2,1);
                INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
                VALUES('{memberA:D}','{children[start].Quest.Id:D}',1,1,1),('{memberB:D}','{children[start + 1].Quest.Id:D}',1,1,1);
                """.ToUpperInvariant().Replace("TEXT/PLAIN", "text/plain").Replace("'X'", "'" + grant + "'"));
        }

        await InstallCohort(first, 0);

        await InstallCohort(second, 2);

        GuildPublishRequest foreign = Progress() with { Address = new(GuildCircleAddressKind.Cohort, null, second, null), Body = "wrong cohort" };

        var rejected = await f.Portal.PublishAsync(f.RunCaller(drivers[0]), children[0].Quest.Id, foreign, default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, rejected.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));

        GuildPublishRequest allowed = Progress() with { Address = new(GuildCircleAddressKind.Cohort, null, first, null), Body = "first cohort only" };

        var published = await f.Portal.PublishAsync(f.RunCaller(drivers[0]), children[0].Quest.Id, allowed, default);

        Assert.True(published.IsSuccess, published.Error.Message);

        var firstClaim = await f.Portal.ClaimAsync(drivers[1], default);

        Assert.True(firstClaim.IsSuccess, firstClaim.Error.Message);

        var firstBody = await f.Portal.ReadClaimAsync(firstClaim.Value, default);

        Assert.True(firstBody.IsSuccess, firstBody.Error.Message);

        Assert.Equal("first cohort only", Encoding.UTF8.GetString(firstBody.Value));

        Assert.True((await f.Portal.ClaimAsync(drivers[2], default)).IsFailure);

        Assert.True((await f.Portal.ClaimAsync(drivers[3], default)).IsFailure);

        GuildPublishRequest secondAllowed = Progress() with { Address = new(GuildCircleAddressKind.Cohort, null, second, null), Body = "second cohort only" };

        var secondPublished = await f.Portal.PublishAsync(f.RunCaller(drivers[2]), children[2].Quest.Id, secondAllowed, default);

        Assert.True(secondPublished.IsSuccess, secondPublished.Error.Message);

        var secondClaim = await f.Portal.ClaimAsync(drivers[3], default);

        Assert.True(secondClaim.IsSuccess, secondClaim.Error.Message);

        var secondBody = await f.Portal.ReadClaimAsync(secondClaim.Value, default);

        Assert.True(secondBody.IsSuccess, secondBody.Error.Message);

        Assert.Equal("second cohort only", Encoding.UTF8.GetString(secondBody.Value));

        Assert.Equal(2L, await f.CountAsync("guild_envelopes"));
    }
}
