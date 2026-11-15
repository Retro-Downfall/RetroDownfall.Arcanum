using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("actual", true)]
    [InlineData("effect", false)]
    [InlineData("library", false)]
    [InlineData("entrypoint", false)]
    [InlineData("extra-member", false)]
    [InlineData("extra-partial", false)]
    [InlineData("authored-attribute", false)]
    public void Native_generated_import_proof_requires_exact_bodies_and_bound_implementation_parts(string shape, bool reviewed)
    {
        CSharpCompilation compilation = NativeBootstrapWithActualGeneratedImports(shape);

        Assert.Equal(reviewed, HostedGrimoireProducerInventory.ProbeCompletedProductionNativeBootstrap([compilation]));

        if (reviewed)
        {
            HostedProducerDiscovery<HostedProducerSite> result = DiscoverNativeBootstrap(compilation);

            Assert.Empty(result.Diagnostics);

            Assert.Empty(result.Items);
        }
    }

    private static CSharpCompilation NativeBootstrapWithActualGeneratedImports(string shape)
    {
        CSharpCompilation actual = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        INamedTypeSymbol actualOwner = Assert.IsAssignableFrom<INamedTypeSymbol>(
            actual.GetTypeByMetadataName("RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntime"));

        CSharpCompilation compilation = NativeBootstrapFixture();

        foreach (string name in new[] { "GetNativeImageInfo", "GetModuleFileName" })
        {
            IMethodSymbol definition = Assert.Single(actualOwner.GetMembers(name).OfType<IMethodSymbol>());

            IMethodSymbol implementation = Assert.IsAssignableFrom<IMethodSymbol>(definition.PartialImplementationPart);

            Assert.True(SymbolEqualityComparer.Default.Equals(definition, implementation.PartialDefinitionPart));

            MethodDeclarationSyntax method = Assert.IsType<MethodDeclarationSyntax>(
                Assert.Single(implementation.DeclaringSyntaxReferences).GetSyntax());

            TypeDeclarationSyntax part = method.Ancestors().OfType<TypeDeclarationSyntax>().First();

            if (name == "GetNativeImageInfo")
            {
                if (shape == "effect")
                {
                    part = part.ReplaceNode(method, method.WithBody(method.Body!.WithStatements(method.Body.Statements.Insert(0,
                        SyntaxFactory.ParseStatement("global::System.IO.File.Delete(\"generated-native-effect\");")))));
                }
                else if (shape is "library" or "entrypoint")
                {
                    string previous = shape == "library" ? "/usr/lib/libSystem.B.dylib" : "dladdr";

                    string changed = shape == "library" ? "/untrusted/native.dylib" : "forged_dladdr";

                    LiteralExpressionSyntax literal = Assert.Single(part.DescendantNodes().OfType<LiteralExpressionSyntax>(),
                        expression => expression.Token.ValueText == previous);

                    part = part.ReplaceNode(literal, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(changed)));
                }
                else if (shape == "extra-member")
                {
                    part = part.AddMembers(SyntaxFactory.ParseMemberDeclaration(
                        "private static readonly string ExtraNativeInput = global::System.IO.File.ReadAllText(\"extra-native-input\");")!);
                }
            }

            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                "namespace RetroDownfall.Arcanum.Infrastructure.Data { " + part.ToFullString() + " }",
                path: "src/NativeGeneratedFixture/" + name + ".cs"));
        }

        if (shape == "extra-partial")
        {
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
                namespace RetroDownfall.Arcanum.Infrastructure.Data
                {
                    internal sealed partial class SqliteNativeRuntime
                    {
                        private static readonly string ExtraNativeInput = global::System.IO.File.ReadAllText("extra-native-input");
                    }
                }
                """, path: "src/NativeGeneratedFixture/Extra.cs"));
        }
        else if (shape == "authored-attribute")
        {
            // Generated method tokens are unchanged; only their attribute's bound owner changes.
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
                namespace System.CodeDom.Compiler
                {
                    public sealed class GeneratedCodeAttribute : global::System.Attribute
                    {
                        public GeneratedCodeAttribute(string tool, string version)
                        {
                            global::System.IO.File.Delete("forged-generated-attribute");
                        }
                    }
                }
                """, path: "src/NativeGeneratedFixture/Attribute.cs"));
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        INamedTypeSymbol owner = Assert.IsAssignableFrom<INamedTypeSymbol>(
            compilation.GetTypeByMetadataName("RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntime"));

        foreach (string name in new[] { "GetNativeImageInfo", "GetModuleFileName" })
        {
            IMethodSymbol definition = Assert.Single(owner.GetMembers(name).OfType<IMethodSymbol>());

            IMethodSymbol implementation = Assert.IsAssignableFrom<IMethodSymbol>(definition.PartialImplementationPart);

            Assert.True(SymbolEqualityComparer.Default.Equals(definition, implementation.PartialDefinitionPart));
        }

        return compilation;
    }
}
