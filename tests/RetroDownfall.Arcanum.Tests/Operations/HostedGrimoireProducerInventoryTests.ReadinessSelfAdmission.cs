using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("owned-lease")]
    [InlineData("missing-first-group")]
    [InlineData("early-lease-disposal")]
    public void Readiness_per_unit_recovery_proves_its_own_explicit_lease_and_each_physical_group_without_an_incoming_shell_lease(string shape)
    {
        string source = ExplicitReadinessSelfAdmissionFixture(shape);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        MethodDeclarationSyntax start = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "StartAsync" && method.Parent is ClassDeclarationSyntax { Identifier.ValueText: "Worker" });

        // The readiness caller has no shell work acquisition or holder binding to inherit.
        Assert.DoesNotContain(start.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "TryAcquireWorkLease" or "Bind" });

        MethodDeclarationSyntax recover = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "RecoverAsync" && method.Parent is ClassDeclarationSyntax { Identifier.ValueText: "PerUnitRecoveryHandler" });

        Assert.Single(recover.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(), static declaration =>
            declaration.UsingKeyword.RawKind != 0 && declaration.AwaitKeyword.RawKind != 0);

        MethodDeclarationSyntax acquire = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "Acquire" && method.Parent is ClassDeclarationSyntax { Identifier.ValueText: "LongRestWorkAdmission" });

        InvocationExpressionSyntax acquisition = Assert.Single(acquire.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "TryAcquireWorkLease" });

        Assert.Equal("LongRestSweep", Assert.IsAssignableFrom<IFieldSymbol>(model.GetSymbolInfo(acquisition.ArgumentList.Arguments[0].Expression).Symbol).Name);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source,
            hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

        HostedProducerSite[] sends = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitRecoveryHandler"
            && site.Callee == "System.IO.File.Delete").ToArray();

        Assert.Equal(2, sends.Length);

        if (shape == "owned-lease")
        {
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code.StartsWith("HOSTED_RECOVERY_", StringComparison.Ordinal));

            HostedProducerSite ownWork = Assert.Single(result.Items, static site => site.RootType == "Worker"
                && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission"
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

            Assert.All(sends, send =>
            {
                Assert.Equal(ownWork.CapsuleId, send.WorkFrontierCapsuleId);

                Assert.NotNull(send.EffectFrontierCapsuleId);
            });

            Assert.NotEqual(sends[0].EffectFrontierCapsuleId, sends[1].EffectFrontierCapsuleId);
        }
        else if (shape == "missing-first-group")
        {
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is
                "HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

            Assert.Null(Assert.Single(sends, static send => send.Member == "SendFirstAsync").EffectFrontierCapsuleId);

            Assert.NotNull(Assert.Single(sends, static send => send.Member == "SendSecondAsync").EffectFrontierCapsuleId);
        }
        else
        {
            Assert.Equal("early-lease-disposal", shape);

            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is
                "HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

            Assert.DoesNotContain(sends, static send => send.WorkFrontierCapsuleId is not null && send.EffectFrontierCapsuleId is not null);
        }
    }

    private static string ReadinessSelfAdmissionFixture(string shape)
    {
        string source = PerUnitRecoveryMatrixFixture(shape == "missing-first-group" ? "missing-group" : "same-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        MethodDeclarationSyntax start = Assert.Single(unit.DescendantNodes().OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "StartAsync" && method.Parent is ClassDeclarationSyntax { Identifier.ValueText: "Worker" });

        BlockSyntax readiness = (BlockSyntax)SyntaxFactory.ParseStatement("""
            {
                var admission = new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission();
                var operation = new RetroDownfall.Arcanum.Core.Operations.LongRunningOperation(
                    RetroDownfall.Arcanum.Core.Operations.LongRunningOperationKinds.PerUnit, 0);
                await new RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler(workAdmission: admission)
                    .ReconcileAsync(operation);
            }
            """);

        source = unit.ReplaceNode(start, start.WithBody(readiness)).ToFullString();

        void ReplaceOne(string before, string after)
        {
            Assert.Equal(1, source.Split(before, StringSplitOptions.None).Length - 1);

            source = source.Replace(before, after, StringComparison.Ordinal);
        }

        ReplaceOne("public enum GrimoireWorkKind { WorkspaceIndexing = 4 }", "public enum GrimoireWorkKind { WorkspaceIndexing = 4, LongRestSweep = 17 }");

        ReplaceOne("private IGrimoireWorkLease? current;", """
            private IGrimoireWorkLease? current;

            private readonly IGrimoireConnectionAdmissionGate gate = new GrimoireConnectionAdmissionGate();

            internal IAsyncDisposable Enter(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();

                if (current is not null) return new Entry(null, null);

                if (!gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease lease))
                {
                    throw new OperationCanceledException(ct);
                }

                if (lease is null) throw new InvalidOperationException();

                return new Entry(Bind(lease), lease);
            }

            private sealed class Entry(IDisposable? binding, IGrimoireWorkLease? lease) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    binding?.Dispose();

                    return lease?.DisposeAsync() ?? ValueTask.CompletedTask;
                }
            }
            """);

        string disposal = shape == "early-entry-disposal" ? "await ownedEntry.DisposeAsync();" : "";

        ReplaceOne("await SendFirstAsync();", "await using IAsyncDisposable ownedEntry = workAdmission.Enter(cancellationToken); " + disposal + " await SendFirstAsync();");

        return source + """
            namespace RetroDownfall.Arcanum.Infrastructure.Data
            {
                // The one gate the fixture's source declares is the reviewed gate (see the Single Gate Census).
                internal sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate
                {
                    public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease lease)
                    {
                        lease = new LongRest.ReadinessWorkLease();

                        return true;
                    }
                }
            }

            namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest
            {
                internal sealed class ReadinessWorkLease : IGrimoireWorkLease
                {
                    public bool TryBeginExternalEffectGroup(out IDisposable group)
                    {
                        group = new ReadinessGroup();

                        return true;
                    }

                    public void Dispose() { }

                    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

                    private sealed class ReadinessGroup : IDisposable
                    {
                        public void Dispose() { }
                    }
                }
            }
            """;
    }
}
