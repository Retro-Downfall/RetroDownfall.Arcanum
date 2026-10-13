using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Returned_lease_cleanup_cannot_close_receiver_through_unknown_default_factory()
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        Assert.Equal(1, source.Split("gate = new ReadinessWorkGate();", StringSplitOptions.None).Length - 1);

        source = source.Replace("gate = new ReadinessWorkGate();", "gate = UnknownReadinessGateFactory.Current.CreateGate();", StringComparison.Ordinal);

        Assert.Equal(1, source.Split("public ValueTask DisposeAsync() => ValueTask.CompletedTask;", StringSplitOptions.None).Length - 1);

        source = source.Replace("public ValueTask DisposeAsync() => ValueTask.CompletedTask;", """
            public ValueTask DisposeAsync()
            {
                System.IO.File.Delete("unknown-factory-invented-lease-cleanup");

                return ValueTask.CompletedTask;
            }
            """, StringComparison.Ordinal);

        source += """
            namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
            {
                internal interface IReadinessGateFactory
                {
                    IGrimoireConnectionAdmissionGate CreateGate() => new ReadinessWorkGate();
                }

                internal sealed class InheritedReadinessGateFactory : IReadinessGateFactory { }

                internal sealed class OverridingReadinessGateFactory : IReadinessGateFactory
                {
                    public IGrimoireConnectionAdmissionGate CreateGate() => throw new InvalidOperationException("An actual override may refuse before returning any gate.");
                }

                internal static class UnknownReadinessGateFactory
                {
                    internal static IReadinessGateFactory Current = new InheritedReadinessGateFactory();
                }
            }
            """;

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var tree = Assert.Single(compilation.SyntaxTrees);

        var model = compilation.GetSemanticModel(tree);

        var factoryCall = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(), static call => call.ToString() == "UnknownReadinessGateFactory.Current.CreateGate()");

        var selected = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(factoryCall).Symbol);

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.IReadinessGateFactory", selected.ContainingType.ToDisplayString());

        Assert.False(selected.IsAbstract);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source,
            hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.DisposeAsync", StringComparison.Ordinal));

        Assert.DoesNotContain(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease"
            && site.Member == "DisposeAsync" && site.Callee == "System.IO.File.Delete"
            && site.Kind == HostedProducerSiteKind.FileSystemEffect
            && site.AuthorityOperationId == "Worker.StartAsync");
    }
}
