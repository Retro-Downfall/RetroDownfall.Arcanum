using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildOperatorSchemaTests
{
    [Fact]
    public async Task Operator_sequence_membership_has_a_declared_V13_to_V14_transition()
    {
        Assert.Equal(GuildCanonicalSchemaVersionThirteenFixture.PublishedFingerprint,
            GuildCanonicalSchemaVersionThirteenFixture.Fingerprint);

        await using var upgraded = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var baseline = await GrimoireSchemaTestInstaller.InstallAsync(upgraded,
            GuildCanonicalSchemaVersionThirteenFixture.ChainSet(), 768, default);

        Assert.True(baseline.GuildCanonical.IsHealthy, baseline.GuildCanonical.ToString());

        var migrated = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, default);

        Assert.True(migrated.GuildCanonical.IsHealthy, migrated.GuildCanonical.ToString());

        await using var fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        await using var command = fresh.CreateCommand();

        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='guild_operator_sequences'";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        await using var upgradedCommand = upgraded.CreateCommand();

        command.CommandText = upgradedCommand.CommandText =
            "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(await command.ExecuteScalarAsync(), await upgradedCommand.ExecuteScalarAsync());
    }
}
