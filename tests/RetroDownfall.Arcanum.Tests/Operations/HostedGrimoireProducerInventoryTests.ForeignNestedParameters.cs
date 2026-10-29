using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private static string ForeignNestedParameterHelper(bool lambda) => """
        public sealed class Handles
        {
            private readonly System.IO.FileStream _stream;

            private Handles(System.IO.FileStream stream) { _stream = stream; }

            public static Handles Open(string path) => new(new System.IO.FileStream(path, System.IO.FileMode.Open));

            private System.IO.FileStream Stream => _stream;

            public void Run()
            {
        """
        + (lambda
            ? "System.Func<bool, bool, int> Read = (create, writable) => "
            : "int Read(bool create, bool writable) ")
        + """
                {
                    _ = Stream;

                    return create ? 1 : writable ? 2 : 3;
                }
        """
        + (lambda ? ";" : "")
        + """
                _ = Read(false, false);

                _ = Read(true, false);

                _ = Read(false, true);
            }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignNestedParameters_DoNotSplitAnUnrelatedAccessor(bool lambda)
    {
        var (compilation, result, states) = ClosedValueDiscover(
            "Handles.Open(\"p\").Run();", ForeignNestedParameterHelper(lambda));

        HostedProducerAdmittedStateObservation[] accessor = CanonicalizationStatesOf(compilation, states, "Handles", "Stream");

        Assert.Equal(
            ["HOSTED_SITE_EFFECT_FRONTIER_MISSING", "HOSTED_SITE_WORK_FRONTIER_MISSING"],
            result.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().Order(StringComparer.Ordinal));

        Assert.Single(accessor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignNestedParameters_NestedCapturesStayDistinct(bool lambda)
    {
        string helper = """
            public static class Nest
            {
                public static int Dispatch(System.Func<int> callback) => callback();

                public static void Run()
                {
            """
            + (lambda ? "System.Func<bool, int> Read = flag => " : "int Read(bool flag) => ")
            + """
                    Dispatch(() => flag ? 1 : 2);

                    _ = Read(false);

                    _ = Read(true);
                }
            }
            """;

        var (compilation, result, states) = ClosedValueDiscover("Nest.Run();", helper);

        Assert.Empty(result.Diagnostics);

        Assert.Equal(2, CanonicalizationStatesOf(compilation, states, "Nest", "Dispatch").Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignNestedParameters_CalleeOwnBindingsStayDistinct(bool lambda)
    {
        string helper = ForeignNestedParameterHelper(lambda);

        // The original helper's raw-string indentation is intentionally not part of the contract.
        int start = helper.IndexOf("_ = Stream;", StringComparison.Ordinal);

        int end = helper.IndexOf("return create ? 1 : writable ? 2 : 3;", StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);

        helper = helper[..start] + "return Select(create, writable);" + helper[(end + "return create ? 1 : writable ? 2 : 3;".Length)..];

        helper = helper.Replace("public void Run()", "public int Select(bool create, bool writable) { _ = Stream; return create ? 1 : writable ? 2 : 3; } public void Run()", StringComparison.Ordinal);

        var (compilation, result, states) = ClosedValueDiscover("Handles.Open(\"p\").Run();", helper);

        Assert.Equal(
            ["HOSTED_SITE_EFFECT_FRONTIER_MISSING", "HOSTED_SITE_WORK_FRONTIER_MISSING"],
            result.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().Order(StringComparer.Ordinal));

        Assert.Equal(3, CanonicalizationStatesOf(compilation, states, "Handles", "Select").Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ForeignNestedParameters_AdmissionOriginsFollowLexicalScope(bool lambda, bool captured)
    {
        string source = "sealed class Holder { public int Value => 1; public void Run() { "
            + (lambda ? "System.Func<object, int> Read = lease => " : "int Read(object lease) ")
            + "{ System.Func<int> Capture = () => lease.GetHashCode(); return Capture(); }"
            + (lambda ? ";" : "") + "_ = Read(new object()); } }";

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode root = compilation.SyntaxTrees.Single().GetRoot();

        SyntaxNode target = captured
            ? root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>().Last()
            : root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single().ExpressionBody!;

        ParameterSyntax parameter = root.DescendantNodes().OfType<ParameterSyntax>().Single(static candidate => candidate.Identifier.ValueText == "lease");

        (bool seed, bool mayContain) = HostedGrimoireProducerInventory.ProbeAdmissionBinding(compilation, target, parameter);

        Assert.Equal(captured, seed);

        Assert.Equal(captured, mayContain);
    }
}
