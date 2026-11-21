using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal static class CoreSchemaVersionFortyFixture
{
    internal const string PublishedFingerprint = "FEEE1C2184FD0748760F91F4121FD675E2C977FFC406B609A6FD0E58FFE97E59";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } =
        [.. GrimoireSchemaCatalog.CoreObjects.Where(item => item.Name is not
            ("session_tool_invocations" or "session_tool_invocations_guard_update" or "session_recovery_checkpoints"
                or "session_recovery_checkpoints_guard_update" or "idx_session_recovery_checkpoints_pending"))];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet()
    {
        GrimoireSchemaVersionChainSet current = GrimoireSchemaVersionChains.Default;

        return new GrimoireSchemaVersionChainSet(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 40, PublishedFingerprint, Objects),
                Objects, [.. current.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 40)]),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            current.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            current.ForTier(GrimoireSchemaTransactionTier.GuildCanonical),
        ]);
    }
}
