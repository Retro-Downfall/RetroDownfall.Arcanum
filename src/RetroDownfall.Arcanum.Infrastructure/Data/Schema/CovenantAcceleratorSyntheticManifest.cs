namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The four shadow tables SQLite's FTS5 module creates behind <c>covenant_fts</c>, pinned as
/// literals because they have no source resource file.
/// </summary>
/// <remarks>
/// These objects exist in <c>sqlite_master</c> but nothing in <c>Data/Schema/</c> declares them, so
/// without this manifest an inspection would have to choose between reporting four unexpected
/// objects and ignoring anything it did not recognize. Both are wrong: the shadows carry the index
/// itself, and a missing or altered one means search would answer from a structure this build did
/// not create.
///
/// <para>The definitions below were captured from the accepted hermetic SQLCipher runtime
/// (SQLCipher 4.17.0 on SQLite 3.53.3, pinned in <c>native-source-manifest.json</c>). They are
/// deliberately literals rather than a runtime capture: reading them from the database being
/// inspected would make the check tautological, accepting whatever it found. A SQLite upgrade that
/// changes a shadow shape must therefore fail here and be reviewed as an explicit native dependency
/// change - which is the intended outcome, not an inconvenience.</para>
/// </remarks>
internal static class CovenantAcceleratorSyntheticManifest
{

    /// <summary>The FTS5 virtual table these shadows belong to.</summary>
    internal const string OwnerName = "covenant_fts";

    private static readonly Lazy<IReadOnlyList<GrimoireSchemaManifestEntry>> LoadedEntries =
        new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<GrimoireSchemaManifestEntry> Entries => LoadedEntries.Value;

    private static IReadOnlyList<GrimoireSchemaManifestEntry> Build()
    {

        // Ordered by name, matching the ORDER BY the inspector reads them with, so the fingerprint
        // does not depend on sqlite_master's physical row order.
        return
        [
            Shadow(
                "covenant_fts_config",
                "CREATE TABLE 'covenant_fts_config'(k PRIMARY KEY, v) WITHOUT ROWID"),

            Shadow(
                "covenant_fts_data",
                "CREATE TABLE 'covenant_fts_data'(id INTEGER PRIMARY KEY, block BLOB)"),

            Shadow(
                "covenant_fts_docsize",
                "CREATE TABLE 'covenant_fts_docsize'(id INTEGER PRIMARY KEY, sz BLOB)"),

            Shadow(
                "covenant_fts_idx",
                "CREATE TABLE 'covenant_fts_idx'(segid, term, pgno, PRIMARY KEY(segid, term)) WITHOUT ROWID"),
        ];

    }

    private static GrimoireSchemaManifestEntry Shadow(string name, string sql) =>
        new(
            GrimoireSchemaObjectType.Table,
            name,
            name,
            GrimoireSqlNormalizer.Normalize(sql),
            IsSynthetic: true,
            // Shadow indexes are implicit and are validated through the owning table's index shape
            // like any other table, so no expected index is declared here.
            Indexes: []);

}
