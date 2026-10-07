using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The coverage gate measures what it is allowed to see. A type-level
/// <c>ExcludeFromCodeCoverage</c> removes a type from the denominator entirely, so on a security
/// boundary it converts "this code is untested" into "this code does not count".
/// </summary>
public sealed class CoverageDenominatorTests
{
    /// <summary>
    /// The internal MCP tool server is the host's authority over workspace reads, writes and process
    /// execution on behalf of a model, and it is about eight thousand lines across sixteen partial
    /// files. One attribute on any part excluded all of them from the 80/70 floors. The containment
    /// primitives it calls are held to the 100% security gate, but the tool handlers that decide when
    /// to call them were not counted at all.
    /// </summary>
    [Fact]
    public void The_internal_tool_server_is_inside_the_coverage_denominator()
    {
        string directory = Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Mcp");

        Assert.True(Directory.Exists(directory), $"Missing source directory: {directory}");

        List<string> parts = [];

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();

            foreach (ClassDeclarationSyntax type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (type.Identifier.Text != "ArcanumInternalToolServer")
                {
                    continue;
                }

                parts.Add(Path.GetRelativePath(directory, file));

                if (HasTypeLevelExclusion(type))
                {
                    offenders.Add(Path.GetRelativePath(directory, file));
                }
            }
        }

        Assert.True(
            parts.Count > 1,
            "The scan did not find the partial parts of ArcanumInternalToolServer, so it proved nothing.");

        Assert.True(
            offenders.Count == 0,
            "A part of ArcanumInternalToolServer carries a type-level ExcludeFromCodeCoverage, which "
            + "removes every part of the partial type from the coverage denominator. Exclude only a "
            + "genuinely unreachable member, with a reason:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));
    }

    /// <summary>
    /// The design says every type excluded through <c>ExcludeFromCodeCoverage</c> carries an inline
    /// reason, and a coverage reviewer trusts that rather than auditing each exclusion. A bare
    /// attribute removes a whole type from the 80/70 denominator with no recorded justification, so
    /// every type-level exclusion under <c>src</c> must carry a trailing or immediately preceding
    /// comment, or a <c>Justification</c>.
    /// </summary>
    [Fact]
    public void Every_type_level_exclusion_in_src_carries_an_inline_reason()
    {
        string sourceRoot = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src");

        List<string> offenders = [];

        int exclusions = 0;

        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, file);

            if (relative.Split(Path.DirectorySeparatorChar).Any(static part => part is "bin" or "obj"))
            {
                continue;
            }

            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();

            foreach (BaseTypeDeclarationSyntax type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                foreach (AttributeListSyntax list in type.AttributeLists)
                {
                    foreach (AttributeSyntax attribute in list.Attributes.Where(IsExclusion))
                    {
                        exclusions++;

                        if (!HasReason(list, attribute))
                        {
                            int line = list.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                            offenders.Add($"{relative}:{line} ({type.Identifier.Text})");
                        }
                    }
                }
            }
        }

        Assert.True(
            exclusions > 0,
            "The scan found no type-level ExcludeFromCodeCoverage under src, so it proved nothing.");

        Assert.True(
            offenders.Count == 0,
            "A type-level ExcludeFromCodeCoverage carries no inline reason. Give it a trailing "
            + "'// Reason: ...' comment (or a Justification) that says why the type has no portable "
            + "test surface and where its behaviour is covered:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));
    }

    /// <summary>
    /// Four small types near a security boundary still carry a type-level exclusion: the startup check
    /// that runs and logs the file-permission self-check, the manager that spawns external MCP server
    /// subprocesses, and the two platform sandbox shims (the Windows AppContainer launcher and the
    /// Linux Landlock ABI). The design used to say a type-level exclusion "never covers a security
    /// boundary", which the tree did not follow. While any of these attributes remains, the design has
    /// to name the type instead of claiming a rule the code breaks.
    /// </summary>
    [Theory]

    [InlineData("Hosting", "ArcanumSecurityStartupChecks")]

    [InlineData("Mcp", "McpConnectionManager")]

    [InlineData("Process", "WindowsAppContainerLauncher")]

    [InlineData("Process", "LinuxLandlock")]

    public void The_design_names_each_security_adjacent_type_that_still_carries_a_type_level_exclusion(
        string folder,
        string typeName)
    {
        string directory = Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            folder);

        List<ClassDeclarationSyntax> declarations = [];

        foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            declarations.AddRange(
                CSharpSyntaxTree.ParseText(File.ReadAllText(file))
                    .GetRoot()
                    .DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(type => type.Identifier.Text == typeName));
        }

        Assert.NotEmpty(declarations);

        if (!declarations.Any(HasTypeLevelExclusion))
        {
            return;
        }

        string design = File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", "Arcanum.DESIGN.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        string paragraph = design
            .Split('\n')
            .Single(static line => line.StartsWith("Types excluded through `[ExcludeFromCodeCoverage]`", StringComparison.Ordinal));

        Assert.Contains(typeName, paragraph, StringComparison.Ordinal);

        Assert.DoesNotContain("never covers a security boundary", paragraph, StringComparison.Ordinal);
    }

    private static bool HasTypeLevelExclusion(ClassDeclarationSyntax type) =>
        type.AttributeLists
            .SelectMany(static list => list.Attributes)
            .Any(IsExclusion);

    private static bool IsExclusion(AttributeSyntax attribute) =>
        attribute.Name.ToString() is "ExcludeFromCodeCoverage" or "ExcludeFromCodeCoverageAttribute"
            or "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage"
            or "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute";

    private static bool HasReason(AttributeListSyntax list, AttributeSyntax attribute)
    {
        bool justified = attribute.ArgumentList?.Arguments.Any(static argument =>
            argument.NameEquals?.Name.Identifier.Text == "Justification"
            && argument.Expression is LiteralExpressionSyntax { Token.ValueText.Length: > 0 }) == true;

        bool trailingComment = list.CloseBracketToken.TrailingTrivia.Any(static trivia =>
            trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            && trivia.ToString().TrimStart('/').Trim().Length > 0);

        bool precedingComment = list.OpenBracketToken.LeadingTrivia
            .Reverse()
            .SkipWhile(static trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia)
                || trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            .FirstOrDefault() is { } nearest
            && nearest.IsKind(SyntaxKind.SingleLineCommentTrivia)
            && nearest.ToString().TrimStart('/').Trim().Length > 0;

        return justified || trailingComment || precedingComment;
    }
}
