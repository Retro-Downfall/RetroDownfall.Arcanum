using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildAwardAuthorityTraversalTests
{
    [Fact]
    public void Award_primary_validation_does_not_recursively_enter_lineage_validation()
    {
        string path = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "src", "RetroDownfall.Arcanum.Infrastructure", "Guild", "SignetAuthority.cs");

        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

        MethodDeclarationSyntax[] validators = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText is "LiveRowAsync" or "LiveRowCoreAsync").ToArray();

        Assert.NotEmpty(validators);

        Assert.DoesNotContain(validators.SelectMany(method => method.DescendantNodes().OfType<InvocationExpressionSyntax>()), invocation =>
            invocation.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == "LiveRowAsync");
    }
}
