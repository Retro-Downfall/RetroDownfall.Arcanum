using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The ordinary retention apply path carries no factory-reset arm.
/// </summary>
/// <remarks>
/// <c>ApplyAsync</c> routes every factory reset to its own path before the ordinary one is reached, so a
/// factory-reset arm inside the ordinary path is dead code, and a weaker one: it ran outside the guarded
/// <c>try</c> and without the factory path's lease maintainer and launch checkpoint. A copy that is never
/// run is still a copy a later change can route to by mistake.
/// </remarks>
public sealed class DataRetentionOrdinaryApplyStructureTests
{
    private const string ServicePath =
        "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs";

    private static readonly string[] OrdinaryApplyMethods = ["ApplyOrdinaryAsync", "ApplyStartedOrdinaryAsync"];

    [Fact]
    public void The_ordinary_apply_path_never_references_the_factory_reset_operation()
    {
        string path = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), ServicePath);

        SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

        MethodDeclarationSyntax[] methods =
        [
            .. root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(static method => OrdinaryApplyMethods.Contains(method.Identifier.ValueText)),
        ];

        Assert.Contains(methods, static method => method.Identifier.ValueText == "ApplyOrdinaryAsync");

        string[] references =
        [
            .. methods.SelectMany(static method => method
                .DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(static access =>
                    access.Name.Identifier.ValueText == "FactoryReset"
                    && access.Expression.ToString().EndsWith("DataRetentionOperation", StringComparison.Ordinal))
                .Select(access => $"{method.Identifier.ValueText}: {access}")),
        ];

        Assert.Empty(references);
    }
}
