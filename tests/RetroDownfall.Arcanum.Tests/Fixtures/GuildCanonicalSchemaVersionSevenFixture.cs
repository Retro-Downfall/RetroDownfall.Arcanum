using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal static class GuildCanonicalSchemaVersionSevenFixture
{
    internal const string PublishedFingerprint = "C741B15E5F818DAC619F5D94EC1B1E59D9E4229209739388CE2BA43C7464BE20";

    internal static IReadOnlyList<GrimoireSchemaObject> Objects { get; } = JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Schema", "Guild7.json")),
        Core37FixtureJsonContext.Default.GrimoireSchemaObjectArray)!;

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        new GrimoireSchemaVersionChain(GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Guild,
            GrimoireSchemaTransactionTier.GuildCanonical, 7, PublishedFingerprint, Objects), Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.GuildCanonical).Steps.Where(step => step.ToVersion <= 7)]),
    ]);
}
