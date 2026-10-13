using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Returned_lease_literal_receiver_preserves_effective_conversion(bool authoredConversion)
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        Assert.Equal(1, source.Split("gate = new ReadinessWorkGate();", StringSplitOptions.None).Length - 1);

        source = source.Replace("gate = new ReadinessWorkGate();", authoredConversion
            ? "gate = (AlternateReadinessWorkGate)new ReadinessWorkGate();"
            : "gate = (IGrimoireConnectionAdmissionGate)new ReadinessWorkGate();", StringComparison.Ordinal);

        Assert.Equal(1, source.Split("public ValueTask DisposeAsync() => ValueTask.CompletedTask;", StringSplitOptions.None).Length - 1);

        source = source.Replace("public ValueTask DisposeAsync() => ValueTask.CompletedTask;", """
            public ValueTask DisposeAsync()
            {
                System.IO.File.Delete("literal-gate-selected-lease-cleanup");

                return ValueTask.CompletedTask;
            }
            """, StringComparison.Ordinal);

        if (authoredConversion)
        {
            CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

            ClassDeclarationSyntax gate = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
                type.Identifier.ValueText == "ReadinessWorkGate");

            unit = unit.ReplaceNode(gate, gate.AddMembers(SyntaxFactory.ParseMemberDeclaration("""
                public static explicit operator AlternateReadinessWorkGate(ReadinessWorkGate value)
                    => new AlternateReadinessWorkGate();
                """)!));

            source = unit.ToFullString() + """
                namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
                {
                    internal sealed class AlternateReadinessWorkGate : IGrimoireConnectionAdmissionGate
                    {
                        public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
                            => throw new InvalidOperationException("The selected authored conversion returned another implementation.");
                    }
                }
                """;
        }

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        CastExpressionSyntax cast = Assert.Single(tree.GetRoot().DescendantNodes().OfType<CastExpressionSyntax>(), static expression =>
            expression.Expression is ObjectCreationExpressionSyntax { Type: IdentifierNameSyntax { Identifier.ValueText: "ReadinessWorkGate" } });

        IConversionOperation selected = Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(cast));

        Assert.Equal(authoredConversion, selected.Conversion.IsUserDefined);

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkGate", selected.Operand.Type!.ToDisplayString());

        if (authoredConversion)
        {
            ConversionOperatorDeclarationSyntax declaration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>());

            IMethodSymbol actualOperator = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(declaration));

            Assert.True(SymbolEqualityComparer.Default.Equals(actualOperator, selected.OperatorMethod));

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.AlternateReadinessWorkGate", selected.Type!.ToDisplayString());
        }
        else
        {
            Assert.Null(selected.OperatorMethod);

            Assert.True(selected.Conversion.IsReference);

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate", selected.Type!.ToDisplayString());
        }

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source,
            hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

        bool unresolved = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.DisposeAsync", StringComparison.Ordinal));

        HostedProducerSite[] cleanup = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease"
            && site.Member == "DisposeAsync" && site.Callee == "System.IO.File.Delete"
            && site.Kind == HostedProducerSiteKind.FileSystemEffect
            && site.AuthorityOperationId == "Worker.StartAsync").ToArray();

        Assert.Equal(authoredConversion, unresolved);

        if (authoredConversion) Assert.Empty(cleanup);
        else Assert.NotEmpty(cleanup);
    }
}
