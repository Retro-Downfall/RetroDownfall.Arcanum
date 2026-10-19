using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class SagaCommittedUseSchemaTests
{
    [Fact]
    public async Task Fresh_schema_installs_the_exact_use_evidence_lane()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using var connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'saga_committed_uses'";

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public void Published_v19_remains_frozen() => Assert.Equal(CoreSchemaVersionNineteenFixture.PublishedFingerprint, CoreSchemaVersionNineteenFixture.Fingerprint);
    [Fact]
    public async Task Evolved_v19_and_fresh_schema_have_identical_catalogs()
    {
        using var oldFile = EvolutionScratchDatabase.Create();

        await using var old = await oldFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(old, CoreSchemaVersionNineteenFixture.ChainSet(), 64, CancellationToken.None);

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
