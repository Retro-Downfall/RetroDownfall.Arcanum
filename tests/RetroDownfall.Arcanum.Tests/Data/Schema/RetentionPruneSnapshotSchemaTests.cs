using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class RetentionPruneSnapshotSchemaTests
{
    [Fact]
    public async Task Fresh_schema_installs_the_retention_snapshot_lane()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using var connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'data_retention_prune_snapshots'";

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public void Published_v20_remains_frozen() => Assert.Equal(CoreSchemaVersionTwentyFixture.PublishedFingerprint, CoreSchemaVersionTwentyFixture.Fingerprint);
    [Fact]
    public async Task Evolved_v20_and_fresh_schema_have_identical_catalogs()
    {
        using var oldFile = EvolutionScratchDatabase.Create();

        await using var old = await oldFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(old, CoreSchemaVersionTwentyFixture.ChainSet(), 64, CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(old, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);

        using var newFile = EvolutionScratchDatabase.Create();

        await using var fresh = await newFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(fresh, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);

        Assert.Equal(await Catalog(fresh), await Catalog(old));
    }

    private static async Task<string[]> Catalog(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT type || ':' || name || ':' || sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type,name";

        await using var reader = await command.ExecuteReaderAsync();

        List<string> values = [];

        while (await reader.ReadAsync()) values.Add(reader.GetString(0));

        return [.. values];
    }
}
