using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Readiness_fresh_holder_census_checks_provider_exposure_in_primitive_service_factory(bool prebind)
    {
        string source = ExplicitReadinessScopedFixture("inner-scope");

        CSharpCompilation original = Compile(source);

        Assert.Empty(original.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree originalTree = Assert.Single(original.SyntaxTrees);

        SemanticModel originalModel = original.GetSemanticModel(originalTree);

        InvocationExpressionSyntax registration = Assert.Single(originalTree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>(), call => originalModel.GetSymbolInfo(call).Symbol is IMethodSymbol
                { Name: "AddScoped", TypeArguments: [_, INamedTypeSymbol { Name: "ExternalRecoveryHandler" }] });

        ExpressionStatementSyntax registeredStatement = Assert.IsType<ExpressionStatementSyntax>(registration.Parent);

        ExpressionSyntax services = Assert.IsType<MemberAccessExpressionSyntax>(registration.Expression).Expression;

        const string annotation = "primitive-factory-handler-registration";

        SyntaxNode root = originalTree.GetRoot().ReplaceNode(registeredStatement,
            registeredStatement.WithAdditionalAnnotations(new SyntaxAnnotation(annotation)));

        ClassDeclarationSyntax external = Assert.Single(root.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "ExternalRecoveryHandler");

        root = root.ReplaceNode(external, external.WithParameterList(SyntaxFactory.ParseParameterList("(string marker)")));

        string factory = prebind
            ? """
                static sp =>
                {
                    _ = sp.GetRequiredService<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission>()
                        .Bind(new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease());

                    return "marker";
                }
                """
            : "static sp => \"marker\"";

        root = root.InsertNodesBefore(Assert.Single(root.GetAnnotatedNodes(annotation)),
            [SyntaxFactory.ParseStatement(services + ".AddScoped<string>(" + factory + ");")]);

        source = root.ToFullString();

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax activated = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "ExternalRecoveryHandler");

        IMethodSymbol constructor = Assert.Single(model.GetDeclaredSymbol(activated)!.InstanceConstructors);

        Assert.Equal(SpecialType.System_String, Assert.Single(constructor.Parameters).Type.SpecialType);

        InvocationExpressionSyntax stringRegistration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "AddScoped", TypeArguments: [ITypeSymbol { SpecialType: SpecialType.System_String }] });

        IMethodSymbol registrationMethod = (IMethodSymbol)model.GetSymbolInfo(stringRegistration).Symbol!;

        Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions", registrationMethod.ContainingType.ToDisplayString());

        SimpleLambdaExpressionSyntax lambda = Assert.IsType<SimpleLambdaExpressionSyntax>(Assert.Single(stringRegistration.ArgumentList.Arguments).Expression);

        IParameterSymbol provider = model.GetDeclaredSymbol(lambda.Parameter)!;

        Assert.Equal("System.IServiceProvider", provider.Type.ToDisplayString());

        if (prebind)
        {
            InvocationExpressionSyntax resolution = Assert.Single(lambda.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
                model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "GetRequiredService", TypeArguments: [INamedTypeSymbol { Name: "LongRestWorkAdmission" }] });

            Assert.True(SymbolEqualityComparer.Default.Equals(provider,
                model.GetSymbolInfo(Assert.IsType<MemberAccessExpressionSyntax>(resolution.Expression).Expression).Symbol));

            InvocationExpressionSyntax bind = Assert.Single(lambda.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
                model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Bind", ContainingType.Name: "LongRestWorkAdmission" });

            Assert.Equal(resolution.Span, Assert.IsType<MemberAccessExpressionSyntax>(bind.Expression).Expression.Span);

            Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions",
                ((IMethodSymbol)model.GetSymbolInfo(resolution).Symbol!).ContainingType.ToDisplayString());
        }
        else
        {
            Assert.Equal("marker", model.GetConstantValue(Assert.IsAssignableFrom<ExpressionSyntax>(lambda.Body)).Value);
        }

        AssertExplicitReadinessLeasePremises(source, "PerUnitCoordinator");

        AssertExplicitReadinessDiscovery(source, "PerUnitCoordinator", prebind ? "primitive-factory-prebinding" : "owned-lease");
    }
}
