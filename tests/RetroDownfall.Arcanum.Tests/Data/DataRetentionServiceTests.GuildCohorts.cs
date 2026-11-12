using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task Factory_reset_inventory_counts_Cohort_evidence()
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync();

        await SeedBudgetResetAccountsAsync();

        var service = CreateService();

        DataRetentionPlan before = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        long previous = Assert.Single(before.Items, item => item.DataClass == RetentionDataClass.GuildEvidence).DerivedRecords;

        await ExecuteAsync("""
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
            VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',
                '11111111-1111-1111-1111-111111111111','panel',1,'text/plain','{}',
                'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',1,1,0);
            """);

        DataRetentionPlan after = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_operator_sequences WHERE KindCode=1 AND ObjectId='EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE'"));

        Assert.Equal(previous + 2, Assert.Single(after.Items, item => item.DataClass == RetentionDataClass.GuildEvidence).DerivedRecords);
    }
    [SkippableFact]
    public async Task Factory_reset_clears_settled_Cohort_before_its_account_and_issuer()
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync();

        await SeedBudgetResetAccountsAsync();

        await ExecuteAsync("""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),4,1,1,
                'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,'2000-01-01T00:00:00.0000000Z');
            UPDATE guild_budget_accounts SET Closed=1,SettlementReceiptId='FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',Revision=Revision+1;
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
            VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',
                '11111111-1111-1111-1111-111111111111','panel',1,'text/plain','{}',
                'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',4,1,0);
            """);

        var recovered = await ReconcileFactoryResetV0Async(CreateService(), "guild-cohort-reset");

        Assert.Equal(1, recovered.Summary.Completed);

        Assert.Equal(0, recovered.Summary.RequiresAttention);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_cohorts"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_budget_accounts"));
    }
    [SkippableFact]
    public async Task Factory_reset_refuses_an_open_Cohort_even_when_its_issuer_is_settled()
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync();

        await SeedBudgetResetAccountsAsync();

        await ExecuteAsync("""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),4,1,1,
                'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,'2000-01-01T00:00:00.0000000Z');
            UPDATE guild_budget_accounts SET Closed=1,SettlementReceiptId='FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',Revision=Revision+1;
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
            VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',
                '11111111-1111-1111-1111-111111111111','panel',1,'text/plain','{}',
                'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',1,1,0);
            """);

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(plan.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired);
    }
}
