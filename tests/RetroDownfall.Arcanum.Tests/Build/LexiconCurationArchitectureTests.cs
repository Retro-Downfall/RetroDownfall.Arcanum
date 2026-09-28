using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Closes the raw-SQL projection/reader and operational/inspection boundaries. These checks accompany
/// executable lifecycle tests: adding a query, consumer, column, or ordinal requires an explicit role.
/// </summary>
public sealed class LexiconCurationArchitectureTests
{
    private const string Service = "RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs";

    private const string Inspection = "RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.CurationInspection.cs";

    private const string Annals = "RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsStore.cs";

    private const string Writer = "RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs";

    private const string Backfill = "RetroDownfall.Arcanum.Infrastructure/Data/Schema/MemoryAnnalsBackfill.cs";

    private const string EntryColumns = "Id,Name,Type,FactsJson,UpdatedAt,ScopeCampaignId,RetiredAtUtc,PinnedAtUtc,CurationGeneration";

    private const string VersionColumns = "VersionId,ClaimId,Sequence,Revision,OperationCode,OriginCode,ScopeKindCode,CampaignId,SensitivityCode,ContentHashFormatCode,ContentHash,ValidFromUtc,ValidToUtc,RecordedAtUtc,RecordedUntilUtc,PredecessorVersionId";

    public static TheoryData<string, string, int, string> Projections => new()
    {
        { Service, "FillExactMatchesAsync", 0, EntryColumns },
        { Service, "FillFtsMatchesViaMatchAsync", 0, EntryColumns },
        { Service, "FillFtsMatchesViaLikeAsync", 0, EntryColumns },
        { Service, "ReadActiveByNormalizedAsync", 0, EntryColumns },
        { Service, "ReadByNormalizedAsync", 0, EntryColumns },
        { Service, "ReadAllLifecycleIdentityForDeletionAsync", 0, "Id" },
        { Service, "ReadFactProvenanceCoreAsync", 0, "EntryId,Fact,SessionId,AttachmentId,LogicalKey,Version,ContentHash,MaterializedAt,SourceType,SourceAvailable" },
        { Inspection, "ReadInspectionIdentitiesAsync", 0, "Id,ScopeCampaignId" },
        { Inspection, "ReadInspectionRowAsync", 0, EntryColumns + ",NameNormalized,FactsText" },
        { Inspection, "ReadVerifiedHeadAsync", 0, "ClaimId,CurrentVersionId,CurrentRevision,CurrentOperationCode,SubjectStoreCode" },
        { Inspection, "ReadVerifiedHeadAsync", 1, VersionColumns.Replace("RecordedUntilUtc", "NULL", StringComparison.Ordinal) },
        { Inspection, "ReadInspectionHistoryAsync", 0, VersionColumns },
        { Inspection, "ReadHistoricalSourcesAsync", 0, "AnnalVersionId,FactOrdinal,SessionId,AttachmentId,LogicalKey,AttachmentVersion,AttachmentContentHash,MaterializedAt,SourceType,ContentHashFormatCode,OperationCode" },
        { Inspection, "ReadVerifiedLabelAsync", 0, "LabelId,ArtifactKindCode,ArtifactId,SessionId,CampaignId,TurnId,ArtifactRevision,ArtifactContentDigest,SensitivityCode,ProvenanceModeCode,ExactGenerationIds,GenerationBloom,SensitivityDigest,ProducingPlanDigest,ProducingAdmissionDigest,ProducingMaintenanceReceiptDigest,ArtifactLabelDigest,CreatedAtUtc" },
        { Annals, "GetClaimAsync", 0, "ClaimId,SubjectStoreCode,SubjectId,CurrentVersionId,CurrentRevision,CurrentOperationCode,UpdatedAtUtc" },
        { Annals, "GetVersionsAsync", 0, "VersionId,ClaimId,Sequence,Revision,OperationCode,OriginCode,ScopeKindCode,CampaignId,SensitivityCode,ValidFromUtc,ValidToUtc,RecordedAtUtc,RecordedUntilUtc,PredecessorVersionId,ContentHashFormatCode,ContentHash" },
        { Annals, "GetLexiconFactProvenanceAsync", 0, "AnnalVersionId,FactOrdinal,SessionId,AttachmentId,LogicalKey,AttachmentVersion,AttachmentContentHash,MaterializedAt,SourceType" },
        { Writer, "ReadHeadAsync", 0, "ClaimId,CurrentVersionId,CurrentRevision,CurrentOperationCode,Sequence,ContentHash,ContentHashFormatCode" },
        { Backfill, "ReadBatchAsync", 0, "Id,Content,CreatedAt,ScopeKindCode,CampaignId" },
        { Backfill, "ReadBatchAsync", 2, "Id,Type,FactsText,UpdatedAt,ScopeCampaignId" },
    };

    [Theory]
    [MemberData(nameof(Projections))]
    public void Every_projection_preserves_its_complete_ordered_column_contract(string path, string method, int query, string expected)
    {
        Assert.Equal(expected.Split(','), ProjectionColumns(SqlText(Method(path, method)), query));

        Assert.Equal(expected.Split(','), ProjectionColumns(SqlText(Method(path, method), legacy: true), query));
    }

    public static TheoryData<string, string, string> Readers => new()
    {
        { Service, "ReadEntry", "GetString:0,GetString:1,GetString:2,GetString:3,GetString:4,GetString:5,GetString:6,GetString:7,ReadPositiveInteger:8" },
        { Service, "ReadFactProvenanceCoreAsync", "GetString:2,GetString:3,GetString:4,ReadCode:5,GetString:6,GetString:7,GetString:8,GetInt32:9,GetString:0,GetString:1" },
        { Inspection, "ReadInspectionIdentitiesAsync", "GetString:0,GetString:1" },
        { Inspection, "ReadInspectionRowAsync", "GetString:9,GetString:10" },
        { Inspection, "ReadVerifiedHeadAsync", "ReadCode:4,GetString:0,GetString:1,ReadCode:2,ReadCode:3" },
        { Inspection, "ReadInspectionVersion", "GetString:0,GetString:1,ReadPositiveInteger:2,ReadCode:3,ReadCode:4,ReadCode:5,ReadCode:6,GetString:7,ReadCode:8,ReadCode:9,GetValue:10,GetString:11,GetString:12,GetString:13,GetString:14,GetString:15" },
        { Inspection, "ReadHistoricalSourcesAsync", "GetString:0,ReadCode:1,GetString:2,GetString:3,GetString:4,ReadCode:5,GetString:6,GetString:7,GetString:8,ReadCode:9,ReadCode:10" },
        { Inspection, "ReadVerifiedLabelAsync", "ReadCode:9,GetValue:10,GetValue:11,GetString:0,ReadCode:1,GetString:2,ReadOptionalGuid:3,ReadOptionalGuid:4,ReadOptionalGuid:5,ReadPositiveInteger:6,GetValue:7,ReadCode:8,GetValue:12,ReadOptionalDigest:13,ReadOptionalDigest:14,ReadOptionalDigest:15,GetValue:16,GetString:17" },
        { Annals, "GetClaimAsync", "GetString:0,ReadCode:1,GetString:2,GetString:3,GetInt32:4,ReadCode:5,GetString:6" },
        { Annals, "GetVersionsAsync", "GetString:0,GetString:1,GetInt64:2,GetInt32:3,ReadCode:4,ReadCode:5,ReadCode:6,GetString:7,ReadCode:8,ReadCode:14,GetValue:15,GetString:9,GetString:10,GetString:11,GetString:12,GetString:13" },
        { Annals, "GetLexiconFactProvenanceAsync", "GetString:0,GetInt32:1,GetString:2,GetString:3,GetString:4,GetInt32:5,GetString:6,GetString:7,GetString:8" },
        { Writer, "ReadHeadAsync", "GetString:0,GetString:1,GetInt32:2,ReadCode:3,GetInt64:4,GetValue:5,ReadCode:6" },
        { Backfill, "ReadBatchAsync", "GetString:0,GetString:1,GetString:2,GetInt64:3,GetString:4,GetString:4,GetString:0,GetString:1,GetString:2,GetString:3" },
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public void Every_reader_binds_the_reviewed_column_to_its_ordinal(string path, string method, string expected)
    {
        Assert.Equal(expected.Split(','), ReaderSlots(Method(path, method)));
    }

    [Fact]
    public void Query_and_reader_inventories_are_bidirectional_including_shared_mappers()
    {
        MethodDeclarationSyntax[] methods = [.. LexiconTrees().SelectMany(tree => tree.DescendantNodes().OfType<MethodDeclarationSyntax>())];

        string[] readers = [.. methods.Where(method => ReaderSlots(method).Length > 0).Select(method => method.Identifier.ValueText).Order(StringComparer.Ordinal)];

        Assert.Equal(Readers.Where(row => (string)row[0] is Service or Inspection).Select(row => (string)row[1]).Order(StringComparer.Ordinal), readers);

        string[] executed = [.. methods.Where(method => Calls(method, "ExecuteReaderAsync")).Select(method => method.Identifier.ValueText).Order(StringComparer.Ordinal)];

        Assert.Equal(new[]
        {
            "FillExactMatchesAsync", "FillFtsMatchesViaLikeAsync", "FillFtsMatchesViaMatchAsync",
            "ReadFactProvenanceCoreAsync", "ReadHistoricalSourcesAsync", "ReadInspectionHistoryAsync",
            "ReadInspectionIdentitiesAsync", "ReadInspectionRowAsync", "ReadNamedEntryAsync", "ReadVerifiedHeadAsync", "ReadVerifiedLabelAsync",
        }.Order(StringComparer.Ordinal), executed);

        string[] projected = [.. methods.Where(method => SqlText(method).Contains("SELECT", StringComparison.Ordinal)
            && !new[] { "DeleteByNameAsync", "ShowExactAsync", "ShowEffectiveAsync" }.Contains(method.Identifier.ValueText, StringComparer.Ordinal))
            .Select(method => method.Identifier.ValueText).Order(StringComparer.Ordinal)];

        Assert.Equal(Projections.Where(row => (string)row[0] is Service or Inspection).Select(row => (string)row[1])
            .Append("VersionColumnsFor").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), projected);

        foreach (string method in new[] { "FillExactMatchesAsync", "FillFtsMatchesViaMatchAsync", "FillFtsMatchesViaLikeAsync", "ReadNamedEntryAsync" })
        {
            Assert.True(Calls(Method(Service, method), "ReadEntry"), method);
        }

        Assert.True(Calls(Method(Inspection, "ReadInspectionRowAsync"), "ReadEntry"));

        Assert.True(Calls(Method(Inspection, "ReadVerifiedHeadAsync"), "ReadInspectionVersion"));

        Assert.True(Calls(Method(Inspection, "ReadInspectionHistoryAsync"), "ReadInspectionVersion"));

        Assert.Equal(Enumerable.Range(0, 9), Ordinals(Service, "ReadEntry").Order());

        Assert.Equal(Enumerable.Range(0, 11), Ordinals(Service, "ReadEntry").Concat(Ordinals(Inspection, "ReadInspectionRowAsync")).Order());

        foreach ((string path, string method, int count) in new (string, string, int)[]
        {
            (Service, "ReadFactProvenanceCoreAsync", 10), (Inspection, "ReadInspectionIdentitiesAsync", 2),
            (Inspection, "ReadVerifiedHeadAsync", 5), (Inspection, "ReadInspectionVersion", 16),
            (Inspection, "ReadHistoricalSourcesAsync", 11), (Inspection, "ReadVerifiedLabelAsync", 18),
            (Annals, "GetClaimAsync", 7), (Annals, "GetVersionsAsync", 16),
            (Annals, "GetLexiconFactProvenanceAsync", 9), (Writer, "ReadHeadAsync", 7), (Backfill, "ReadBatchAsync", 5),
        })
        {
            Assert.Equal(Enumerable.Range(0, count), Ordinals(path, method).Order());
        }
    }

    [Fact]
    public void Operational_SQL_is_active_only_and_all_lifecycle_content_has_explicit_inspection_or_mutation_owners()
    {
        foreach (string method in new[] { "FillExactMatchesAsync", "FillFtsMatchesViaMatchAsync", "FillFtsMatchesViaLikeAsync", "ReadActiveByNormalizedAsync" })
        {
            Assert.Matches(@"\b(?:e\.)?RetiredAtUtc IS NULL\b", SqlText(Method(Service, method)));

            Assert.DoesNotContain("RetiredAtUtc IS NULL", SqlText(Method(Service, method), legacy: true), StringComparison.Ordinal);
        }

        Assert.Matches(@"\bRetiredAtUtc IS NULL\b", SqlText(Method(Inspection, "ShowEffectiveAsync")));

        foreach (string method in new[] { "GetByNameAsync", "GetByNameInScopeAsync" })
        {
            Assert.True(Calls(Method(Service, method), "ReadActiveByNormalizedAsync"), method);

            Assert.False(Calls(Method(Service, method), "ReadByNormalizedAsync"), method);
        }

        Assert.Equal(new[] { "UpsertCoreAsync" }, LexiconTrees()
            .SelectMany(tree => tree.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(method => Calls(method, "ReadByNormalizedAsync"))
            .Select(method => method.Identifier.ValueText).Order(StringComparer.Ordinal));

        Assert.DoesNotContain("RetiredAtUtc IS NULL", SqlText(Method(Inspection, "ShowExactAsync")), StringComparison.Ordinal);

        Assert.DoesNotContain("RetiredAtUtc IS NULL", SqlText(Method(Inspection, "ListInspectionAsync")), StringComparison.Ordinal);

        Assert.Equal(["Id"], ProjectionColumns(SqlText(Method(Service, "ReadAllLifecycleIdentityForDeletionAsync")), 0));

        Assert.DoesNotContain("RetiredAtUtc", SqlText(Method(Service, "ReadAllLifecycleIdentityForDeletionAsync")), StringComparison.Ordinal);

        foreach ((string caller, string callee) in new (string, string)[]
        {
            ("MatchEntitiesAsync", "FillTierAsync"), ("FillTierAsync", "FillExactMatchesAsync"),
            ("FillTierAsync", "FillFtsMatchesAsync"), ("FillFtsMatchesAsync", "FillFtsMatchesViaMatchAsync"),
            ("FillFtsMatchesAsync", "FillFtsMatchesViaLikeAsync"),
        })
        {
            Assert.True(Calls(Method(Service, caller), callee), caller + " must reach " + callee);
        }
    }

    [Fact]
    public void Capability_dependent_reads_share_the_snapshot_and_retention_probes_inside_its_write_transaction()
    {
        foreach ((string path, string method) in new (string, string)[]
        {
            (Service, "MatchEntitiesAsync"), (Service, "GetByNameAsync"), (Service, "GetByNameInScopeAsync"),
            (Annals, "GetVersionsAsync"), (Annals, "GetLexiconFactProvenanceAsync"),
            ("RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "AddLexiconCandidatesAsync"),
        })
        {
            Assert.True(Calls(Method(path, method), "InSnapshotAsync"), method + " must retain the capability snapshot");
        }

        MethodDeclarationSyntax deletion = Method("RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs", "DeleteLexiconCandidateAsync");

        InvocationExpressionSyntax begin = Assert.Single(deletion.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString() == "BeginMutationTransactionAsync");

        InvocationExpressionSyntax capability = Assert.Single(deletion.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString() == "GrimoireCoreSchemaVersion.ReadAsync");

        Assert.True(begin.SpanStart < capability.SpanStart);

        Assert.Equal(new[] { "connection", "cancellationToken", "transaction" },
            capability.ArgumentList.Arguments.Select(argument => argument.Expression.ToString()));
    }

    [Fact]
    public void Every_operational_service_consumer_and_call_is_explicitly_classified()
    {
        Dictionary<string, string[]> expected = new(StringComparer.Ordinal)
        {
            ["RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs"] = ["MatchEntitiesAsync"],
            ["RetroDownfall.Arcanum.Infrastructure/Daemons/UnseenServantDaemonJob.cs"] = ["GetByNameAsync"],
            ["RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.LexiconTools.cs"] = ["DeleteByNameAsync", "FindAllLifecycleIdentityForDeletionAsync", "UpsertAsync", "UpsertAsync"],
            ["RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs"] = ["DeleteByNameAsync", "FindAllLifecycleIdentityForDeletionAsync", "ListInspectionAsync", "ShowEffectiveAsync"],
        };

        string root = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "src");

        Dictionary<string, CompilationUnitSyntax> consumers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal))
            .Select(path => (Path: Path.GetRelativePath(root, path).Replace('\\', '/'), Tree: Parse(File.ReadAllText(path))))
            .Where(item => item.Tree.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier => identifier.Identifier.ValueText == "ILexiconService"))
            .Where(item => item.Path != Service && !item.Path.EndsWith("ServiceCollectionExtensions.cs", StringComparison.Ordinal))
            .ToDictionary(item => item.Path, item => item.Tree, StringComparer.Ordinal);

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), consumers.Keys.Order(StringComparer.Ordinal));

        foreach ((string path, string[] calls) in expected)
        {
            CompilationUnitSyntax tree = consumers[path];

            HashSet<string> receivers = tree.DescendantNodes().OfType<ParameterSyntax>()
                .Where(parameter => parameter.Type?.ToString() == "ILexiconService").Select(parameter => parameter.Identifier.ValueText)
                .Concat(tree.DescendantNodes().OfType<VariableDeclarationSyntax>()
                    .Where(declaration => declaration.Type.ToString() == "ILexiconService")
                    .SelectMany(declaration => declaration.Variables.Select(variable => variable.Identifier.ValueText)))
                .ToHashSet(StringComparer.Ordinal);

            string[] actual = [.. tree.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(invocation => invocation.Expression).OfType<MemberAccessExpressionSyntax>()
                .Where(access => receivers.Contains(access.Expression.ToString()))
                .Select(access => access.Name.Identifier.ValueText).Order(StringComparer.Ordinal)];

            Assert.Equal(calls.Order(StringComparer.Ordinal), actual);
        }

        MethodDeclarationSyntax explain = Method("RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs", "HandleExplainAsync");

        Assert.Contains("entry.Eligibility==LexiconRetrievalEligibility.Eligible", Tokens(explain), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT a, nested(x, y), c FROM table", new[] { "a", "nested(x, y)", "c" })]
    [InlineData("SELECT a, (SELECT x FROM other) AS evidence, c FROM table", new[] { "a", "evidence", "c" })]
    public void Projection_parser_counts_nested_expressions_as_one_column(string sql, string[] expected)
    {
        Assert.Equal(expected, ProjectionColumns(sql, 0));
    }

    [Fact]
    public void SQL_outside_the_Lexicon_service_has_only_explicit_inspection_evidence_or_erasure_owners()
    {
        string root = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "src");

        List<string> actual = [];

        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.Contains("/Lexicon/", StringComparison.Ordinal)))
        {
            string source = File.ReadAllText(path);

            if (!source.Contains("lexicon_entries", StringComparison.Ordinal)) continue;

            foreach (MethodDeclarationSyntax method in Parse(source).DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                string sql = SqlText(method);

                if (Regex.IsMatch(sql, @"\b(?:FROM|JOIN) lexicon_entries\b", RegexOptions.CultureInvariant))
                {
                    actual.Add(Path.GetFileNameWithoutExtension(path) + "." + method.Identifier.ValueText);
                }
            }
        }

        // Scalar status is operator inventory; reset/pruning/purge selects identities only. The
        // version writer snapshots evidence, and the historical schema backfill is not retrieval.
        Assert.Equal(new[]
        {
            "AnnalsClaimWriter.SnapshotLexiconProvenanceAsync",
            "DataRetentionService.BuildMemoryResetSelections",
            "DataRetentionService.Pruning.AddLexiconCandidatesCoreAsync",
            "DataRetentionService.Pruning.DeleteLexiconCandidateAsync",
            "GrimoireSchemaInstaller.TryRebuildLexiconFtsAsync",
            "MemoryAnnalsBackfill.ReadBatchAsync",
            "MemoryEndpoints.BuildStatusAsync",
        }.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    private static int[] Ordinals(string path, string method) => [.. ReaderSlots(Method(path, method))
        .Select(slot => int.Parse(slot[(slot.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture)).Distinct()];

    private static string[] ReaderSlots(MethodDeclarationSyntax method) => [.. method.DescendantNodes().OfType<InvocationExpressionSyntax>()
        .Select(invocation =>
        {
            string name = invocation.Expression is MemberAccessExpressionSyntax member ? member.Name.Identifier.ValueText : invocation.Expression.ToString();

            SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;

            int index = name is "ReadCode" or "ReadPositiveInteger" or "ReadOptionalGuid" or "ReadOptionalDigest" ? 1 : 0;

            bool getter = name is "GetString" or "GetValue" or "GetInt32" or "GetInt64";

            return (getter || index == 1) && arguments.Count > index
                && arguments[index].Expression is LiteralExpressionSyntax literal && literal.Token.Value is int ordinal
                ? $"{name}:{ordinal}" : null;
        }).OfType<string>()];

    private static string[] ProjectionColumns(string sql, int query)
    {
        int cursor = 0;

        for (int index = 0; index <= query; index++)
        {
            int start = sql.IndexOf("SELECT ", cursor, StringComparison.Ordinal) + 7;

            Assert.True(start >= 7, "Missing SELECT projection: " + sql);

            int depth = 0;

            int columnStart = start;

            List<string> columns = [];

            for (cursor = start; cursor < sql.Length; cursor++)
            {
                if (sql[cursor] == '(') depth++;

                if (sql[cursor] == ')') depth--;

                bool end = depth == 0 && sql.AsSpan(cursor).StartsWith(" FROM ", StringComparison.Ordinal);

                if (depth == 0 && (sql[cursor] == ',' || end))
                {
                    string column = sql[columnStart..cursor].Trim();

                    int alias = column.LastIndexOf(" AS ", StringComparison.Ordinal);

                    columns.Add(alias >= 0 ? column[(alias + 4)..] : column.StartsWith("EXISTS(", StringComparison.Ordinal)
                        ? "SourceAvailable" : Regex.Replace(column, @"^\w+\.", string.Empty));

                    columnStart = cursor + 1;

                    if (end)
                    {
                        cursor += 6;

                        if (index == query) return [.. columns];

                        break;
                    }
                }
            }
        }

        throw new InvalidOperationException("Unterminated SELECT: " + sql);
    }

    private static string SqlText(MethodDeclarationSyntax method, bool legacy = false)
    {
        Dictionary<string, string> constants = LexiconTrees().SelectMany(tree => tree.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            .Where(variable => variable.Identifier.ValueText is "SelectColumns" or "LegacySelectColumns" or "VersionColumns")
            .ToDictionary(variable => variable.Identifier.ValueText, variable => ((LiteralExpressionSyntax)variable.Initializer!.Value).Token.ValueText);

        string Expand(SyntaxNode node)
        {
            if (node is LiteralExpressionSyntax literal && literal.Token.Value is string value) return value;

            if (node is IdentifierNameSyntax identifier && constants.TryGetValue(identifier.Identifier.ValueText, out string? constant)) return constant;

            if (node is InvocationExpressionSyntax invocation && invocation.Expression is IdentifierNameSyntax helper)
            {
                if (helper.Identifier.ValueText == "EntryColumnsFor") return constants[legacy ? "LegacySelectColumns" : "SelectColumns"];

                if (helper.Identifier.ValueText == "VersionColumnsFor") return legacy
                    ? constants["VersionColumns"].Replace("v.ContentHashFormatCode", "1 AS ContentHashFormatCode", StringComparison.Ordinal)
                    : constants["VersionColumns"];
            }

            if (node is ConditionalExpressionSyntax conditional && conditional.Condition is IdentifierNameSyntax capability
                && capability.Identifier.ValueText is "curation" or "hasFormat" or "legacySchema")
            {
                bool whenTrue = capability.Identifier.ValueText == "legacySchema" ? legacy : !legacy;

                return Expand(whenTrue ? conditional.WhenTrue : conditional.WhenFalse);
            }

            if (node is InterpolatedStringExpressionSyntax interpolated) return string.Concat(interpolated.Contents.Select(content =>
                content is InterpolatedStringTextSyntax text ? text.TextToken.ValueText : Expand(((InterpolationSyntax)content).Expression)));

            return string.Join(" ", node.ChildNodes().Select(Expand));
        }

        return Regex.Replace(Expand(method), @"\s+", " ");
    }

    private static bool Calls(SyntaxNode node, string name) => node.DescendantNodes().OfType<InvocationExpressionSyntax>()
        .Any(invocation => (invocation.Expression is MemberAccessExpressionSyntax member ? member.Name.Identifier.ValueText : invocation.Expression.ToString()) == name);

    private static string Tokens(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));

    private static MethodDeclarationSyntax Method(string path, string name) => Assert.Single(Parse(File.ReadAllText(
        Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "src", path))).DescendantNodes().OfType<MethodDeclarationSyntax>(),
        method => method.Identifier.ValueText == name);

    private static CompilationUnitSyntax Parse(string source) => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetCompilationUnitRoot();

    private static IEnumerable<CompilationUnitSyntax> LexiconTrees() => Directory.EnumerateFiles(Path.Combine(
        NativeSqlCipherTestPaths.RepositoryRoot(), "src", "RetroDownfall.Arcanum.Infrastructure", "Lexicon"), "*.cs")
        .Select(path => Parse(File.ReadAllText(path)));
}
