using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>Version 19, captured before exact committed Saga uses were added.</summary>
internal static class CoreSchemaVersionNineteenFixture
{
    internal const string PublishedFingerprint = "5FB2D2B1187D76CE5900B9A984C00312452E6F0BD45EFC2E69D8BDF20EA2B204";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects => [.. GrimoireSchemaCatalog.CoreObjects.Where(item => !item.Name.StartsWith("saga_committed_uses", StringComparison.Ordinal))];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 19, Fingerprint, Objects), Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 19)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}
