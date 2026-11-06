using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal static class GuildCanonicalSchemaVersionTwoFixture
{
    internal const string PublishedFingerprint = "816961C80560CD3A36EB64111D1F19240A3B781516180E9A2609A8CA919F501F";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } = JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Schema", "Guild2.json")),
        Core37FixtureJsonContext.Default.GrimoireSchemaObjectArray)!;

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        new GrimoireSchemaVersionChain(GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Guild,
            GrimoireSchemaTransactionTier.GuildCanonical, 2, PublishedFingerprint, Objects), Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical).Steps.Where(step => step.ToVersion <= 2)]),
    ]);
}
