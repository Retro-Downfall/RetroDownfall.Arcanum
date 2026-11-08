using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal static class GuildCanonicalSchemaVersionEightFixture
{
    internal const string PublishedFingerprint = "3DD0FE6EC88B44D5F4AE0395D082281E74155CB429E4B3B53A9D6AE147CC9432";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } = [.. GrimoireSchemaCatalog.GuildCanonicalObjects
        .Where(item => !item.Name.Contains("guild_local_executions", StringComparison.Ordinal)
            && !item.Name.Contains("guild_quest_cancellations", StringComparison.Ordinal)
            && !item.Name.Contains("guild_local_wait_checkpoints", StringComparison.Ordinal))
        .Select(item => item.Name is "guild_content" or "guild_content_erasure_receipts"
            ? item with { Sql = item.Sql.Replace("BETWEEN 0 AND 10", "BETWEEN 0 AND 8", StringComparison.Ordinal) }
            : item.Name == "guild_utc_instant_columns" ? item with { Sql = item.Sql.Replace(
                "\nUNION ALL SELECT 'guild_quest_cancellations' AS TableName, 'AcceptedAtUtc' AS ColumnName;", ";", StringComparison.Ordinal) } : item)];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        new GrimoireSchemaVersionChain(GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Guild,
            GrimoireSchemaTransactionTier.GuildCanonical, 8, PublishedFingerprint, Objects), Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical).Steps.Where(step => step.ToVersion <= 8)]),
    ]);
}
