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

    private const string ScalarOperandHelper = """
        static class ScalarProbe
        {
            public static float Pure() => 1f;

            public static double PureDouble() => 1d;

            public static int Offset() => 1;

            public static CancellationToken Token() => default;

            public static ReadOnlySpan<float> Span() => new float[64];

            public static System.Numerics.Vector<float> Vector() => default;
        }

        sealed class ScalarConversionOperand
        {
            public static implicit operator int(ScalarConversionOperand value)
            {
                System.IO.File.Delete("scalar-operand-conversion");

                return 1;
            }

            public static implicit operator CancellationToken(ScalarConversionOperand value)
            {
                System.IO.File.Delete("scalar-operand-token-conversion");

                return default;
            }

            public static implicit operator float(ScalarConversionOperand value)
            {
                System.IO.File.Delete("scalar-operand-float-conversion");

                return 1f;
            }
        }
        """;

    [Theory]
    [InlineData("index-call", "string text = \"abcdef\"; _ = text[Math.Min(text.Length, 3)..];")]
    [InlineData("index-property-chain", "byte[] bytes = new byte[8]; _ = bytes[System.Text.Encoding.UTF8.Preamble.Length..];")]
    [InlineData("index-local", "string text = \"abcdef\"; int start = 1; _ = text[start..];")]
    [InlineData("index-helper-call", "string text = \"abcdef\"; _ = text[ScalarProbe.Offset()..];")]
    [InlineData("index-end", "string text = \"abcdef\"; _ = text[..ScalarProbe.Offset()];")]
    [InlineData("index-both-ends", "string text = \"abcdef\"; _ = text[ScalarProbe.Offset()..(ScalarProbe.Offset() + 2)];")]
    [InlineData("int128-chain", "long value = 5; int percentage = 3; System.Int128 rounded = ((System.Int128)value * percentage + 99) / 100; _ = rounded;")]
    [InlineData("int128-subtract", "long left = 5; int right = 3; _ = (System.Int128)left - right;")]
    [InlineData("int128-call-operands", "_ = (System.Int128)ScalarProbe.Offset() + ScalarProbe.Offset();")]
    [InlineData("int128-unsigned-sources", "ulong big = 5; uint mid = 2; ushort small = 1; byte tiny = 1; _ = (System.Int128)big + mid + small + tiny;")]
    [InlineData("cancellation-equals", "CancellationToken copy = token; _ = copy == token; _ = copy != token;")]
    [InlineData("cancellation-call-operand", "_ = ScalarProbe.Token() == token; _ = token != ScalarProbe.Token();")]
    [InlineData("finite-float-call", "_ = float.IsFinite(ScalarProbe.Pure());")]
    [InlineData("finite-float-expression", "_ = float.IsFinite(ScalarProbe.Pure() * 2f + 1f);")]
    [InlineData("finite-double-call", "_ = double.IsFinite(2d * ScalarProbe.PureDouble());")]
    [InlineData("finite-lambda-parameter", "float[] values = [1f]; _ = values.Any(value => !float.IsFinite(value));")]
    [InlineData("vector-span-call", "_ = new System.Numerics.Vector<float>(ScalarProbe.Span());")]
    [InlineData("vector-slice-call", "ReadOnlySpan<float> data = new float[64]; _ = new System.Numerics.Vector<float>(data.Slice(ScalarProbe.Offset(), 8));")]
    [InlineData("vector-dot-call", "System.Numerics.Vector<float> value = default; _ = System.Numerics.Vector.Dot<float>(value, ScalarProbe.Vector());")]
    public void StructuralOperandRuleAcceptsExactFrameworkScalarOperatorsWithBuiltInOperands(string shape, string body)
    {
        Assert.NotEmpty(shape);

        CSharpCompilation compilation = Compile("using System.Linq; " + R2Source(body + " await Task.CompletedTask;", ScalarOperandHelper));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Empty(result.Diagnostics);

        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("index-boundary", "string text = \"abcdef\"; _ = text[(int)new ScalarConversionOperand()..];", "System.Index.op_Implicit")]
    [InlineData("int128-boundary", "_ = (System.Int128)(int)new ScalarConversionOperand();", "System.Int128.op_Implicit")]
    [InlineData("int128-arithmetic-boundary", "System.Int128 left = 1; _ = left + (int)new ScalarConversionOperand();", "System.Int128.op_Addition")]
    [InlineData("cancellation-boundary", "CancellationToken copy = token; _ = copy == new ScalarConversionOperand();", "System.Threading.CancellationToken.op_Equality")]
    [InlineData("finite-boundary", "_ = float.IsFinite(new ScalarConversionOperand());", "System.Single.IsFinite")]
    [InlineData("finite-explicit-boundary", "_ = float.IsFinite((float)new ScalarConversionOperand());", "System.Single.IsFinite")]
    public void StructuralOperandRuleRefusesAUserConversionAtTheOperatorBoundary(string shape, string body, string callee)
    {
        Assert.NotEmpty(shape);

        CSharpCompilation compilation = Compile("using System.Linq; " + R2Source(body + " await Task.CompletedTask;", ScalarOperandHelper));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == callee);

        // The authored conversion stays traversed as ordinary executable work.
        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("index-local", "string text = \"abcdef\"; int start = new ScalarConversionOperand(); _ = text[start..];")]
    [InlineData("int128-local", "int amount = new ScalarConversionOperand(); _ = (System.Int128)amount + 1;")]
    [InlineData("finite-local", "float amount = new ScalarConversionOperand(); _ = float.IsFinite(amount);")]
    public void StructuralOperandRuleStillTraversesAConversionBehindALocalOperand(string shape, string body)
    {
        Assert.NotEmpty(shape);

        CSharpCompilation compilation = Compile("using System.Linq; " + R2Source(body + " await Task.CompletedTask;", ScalarOperandHelper));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void AuthoredInt128LookalikeArithmeticRemainsExecutableWork()
    {
        const string helper = """
            namespace System
            {
                public readonly struct Int128
                {
                    public static Int128 operator +(Int128 left, Int128 right)
                    {
                        System.IO.File.Delete("int128-arithmetic-lookalike");

                        return default;
                    }

                    public static implicit operator Int128(int value) => default;
                }
            }
            """;

        string source = R2Source("System.Int128 left = 1; _ = left + 2; await Task.CompletedTask;", helper);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        BinaryExpressionSyntax addition = Assert.Single(tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>(),
            static binary => binary.IsKind(SyntaxKind.AddExpression));

        IBinaryOperation operation = Assert.IsAssignableFrom<IBinaryOperation>(model.GetOperation(addition));

        IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(operation.OperatorMethod);

        Assert.Equal("System.Int128", selected.ContainingType.ToDisplayString());

        Assert.NotEmpty(selected.DeclaringSyntaxReferences);

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
    }

    [Fact]
    public void FrameworkScalarOperatorsAreCompilerBoundToTheExactCoreLibraryIdentities()
    {
        const string body = "long value = 5; System.Int128 total = (System.Int128)value * 2 + 1; CancellationToken copy = token; _ = copy == token; _ = total; await Task.CompletedTask;";

        CSharpCompilation compilation = Compile(R2Source(body));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        IMethodSymbol[] operators = tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Select(binary => model.GetOperation(binary))
            .OfType<IBinaryOperation>()
            .Select(static operation => operation.OperatorMethod)
            .OfType<IMethodSymbol>()
            .ToArray();

        Assert.Contains(operators, static method => method.ContainingType.ToDisplayString() == "System.Int128" && method.Name == "op_Addition");

        Assert.Contains(operators, static method => method.ContainingType.ToDisplayString() == "System.Int128" && method.Name == "op_Multiply");

        Assert.Contains(operators, static method => method.ContainingType.ToDisplayString() == "System.Threading.CancellationToken" && method.Name == "op_Equality");

        Assert.All(operators, static method =>
        {
            Assert.Empty(method.DeclaringSyntaxReferences);

            Assert.True(method.IsStatic);

            Assert.Equal(MethodKind.UserDefinedOperator, method.MethodKind);
        });

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(body));

        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("cancellation-equality", "counterfeit::System.Threading.CancellationToken left = default; _ = left == left;", "System.Threading.CancellationToken.op_Equality")]
    [InlineData("cancellation-inequality", "counterfeit::System.Threading.CancellationToken left = default; _ = left != left;", "System.Threading.CancellationToken.op_Inequality")]
    [InlineData("int128-addition", "counterfeit::System.Int128 left = default; _ = left + left;", "System.Int128.op_Addition")]
    [InlineData("int128-subtraction", "counterfeit::System.Int128 left = default; _ = left - left;", "System.Int128.op_Subtraction")]
    [InlineData("int128-multiplication", "counterfeit::System.Int128 left = default; _ = left * left;", "System.Int128.op_Multiply")]
    [InlineData("int128-division", "counterfeit::System.Int128 left = default; _ = left / left;", "System.Int128.op_Division")]
    [InlineData("int128-implicit", "counterfeit::System.Int128 value = 3;", "System.Int128.op_Implicit")]
    [InlineData("index-implicit", "counterfeit::System.Index value = 3;", "System.Index.op_Implicit")]
    public void FrameworkScalarOperatorsFromAnUnreviewedAssemblyStayUnclassified(string shape, string body, string callee)
    {
        Assert.NotEmpty(shape);

        const string lookalikes = """
            namespace System.Threading
            {
                public readonly struct CancellationToken
                {
                    public static bool operator ==(CancellationToken left, CancellationToken right) => true;

                    public static bool operator !=(CancellationToken left, CancellationToken right) => false;

                    public override bool Equals(object? other) => true;

                    public override int GetHashCode() => 0;
                }
            }

            namespace System
            {
                public readonly struct Int128
                {
                    public static Int128 operator +(Int128 left, Int128 right) => default;

                    public static Int128 operator -(Int128 left, Int128 right) => default;

                    public static Int128 operator *(Int128 left, Int128 right) => default;

                    public static Int128 operator /(Int128 left, Int128 right) => default;

                    public static implicit operator Int128(int value) => default;
                }

                public readonly struct Index
                {
                    public static implicit operator Index(int value) => default;
                }
            }
            """;

        CSharpCompilation foreign = CompileWithProductionReferencePack(lookalikes, "RetroDownfall.Arcanum.Api").WithAssemblyName("Untrusted.Scalars");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        CSharpCompilation compilation = CompileWithProductionReferencePack("extern alias counterfeit; " + R2Source(body + " await Task.CompletedTask;"), "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        // The operator is a metadata method whose declaring assembly is not the trusted CoreLib identity.
        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        IMethodSymbol selected = Assert.Single(tree.GetRoot().DescendantNodes().Select(node => model.GetOperation(node)).OfType<IOperation>()
            .SelectMany(static operation => operation.DescendantsAndSelf())
            .Select(static operation => operation switch
            {
                IBinaryOperation { OperatorMethod: { } method } => method,
                IConversionOperation { OperatorMethod: { } method } => method,
                _ => null,
            }).OfType<IMethodSymbol>().Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>());

        Assert.Equal("Untrusted.Scalars", selected.ContainingAssembly.Name);

        Assert.Empty(selected.DeclaringSyntaxReferences);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == callee);
    }

    // The pure intrinsic table names exact framework identities; a same-named static from another assembly
    // is an unclassified external call, however harmless its body looks. System.Double, System.Int32 and
    // System.String are also in the name-keyed reviewed intrinsic set, which accepts them only when the
    // compilation binds the type as the special type its own core library defines.
    [Theory]
    [InlineData("single-is-finite", "counterfeit::System.Single value = default; _ = counterfeit::System.Single.IsFinite(value);", "System.Single.IsFinite")]
    [InlineData("double-is-finite", "counterfeit::System.Double value = default; _ = counterfeit::System.Double.IsFinite(value);", "System.Double.IsFinite")]
    [InlineData("int32-max", "counterfeit::System.Int32 value = default; _ = counterfeit::System.Int32.Max(value, value);", "System.Int32.Max")]
    [InlineData("string-concat", "counterfeit::System.String text = null!; _ = counterfeit::System.String.Concat(text, text);", "System.String.Concat")]
    public void FrameworkIntrinsicsFromAnUnreviewedAssemblyStayUnclassified(string shape, string body, string callee)
    {
        Assert.NotEmpty(shape);

        const string lookalikes = """
            namespace System
            {
                public readonly struct Single
                {
                    public static bool IsFinite(Single value) => true;
                }

                public readonly struct Double
                {
                    public static bool IsFinite(Double value) => true;
                }

                public readonly struct Int32
                {
                    public static Int32 Max(Int32 left, Int32 right) => left;
                }

                public sealed class String
                {
                    public static String Concat(String left, String right) => left;
                }
            }
            """;

        CSharpCompilation foreign = CompileWithProductionReferencePack(lookalikes, "RetroDownfall.Arcanum.Api").WithAssemblyName("Untrusted.Intrinsics");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        CSharpCompilation compilation = CompileWithProductionReferencePack("extern alias counterfeit; " + R2Source(body + " await Task.CompletedTask;"), "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        IMethodSymbol selected = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(invocation => model.GetSymbolInfo(invocation).Symbol).OfType<IMethodSymbol>(),
            static method => method.ContainingAssembly.Name == "Untrusted.Intrinsics");

        Assert.Equal(SpecialType.None, selected.ContainingType.SpecialType);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == callee);
    }

    // The same names bound to the real framework types stay accepted, so the rows above refuse the
    // counterfeit assembly and not the member.
    [Fact]
    public void FrameworkIntrinsicsFromTheCoreLibraryStayClassified()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(
            "float single = 1f; double number = 2d; int count = 3; string text = \"a\"; "
            + "_ = float.IsFinite(single); _ = double.IsFinite(number); _ = int.Max(count, count); _ = string.Concat(text, text); await Task.CompletedTask;"));

        Assert.Empty(result.Diagnostics);
    }
}
