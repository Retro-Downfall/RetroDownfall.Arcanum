using System.Text.Json;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The published Core35 catalog before the standalone attachment retrieval index.</summary>
internal static class CoreSchemaVersionThirtyFiveFixture
{
    internal const string PublishedFingerprint = "1243F9B7B5256B4C07A71E030C145D20804297950101E93A5E411510D872E148";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } = JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Schema", "Core35.json")),
        Core35FixtureJsonContext.Default.GrimoireSchemaObjectArray)!;

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 35, PublishedFingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(static step => step.ToVersion <= 35)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}

[JsonSerializable(typeof(GrimoireSchemaObject[]))]
internal sealed partial class Core35FixtureJsonContext : JsonSerializerContext;
