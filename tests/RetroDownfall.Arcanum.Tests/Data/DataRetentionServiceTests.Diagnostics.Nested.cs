using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prune_nested_campaign_label_refusal_keeps_the_actual_preservation_cause(bool rollup)
    {
        RequireSqlCipher();

        (Guid contribution, Guid campaign) = await SeedRetentionSummaryAsync(true, OldTimestamp);

        Guid artifact = contribution;

        if (rollup)
        {
            (artifact, _) = await SeedRetentionSummaryAsync(false, OldTimestamp, campaign);

            await ExecuteAsync("""
                INSERT INTO campaign_rollup_sources(RollupArtifactId,SessionId,CampaignId,RollupRevision,
                    ContributionArtifactId,ContributionRevision,ContentDigest,SensitivityDigest,SummarizedThroughSequence)
                SELECT @rollup,SessionId,CampaignId,1,ArtifactId,Revision,ContentDigest,SensitivityDigest,SummarizedThroughSequence
                FROM campaign_contribution_artifacts WHERE ArtifactId=@contribution;
                """, ("@rollup", Canonical(artifact)), ("@contribution", Canonical(contribution)));
        }

        Guid entry = Guid.NewGuid();

        await ExecuteAsync("""
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
            SELECT @entry,SessionId,1,'retained source','test',@at,1
            FROM campaign_contribution_artifacts WHERE ArtifactId=@contribution;
            """, ("@entry", Canonical(entry)), ("@at", OldTimestamp), ("@contribution", Canonical(contribution)));

        (_, Guid survivor) = await SeedSessionAsync(pinned: false);

        HeartbeatCountingOperationStore operations = new(new LongRunningOperationStore(_db!, TestOrdinaryConnectionFactory.For(_db!)))
        {
            AfterStartAsync = _ => LabelRetentionSummaryAsync(!rollup, artifact, campaign, compactIdentity: false),
        };

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        DataRetentionService service = CreateService(settings, operationStore: operations);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains("entry:" + entry.ToString("D"), plan.CandidateIds);

        var applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(1, await CountAsync("Entries", "Id", Canonical(entry)));

        Assert.Equal(0, await CountAsync("Entries", "Id", Canonical(survivor)));

        Assert.Contains(applied.Value.Conflicts, conflict => conflict.Code == "Covenant.ForbiddenAuthority"
            && conflict.ResourceId.Contains(entry.ToString("D"), StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(applied.Value.Conflicts, static conflict => conflict.Code == "Data.PlanChanged");
    }
}
