using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The complete published Core31 catalog, frozen before projection write access changes.</summary>
internal static class CoreSchemaVersionThirtyOneFixture
{
    internal const string PublishedFingerprint = "11091ED68F18B87241A0C4AA1FBB841A447CA99C1BCC6A6B230FBFBD6F3964F2";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } = JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Schema", "Core31.json")),
        Core31FixtureJsonContext.Default.GrimoireSchemaObjectArray)!;

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 31, Fingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 31)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}

[JsonSerializable(typeof(GrimoireSchemaObject[]))]
internal sealed partial class Core31FixtureJsonContext : JsonSerializerContext;
