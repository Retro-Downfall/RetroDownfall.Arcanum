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
    public void NullableNumericForwardingRetainsItsActualValueSource(bool rounded)
    {
        long? original = 9007199254740993L;

        Assert.Equal<long?>(9007199254740992L, (long?)(double?)original);

        Assert.Equal(original, (long?)original);

        string returned = rounded ? "(long?)(double?)value" : "(long?)value";

        string helper = "internal static class NullableIdentityTarget { "
            + "internal static long? Observed; "
            + "internal static long? Forward(long? value) => " + returned + "; "
            + "internal static void Observe(long? value) { Read(ref value); System.IO.File.Exists(\"nullable-identity-anchor\"); } "
            + "private static void Read(ref long? value) { Observed = value; } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source("long? shared = 9007199254740993L; NullableIdentityTarget.Observe(shared); "
                + "NullableIdentityTarget.Observe(NullableIdentityTarget.Forward(shared)); await Task.CompletedTask;", helper),
            "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax target = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "NullableIdentityTarget");

        MethodDeclarationSyntax forward = Assert.Single(target.Members.OfType<MethodDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Forward");

        IMethodSymbol forwardMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(forward));

        IParameterSymbol forwardParameter = Assert.Single(forwardMethod.Parameters);

        Assert.Equal(RefKind.None, forwardParameter.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(forwardParameter.Type, forwardMethod.ReturnType));

        INamedTypeSymbol nullableLong = Assert.IsAssignableFrom<INamedTypeSymbol>(forwardMethod.ReturnType);

        Assert.Equal(SpecialType.System_Nullable_T, nullableLong.OriginalDefinition.SpecialType);

        Assert.Equal(SpecialType.System_Int64, Assert.Single(nullableLong.TypeArguments).SpecialType);

        AssertMetadataReferenceOwns(compilation, nullableLong.ContainingAssembly);

        IConversionOperation outer = Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(forward.ExpressionBody!.Expression));

        Assert.Null(outer.OperatorMethod);

        Assert.False(outer.Conversion.IsUserDefined);

        IOperation operand = outer.Operand;

        if (rounded)
        {
            IConversionOperation inner = Assert.IsAssignableFrom<IConversionOperation>(operand);

            Assert.Null(inner.OperatorMethod);

            Assert.False(inner.Conversion.IsUserDefined);

            Assert.False(inner.Conversion.IsIdentity);

            Assert.False(outer.Conversion.IsIdentity);

            INamedTypeSymbol nullableDouble = Assert.IsAssignableFrom<INamedTypeSymbol>(inner.Type);

            Assert.Equal(SpecialType.System_Nullable_T, nullableDouble.OriginalDefinition.SpecialType);

            Assert.Equal(SpecialType.System_Double, Assert.Single(nullableDouble.TypeArguments).SpecialType);

            Assert.True(SymbolEqualityComparer.Default.Equals(nullableLong, outer.Type));

            Assert.True(compilation.ClassifyConversion(forwardParameter.Type, nullableDouble).IsNullable);

            Assert.True(compilation.ClassifyConversion(nullableDouble, nullableLong).IsNullable);

            operand = inner.Operand;
        }
        else
        {
            Assert.True(outer.Conversion.IsIdentity);
        }

        Assert.True(SymbolEqualityComparer.Default.Equals(forwardParameter,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(operand).Parameter));

        MethodDeclarationSyntax observe = Assert.Single(target.Members.OfType<MethodDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Observe");

        IMethodSymbol observeMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(observe));

        InvocationExpressionSyntax readSyntax = Assert.Single(observe.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "Read");

        IInvocationOperation read = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(readSyntax));

        IArgumentOperation refArgument = Assert.Single(read.Arguments);

        Assert.Equal(RefKind.Ref, refArgument.Parameter!.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(observeMethod.Parameters),
            Assert.IsAssignableFrom<IParameterReferenceOperation>(refArgument.Value).Parameter));

        InvocationExpressionSyntax anchorSyntax = Assert.Single(observe.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Exists");

        IInvocationOperation anchor = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(anchorSyntax));

        Assert.Equal("System.IO.File", anchor.TargetMethod.ContainingType.ToDisplayString());

        Assert.Empty(anchor.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, anchor.TargetMethod.ContainingAssembly);

        InvocationExpressionSyntax[] calls = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static invocation => invocation.Expression.ToString() == "NullableIdentityTarget.Observe")
            .OrderBy(static invocation => invocation.SpanStart).ToArray();

        Assert.Equal(2, calls.Length);

        IInvocationOperation first = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[0]));

        IInvocationOperation second = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[1]));

        Assert.True(SymbolEqualityComparer.Default.Equals(observeMethod, first.TargetMethod));

        Assert.True(SymbolEqualityComparer.Default.Equals(observeMethod, second.TargetMethod));

        ILocalSymbol shared = Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(first.Arguments).Value).Local;

        IInvocationOperation forwarded = Assert.IsAssignableFrom<IInvocationOperation>(Assert.Single(second.Arguments).Value);

        Assert.True(SymbolEqualityComparer.Default.Equals(forwardMethod, forwarded.TargetMethod));

        Assert.True(SymbolEqualityComparer.Default.Equals(shared,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(forwarded.Arguments).Value).Local));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot() with
            {
                Authority = HostedProducerAuthorityKind.PreReadinessStartup,
                WorkKind = null,
                Proof = "Worker.StartAsync: nullable conversion source identity fixture",
            }])], []);

        Assert.Empty(result.Diagnostics);

        Assert.Contains(result.Items, site => site.EnclosingType == "NullableIdentityTarget"
            && site.Member == "Observe" && site.Callee == "System.IO.File.Exists"
            && site.OperationId.EndsWith("/site@" + tree.FilePath + ":" + anchorSyntax.SpanStart, StringComparison.Ordinal));

        HostedProducerEvaluationEnvironmentMetric metric = Assert.Single(
            Assert.IsType<HostedProducerAnalysisMetrics>(result.AnalysisMetrics).EvaluationEnvironments,
            static candidate => candidate.Member.StartsWith("M:NullableIdentityTarget.Observe(", StringComparison.Ordinal));

        Assert.Equal(rounded ? 2 : 1, metric.InternerRegistrations);
    }
}
