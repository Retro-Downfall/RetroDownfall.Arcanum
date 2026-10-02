using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
/// whether through <c>INSERT OR REPLACE</c>, <c>REPLACE INTO</c>, <c>UPDATE OR REPLACE</c> or a
/// table-level <c>ON CONFLICT REPLACE</c>, and only the evidence store may update it, and only its
/// receipts.</para>
/// </remarks>
public sealed class MemoryErasureEvidenceDeleterTests
{
    private const string Owner = "src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs";

    /// <summary>
    /// Closed. Release and every operator re-creation delete through the one fingerprint-release file,
    /// reset-key is the one caller of the unverifiable-row delete, and restore staging's evidence step is
    /// the one caller of the destination-authoritative replacement. Nothing else.
    /// </summary>
    internal static readonly string[] AllowedCallers =
    [
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreErasureEvidenceApplier.cs",
        "src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureFingerprintRelease.cs",
        "src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureAdministration.cs",
    ];

    /// <summary>
    /// One evidence table as SQLite accepts it: optionally schema-qualified (<c>main.</c>,
    /// <c>"main".</c>, <c>[main].</c>, <c>'main'.</c>, <c>temp.</c>), and bare or quoted with double
    /// quotes, brackets, backticks or single quotes, which SQLite's legacy rule reads as an identifier
    /// wherever one is expected. Group 1 is the table's suffix.
    /// </summary>
    private const string EvidenceTable =
        @"(?:(?:""\w+""|\[\w+\]|`\w+`|'\w+'|\w+)\s*\.\s*)?[""\[`']?memory_erasure_(fingerprints|receipts|receipt_subjects)\b";

    private const string FingerprintRelease = "src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureFingerprintRelease.cs";

    /// <summary>
    /// Closed: the one release service, the operator writes that re-create an erased identity, and the
    /// preflights that disclose such a release before it is approved, each by the member that makes the
    /// call. Extraction, the Lexicon scribe, the Covenant kernel's agent arm and turn publication are
    /// agent paths and never appear here.
    /// </summary>
    private static readonly string[] AllowedFingerprintReleaseCallers =
    [
        "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs::ApplyDecisionAsync",
        "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs::PrepareAsync",
        "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs::CommitAsync",
        "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs::PrepareAsync",
        "src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs::CorrectAsync",
        "src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureRelease.cs::ReleaseCoreAsync",
        "src/RetroDownfall.Arcanum.Infrastructure/Memory/SagaMemoryReviewService.cs::ApplyDecisionAsync",
        "src/RetroDownfall.Arcanum.Infrastructure/Memory/SagaMemoryReviewService.cs::PrepareAsync",
    ];

    /// <summary>
    /// The members of the fingerprint-release helper that delete nothing, and so may be read from any
    /// path. Closed: every other member, the three lifting members and any member added later, is a
    /// deleter whose callers must be listed above until it is classified here.
    /// </summary>
    private static readonly string[] NonDeletingFingerprintReleaseMembers =
    [
        "OperatorMayRelease",
    ];

    /// <summary>
    /// A <c>using</c> alias or <c>using static</c> of either class, which would let a caller name its
    /// members under a name no scan above reads.
    /// </summary>
    private static readonly Regex EvidenceAlias = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+|\w+\s*=\s*)[\w.:]*\b(?:MemoryErasureEvidence|MemoryErasureFingerprintRelease)\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>The six files that create the evidence tables: each table's head file and its V13 step.</summary>
    private static readonly string[] EvidenceTableDdl =
    [
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/memory_erasure_fingerprints.sql",
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/memory_erasure_receipt_subjects.sql",
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/memory_erasure_receipts.sql",
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V13/010_memory_erasure_fingerprints.sql",
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V13/020_memory_erasure_receipts.sql",
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V13/030_memory_erasure_receipt_subjects.sql",
    ];

    private static readonly Regex EvidenceDelete = new(
        @"DELETE\s+FROM\s+" + EvidenceTable,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every statement that rewrites an evidence row by deleting it: <c>INSERT OR REPLACE</c>,
    /// <c>REPLACE INTO</c>, and <c>UPDATE OR REPLACE</c>, which takes no <c>INTO</c>.
    /// </summary>
    private static readonly Regex EvidenceReplace = new(
        @"\b(?:(?:INSERT\s+OR\s+REPLACE|REPLACE)\s+INTO|UPDATE\s+OR\s+REPLACE)\s+" + EvidenceTable,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EvidenceTableCreate = new(
        @"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?" + EvidenceTable,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConflictReplace = new(
        @"\bON\s+CONFLICT\s+REPLACE\b",
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

    /// <summary>
    /// The deleting members, named through their class wherever a fluent chain breaks the line: house
    /// style writes <c>await MemoryErasureEvidence</c> on one line and <c>.DeleteFingerprintAsync(</c> on
    /// the next, and a caller written that way is a caller.
    /// </summary>
    private static readonly Regex EvidenceDeleterCall = new(
        @"\bMemoryErasureEvidence\s*\.\s*(?:DeleteFingerprintAsync|DeleteUnverifiableAsync|ReplaceAllAsync)\b",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Evidence_deleter_callers_are_a_closed_allow_list()
    {
        Assert.Matches(EvidenceDeleterCall, "MemoryErasureEvidence.DeleteFingerprintAsync(connection, transaction, fingerprint, ct)");

        Assert.Matches(EvidenceDeleterCall, "await MemoryErasureEvidence\n                .DeleteUnverifiableAsync(connection, transaction, keyId, ct)");

        Assert.DoesNotMatch(EvidenceDeleterCall, "MemoryErasureEvidence.DeleteFingerprintAsyncLater(connection)");

        string[] callers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source => !source.IsExactOwner(Owner) && EvidenceDeleterCall.IsMatch(source.Text))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(AllowedCallers.Order(StringComparer.Ordinal), callers);
    }

    /// <summary>
    /// The fingerprint-release helper is the one deleter of fingerprints, so who may reach it is closed
    /// too: a call from an agent path would lift an erasure no operator asked to lift.
    /// </summary>
    [Fact]
    public void Fingerprint_release_callers_are_a_closed_allow_list()
    {
        string root = NativeSqlCipherTestPaths.RepositoryRoot();

        string[] callers =
        [
            .. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(file => (Path: Path.GetRelativePath(root, file).Replace('\\', '/'), Text: File.ReadAllText(file)))
                .Where(static source => source.Path != FingerprintRelease
                    && source.Text.Contains("MemoryErasureFingerprintRelease", StringComparison.Ordinal))
                .SelectMany(static source => FingerprintReleaseCallers(source.Text).Select(member => $"{source.Path}::{member}"))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(AllowedFingerprintReleaseCallers.Order(StringComparer.Ordinal), callers);
    }

    /// <summary>
    /// The caller scan reads the member a call sits in, through lambdas, and qualified names too, and it
    /// reads every member of the helper but the one that deletes nothing: a member added to the helper
    /// later is a caller to list, whatever it is named.
    /// </summary>
    [Fact]
    public void The_fingerprint_release_caller_scan_names_the_enclosing_member()
    {
        const string source = """
            internal sealed class Fixture
            {
                internal async Task InsertCoreAsync()
                {
                    await Run(async () => _ = await MemoryErasureFingerprintRelease
                        .ReleaseForOperatorWriteAsync(null!, null!, default, null, null, default));
                }

                internal Task<bool?> Probe() =>
                    RetroDownfall.Arcanum.Infrastructure.Data.MemoryErasureFingerprintRelease.WouldReleaseAsync(null!, null, default, null, null, default);

                internal bool Permitted() => MemoryErasureFingerprintRelease.OperatorMayRelease(null);

                internal Task<int> ExtractAsync() =>
                    MemoryErasureFingerprintRelease.LiftAsync(null!, null, null!, default);
            }
            """;

        Assert.Equal(
            ["ExtractAsync", "InsertCoreAsync", "Probe"],
            FingerprintReleaseCallers(source).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every non-private member the helper declares is either a deleter whose callers the allow-list
    /// closes, or classified as deleting nothing. A new member fails here until it is one or the other.
    /// </summary>
    [Fact]
    public void Every_fingerprint_release_member_is_classified()
    {
        string text = File.ReadAllText(Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), FingerprintRelease));

        string[] members =
        [
            .. CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview))
                .GetCompilationUnitRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(static method => !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PrivateKeyword)))
                .Select(static method => method.Identifier.ValueText)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            ["DeleteCandidatesAsync", "OperatorMayRelease", "ReleaseForOperatorWriteAsync", "WouldReleaseAsync"],
            members);

        Assert.All(NonDeletingFingerprintReleaseMembers, member => Assert.Contains(member, members));
    }

    /// <summary>
    /// No source aliases the evidence store or the release helper, or imports either statically, so
    /// every call to their members names the class the scans read.
    /// </summary>
    [Fact]
    public void No_source_aliases_the_evidence_store_or_the_release_helper()
    {
        Assert.Matches(EvidenceAlias, "using Evidence = RetroDownfall.Arcanum.Infrastructure.Data.MemoryErasureEvidence;");

        Assert.Matches(EvidenceAlias, "global using Release = global::RetroDownfall.Arcanum.Infrastructure.Data.MemoryErasureFingerprintRelease;");

        Assert.Matches(EvidenceAlias, "using static RetroDownfall.Arcanum.Infrastructure.Data.MemoryErasureEvidence;");

        Assert.DoesNotMatch(EvidenceAlias, "using RetroDownfall.Arcanum.Infrastructure.Data;");

        Assert.Empty(
            ProductionSourceInventory.Sources()
                .Where(static source => EvidenceAlias.IsMatch(source.Text))
                .Select(static source => source.RelativePath));
    }

    [Fact]
    public void No_source_replaces_or_upserts_evidence_rows()
    {
        // The scan is only as good as its patterns, so each one is shown to catch the shape it names.
        Assert.Matches(EvidenceReplace, "INSERT OR REPLACE INTO memory_erasure_receipts (MutationId) VALUES ($m)");

        Assert.Matches(EvidenceReplace, "REPLACE INTO \"memory_erasure_fingerprints\" (Fingerprint) VALUES ($f)");

        Assert.Matches(EvidenceReplace, "UPDATE OR REPLACE memory_erasure_receipts SET KeyId = KeyId WHERE 0;");

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
    [InlineData("'memory_erasure_fingerprints'", "fingerprints")]
    [InlineData("main.'memory_erasure_receipts'", "receipts")]
    public void Every_pin_recognizes_every_legal_spelling_of_an_evidence_table(string table, string suffix)
    {
        AssertCaught(EvidenceDelete, $"DELETE FROM {table} WHERE 0;", suffix);

        AssertCaught(EvidenceReplace, $"INSERT OR REPLACE INTO {table} (KeyId) VALUES ($k);", suffix);

        AssertCaught(EvidenceReplace, $"REPLACE INTO {table} (KeyId) VALUES ($k);", suffix);

        AssertCaught(EvidenceReplace, $"UPDATE OR REPLACE {table} SET KeyId = KeyId WHERE 0;", suffix);

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

    /// <summary>
    /// A table-level <c>ON CONFLICT REPLACE</c> turns a colliding insert or update into a delete that no
    /// statement names and no update guard sees, so no evidence table, head or transition, declares one.
    /// </summary>
    [Fact]
    public void No_evidence_table_declares_a_replace_conflict_clause()
    {
        Assert.Matches(ConflictReplace, "Fingerprint BLOB NOT NULL PRIMARY KEY ON CONFLICT REPLACE CHECK (length(Fingerprint) = 32),");

        Assert.DoesNotMatch(ConflictReplace, "INSERT INTO t (a) VALUES (1) ON CONFLICT (a) DO UPDATE SET a = excluded.a;");

        ProductionSource[] ddl =
        [
            .. SqlSources()
                .Where(static source => EvidenceTableCreate.IsMatch(source.Text))
                .OrderBy(static source => source.RelativePath, StringComparer.Ordinal),
        ];

        Assert.Equal(EvidenceTableDdl, ddl.Select(static source => source.RelativePath));

        Assert.All(ddl, static source => Assert.DoesNotMatch(ConflictReplace, source.Text));
    }

    /// <summary>
    /// The members whose code names any member of the helper through its class, comments aside, but
    /// the ones classified as deleting nothing.
    /// </summary>
    private static IEnumerable<string> FingerprintReleaseCallers(string source) =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))
            .GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(static access => !NonDeletingFingerprintReleaseMembers.Contains(access.Name.Identifier.ValueText, StringComparer.Ordinal))
            .Where(static access => access.Expression switch
            {
                IdentifierNameSyntax name => name.Identifier.ValueText == "MemoryErasureFingerprintRelease",
                MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText == "MemoryErasureFingerprintRelease",
                AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText == "MemoryErasureFingerprintRelease",
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText == "MemoryErasureFingerprintRelease",
                _ => false,
            })
            .Select(static access => access.Ancestors().OfType<MemberDeclarationSyntax>().First() switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                ConstructorDeclarationSyntax => ".ctor",
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(static variable => variable.Identifier.ValueText)),
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                MemberDeclarationSyntax other => other.Kind().ToString(),
            });

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
