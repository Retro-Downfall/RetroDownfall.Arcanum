using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task Guild_owner_cleanup_retains_exact_completed_denial_after_cursor_advances()
    {
        RequireSqlCipher();

        (Guid session, _) = await SeedSessionAsync(pinned: false);

        await DeleteGuildTestOwnerAsync();

        var cleaned = await GuildCleanup([]).RunBatchAsync(8, 8, CancellationToken.None);

        Assert.True(cleaned.IsSuccess, cleaned.Error.Message);

        Assert.Equal(1, await CampaignScalarCountAsync($"SELECT COUNT(*) FROM guild_owner_cleanup WHERE OwnerKindCode=2 AND OwnerId='{session.ToString("D").ToUpperInvariant()}' AND PhaseCode=6"));

        var repeated = await GuildCleanup([]).RunBatchAsync(8, 8, CancellationToken.None);

        Assert.True(repeated.IsSuccess, repeated.Error.Message);

        Assert.Equal(0, repeated.Value.OwnersCleaned);
    }

    [SkippableFact]
    public async Task Guild_owner_cleanup_seals_but_retains_uncertain_terminal_work_and_cursor()
    {
        RequireSqlCipher();

        (Guid session, _) = await SeedSessionAsync(pinned: false);

        await SeedRetentionQuestAsync(session);

        await ExecuteAsync("UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1");

        await DeleteGuildTestOwnerAsync();

        var cleaned = await GuildCleanup([new GuildQuestCleanupParticipant(FixtureOrdinaryConnectionFactory.For(_db!), TimeProvider.System)])
            .RunBatchAsync(8, 8, CancellationToken.None);

        Assert.True(cleaned.IsFailure);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_quests WHERE Retired=1 AND CancellationRequested=1"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode=2"));
    }

    [SkippableFact]
    public async Task Guild_linked_terminal_operation_is_never_an_ordinary_history_candidate()
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync();

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.LongRunningOperations.Enabled = true;

        settings.Retention.LongRunningOperations.Days = 1;

        DataRetentionPlan plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.DoesNotContain(plan.CandidateIds, id => id.Contains("22222222-2222-2222-2222-222222222222", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Guild_operation_link_pins_history_even_if_the_operation_kind_is_corrupt()
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync();

        await ExecuteAsync("UPDATE LongRunningOperations SET Kind='other-work'");

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.LongRunningOperations.Enabled = true;

        settings.Retention.LongRunningOperations.Days = 1;

        DataRetentionPlan plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.DoesNotContain(plan.CandidateIds, id => id.Contains("22222222-2222-2222-2222-222222222222", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Guild_terminal_operation_without_its_domain_link_cannot_be_reset_as_zero_work()
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync();

        await ExecuteAsync("DELETE FROM guild_quests");

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(plan.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired);
    }

    [SkippableFact]
    public async Task Guild_orphan_Quest_materializes_the_same_sealed_cleanup_work_without_an_event()
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync(Guid.NewGuid());

        var cleaned = await GuildCleanup([new GuildQuestCleanupParticipant(FixtureOrdinaryConnectionFactory.For(_db!), TimeProvider.System)])
            .RunBatchAsync(8, 8, CancellationToken.None);

        Assert.True(cleaned.IsFailure);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_quests WHERE Retired=1 AND CancellationRequested=1"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_owner_cleanup WHERE EventSequence IS NULL AND PhaseCode<6"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode=2"));
    }

    [SkippableTheory]
    [InlineData("UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1")]
    [InlineData("INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc) VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',zeroblob(32),0,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,1,'2000-01-01T00:00:00.0000000Z','2000-01-01T00:00:00.0000000Z')")]
    [InlineData("INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc) VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),4,1,1,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,'2000-01-01T00:00:00.0000000Z'); INSERT INTO guild_result_outbox(QuestId,ResultId,Digest,DeliveryStateCode,Revision,CreatedAtUtc) VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),1,1,'2000-01-01T00:00:00.0000000Z')")]
    public async Task Guild_terminal_execution_does_not_hide_reset_obligations(string obligation)
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync();

        await ExecuteAsync(obligation);

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(plan.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired);
    }

    [SkippableFact]
    public async Task Guild_settled_terminal_goal_retention_keeps_exact_erasure_and_issue_evidence()
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync();

        await ExecuteAsync("""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),4,1,1,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,'2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            VALUES('CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',1,'11111111-1111-1111-1111-111111111111',1,@digest,'text/plain',X'',0,0,'2000-01-01T00:00:00.0000000Z');
            """, ("@digest", SHA256.HashData([])));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.GuildContent.Enabled = true;

        settings.Retention.GuildContent.Days = 1;

        var service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains("guild-content:CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC", plan.CandidateIds);

        var applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content WHERE ContentKindCode=1"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_quests WHERE Name IS NULL"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content_erasure_receipts WHERE ContentId='CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC' AND ContentKindCode=1 AND Revision=1 AND ByteLength=0"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM LongRunningOperations WHERE Id='22222222222222222222222222222222'"));
    }

    [SkippableFact]
    public async Task Guild_quest_reference_pins_an_unavailable_card_record()
    {
        RequireSqlCipher();

        await SeedRetentionQuestAsync();

        await ExecuteAsync("""
            INSERT INTO guild_agent_cards(CardId,AgentId,SourceKindCode,SourceIdentity,CurrentRevision,Available,CreatedAtUtc,UpdatedAtUtc)
            VALUES('99999999-9999-9999-9999-999999999999','88888888-8888-8888-8888-888888888888',0,'quest-retention',1,0,'2000-01-01T00:00:00.0000000Z','2000-01-01T00:00:00.0000000Z');
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.GuildRecords.Enabled = true;

        settings.Retention.GuildRecords.Days = 1;

        DataRetentionPlan plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.DoesNotContain("guild-record:99999999-9999-9999-9999-999999999999", plan.CandidateIds);
    }

    private async Task SeedRetentionQuestAsync(Guid? sessionId = null)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await _db.Database.OpenConnectionAsync();

        await GuildQuestSchemaTests.SeedQuestAsync(connection, sessionId, SHA256.HashData([]));

        await ExecuteAsync($"UPDATE LongRunningOperations SET State={(int)LongRunningOperationState.Completed},CreatedAt='2000-01-01T00:00:00.0000000Z',CompletedAt='2000-01-01T00:00:00.0000000Z'");
    }
}
