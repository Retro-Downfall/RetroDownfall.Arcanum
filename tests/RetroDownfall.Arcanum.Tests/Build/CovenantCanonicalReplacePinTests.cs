using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// No source replaces a row in a Covenant canonical table.
/// </summary>
/// <remarks>
/// <para>A replace resolves its conflict by deleting the old row, and SQLite fires delete triggers for
/// that deletion only when <c>recursive_triggers</c> is on, which Arcanum never sets. It is also an
/// insert, so no update guard fires. <c>INSERT OR REPLACE</c> and <c>REPLACE INTO</c> would therefore
/// walk past every canonical guard at once: a key-epoch row could be re-keyed to a new binding epoch
/// with none of its curation purged, and a ledger row could be rewritten in place.</para>
///
/// <para>So the rule is carried here, as a scan of every comment-free <c>src/**/*.cs</c> file and
/// every <c>src/**/*.sql</c> file. The table set is read from the canonical <c>Tables</c> folder, so a
/// canonical table added later is covered without touching this file.</para>
/// </remarks>
public sealed class CovenantCanonicalReplacePinTests
{
    private const string CanonicalTablesFolder =
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables";

    private static readonly Lazy<Regex> CanonicalReplace = new(BuildPattern);

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
    public void Statements_outside_the_canonical_tier_are_not_caught(string statement) =>
        Assert.DoesNotMatch(CanonicalReplace.Value, statement);

    [Fact]
    public void No_source_replaces_a_covenant_canonical_row() =>
        Assert.Empty(FilesMatching(CanonicalReplace.Value));

    private static void AssertCaught(string statement, string name)
    {
        Match match = CanonicalReplace.Value.Match(statement);

        Assert.True(match.Success, $"The pin missed: {statement}");

        Assert.Equal(name, match.Groups[1].Value);
    }

    /// <summary>
    /// One canonical table as SQLite accepts it: optionally schema-qualified (<c>main.</c>,
    /// <c>"main".</c>, <c>[main].</c>, <c>`main`.</c>, <c>'main'.</c>), and bare or quoted with double
    /// quotes, brackets, backticks or single quotes. Group 1 is the table's name.
    /// </summary>
    private static Regex BuildPattern()
    {
        string tables = string.Join('|', CanonicalTables().Select(Regex.Escape));

        return new Regex(
            @"\b(?:INSERT\s+OR\s+REPLACE|REPLACE)\s+INTO\s+"
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
