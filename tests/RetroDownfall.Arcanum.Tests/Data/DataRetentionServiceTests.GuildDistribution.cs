using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task Factory_reset_counts_and_clears_shared_ledger_before_its_Cohort()
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

        var service = CreateService();

        DataRetentionPlan before = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        long evidence = Assert.Single(before.Items, item => item.DataClass == RetentionDataClass.GuildEvidence).DerivedRecords;

        await ExecuteAsync("""
            INSERT INTO guild_cohort_ledger(Id,CohortId,Sequence,AuthorQuestId,AuthorityCode,KindCode,ContentId,
                Digest,ByteLength,ActorKey,IdempotencyKey,RequestDigest,ReceiptId,AcceptedRevision,CreatedAtUtc)
            VALUES('AAAAAAAA-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',1,
                '11111111-1111-1111-1111-111111111111',2,1,'AAAAAAAA-2222-2222-2222-222222222222',
                zeroblob(32),1,'quest:11111111-1111-1111-1111-111111111111','AAAAAAAA-3333-3333-3333-333333333333',
                zeroblob(32),'AAAAAAAA-4444-4444-4444-444444444444',1,'2000-01-01T00:00:00.0000000Z');
            """);

        DataRetentionPlan after = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Equal(evidence + 1, Assert.Single(after.Items, item => item.DataClass == RetentionDataClass.GuildEvidence).DerivedRecords);

        var recovered = await ReconcileFactoryResetV0Async(service, "guild-ledger-reset");

        Assert.Equal(1, recovered.Summary.Completed);

        Assert.Equal(0, recovered.Summary.RequiresAttention);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_cohort_ledger"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_cohorts"));
    }
}
