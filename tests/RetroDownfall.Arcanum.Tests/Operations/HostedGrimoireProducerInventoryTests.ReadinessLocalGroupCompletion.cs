using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("awaited")]
    [InlineData("tail-return")]
    [InlineData("cached-return")]
    [InlineData("aggregate-return")]
    public void Readiness_returned_task_does_not_retain_a_forwarder_local_group(string shape)
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        ClassDeclarationSyntax original = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "PerUnitRecoveryHandler");

        ClassDeclarationSyntax handler = original;

        foreach (string ordinal in new[] { "First", "Second" })
        {
            MethodDeclarationSyntax send = Assert.Single(handler.Members.OfType<MethodDeclarationSyntax>(), method =>
                method.Identifier.ValueText == "Send" + ordinal + "Async");

            string completion = shape switch
            {
                "awaited" => $"await Send{ordinal}Async(lease);",
                "tail-return" => $"return Send{ordinal}Async(lease);",
                "cached-return" => $"Task pending = Send{ordinal}Async(lease); return pending;",
                _ => $"Task pending = Send{ordinal}Async(lease); return Task.WhenAll(pending);",
            };

            MethodDeclarationSyntax forwarder = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($$"""
                private {{(shape == "awaited" ? "async " : "")}}Task Forward{{ordinal}}Async()
                {
                    IGrimoireWorkLease lease = workAdmission.Lease;

                    if (!lease.TryBeginExternalEffectGroup(out IDisposable group)) {{(shape == "awaited" ? "return;" : "return Task.CompletedTask;")}}

                    using var owned = group;

                    {{completion}}
                }
                """)!;

            MethodDeclarationSyntax deferred = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($$"""
                private async Task Send{{ordinal}}Async(IGrimoireWorkLease forwardedLease)
                {
                    await Task.Yield();

                    System.IO.File.Delete("deferred-{{ordinal}}");
                }
                """)!;

            handler = handler.ReplaceNode(send, deferred).AddMembers(forwarder);
        }

        MethodDeclarationSyntax admitted = Assert.Single(handler.Members.OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "RecoverAdmittedAsync");

        handler = handler.ReplaceNode(admitted, admitted.WithBody((BlockSyntax)SyntaxFactory.ParseStatement(
            "{ await ForwardFirstAsync(); await ForwardSecondAsync(); }")));

        source = unit.ReplaceNode(original, handler).ToFullString();

        AssertExplicitReadinessLeasePremises(source, "PerUnitRecoveryHandler");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        foreach (string ordinal in new[] { "First", "Second" })
        {
            MethodDeclarationSyntax forwarder = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), method =>
                method.Identifier.ValueText == "Forward" + ordinal + "Async");

            Assert.Equal(shape == "awaited", model.GetDeclaredSymbol(forwarder)!.IsAsync);

            LocalDeclarationStatementSyntax retained = Assert.Single(forwarder.Body!.Statements.OfType<LocalDeclarationStatementSyntax>(), static statement =>
                statement.UsingKeyword.RawKind != 0);

            InvocationExpressionSyntax dispatch = Assert.Single(forwarder.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
                model.GetSymbolInfo(call).Symbol is IMethodSymbol method && method.Name == "Send" + ordinal + "Async");

            Assert.True(retained.Span.End < dispatch.SpanStart && retained.Parent!.Span.Contains(dispatch.Span));

            IInvocationOperation dispatched = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(dispatch));

            Assert.Equal("IGrimoireWorkLease", Assert.Single(dispatched.TargetMethod.Parameters).Type.Name);

            Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(dispatched.Arguments).Value);

            if (shape == "awaited") Assert.IsType<AwaitExpressionSyntax>(dispatch.Parent);
            else if (shape == "tail-return") Assert.IsType<ReturnStatementSyntax>(dispatch.Parent);
            else Assert.IsType<EqualsValueClauseSyntax>(dispatch.Parent);

            if (shape == "aggregate-return")
            {
                InvocationExpressionSyntax aggregate = Assert.IsType<InvocationExpressionSyntax>(forwarder.Body.Statements.OfType<ReturnStatementSyntax>().Last().Expression);

                Assert.Equal("System.Threading.Tasks.Task.WhenAll", Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(aggregate).Symbol)
                    .ContainingType.ToDisplayString() + ".WhenAll");
            }

            MethodDeclarationSyntax send = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), method =>
                method.Identifier.ValueText == "Send" + ordinal + "Async");

            AwaitExpressionSyntax deferred = Assert.Single(send.DescendantNodes().OfType<AwaitExpressionSyntax>());

            InvocationExpressionSyntax yieldCall = Assert.IsType<InvocationExpressionSyntax>(deferred.Expression);

            IMethodSymbol yielding = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(yieldCall).Symbol);

            Assert.Equal("Yield", yielding.Name);

            Assert.Equal("System.Threading.Tasks.Task", yielding.ContainingType.ToDisplayString());

            InvocationExpressionSyntax effect = Assert.Single(send.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
                model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Delete" });

            Assert.True(deferred.Span.End < effect.SpanStart);
        }

        output.WriteLine($"R_READINESS_LOCAL_GROUP_PREMISES={shape}|realDeferredHelperAndLocalUsingGroup=passed");

        if (shape == "awaited")
        {
            AssertExplicitReadinessDiscovery(source, "PerUnitRecoveryHandler", "owned-lease");
        }
        else
        {
            HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source,
                hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

            HostedProducerSite[] sends = result.Items.Where(static site => site.RootType == "Worker"
                && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitRecoveryHandler"
                && site.Callee == "System.IO.File.Delete").ToArray();

            Assert.Equal(new[] { "SendFirstAsync", "SendSecondAsync" }, sends.Select(static send => send.Member).Distinct().Order().ToArray());

            Assert.All(sends, static send => Assert.Null(send.EffectFrontierCapsuleId));
        }
    }
}
