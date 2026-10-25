using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>Version 20, captured before durable retention snapshots were added.</summary>
internal static class CoreSchemaVersionTwentyFixture
{
    internal const string PublishedFingerprint = "9416EC4081548FEECE664D293FFC8ED5CB97C6E62FF0003CF286DB04FCEAFA38";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects => [.. GrimoireSchemaCatalog.CoreObjects.Where(item => !item.Name.StartsWith("data_retention_prune_snapshots", StringComparison.Ordinal))];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 20, Fingerprint, Objects), Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 20)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}
