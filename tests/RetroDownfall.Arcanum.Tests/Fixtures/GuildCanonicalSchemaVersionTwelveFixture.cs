using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal static class GuildCanonicalSchemaVersionTwelveFixture
{
    internal const string PublishedFingerprint = "7985F86162BEAAAB33EC0656BF4E39801AF4CBD78F6680D08E3DF87723B4C5F3";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } = JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Schema", "Guild12.json")),
        Core37FixtureJsonContext.Default.GrimoireSchemaObjectArray)!;

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        new GrimoireSchemaVersionChain(GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Guild,
            GrimoireSchemaTransactionTier.GuildCanonical, 12, PublishedFingerprint, Objects), Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical).Steps.Where(step => step.ToVersion <= 12)]),
    ]);
}
