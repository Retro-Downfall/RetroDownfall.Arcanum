using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_prune_preview_counts_selected_native_owner_closures_once_and_matches_the_committed_totals(bool deleteSessions)
    {
        RequireSqlCipher();

        (Guid firstContribution, Guid campaign) = await SeedRetentionSummaryAsync(true, OldTimestamp);

        (Guid secondContribution, _) = await SeedRetentionSummaryAsync(true, OldTimestamp, campaign);

        Guid firstSession = await ReadContributionOwnerAsync(firstContribution);

        Guid secondSession = await ReadContributionOwnerAsync(secondContribution);

        (Guid freshRollup, _) = await SeedRetentionSummaryAsync(false, DateTimeOffset.UtcNow.ToString("O"), campaign);

        (Guid unrelatedRollup, _) = await SeedRetentionSummaryAsync(false, DateTimeOffset.UtcNow.ToString("O"));

        foreach ((Guid owner, int count) in new[] { (firstSession, 2), (secondSession, 1) })
        {
            for (int sequence = 1; sequence <= count; sequence++)
            {
                await ExecuteAsync("""
                    INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
                    VALUES(@entry,@session,'user','a native project decision','',@at,@sequence);
                    """, ("@entry", Canonical(Guid.NewGuid())), ("@session", Canonical(owner)),
                    ("@at", OldTimestamp), ("@sequence", sequence));
            }

            await ExecuteAsync("UPDATE Sessions SET UpdatedAt = @at WHERE Id = @owner;",
                ("@at", OldTimestamp), ("@owner", Canonical(owner)));

            ISessionSummaryArtifactStore summaries = new SessionDerivedArtifactStore(
                new CovenantConnectionSource(_db!, FixtureOrdinaryConnectionFactory.For(_db!)),
                CovenantSqliteConnectionInitializer.Instance);

            Result<SessionDerivedArtifactWriteReceipt> summary = await summaries.ReplaceAsync(
                new SessionSummaryArtifactWrite(owner, "compressed native history", null,
                    ContentSensitivity.None, GenerationProvenance.CreateExact(Array.Empty<Guid>())),
                CancellationToken.None);

            Assert.True(summary.IsSuccess, summary.Error.Message);
        }

        foreach (Guid contribution in new[] { firstContribution, secondContribution })
        {
            await ExecuteAsync("""
                INSERT INTO campaign_rollup_sources(RollupArtifactId,SessionId,CampaignId,RollupRevision,
                    ContributionArtifactId,ContributionRevision,ContentDigest,SensitivityDigest,SummarizedThroughSequence)
                SELECT @rollup,SessionId,CampaignId,1,ArtifactId,Revision,ContentDigest,SensitivityDigest,SummarizedThroughSequence
                FROM campaign_contribution_artifacts WHERE ArtifactId = @contribution;
                """, ("@rollup", Canonical(freshRollup)), ("@contribution", Canonical(contribution)));
        }

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        settings.Retention.CampaignSummaries = EnabledRule();

        if (deleteSessions)
        {
            settings.Retention.ActiveSessions = EnabledRule();
        }

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(deleteSessions ? 5 : 3, plan.Rows);

        // Three Entry FTS rows, four Session-summary rows, two contributions, one shared rollup,
        // and its two source rows; Session deletion also removes two contribution-state rows.
        Assert.Equal(deleteSessions ? 14 : 12, plan.DerivedRecords);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(plan.Rows, applied.Value.RowsDeleted);

        Assert.Equal(plan.DerivedRecords, applied.Value.DerivedRecordsDeleted);

        Assert.Equal(0, await CountAsync("campaign_rollup_artifacts", "ArtifactId", Canonical(freshRollup)));

        Assert.Equal(0, await CountAsync("campaign_contribution_artifacts", "ArtifactId", Canonical(firstContribution)));

        Assert.Equal(0, await CountAsync("campaign_contribution_artifacts", "ArtifactId", Canonical(secondContribution)));

        Assert.Equal(0, await CountAsync("session_summary_state", "SessionId", Canonical(firstSession)));

        Assert.Equal(0, await CountAsync("session_summary_state", "SessionId", Canonical(secondSession)));

        Assert.Equal(1, await CountAsync("campaign_rollup_artifacts", "ArtifactId", Canonical(unrelatedRollup)));

        Assert.Equal(deleteSessions ? 0 : 1, await CountAsync("Sessions", "Id", Canonical(firstSession)));

        Assert.Equal(deleteSessions ? 0 : 1, await CountAsync("Sessions", "Id", Canonical(secondSession)));
    }

    private async Task<Guid> ReadContributionOwnerAsync(Guid artifactId)
    {
        await using DbCommand command = _db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT SessionId FROM campaign_contribution_artifacts WHERE ArtifactId = @artifact;";

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@artifact";

        parameter.Value = Canonical(artifactId);

        _ = command.Parameters.Add(parameter);

        return Guid.Parse((string)(await command.ExecuteScalarAsync())!);
    }
}
