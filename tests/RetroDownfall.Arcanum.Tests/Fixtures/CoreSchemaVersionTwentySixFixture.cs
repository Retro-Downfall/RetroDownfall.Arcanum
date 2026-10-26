using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>Approved Core26 DAG schema, frozen before source revision triggers were added.</summary>
internal static class CoreSchemaVersionTwentySixFixture
{
    internal const string PublishedFingerprint = "9B9BE8D38636742B481D8C4A35BC1C84262AC4DA99373652B22FBB44517462A9";

    private static readonly HashSet<string> LaterObjects = new(StringComparer.Ordinal)
    {
        "workspace_file_chunks_tapestry_revision_insert",
        "workspace_file_chunks_tapestry_revision_replace_update",
        "workspace_file_chunks_tapestry_revision_update",
        "workspace_file_chunks_tapestry_revision_delete",
        "Entries_tapestry_revision_insert",
        "Entries_tapestry_revision_replace_update",
        "Entries_tapestry_revision_update",
        "Entries_tapestry_revision_delete",
        "session_attachment_chunks_tapestry_revision_insert",
        "session_attachment_chunks_tapestry_revision_replace_update",
        "session_attachment_chunks_tapestry_revision_update",
        "session_attachment_chunks_tapestry_revision_delete",
    };

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects.Where(item => !LaterObjects.Contains(item.Name)),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 26, Fingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 26)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}
