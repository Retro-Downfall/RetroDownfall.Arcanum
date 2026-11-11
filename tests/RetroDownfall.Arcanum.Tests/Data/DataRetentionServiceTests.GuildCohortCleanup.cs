using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task Owner_cleanup_defers_for_an_open_Cohort_even_when_the_Quest_is_terminal()
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync(Guid.NewGuid());

        await SeedBudgetResetAccountsAsync();

        await ExecuteAsync("""
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
            VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',
                '11111111-1111-1111-1111-111111111111','private name',1,'text/plain','{}',
                'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',1,1,0);
            """);

        var cleanup = GuildCleanup([new GuildCohortCleanupParticipant(FixtureOrdinaryConnectionFactory.For(_db!), null, null)]);

        var first = await cleanup.RunBatchAsync(8, 8, default);

        Assert.True(first.IsFailure);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_owner_cleanup WHERE PhaseCode<6"));
    }

    [SkippableFact]
    public async Task Owner_cleanup_redacts_terminal_Cohort_name_before_advancing_cursor()
    {
        RequireSqlCipher();

        Guid deletedOwner = Guid.NewGuid();

        await SeedRetentionQuestAsync(deletedOwner);

        await SeedBudgetResetAccountsAsync();

        await ExecuteAsync("""
            UPDATE guild_quests SET Retired=1,CancellationRequested=1,Revision=Revision+1;
            UPDATE guild_budget_accounts SET Closed=1,SettlementReceiptId='FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',Revision=Revision+1;
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
            VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',
                '11111111-1111-1111-1111-111111111111','private name',1,'text/plain','{}',
                'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',4,1,0);
            """);

        await ExecuteAsync("""
            INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,CreatedAtUtc,UpdatedAtUtc)
            VALUES(2,@owner,'2000-01-01T00:00:00.0000000Z','2000-01-01T00:00:00.0000000Z');
            """, ("@owner", deletedOwner.ToString("D").ToUpperInvariant()));

        var cleanup = GuildCleanup([new GuildCohortCleanupParticipant(FixtureOrdinaryConnectionFactory.For(_db!), null, null)]);

        _ = await cleanup.RunBatchAsync(8, 8, default);

        var final = await cleanup.RunBatchAsync(8, 8, default);

        Assert.True(final.IsSuccess, final.Error.Message);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_cohorts WHERE Name IS NULL"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_owner_cleanup WHERE PhaseCode<6"));
    }
    [SkippableFact]
    public async Task Owner_cleanup_retains_cursor_when_terminal_Cohort_issuer_has_late_unknown_accounting()
    {
        RequireSqlCipher();

        Guid deletedOwner = Guid.NewGuid();

        await SeedRetentionQuestAsync(deletedOwner);

        await SeedBudgetResetAccountsAsync();

        await ExecuteAsync("""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),4,1,1,
                'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,'2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
            VALUES('DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD','11111111-1111-1111-1111-111111111111',zeroblob(32),0,
                'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',1,1,'2000-01-01T00:00:00.0000000Z','2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,KnownUsd,KnownTokens,UnknownUsd,StateCode,Revision)
            VALUES('CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','11111111-1111-1111-1111-111111111111',
                'DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',0,
                'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF','0',0,1,2,1);
            INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
            VALUES('BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',zeroblob(32),'0',0,1,0,1,0,1);
            UPDATE guild_budget_accounts SET Closed=1,SettlementReceiptId='FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',Revision=Revision+1;
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,StateCode,Revision,MembershipVersion)
            VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',
                '11111111-1111-1111-1111-111111111111','private name',1,'text/plain','{}',
                'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',4,1,0);
            INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,CreatedAtUtc,UpdatedAtUtc)
            VALUES(2,@owner,'2000-01-01T00:00:00.0000000Z','2000-01-01T00:00:00.0000000Z');
            """, ("@owner", deletedOwner.ToString("D").ToUpperInvariant()));

        await ExecuteAsync("UPDATE guild_quests SET AccountingStateCode=1,Revision=Revision+1");

        var connections = FixtureOrdinaryConnectionFactory.For(_db!);

        var cleanup = GuildCleanup([new GuildQuestCleanupParticipant(connections, TimeProvider.System),
            new GuildCohortCleanupParticipant(connections, null, null)]);

        var result = await cleanup.RunBatchAsync(8, 8, default);

        Assert.True(result.IsFailure);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_quests WHERE AccountingStateCode=1"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_owner_cleanup WHERE PhaseCode<6"));
    }
}
