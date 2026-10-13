using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("property-arrow")]
    [InlineData("accessor-arrow")]
    [InlineData("block-unused-lambda")]
    [InlineData("block-unused-local-function")]
    public void Readiness_storage_observation_requires_actual_getter_expression(string shape)
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        PropertyDeclarationSyntax original = Assert.Single(unit.DescendantNodes().OfType<PropertyDeclarationSyntax>(), static property =>
            property.Identifier.ValueText == "HasLease");

        string declaration = shape switch
        {
            "property-arrow" => "internal bool HasLease => current is not null;",
            "accessor-arrow" => "internal bool HasLease { get => current is not null; }",
            "block-unused-lambda" => "internal bool HasLease { get { System.Func<bool> unused = () => current is not null; return true; } }",
            "block-unused-local-function" => "internal bool HasLease { get { bool Unused() => current is not null; return true; } }",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        source = unit.ReplaceNode(original, SyntaxFactory.ParseMemberDeclaration(declaration)!).ToFullString();

        AssertExplicitReadinessLeasePremises(source, "PerUnitRecoveryHandler");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        PropertyDeclarationSyntax property = Assert.Single(tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>(), static candidate =>
            candidate.Identifier.ValueText == "HasLease");

        IPropertySymbol symbol = Assert.IsAssignableFrom<IPropertySymbol>(model.GetDeclaredSymbol(property));

        Assert.Equal(SpecialType.System_Boolean, symbol.Type.SpecialType);

        Assert.Null(symbol.SetMethod);

        Assert.Equal(MethodKind.PropertyGet, symbol.GetMethod!.MethodKind);

        IfStatementSyntax guard = Assert.Single(tree.GetRoot().DescendantNodes().OfType<IfStatementSyntax>(), candidate =>
            model.GetSymbolInfo(candidate.Condition).Symbol is IPropertySymbol selected
            && SymbolEqualityComparer.Default.Equals(selected, symbol));

        IPropertyReferenceOperation read = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(guard.Condition));

        Assert.True(SymbolEqualityComparer.Default.Equals(symbol.GetMethod, read.Property.GetMethod));

        IsPatternExpressionSyntax observation = Assert.Single(property.DescendantNodes().OfType<IsPatternExpressionSyntax>());

        IFieldSymbol storage = Assert.IsAssignableFrom<IFieldSymbol>(model.GetSymbolInfo(observation.Expression).Symbol);

        Assert.Equal("current", storage.Name);

        Assert.True(SymbolEqualityComparer.Default.Equals(symbol.ContainingType, storage.ContainingType));

        UnaryPatternSyntax notNull = Assert.IsType<UnaryPatternSyntax>(observation.Pattern);

        Assert.True(Assert.IsType<LiteralExpressionSyntax>(Assert.IsType<ConstantPatternSyntax>(notNull.Pattern).Expression)
            .IsKind(SyntaxKind.NullLiteralExpression));

        bool supported = shape is "property-arrow" or "accessor-arrow";

        if (shape == "property-arrow")
        {
            // Collector stores this expression itself as the authored getter source.
            Assert.Same(observation, property.ExpressionBody!.Expression);
        }
        else
        {
            AccessorDeclarationSyntax getter = Assert.Single(property.AccessorList!.Accessors);

            Assert.True(SymbolEqualityComparer.Default.Equals(symbol.GetMethod, model.GetDeclaredSymbol(getter)));

            if (shape == "accessor-arrow")
            {
                Assert.Same(observation, getter.ExpressionBody!.Expression);
            }
            else
            {
                Assert.Null(getter.ExpressionBody);

                ReturnStatementSyntax returned = Assert.IsType<ReturnStatementSyntax>(getter.Body!.Statements.Last());

                Assert.True(Assert.IsType<LiteralExpressionSyntax>(returned.Expression).IsKind(SyntaxKind.TrueLiteralExpression));

                Assert.True(model.GetConstantValue(returned.Expression).HasValue);

                Assert.Equal(true, model.GetConstantValue(returned.Expression).Value);

                if (shape == "block-unused-lambda")
                {
                    Assert.Single(getter.DescendantNodes().OfType<LambdaExpressionSyntax>());

                    Assert.Empty(getter.DescendantNodes().OfType<ArrowExpressionClauseSyntax>());
                }
                else
                {
                    LocalFunctionStatementSyntax unused = Assert.Single(getter.DescendantNodes().OfType<LocalFunctionStatementSyntax>());

                    Assert.Equal(MethodKind.LocalFunction, model.GetDeclaredSymbol(unused)!.MethodKind);

                    Assert.Same(observation, Assert.Single(getter.DescendantNodes().OfType<ArrowExpressionClauseSyntax>()).Expression);
                }

                Assert.Empty(getter.DescendantNodes().OfType<InvocationExpressionSyntax>());
            }
        }

        output.WriteLine($"R_READINESS_OBSERVATION_PREMISES={shape}|selectedGetterAndExactStorage=passed");

        AssertExplicitReadinessDiscovery(source, "PerUnitRecoveryHandler", supported ? "owned-lease" : "unsupported-getter-body");
    }
}
