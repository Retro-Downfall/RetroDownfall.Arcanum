using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The exact normalized Core version-11 tree before review queues were added.</summary>
internal static class CoreSchemaVersionElevenFixture
{
    internal const string PublishedFingerprint =
        "A42B44B75CC2EA1E3D8E1949A37EE82D6D4D4DB37AA3335F732A54788DC9FF0B";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects
            .Where(static definition => !definition.Name.StartsWith("annal_review_", StringComparison.Ordinal))
            .Select(static definition => definition.Name == "grimoire_utc_instant_columns"
                ? definition with { Sql = WithoutReviewInventoryRows(definition.Sql) }
                : definition),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 11,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion <= 11),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

    private static string WithoutReviewInventoryRows(string sql) =>
        string.Join(
            '\n',
            sql.Split('\n').Where(static line => !line.Contains("annal_review_", StringComparison.Ordinal)));
}
