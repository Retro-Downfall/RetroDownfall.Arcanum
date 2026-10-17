using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("work")]
    [InlineData("effect")]
    public void ConversionOperatorTargetTypeInitializationCannotRetainDisposedAuthority(string lifetime)
    {
        string handle = lifetime == "work" ? "lease" : "held";

        string source = R2Source("var input = new ConversionInput(); " + R2Admission
            + "ConversionReleaseState.Release = () => " + handle + ".Dispose(); ConvertedResult selected = input; _ = selected;",
            "static class ConversionReleaseState { public static System.Action Release = null!; } "
            + "sealed class ConversionInput { } "
            + "sealed class ConvertedResult { static ConvertedResult() { ConversionReleaseState.Release(); } "
            + "public static implicit operator ConvertedResult(ConversionInput value) { System.IO.File.Delete(\"after-converter-target-initialization\"); return null!; } }");

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var tree = compilation.SyntaxTrees.Single();

        var model = compilation.GetSemanticModel(tree);

        VariableDeclaratorSyntax selectedVariable = Assert.Single(tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "selected");

        IVariableInitializerOperation initialization = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(selectedVariable.Initializer!));

        IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(initialization.Value);

        Assert.NotNull(conversion.OperatorMethod);

        Assert.Equal("ConvertedResult", conversion.OperatorMethod!.ContainingType.Name);

        Assert.True(conversion.OperatorMethod.IsStatic);

        Assert.IsAssignableFrom<ILocalReferenceOperation>(conversion.Operand);

        IMethodSymbol typeInitializer = Assert.Single(conversion.OperatorMethod.ContainingType.StaticConstructors);

        Assert.Equal(MethodKind.StaticConstructor, typeInitializer.MethodKind);

        Assert.False(typeInitializer.IsImplicitlyDeclared);

        Assert.DoesNotContain(tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            static creation => creation.Type.ToString() == "ConvertedResult");

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "ConvertedResult" && site.Member == "op_Implicit");

        Assert.Null(lifetime == "work" ? effect.WorkFrontierCapsuleId : effect.EffectFrontierCapsuleId);

        string code = lifetime == "work" ? "HOSTED_SITE_WORK_FRONTIER_MISSING" : "HOSTED_SITE_EFFECT_FRONTIER_MISSING";

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == effect.Callee
            && diagnostic.Identity.EndsWith(effect.OperationId[effect.OperationId.LastIndexOf("/site@", StringComparison.Ordinal)..], StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("static-constructor", "work")]
    [InlineData("static-constructor", "effect")]
    [InlineData("static-field", "work")]
    [InlineData("static-field", "effect")]
    public void ConversionOperandHelperTypeInitializationCannotRetainDisposedAuthority(string shape, string lifetime)
    {
        string handle = lifetime == "work" ? "lease" : "held";

        string initializer = shape == "static-constructor"
            ? "static ConversionHelper() { ConversionReleaseState.Release(); } "
            : "private static readonly int marker = Initialize(); static ConversionHelper() { } "
                + "private static int Initialize() { ConversionReleaseState.Release(); return 0; } ";

        string source = R2Source(R2Admission
            + "ConversionReleaseState.Release = () => " + handle + ".Dispose(); int selected = ConversionHelper.Make(); _ = selected;",
            "static class ConversionReleaseState { public static System.Action Release = null!; } "
            + "sealed class ConversionInput { public static implicit operator int(ConversionInput value) { System.IO.File.Delete(\"after-helper-type-initialization\"); return 1; } } "
            + "static class ConversionHelper { " + initializer
            + "internal static ConversionInput Make() { return new ConversionInput(); } }");

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var tree = compilation.SyntaxTrees.Single();

        var model = compilation.GetSemanticModel(tree);

        VariableDeclaratorSyntax selectedVariable = Assert.Single(tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "selected");

        IVariableInitializerOperation initialization = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(selectedVariable.Initializer!));

        IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(initialization.Value);

        Assert.NotNull(conversion.OperatorMethod);

        Assert.Equal("ConversionInput", conversion.OperatorMethod!.ContainingType.Name);

        IInvocationOperation call = Assert.IsAssignableFrom<IInvocationOperation>(conversion.Operand);

        Assert.Equal("ConversionHelper", call.TargetMethod.ContainingType.Name);

        Assert.Equal("Make", call.TargetMethod.Name);

        Assert.Empty(call.Arguments);

        Assert.Single(call.TargetMethod.DeclaringSyntaxReferences);

        IMethodSymbol typeInitializer = Assert.Single(call.TargetMethod.ContainingType.StaticConstructors);

        Assert.Equal(MethodKind.StaticConstructor, typeInitializer.MethodKind);

        Assert.False(typeInitializer.IsImplicitlyDeclared);

        ClassDeclarationSyntax helper = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "ConversionHelper");

        MethodDeclarationSyntax make = Assert.Single(helper.Members.OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "Make");

        Assert.IsAssignableFrom<IMethodBodyOperation>(model.GetOperation(make));

        Assert.DoesNotContain(make.DescendantNodes(), static node => node is InvocationExpressionSyntax);

        ObjectCreationExpressionSyntax created = Assert.Single(make.DescendantNodes().OfType<ObjectCreationExpressionSyntax>());

        IObjectCreationOperation creation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(created));

        Assert.True(creation.Constructor!.IsImplicitlyDeclared);

        Assert.Empty(creation.Constructor.Parameters);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "ConversionInput" && site.Member == "op_Implicit");

        Assert.Null(lifetime == "work" ? effect.WorkFrontierCapsuleId : effect.EffectFrontierCapsuleId);

        string code = lifetime == "work" ? "HOSTED_SITE_WORK_FRONTIER_MISSING" : "HOSTED_SITE_EFFECT_FRONTIER_MISSING";

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == effect.Callee
            && diagnostic.Identity.EndsWith(effect.OperationId[effect.OperationId.LastIndexOf("/site@", StringComparison.Ordinal)..], StringComparison.Ordinal));
    }
}
