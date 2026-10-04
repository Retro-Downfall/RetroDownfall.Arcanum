using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The exact raw Covenant canonical version-4 tree before review queues were added.</summary>
internal static class CovenantCanonicalSchemaVersionFourFixture
{
    internal const string PublishedFingerprint =
        "7E7B7B2B590EA4A4D4EEA8A0C463E813319BECA7E07750CAF51258F5BA35C6A2";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CovenantCanonicalSchemaVersionFiveFixture.Objects
            .Where(static definition => !definition.Name.StartsWith("covenant_review_", StringComparison.Ordinal))
            .Select(static definition => definition.Name == "covenant_utc_instant_columns"
                ? definition with { Sql = WithoutReviewInventoryRows(definition.Sql) }
                : definition),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeRawSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Covenant,
                    GrimoireSchemaTransactionTier.CovenantCanonical,
                    version: 4,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.CovenantCanonical)
                        .Steps
                        .Where(static step => step.ToVersion <= 4),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

    private static string WithoutReviewInventoryRows(string sql) =>
        string.Join(
            '\n',
            sql.Split('\n').Where(static line => !line.Contains("covenant_review_", StringComparison.Ordinal)));
}
