using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildDistributionSchemaTests
{
    [Fact]
    public async Task Distribution_schema_is_a_declared_V12_to_V13_transition()
    {
        Assert.Equal(GuildCanonicalSchemaVersionTwelveFixture.PublishedFingerprint,
            GuildCanonicalSchemaVersionTwelveFixture.Fingerprint);

        Assert.Contains(GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical).Steps, step => step.FromVersion == 12 && step.ToVersion == 13);

        await using var upgraded = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var baseline = await GrimoireSchemaTestInstaller.InstallAsync(upgraded,
            GuildCanonicalSchemaVersionTwelveFixture.ChainSet(), 768, default);

        Assert.True(baseline.GuildCanonical.IsHealthy, baseline.GuildCanonical.ToString());

        var migrated = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, default);

        Assert.True(migrated.GuildCanonical.IsHealthy, migrated.GuildCanonical.ToString());

        await using var fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        await using var first = upgraded.CreateCommand();

        await using var second = fresh.CreateCommand();

        first.CommandText = second.CommandText =
            "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(await first.ExecuteScalarAsync(), await second.ExecuteScalarAsync());

        first.CommandText = """
            SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN
                ('guild_work_offers','guild_work_offer_recipients','guild_work_bids','guild_work_awards','guild_work_mutations','guild_cohort_ledger',
                 'guild_cohort_award_authority_scan','guild_distribution_notifications')
            """;

        Assert.Equal(8L, await first.ExecuteScalarAsync());
    }
}
