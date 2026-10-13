using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedLocalReturnRetainsItsActualSourceInsteadOfAnOwnArgument(bool capturesOuter)
    {
        bool outerWitness = true;

        bool sharedWitness = false;

        bool Own(bool inner) => inner;

        bool Captured(bool inner) => outerWitness;

        Assert.False(Own(sharedWitness));

        Assert.True(Captured(sharedWitness));

        string returned = capturesOuter ? "outer" : "inner";

        string helper = "internal static class CapturedLocalIdentityTarget { "
            + "internal static bool Observed; "
            + "internal static void Run(bool outer) { bool shared = false; bool Forward(bool inner) => " + returned + "; "
            + "Observe(shared); Observe(Forward(shared)); } "
            + "internal static void Observe(bool value) { if (value) System.IO.File.Delete(\"captured-local-effect\"); "
            + "Read(ref value); System.IO.File.Exists(\"captured-local-anchor\"); } "
            + "private static void Read(ref bool value) { Observed = value; } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source("CapturedLocalIdentityTarget.Run(true); await Task.CompletedTask;", helper),
            "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax target = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "CapturedLocalIdentityTarget");

        MethodDeclarationSyntax run = Assert.Single(target.Members.OfType<MethodDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Run");

        IMethodSymbol runMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(run));

        IParameterSymbol outer = Assert.Single(runMethod.Parameters);

        LocalFunctionStatementSyntax forward = Assert.Single(run.DescendantNodes().OfType<LocalFunctionStatementSyntax>());

        IMethodSymbol forwardMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(forward));

        IParameterSymbol inner = Assert.Single(forwardMethod.Parameters);

        Assert.Equal(MethodKind.LocalFunction, forwardMethod.MethodKind);

        Assert.Equal(0, outer.Ordinal);

        Assert.Equal(0, inner.Ordinal);

        Assert.Equal(RefKind.None, outer.RefKind);

        Assert.Equal(RefKind.None, inner.RefKind);

        Assert.Equal(SpecialType.System_Boolean, forwardMethod.ReturnType.SpecialType);

        Assert.True(SymbolEqualityComparer.Default.Equals(outer.Type, inner.Type));

        Assert.True(SymbolEqualityComparer.Default.Equals(inner.Type, forwardMethod.ReturnType));

        Assert.True(SymbolEqualityComparer.Default.Equals(outer.ContainingSymbol, runMethod));

        Assert.True(SymbolEqualityComparer.Default.Equals(inner.ContainingSymbol, forwardMethod));

        Assert.False(SymbolEqualityComparer.Default.Equals(outer.ContainingSymbol, inner.ContainingSymbol));

        IParameterReferenceOperation actualReturn = Assert.IsAssignableFrom<IParameterReferenceOperation>(
            model.GetOperation(forward.ExpressionBody!.Expression));

        Assert.True(SymbolEqualityComparer.Default.Equals(capturesOuter ? outer : inner, actualReturn.Parameter));

        Assert.Equal(!capturesOuter, SymbolEqualityComparer.Default.Equals(actualReturn.Parameter.ContainingSymbol, forwardMethod));

        MethodDeclarationSyntax observe = Assert.Single(target.Members.OfType<MethodDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Observe");

        IMethodSymbol observeMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(observe));

        IParameterSymbol observedParameter = Assert.Single(observeMethod.Parameters);

        InvocationExpressionSyntax readSyntax = Assert.Single(observe.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "Read");

        IInvocationOperation read = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(readSyntax));

        IArgumentOperation refArgument = Assert.Single(read.Arguments);

        Assert.Equal(RefKind.Ref, refArgument.Parameter!.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(observedParameter,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(refArgument.Value).Parameter));

        IfStatementSyntax conditional = Assert.Single(observe.DescendantNodes().OfType<IfStatementSyntax>());

        Assert.True(SymbolEqualityComparer.Default.Equals(observedParameter,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(model.GetOperation(conditional.Condition)).Parameter));

        InvocationExpressionSyntax effectSyntax = Assert.Single(conditional.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

        Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", effect.TargetMethod.Name);

        AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);

        InvocationExpressionSyntax anchorSyntax = Assert.Single(observe.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Exists");

        IInvocationOperation anchor = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(anchorSyntax));

        Assert.Equal("System.IO.File", anchor.TargetMethod.ContainingType.ToDisplayString());

        Assert.Empty(anchor.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, anchor.TargetMethod.ContainingAssembly);

        InvocationExpressionSyntax[] calls = run.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static invocation => invocation.Expression.ToString() == "Observe")
            .OrderBy(static invocation => invocation.SpanStart).ToArray();

        Assert.Equal(2, calls.Length);

        IInvocationOperation first = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[0]));

        IInvocationOperation second = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[1]));

        Assert.True(SymbolEqualityComparer.Default.Equals(observeMethod, first.TargetMethod));

        Assert.True(SymbolEqualityComparer.Default.Equals(observeMethod, second.TargetMethod));

        ILocalSymbol shared = Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(first.Arguments).Value).Local;

        IInvocationOperation forwarded = Assert.IsAssignableFrom<IInvocationOperation>(Assert.Single(second.Arguments).Value);

        Assert.True(SymbolEqualityComparer.Default.Equals(forwardMethod, forwarded.TargetMethod));

        IArgumentOperation ownArgument = Assert.Single(forwarded.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(inner, ownArgument.Parameter));

        Assert.True(ownArgument.InConversion.IsIdentity);

        Assert.True(SymbolEqualityComparer.Default.Equals(shared,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(ownArgument.Value).Local));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot() with
            {
                Authority = HostedProducerAuthorityKind.PreReadinessStartup,
                WorkKind = null,
                Proof = "Worker.StartAsync: captured local return source identity fixture",
            }])], []);

        Assert.Empty(result.Diagnostics);

        Assert.Contains(result.Items, site => site.EnclosingType == "CapturedLocalIdentityTarget"
            && site.Member == "Observe" && site.Callee == "System.IO.File.Exists"
            && site.OperationId.EndsWith("/site@" + tree.FilePath + ":" + anchorSyntax.SpanStart, StringComparison.Ordinal));

        HostedProducerSite[] physical = result.Items.Where(site => site.EnclosingType == "CapturedLocalIdentityTarget"
            && site.Member == "Observe" && site.Callee == "System.IO.File.Delete").ToArray();

        if (capturesOuter)
        {
            Assert.NotEmpty(physical);

            Assert.All(physical, site =>
            {
                Assert.EndsWith("/site@" + tree.FilePath + ":" + effectSyntax.SpanStart, site.OperationId);

                Assert.Null(site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);
            });
        }
        else Assert.Empty(physical);

        HostedProducerEvaluationEnvironmentMetric metric = Assert.Single(
            Assert.IsType<HostedProducerAnalysisMetrics>(result.AnalysisMetrics).EvaluationEnvironments,
            static candidate => candidate.Member.StartsWith("M:CapturedLocalIdentityTarget.Observe(", StringComparison.Ordinal));

        Assert.Equal(capturesOuter ? 2 : 1, metric.InternerRegistrations);
    }
}
