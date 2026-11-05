using System.Text.Json;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The published Core37 catalog before the quantized embedding companions.</summary>
internal static class CoreSchemaVersionThirtySevenFixture
{
    internal const string PublishedFingerprint = "72BC0F63C58D1B0E0A2E2A6A2FA2C2491506F5983289019CDD5D24AED7A1D634";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } = JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Schema", "Core37.json")),
        Core37FixtureJsonContext.Default.GrimoireSchemaObjectArray)!;

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 37, PublishedFingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(static step => step.ToVersion <= 37)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical),
    ]);
}

[JsonSerializable(typeof(GrimoireSchemaObject[]))]
internal sealed partial class Core37FixtureJsonContext : JsonSerializerContext;
