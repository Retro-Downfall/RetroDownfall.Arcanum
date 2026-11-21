using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal static class CoreSchemaVersionFortyOneFixture
{
    internal const string PublishedFingerprint = "07294998C49940585EF4E77A45D77DA18841CDE4B94B32BFF246DA1802D5267B";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } =
        [.. GrimoireSchemaCatalog.CoreObjects.Where(item => item.Name is not
            ("session_recovery_checkpoints"
                or "session_recovery_checkpoints_guard_update" or "idx_session_recovery_checkpoints_pending"))];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet()
    {
        GrimoireSchemaVersionChainSet current = GrimoireSchemaVersionChains.Default;

        return new GrimoireSchemaVersionChainSet(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 41, PublishedFingerprint, Objects),
                Objects, [.. current.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 41)]),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            current.ForTier(GrimoireSchemaTransactionTier.GuildCanonical),
        ]);
    }
}
