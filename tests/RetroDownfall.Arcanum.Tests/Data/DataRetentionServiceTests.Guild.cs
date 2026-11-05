using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task Guild_content_prune_preserves_current_card_content_and_erasure_evidence()
    {
        RequireSqlCipher();

        Guid unused = Guid.NewGuid();

        Guid current = Guid.NewGuid();

        await SeedCleanupContentAsync(unused, Guid.NewGuid());

        await SeedCleanupContentAsync(current, Guid.NewGuid());

        await ExecuteAsync("""
            INSERT INTO guild_agent_cards(CardId,AgentId,SourceKindCode,SourceIdentity,CurrentRevision,ContentId,Available,CreatedAtUtc,UpdatedAtUtc)
            VALUES(@card,@card,0,'retention-card',1,@content,1,'2000-01-01T00:00:00.0000000Z','2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_content_erasure_receipts(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,ErasedAtUtc)
            VALUES(@receipt,0,@receipt,1,zeroblob(32),'text/plain',2,'2000-01-01T00:00:00.0000000Z');
            """, ("@card", Guid.NewGuid().ToString("D").ToUpperInvariant()), ("@content", current.ToString("D").ToUpperInvariant()),
            ("@receipt", Guid.NewGuid().ToString("D").ToUpperInvariant()));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.GuildContent.Enabled = true;

        settings.Retention.GuildContent.Days = 1;

        var service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains("guild-content:" + unused.ToString("D").ToUpperInvariant(), plan.CandidateIds);

        Assert.DoesNotContain("guild-content:" + current.ToString("D").ToUpperInvariant(), plan.CandidateIds);

        var applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content;"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content_erasure_receipts;"));
    }

    [SkippableFact]
    public async Task Guild_factory_plan_includes_payload_records_and_evidence()
    {
        RequireSqlCipher();

        await SeedCleanupContentAsync(Guid.NewGuid(), Guid.NewGuid());

        await ExecuteAsync("""
            INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,CreatedAtUtc,UpdatedAtUtc)
            VALUES(2,@owner,'2000-01-01T00:00:00.0000000Z','2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_declared_roots(RootKey,SourceKindCode,WorkspaceId,InstallationGeneration,PathDigest,ScanId,StateCode)
            VALUES('factory-root',0,NULL,@owner,@digest,@owner,1);
            INSERT INTO guild_declared_source_claims(SourceIdentity,RootKey,ExplicitAgentId,LastSeenScanId,ObservedValid,SourceDigest)
            VALUES('factory-source','factory-root',NULL,@owner,0,NULL);
            """, ("@owner", Guid.NewGuid().ToString("D").ToUpperInvariant()), ("@digest", new string('A',64)));

        var service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains(plan.Items, item => item.DataClass == RetentionDataClass.GuildContent && item.Rows == 1);

        Assert.Contains(plan.Items, item => item.DataClass == RetentionDataClass.GuildEvidence && item.DerivedRecords == 3);

        var recovered = await ReconcileFactoryResetV0Async(service, "guild-factory-reset-recovery");

        Assert.Equal(1, recovered.Summary.Completed);

        Assert.Equal(0, recovered.Summary.RequiresAttention);

        Assert.Equal(LongRunningOperationState.Completed, recovered.Operation.State);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content;"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_owner_cleanup;"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_declared_source_claims;"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_declared_roots;"));
    }

    [SkippableFact]
    public async Task Guild_classes_are_inventoried_even_when_feature_is_disabled()
    {
        RequireSqlCipher();

        DataRetentionStatus status = await CreateService().GetStatusAsync();

        foreach (RetentionDataClass dataClass in new[] { RetentionDataClass.GuildContent, RetentionDataClass.GuildRecords, RetentionDataClass.GuildEvidence })
        {
            DataRetentionStatusItem item = Assert.Single(status.Items, candidate => candidate.DataClass == dataClass);

            Assert.Equal(0, item.Rows);

            Assert.False(item.PolicyEnabled);
        }
    }
}
