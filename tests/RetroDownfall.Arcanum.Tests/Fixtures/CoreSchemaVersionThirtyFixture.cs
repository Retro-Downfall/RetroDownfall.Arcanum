using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The complete published Core30 catalog, frozen before workspace scope access changes.</summary>
internal static class CoreSchemaVersionThirtyFixture
{
    internal const string PublishedFingerprint = "1D6CCB39EABA6E314F963E8B330E8FDDE44DD4B027EA94A63C68C959D19950CA";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } = JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Schema", "Core30.json")),
        Core30FixtureJsonContext.Default.GrimoireSchemaObjectArray)!;

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 30, Fingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 30)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}

[JsonSerializable(typeof(GrimoireSchemaObject[]))]
internal sealed partial class Core30FixtureJsonContext : JsonSerializerContext;
