using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal static class GuildCanonicalSchemaVersionNineFixture
{
    internal const string PublishedFingerprint = "D08C0850320EA982D75496C05A873C00F96DF54E9993F092E8959D19172FE796";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } = [.. GrimoireSchemaCatalog.GuildCanonicalObjects
        .Where(item => item.Name is not "idx_guild_budget_effects_quest" and not "idx_guild_workspace_leases_quest"
            and not "idx_guild_quests_cancellation")];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        new GrimoireSchemaVersionChain(GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Guild,
            GrimoireSchemaTransactionTier.GuildCanonical, 9, PublishedFingerprint, Objects), Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical).Steps.Where(step => step.ToVersion <= 9)]),
    ]);
}
