using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Primary_terminal_receipt_assigns_durable_vote_sequence_and_keeps_exact_designated_bytes()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        await ConfirmReceiptDispatchAsync(f, driver);

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        string quest = row.Quest.Id.ToString("D").ToUpperInvariant();

        string account = Guid.NewGuid().ToString("D").ToUpperInvariant();

        string agent = row.Quest.AgentId.ToString("D").ToUpperInvariant();

        string cohort = Guid.NewGuid().ToString("D").ToUpperInvariant();

        string member = Guid.NewGuid().ToString("D").ToUpperInvariant();

        string grant = JsonSerializer.Serialize(row.Quest.ResolvedGrant, GuildJsonContext.Default.GuildGrant).Replace("'", "''", StringComparison.Ordinal);

        await f.ExecuteAsync($"""
            INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
            VALUES('{account}',0,'{quest}','{account}',1);
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
            VALUES('{cohort}','{quest}','{quest}','panel',1,'text/plain','{grant}','{account}',1,1,1);
            INSERT INTO guild_cohort_members(Id,CohortId,AgentId,PrimaryVotingQuestId,AdmissionSequence,StateCode)
            VALUES('{member}','{cohort}','{agent}','{quest}',1,1);
            INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
            VALUES('{member}','{quest}',1,1,1);
            """);

        Assert.Equal(1L, await ReceiptScalarAsync(f, """
            SELECT COUNT(*) FROM guild_cohort_members m JOIN guild_cohort_member_quests a ON a.MemberId=m.Id
            JOIN guild_signets s ON s.QuestId=a.QuestId
            WHERE m.AgentId=s.AgentId AND m.PrimaryVotingQuestId=s.QuestId AND m.StateCode=1 AND a.Active=1
            """));

        byte[] designated = Encoding.UTF8.GetBytes("exact primary output");

        GuildQuestResult result = ReceiptResult(row.Quest.Id) with
        {
            DesignatedResultCount = 1,
            DesignatedResultUtf8 = designated,
            DesignatedResultMimeType = "text/plain",
        };

        var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("cohort-result")), row.Quest.Revision, result, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        Assert.Equal(1L, await ReceiptScalarAsync(f, "SELECT ResultSequence FROM guild_cohort_contributions"));

        Assert.Equal("exact primary output", await ReceiptScalarAsync(f, "SELECT CAST(c.Body AS TEXT) FROM guild_cohort_contributions v JOIN guild_content c ON c.ContentId=v.ContentId"));

        Assert.Equal(1L, await ReceiptScalarAsync(f, "SELECT ResultSequence FROM guild_cohorts"));

        var replay = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("cohort-result")), row.Quest.Revision, result, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        var changed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("cohort-result")), row.Quest.Revision,
            result with { DesignatedResultUtf8 = Encoding.UTF8.GetBytes("different primary output") }, default);

        Assert.True(changed.IsFailure);

        Assert.Equal("Guild.ResultConflict", changed.Error.Code);

        Assert.Equal(1L, await ReceiptScalarAsync(f, "SELECT COUNT(*) FROM guild_cohort_contributions"));
    }
}
