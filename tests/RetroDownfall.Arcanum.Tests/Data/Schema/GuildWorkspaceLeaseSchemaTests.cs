using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildWorkspaceLeaseSchemaTests
{
    [Fact]
    public async Task Published_v7_upgrade_preserves_other_families_and_matches_fresh_v8_catalog()
    {
        Assert.Equal(GuildCanonicalSchemaVersionSevenFixture.PublishedFingerprint, GuildCanonicalSchemaVersionSevenFixture.Fingerprint);

        await using SqliteConnection upgraded = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, GuildCanonicalSchemaVersionSevenFixture.ChainSet(), 768, default);

        object? other = await ScalarAsync(upgraded, "SELECT group_concat(SourceDefinitionFingerprint || InstalledCatalogFingerprint) FROM grimoire_feature_schemas WHERE FamilyCode<>2");

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        Assert.Equal(GrimoireSchemaVersionChains.GuildCanonicalSchemaVersion, installed.GuildCanonical.SchemaVersion);

        Assert.Equal(other, await ScalarAsync(upgraded, "SELECT group_concat(SourceDefinitionFingerprint || InstalledCatalogFingerprint) FROM grimoire_feature_schemas WHERE FamilyCode<>2"));

        await using SqliteConnection fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, default);

        const string catalog = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(await ScalarAsync(fresh, catalog), await ScalarAsync(upgraded, catalog));
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}
