using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("getter", "work")]
    [InlineData("getter", "effect")]
    [InlineData("nested", "work")]
    [InlineData("nested", "effect")]
    [InlineData("helper-getter", "work")]
    [InlineData("helper-getter", "effect")]
    public void ConversionOperandImplicitCleanupCannotRetainDisposedAuthority(string shape, string lifetime)
    {
        string handle = lifetime == "work" ? "lease" : "held";

        string operand = shape switch
        {
            "getter" => "new ConversionCarrier(" + handle + ").Value",
            "nested" => "(ConversionInput)new ConversionCarrier(" + handle + ")",
            _ => "ConversionOperand.Read(" + handle + ")",
        };

        string source = R2Source(R2Admission + "int selected = " + operand + "; _ = selected;",
            "sealed class ConversionInput { public static implicit operator int(ConversionInput value) { System.IO.File.Delete(\"after-implicit-cleanup\"); return 1; } } "
            + "sealed class ConversionCarrier(System.IDisposable owned) { "
            + "public ConversionInput Value { get { owned.Dispose(); return new ConversionInput(); } } "
            + "public static implicit operator ConversionInput(ConversionCarrier value) => value.Value; } "
            + "static class ConversionOperand { internal static ConversionInput Read(System.IDisposable owned) => new ConversionCarrier(owned).Value; }");

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

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "ConversionInput" && site.Member == "op_Implicit");

        Assert.Null(lifetime == "work" ? effect.WorkFrontierCapsuleId : effect.EffectFrontierCapsuleId);

        string code = lifetime == "work" ? "HOSTED_SITE_WORK_FRONTIER_MISSING" : "HOSTED_SITE_EFFECT_FRONTIER_MISSING";

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == "System.IO.File.Delete"
            && diagnostic.Identity.EndsWith(effect.OperationId[effect.OperationId.LastIndexOf("/site@", StringComparison.Ordinal)..], StringComparison.Ordinal));
    }
}
