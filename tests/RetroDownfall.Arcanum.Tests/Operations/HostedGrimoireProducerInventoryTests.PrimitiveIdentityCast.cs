using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void PrimitiveIdentityCastForwardingPreservesTheSameEvaluationSource()
    {
        const string helper = "internal static class PrimitiveIdentityCastTarget { "
            + "internal static long Observed; internal static long Forward(long value) => (long)value; "
            + "internal static void Observe(long value) { Read(ref value); System.IO.File.Exists(\"primitive-identity-cast-anchor\"); } "
            + "private static void Read(ref long value) { Observed = value; } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source("long shared = 16777217L; PrimitiveIdentityCastTarget.Observe(shared); "
                + "PrimitiveIdentityCastTarget.Observe(PrimitiveIdentityCastTarget.Forward(shared)); await Task.CompletedTask;", helper),
            "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax target = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "PrimitiveIdentityCastTarget");

        MethodDeclarationSyntax forwardSyntax = Assert.Single(target.Members.OfType<MethodDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Forward");

        IMethodSymbol forwardMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(forwardSyntax));

        IParameterSymbol formal = Assert.Single(forwardMethod.Parameters);

        Assert.Equal(SpecialType.System_Int64, formal.Type.SpecialType);

        Assert.True(SymbolEqualityComparer.Default.Equals(formal.Type, forwardMethod.ReturnType));

        Assert.Equal(RefKind.None, formal.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(formal.ContainingSymbol, forwardMethod));

        IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(forwardSyntax.ExpressionBody!.Expression));

        Assert.True(conversion.Conversion.IsIdentity);

        Assert.Null(conversion.OperatorMethod);

        Assert.False(conversion.IsImplicit);

        Assert.True(SymbolEqualityComparer.Default.Equals(formal,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(conversion.Operand).Parameter));

        MethodDeclarationSyntax observeSyntax = Assert.Single(target.Members.OfType<MethodDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Observe");

        IMethodSymbol observeMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(observeSyntax));

        InvocationExpressionSyntax[] calls = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static invocation => invocation.Expression.ToString() == "PrimitiveIdentityCastTarget.Observe")
            .OrderBy(static invocation => invocation.SpanStart).ToArray();

        Assert.Equal(2, calls.Length);

        IInvocationOperation first = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[0]));

        IInvocationOperation second = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[1]));

        Assert.True(SymbolEqualityComparer.Default.Equals(observeMethod, first.TargetMethod));

        Assert.True(SymbolEqualityComparer.Default.Equals(observeMethod, second.TargetMethod));

        ILocalSymbol shared = Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(first.Arguments).Value).Local;

        IInvocationOperation forwarded = Assert.IsAssignableFrom<IInvocationOperation>(Assert.Single(second.Arguments).Value);

        Assert.True(SymbolEqualityComparer.Default.Equals(forwardMethod, forwarded.TargetMethod));

        Assert.True(SymbolEqualityComparer.Default.Equals(formal, Assert.Single(forwarded.Arguments).Parameter));

        Assert.True(Assert.Single(forwarded.Arguments).InConversion.IsIdentity);

        Assert.True(SymbolEqualityComparer.Default.Equals(shared,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(forwarded.Arguments).Value).Local));

        InvocationExpressionSyntax readSyntax = Assert.Single(observeSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "Read");

        IArgumentOperation refArgument = Assert.Single(Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(readSyntax)).Arguments);

        Assert.Equal(RefKind.Ref, refArgument.Parameter!.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(observeMethod.Parameters),
            Assert.IsAssignableFrom<IParameterReferenceOperation>(refArgument.Value).Parameter));

        InvocationExpressionSyntax anchorSyntax = Assert.Single(observeSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Exists");

        IInvocationOperation anchor = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(anchorSyntax));

        Assert.Equal("System.IO.File", anchor.TargetMethod.ContainingType.ToDisplayString());

        Assert.Empty(anchor.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, anchor.TargetMethod.ContainingAssembly);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot() with
            {
                Authority = HostedProducerAuthorityKind.PreReadinessStartup,
                WorkKind = null,
                Proof = "Worker.StartAsync: primitive identity cast source fixture",
            }])], []);

        Assert.Empty(result.Diagnostics);

        Assert.Contains(result.Items, site => site.EnclosingType == "PrimitiveIdentityCastTarget"
            && site.Member == "Observe" && site.Callee == "System.IO.File.Exists"
            && site.OperationId.EndsWith("/site@" + tree.FilePath + ":" + anchorSyntax.SpanStart, StringComparison.Ordinal));

        HostedProducerEvaluationEnvironmentMetric metric = Assert.Single(
            Assert.IsType<HostedProducerAnalysisMetrics>(result.AnalysisMetrics).EvaluationEnvironments,
            static candidate => candidate.Member.StartsWith("M:PrimitiveIdentityCastTarget.Observe(", StringComparison.Ordinal));

        Assert.Equal(1, metric.InternerRegistrations);
    }
}
