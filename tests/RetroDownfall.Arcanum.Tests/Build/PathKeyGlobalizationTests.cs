using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The code that fixes the identity of a registered Campaign root, of a spelled path and of the Sanctum's judgement of it never asks
/// <see cref="string.Normalize()"/> for a Unicode normalisation form. The shipping executable runs with invariant globalization, where that
/// method (and <see cref="string.IsNormalized()"/>) hands a non-ASCII string back unchanged and says nothing, so a unit test that runs with
/// the globalization library passes against behaviour the product does not have: two canonically equivalent spellings of one directory
/// would be two keys, a Campaign would be registered twice and a Sanctum would silently not apply. The form is fixed by
/// <c>UnicodeNfc</c>, which runs on the product's own Unicode 17 tables in every host.
/// </summary>
public sealed class PathKeyGlobalizationTests
{
    // The backend projects whose sources hold the path keys. TheForge.Ux is a client and is judged by its own host.
    private static readonly string[] Projects =
    [
        "RetroDownfall.Arcanum.Core",
        "RetroDownfall.Arcanum.Infrastructure",
        "RetroDownfall.Arcanum.Api",
    ];

    // The sources named for a Campaign root or a Sanctum, and the path policy they judge through.
    private static readonly string[] NamedSources =
    [
        "WorkspacePathPolicy.cs",
        "AttachmentSourceSanctum.cs",
        "UnicodeNfc.cs",
        "CovenantUnicodePolicyV1.cs",
    ];

    private static bool IsPathKeySource(string path)
    {
        string name = Path.GetFileName(path);

        return name.StartsWith("Campaign", StringComparison.Ordinal)
            || name.StartsWith("Sanctum", StringComparison.Ordinal)
            || NamedSources.Contains(name, StringComparer.Ordinal);
    }

    private static string[] PathKeySources()
    {
        string root = TestRepositoryPaths.RepositoryRoot();

        return
        [
            .. Projects
                .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
                .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(IsPathKeySource)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Every use, in code and not in a comment, of the globalization library's normalisation: a mention of <c>NormalizationForm</c>, a
    /// call of <c>Normalize</c> or <c>IsNormalized</c> that names a form, and a call of either that names none (the default form is C).
    /// </summary>
    internal static string[] GlobalizationNormalization(string source)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(source).GetRoot();

        List<string> findings = [];

        foreach (SyntaxNode node in root.DescendantNodes())
        {
            switch (node)
            {
                case IdentifierNameSyntax { Identifier.ValueText: "NormalizationForm" } form:
                    findings.Add($"line {form.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: NormalizationForm");

                    break;

                case InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Normalize" or "IsNormalized" } access,
                    ArgumentList.Arguments.Count: 0,
                }:
                    findings.Add($"line {access.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {access.Name.Identifier.ValueText}()");

                    break;
            }
        }

        return [.. findings];
    }

    [Fact]
    public void No_source_that_fixes_the_identity_of_a_path_asks_the_globalization_library_for_a_normalization_form()
    {
        string[] sources = PathKeySources();

        // The scan reads the sources that matter: a rename that left it with nothing to read must not pass for ever.
        string[] names = [.. sources.Select(Path.GetFileName).OfType<string>()];

        Assert.Contains("CampaignRootContainment.cs", names);

        Assert.Contains("CampaignRepository.cs", names);

        Assert.Contains("SanctumGuard.cs", names);

        Assert.Contains("AttachmentSourceSanctum.cs", names);

        Assert.Contains("WorkspacePathPolicy.cs", names);

        Assert.Contains("UnicodeNfc.cs", names);

        string[] offenders =
        [
            .. sources.SelectMany(path => GlobalizationNormalization(File.ReadAllText(path))
                .Select(finding => $"{Path.GetRelativePath(TestRepositoryPaths.RepositoryRoot(), path)} {finding}")),
        ];

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_scan_finds_a_form_a_default_form_and_a_query_and_leaves_the_product_normalization_and_comments_alone()
    {
        const string Source = """
            using System.Text;

            // value.Normalize(NormalizationForm.FormC) in a comment is no use.
            public static class Fixture
            {
                /// <summary>See <see cref="string.Normalize()"/>.</summary>
                public static string Named(string name) => name.Normalize(NormalizationForm.FormC);

                public static string Default(string name) => name.Normalize();

                public static bool Asked(string name) => name.IsNormalized();

                public static bool AskedWithAForm(string name) => name.IsNormalized(System.Text.NormalizationForm.FormD);

                public static string Product(string name) => UnicodeNfc.Normalize(name);

                public static string Instant(string stamp) => UtcInstantText.Normalize(stamp);
            }
            """;

        // A form named in code, the default form, a query of the form, a form named in a qualified name: and nothing in a comment, in a
        // cref or in the product's own helpers.
        Assert.Equal(
            [
                "line 7: NormalizationForm",
                "line 9: Normalize()",
                "line 11: IsNormalized()",
                "line 13: NormalizationForm",
            ],
            GlobalizationNormalization(Source));
    }
}
