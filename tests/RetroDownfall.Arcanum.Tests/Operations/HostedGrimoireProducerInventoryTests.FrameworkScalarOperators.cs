using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("index", "System.Index", "Int32")]
    [InlineData("int128-int32", "System.Int128", "Int32")]
    [InlineData("int128-int64", "System.Int128", "Int64")]
    public void ExactFrameworkScalarOperatorsAreCompilerBoundValueOperations(
        string shape,
        string targetMetadataName,
        string sourceSpecialType)
    {
        string initializer = shape switch
        {
            "index" => "1",
            "int128-int32" => "1",
            _ => "1L",
        };

        string source = R2Source(targetMetadataName + " value = " + initializer + "; _ = value;");
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);
        SemanticModel model = compilation.GetSemanticModel(tree);
        VariableDeclaratorSyntax value = Assert.Single(tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "value");
        IVariableInitializerOperation initialization = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(value.Initializer!));
        IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(initialization.Value);
        IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(conversion.OperatorMethod);
        INamedTypeSymbol target = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(targetMetadataName));
        string trustedAssembly = shape == "index"
            ? typeof(System.Index).Assembly.GetName().Name!
            : typeof(Int128).Assembly.GetName().Name!;

        Assert.Equal("op_Implicit", selected.Name);
        Assert.True(selected.IsStatic);
        Assert.Equal(targetMetadataName, selected.ContainingType.ToDisplayString());
        Assert.True(SymbolEqualityComparer.Default.Equals(target, selected.ContainingType));
        Assert.True(SymbolEqualityComparer.Default.Equals(target, selected.ReturnType));
        Assert.Equal(sourceSpecialType == "Int32" ? SpecialType.System_Int32 : SpecialType.System_Int64,
            selected.Parameters[0].Type.SpecialType);
        Assert.Equal(target.ContainingAssembly.Identity, selected.ContainingAssembly.Identity);
        Assert.Equal(trustedAssembly, selected.ContainingAssembly.Identity.Name);
        Assert.Empty(target.DeclaringSyntaxReferences);
        Assert.Empty(selected.DeclaringSyntaxReferences);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Empty(result.Items);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void AuthoredIndexLookalikeOperatorRemainsExecutableWork()
    {
        const string helper = "namespace System { public struct Index { public static implicit operator Index(int value) { System.IO.File.Delete(\"lookalike\"); return default; } } }";
        string source = R2Source("System.Index value = 1; _ = value;", helper);
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);
        SemanticModel model = compilation.GetSemanticModel(tree);
        VariableDeclaratorSyntax value = Assert.Single(tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "value");
        IVariableInitializerOperation initialization = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(value.Initializer!));
        IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(initialization.Value);
        IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(conversion.OperatorMethod);

        Assert.Equal("System.Index", selected.ContainingType.ToDisplayString());
        Assert.Equal(compilation.Assembly.Identity, selected.ContainingAssembly.Identity);
        Assert.NotEmpty(selected.DeclaringSyntaxReferences);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
    }

    [Fact]
    public void AuthoredPayloadConversionBeforeFrameworkInt128OperatorRemainsExecutableWork()
    {
        const string helper = "sealed class ScalarPayload : System.IDisposable { public void Dispose() => System.IO.File.Delete(\"payload-handle\"); public static implicit operator long(ScalarPayload value) { value.Dispose(); return 1; } }";
        string source = R2Source("System.Int128 value = (System.Int128)(long)new ScalarPayload(); _ = value;", helper);
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);
        SemanticModel model = compilation.GetSemanticModel(tree);
        VariableDeclaratorSyntax value = Assert.Single(tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "value");
        IVariableInitializerOperation initialization = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(value.Initializer!));
        Stack<IOperation> pending = new();
        pending.Push(initialization.Value);
        List<IMethodSymbol> operators = [];

        while (pending.TryPop(out IOperation? operation))
        {
            if (operation is IConversionOperation { OperatorMethod: { } selected }) operators.Add(selected);

            foreach (IOperation child in operation.ChildOperations) pending.Push(child);
        }

        IMethodSymbol payloadConversion = Assert.Single(operators, static method => method.ContainingType.Name == "ScalarPayload");
        IMethodSymbol frameworkConversion = Assert.Single(operators, static method => method.ContainingType.ToDisplayString() == "System.Int128");

        Assert.Equal(SpecialType.System_Int64, payloadConversion.ReturnType.SpecialType);
        Assert.Equal(compilation.Assembly.Identity, payloadConversion.ContainingAssembly.Identity);
        Assert.NotEmpty(payloadConversion.DeclaringSyntaxReferences);
        Assert.Equal(typeof(Int128).Assembly.GetName().Name, frameworkConversion.ContainingAssembly.Identity.Name);
        Assert.Empty(frameworkConversion.DeclaringSyntaxReferences);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
    }
}
