using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Readiness_joined_send_before_cleanup_retains_authority_and_later_send_refuses_it()
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        MethodDeclarationSyntax admitted = Assert.Single(unit.DescendantNodes().OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "RecoverAdmittedAsync" && method.Parent is ClassDeclarationSyntax { Identifier.ValueText: "PerUnitRecoveryHandler" });

        Assert.Equal(2, admitted.Body!.Statements.Count);

        source = unit.ReplaceNode(admitted, admitted.WithBody(admitted.Body.WithStatements(
            admitted.Body.Statements.Insert(1, SyntaxFactory.ParseStatement("await workAdmission.Lease.DisposeAsync();"))))).ToFullString();

        AssertExplicitReadinessLeasePremises(source, "PerUnitRecoveryHandler");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        MethodDeclarationSyntax actual = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "RecoverAdmittedAsync" && method.Parent is ClassDeclarationSyntax { Identifier.ValueText: "PerUnitRecoveryHandler" });

        InvocationExpressionSyntax[] calls = actual.Body!.Statements.Select(statement =>
            Assert.IsType<InvocationExpressionSyntax>(Assert.IsType<AwaitExpressionSyntax>(
                Assert.IsType<ExpressionStatementSyntax>(statement).Expression).Expression)).ToArray();

        Assert.Equal(3, calls.Length);
        Assert.Equal("SendFirstAsync", Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(calls[0]).Symbol).Name);
        Assert.Equal("SendSecondAsync", Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(calls[2]).Symbol).Name);
        Assert.True(calls[0].Span.End < calls[1].SpanStart);
        Assert.True(calls[1].Span.End < calls[2].SpanStart);

        IInvocationOperation disposal = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[1]));

        Assert.Equal("DisposeAsync", disposal.TargetMethod.Name);
        Assert.True(SymbolEqualityComparer.Default.Equals(disposal.TargetMethod.ReturnType,
            compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask")));

        IPropertyReferenceOperation leaseRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(disposal.Instance);

        Assert.Equal("Lease", leaseRead.Property.Name);
        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission", leaseRead.Property.ContainingType.ToDisplayString());

        IParameterReferenceOperation holder = Assert.IsAssignableFrom<IParameterReferenceOperation>(leaseRead.Instance);

        Assert.Equal("workAdmission", holder.Parameter.Name);
        Assert.Equal(MethodKind.Constructor, Assert.IsAssignableFrom<IMethodSymbol>(holder.Parameter.ContainingSymbol).MethodKind);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source,
            hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

        HostedProducerSite ownWork = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission"
            && site.Kind == HostedProducerSiteKind.EffectFrontier
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite[] sends = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitRecoveryHandler"
            && site.Kind == HostedProducerSiteKind.FileSystemEffect && site.Callee == "System.IO.File.Delete"
            && site.AuthorityOperationId == "Worker.StartAsync").ToArray();

        Assert.Equal(new[] { "SendFirstAsync", "SendSecondAsync" }, sends.Select(static send => send.Member).Distinct().Order().ToArray());

        Assert.All(sends.Where(static send => send.Member == "SendFirstAsync"), send =>
        {
            Assert.Equal(ownWork.CapsuleId, send.WorkFrontierCapsuleId);
            Assert.NotNull(send.EffectFrontierCapsuleId);
        });

        Assert.All(sends.Where(static send => send.Member == "SendSecondAsync"), static send =>
        {
            Assert.Null(send.WorkFrontierCapsuleId);
            Assert.Null(send.EffectFrontierCapsuleId);
        });

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN");

        output.WriteLine("R_READINESS_HISTORICAL_SEND=firstJoinedBeforeCleanup|secondAfterJoinedCleanup|compilerAndPublishedCapsules=passed");
    }
}
