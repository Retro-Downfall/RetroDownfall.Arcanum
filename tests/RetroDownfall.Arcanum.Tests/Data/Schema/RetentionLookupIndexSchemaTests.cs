using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class RetentionLookupIndexSchemaTests
{
    [Fact]
    public async Task VersionTwentyTwo_EvolvesToTheExactFreshRetentionIndexCatalog()
    {
        using var oldFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection old = await oldFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(old, CoreSchemaVersionTwentyTwoFixture.ChainSet(), 64, CancellationToken.None);

        await using (SqliteCommand seed = old.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO UploadedFiles(Id,Filename,Bytes,Purpose,MimeType,CreatedAt)
                VALUES('AbCdEf1234567890AbCdEf1234567890','kept',0,'batch','application/jsonl','2000-01-01T00:00:00.0000000Z');
                """;

            _ = await seed.ExecuteNonQueryAsync();
        }

        Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion, (await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(old, 64, CancellationToken.None)).Core.SchemaVersion);

        using var freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(fresh, 64, CancellationToken.None);

        Assert.Equal(await ReadCatalogAsync(fresh), await ReadCatalogAsync(old));

        await using SqliteCommand read = old.CreateCommand();

        read.CommandText = "SELECT Id FROM UploadedFiles";

        Assert.Equal("AbCdEf1234567890AbCdEf1234567890", await read.ExecuteScalarAsync());
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
