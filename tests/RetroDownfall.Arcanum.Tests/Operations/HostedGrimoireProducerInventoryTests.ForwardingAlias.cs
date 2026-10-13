using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("direct", true)]
    [InlineData("alias", true)]
    [InlineData("conditional-alias", false)]
    [InlineData("repeated-alias", false)]
    [InlineData("duplicate-alias", false)]
    [InlineData("repeated-helper-alias", false)]
    [InlineData("wrong-provider-alias", false)]
    public void ScopedForwardingAliasOutsideNominalClosureRetainsOnlyExactSourceAuthority(string shape, bool admitted)
    {
        const string contract = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.ILongRestSweepCoordinator";

        const string concrete = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator";

        const string direct = "services.AddScoped<" + contract + ", " + concrete + ">();";

        const string alias = "services.AddScoped<" + contract + ">(static sp => sp.GetRequiredService<" + concrete + ">());";

        string source = OrdinaryScopedHolderFixture("same-scoped-lease");

        string registration = shape switch
        {
            "direct" => direct,
            "alias" => alias,
            "conditional-alias" => "if (System.DateTime.UtcNow.Ticks > 0) { " + alias + " }",
            "repeated-alias" => "for (int aliasIndex = 0; aliasIndex < 2; aliasIndex++) { " + alias + " }",
            "duplicate-alias" => alias + " " + alias,
            "repeated-helper-alias" => "AliasRegistration.Add(services); AliasRegistration.Add(services);",
            "wrong-provider-alias" => "services.AddScoped<" + contract + ">(static sp => new ServiceCollection().BuildServiceProvider().GetRequiredService<" + concrete + ">());",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        if (shape != "direct")
            source = source.ReplaceSdkFixtureText(direct, "services.AddScoped<" + concrete + ">(); " + registration, StringComparison.Ordinal);

        if (shape == "repeated-helper-alias")
            source += " internal static class AliasRegistration { internal static void Add(IServiceCollection services) { " + alias + " } }";

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        SyntaxNode syntax = tree.GetRoot();

        ClassDeclarationSyntax coordinator = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "LongRestSweepCoordinator");

        // No handler constructor consumes the nominal interface. The ordinary census
        // discovers the concrete holder consumer separately from the recovery path.
        Assert.DoesNotContain(syntax.DescendantNodes().OfType<ParameterSyntax>(),
            parameter => model.GetDeclaredSymbol(parameter) is IParameterSymbol declared && declared.Type.ToDisplayString() == contract);

        Assert.Contains(coordinator.ParameterList!.Parameters,
            parameter => model.GetTypeInfo(parameter.Type!).Type?.ToDisplayString()
                == "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission");

        InvocationExpressionSyntax[] registrations = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "AddScoped" } method
                && method.TypeArguments.FirstOrDefault()?.ToDisplayString() == contract).ToArray();

        Assert.Equal(shape == "duplicate-alias" ? 2 : 1, registrations.Length);

        if (shape != "direct")
        {
            Assert.All(registrations, call =>
            {
                var factory = Assert.IsAssignableFrom<AnonymousFunctionExpressionSyntax>(Assert.Single(call.ArgumentList.Arguments).Expression);

                InvocationExpressionSyntax forwarded = Assert.Single(factory.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                    invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "GetRequiredService" });

                IMethodSymbol resolver = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(forwarded).Symbol);

                Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions", resolver.ContainingType.ToDisplayString());

                Assert.Equal(concrete, Assert.Single(resolver.TypeArguments).ToDisplayString());

                var access = Assert.IsType<MemberAccessExpressionSyntax>(forwarded.Expression);

                IParameterSymbol provider = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(
                    Assert.IsType<SimpleLambdaExpressionSyntax>(factory).Parameter));

                Assert.Equal(shape != "wrong-provider-alias",
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, provider));
            });

            Assert.Equal(shape == "conditional-alias", registrations.Any(static call => call.Ancestors().OfType<IfStatementSyntax>().Any()));

            Assert.Equal(shape == "repeated-alias", registrations.Any(static call => call.Ancestors().OfType<ForStatementSyntax>().Any()));

            if (shape == "repeated-helper-alias")
            {
                InvocationExpressionSyntax[] incoming = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(call => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Add", ContainingType.Name: "AliasRegistration" }).ToArray();

                Assert.Equal(2, incoming.Length);

                Assert.True(SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(incoming[0]).Symbol, model.GetSymbolInfo(incoming[1]).Symbol));
            }
        }

        MethodDeclarationSyntax worker = Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "RunOneAsync");

        InvocationExpressionSyntax dispatch = Assert.Single(worker.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "ResumeAsync" });

        Assert.Contains(dispatch.Ancestors(), static node => node is AwaitExpressionSyntax);

        Assert.Equal(contract, Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(dispatch).Symbol).ContainingType.ToDisplayString());

        output.WriteLine("FORWARDING_ALIAS_SOURCE_PREREQUISITES shape=" + shape + " compilerRegistrationDispatch=passed");

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

        Assert.Empty(hosted.Diagnostics);

        Assert.Contains("Worker", hosted.Items);

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], hosted, [new("Worker", [root])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
            or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

        HostedProducerSite[] sends = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == concrete && site.Callee == "System.IO.File.Delete").ToArray();

        Assert.Equal(2, sends.Length);

        HostedProducerSite acquired = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        output.WriteLine("FORWARDING_ALIAS_DISCOVERY_PREREQUISITES shape=" + shape + " bothSendsAndAcquisition=passed");

        foreach (HostedProducerSite send in sends)
        {
            if (admitted)
            {
                Assert.Equal(acquired.CapsuleId, send.WorkFrontierCapsuleId);

                HostedProducerSite group = Assert.Single(result.Items, site => site.RootType == "Worker"
                    && site.EnclosingType == concrete && site.Member == send.Member
                    && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

                Assert.Equal(group.CapsuleId, send.EffectFrontierCapsuleId);
            }
            else
            {
                Assert.Null(send.WorkFrontierCapsuleId);

                Assert.Null(send.EffectFrontierCapsuleId);

                foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
                    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == send.Callee
                        && diagnostic.Identity.EndsWith(send.OperationId[send.OperationId.LastIndexOf("/site@", StringComparison.Ordinal)..], StringComparison.Ordinal));
            }
        }

        if (admitted)
        {
            Assert.NotEqual(sends[0].EffectFrontierCapsuleId, sends[1].EffectFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);
        }
    }
}
