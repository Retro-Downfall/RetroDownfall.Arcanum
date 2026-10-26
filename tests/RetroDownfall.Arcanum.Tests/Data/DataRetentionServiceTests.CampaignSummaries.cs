using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Data.Covenant;

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

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Shared_summary_prune_plans_complete_selected_root_reclamation_and_matches_physical_deletes(
        bool retainSuccessor, bool selectNativeOwner)
    {
        RequireSqlCipher();

        Guid campaign = Guid.NewGuid();

        _db!.Campaigns.Add(new Campaign
        {
            Id = campaign,
            Name = campaign.ToString("N"),
            NameLower = campaign.ToString("N"),
            Path = "/campaigns/" + campaign.ToString("N"),
            Settings = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync();

        await _db.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        CampaignRollupStore store = new(new FixedCovenantConnectionSource(connection), CovenantSqliteConnectionInitializer.Instance);

        List<Guid> roots = [];

        List<Guid> contributions = [];

        Guid selectedEntry = Guid.Empty;

        CampaignRollupArtifact? successor = null;

        for (int index = 0; index < (retainSuccessor ? 4 : 3); index++)
        {
            Guid session = Guid.NewGuid();

            Guid entry = Guid.NewGuid();

            if (index == 2)
            {
                selectedEntry = entry;
            }

            using IDisposable binding = CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

            await ExecuteAsync("""
                INSERT INTO Sessions(Id,CampaignId,CreatedAt,UpdatedAt) VALUES(@session,@campaign,@now,@now);
                INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES(@session,2,@campaign,@now);
                INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES(@entry,@session,1,'project decision','',@now,1);
                """, ("@session", Canonical(session)), ("@campaign", Canonical(campaign)),
                ("@entry", Canonical(entry)), ("@now", DateTimeOffset.UtcNow.ToString("O")));

            var prepared = await store.PrepareContributionAsync(session, null, null, CancellationToken.None);

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            var contribution = await store.PublishContributionAsync(prepared.Value!, "project decision", null, null, CancellationToken.None);

            Assert.True(contribution.IsSuccess, contribution.Error.Message);

            contributions.Add(contribution.Value.ArtifactId);

            var fold = await store.PrepareRollupAsync(campaign, null, CancellationToken.None);

            Assert.True(fold.IsSuccess, fold.Error.Message);

            var published = await store.PublishRollupAsync(fold.Value!, "project summary", null, null, CancellationToken.None);

            Assert.True(published.IsSuccess, published.Error.Message);

            roots.Add(published.Value.ArtifactId);

            successor = published.Value;
        }

        // Age only the selected roots; contribution bodies remain outside the summary age rule.
        await ExecuteAsync("DROP TRIGGER campaign_rollup_artifacts_guard_update;");

        foreach (Guid root in roots.Take(selectNativeOwner ? 2 : 3))
        {
            await ExecuteAsync("UPDATE campaign_rollup_artifacts SET CreatedAtUtc = @at WHERE ArtifactId = @id;",
                ("@at", OldTimestamp), ("@id", Canonical(root)));
        }

        await ExecuteAsync(Assert.Single(GrimoireSchemaCatalog.CoreObjects, item => item.Name == "campaign_rollup_artifacts_guard_update").Sql);

        if (selectNativeOwner)
        {
            await ExecuteAsync("UPDATE Entries SET CreatedAt = @at WHERE Id = @id;", ("@at", OldTimestamp), ("@id", Canonical(selectedEntry)));
        }

        HashSet<string> before = await ReadSharedRetentionPhysicalKeysAsync();

        HashSet<string> expectedDeleted = [.. roots.Take(3).Select(id => "campaign_rollup_artifacts:" + Canonical(id))];

        if (!retainSuccessor)
        {
            expectedDeleted.UnionWith(before.Where(key => key.StartsWith("campaign_rollup_manifests:", StringComparison.Ordinal)
                || key.StartsWith("campaign_rollup_manifest_sources:", StringComparison.Ordinal)));
        }

        if (selectNativeOwner)
        {
            expectedDeleted.Add("campaign_contribution_artifacts:" + Canonical(contributions[2]));
        }

        Assert.Equal(retainSuccessor ? 3 : selectNativeOwner ? 10 : 9, expectedDeleted.Count);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.CampaignSummaries = EnabledRule();

        if (selectNativeOwner)
        {
            settings.Retention.Entries = EnabledRule();
        }

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(selectNativeOwner ? 2 : 3, plan.CandidateIds.Count(id => id.StartsWith("campaign-rollup:", StringComparison.Ordinal)));

        Assert.DoesNotContain(plan.CandidateIds, id => id.StartsWith("campaign-contribution:", StringComparison.Ordinal));

        Assert.Equal(expectedDeleted.Count, plan.Items.Where(item => item.DataClass == RetentionDataClass.CampaignSummaries)
            .Sum(item => item.Rows + item.DerivedRecords));

        HashSet<string> plannedKeys = new(StringComparer.Ordinal);

        if (selectNativeOwner)
        {
            Guid owner = await ReadContributionOwnerAsync(contributions[2]);

            _ = await CampaignSummaryLifecycle.ReadSessionClosureAsync(connection, null, owner,
                CancellationToken.None, includeOwnerRows: false, rowKeys: plannedKeys);
        }

        foreach (Guid root in roots.Take(selectNativeOwner ? 2 : 3))
        {
            plannedKeys.UnionWith(await CampaignSummaryLifecycle.ReadArtifactClosureRowKeysAsync(
                connection, null, SensitiveArtifactKind.CampaignRollup, root, CancellationToken.None));
        }

        await CampaignSummaryLifecycle.AddSharedReclamationRowKeysAsync(connection, null, plannedKeys, CancellationToken.None);

        Assert.Equal(expectedDeleted.Order(StringComparer.Ordinal), plannedKeys.Order(StringComparer.Ordinal));

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(plan.Rows, applied.Value.RowsDeleted);

        Assert.Equal(plan.DerivedRecords, applied.Value.DerivedRecordsDeleted);

        HashSet<string> after = await ReadSharedRetentionPhysicalKeysAsync();

        Assert.Equal(expectedDeleted.Order(StringComparer.Ordinal), before.Except(after).Order(StringComparer.Ordinal));

        Assert.Empty(after.Except(before));

        Assert.Equal(contributions.Count - (selectNativeOwner ? 1 : 0), after.Count(key => key.StartsWith("campaign_contribution_artifacts:", StringComparison.Ordinal)));

        if (retainSuccessor)
        {
            Assert.True((await store.ValidateAsync(successor!, null, CancellationToken.None)).IsSuccess);

            Assert.Equal(4, after.Count(key => key.StartsWith("campaign_rollup_manifest_sources:", StringComparison.Ordinal)));
        }
    }

    private async Task<HashSet<string>> ReadSharedRetentionPhysicalKeysAsync()
    {
        await using var command = _db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = """
            SELECT 'campaign_rollup_artifacts:' || ArtifactId FROM campaign_rollup_artifacts
            UNION ALL SELECT 'campaign_contribution_artifacts:' || ArtifactId FROM campaign_contribution_artifacts
            UNION ALL SELECT 'campaign_rollup_manifests:' || ManifestId FROM campaign_rollup_manifests
            UNION ALL SELECT 'campaign_rollup_manifest_sources:' || ManifestId || ':' || SessionId FROM campaign_rollup_manifest_sources;
            """;

        await using var reader = await command.ExecuteReaderAsync();

        HashSet<string> keys = new(StringComparer.Ordinal);

        while (await reader.ReadAsync())
        {
            Assert.True(keys.Add(reader.GetString(0)));
        }

        return keys;
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
                Id = campaign,
                Name = campaign.ToString("N"),
                NameLower = campaign.ToString("N"),
                Path = "/campaigns/" + campaign.ToString("N"),
                Settings = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
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
