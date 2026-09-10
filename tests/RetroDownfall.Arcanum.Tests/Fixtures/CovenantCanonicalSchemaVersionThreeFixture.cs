using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// Covenant canonical's published raw version-3 head tree before the UTC instant inventory view was added.
/// </summary>
internal static class CovenantCanonicalSchemaVersionThreeFixture
{
    internal const string PublishedFingerprint =
        "E85966D8DA8878566A10B08A75FBDD623064D0D7FABCFEBF4CA17F24A9E66BB1";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CovenantCanonicalObjects
            .Where(static definition => definition.Name != "covenant_utc_instant_columns"),
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
                    version: 3,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.CovenantCanonical)
                        .Steps
                        .Where(static step => step.ToVersion <= 3),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
}
