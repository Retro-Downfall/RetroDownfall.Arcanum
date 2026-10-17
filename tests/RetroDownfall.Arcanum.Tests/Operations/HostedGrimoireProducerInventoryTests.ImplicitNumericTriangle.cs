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
    public void ImplicitNumericForwardingRetainsTheActualConversionTriangle(bool losesPrecision)
    {
        static float ForwardWitness(float value) => value;

        long original = 16777217L;

        float rounded = 16777217f;

        Assert.Equal(16777217d, (double)original);

        Assert.Equal(16777216d, (double)ForwardWitness(original));

        Assert.Equal((double)rounded, (double)ForwardWitness(rounded));

        string declaration = losesPrecision ? "long shared = 16777217L; " : "float shared = 16777217f; ";

        const string helper = "internal static class NumericTriangleTarget { "
            + "internal static double Observed; internal static float Forward(float value) => value; "
            + "internal static void Observe(double value) { Read(ref value); System.IO.File.Exists(\"numeric-triangle-anchor\"); } "
            + "private static void Read(ref double value) { Observed = value; } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source(declaration + "NumericTriangleTarget.Observe(shared); "
                + "NumericTriangleTarget.Observe(NumericTriangleTarget.Forward(shared)); await Task.CompletedTask;", helper),
            "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax target = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "NumericTriangleTarget");

        MethodDeclarationSyntax forwardSyntax = Assert.Single(target.Members.OfType<MethodDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Forward");

        IMethodSymbol forwardMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(forwardSyntax));

        IParameterSymbol forwardParameter = Assert.Single(forwardMethod.Parameters);

        Assert.Equal(SpecialType.System_Single, forwardMethod.ReturnType.SpecialType);

        Assert.True(SymbolEqualityComparer.Default.Equals(forwardParameter.Type, forwardMethod.ReturnType));

        Assert.True(SymbolEqualityComparer.Default.Equals(forwardParameter,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(model.GetOperation(forwardSyntax.ExpressionBody!.Expression)).Parameter));

        Assert.True(SymbolEqualityComparer.Default.Equals(forwardMethod, forwardParameter.ContainingSymbol));

        MethodDeclarationSyntax observeSyntax = Assert.Single(target.Members.OfType<MethodDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Observe");

        IMethodSymbol observeMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(observeSyntax));

        IParameterSymbol observedParameter = Assert.Single(observeMethod.Parameters);

        Assert.Equal(SpecialType.System_Double, observedParameter.Type.SpecialType);

        InvocationExpressionSyntax[] calls = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static invocation => invocation.Expression.ToString() == "NumericTriangleTarget.Observe")
            .OrderBy(static invocation => invocation.SpanStart).ToArray();

        Assert.Equal(2, calls.Length);

        IInvocationOperation first = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[0]));

        IInvocationOperation second = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[1]));

        Assert.True(SymbolEqualityComparer.Default.Equals(observeMethod, first.TargetMethod));

        Assert.True(SymbolEqualityComparer.Default.Equals(observeMethod, second.TargetMethod));

        IConversionOperation direct = Assert.IsAssignableFrom<IConversionOperation>(Assert.Single(first.Arguments).Value);

        IConversionOperation widened = Assert.IsAssignableFrom<IConversionOperation>(Assert.Single(second.Arguments).Value);

        foreach (IConversionOperation conversion in new[] { direct, widened })
        {
            Assert.True(conversion.IsImplicit);

            Assert.Null(conversion.OperatorMethod);

            Assert.True(conversion.Conversion.IsNumeric);

            Assert.False(conversion.Conversion.IsIdentity);

            Assert.Equal(SpecialType.System_Double, conversion.Type!.SpecialType);
        }

        Assert.True(model.GetConversion(Assert.Single(calls[0].ArgumentList.Arguments).Expression).IsNumeric);

        Assert.True(model.GetConversion(Assert.Single(calls[1].ArgumentList.Arguments).Expression).IsNumeric);

        ILocalSymbol shared = Assert.IsAssignableFrom<ILocalReferenceOperation>(direct.Operand).Local;

        Assert.Equal(losesPrecision ? SpecialType.System_Int64 : SpecialType.System_Single, shared.Type.SpecialType);

        IInvocationOperation forwarded = Assert.IsAssignableFrom<IInvocationOperation>(widened.Operand);

        Assert.True(SymbolEqualityComparer.Default.Equals(forwardMethod, forwarded.TargetMethod));

        Assert.Equal(SpecialType.System_Single, widened.Operand.Type!.SpecialType);

        IArgumentOperation incoming = Assert.Single(forwarded.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(forwardParameter, incoming.Parameter));

        IOperation actualSource = incoming.Value;

        if (losesPrecision)
        {
            IConversionOperation narrowed = Assert.IsAssignableFrom<IConversionOperation>(actualSource);

            Assert.True(narrowed.IsImplicit && narrowed.Conversion.IsNumeric);

            Assert.Null(narrowed.OperatorMethod);

            Assert.Equal(SpecialType.System_Single, narrowed.Type!.SpecialType);

            Assert.Equal(SpecialType.System_Int64, narrowed.Operand.Type!.SpecialType);

            actualSource = narrowed.Operand;
        }

        Assert.True(SymbolEqualityComparer.Default.Equals(shared,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(actualSource).Local));

        InvocationExpressionSyntax readSyntax = Assert.Single(observeSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "Read");

        IInvocationOperation read = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(readSyntax));

        IArgumentOperation refArgument = Assert.Single(read.Arguments);

        Assert.Equal(RefKind.Ref, refArgument.Parameter!.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(observedParameter,
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
                Proof = "Worker.StartAsync: implicit numeric triangle identity fixture",
            }])], []);

        Assert.Empty(result.Diagnostics);

        Assert.Contains(result.Items, site => site.EnclosingType == "NumericTriangleTarget"
            && site.Member == "Observe" && site.Callee == "System.IO.File.Exists"
            && site.OperationId.EndsWith("/site@" + tree.FilePath + ":" + anchorSyntax.SpanStart, StringComparison.Ordinal));

        HostedProducerEvaluationEnvironmentMetric metric = Assert.Single(
            Assert.IsType<HostedProducerAnalysisMetrics>(result.AnalysisMetrics).EvaluationEnvironments,
            static candidate => candidate.Member.StartsWith("M:NumericTriangleTarget.Observe(", StringComparison.Ordinal));

        Assert.Equal(losesPrecision ? 2 : 1, metric.InternerRegistrations);
    }
}
