namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Closed shipping-native shadow shapes of the literal-search external-content index.</summary>
internal static class MemorySearchFtsSyntheticManifest
{
    internal static IReadOnlyList<GrimoireSchemaManifestEntry> Entries =>
    [
        Shadow("memory_search_fts_config", "CREATE TABLE 'memory_search_fts_config'(k PRIMARY KEY, v) WITHOUT ROWID"),
        Shadow("memory_search_fts_data", "CREATE TABLE 'memory_search_fts_data'(id INTEGER PRIMARY KEY, block BLOB)"),
        Shadow("memory_search_fts_docsize", "CREATE TABLE 'memory_search_fts_docsize'(id INTEGER PRIMARY KEY, sz BLOB)"),
        Shadow("memory_search_fts_idx", "CREATE TABLE 'memory_search_fts_idx'(segid, term, pgno, PRIMARY KEY(segid, term)) WITHOUT ROWID"),
    ];

    private static GrimoireSchemaManifestEntry Shadow(string name, string sql) =>
        new(GrimoireSchemaObjectType.Table, name, name, GrimoireSqlNormalizer.Normalize(sql), IsSynthetic: true, Indexes: []);
}
