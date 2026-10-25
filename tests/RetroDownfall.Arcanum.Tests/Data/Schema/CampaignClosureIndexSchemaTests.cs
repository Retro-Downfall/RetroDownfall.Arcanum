using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class CampaignClosureIndexSchemaTests
{
    [Fact]
    public void Version_twenty_three_reconstruction_matches_the_published_fingerprint()
    {
        Assert.Equal(CoreSchemaVersionTwentyThreeFixture.PublishedFingerprint, CoreSchemaVersionTwentyThreeFixture.Fingerprint);

        Assert.Equal(CoreSchemaVersionTwentyThreeFixture.PublishedFingerprint,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).SourceDefinitionFingerprintFor(23));
    }

    [Fact]
    public async Task VersionTwentyThree_EvolvesToTheExactFreshCampaignClosureIndexCatalog()
    {
        using var oldFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection old = await oldFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(old, CoreSchemaVersionTwentyThreeFixture.ChainSet(), 64, CancellationToken.None);

        await using (SqliteCommand seed = old.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO UploadedFiles(Id,Filename,Bytes,Purpose,MimeType,CreatedAt)
                VALUES('AbCdEf1234567890AbCdEf1234567890','kept',0,'batch','application/jsonl','2000-01-01T00:00:00.0000000Z');
                """;

            _ = await seed.ExecuteNonQueryAsync();
        }

        Assert.Equal(24, (await GrimoireSchemaTestInstaller.InstallAsync(old, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None)).Core.SchemaVersion);

        using var freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(fresh, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);

        Assert.Equal(await ReadCatalogAsync(fresh), await ReadCatalogAsync(old));

        await using SqliteCommand read = old.CreateCommand();

        read.CommandText = "SELECT Id FROM UploadedFiles";

        Assert.Equal("AbCdEf1234567890AbCdEf1234567890", await read.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_transition_rolls_back_indexes_and_version_then_retries(bool cancel)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentyThreeFixture.ChainSet(), 64, CancellationToken.None);

        string[] before = await ReadCatalogAsync(connection);

        using CancellationTokenSource cancellation = new();

        connection.CreateFunction("cancel_transition", () =>
        {
            cancellation.Cancel();

            return 0L;
        });

        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        GrimoireSchemaVersionStep step = Assert.Single(core.Steps, item => item.ToVersion == 24);

        GrimoireSchemaVersionStep interrupted = step with
        {
            Statements = [.. step.Statements.Select((statement, index) => index == 2
                ? statement with { Sql = cancel ? "SELECT cancel_transition();" : "SELECT MissingColumn FROM Sessions;" }
                : statement)],
        };

        GrimoireSchemaVersionChainSet chain = new(
        [
            new GrimoireSchemaVersionChain(core.HeadManifest, core.HeadObjects, [.. core.Steps.Where(item => item.ToVersion < 24), interrupted]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chain, 64, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<SqliteException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chain, 64, CancellationToken.None));
        }

        Assert.Equal(before, await ReadCatalogAsync(connection));

        Assert.Equal(23, (await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentyThreeFixture.ChainSet(), 64, CancellationToken.None)).Core.SchemaVersion);

        Assert.Equal(24, (await GrimoireSchemaTestInstaller.InstallAsync(connection, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None)).Core.SchemaVersion);
    }

    private static async Task<string[]> ReadCatalogAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT type || ':' || name || ':' || sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type,name";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> rows = [];

        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }
}
