using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("work", false)]
    [InlineData("work", true)]
    [InlineData("effect", false)]
    [InlineData("effect", true)]
    [InlineData("preserved", false)]
    [InlineData("preserved", true)]
    public void ConversionOperatorFrontierBeginsAfterItsOperandCompletes(string lifetime, bool explicitCast)
    {
        string operand = lifetime switch
        {
            "work" => "ConversionOperand.DropWork(lease)",
            "effect" => "ConversionOperand.DropEffect(held)",
            _ => "ConversionOperand.Preserve(lease, held)",
        };

        string initializer = explicitCast ? "(int)(" + operand + ")" : "(" + operand + ")";

        string source = R2Source(R2Admission + "int selected = " + initializer + "; _ = selected;",
            "sealed class ConversionInput { public static implicit operator int(ConversionInput value) { System.IO.File.Delete(\"after-operand\"); return 1; } } "
            + "static class ConversionOperand { "
            + "internal static ConversionInput DropWork(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease value) { value.Dispose(); return new ConversionInput(); } "
            + "internal static ConversionInput DropEffect(System.IDisposable value) { value.Dispose(); return new ConversionInput(); } "
            + "internal static ConversionInput Preserve(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease work, System.IDisposable effect) { return new ConversionInput(); } }");

        var compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var tree = compilation.SyntaxTrees.Single();

        var model = compilation.GetSemanticModel(tree);

        VariableDeclaratorSyntax selectedVariable = Assert.Single(tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "selected");

        IVariableInitializerOperation initialization = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(selectedVariable.Initializer!));

        IOperation value = initialization.Value;

        while (value is IParenthesizedOperation parenthesized) value = parenthesized.Operand;

        IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(value);

        Assert.NotNull(conversion.OperatorMethod);

        Assert.Equal("op_Implicit", conversion.OperatorMethod!.Name);

        Assert.Equal("ConversionInput", conversion.OperatorMethod.ContainingType.Name);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal("ConversionInput", effect.EnclosingType);

        Assert.Equal("op_Implicit", effect.Member);

        Assert.Equal(lifetime == "work", effect.WorkFrontierCapsuleId is null);

        Assert.Equal(lifetime == "effect", effect.EffectFrontierCapsuleId is null);

        Assert.Equal(lifetime == "work", result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete"));

        Assert.Equal(lifetime == "effect", result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is
            "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALL_TARGET_UNRESOLVED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }
}
