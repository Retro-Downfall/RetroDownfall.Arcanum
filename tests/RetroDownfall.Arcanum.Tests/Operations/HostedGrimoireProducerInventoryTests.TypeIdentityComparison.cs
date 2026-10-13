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
    [InlineData("typeof-equals-typeof", "_ = typeof(string) == typeof(int);")]
    [InlineData("typeof-not-equals-typeof", "_ = typeof(string) != typeof(int);")]
    [InlineData("gettype-equals-typeof", "object value = new object(); _ = value.GetType() == typeof(string);")]
    [InlineData("typeof-equals-gettype", "object value = new object(); _ = typeof(string) == value.GetType();")]
    [InlineData("gettype-not-equals-typeof", "object value = new object(); _ = value.GetType() != typeof(string);")]
    [InlineData("typeof-not-equals-gettype", "object value = new object(); _ = typeof(string) != value.GetType();")]
    [InlineData("gettype-not-equals-gettype", "object left = new object(); object right = new object(); _ = left.GetType() != right.GetType();")]
    [InlineData("gettype-on-struct", "int number = 1; _ = number.GetType() == typeof(int);")]
    [InlineData("gettype-on-call-receiver", "_ = TypeProbe.Make().GetType() != typeof(string);")]
    [InlineData("gettype-on-interface", "System.IDisposable? disposable = null; _ = disposable!.GetType() != typeof(string);")]
    [InlineData("typeof-open-generic", "_ = typeof(System.Collections.Generic.List<>) == typeof(System.Collections.Generic.List<int>);")]
    public void RuntimeTypeIdentityComparisonIsClassifiedForTypeofAndExactGetType(string shape, string body)
    {
        Assert.NotEmpty(shape);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source(body, "static class TypeProbe { public static object Make() => new object(); }"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RuntimeTypeIdentityComparisonIsTheFrameworkTypeOperator()
    {
        const string body = "object value = new object(); _ = value.GetType() != typeof(string);";

        CSharpCompilation compilation = Compile(R2Source(body));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        BinaryExpressionSyntax comparison = Assert.Single(tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>(),
            static binary => binary.IsKind(SyntaxKind.NotEqualsExpression));

        IBinaryOperation operation = Assert.IsAssignableFrom<IBinaryOperation>(model.GetOperation(comparison));

        IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(operation.OperatorMethod);

        Assert.Equal("op_Inequality", selected.Name);

        Assert.Equal("System.Type", selected.ContainingType.ToDisplayString());

        Assert.Empty(selected.DeclaringSyntaxReferences);

        Assert.IsAssignableFrom<IInvocationOperation>(operation.LeftOperand);

        Assert.IsAssignableFrom<ITypeOfOperation>(operation.RightOperand);
    }

    [Theory]
    [InlineData("local", "Type value = typeof(string); _ = value == typeof(int);")]
    [InlineData("local-not-equals", "Type value = typeof(string); _ = value != typeof(int);")]
    [InlineData("both-locals", "Type left = typeof(string); Type right = typeof(int); _ = left == right;")]
    [InlineData("property", "_ = TypeProbe.Current == typeof(int);")]
    [InlineData("call", "_ = TypeProbe.Resolve() != typeof(int);")]
    [InlineData("parameter-type", "_ = TypeProbe.Same(typeof(int), typeof(string));")]
    [InlineData("gettype-result-stored", "Type value = new object().GetType(); _ = value == typeof(int);")]
    [InlineData("type-argument-gettype", "_ = TypeProbe.Resolve().GetType() == TypeProbe.Resolve();")]
    public void ArbitraryTypeValuesAreNotClassifiedAsRuntimeTypeIdentity(string shape, string body)
    {
        Assert.NotEmpty(shape);

        const string helper = """
            static class TypeProbe
            {
                public static Type Current => typeof(string);

                public static Type Resolve() => typeof(string);

                public static bool Same(Type left, Type right) => left == right;
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body, helper));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail is "System.Type.op_Equality" or "System.Type.op_Inequality");
    }

    [Fact]
    public void AnAuthoredConversionIntoTypeStaysExecutableWorkAndUnclassified()
    {
        const string helper = """
            sealed class TypeConversionProbe
            {
                public static implicit operator Type(TypeConversionProbe value)
                {
                    System.IO.File.Delete("type-conversion-effect");

                    return typeof(string);
                }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source("_ = new TypeConversionProbe() == typeof(int);", helper));

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Type.op_Equality");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
    }

    [Fact]
    public void AnAuthoredGetTypeHidingMethodStaysExecutableWorkAndUnclassified()
    {
        const string helper = """
            sealed class HidingProbe
            {
                public new Type GetType()
                {
                    System.IO.File.Delete("get-type-lookalike-effect");

                    return typeof(string);
                }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(
            R2Source("HidingProbe probe = new HidingProbe(); _ = probe.GetType() == typeof(int);", helper));

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Type.op_Equality");
    }
}
