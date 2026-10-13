using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("direct")]
    [InlineData("forwarded-once")]
    [InlineData("conditional")]
    [InlineData("loop")]
    [InlineData("forwarded-twice")]
    [InlineData("transient")]
    [InlineData("singleton")]
    [InlineData("holder-singleton")]
    public void Readiness_prior_holder_consumer_requires_scoped_registration_on_every_execution_path(string shape)
    {
        string source = ExplicitReadinessScopedFixture("inner-scope");

        const string holder = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

        const string prior = "RetroDownfall.Arcanum.Infrastructure.Operations.ExecutionPriorCarrier";

        string method = shape switch
        {
            "transient" => "AddTransient",
            "singleton" => "AddSingleton",
            _ => "AddScoped",
        };

        string descriptor = $"services.{method}<{prior}>();";

        string registration = shape switch
        {
            "forwarded-once" => $"{prior}.Register(services);",
            "forwarded-twice" => $"{prior}.Register(services); {prior}.Register(services);",
            "conditional" => $"if (System.DateTime.UtcNow.Ticks > 0) {{ {descriptor} }}",
            "loop" => $"for (int priorIndex = 0; priorIndex < 2; priorIndex++) {{ {descriptor} }}",
            _ => descriptor,
        };

        void ReplaceOne(string before, string after)
        {
            Assert.Equal(1, source.Split(before, StringSplitOptions.None).Length - 1);

            source = source.Replace(before, after, StringComparison.Ordinal);
        }

        const string existing = "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitCoordinator>();";

        ReplaceOne(existing, existing + " " + registration);

        if (shape == "holder-singleton")
        {
            ReplaceOne($"services.AddScoped<{holder}>();", $"services.AddSingleton<{holder}>();");
        }

        const string materialize = "Dictionary<string, ILongRunningOperationRecoveryHandler> scopedHandlers = operationScope.ServiceProvider";

        ReplaceOne(materialize, $"_ = operationScope.ServiceProvider.GetRequiredService<{prior}>(); " + materialize);

        string forwarder = shape.StartsWith("forwarded-", StringComparison.Ordinal)
            ? $"internal static void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services) {{ {descriptor} }}"
            : "";

        source += $$"""
            namespace RetroDownfall.Arcanum.Infrastructure.Operations
            {
                internal sealed class ExecutionPriorCarrier({{holder}} workAdmission)
                {
                    {{forwarder}}
                }
            }
            """;

        Compilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        InvocationExpressionSyntax descriptorCall = Assert.Single(compilation.SyntaxTrees.SelectMany(static tree =>
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()), call =>
                compilation.GetSemanticModel(call.SyntaxTree).GetSymbolInfo(call).Symbol is IMethodSymbol symbol
                && symbol.Name == method && symbol.TypeArguments is [ITypeSymbol contract]
                && contract.ToDisplayString() == prior);

        IMethodSymbol descriptorMethod = Assert.IsAssignableFrom<IMethodSymbol>(compilation.GetSemanticModel(descriptorCall.SyntaxTree)
            .GetSymbolInfo(descriptorCall).Symbol);

        Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions", descriptorMethod.ContainingType.ToDisplayString());

        Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", descriptorMethod.ContainingAssembly.Identity.Name);

        Assert.Empty(descriptorMethod.DeclaringSyntaxReferences);

        if (shape.StartsWith("forwarded-", StringComparison.Ordinal))
        {
            IMethodSymbol[] forwardedTargets = compilation.SyntaxTrees.SelectMany(static tree =>
                tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()).Select(call =>
                    compilation.GetSemanticModel(call.SyntaxTree).GetSymbolInfo(call).Symbol).OfType<IMethodSymbol>()
                .Where(symbol => symbol.Name == "Register" && symbol.ContainingType.ToDisplayString() == prior).ToArray();

            Assert.Equal(shape == "forwarded-once" ? 1 : 2, forwardedTargets.Length);

            Assert.All(forwardedTargets, target => Assert.True(SymbolEqualityComparer.Default.Equals(target, forwardedTargets[0])));

            Assert.Single(forwardedTargets[0].DeclaringSyntaxReferences);
        }

        AssertExplicitReadinessDiscovery(source, "PerUnitCoordinator", shape is "direct" or "forwarded-once" or "singleton" ? "owned-lease" : shape);
    }

    [Fact]
    public void Readiness_singleton_constructor_uses_root_holder_and_scope_validation_rejects_captive_dependency()
    {
        ServiceCollection services = new();

        services.AddScoped<ReadinessSingletonIdentityHolder>();

        services.AddSingleton<ReadinessSingletonIdentityCarrier>();

        using (ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false }))
        {
            using IServiceScope operationScope = provider.CreateScope();

            ReadinessSingletonIdentityHolder scopedHolder = operationScope.ServiceProvider.GetRequiredService<ReadinessSingletonIdentityHolder>();

            ReadinessSingletonIdentityCarrier carrier = operationScope.ServiceProvider.GetRequiredService<ReadinessSingletonIdentityCarrier>();

            ReadinessSingletonIdentityHolder rootHolder = provider.GetRequiredService<ReadinessSingletonIdentityHolder>();

            Assert.Same(rootHolder, carrier.Holder);

            Assert.NotSame(scopedHolder, carrier.Holder);

            Assert.Same(carrier, provider.GetRequiredService<ReadinessSingletonIdentityCarrier>());
        }

        using ServiceProvider validated = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false,
        });

        using IServiceScope validatedOperationScope = validated.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            validatedOperationScope.ServiceProvider.GetRequiredService<ReadinessSingletonIdentityCarrier>());
    }

    private sealed class ReadinessSingletonIdentityHolder
    {
        public ReadinessSingletonIdentityHolder() { }
    }

    private sealed class ReadinessSingletonIdentityCarrier
    {
        public ReadinessSingletonIdentityCarrier(ReadinessSingletonIdentityHolder holder)
        {
            Holder = holder;
        }

        public ReadinessSingletonIdentityHolder Holder { get; }
    }
}
