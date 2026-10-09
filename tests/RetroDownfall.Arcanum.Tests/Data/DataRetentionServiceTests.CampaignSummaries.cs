using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Campaign_summary_pruning_removes_aged_derived_context_and_preserves_fresh_other_owners(bool contribution)
    {
        RequireSqlCipher();

        (Guid oldArtifact, Guid oldOwner) = await SeedRetentionSummaryAsync(contribution, OldTimestamp);

        (Guid freshArtifact, Guid freshOwner) = await SeedRetentionSummaryAsync(contribution, DateTimeOffset.UtcNow.ToString("O"));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.CampaignSummaries = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        string prefix = contribution ? "campaign-contribution:" : "campaign-rollup:";

        Assert.Equal(prefix + oldArtifact.ToString("D").ToUpperInvariant(), Assert.Single(plan.CandidateIds));

        Assert.Contains(plan.Items, item => item.DataClass == RetentionDataClass.CampaignSummaries && item.Rows == 1);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        string table = contribution ? "campaign_contribution_artifacts" : "campaign_rollup_artifacts";

        Assert.Equal(0, await CountAsync(table, "ArtifactId", oldArtifact.ToString("D").ToUpperInvariant()));

        Assert.Equal(1, await CountAsync(table, "ArtifactId", freshArtifact.ToString("D").ToUpperInvariant()));

        Assert.Equal(1, await CountAsync("Campaigns", "Id", oldOwner.ToString("D").ToUpperInvariant()));

        Assert.Equal(1, await CountAsync("Campaigns", "Id", freshOwner.ToString("D").ToUpperInvariant()));

        Assert.Equal(2, await CampaignScalarCountAsync("SELECT COUNT(*) FROM Sessions;"));

        string state = contribution ? "campaign_contribution_state" : "campaign_rollup_state";

        Assert.Equal(1, await CampaignScalarCountAsync($"SELECT COUNT(*) FROM {state} WHERE CampaignId = '{oldOwner.ToString("D").ToUpperInvariant()}' AND CurrentArtifactId IS NULL AND Revision = 1 AND RefoldRequired = 1;"));
    }

    [SkippableFact]
    public async Task Campaign_summary_pruning_refuses_a_plan_after_its_policy_is_disabled()
    {
        RequireSqlCipher();

        (Guid artifact, _) = await SeedRetentionSummaryAsync(false, OldTimestamp);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.CampaignSummaries = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Single(plan.CandidateIds);

        settings.Retention.CampaignSummaries.Enabled = false;

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsFailure);

        Assert.Equal(1, await CountAsync("campaign_rollup_artifacts", "ArtifactId", artifact.ToString("D").ToUpperInvariant()));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Campaign_summary_pruning_counts_a_shared_aggregate_closure_once_and_invalidates_a_fresh_dependent(bool agedAggregate)
    {
        RequireSqlCipher();

        (Guid contribution, Guid campaign) = await SeedRetentionSummaryAsync(true, OldTimestamp);

        (Guid rollup, _) = await SeedRetentionSummaryAsync(false,
            agedAggregate ? OldTimestamp : DateTimeOffset.UtcNow.ToString("O"), campaign);

        await ExecuteAsync("""
            INSERT INTO campaign_rollup_sources(RollupArtifactId,SessionId,CampaignId,RollupRevision,
                ContributionArtifactId,ContributionRevision,ContentDigest,SensitivityDigest,SummarizedThroughSequence)
            SELECT @rollup,SessionId,CampaignId,1,ArtifactId,Revision,ContentDigest,SensitivityDigest,SummarizedThroughSequence
            FROM campaign_contribution_artifacts WHERE ArtifactId = @contribution;
            """, ("@rollup", rollup.ToString("D").ToUpperInvariant()), ("@contribution", contribution.ToString("D").ToUpperInvariant()));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.CampaignSummaries = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(agedAggregate ? 2 : 1, plan.Rows);

        Assert.Equal(agedAggregate ? 1 : 2, plan.DerivedRecords);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(plan.Rows, applied.Value.RowsDeleted);

        Assert.Equal(plan.DerivedRecords, applied.Value.DerivedRecordsDeleted);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM campaign_contribution_artifacts;"));

        Assert.Equal(2, await CampaignScalarCountAsync("SELECT COUNT(*) FROM Sessions;"));
    }

    private async Task<(Guid ArtifactId, Guid CampaignId)> SeedRetentionSummaryAsync(bool contribution, string created, Guid? existingCampaign = null)
    {
        Guid campaign = existingCampaign ?? Guid.NewGuid();

        Guid session = Guid.NewGuid();

        Guid artifact = Guid.NewGuid();

        if (existingCampaign is null)
        {
            _db!.Campaigns.Add(new Campaign
            {
                Id = campaign, Name = campaign.ToString("N"), NameLower = campaign.ToString("N"), Path = "/campaigns/" + campaign.ToString("N"),
                Settings = "{}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        _db!.Sessions.Add(new Session { Id = session, CampaignId = campaign, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        _ = await _db.SaveChangesAsync();

        await _db.Database.OpenConnectionAsync();

        string c = campaign.ToString("D").ToUpperInvariant();

        string s = session.ToString("D").ToUpperInvariant();

        string a = artifact.ToString("D").ToUpperInvariant();

        using IDisposable binding = CovenantSqliteConnectionInitializer.Instance.Authorize(
            (Microsoft.Data.Sqlite.SqliteConnection)_db.Database.GetDbConnection(), CovenantSqliteAuthorizationKind.SessionBindingWrite);

        await ExecuteAsync("INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES(@session,2,@campaign,@now);",
            ("@session", s), ("@campaign", c), ("@now", created));

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("persisted project decision"));

        string table = contribution ? "campaign_contribution_artifacts" : "campaign_rollup_artifacts";

        string ownerColumn = contribution ? ",SessionId" : "";

        string ownerValue = contribution ? ",@session" : "";

        string frontierColumn = contribution ? "SummarizedThroughSequence" : "SourceCount";

        await ExecuteAsync($"INSERT INTO {table}(ArtifactId,CampaignId{ownerColumn},Revision,Content,ContentDigest,SensitivityCode,SensitivityDigest,SourceManifestDigest,SourceGeneration,{frontierColumn},CreatedAtUtc) VALUES(@artifact,@campaign{ownerValue},1,'persisted project decision',@digest,0,@digest,@digest,0,0,@now);",
            ("@artifact", a), ("@campaign", c), ("@session", s), ("@digest", digest), ("@now", created));

        if (contribution)
        {
            await ExecuteAsync("INSERT INTO campaign_contribution_state(SessionId,CampaignId,CurrentArtifactId,Revision,SourceGeneration,SummarizedThroughSequence,RefoldRequired,UpdatedAtUtc) VALUES(@session,@campaign,@artifact,1,0,0,0,@now);",
                ("@session", s), ("@campaign", c), ("@artifact", a), ("@now", created));
        }
        else
        {
            await ExecuteAsync("INSERT INTO campaign_rollup_state(CampaignId,CurrentArtifactId,Revision,SourceGeneration,RefoldRequired,UpdatedAtUtc) VALUES(@campaign,@artifact,1,0,0,@now);",
                ("@campaign", c), ("@artifact", a), ("@now", created));
        }

        return (artifact, campaign);
    }

    private async Task<long> CampaignScalarCountAsync(string sql)
    {
        await using System.Data.Common.DbCommand command = _db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
