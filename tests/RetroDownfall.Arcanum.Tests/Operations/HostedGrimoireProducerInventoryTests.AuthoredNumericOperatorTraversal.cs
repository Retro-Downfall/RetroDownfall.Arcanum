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
    [InlineData("unary")]
    [InlineData("binary")]
    public void AuthoredNumericOperatorsExposeTheirPhysicalEffectBeforeExactPrimitiveConsumption(string shape)
    {
        string expression = shape == "unary" ? "-default(Probe)" : "default(Probe) + default(Probe)";

        string declaration = shape == "unary"
            ? "public static float operator -(Probe value) { System.IO.File.Delete(\"operator-effect\"); return 1f; }"
            : "public static float operator +(Probe left, Probe right) { System.IO.File.Delete(\"operator-effect\"); return 1f; }";

        string source = R2Source("_ = float.IsFinite(" + expression + "); await Task.CompletedTask;", "struct Probe { " + declaration + " }");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax finiteSyntax = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "float.IsFinite");

        IInvocationOperation finite = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(finiteSyntax));

        IMethodSymbol framework = finite.TargetMethod;

        Assert.Equal("IsFinite", framework.Name);

        Assert.True(framework.IsStatic);

        Assert.Equal(0, framework.Arity);

        Assert.Equal(SpecialType.System_Boolean, framework.ReturnType.SpecialType);

        Assert.Equal(SpecialType.System_Single, framework.ContainingType.SpecialType);

        Assert.Empty(framework.DeclaringSyntaxReferences);

        IParameterSymbol frameworkParameter = Assert.Single(framework.Parameters);

        Assert.Equal(RefKind.None, frameworkParameter.RefKind);

        Assert.Equal(SpecialType.System_Single, frameworkParameter.Type.SpecialType);

        Assert.True(SymbolEqualityComparer.Default.Equals(compilation.GetSpecialType(SpecialType.System_Single), framework.ContainingType));

        Assert.Equal(typeof(float).Assembly.GetName().Name, framework.ContainingAssembly.Identity.Name);

        Assert.Equal(typeof(float).Assembly.GetName().Version, framework.ContainingAssembly.Identity.Version);

        Assert.Equal(Convert.ToHexString(typeof(float).Assembly.GetName().GetPublicKeyToken()!),
            Convert.ToHexString(framework.ContainingAssembly.Identity.PublicKeyToken.AsSpan()));

        AssertMetadataReferenceOwns(compilation, framework.ContainingAssembly);

        IArgumentOperation payload = Assert.Single(finite.Arguments);

        Assert.True(payload.InConversion.IsIdentity && payload.OutConversion.IsIdentity);

        IMethodSymbol selected;

        if (shape == "unary")
        {
            IUnaryOperation unary = Assert.IsAssignableFrom<IUnaryOperation>(payload.Value);

            selected = Assert.IsAssignableFrom<IMethodSymbol>(unary.OperatorMethod);

            Assert.Equal(UnaryOperatorKind.Minus, unary.OperatorKind);

            Assert.IsAssignableFrom<IDefaultValueOperation>(unary.Operand);

            Assert.Single(selected.Parameters);
        }
        else
        {
            IBinaryOperation binary = Assert.IsAssignableFrom<IBinaryOperation>(payload.Value);

            selected = Assert.IsAssignableFrom<IMethodSymbol>(binary.OperatorMethod);

            Assert.Equal(BinaryOperatorKind.Add, binary.OperatorKind);

            Assert.IsAssignableFrom<IDefaultValueOperation>(binary.LeftOperand);

            Assert.IsAssignableFrom<IDefaultValueOperation>(binary.RightOperand);

            Assert.Equal(2, selected.Parameters.Length);
        }

        Assert.Equal(MethodKind.UserDefinedOperator, selected.MethodKind);

        Assert.Equal(shape == "unary" ? "op_UnaryNegation" : "op_Addition", selected.Name);

        Assert.True(selected.IsStatic);

        Assert.Equal(SpecialType.System_Single, selected.ReturnType.SpecialType);

        Assert.Equal("Probe", selected.ContainingType.Name);

        Assert.Equal(TypeKind.Struct, selected.ContainingType.TypeKind);

        Assert.Equal(compilation.Assembly.Identity, selected.ContainingAssembly.Identity);

        Assert.All(selected.Parameters, parameter =>
        {
            Assert.Equal(RefKind.None, parameter.RefKind);

            Assert.True(SymbolEqualityComparer.Default.Equals(selected.ContainingType, parameter.Type));
        });

        OperatorDeclarationSyntax authored = Assert.Single(syntax.DescendantNodes().OfType<OperatorDeclarationSyntax>());

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(authored), selected));

        Assert.Equal(authored.Span, Assert.Single(selected.DeclaringSyntaxReferences).Span);

        Assert.DoesNotContain(payload.Value.DescendantsAndSelf(), static operation => operation is IConversionOperation or IObjectCreationOperation);

        InvocationExpressionSyntax effectSyntax = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Delete");

        Assert.True(authored.Span.Contains(effectSyntax.Span));

        IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

        Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", effect.TargetMethod.Name);

        Assert.Empty(effect.TargetMethod.DeclaringSyntaxReferences);

        Assert.Equal(SpecialType.System_String, Assert.Single(effect.TargetMethod.Parameters).Type.SpecialType);

        AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);

        output.WriteLine("AUTHORED_NUMERIC_OPERATOR_PREMISES shape=" + shape + " selectedAuthoredOperatorAndExactFrameworkConsumer=passed");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        string coordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

        HostedProducerSite[] physical = result.Items.Where(static site => site.RootType == "Worker"
            && site.Callee == "System.IO.File.Delete").ToArray();

        Assert.NotEmpty(physical);

        Assert.All(physical, site =>
        {
            Assert.EndsWith(coordinate, site.OperationId);

            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("work")]
    [InlineData("effect")]
    [InlineData("preserved")]
    public void AuthoredOperatorFormalCleanupEndsOnlyTheMatchingCallerFrontier(string lifetime)
    {
        string value = lifetime == "effect" ? "held" : "lease";

        string body = R2Admission + "_ = float.IsFinite(default(LeaseOperatorProbe) + " + value + "); System.IO.File.Delete(\"after-operator\"); await Task.CompletedTask;";

        string cleanup = lifetime == "preserved" ? "" : "value.Dispose();";

        string helper = "sealed class LeaseOperatorProbe { public static float operator +(LeaseOperatorProbe probe, System.IDisposable value) { "
            + cleanup + " return 1f; } }";

        CSharpCompilation compilation = Compile(R2Source(body, helper));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        BinaryExpressionSyntax callSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>(),
            static expression => expression.Left.ToString() == "default(LeaseOperatorProbe)");

        IBinaryOperation call = Assert.IsAssignableFrom<IBinaryOperation>(model.GetOperation(callSyntax));

        IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(call.OperatorMethod);

        Assert.Equal(MethodKind.UserDefinedOperator, selected.MethodKind);

        Assert.Equal("op_Addition", selected.Name);

        Assert.Equal(compilation.Assembly.Identity, selected.ContainingAssembly.Identity);

        Assert.Equal(SpecialType.System_Single, selected.ReturnType.SpecialType);

        Assert.Equal(2, selected.Parameters.Length);

        IParameterSymbol admission = selected.Parameters[1];

        Assert.Equal("System.IDisposable", admission.Type.ToDisplayString());

        Assert.Equal(RefKind.None, admission.RefKind);

        Assert.IsAssignableFrom<IDefaultValueOperation>(call.LeftOperand);

        IOperation operand = call.RightOperand;

        while (operand is IConversionOperation conversion)
        {
            Assert.Null(conversion.OperatorMethod);

            operand = conversion.Operand;
        }

        ILocalReferenceOperation actual = Assert.IsAssignableFrom<ILocalReferenceOperation>(operand);

        Assert.Equal(value, actual.Local.Name);

        Assert.Equal(lifetime == "effect" ? "System.IDisposable" : "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease",
            actual.Local.Type.ToDisplayString());

        OperatorDeclarationSyntax declaration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<OperatorDeclarationSyntax>());

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(declaration), selected));

        Assert.Equal(declaration.Span, Assert.Single(selected.DeclaringSyntaxReferences).Span);

        if (lifetime == "preserved")
        {
            Assert.Empty(declaration.DescendantNodes().OfType<InvocationExpressionSyntax>());
        }
        else
        {
            InvocationExpressionSyntax disposeSyntax = Assert.Single(declaration.DescendantNodes().OfType<InvocationExpressionSyntax>());

            IInvocationOperation dispose = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(disposeSyntax));

            Assert.Equal("System.IDisposable", dispose.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal("Dispose", dispose.TargetMethod.Name);

            Assert.Empty(dispose.Arguments);

            IParameterReferenceOperation receiver = Assert.IsAssignableFrom<IParameterReferenceOperation>(dispose.Instance);

            Assert.True(SymbolEqualityComparer.Default.Equals(admission, receiver.Parameter));
        }

        InvocationExpressionSyntax effectSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        Assert.True(callSyntax.Span.End < effectSyntax.SpanStart);

        IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

        Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", effect.TargetMethod.Name);

        Assert.Empty(effect.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);

        output.WriteLine("AUTHORED_OPERATOR_FORMAL_PREMISES lifetime=" + lifetime + " exactCallerLocalAndSelectedDisposeFormal=passed");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        string coordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

        HostedProducerSite physical = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.Callee == "System.IO.File.Delete");

        Assert.EndsWith(coordinate, physical.OperationId);

        Assert.Equal(lifetime == "work", physical.WorkFrontierCapsuleId is null);

        Assert.Equal(lifetime == "effect", physical.EffectFrontierCapsuleId is null);

        Assert.Equal(lifetime == "work", result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal)));

        Assert.Equal(lifetime == "effect", result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal)));
    }

    [Fact]
    public void ForeignMetadataUnaryOperatorRetainsItsOwnUnclassifiedBoundary()
    {
        CSharpCompilation foreign = Compile("public struct ForeignOperatorProbe { public static float operator -(ForeignOperatorProbe value) { System.IO.File.Delete(\"foreign-operator\"); return 1f; } }")
            .WithAssemblyName("Unreviewed.NumericOperator");

        using MemoryStream image = new();

        Microsoft.CodeAnalysis.Emit.EmitResult emitted = foreign.Emit(image);

        Assert.True(emitted.Success, string.Join(global::System.Environment.NewLine, emitted.Diagnostics));

        PortableExecutableReference foreignReference = MetadataReference.CreateFromImage(image.ToArray());

        CSharpCompilation compilation = Compile(R2Source("_ = float.IsFinite(-default(ForeignOperatorProbe)); await Task.CompletedTask;"))
            .AddReferences(foreignReference);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        PrefixUnaryExpressionSyntax callSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<PrefixUnaryExpressionSyntax>());

        IUnaryOperation call = Assert.IsAssignableFrom<IUnaryOperation>(model.GetOperation(callSyntax));

        IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(call.OperatorMethod);

        Assert.Equal("op_UnaryNegation", selected.Name);

        Assert.Equal("Unreviewed.NumericOperator", selected.ContainingAssembly.Identity.Name);

        Assert.Empty(selected.DeclaringSyntaxReferences);

        Assert.Equal(SpecialType.System_Single, selected.ReturnType.SpecialType);

        Assert.IsAssignableFrom<IDefaultValueOperation>(call.Operand);

        Assert.True(SymbolEqualityComparer.Default.Equals(compilation.GetAssemblyOrModuleSymbol(foreignReference), selected.ContainingAssembly));

        InvocationExpressionSyntax finiteSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "float.IsFinite");

        IInvocationOperation finite = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(finiteSyntax));

        Assert.Equal(SpecialType.System_Single, finite.TargetMethod.ContainingType.SpecialType);

        Assert.Empty(finite.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, finite.TargetMethod.ContainingAssembly);

        output.WriteLine("FOREIGN_OPERATOR_PREMISES exactSelectedMetadataOperatorAndFrameworkConsumer=passed");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        string coordinate = tree.FilePath + ":" + callSyntax.SpanStart;

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "ForeignOperatorProbe.op_UnaryNegation" && diagnostic.Identity == coordinate);
    }
}
