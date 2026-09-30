using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The selective-erasure structural pins (spec §19.3) that no behavioural test can hold on its own.
/// </summary>
/// <remarks>
/// <para><b>Extraction is the only Saga writer.</b> Saga reports its inference-provider authorship as
/// Known without measuring it, and that is honest only while every memory arrives from a headless
/// extraction. So exactly one statement in <c>src</c> inserts a Saga row, and exactly one production
/// file calls the store's insert.</para>
///
/// <para><b>New erasure log lines are content-free.</b> A log template that names an erased item's
/// content, name or key would copy it into application logs, which an erase never reaches.
/// <see cref="ContentFreeLogFiles"/> is closed: every later erasure task appends its new source files
/// to it.</para>
/// </remarks>
public sealed class MemoryErasureStructuralTests
{
    /// <summary>
    /// Every erasure source file whose log templates this scan holds, relative to
    /// <c>src/RetroDownfall.Arcanum.Infrastructure/</c>. Closed: a new erasure source file joins it.
    /// </summary>
    /// <remarks>
    /// Existing chokepoint owners such as the Lexicon service are not listed. They already log names
    /// under the rule that preceded this one, and the content-free rule covers new log lines.
    /// </remarks>
    internal static readonly string[] ContentFreeLogFiles =
    [
        "Security/MemoryErasureKeyring.cs",
        "Data/MemoryErasureEvidence.cs",
        "Data/MemoryErasureGuard.cs",
        "Data/MemoryErasureKeyWarmup.cs",
        "Data/SagaErasureWriteGate.cs",
        "Data/Covenant/CovenantAgentErasureGate.cs",
        "Memory/MemoryErasureTokens.cs",
        "Data/GrimoireWalCheckpoint.cs",
        "Memory/MemoryErasureScrubber.cs",
        "Memory/MemoryErasureExposure.cs",
        "Memory/MemoryErasureProtocol.cs",
    ];

    private const string InfrastructureRoot = "src/RetroDownfall.Arcanum.Infrastructure";

    private const string SagaStore = "src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs";

    private const string SagaExtraction = "src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs";

    private static readonly Regex SagaInsert = new(
        @"INSERT\s+(OR\s+\w+\s+)?INTO\s+""?saga_memories""?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ForbiddenPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name",
        "Key",
        "NormalizedKey",
        "Content",
        "Fact",
        "Facts",
    };

    private static readonly Regex Placeholder = new(
        @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:[,:][^}]*)?\}",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Extraction_is_the_only_saga_writer()
    {
        // The pattern is shown to catch every shape it names before its silence is trusted.
        Assert.Matches(SagaInsert, "INSERT INTO saga_memories (Id) VALUES ($id)");

        Assert.Matches(SagaInsert, "insert or ignore into \"saga_memories\" (Id) VALUES ($id)");

        Assert.DoesNotMatch(SagaInsert, "INSERT INTO saga_memories_archive (Id) VALUES ($id)");

        (string Path, int Matches)[] inserts =
        [
            .. Sources("*.cs")
                .Concat(Sources("*.sql"))
                .Select(source => (source.Path, SagaInsert.Matches(source.Text).Count))
                .Where(static source => source.Count > 0)
                .OrderBy(static source => source.Path, StringComparer.Ordinal),
        ];

        Assert.Equal([(SagaStore, 1)], inserts);

        (string Path, int Calls)[] callers =
        [
            .. Sources("*.cs")
                .Where(static source => source.Text.Contains("ISagaMemoryStore", StringComparison.Ordinal))
                .Select(static source => (source.Path, InsertAsyncCalls(source.Text)))
                .Where(static source => source.Item2 > 0)
                .OrderBy(static source => source.Path, StringComparer.Ordinal),
        ];

        Assert.Equal([(SagaExtraction, 2)], callers);
    }

    [Fact]
    public void New_erasure_log_templates_are_content_free()
    {
        string root = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), InfrastructureRoot);

        Assert.Equal(ContentFreeLogFiles.Length, ContentFreeLogFiles.Distinct(StringComparer.Ordinal).Count());

        List<string> violations = [];

        foreach (string file in ContentFreeLogFiles)
        {
            string path = Path.Combine(root, file);

            Assert.True(File.Exists(path), $"{InfrastructureRoot}/{file} is listed but does not exist.");

            violations.AddRange(
                LogTemplateViolations(File.ReadAllText(path)).Select(violation => $"{file}: {violation}"));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void The_log_template_scan_flags_a_named_placeholder()
    {
        const string flagged = """
            internal sealed class Fixture(Microsoft.Extensions.Logging.ILogger logger)
            {
                internal void Erase(string name) => logger.LogWarning("Erased {Name}.", name);
            }
            """;

        Assert.Single(LogTemplateViolations(flagged));

        const string attribute = """
            internal static partial class Fixture
            {
                [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Erased {normalizedKey:l} at {Store}.")]
                internal static partial void Erased(ILogger logger, string normalizedKey, int store);

                [LoggerMessage(2, LogLevel.Information, "Released {Facts}.")]
                internal static partial void Released(ILogger logger, string facts);
            }
            """;

        Assert.Equal(2, LogTemplateViolations(attribute).Count);

        const string clean = """
            internal sealed class Fixture(Microsoft.Extensions.Logging.ILogger logger)
            {
                internal void Erase(int store) => logger.LogInformation("Erased one item in store {Store}; {NameCount} names.", store, 1);

                internal string Describe(string name) => string.Format("Erased {Name}.", name);
            }
            """;

        Assert.Empty(LogTemplateViolations(clean));
    }

    /// <summary>
    /// Every placeholder the scan forbids in a template passed to a <c>Log…</c> invocation or declared
    /// on a <c>[LoggerMessage]</c> attribute.
    /// </summary>
    private static List<string> LogTemplateViolations(string source)
    {
        CompilationUnitSyntax root = CSharpSyntaxTree
            .ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))
            .GetCompilationUnitRoot();

        IEnumerable<SyntaxNode> templates = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(static invocation => InvokedName(invocation).StartsWith("Log", StringComparison.Ordinal))
            .Select(static invocation => (SyntaxNode)invocation.ArgumentList)
            .Concat(root.DescendantNodes()
                .OfType<AttributeSyntax>()
                .Where(static attribute => attribute.Name.ToString() is "LoggerMessage" or "LoggerMessageAttribute"
                    || attribute.Name.ToString().EndsWith(".LoggerMessage", StringComparison.Ordinal)
                    || attribute.Name.ToString().EndsWith(".LoggerMessageAttribute", StringComparison.Ordinal))
                .Where(static attribute => attribute.ArgumentList is not null)
                .Select(static attribute => (SyntaxNode)attribute.ArgumentList!));

        List<string> violations = [];

        foreach (SyntaxNode arguments in templates)
        {
            foreach (LiteralExpressionSyntax literal in arguments.DescendantNodes()
                .OfType<LiteralExpressionSyntax>()
                .Where(static literal => literal.IsKind(SyntaxKind.StringLiteralExpression)))
            {
                foreach (Match match in Placeholder.Matches(literal.Token.ValueText))
                {
                    if (ForbiddenPlaceholders.Contains(match.Groups["name"].Value))
                    {
                        violations.Add($"{match.Value} in \"{literal.Token.ValueText}\"");
                    }
                }
            }
        }

        return violations;
    }

    private static string InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => string.Empty,
    };

    private static int InsertAsyncCalls(string source) =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))
            .GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(static invocation => invocation.Expression is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText == "InsertAsync");

    /// <summary>Every authored file under <c>src</c> matching the pattern, whole and repository-relative.</summary>
    private static IEnumerable<(string Path, string Text)> Sources(string pattern)
    {
        string repositoryRoot = NativeSqlCipherTestPaths.RepositoryRoot();

        List<(string Path, string Text)> sources = [];

        foreach (string file in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), pattern, SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            sources.Add((Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/'), File.ReadAllText(file)));
        }

        Assert.NotEmpty(sources);

        return sources;
    }
}
