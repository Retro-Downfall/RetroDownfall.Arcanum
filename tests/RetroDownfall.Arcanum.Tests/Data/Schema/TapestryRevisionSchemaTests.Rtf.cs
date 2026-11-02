using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class TapestryRevisionSchemaTests
{
    [Fact]
    public void Core33_retains_its_published_physical_catalog_and_transition_identity()
    {
        Assert.Equal(CoreSchemaVersionThirtyThreeFixture.PublishedFingerprint, CoreSchemaVersionThirtyThreeFixture.Fingerprint);

        Assert.Equal(CoreSchemaVersionThirtyThreeFixture.PublishedFingerprint,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).SourceDefinitionFingerprintFor(33));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rtf_mutations_stay_out_of_the_revision_corpus_on_fresh_and_upgraded_stores(bool upgrade)
    {
        using var file = EvolutionScratchDatabase.Create();

        object? inheritedRevision = null;

        await using (SqliteConnection initial = await file.OpenAsync(CancellationToken.None))
        {
            await GrimoireSchemaTestInstaller.InstallAsync(initial,
                upgrade ? CoreSchemaVersionThirtyThreeFixture.ChainSet() : GrimoireSchemaVersionChains.Default,
                64, CancellationToken.None);

            await ExecuteAsync(initial, "PRAGMA recursive_triggers=OFF;" + AttachmentOwner() + RtfChunk("first", 0) + RtfChunk("second", 1));

            inheritedRevision = await RevisionsAsync(initial, Session, "SessionAttachment");

            Assert.Equal(upgrade ? "2:0" : null, inheritedRevision);
        }

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, CancellationToken.None);

        Assert.True(installed.Core.IsHealthy);

        Assert.Equal(35, installed.Core.SchemaVersion);

        Assert.Null(await GrimoireSchemaEvolutionInstallerTests.ReadJournalAsync(connection));

        await using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM session_attachment_chunks WHERE Content='source'";

            Assert.Equal(2L, await count.ExecuteScalarAsync());
        }

        // Include scope moves, both replacement keys, and UPDATE OR REPLACE with recursive deletes off.
        string[] mutations =
        [
            "UPDATE session_attachment_chunks SET Content='edited' WHERE ChunkId='first';",
            $"UPDATE session_attachment_chunks SET SessionId='{OtherSession}' WHERE ChunkId='first';",
            RtfChunk("first", 0).Replace("INSERT INTO", "INSERT OR REPLACE INTO", StringComparison.Ordinal),
            RtfChunk("replacement", 0).Replace("INSERT INTO", "INSERT OR REPLACE INTO", StringComparison.Ordinal),
            "UPDATE OR REPLACE session_attachment_chunks SET ChunkIndex=1 WHERE ChunkId='replacement';",
            "DELETE FROM session_attachment_chunks;",
        ];

        await ExecuteAsync(connection, "PRAGMA recursive_triggers=OFF;");

        foreach (string mutation in mutations)
        {
            await ExecuteAsync(connection, mutation);

            Assert.Equal(inheritedRevision, await RevisionsAsync(connection, Session, "SessionAttachment"));

            Assert.Null(await RevisionsAsync(connection, OtherSession, "SessionAttachment"));
        }

        // Ordinary text still participates after the trigger replacements.
        await ExecuteAsync(connection, AttachmentChunk("text", "text/plain"));

        Assert.Equal(upgrade ? "3:0" : "1:0", await RevisionsAsync(connection, Session, "SessionAttachment"));

        using var freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult freshInstall = await GrimoireSchemaTestInstaller.InstallAsync(fresh, 64, CancellationToken.None);

        Assert.Equal(freshInstall.Core.InstalledCatalogFingerprint, installed.Core.InstalledCatalogFingerprint);

        GrimoireSchemaRefusedException refused = await Assert.ThrowsAsync<GrimoireSchemaRefusedException>(() =>
            GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionThirtyThreeFixture.ChainSet(), 64, CancellationToken.None));

        Assert.Equal(GrimoireSchemaTierHealth.IncompatibleNewerVersion, refused.Health);
    }

    private static string RtfChunk(string id, int index) =>
        AttachmentChunk(id, " APPLICATION/RTF; charset=utf-8 ")
            .Replace("'hash',0,0,6", $"'hash',{index},0,6", StringComparison.Ordinal);
}
