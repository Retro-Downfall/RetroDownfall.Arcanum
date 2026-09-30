using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// No source replaces a row in a Covenant canonical table.
/// </summary>
/// <remarks>
/// <para>A replace resolves its conflict by deleting the other row, and SQLite fires delete triggers
/// for that deletion only when <c>recursive_triggers</c> is on, which Arcanum never sets.
/// <c>INSERT OR REPLACE</c> and <c>REPLACE INTO</c> are inserts, so no update guard fires either, and
/// <c>UPDATE OR REPLACE</c> silently deletes the row its new key collides with. Each would walk past
/// the canonical delete guards: a key-epoch row could be re-keyed to a new binding epoch with none of
/// its curation purged, and a colliding key's row could vanish with its binding epoch.</para>
///
/// <para>So the rule is carried here, as a scan of every comment-free <c>src/**/*.cs</c> file and
/// every <c>src/**/*.sql</c> file. The table set is read from the canonical <c>Tables</c> folder, so a
/// canonical table added later is covered without touching this file. A table-level
/// <c>ON CONFLICT REPLACE</c> clause would turn every plain insert or update into a replace, so the
/// canonical schema files are scanned for that too.</para>
/// </remarks>
public sealed class CovenantCanonicalReplacePinTests
{
    private const string CanonicalFolder =
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical";

    private const string CanonicalTablesFolder = CanonicalFolder + "/Tables";

    private static readonly Lazy<Regex> CanonicalReplace = new(BuildPattern);

    /// <summary>
    /// A constraint's conflict clause choosing REPLACE. The upsert clause, <c>ON CONFLICT (…) DO …</c>,
    /// is a different construct that fires the update guards, and does not match.
    /// </summary>
    private static readonly Regex ConflictReplace = new(
        @"\bON\s+CONFLICT\s+REPLACE\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void The_table_set_is_every_canonical_table_the_catalog_installs()
    {
        string[] catalog =
        [
            .. GrimoireSchemaCatalog.CovenantCanonicalObjects
                .Where(static definition => definition.Category == GrimoireSchemaCategory.Tables)
                .Select(static definition => definition.Name)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Contains("covenant_key_epochs", catalog);

        Assert.Equal(catalog, CanonicalTables());
    }

    /// <summary>
    /// SQLite accepts a table name schema-qualified and quoted several ways, and a pin that knew only
    /// the bare spelling could be walked around by writing the same statement another way.
    /// </summary>
    [Theory]
    [InlineData("covenant_key_epochs", "covenant_key_epochs")]
    [InlineData("\"covenant_curation_heads\"", "covenant_curation_heads")]
    [InlineData("[covenant_entries]", "covenant_entries")]
    [InlineData("`covenant_versions`", "covenant_versions")]
    [InlineData("'covenant_mutation_receipts'", "covenant_mutation_receipts")]
    [InlineData("main.covenant_key_epochs", "covenant_key_epochs")]
    [InlineData("\"main\".covenant_heads", "covenant_heads")]
    [InlineData("[main].[covenant_search_outbox]", "covenant_search_outbox")]
    [InlineData("`main`.`covenant_curation_versions`", "covenant_curation_versions")]
    [InlineData("'main'.'covenant_curation_receipts'", "covenant_curation_receipts")]
    [InlineData("temp.\"covenant_state\"", "covenant_state")]
    public void Every_legal_spelling_of_a_canonical_table_is_caught(string table, string name)
    {
        AssertCaught($"INSERT OR REPLACE INTO {table} (NormalizedKey) VALUES ($k);", name);

        AssertCaught($"insert or replace into {table} (NormalizedKey) VALUES ($k);", name);

        AssertCaught($"REPLACE INTO {table} (NormalizedKey) VALUES ($k);", name);

        AssertCaught($"INSERT\n    OR REPLACE\n    INTO {table}\n    (NormalizedKey) VALUES ($k);", name);

        AssertCaught($"UPDATE OR REPLACE {table} SET NormalizedKey = $to WHERE NormalizedKey = $from;", name);

        AssertCaught($"update or replace {table} SET NormalizedKey = $to WHERE NormalizedKey = $from;", name);

        AssertCaught($"UPDATE\n    OR REPLACE {table}\n    SET NormalizedKey = $to;", name);
    }

    /// <summary>
    /// The pin names canonical tables and nothing else: the vector mirrors legitimately replace their
    /// own rows, and a longer name that merely starts with a canonical one is a different table.
    /// </summary>
    [Theory]
    [InlineData("INSERT OR REPLACE INTO \"saga_memory_embeddings_vec\" (\"MemoryId\") VALUES ($m);")]
    [InlineData("INSERT OR REPLACE INTO covenant_search_documents (EntryId) VALUES ($e);")]
    [InlineData("INSERT OR REPLACE INTO covenant_key_epochs_archive (NormalizedKey) VALUES ($k);")]
    [InlineData("INSERT INTO covenant_key_epochs (NormalizedKey, KeyEpoch, UpdatedAtUtc) VALUES ($k, 1, $t);")]
    [InlineData("UPDATE covenant_key_epochs SET KeyEpoch = KeyEpoch + 1 WHERE NormalizedKey = $k;")]
    [InlineData("UPDATE OR IGNORE covenant_curation_heads SET IsPinned = 0 WHERE NormalizedKey = $k;")]
    [InlineData("UPDATE OR REPLACE saga_memory_embeddings SET Dim = 3 WHERE MemoryId = $m;")]
    public void Statements_outside_the_canonical_tier_are_not_caught(string statement) =>
        Assert.DoesNotMatch(CanonicalReplace.Value, statement);

    [Fact]
    public void No_source_replaces_a_covenant_canonical_row() =>
        Assert.Empty(FilesMatching(CanonicalReplace.Value));

    [Fact]
    public void No_canonical_schema_file_declares_a_replace_conflict_clause()
    {
        // The scan is only as good as its pattern, so it is shown to catch the clause in the places a
        // table definition can carry it, and to leave the upsert clause alone.
        Assert.Matches(ConflictReplace, "NormalizedKey TEXT NOT NULL PRIMARY KEY ON CONFLICT REPLACE,");

        Assert.Matches(ConflictReplace, "UNIQUE (CampaignId, NormalizedKey)\n    on conflict replace");

        Assert.DoesNotMatch(ConflictReplace, "ON CONFLICT(NormalizedKey) DO UPDATE SET KeyEpoch = KeyEpoch + 1");

        ProductionSource[] schema = [.. CanonicalSchemaSources()];

        // Every table definition is among the files scanned, not only the transitions and triggers.
        Assert.Equal(
            CanonicalTables(),
            schema
                .Where(static source => source.RelativePath.StartsWith(CanonicalTablesFolder + "/", StringComparison.Ordinal))
                .Select(static source => Path.GetFileNameWithoutExtension(source.RelativePath))
                .Order(StringComparer.Ordinal));

        Assert.Empty(
            schema
                .Where(static source => ConflictReplace.IsMatch(source.Text))
                .Select(static source => source.RelativePath));
    }

    private static void AssertCaught(string statement, string name)
    {
        Match match = CanonicalReplace.Value.Match(statement);

        Assert.True(match.Success, $"The pin missed: {statement}");

        Assert.Equal(name, match.Groups[1].Value);
    }

    /// <summary>
    /// A replace, <c>INSERT OR REPLACE INTO</c>, <c>REPLACE INTO</c> or <c>UPDATE OR REPLACE</c>, of one
    /// canonical table as SQLite accepts it: optionally schema-qualified (<c>main.</c>,
    /// <c>"main".</c>, <c>[main].</c>, <c>`main`.</c>, <c>'main'.</c>), and bare or quoted with double
    /// quotes, brackets, backticks or single quotes. Group 1 is the table's name.
    /// </summary>
    private static Regex BuildPattern()
    {
        string tables = string.Join('|', CanonicalTables().Select(Regex.Escape));

        return new Regex(
            @"\b(?:(?:INSERT\s+OR\s+REPLACE|REPLACE)\s+INTO|UPDATE\s+OR\s+REPLACE)\s+"
                + @"(?:(?:""\w+""|\[\w+\]|`\w+`|'\w+'|\w+)\s*\.\s*)?[""\[`']?"
                + $@"({tables})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The canonical tables, one per file in the canonical <c>Tables</c> folder, named after the file.
    /// </summary>
    private static string[] CanonicalTables()
    {
        string folder = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), CanonicalTablesFolder);

        string[] tables =
        [
            .. Directory.EnumerateFiles(folder, "*.sql", SearchOption.TopDirectoryOnly)
                .Select(static file => Path.GetFileNameWithoutExtension(file))
                .Order(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(tables);

        return tables;
    }

    /// <summary>
    /// Every <c>.sql</c> file under the canonical folder: tables, triggers, views and transitions.
    /// </summary>
    private static IEnumerable<ProductionSource> CanonicalSchemaSources() =>
        SqlSources().Where(static source => source.RelativePath.StartsWith(CanonicalFolder + "/", StringComparison.Ordinal));

    private static string[] FilesMatching(Regex pattern) =>
    [
        .. ProductionSourceInventory.Sources()
            .Concat(SqlSources())
            .Where(source => pattern.IsMatch(source.Text))
            .Select(static source => source.RelativePath)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Every authored <c>.sql</c> file under <c>src</c>, whole: a schema comment that named a replace
    /// would be read as one, which is the safe direction to be wrong in.
    /// </summary>
    private static IEnumerable<ProductionSource> SqlSources()
    {
        string repositoryRoot = NativeSqlCipherTestPaths.RepositoryRoot();

        List<ProductionSource> sources = [];

        foreach (string file in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.sql", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            sources.Add(new ProductionSource(
                Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/'),
                File.ReadAllText(file)));
        }

        Assert.NotEmpty(sources);

        return sources;
    }
}
