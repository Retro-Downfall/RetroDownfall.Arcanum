using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("preserved")]
    [InlineData("primary-cleanup")]
    [InlineData("readonly-field-cleanup")]
    [InlineData("local-alias-cleanup")]
    [InlineData("property-cleanup")]
    [InlineData("method-group-cleanup")]
    [InlineData("generic-method-group-cleanup")]
    [InlineData("carrier-forwarding-cleanup")]
    [InlineData("constructor-cleanup")]
    [InlineData("aggregate-cleanup")]
    [InlineData("local-callback-cleanup")]
    [InlineData("property-callback-cleanup")]
    public void Readiness_owned_lease_lifetime_follows_prior_joined_helper_without_a_lease_argument(string shape)
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        ClassDeclarationSyntax handler = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "PerUnitRecoveryHandler");

        MethodDeclarationSyntax admitted = Assert.Single(handler.Members.OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "RecoverAdmittedAsync");

        string priorBody = shape switch
        {
            "primary-cleanup" => "private async Task PriorHolderUseAsync() { await workAdmission.Lease.DisposeAsync(); }",
            "readonly-field-cleanup" => "private async Task PriorHolderUseAsync() { await capturedHolder.Lease.DisposeAsync(); }",
            "local-alias-cleanup" => "private async Task PriorHolderUseAsync() { var holderAlias = workAdmission; await holderAlias.Lease.DisposeAsync(); }",
            "property-cleanup" => "private async Task PriorHolderUseAsync() { if (CloseLease) await Task.CompletedTask; }",
            "method-group-cleanup" => "private Task PriorHolderUseAsync() { Run(CloseLease); return Task.CompletedTask; }",
            "generic-method-group-cleanup" => "private Task PriorHolderUseAsync() { Run(CloseLease<int>); return Task.CompletedTask; }",
            "carrier-forwarding-cleanup" => "private Task PriorHolderUseAsync() { Forward(this); return Task.CompletedTask; }",
            "constructor-cleanup" => "private Task PriorHolderUseAsync() { _ = new Closer(workAdmission); return Task.CompletedTask; }",
            "aggregate-cleanup" => "private Task PriorHolderUseAsync() { var holders = new[] { workAdmission }; holders[0].Lease.Dispose(); return Task.CompletedTask; }",
            "local-callback-cleanup" => "private Task PriorHolderUseAsync() { Action cleanup = () => workAdmission.Lease.Dispose(); cleanup(); return Task.CompletedTask; }",
            "property-callback-cleanup" => "private Task PriorHolderUseAsync() { Func<bool> cleanup = () => CloseLease; _ = cleanup(); return Task.CompletedTask; }",
            _ => "private Task PriorHolderUseAsync() => Task.CompletedTask;",
        };

        MethodDeclarationSyntax prior = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(priorBody)!;

        if (shape == "readonly-field-cleanup")
        {
            handler = handler.AddMembers(SyntaxFactory.ParseMemberDeclaration(
                "private readonly RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission capturedHolder = workAdmission;")!);
        }
        else if (shape is "property-cleanup" or "property-callback-cleanup")
        {
            handler = handler.AddMembers(SyntaxFactory.ParseMemberDeclaration(
                "private bool CloseLease { get { workAdmission.Lease.Dispose(); return true; } }")!);
        }

        if (shape is "method-group-cleanup" or "generic-method-group-cleanup" or "carrier-forwarding-cleanup")
        {
            handler = handler.AddMembers(SyntaxFactory.ParseMemberDeclaration(
                shape == "generic-method-group-cleanup"
                    ? "private void CloseLease<T>() => workAdmission.Lease.Dispose();"
                    : "private void CloseLease() => workAdmission.Lease.Dispose();")!);

            handler = handler.AddMembers(SyntaxFactory.ParseMemberDeclaration(
                shape == "carrier-forwarding-cleanup"
                    ? "private void Forward(PerUnitRecoveryHandler value) => value.CloseLease();"
                    : "private void Run(Action callback) => callback();")!);
        }
        else if (shape == "constructor-cleanup")
        {
            handler = handler.AddMembers(SyntaxFactory.ParseMemberDeclaration(
                "private sealed class Closer { public Closer(RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission value) { value.Lease.Dispose(); } }")!);
        }

        admitted = Assert.Single(handler.Members.OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "RecoverAdmittedAsync");

        handler = handler.ReplaceNode(admitted, admitted.WithBody(admitted.Body!.WithStatements(
            admitted.Body.Statements.Insert(0, SyntaxFactory.ParseStatement("await PriorHolderUseAsync();")))))
            .AddMembers(prior);

        source = unit.ReplaceNode(Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "PerUnitRecoveryHandler"), handler).ToFullString();

        AssertExplicitReadinessLeasePremises(source, "PerUnitRecoveryHandler");

        CSharpCompilation compilation = Compile(source);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax call = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            candidate => model.GetSymbolInfo(candidate).Symbol is IMethodSymbol { Name: "PriorHolderUseAsync" });

        Assert.Empty(call.ArgumentList.Arguments);

        Assert.IsType<AwaitExpressionSyntax>(call.Parent);

        AssertExplicitReadinessDiscovery(source, "PerUnitRecoveryHandler", shape == "preserved" ? "owned-lease" : "early-holder-disposal");
    }
}
