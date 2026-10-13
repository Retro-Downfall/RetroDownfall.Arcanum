using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Returned_lease_cleanup_requires_closed_receiver_for_authored_default_gate(bool unknownReceiver, bool nestedFactory)
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        InterfaceDeclarationSyntax contract = Assert.Single(unit.DescendantNodes().OfType<InterfaceDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "IGrimoireConnectionAdmissionGate");

        MethodDeclarationSyntax slot = Assert.Single(contract.Members.OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "TryAcquireWorkLease");

        MethodDeclarationSyntax inherited = slot.WithSemicolonToken(default).WithBody((BlockSyntax)SyntaxFactory.ParseStatement("""
            {
                lease = new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease();

                return true;
            }
            """));

        unit = unit.ReplaceNode(contract, contract.ReplaceNode(slot, inherited));

        ClassDeclarationSyntax gate = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "ReadinessWorkGate");

        unit = unit.ReplaceNode(gate, gate.WithMembers(SyntaxFactory.List(gate.Members.Where(static member => member is not
            MethodDeclarationSyntax { Identifier.ValueText: "TryAcquireWorkLease" }))));

        source = unit.ToFullString();

        Assert.Equal(1, source.Split("public ValueTask DisposeAsync() => ValueTask.CompletedTask;", StringSplitOptions.None).Length - 1);

        source = source.Replace("public ValueTask DisposeAsync() => ValueTask.CompletedTask;", """
            public ValueTask DisposeAsync()
            {
                System.IO.File.Delete("actual-inherited-default-lease-cleanup");

                return ValueTask.CompletedTask;
            }
            """, StringComparison.Ordinal);

        if (unknownReceiver)
        {
            Assert.Equal(1, source.Split("gate = new ReadinessWorkGate();", StringSplitOptions.None).Length - 1);

            source = source.Replace("gate = new ReadinessWorkGate();", nestedFactory
                ? "gate = UnprovenDefaultReadinessGate.Factory.Create();"
                : "gate = UnprovenDefaultReadinessGate.Current;", StringComparison.Ordinal);

            source += """
                namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
                {
                    internal static class UnprovenDefaultReadinessGate
                    {
                        internal static IGrimoireConnectionAdmissionGate Current = new ReadinessWorkGate();

                        internal static IReadinessGateFactory Factory = new DefaultReadinessGateFactory();
                    }

                    internal interface IReadinessGateFactory
                    {
                        IGrimoireConnectionAdmissionGate Create() => new ReadinessWorkGate();
                    }

                    internal sealed class DefaultReadinessGateFactory : IReadinessGateFactory { }

                    internal sealed class OverridingReadinessGateFactory : IReadinessGateFactory
                    {
                        public IGrimoireConnectionAdmissionGate Create() => new OverridingReadinessGate();
                    }

                    internal sealed class OverridingReadinessGate : IGrimoireConnectionAdmissionGate
                    {
                        public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
                            => throw new InvalidOperationException("This receiver overrides the authored default.");
                    }
                }
                """;
        }

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        InterfaceDeclarationSyntax exactContract = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InterfaceDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "IGrimoireConnectionAdmissionGate");

        IMethodSymbol exactSlot = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(Assert.Single(exactContract.Members.OfType<MethodDeclarationSyntax>())));

        Assert.False(exactSlot.IsAbstract);

        ClassDeclarationSyntax concreteGate = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "ReadinessWorkGate");

        INamedTypeSymbol concrete = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(concreteGate));

        Assert.True(SymbolEqualityComparer.Default.Equals(concrete.FindImplementationForInterfaceMember(exactSlot), exactSlot));

        if (unknownReceiver)
        {
            ClassDeclarationSyntax overrideGate = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
                type.Identifier.ValueText == "OverridingReadinessGate");

            INamedTypeSymbol overriding = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(overrideGate));

            IMethodSymbol effective = Assert.IsAssignableFrom<IMethodSymbol>(overriding.FindImplementationForInterfaceMember(exactSlot));

            Assert.True(SymbolEqualityComparer.Default.Equals(effective.ContainingType, overriding));

            Assert.NotEmpty(effective.DeclaringSyntaxReferences);
        }

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source,
            hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

        bool unresolved = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.DisposeAsync", StringComparison.Ordinal));

        HostedProducerSite[] cleanup = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease"
            && site.Member == "DisposeAsync" && site.Callee == "System.IO.File.Delete"
            && site.Kind == HostedProducerSiteKind.FileSystemEffect
            && site.AuthorityOperationId == "Worker.StartAsync").ToArray();

        Assert.Equal(unknownReceiver, unresolved);

        if (unknownReceiver) Assert.Empty(cleanup);
        else Assert.NotEmpty(cleanup);
    }
}
