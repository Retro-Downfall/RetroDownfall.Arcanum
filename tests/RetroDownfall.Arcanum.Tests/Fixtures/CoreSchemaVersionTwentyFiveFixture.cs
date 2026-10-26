using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The published Core25 tree before DAG persistence; frozen before any Core26 edit.</summary>
internal static class CoreSchemaVersionTwentyFiveFixture
{
    internal const string PublishedFingerprint = "4CD45949338193A2EDB9B54D990D0579763FB4BB0E12FB68C2447BEFE0177B20";

    private static readonly HashSet<string> LaterObjects = new(StringComparer.Ordinal)
    {
        "tapestry_edges",
        "tapestry_scope_revisions",
        "tapestry_nodes_guard_insert",
        "tapestry_nodes_guard_update",
        "tapestry_nodes_guard_delete",
        "tapestry_node_embeddings_guard_insert",
        "tapestry_node_embeddings_guard_update",
        "tapestry_node_embeddings_guard_delete",
        "tapestry_edges_guard_insert",
        "tapestry_edges_guard_update",
        "tapestry_edges_guard_delete",
        "tapestry_generations_guard_insert",
        "tapestry_generations_guard_update",
        "tapestry_nodes_legacy_insert",
        "tapestry_nodes_legacy_update",
    };

    private static readonly IReadOnlyDictionary<string, string> Frozen = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tapestry_generations"] =
            """
            CREATE TABLE IF NOT EXISTS tapestry_generations (
                GenerationId TEXT PRIMARY KEY,
                ScopeKind TEXT NOT NULL,
                ScopeId TEXT NOT NULL,
                Status TEXT NOT NULL,
                AlgorithmVersion TEXT NOT NULL,
                SettingsFingerprint TEXT NOT NULL,
                SummaryModel TEXT,
                SummaryRecipeVersion TEXT NOT NULL,
                EmbeddingDimension INTEGER NOT NULL,
                CorpusFingerprint TEXT NOT NULL,
                LayerCount INTEGER NOT NULL DEFAULT 0,
                NodeCount INTEGER NOT NULL DEFAULT 0,
                RootNodeCount INTEGER NOT NULL DEFAULT 0,
                TerminalReason TEXT,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_tapestry_generations_scope
                ON tapestry_generations(ScopeKind, ScopeId, Status);

            -- Exactly one Complete generation per scope is what retrieval reads and what the publishing switch
            -- assumes. The switch supersedes the previous Complete row and promotes the new one in a single
            -- transaction, which keeps the invariant only for as long as every writer follows it; this index is what
            -- makes a second Complete row for one scope impossible rather than merely unexpected. It covers Complete
            -- rows alone, so any number of Building and Superseded generations can coexist with the current one.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_tapestry_generations_complete_scope
                ON tapestry_generations(ScopeKind, ScopeId) WHERE Status = 'Complete';
            """,
        ["tapestry_nodes"] =
            """
            CREATE TABLE IF NOT EXISTS tapestry_nodes (
                NodeId TEXT PRIMARY KEY,
                GenerationId TEXT NOT NULL,
                ScopeKind TEXT NOT NULL,
                ScopeId TEXT NOT NULL,
                Layer INTEGER NOT NULL,
                ParentScopeKey TEXT NOT NULL,
                NodeKind TEXT NOT NULL,
                ParentNodeId TEXT,
                SourceKind TEXT,
                SourceId TEXT,
                SourceLabel TEXT NOT NULL,
                Content TEXT,
                ContentHash TEXT NOT NULL,
                ChildMembershipHash TEXT,
                DescendantLeafCount INTEGER NOT NULL DEFAULT 1,
                ClusterOrdinal INTEGER NOT NULL DEFAULT 0,
                PartitionReason TEXT NOT NULL DEFAULT 'None',
                EmbeddingDimension INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY(GenerationId) REFERENCES tapestry_generations(GenerationId) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_tapestry_nodes_generation
                ON tapestry_nodes(GenerationId, Layer);
            CREATE INDEX IF NOT EXISTS idx_tapestry_nodes_parent_scope
                ON tapestry_nodes(ParentScopeKey);
            CREATE INDEX IF NOT EXISTS idx_tapestry_nodes_parent
                ON tapestry_nodes(ParentNodeId);
            CREATE INDEX IF NOT EXISTS idx_tapestry_nodes_membership
                ON tapestry_nodes(GenerationId, ChildMembershipHash);
            """,
    };

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects.Where(item => !LaterObjects.Contains(item.Name))
            .Select(item => Frozen.TryGetValue(item.Name, out string? sql) ? item with { Sql = sql + "\n" } : item),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(
            GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 25, Fingerprint, Objects),
            Objects,
            [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 25)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}
