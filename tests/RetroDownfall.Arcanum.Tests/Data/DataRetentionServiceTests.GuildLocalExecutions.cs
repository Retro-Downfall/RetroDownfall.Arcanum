using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task Factory_reset_refuses_local_execution_evidence_whose_quest_link_is_missing()
    {
        RequireSqlCipher();

        await ExecuteAsync("""
            INSERT INTO guild_local_executions(QuestId,ApprenticeId,RunId,SessionId,ParentQuestId,RootQuestId,
                ParentApprenticeId,InputContentId,InputRevision,InputDigest,InputByteLength)
            VALUES('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222',
                '33333333-3333-3333-3333-333333333333','44444444-4444-4444-4444-444444444444',NULL,
                '11111111-1111-1111-1111-111111111111',NULL,'55555555-5555-5555-5555-555555555555',1,zeroblob(32),2);
            """);

        var plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(plan.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired
            && conflict.ResourceId == "11111111-1111-1111-1111-111111111111");

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_local_executions"));
    }
}
