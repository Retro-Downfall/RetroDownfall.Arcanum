using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_workspace_root_and_claims_follow_owner_cleanup_and_bounded_orphan_recovery(bool recordedDeletion)
    {
        RequireSqlCipher();

        Guid campaign = Guid.NewGuid();

        if (recordedDeletion)
        {
            await SeedCampaignRowAsync(campaign);
        }

        await ExecuteAsync("""
            INSERT INTO guild_declared_roots(RootKey,SourceKindCode,WorkspaceId,InstallationGeneration,PathDigest,ScanId,StateCode)
            VALUES('workspace-root',1,@owner,@generation,@digest,@generation,1);
            INSERT INTO guild_declared_source_claims(SourceIdentity,RootKey,ExplicitAgentId,LastSeenScanId,ObservedValid,SourceDigest)
            VALUES('workspace-source','workspace-root',NULL,@generation,0,NULL);
            """, ("@owner", Canonical(campaign)), ("@generation", Canonical(Guid.NewGuid())), ("@digest", new string('A',64)));

        if (recordedDeletion)
        {
            await ExecuteAsync("DELETE FROM Campaigns WHERE Id=@owner;", ("@owner", Canonical(campaign)));
        }

        var result = await GuildCleanup([new GuildRosterCleanupParticipant()]).RunBatchAsync(8, 1, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_declared_source_claims;"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_declared_roots;"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_owner_cleanup WHERE PhaseCode<6;"));

        Assert.Equal(1, await CampaignScalarCountAsync($"SELECT COUNT(*) FROM guild_owner_cleanup WHERE PhaseCode=6 AND OwnerKindCode=1 AND OwnerId='{Canonical(campaign)}'"));

        Assert.Equal(1, result.Value.OwnersCleaned);
    }
}
