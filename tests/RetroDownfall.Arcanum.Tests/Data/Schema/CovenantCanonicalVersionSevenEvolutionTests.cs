using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class CovenantCanonicalVersionSevenEvolutionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void The_version_six_source_is_frozen_before_index_evolution()
    {
        Assert.Equal(CovenantCanonicalSchemaVersionSixFixture.PublishedFingerprint, CovenantCanonicalSchemaVersionSixFixture.Fingerprint);

        output.WriteLine($"FrozenCanonical6={CovenantCanonicalSchemaVersionSixFixture.Fingerprint}; Canonical7={GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint}; Core29={GrimoireSchemaCatalog.CoreSchemaFingerprint}; Accelerator1={GrimoireSchemaCatalog.CovenantAcceleratorSchemaFingerprint}.");
    }

    [Fact]
    public async Task The_version_six_catalog_evolves_to_seven_exactly_like_a_fresh_install()
    {
        using EvolutionScratchDatabase oldFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection evolved = await oldFile.OpenAsync(CancellationToken.None);

        var old = await GrimoireSchemaTestInstaller.InstallAsync(evolved, CovenantCanonicalSchemaVersionSixFixture.ChainSet(), 1536, CancellationToken.None);

        Assert.Equal(6, old.CovenantCanonical.SchemaVersion);

        var upgraded = await GrimoireSchemaTestInstaller.InstallAsync(evolved, GrimoireSchemaVersionChains.Default, 1536, CancellationToken.None);

        Assert.Equal(7, upgraded.CovenantCanonical.SchemaVersion);

        using EvolutionScratchDatabase freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(fresh, GrimoireSchemaVersionChains.Default, 1536, CancellationToken.None);

        Assert.Equal(7, installed.CovenantCanonical.SchemaVersion);

        Assert.Equal(await DefinitionsAsync(fresh), await DefinitionsAsync(evolved));
    }

    private static async Task<string[]> DefinitionsAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT type || ':' || name || ':' || sql FROM sqlite_master WHERE name LIKE 'covenant_%' OR name LIKE 'idx_covenant_%' OR name LIKE 'ux_covenant_%' ORDER BY type, name;";

        List<string> definitions = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) definitions.Add(reader.GetString(0));

        return [.. definitions];
    }
}
