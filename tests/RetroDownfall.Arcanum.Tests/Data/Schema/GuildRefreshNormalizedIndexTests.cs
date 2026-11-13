using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildRefreshNormalizedIndexTests
{
    [Fact]
    public async Task Version_seventeen_upgrade_adds_normalized_index_without_changing_prior_identity()
    {
        GrimoireSchemaVersionChainSet old = PreviousChains();

        await using var upgraded = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(upgraded, old, 768, default)).GuildCanonical.IsHealthy);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, default)).GuildCanonical.IsHealthy);

        await using var fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, default)).GuildCanonical.IsHealthy);

        await using var first = upgraded.CreateCommand();

        await using var second = fresh.CreateCommand();

        first.CommandText = second.CommandText = "SELECT SchemaVersion || SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(await first.ExecuteScalarAsync(), await second.ExecuteScalarAsync());

        Assert.Equal(18, GrimoireSchemaVersionChains.GuildCanonicalSchemaVersion);
    }

    [Fact]
    public async Task Interrupted_normalized_index_transition_rolls_back_and_can_reopen()
    {
        GrimoireSchemaVersionChainSet current = GrimoireSchemaVersionChains.Default;

        GrimoireSchemaVersionChain guild = current.ForTier(GrimoireSchemaTransactionTier.GuildCanonical);

        GrimoireSchemaVersionStep step = guild.Steps.Single(item => item.ToVersion == 18);

        GrimoireSchemaVersionStep failed = step with { Statements = [.. step.Statements,
            new("fixture.failure", 20, "failure", "SELECT * FROM missing_normalized_index_fixture;")] };

        GrimoireSchemaVersionChainSet interrupted = new([
            current.ForTier(GrimoireSchemaTransactionTier.Core),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            new GrimoireSchemaVersionChain(guild.HeadManifest, guild.HeadObjects,
                [.. guild.Steps.Select(item => item.ToVersion == 18 ? failed : item)]),
        ]);

        await using var connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, PreviousChains(), 768, default)).GuildCanonical.IsHealthy);

        Assert.False((await GrimoireSchemaTestInstaller.InstallAsync(connection, interrupted, 768, default)).GuildCanonical.IsHealthy);

        await using var query = connection.CreateCommand();

        query.CommandText = "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(17L, await query.ExecuteScalarAsync());

        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='idx_guild_spend_refreshes_effect_norm'";

        Assert.Equal(0L, await query.ExecuteScalarAsync());

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default)).GuildCanonical.IsHealthy);

        Assert.Equal(1L, await query.ExecuteScalarAsync());
    }

    private static GrimoireSchemaVersionChainSet PreviousChains()
    {
        const string previous = "A94F9C12CDF3F72EAD2FC41AF203450F80CB7D291CDCB9B27E2ACDEA5E498A79";

        IReadOnlyList<GrimoireSchemaObject> objects = [.. GrimoireSchemaCatalog.GuildCanonicalObjects
            .Where(item => item.Name != "idx_guild_spend_refreshes_effect_norm")];

        Assert.Equal(previous, GrimoireSchemaCatalog.ComputeSourceFingerprint(objects));

        GrimoireSchemaVersionChainSet current = GrimoireSchemaVersionChains.Default;

        GrimoireSchemaVersionChainSet old = new([
            current.ForTier(GrimoireSchemaTransactionTier.Core),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            new GrimoireSchemaVersionChain(GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Guild,
                GrimoireSchemaTransactionTier.GuildCanonical, 17, previous, objects), objects,
                [.. current.ForTier(GrimoireSchemaTransactionTier.GuildCanonical).Steps.Where(step => step.ToVersion <= 17)]),
        ]);

        return old;
    }
}
