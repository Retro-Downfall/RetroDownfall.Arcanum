using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Readiness_eager_descriptor_activation_requires_every_materialized_constructor_to_preserve_the_holder(
        bool backupName, bool prebinding)
    {
        string source = ExplicitReadinessEagerDescriptorFixture(backupName, prebinding);

        AssertExplicitReadinessLeasePremises(source, "PerUnitCoordinator");

        AssertExplicitReadinessDescriptorSymbols(source, backupName, shadowed: false);

        AssertExplicitReadinessDiscovery(source, "PerUnitCoordinator", prebinding ? "other-handler-prebinding" : "owned-lease");
    }

    [Fact]
    public void Readiness_eager_descriptor_activation_refuses_a_compiler_selected_authored_framework_shadow()
    {
        string source = ExplicitReadinessEagerDescriptorFixture(backupName: false, prebinding: false);

        const string extension = "services.TryAddEnumerable(ServiceDescriptor.Scoped<";

        const string selected = "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, ServiceDescriptor.Scoped<";

        Assert.Equal(1, source.Split(extension, StringSplitOptions.None).Length - 1);

        source = source.Replace(extension, selected, StringComparison.Ordinal) + """

            namespace Microsoft.Extensions.DependencyInjection.Extensions
            {
                internal static class ServiceCollectionDescriptorExtensions
                {
                    internal static void TryAddEnumerable(
                        Microsoft.Extensions.DependencyInjection.IServiceCollection services,
                        Microsoft.Extensions.DependencyInjection.ServiceDescriptor descriptor)
                    {
                    }
                }
            }
            """;

        AssertExplicitReadinessLeasePremises(source, "PerUnitCoordinator");

        AssertExplicitReadinessDescriptorSymbols(source, backupName: false, shadowed: true);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source,
            hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is
            "HOSTED_RECOVERY_HANDLER_UNPROVEN" or "HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN"
            or "HOSTED_SITE_EFFECT_FRONTIER_MISSING" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.DoesNotContain(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitCoordinator"
            && site.Callee == "System.IO.File.Delete"
            && site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null);
    }

    private void AssertExplicitReadinessDescriptorSymbols(string source, bool backupName, bool shadowed)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax creation = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Scoped", TypeArguments.Length: 2 });

        IMethodSymbol factory = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(creation).Symbol);

        Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceDescriptor", factory.ContainingType.ToDisplayString());

        Assert.All(factory.Locations, static location => Assert.True(location.IsInMetadata));

        Assert.Equal("RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler", factory.TypeArguments[0].ToDisplayString());

        Assert.Equal(backupName ? "RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler"
            : "RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler", factory.TypeArguments[1].ToDisplayString());

        InvocationExpressionSyntax registration = Assert.Single(creation.Ancestors().OfType<InvocationExpressionSyntax>(), call =>
            model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "TryAddEnumerable" });

        IMethodSymbol method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(registration).Symbol);

        method = method.ReducedFrom ?? method;

        Assert.Equal("Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions", method.ContainingType.ToDisplayString());

        Assert.All(method.Locations, location => Assert.Equal(shadowed, location.IsInSource));

        output.WriteLine($"R_READINESS_EAGER_DESCRIPTOR_PREMISES=backup={backupName}|shadow={shadowed}|compilerSelectedDescriptorAndRegistration=passed");
    }

    private static string ExplicitReadinessEagerDescriptorFixture(bool backupName, bool prebinding)
    {
        string source = ExplicitReadinessScopedFixture(prebinding ? "other-handler-prebinding" : "inner-scope");

        const string original = "services.AddScoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler>();";

        Assert.Equal(1, source.Split(original, StringSplitOptions.None).Length - 1);

        string implementation = backupName ? "RetroDownfall.Arcanum.Infrastructure.Backup.BackupCreateRecoveryHandler"
            : "RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler";

        source = source.Replace(original,
            "services.TryAddEnumerable(ServiceDescriptor.Scoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, "
                + implementation + ">());", StringComparison.Ordinal);

        if (backupName)
        {
            CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

            ClassDeclarationSyntax other = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
                type.Identifier.ValueText == "ExternalRecoveryHandler");

            ClassDeclarationSyntax moved = other.WithIdentifier(SyntaxFactory.Identifier("BackupCreateRecoveryHandler"));

            unit = unit.RemoveNode(other, SyntaxRemoveOptions.KeepNoTrivia)!;

            unit = unit.AddMembers(SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName("RetroDownfall.Arcanum.Infrastructure.Backup"))
                .AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("RetroDownfall.Arcanum.Core.Operations")))
                .AddMembers(moved).NormalizeWhitespace());

            source = unit.ToFullString();
        }

        return "using Microsoft.Extensions.DependencyInjection.Extensions;\n" + source;
    }
}
