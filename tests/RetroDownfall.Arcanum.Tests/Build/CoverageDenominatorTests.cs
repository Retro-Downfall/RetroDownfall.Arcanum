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

                bool excluded = type.AttributeLists
                    .SelectMany(static list => list.Attributes)
                    .Any(static attribute => attribute.Name.ToString() is "ExcludeFromCodeCoverage" or "ExcludeFromCodeCoverageAttribute"
                        or "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage"
                        or "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute");

                if (excluded)
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
}
