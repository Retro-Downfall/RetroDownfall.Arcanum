using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Readiness_argument_cleanup_after_a_joined_send_refuses_the_next_send()
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        ClassDeclarationSyntax handler = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "PerUnitRecoveryHandler");

        MethodDeclarationSyntax second = Assert.Single(handler.Members.OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "SendSecondAsync");

        MethodDeclarationSyntax admitted = Assert.Single(handler.Members.OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "RecoverAdmittedAsync");

        InvocationExpressionSyntax secondCall = Assert.Single(admitted.DescendantNodes().OfType<InvocationExpressionSyntax>(), static call =>
            call.Expression is IdentifierNameSyntax { Identifier.ValueText: "SendSecondAsync" });

        MethodDeclarationSyntax changedAdmitted = admitted.ReplaceNode(secondCall, secondCall.WithArgumentList(
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(SyntaxFactory.ParseExpression("CloseLeaseForArgument()"))))));

        ClassDeclarationSyntax changed = handler.ReplaceNode(admitted, changedAdmitted);

        changed = changed.ReplaceNode(Assert.Single(changed.Members.OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "SendSecondAsync"), second.WithParameterList(
                SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("cleanupResult")).WithType(SyntaxFactory.ParseTypeName("bool").WithTrailingTrivia(SyntaxFactory.Space))))))
            .AddMembers(SyntaxFactory.ParseMemberDeclaration("private bool CloseLeaseForArgument() { workAdmission.Lease.Dispose(); return true; }")!);

        source = unit.ReplaceNode(handler, changed).ToFullString();

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

        Assert.Equal(2, calls.Length);
        Assert.Equal("SendFirstAsync", Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(calls[0]).Symbol).Name);
        Assert.Equal("SendSecondAsync", Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(calls[1]).Symbol).Name);
        Assert.True(calls[0].Span.End < calls[1].SpanStart);

        IInvocationOperation invocation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[1]));

        IArgumentOperation argument = Assert.Single(invocation.Arguments);

        Assert.Equal(ArgumentKind.Explicit, argument.ArgumentKind);
        Assert.Equal(SpecialType.System_Boolean, argument.Parameter!.Type.SpecialType);
        Assert.Equal(RefKind.None, argument.Parameter.RefKind);
        Assert.True(argument.InConversion.IsIdentity);

        IInvocationOperation cleanup = Assert.IsAssignableFrom<IInvocationOperation>(argument.Value);

        Assert.Equal("CloseLeaseForArgument", cleanup.TargetMethod.Name);
        Assert.Equal(SpecialType.System_Boolean, cleanup.TargetMethod.ReturnType.SpecialType);
        Assert.IsAssignableFrom<IInstanceReferenceOperation>(cleanup.Instance);
        Assert.True(calls[1].SpanStart < cleanup.Syntax.SpanStart);
        Assert.True(cleanup.Syntax.Span.End < calls[1].Span.End);

        MethodDeclarationSyntax close = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(cleanup.TargetMethod.DeclaringSyntaxReferences).GetSyntax());

        InvocationExpressionSyntax disposalSyntax = Assert.Single(close.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation disposal = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(disposalSyntax));

        Assert.Equal("Dispose", disposal.TargetMethod.Name);
        Assert.Equal(SpecialType.System_Void, disposal.TargetMethod.ReturnType.SpecialType);

        IPropertyReferenceOperation leaseRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(disposal.Instance);

        Assert.Equal("Lease", leaseRead.Property.Name);
        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission", leaseRead.Property.ContainingType.ToDisplayString());
        Assert.Equal("workAdmission", Assert.IsAssignableFrom<IParameterReferenceOperation>(leaseRead.Instance).Parameter.Name);

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

        output.WriteLine("R_READINESS_ARGUMENT_CLEANUP=firstJoinedBeforeArgumentCleanup|secondAfterActualArgumentCleanup|compilerAndPublishedCapsules=passed");
    }
}
