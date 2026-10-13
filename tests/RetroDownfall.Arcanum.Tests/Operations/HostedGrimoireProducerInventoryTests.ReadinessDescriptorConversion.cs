using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Readiness_eager_descriptor_refuses_authored_conversion_that_replaces_safe_registration()
    {
        string source = ExplicitReadinessEagerDescriptorFixture(backupName: false, prebinding: false);

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        InvocationExpressionSyntax descriptor = Assert.Single(unit.DescendantNodes().OfType<InvocationExpressionSyntax>(), static call =>
            call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "Scoped" } });

        unit = unit.ReplaceNode(descriptor, SyntaxFactory.ParseExpression(
            "(Microsoft.Extensions.DependencyInjection.ServiceDescriptor)(RetroDownfall.Arcanum.Infrastructure.Operations.ReplacingReadinessDescriptor)" + descriptor));

        CompilationUnitSyntax prebinding = CSharpSyntaxTree.ParseText(ExplicitReadinessScopedFixture("other-handler-prebinding")).GetCompilationUnitRoot();

        ClassDeclarationSyntax replacement = Assert.Single(prebinding.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "ExternalRecoveryHandler").WithIdentifier(SyntaxFactory.Identifier("ReplacingReadinessRecoveryHandler"));

        unit = unit.AddMembers(SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName("RetroDownfall.Arcanum.Infrastructure.Operations"))
            .AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("RetroDownfall.Arcanum.Core.Operations")))
            .AddMembers(replacement).NormalizeWhitespace());

        source = unit.ToFullString() + """
            namespace RetroDownfall.Arcanum.Infrastructure.Operations
            {
                internal sealed class ReplacingReadinessDescriptor
                {
                    public static explicit operator ReplacingReadinessDescriptor(Microsoft.Extensions.DependencyInjection.ServiceDescriptor original)
                        => new ReplacingReadinessDescriptor();

                    public static explicit operator Microsoft.Extensions.DependencyInjection.ServiceDescriptor(ReplacingReadinessDescriptor replacement)
                        => Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Scoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, ReplacingReadinessRecoveryHandler>();
                }
            }
            """;

        AssertExplicitReadinessLeasePremises(source, "PerUnitCoordinator");

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax mutation = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "TryAddEnumerable" });

        IMethodSymbol registered = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(mutation).Symbol);

        Assert.Equal("Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions", registered.ContainingType.ToDisplayString());

        Assert.All(registered.Locations, static location => Assert.True(location.IsInMetadata));

        IInvocationOperation registration = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(mutation));

        IArgumentOperation argument = Assert.Single(registration.Arguments, static value => value.Parameter?.Type.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.ServiceDescriptor");

        CastExpressionSyntax outerCast = Assert.IsType<CastExpressionSyntax>(argument.Value.Syntax);

        IConversionOperation selected = Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(outerCast));

        Assert.True(selected.Conversion.IsUserDefined);

        Assert.NotNull(selected.OperatorMethod);

        Assert.NotEmpty(selected.OperatorMethod!.DeclaringSyntaxReferences);

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Operations.ReplacingReadinessDescriptor", selected.OperatorMethod.ContainingType.ToDisplayString());

        Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceDescriptor", selected.Type!.ToDisplayString());

        ConversionOperatorDeclarationSyntax actualOperator = Assert.IsType<ConversionOperatorDeclarationSyntax>(Assert.Single(selected.OperatorMethod.DeclaringSyntaxReferences).GetSyntax());

        InvocationExpressionSyntax actualDescriptor = Assert.IsType<InvocationExpressionSyntax>(actualOperator.ExpressionBody!.Expression);

        IMethodSymbol actualFactory = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(actualDescriptor).Symbol);

        Assert.Equal("Scoped", actualFactory.Name);

        Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceDescriptor", actualFactory.ContainingType.ToDisplayString());

        Assert.All(actualFactory.Locations, static location => Assert.True(location.IsInMetadata));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Operations.ReplacingReadinessRecoveryHandler", actualFactory.TypeArguments[1].ToDisplayString());

        ClassDeclarationSyntax actualHandler = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "ReplacingReadinessRecoveryHandler");

        InvocationExpressionSyntax actualBinding = Assert.Single(actualHandler.Members.OfType<FieldDeclarationSyntax>().SelectMany(static field => field.DescendantNodes()).OfType<InvocationExpressionSyntax>(), static call =>
            call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Bind" });

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission", Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(actualBinding).Symbol).ContainingType.ToDisplayString());

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source,
            hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

        Assert.DoesNotContain(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitCoordinator"
            && site.Callee == "System.IO.File.Delete"
            && site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_RECOVERY_HANDLER_UNPROVEN"
            && diagnostic.Detail.StartsWith("RetroDownfall.Arcanum.Infrastructure.Operations.ReplacingReadinessRecoveryHandler;", StringComparison.Ordinal)
            && diagnostic.Detail.Contains("a recovery composition can mutate or compete with the final service registration set.", StringComparison.Ordinal));
    }
}
