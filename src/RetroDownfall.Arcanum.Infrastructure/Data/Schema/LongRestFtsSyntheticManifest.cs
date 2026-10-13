namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Closed FTS5 shadow shapes of the two Core-owned content-bearing discovery indexes.</summary>
internal static class LongRestFtsSyntheticManifest
{
    internal static IReadOnlyList<GrimoireSchemaManifestEntry> For(string owner)
    {
        // These are the accepted hermetic FTS5 table shapes, never database-derived expectations.
        return
        [
            Shadow(owner + "_config", $"CREATE TABLE '{owner}_config'(k PRIMARY KEY, v) WITHOUT ROWID"),
            Shadow(owner + "_content", $"CREATE TABLE '{owner}_content'(id INTEGER PRIMARY KEY, c0, c1, c2)"),
            Shadow(owner + "_data", $"CREATE TABLE '{owner}_data'(id INTEGER PRIMARY KEY, block BLOB)"),
            Shadow(owner + "_docsize", $"CREATE TABLE '{owner}_docsize'(id INTEGER PRIMARY KEY, sz BLOB)"),
            Shadow(owner + "_idx", $"CREATE TABLE '{owner}_idx'(segid, term, pgno, PRIMARY KEY(segid, term)) WITHOUT ROWID"),
        ];
    }

    private static GrimoireSchemaManifestEntry Shadow(string name, string sql) =>
        new(GrimoireSchemaObjectType.Table, name, name, GrimoireSqlNormalizer.Normalize(sql), IsSynthetic: true, Indexes: []);
}
