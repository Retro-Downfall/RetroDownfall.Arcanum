using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("tail-return")]
    [InlineData("awaited")]
    [InlineData("detached")]
    public void Readiness_scoped_forwarder_requires_actual_owned_completion(string shape)
    {
        string source = ExplicitReadinessScopedFixture("inner-scope");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        MethodDeclarationSyntax forwarder = Assert.Single(unit.DescendantNodes().OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "RecoverAsync" && method.Parent is ClassDeclarationSyntax { Identifier.ValueText: "PerUnitRecoveryHandler" });

        if (shape != "tail-return")
        {
            MethodDeclarationSyntax replacement = forwarder.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody((BlockSyntax)SyntaxFactory.ParseStatement(shape == "awaited"
                    ? "{ await coordinator.RecoverAsync(operation, cancellationToken); }"
                    : "{ _ = coordinator.RecoverAsync(operation, cancellationToken); return Task.CompletedTask; }"));

            if (shape == "awaited") replacement = replacement.AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));

            source = unit.ReplaceNode(forwarder, replacement).ToFullString();
        }

        AssertExplicitReadinessLeasePremises(source, "PerUnitCoordinator");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        forwarder = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "RecoverAsync" && method.Parent is ClassDeclarationSyntax { Identifier.ValueText: "PerUnitRecoveryHandler" });

        InvocationExpressionSyntax call = Assert.Single(forwarder.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation invocation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(call));

        Assert.Equal("PerUnitCoordinator", invocation.TargetMethod.ContainingType.Name);

        IParameterSymbol receiver = Assert.IsAssignableFrom<IParameterReferenceOperation>(invocation.Instance).Parameter;

        Assert.Equal("PerUnitCoordinator", receiver.Type.Name);

        Assert.Equal(MethodKind.Constructor, Assert.IsAssignableFrom<IMethodSymbol>(receiver.ContainingSymbol).MethodKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(forwarder)!.ContainingType, receiver.ContainingType));

        if (shape == "tail-return")
        {
            Assert.False(model.GetDeclaredSymbol(forwarder)!.IsAsync);

            Assert.Same(call, forwarder.ExpressionBody!.Expression);
        }
        else if (shape == "awaited")
        {
            Assert.True(model.GetDeclaredSymbol(forwarder)!.IsAsync);

            Assert.Same(call, Assert.Single(forwarder.DescendantNodes().OfType<AwaitExpressionSyntax>()).Expression);
        }
        else
        {
            AssignmentExpressionSyntax discarded = Assert.Single(forwarder.DescendantNodes().OfType<AssignmentExpressionSyntax>());

            Assert.IsAssignableFrom<IDiscardOperation>(Assert.IsAssignableFrom<ISimpleAssignmentOperation>(model.GetOperation(discarded)).Target);

            Assert.Same(call, discarded.Right);

            ReturnStatementSyntax returned = Assert.Single(forwarder.DescendantNodes().OfType<ReturnStatementSyntax>());

            Assert.Equal("CompletedTask", Assert.IsAssignableFrom<IPropertySymbol>(model.GetSymbolInfo(returned.Expression!).Symbol).Name);

            Assert.Empty(forwarder.DescendantNodes().OfType<AwaitExpressionSyntax>());
        }

        output.WriteLine($"R_READINESS_FORWARDING_PREMISES={shape}|actualConstructorReceiverAndTaskCompletionShape=passed");

        AssertExplicitReadinessDiscovery(source, "PerUnitCoordinator", shape == "detached" ? "detached-forwarder" : "owned-lease");
    }
}
