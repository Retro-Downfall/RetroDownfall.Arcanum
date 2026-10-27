using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Campaign_retention_eligibility_excludes_protected_rows_and_prunes_clean_neighbors(bool contribution)
    {
        RequireSqlCipher();
        (Guid protectedId, _) = await SeedRetentionSummaryAsync(contribution, OldTimestamp, sensitive: true);
        (Guid cleanId, _) = await SeedRetentionSummaryAsync(contribution, OldTimestamp);
        await AssertCampaignRetentionKeepsProtectedAsync(contribution, protectedId, cleanId);
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Campaign_retention_eligibility_excludes_normalized_self_labels(bool contribution, bool compactIdentity)
    {
        RequireSqlCipher();
        (Guid protectedId, Guid campaign) = await SeedRetentionSummaryAsync(contribution, OldTimestamp);
        await LabelRetentionSummaryAsync(contribution, protectedId, campaign, compactIdentity);
        (Guid cleanId, _) = await SeedRetentionSummaryAsync(contribution, OldTimestamp);
        await AssertCampaignRetentionKeepsProtectedAsync(contribution, protectedId, cleanId);
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Campaign_retention_eligibility_excludes_clean_contributors_to_protected_rollups(bool compactIdentity, bool missingLabel)
    {
        RequireSqlCipher();
        (Guid protectedContributor, Guid campaign) = await SeedRetentionSummaryAsync(true, OldTimestamp);
        (Guid protectedRollup, _) = await SeedRetentionSummaryAsync(false, DateTimeOffset.UtcNow.ToString("O"), campaign, sensitive: true);
        if (!missingLabel)
            await LabelRetentionSummaryAsync(false, protectedRollup, campaign, compactIdentity);
        await ExecuteAsync("""
            INSERT INTO campaign_rollup_sources(RollupArtifactId,SessionId,CampaignId,RollupRevision,
                ContributionArtifactId,ContributionRevision,ContentDigest,SensitivityDigest,SummarizedThroughSequence)
            SELECT @rollup,SessionId,CampaignId,1,ArtifactId,Revision,ContentDigest,SensitivityDigest,SummarizedThroughSequence
            FROM campaign_contribution_artifacts WHERE ArtifactId = @contribution;
            """, ("@rollup", Canonical(protectedRollup)), ("@contribution", Canonical(protectedContributor)));
        (Guid cleanId, _) = await SeedRetentionSummaryAsync(true, OldTimestamp);
        await AssertCampaignRetentionKeepsProtectedAsync(true, protectedContributor, cleanId);
        Assert.Equal(1, await CountAsync("campaign_rollup_artifacts", "ArtifactId", Canonical(protectedRollup)));
    }

    [SkippableTheory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task Campaign_retention_cutoff_preserves_canonical_tick_precision(bool contribution, long offsetTicks)
    {
        RequireSqlCipher();
        FakeTimeProvider clock = new();
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567));
        DateTimeOffset cutoff = clock.GetUtcNow().AddDays(-1);
        (Guid artifact, _) = await SeedRetentionSummaryAsync(contribution, UtcInstantText.Format(cutoff.AddTicks(offsetTicks)));
        ArcanumSettings settings = CreatePruneSettings();
        settings.Retention.CampaignSummaries = EnabledRule();
        DataRetentionService service = CreateService(settings, timeProvider: clock);
        DataRetentionRequest request = new(DataRetentionOperation.Prune);
        DataRetentionPlan plan = await service.PlanAsync(request);
        Assert.Equal(offsetTicks <= 0 ? 1 : 0, plan.CandidateIds.Length);
        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));
        Assert.True(applied.IsSuccess, applied.Error.Message);
        Assert.Empty(applied.Value.Conflicts);
        Assert.Equal(offsetTicks <= 0 ? 1 : 0, applied.Value.RowsDeleted);
        Assert.Equal(offsetTicks <= 0 ? 0 : 1, await CountAsync(contribution ? "campaign_contribution_artifacts" : "campaign_rollup_artifacts", "ArtifactId", Canonical(artifact)));
    }

    [SkippableFact]
    public async Task Campaign_retention_cutoff_does_not_expand_when_the_clock_advances_after_planning()
    {
        RequireSqlCipher();
        FakeTimeProvider clock = new();
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        (Guid oldArtifact, _) = await SeedRetentionSummaryAsync(false, UtcInstantText.Format(clock.GetUtcNow().AddDays(-1).AddSeconds(-1)));
        (Guid laterArtifact, _) = await SeedRetentionSummaryAsync(false, UtcInstantText.Format(clock.GetUtcNow().AddDays(-1).AddSeconds(1)));
        ArcanumSettings settings = CreatePruneSettings();
        settings.Retention.CampaignSummaries = EnabledRule();
        HeartbeatCountingOperationStore operations = new(new LongRunningOperationStore(_db!, TestOrdinaryConnectionFactory.For(_db!)))
        {
            AfterStartAsync = _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                return Task.CompletedTask;
            },
        };
        DataRetentionService service = CreateService(settings, timeProvider: clock, operationStore: operations);
        DataRetentionRequest request = new(DataRetentionOperation.Prune);
        DataRetentionPlan plan = await service.PlanAsync(request);
        Assert.Equal("campaign-rollup:" + Canonical(oldArtifact), Assert.Single(plan.CandidateIds));
        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));
        Assert.True(applied.IsSuccess, applied.Error.Message);
        Assert.Empty(applied.Value.Conflicts);
        Assert.Equal(1, applied.Value.RowsDeleted);
        Assert.Equal(0, await CountAsync("campaign_rollup_artifacts", "ArtifactId", Canonical(oldArtifact)));
        Assert.Equal(1, await CountAsync("campaign_rollup_artifacts", "ArtifactId", Canonical(laterArtifact)));
    }

    [SkippableTheory]
    [InlineData("self-label")]
    [InlineData("dependent-label")]
    [InlineData("dependent-code")]
    public async Task Campaign_retention_late_protection_after_operation_start_refuses_the_atomic_delete(string mode)
    {
        RequireSqlCipher();
        (Guid contribution, Guid campaign) = await SeedRetentionSummaryAsync(true, OldTimestamp);
        (Guid rollup, _) = await SeedRetentionSummaryAsync(false, DateTimeOffset.UtcNow.ToString("O"), campaign);
        await ExecuteAsync("""
            INSERT INTO campaign_rollup_sources(RollupArtifactId,SessionId,CampaignId,RollupRevision,
                ContributionArtifactId,ContributionRevision,ContentDigest,SensitivityDigest,SummarizedThroughSequence)
            SELECT @rollup,SessionId,CampaignId,1,ArtifactId,Revision,ContentDigest,SensitivityDigest,SummarizedThroughSequence
            FROM campaign_contribution_artifacts WHERE ArtifactId = @contribution;
            """, ("@rollup", Canonical(rollup)), ("@contribution", Canonical(contribution)));
        bool protectedAfterStart = false;
        HeartbeatCountingOperationStore operations = new(new LongRunningOperationStore(_db!, TestOrdinaryConnectionFactory.For(_db!)))
        {
            AfterStartAsync = async _ =>
            {
                if (mode == "dependent-code")
                {
                    await ExecuteAsync("DROP TRIGGER campaign_rollup_artifacts_guard_update;");
                    await ExecuteAsync("UPDATE campaign_rollup_artifacts SET SensitivityCode = 1 WHERE ArtifactId = @id;", ("@id", Canonical(rollup)));
                    await ExecuteAsync(Assert.Single(GrimoireSchemaCatalog.CoreObjects,
                        item => item.Name == "campaign_rollup_artifacts_guard_update").Sql);
                }
                else await LabelRetentionSummaryAsync(mode == "self-label", mode == "self-label" ? contribution : rollup, campaign, compactIdentity: true);
                protectedAfterStart = true;
            },
        };
        ArcanumSettings settings = CreatePruneSettings();
        settings.Retention.CampaignSummaries = EnabledRule();
        DataRetentionService service = CreateService(settings, operationStore: operations);
        DataRetentionRequest request = new(DataRetentionOperation.Prune);
        DataRetentionPlan plan = await service.PlanAsync(request);
        Assert.Equal("campaign-contribution:" + Canonical(contribution), Assert.Single(plan.CandidateIds));
        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));
        Assert.True(protectedAfterStart);
        Assert.True(applied.IsSuccess, applied.Error.Message);
        Assert.Equal(0, applied.Value.RowsDeleted);
        Assert.Equal(ErrorCodes.Data.PlanChanged, Assert.Single(applied.Value.Conflicts).Code);
        Assert.Equal(1, await CountAsync("campaign_contribution_artifacts", "ArtifactId", Canonical(contribution)));
        Assert.Equal(1, await CountAsync("campaign_rollup_artifacts", "ArtifactId", Canonical(rollup)));
    }

    [SkippableFact]
    public async Task Campaign_retention_preserves_contributions_shared_by_clean_and_protected_manifest_prefixes()
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
        for (int index = 0; index < 2; index++)
        {
            Guid session = Guid.NewGuid();
            using IDisposable binding = CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);
            await ExecuteAsync("""
                INSERT INTO Sessions(Id,CampaignId,CreatedAt,UpdatedAt) VALUES(@session,@campaign,@now,@now);
                INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES(@session,2,@campaign,@now);
                INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES(@entry,@session,1,'project decision','',@now,1);
                """, ("@session", Canonical(session)), ("@campaign", Canonical(campaign)),
                ("@entry", Canonical(Guid.NewGuid())), ("@now", UtcInstantText.Format(DateTimeOffset.UtcNow)));
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
        }

        await LabelRetentionSummaryAsync(false, roots[1], campaign, compactIdentity: true);
        await ExecuteAsync("DROP TRIGGER campaign_contribution_artifacts_guard_update;");
        await ExecuteAsync("UPDATE campaign_contribution_artifacts SET CreatedAtUtc = @old WHERE ArtifactId = @id;",
            ("@old", UtcInstantText.Format(DateTimeOffset.Parse(OldTimestamp, System.Globalization.CultureInfo.InvariantCulture))),
            ("@id", Canonical(contributions[0])));
        await ExecuteAsync(Assert.Single(GrimoireSchemaCatalog.CoreObjects,
            item => item.Name == "campaign_contribution_artifacts_guard_update").Sql);
        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM campaign_rollup_sources;"));
        Assert.Equal(2, await CountAsync("campaign_rollup_source_membership", "ContributionArtifactId", Canonical(contributions[0])));
        (Guid cleanId, _) = await SeedRetentionSummaryAsync(true, OldTimestamp);
        await AssertCampaignRetentionKeepsProtectedAsync(true, contributions[0], cleanId);
        foreach (Guid root in roots)
            Assert.Equal(1, await CountAsync("campaign_rollup_artifacts", "ArtifactId", Canonical(root)));
        Assert.Equal(2, await CountAsync("campaign_rollup_source_membership", "ContributionArtifactId", Canonical(contributions[0])));
        Assert.Equal(1, await CountAsync("campaign_contribution_artifacts", "ArtifactId", Canonical(contributions[1])));
    }

    [SkippableFact]
    public async Task Campaign_retention_legacy_membership_schema_preserves_protected_closures()
    {
        RequireSqlCipher();
        // Recreate the complete pre-shared-provenance shape, including its membership triggers.
        foreach (string name in new[]
        {
            "campaign_contribution_state_campaign_delete",
            "campaign_contribution_state_campaign_update",
        })
        {
            string sql = Assert.Single(GrimoireSchemaCatalog.CoreObjects, item => item.Name == name).Sql;
            await ExecuteAsync($"DROP TRIGGER {name};");
            await ExecuteAsync(sql.Replace("campaign_rollup_source_membership", "campaign_rollup_sources", StringComparison.Ordinal));
        }

        foreach (string name in new[]
        {
            "campaign_rollup_artifacts_manifest_insert",
            "campaign_rollup_artifacts_manifest_delete",
            "campaign_rollup_sources_manifest_guard",
            "campaign_rollup_manifest_sources_campaign_delete",
        })
            await ExecuteAsync($"DROP TRIGGER {name};");
        await ExecuteAsync("DROP VIEW campaign_rollup_source_membership;");
        await ExecuteAsync("DROP INDEX idx_campaign_rollup_artifacts_manifest;");
        await ExecuteAsync("ALTER TABLE campaign_rollup_artifacts DROP COLUMN ManifestId;");
        await ExecuteAsync("DROP TABLE campaign_rollup_manifest_sources;");
        await ExecuteAsync("DROP TABLE campaign_rollup_manifests;");
        Assert.False(await CampaignRollupSourceStorage.IsInstalledAsync(_db!.Database.GetDbConnection(), null, CancellationToken.None));
        (Guid protectedContribution, Guid campaign) = await SeedRetentionSummaryAsync(true, OldTimestamp);
        (Guid protectedRollup, _) = await SeedRetentionSummaryAsync(false, DateTimeOffset.UtcNow.ToString("O"), campaign, sensitive: true);
        await LabelRetentionSummaryAsync(false, protectedRollup, campaign, compactIdentity: true);
        await ExecuteAsync("""
            INSERT INTO campaign_rollup_sources(RollupArtifactId,SessionId,CampaignId,RollupRevision,
                ContributionArtifactId,ContributionRevision,ContentDigest,SensitivityDigest,SummarizedThroughSequence)
            SELECT @rollup,SessionId,CampaignId,1,ArtifactId,Revision,ContentDigest,SensitivityDigest,SummarizedThroughSequence
            FROM campaign_contribution_artifacts WHERE ArtifactId = @contribution;
            """, ("@rollup", Canonical(protectedRollup)), ("@contribution", Canonical(protectedContribution)));
        (Guid cleanId, _) = await SeedRetentionSummaryAsync(true, OldTimestamp);
        await AssertCampaignRetentionKeepsProtectedAsync(true, protectedContribution, cleanId);
        Assert.Equal(1, await CountAsync("campaign_rollup_artifacts", "ArtifactId", Canonical(protectedRollup)));
    }

    private async Task AssertCampaignRetentionKeepsProtectedAsync(bool contribution, Guid protectedId, Guid cleanId)
    {
        ArcanumSettings settings = CreatePruneSettings();
        settings.Retention.CampaignSummaries = EnabledRule();
        DataRetentionService service = CreateService(settings);
        DataRetentionRequest request = new(DataRetentionOperation.Prune);
        DataRetentionPlan plan = await service.PlanAsync(request);
        string prefix = contribution ? "campaign-contribution:" : "campaign-rollup:";
        Assert.Equal(prefix + Canonical(cleanId), Assert.Single(plan.CandidateIds));
        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));
        Assert.True(applied.IsSuccess, applied.Error.Message);
        Assert.Empty(applied.Value.Conflicts);
        Assert.Equal(1, applied.Value.RowsDeleted);
        string table = contribution ? "campaign_contribution_artifacts" : "campaign_rollup_artifacts";
        Assert.Equal(1, await CountAsync(table, "ArtifactId", Canonical(protectedId)));
        Assert.Equal(0, await CountAsync(table, "ArtifactId", Canonical(cleanId)));
        Assert.Empty((await service.PlanAsync(request)).CandidateIds);
    }

    private async Task LabelRetentionSummaryAsync(bool contribution, Guid artifact, Guid campaign, bool compactIdentity)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();
        Guid? session = null;
        if (contribution)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT SessionId FROM campaign_contribution_artifacts WHERE ArtifactId = @id;";
            command.Parameters.AddWithValue("@id", Canonical(artifact));
            session = Guid.Parse((string)(await command.ExecuteScalarAsync())!);
        }

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Result<LabeledArtifactWriteReceipt> written = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction,
                new DerivedArtifactWrite(contribution ? SensitiveArtifactKind.CampaignContribution : SensitiveArtifactKind.CampaignRollup,
                    artifact, session, campaign, null, 1, new CovenantDigest(SHA256.HashData(Encoding.UTF8.GetBytes("persisted project decision"))),
                    ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()])), CancellationToken.None);
            Assert.True(written.IsSuccess, written.Error.Message);
            await transaction.CommitAsync();
        }

        if (compactIdentity)
        {
            // Reproduce a supported historical spelling, then restore the production write guard.
            await ExecuteAsync("DROP TRIGGER artifact_sensitivity_guard_update;");
            await ExecuteAsync("UPDATE artifact_sensitivity SET ArtifactId = @compact WHERE ArtifactId = @id;",
                ("@compact", artifact.ToString("N").ToLowerInvariant()), ("@id", Canonical(artifact)));
            await ExecuteAsync(Assert.Single(GrimoireSchemaCatalog.CoreObjects,
                item => item.Name == "artifact_sensitivity_guard_update").Sql);
        }
    }
}
