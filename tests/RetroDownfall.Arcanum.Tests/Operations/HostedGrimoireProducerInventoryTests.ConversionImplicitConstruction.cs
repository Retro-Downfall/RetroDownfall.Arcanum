using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("generic", "work")]
    [InlineData("generic", "effect")]
    [InlineData("record-copy", "work")]
    [InlineData("record-copy", "effect")]
    public void ConversionOperandImplicitConstructionCannotRetainDisposedAuthority(string shape, string lifetime)
    {
        string handle = lifetime == "work" ? "lease" : "held";

        string before = shape == "record-copy" ? "var previous = new ConversionInput(); " : "";

        string operand = shape == "generic" ? "ConversionGeneric.Make<DerivedInput>()" : "previous with { }";

        string helpers = shape == "generic"
            ? "class ConversionInput { public static System.Action Release = null!; internal static int ReleaseNow() { Release(); return 0; } "
                + "public static implicit operator int(ConversionInput value) { System.IO.File.Delete(\"after-generic-construction\"); return 1; } } "
                + "sealed class DerivedInput : ConversionInput { private readonly int marker = ConversionInput.ReleaseNow(); } "
                + "static class ConversionGeneric { internal static ConversionInput Make<T>() where T : ConversionInput, new() => new T(); }"
            : "record ConversionInput { public static System.Action Release = null!; public ConversionInput() { } "
                + "protected ConversionInput(ConversionInput previous) { Release(); } "
                + "public static implicit operator int(ConversionInput value) { System.IO.File.Delete(\"after-record-copy\"); return 1; } }";

        string source = R2Source(before + R2Admission + "ConversionInput.Release = () => " + handle
            + ".Dispose(); int selected = " + operand + "; _ = selected;", helpers);

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

        if (shape == "generic")
        {
            ObjectCreationExpressionSyntax creation = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
                static expression => expression.Type.ToString() == "T");

            Assert.IsAssignableFrom<ITypeParameterObjectCreationOperation>(model.GetOperation(creation));

            InvocationExpressionSyntax call = Assert.Single(selectedVariable.DescendantNodes().OfType<InvocationExpressionSyntax>());

            IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(call).Symbol);

            Assert.Equal("DerivedInput", Assert.Single(selected.TypeArguments).Name);
        }
        else
        {
            Assert.IsAssignableFrom<IWithOperation>(conversion.Operand);

            var input = compilation.GetTypeByMetadataName("ConversionInput")!;

            IMethodSymbol copy = Assert.Single(input.InstanceConstructors,
                constructor => constructor.Parameters.Length == 1
                    && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, input));

            Assert.False(copy.IsImplicitlyDeclared);
        }

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "ConversionInput" && site.Member == "op_Implicit");

        Assert.Null(lifetime == "work" ? effect.WorkFrontierCapsuleId : effect.EffectFrontierCapsuleId);

        string code = lifetime == "work" ? "HOSTED_SITE_WORK_FRONTIER_MISSING" : "HOSTED_SITE_EFFECT_FRONTIER_MISSING";

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == effect.Callee
            && diagnostic.Identity.EndsWith(effect.OperationId[effect.OperationId.LastIndexOf("/site@", StringComparison.Ordinal)..], StringComparison.Ordinal));
    }
}
