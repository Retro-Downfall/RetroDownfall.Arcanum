using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// No production source constructs a <see cref="System.Text.RegularExpressions.Regex"/> that can run
/// forever on hostile input.
/// </summary>
/// <remarks>
/// A backtracking pattern with no match timeout can be driven into catastrophic backtracking by text the
/// process did not write (a prompt, a file, a model reply). A hand-constructed Regex therefore has to
/// pass a <see cref="TimeSpan"/> timeout; the alternative is a source-generated
/// <c>[GeneratedRegex(..., matchTimeoutMilliseconds: n)]</c> partial method, which this scan does not
/// look at. The check is syntactic, so it sees explicit <c>new Regex(...)</c> and the target-typed
/// <c>Regex field = new(...)</c> form.
/// </remarks>
public sealed class RegexConventionTests
{
    /// <summary>
    /// Sites that construct a pattern only to learn whether it parses and never match with it, so no
    /// input can drive them. Keyed by repository-relative path; the value says why the site is exempt.
    /// </summary>
    private static readonly Dictionary<string, string> ConstructionOnlySites = new(StringComparer.Ordinal)
    {
        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/ProvingGroundsViewModel.cs"] =
            "builds the operator's Inquisitor pattern solely to surface a parse error; the match runs in ProvingGroundsArbiter under a timeout",
    };

    [Fact]
    public void NoNewRegexWithoutTimeoutOrGenerated()
    {
        string[] violations = FindRegexSites()
            .Where(static site => !site.HasTimeout)
            .Where(static site => !ConstructionOnlySites.ContainsKey(site.RelativePath))
            .Select(static site => $"{site.RelativePath}:{site.Line} {site.Describe}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Regex sites without a match timeout (use [GeneratedRegex(..., matchTimeoutMilliseconds: n)] "
                + "or pass a TimeSpan):\n"
                + string.Join("\n", violations));
    }

    [Fact]
    public void Every_construction_only_exemption_still_names_a_regex_site()
    {
        string[] staleExemptions = ConstructionOnlySites.Keys
            .Where(static path => !FindRegexSites().Any(site => site.RelativePath == path && !site.HasTimeout))
            .ToArray();

        Assert.True(
            staleExemptions.Length == 0,
            "Exemptions that no longer match a timeout-less Regex site:\n" + string.Join("\n", staleExemptions));
    }

    private static IEnumerable<RegexSite> FindRegexSites()
    {
        string repositoryRoot = TestRepositoryPaths.RepositoryRoot();
        string sourceRoot = Path.Combine(repositoryRoot, "src");
        string separator = Path.DirectorySeparatorChar.ToString();

        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                || path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            {
                continue;
            }

            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
            string relativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

            foreach (ObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (IsRegexTypeName(creation.Type))
                {
                    yield return Site(relativePath, creation, "new Regex(...)", creation.ArgumentList?.Arguments.Count ?? 0);
                }
            }

            foreach (ImplicitObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
            {
                if (IsDeclaredAsRegex(creation))
                {
                    yield return Site(relativePath, creation, "Regex x = new(...)", creation.ArgumentList.Arguments.Count);
                }
            }
        }
    }

    /// <summary>
    /// <c>new Regex(pattern, options, timeout)</c> is the only constructor shape that carries a timeout,
    /// so fewer than three arguments means the engine's infinite default.
    /// </summary>
    private static RegexSite Site(string relativePath, SyntaxNode node, string describe, int argumentCount) =>
        new(
            relativePath,
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            describe + " with no timeout argument",
            HasTimeout: argumentCount >= 3);

    private static bool IsRegexTypeName(TypeSyntax type) =>
        type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text == "Regex",
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text == "Regex",
            _ => false,
        };

    private static bool IsDeclaredAsRegex(ImplicitObjectCreationExpressionSyntax creation)
    {
        if (creation.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator })
        {
            return false;
        }

        return declarator.Parent switch
        {
            VariableDeclarationSyntax declaration => IsRegexTypeName(
                declaration.Type is NullableTypeSyntax nullable ? nullable.ElementType : declaration.Type),
            _ => false,
        };
    }

    private sealed record RegexSite(string RelativePath, int Line, string Describe, bool HasTimeout);
}
