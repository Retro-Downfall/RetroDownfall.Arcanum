using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Only the named deleters touch the erasure evidence tables, and nothing rewrites evidence in place.
/// </summary>
/// <remarks>
/// <para>No trigger guards a fingerprint or receipt against deletion: a delete-guard would also stop
/// release, key reset and restore staging, which are exactly the operations that must delete. So the
/// rule is carried here instead, as a scan of every comment-free <c>src/**/*.cs</c> file and every
/// <c>src/**/*.sql</c> file. Every evidence statement lives in <see cref="Owner"/>, and the members
/// that delete are called only from a closed list of files.</para>
///
/// <para>A full installation reset removes the database file, not rows, so it needs no entry.</para>
///
/// <para>The receipt table's update guard stops every update but two, and nothing else: a replace
/// deletes the old row, which cascades to its subjects and never fires an update trigger, and a
/// fingerprint or subject row can be updated in place. So no source may replace or upsert evidence,
/// and only the evidence store may update it, and only its receipts.</para>
/// </remarks>
public sealed class MemoryErasureEvidenceDeleterTests
{
    private const string Owner = "src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs";

    /// <summary>
    /// Closed. Release and operator re-creation, reset-key, and restore staging add themselves when they
    /// exist. Nothing else.
    /// </summary>
    internal static readonly string[] AllowedCallers = [];

    /// <summary>
    /// One evidence table as SQLite accepts it: optionally schema-qualified (<c>main.</c>,
    /// <c>"main".</c>, <c>[main].</c>, <c>temp.</c>), and bare or quoted with double quotes, brackets or
    /// backticks. Group 1 is the table's suffix.
    /// </summary>
    private const string EvidenceTable =
        @"(?:(?:""\w+""|\[\w+\]|`\w+`|\w+)\s*\.\s*)?[""\[`]?memory_erasure_(fingerprints|receipts|receipt_subjects)\b";

    private static readonly Regex EvidenceDelete = new(
        @"DELETE\s+FROM\s+" + EvidenceTable,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EvidenceReplace = new(
        @"\b(?:INSERT\s+OR\s+REPLACE|REPLACE)\s+INTO\s+" + EvidenceTable,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EvidenceUpdate = new(
        @"\bUPDATE\s+(?:OR\s+\w+\s+)?" + EvidenceTable,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EvidenceUpsert = new(
        @"INSERT[^;]*?INTO\s+" + EvidenceTable + @"[^;]*?ON\s+CONFLICT[^;]*?DO\s+UPDATE",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    [Fact]
    public void Only_the_evidence_store_deletes_evidence_rows()
    {
        string[] owner = [Owner];

        Assert.Equal(owner, FilesMatching(EvidenceDelete));

        Assert.Contains(EvidenceDelete.Matches(OwnerText()), static match => match.Groups[1].Value == "fingerprints");

        Assert.Contains(EvidenceDelete.Matches(OwnerText()), static match => match.Groups[1].Value == "receipts");
    }

    [Fact]
    public void Evidence_deleter_callers_are_a_closed_allow_list()
    {
        string[] members =
        [
            "MemoryErasureEvidence.DeleteFingerprintAsync",
            "MemoryErasureEvidence.DeleteUnverifiableAsync",
            "MemoryErasureEvidence.ReplaceAllAsync",
        ];

        string[] callers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source => !source.IsExactOwner(Owner) && members.Any(source.Names))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(AllowedCallers.Order(StringComparer.Ordinal), callers);
    }

    [Fact]
    public void No_source_replaces_or_upserts_evidence_rows()
    {
        // The scan is only as good as its patterns, so each one is shown to catch the shape it names.
        Assert.Matches(EvidenceReplace, "INSERT OR REPLACE INTO memory_erasure_receipts (MutationId) VALUES ($m)");

        Assert.Matches(EvidenceReplace, "REPLACE INTO \"memory_erasure_fingerprints\" (Fingerprint) VALUES ($f)");

        Assert.Matches(
            EvidenceUpsert,
            "INSERT INTO memory_erasure_receipt_subjects (MutationId, SubjectDigest)\n    VALUES ($m, $d)\n    ON CONFLICT (MutationId, SubjectDigest) DO UPDATE SET SubjectDigest = excluded.SubjectDigest");

        Assert.Empty(FilesMatching(EvidenceReplace));

        Assert.Empty(FilesMatching(EvidenceUpsert));
    }

    /// <summary>
    /// SQLite accepts a table name schema-qualified and quoted several ways, and a pin that knew only
    /// the bare spelling could be walked around by writing the same statement another way.
    /// </summary>
    [Theory]
    [InlineData("memory_erasure_fingerprints", "fingerprints")]
    [InlineData("\"memory_erasure_receipts\"", "receipts")]
    [InlineData("[memory_erasure_receipt_subjects]", "receipt_subjects")]
    [InlineData("`memory_erasure_fingerprints`", "fingerprints")]
    [InlineData("main.memory_erasure_receipts", "receipts")]
    [InlineData("\"main\".memory_erasure_receipts", "receipts")]
    [InlineData("[main].[memory_erasure_fingerprints]", "fingerprints")]
    [InlineData("temp.\"memory_erasure_receipt_subjects\"", "receipt_subjects")]
    [InlineData("`main`.`memory_erasure_receipts`", "receipts")]
    public void Every_pin_recognizes_every_legal_spelling_of_an_evidence_table(string table, string suffix)
    {
        AssertCaught(EvidenceDelete, $"DELETE FROM {table} WHERE 0;", suffix);

        AssertCaught(EvidenceReplace, $"INSERT OR REPLACE INTO {table} (KeyId) VALUES ($k);", suffix);

        AssertCaught(EvidenceReplace, $"REPLACE INTO {table} (KeyId) VALUES ($k);", suffix);

        AssertCaught(EvidenceUpdate, $"UPDATE {table} SET KeyId = KeyId WHERE 0;", suffix);

        AssertCaught(EvidenceUpdate, $"UPDATE OR IGNORE {table} SET KeyId = KeyId WHERE 0;", suffix);

        AssertCaught(
            EvidenceUpsert,
            $"INSERT INTO {table} (KeyId) VALUES ($k) ON CONFLICT (KeyId) DO UPDATE SET KeyId = excluded.KeyId;",
            suffix);
    }

    [Fact]
    public void Only_the_evidence_store_updates_receipts_and_nothing_updates_fingerprints_or_subjects()
    {
        Assert.Matches(EvidenceUpdate, "UPDATE OR IGNORE memory_erasure_fingerprints SET KeyId = $k");

        // The receipt guard's own trigger names the table after ON, and must never read as an update.
        Assert.DoesNotMatch(EvidenceUpdate, "BEFORE UPDATE ON memory_erasure_receipts");

        Assert.DoesNotMatch(EvidenceUpdate, "BEFORE UPDATE ON main.memory_erasure_receipts");

        string[] owner = [Owner];

        Assert.Equal(owner, FilesMatching(EvidenceUpdate));

        Assert.All(EvidenceUpdate.Matches(OwnerText()), static match => Assert.Equal("receipts", match.Groups[1].Value));
    }

    private static void AssertCaught(Regex pattern, string statement, string suffix)
    {
        Match match = pattern.Match(statement);

        Assert.True(match.Success, $"The pin missed: {statement}");

        Assert.Equal(suffix, match.Groups[1].Value);
    }

    private static string OwnerText() =>
        ProductionSourceInventory.Sources().Single(static source => source.IsExactOwner(Owner)).Text;

    private static string[] FilesMatching(Regex pattern) =>
    [
        .. ProductionSourceInventory.Sources()
            .Concat(SqlSources())
            .Where(source => pattern.IsMatch(source.Text))
            .Select(static source => source.RelativePath)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Every authored <c>.sql</c> file under <c>src</c>, whole: a schema comment that named an evidence
    /// statement would be read as one, which is the safe direction to be wrong in.
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
