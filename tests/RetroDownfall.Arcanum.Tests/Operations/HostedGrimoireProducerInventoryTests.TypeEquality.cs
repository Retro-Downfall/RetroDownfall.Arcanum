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
    public void ExactFrameworkTypeEqualityWithCompilerTypeOfOperandsIsReviewed()
    {
        const string helper = "public enum TypeEqualityFixtureEnum : byte { One } "
            + "public static class TypeEqualityProbe { public static bool Matches<T>() where T : struct, System.Enum => typeof(T) == typeof(TypeEqualityFixtureEnum); }";
        CSharpCompilation compilation = CompileTypeEqualityFixture(
            "_ = TypeEqualityProbe.Matches<TypeEqualityFixtureEnum>(); await Task.CompletedTask;",
            helper);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        BinaryExpressionSyntax equality = Assert.Single(tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>(),
            static binary => binary.IsKind(SyntaxKind.EqualsExpression));

        Assert.IsType<TypeOfExpressionSyntax>(equality.Left);

        Assert.IsType<TypeOfExpressionSyntax>(equality.Right);

        IBinaryOperation operation = AssertExactSystemTypeEquality(compilation, model, equality);

        ITypeOfOperation left = Assert.IsAssignableFrom<ITypeOfOperation>(operation.LeftOperand);

        ITypeOfOperation right = Assert.IsAssignableFrom<ITypeOfOperation>(operation.RightOperand);

        Assert.Equal("T", left.TypeOperand.Name);

        Assert.Equal("TypeEqualityFixtureEnum", right.TypeOperand.Name);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverTypeEquality(compilation);

        Assert.Empty(result.Items);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ArbitraryTypeValuesAndAuthoredTypeSubclassRemainUnclassified()
    {
        const string helper = "public enum TypeEqualityFixtureEnum : byte { One } "
            + "public static class TypeEqualityProbe { public static bool Compare(System.Type left, System.Type right) => left == right; } "
            + "public sealed class EffectfulType : System.Reflection.TypeDelegator { "
            + "public EffectfulType() : base(typeof(int)) { } "
            + "public override bool Equals(System.Type? other) { System.IO.File.Delete(\"virtual-type-equals\"); return false; } public override bool Equals(object? other) => false; }";
        CSharpCompilation compilation = CompileTypeEqualityFixture(
            "_ = TypeEqualityProbe.Compare(new EffectfulType(), typeof(TypeEqualityFixtureEnum)); await Task.CompletedTask;",
            helper);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        BinaryExpressionSyntax equality = Assert.Single(tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>(),
            static binary => binary.IsKind(SyntaxKind.EqualsExpression));

        IBinaryOperation operation = AssertExactSystemTypeEquality(compilation, model, equality);

        Assert.IsAssignableFrom<IParameterReferenceOperation>(operation.LeftOperand);

        Assert.IsAssignableFrom<IParameterReferenceOperation>(operation.RightOperand);

        MethodDeclarationSyntax equalsDeclaration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "Equals"
                && method.ParameterList.Parameters.Single().Type?.ToString() == "System.Type?");

        IMethodSymbol authoredEquals = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(equalsDeclaration));

        Assert.True(authoredEquals.IsOverride);

        Assert.Equal(compilation.Assembly.Identity, authoredEquals.ContainingAssembly.Identity);

        IMethodSymbol frameworkEquals = authoredEquals;

        while (frameworkEquals.OverriddenMethod is { } overridden)
            frameworkEquals = overridden;

        Assert.Equal("System.Type", frameworkEquals.ContainingType.ToDisplayString());

        Assert.Equal("Equals", frameworkEquals.Name);

        Assert.True(frameworkEquals.IsVirtual && !frameworkEquals.IsStatic);

        Assert.Equal(SpecialType.System_Boolean, frameworkEquals.ReturnType.SpecialType);

        IParameterSymbol equalsParameter = Assert.Single(frameworkEquals.Parameters);

        Assert.Equal(RefKind.None, equalsParameter.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(equalsParameter.Type, operation.OperatorMethod!.ContainingType));

        Assert.Equal(operation.OperatorMethod!.ContainingAssembly.Identity, frameworkEquals.ContainingAssembly.Identity);

        Assert.Empty(frameworkEquals.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, frameworkEquals.ContainingAssembly);

        InvocationExpressionSyntax virtualEffect = Assert.Single(equalsDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(virtualEffect));

        Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", effect.TargetMethod.Name);

        AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverTypeEquality(compilation);

        string coordinate = tree.FilePath + ":" + equality.SpanStart;

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Type.op_Equality"
            && diagnostic.Identity == coordinate);
    }

    [Fact]
    public void AuthoredConversionToTypeKeepsItsEffectAndDoesNotBecomeAReviewedTypeOfComparison()
    {
        const string helper = "public enum TypeEqualityFixtureEnum : byte { One } "
            + "public sealed class TypePayload { public static implicit operator System.Type(TypePayload value) { "
            + "System.IO.File.Delete(\"type-payload-conversion\"); return typeof(TypeEqualityFixtureEnum); } }";
        CSharpCompilation compilation = CompileTypeEqualityFixture(
            "System.Type candidate = new TypePayload(); _ = candidate == typeof(TypeEqualityFixtureEnum); await Task.CompletedTask;",
            helper);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        BinaryExpressionSyntax equality = Assert.Single(tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>(),
            static binary => binary.IsKind(SyntaxKind.EqualsExpression));

        IBinaryOperation operation = AssertExactSystemTypeEquality(compilation, model, equality);

        ILocalReferenceOperation candidate = Assert.IsAssignableFrom<ILocalReferenceOperation>(operation.LeftOperand);

        Assert.Equal("candidate", candidate.Local.Name);

        VariableDeclaratorSyntax candidateDeclaration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "candidate");

        IVariableInitializerOperation initialization = Assert.IsAssignableFrom<IVariableInitializerOperation>(
            model.GetOperation(candidateDeclaration.Initializer!));

        IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(initialization.Value);

        IMethodSymbol authoredConversion = Assert.IsAssignableFrom<IMethodSymbol>(conversion.OperatorMethod);

        Assert.Equal(MethodKind.Conversion, authoredConversion.MethodKind);

        Assert.Equal("TypePayload", authoredConversion.ContainingType.Name);

        Assert.Equal(compilation.Assembly.Identity, authoredConversion.ContainingAssembly.Identity);

        Assert.Single(authoredConversion.DeclaringSyntaxReferences);

        InvocationExpressionSyntax effectSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        Assert.True(Assert.Single(authoredConversion.DeclaringSyntaxReferences).Span.Contains(effectSyntax.Span));

        IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

        Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", effect.TargetMethod.Name);

        AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverTypeEquality(compilation);

        string equalityCoordinate = tree.FilePath + ":" + equality.SpanStart;

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Type.op_Equality"
            && diagnostic.Identity == equalityCoordinate);

        string effectCoordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

        HostedProducerSite physical = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.Callee == "System.IO.File.Delete");

        Assert.EndsWith(effectCoordinate, physical.OperationId);

        Assert.Null(physical.WorkFrontierCapsuleId);

        Assert.Null(physical.EffectFrontierCapsuleId);

        foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code
                && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.EndsWith(effectCoordinate, StringComparison.Ordinal));
    }

    private static CSharpCompilation CompileTypeEqualityFixture(string body, string helper)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source(body, helper),
            "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    private static IBinaryOperation AssertExactSystemTypeEquality(
        CSharpCompilation compilation,
        SemanticModel model,
        BinaryExpressionSyntax syntax)
    {
        IBinaryOperation operation = Assert.IsAssignableFrom<IBinaryOperation>(model.GetOperation(syntax));

        IMethodSymbol method = Assert.IsAssignableFrom<IMethodSymbol>(operation.OperatorMethod);

        INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("System.Type"));

        IAssemblySymbol core = compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly;

        Assert.Equal(BinaryOperatorKind.Equals, operation.OperatorKind);

        Assert.False(operation.IsLifted);

        Assert.Equal(SpecialType.System_Boolean, operation.Type!.SpecialType);

        Assert.Equal(MethodKind.UserDefinedOperator, method.MethodKind);

        Assert.Equal("op_Equality", method.Name);

        Assert.True(method.IsStatic);

        Assert.Equal(0, method.Arity);

        Assert.True(SymbolEqualityComparer.Default.Equals(type, method.ContainingType));

        Assert.Equal(SpecialType.System_Boolean, method.ReturnType.SpecialType);

        Assert.Equal(2, method.Parameters.Length);

        Assert.All(method.Parameters, parameter =>
        {
            Assert.Equal(RefKind.None, parameter.RefKind);

            Assert.True(SymbolEqualityComparer.Default.Equals(type, parameter.Type));
        });

        Assert.Equal(core.Identity, method.ContainingAssembly.Identity);

        Assert.Equal(new Version(10, 0, 0, 0), method.ContainingAssembly.Identity.Version);

        Assert.Equal(string.Empty, method.ContainingAssembly.Identity.CultureName);

        Assert.Equal(core.Identity.Name == "System.Runtime" ? "B03F5F7F11D50A3A" : "7CEC85D7BEA7798E",
            Convert.ToHexString(method.ContainingAssembly.Identity.PublicKeyToken.AsSpan()));

        Assert.Empty(method.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, method.ContainingAssembly);

        return operation;
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverTypeEquality(CSharpCompilation compilation) =>
        HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation],
            new(["Worker"], []),
            [new("Worker", [OrdinaryRoot()])],
            []);
}
