using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("preserved")]
    [InlineData("later-null-write")]
    [InlineData("later-out-write")]
    [InlineData("formal-null-before-capture")]
    public void Readiness_inner_scope_requires_stable_actual_scope_factory_storage(string shape)
    {
        string source = ExplicitReadinessScopedFixture("inner-scope");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        ClassDeclarationSyntax reconciler = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "LongRunningOperationReconciler");

        ConstructorDeclarationSyntax constructor = Assert.Single(reconciler.Members.OfType<ConstructorDeclarationSyntax>(), static declaration =>
            declaration.ParameterList.Parameters.Count == 2);

        if (shape != "preserved")
        {
            BlockSyntax body = shape == "formal-null-before-capture"
                ? constructor.Body!.WithStatements(constructor.Body.Statements.Insert(0, SyntaxFactory.ParseStatement("scopeFactory = null!;")))
                : constructor.Body!.AddStatements(SyntaxFactory.ParseStatement(shape == "later-null-write"
                    ? "_scopeFactory = null;" : "ResetScopeFactory(out _scopeFactory);"));

            reconciler = reconciler.ReplaceNode(constructor, constructor.WithBody(body));

            if (shape == "later-out-write")
            {
                reconciler = reconciler.AddMembers(SyntaxFactory.ParseMemberDeclaration("""
                    private static void ResetScopeFactory(out IServiceScopeFactory? value) { value = null; }
                    """)!);
            }

            source = unit.ReplaceNode(Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
                type.Identifier.ValueText == "LongRunningOperationReconciler"), reconciler).ToFullString();
        }

        AssertExplicitReadinessLeasePremises(source, "PerUnitCoordinator");

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax actual = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "LongRunningOperationReconciler");

        ConstructorDeclarationSyntax selected = Assert.Single(actual.Members.OfType<ConstructorDeclarationSyntax>(), static declaration =>
            declaration.ParameterList.Parameters.Count == 2);

        IMethodSymbol selectedConstructor = model.GetDeclaredSymbol(selected)!;

        Assert.Equal("Microsoft.Extensions.DependencyInjection.IServiceScopeFactory", selectedConstructor.Parameters[1].Type.ToDisplayString());

        IFieldSymbol field = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(Assert.Single(actual.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(static declaration => declaration.Declaration.Variables), static variable => variable.Identifier.ValueText == "_scopeFactory")));

        Assert.True(field.IsReadOnly);

        Assert.False(field.IsStatic);

        Assert.Equal(Accessibility.Private, field.DeclaredAccessibility);

        AssignmentExpressionSyntax captured = Assert.Single(selected.Body!.DescendantNodes().OfType<AssignmentExpressionSyntax>(), assignment =>
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, field)
            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Right).Symbol, selectedConstructor.Parameters[1]));

        if (shape == "later-null-write")
        {
            AssignmentExpressionSyntax replaced = Assert.Single(selected.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>(), assignment =>
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, field) && assignment.Right.IsKind(SyntaxKind.NullLiteralExpression));

            Assert.True(replaced.SpanStart > captured.Span.End);

            Assert.Null(model.GetConstantValue(replaced.Right).Value);
        }
        else if (shape == "later-out-write")
        {
            InvocationExpressionSyntax reset = Assert.Single(selected.Body.DescendantNodes().OfType<InvocationExpressionSyntax>(), static call =>
                call.Expression is IdentifierNameSyntax { Identifier.ValueText: "ResetScopeFactory" });

            IArgumentOperation argument = Assert.Single(Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(reset)).Arguments);

            Assert.Equal(RefKind.Out, argument.Parameter!.RefKind);

            Assert.True(SymbolEqualityComparer.Default.Equals(field, Assert.IsAssignableFrom<IFieldReferenceOperation>(argument.Value).Field));

            Assert.True(reset.SpanStart > captured.Span.End);
        }

        else if (shape == "formal-null-before-capture")
        {
            AssignmentExpressionSyntax changedFormal = Assert.Single(selected.Body!.DescendantNodes().OfType<AssignmentExpressionSyntax>(), assignment =>
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, selectedConstructor.Parameters[1]));

            Assert.True(changedFormal.Span.End < captured.SpanStart);

            Assert.True(SymbolEqualityComparer.Default.Equals(selectedConstructor.Parameters[1], Assert.IsAssignableFrom<IParameterReferenceOperation>(model.GetOperation(changedFormal.Left)).Parameter));
        }

        AssertExplicitReadinessDiscovery(source, "PerUnitCoordinator", shape == "preserved" ? "owned-lease" : "replaced-scope-factory");
    }
}
