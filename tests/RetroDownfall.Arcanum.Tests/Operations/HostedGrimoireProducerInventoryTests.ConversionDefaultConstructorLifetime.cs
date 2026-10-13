using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("initializer", "work")]
    [InlineData("initializer", "effect")]
    [InlineData("base", "work")]
    [InlineData("base", "effect")]
    public void DefaultConstructorOperandExecutionCannotRetainDisposedConversionAuthority(string shape, string lifetime)
    {
        string handle = lifetime == "work" ? "lease" : "held";

        string inheritance = shape == "base" ? " : ConversionBase" : "";

        string initializer = shape == "initializer" ? "private readonly int marker = ReleaseNow(); " : "";

        string parent = shape == "base" ? "class ConversionBase { protected ConversionBase() { ConversionInput.Release(); } } " : "";

        string source = R2Source(R2Admission
            + "ConversionInput.Release = () => " + handle + ".Dispose(); int selected = new ConversionInput(); _ = selected;",
            parent + "sealed class ConversionInput" + inheritance + " { "
            + "public static System.Action Release = null!; " + initializer
            + "private static int ReleaseNow() { Release(); return 0; } "
            + "public static implicit operator int(ConversionInput value) { System.IO.File.Delete(\"after-default-construction\"); return 1; } }");

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

        IObjectCreationOperation creation = Assert.IsAssignableFrom<IObjectCreationOperation>(conversion.Operand);

        Assert.NotNull(creation.Constructor);

        Assert.True(creation.Constructor!.IsImplicitlyDeclared);

        Assert.Empty(creation.Constructor.Parameters);

        Assert.Empty(creation.Arguments);

        Assert.DoesNotContain(creation.ChildOperations, static operation => operation is IInvocationOperation);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "ConversionInput" && site.Member == "op_Implicit");

        Assert.Null(lifetime == "work" ? effect.WorkFrontierCapsuleId : effect.EffectFrontierCapsuleId);

        string code = lifetime == "work" ? "HOSTED_SITE_WORK_FRONTIER_MISSING" : "HOSTED_SITE_EFFECT_FRONTIER_MISSING";

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == effect.Callee
            && diagnostic.Identity.EndsWith(effect.OperationId[effect.OperationId.LastIndexOf("/site@", StringComparison.Ordinal)..], StringComparison.Ordinal));
    }
}
