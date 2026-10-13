using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Readiness_group_local_source_preserves_actual_contextual_conversion(bool authoredReplacement)
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        MethodDeclarationSyntax[] sends = unit.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(static method => method.Identifier.ValueText is "SendFirstAsync" or "SendSecondAsync"
                && method.Parent is ClassDeclarationSyntax { Identifier.ValueText: "PerUnitRecoveryHandler" }).ToArray();

        Assert.Equal(2, sends.Length);

        string concreteType = authoredReplacement ? "ReadinessConvertedLease" : "ReadinessWorkLease";

        unit = unit.ReplaceNodes(sends, (original, rewritten) =>
        {
            BlockSyntax locals = (BlockSyntax)SyntaxFactory.ParseStatement($$"""
                {
                    RetroDownfall.Arcanum.Infrastructure.Data.LongRest.{{concreteType}} actual =
                        (RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease)workAdmission.Lease;
                    IGrimoireWorkLease lease = actual;
                }
                """);

            Assert.IsType<LocalDeclarationStatementSyntax>(original.Body!.Statements[0]);

            return rewritten.WithBody(rewritten.Body!.WithStatements(
                SyntaxFactory.List(locals.Statements.Concat(rewritten.Body.Statements.Skip(1)))));
        });

        source = unit.ToFullString();

        if (authoredReplacement)
        {
            source += """
                namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
                {
                    internal sealed class ReadinessConvertedLease : IGrimoireWorkLease
                    {
                        private readonly ReadinessWorkLease foreign = new();

                        public static implicit operator ReadinessConvertedLease(ReadinessWorkLease source) => new();

                        public bool TryBeginExternalEffectGroup(out IDisposable group) => foreign.TryBeginExternalEffectGroup(out group);

                        public void Dispose() => foreign.Dispose();

                        public ValueTask DisposeAsync() => foreign.DisposeAsync();
                    }
                }
                """;
        }

        AssertExplicitReadinessLeasePremises(source, "PerUnitRecoveryHandler");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        VariableDeclaratorSyntax[] actuals = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(static variable => variable.Identifier.ValueText == "actual"
                && variable.Ancestors().OfType<MethodDeclarationSyntax>().Any(static method =>
                    method.Identifier.ValueText is "SendFirstAsync" or "SendSecondAsync")).ToArray();

        Assert.Equal(2, actuals.Length);

        foreach (VariableDeclaratorSyntax actual in actuals)
        {
            CastExpressionSyntax cast = Assert.IsType<CastExpressionSyntax>(actual.Initializer!.Value);

            IConversionOperation builtIn = Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(cast));

            Assert.Null(builtIn.OperatorMethod);
            Assert.True(builtIn.Conversion.IsReference);

            Conversion contextual = model.GetConversion(cast);

            Assert.Equal(authoredReplacement, contextual.IsUserDefined);

            IVariableDeclaratorOperation declared = Assert.IsAssignableFrom<IVariableDeclaratorOperation>(model.GetOperation(actual));

            Assert.True(SymbolEqualityComparer.Default.Equals(declared.Symbol.Type, model.GetTypeInfo(cast).ConvertedType));

            if (authoredReplacement)
            {
                IConversionOperation effective = Assert.IsAssignableFrom<IConversionOperation>(declared.Initializer!.Value);

                Assert.True(effective.IsImplicit);
                Assert.True(effective.Conversion.IsUserDefined);
                Assert.True(SymbolEqualityComparer.Default.Equals(effective.OperatorMethod, contextual.MethodSymbol));
                Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessConvertedLease",
                    effective.OperatorMethod!.ContainingType.ToDisplayString());
            }
            else
            {
                Assert.Null(contextual.MethodSymbol);
                Assert.True(contextual.IsIdentity);
            }
        }

        if (authoredReplacement)
        {
            FieldDeclarationSyntax field = Assert.Single(tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>(), declaration =>
                declaration.Parent is ClassDeclarationSyntax { Identifier.ValueText: "ReadinessConvertedLease" });

            IFieldSymbol foreign = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(Assert.Single(field.Declaration.Variables)));

            Assert.True(foreign.IsReadOnly);
            Assert.False(foreign.IsStatic);
            Assert.Equal(Accessibility.Private, foreign.DeclaredAccessibility);

            IObjectCreationOperation created = Assert.IsAssignableFrom<IObjectCreationOperation>(
                model.GetOperation(Assert.Single(field.Declaration.Variables).Initializer!.Value));

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease", created.Type!.ToDisplayString());
            Assert.Empty(created.Arguments);

            ClassDeclarationSyntax convertedType = Assert.IsType<ClassDeclarationSyntax>(field.Parent);

            MethodDeclarationSyntax groupMethod = Assert.Single(convertedType.Members.OfType<MethodDeclarationSyntax>(), static method =>
                method.Identifier.ValueText == "TryBeginExternalEffectGroup");

            IInvocationOperation groupCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(groupMethod.ExpressionBody!.Expression));

            Assert.Equal("TryBeginExternalEffectGroup", groupCall.TargetMethod.Name);
            Assert.True(SymbolEqualityComparer.Default.Equals(groupCall.TargetMethod.ContainingType, created.Type));
            Assert.True(SymbolEqualityComparer.Default.Equals(
                Assert.IsAssignableFrom<IFieldReferenceOperation>(groupCall.Instance).Field, foreign));
        }

        output.WriteLine($"R_READINESS_CONTEXTUAL_LEASE_PREMISE=authored:{authoredReplacement}|explicitBuiltInCastAndActualLocalConversion=passed");

        AssertExplicitReadinessDiscovery(source, "PerUnitRecoveryHandler", authoredReplacement ? "contextual-foreign-lease" : "owned-lease");
    }
}
