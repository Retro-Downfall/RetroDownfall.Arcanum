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
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedRecordEqualityPreservesTheActualConvertedReceiver(bool retargeted)
    {
        var (compilation, tree, model, equality, comparison) = InspectRepeatedRecordReceiver(retargeted);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGeneratedRecordFixture(compilation);

        if (!retargeted)
        {
            Assert.Empty(result.Items);

            Assert.Empty(result.Diagnostics);

            return;
        }

        // Distinct execution-closure obligation: certification cannot substitute for the returned
        // record's actual conditional Equals execution. The record has only a closed scalar field, so
        // Canonical Equals Traversal reaches the authored Equals for the converted receiver too, and the
        // effect keeps its type-named boundary instead of the site-named exact-construction boundary.
        AssertAuthoredEqualsPhysicalEffect(result, tree, model, equality, comparison, "retargeted-record-equality",
            canonicalRecord: "RetargetedRecord");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedRecordConversionCertifiesTheClosedValueComparisonWithoutErasingItsEquals(bool retargeted)
    {
        var (compilation, tree, _, _, comparison) = InspectRepeatedRecordReceiver(retargeted);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGeneratedRecordFixture(compilation);

        if (!retargeted)
        {
            Assert.Empty(result.Items);

            Assert.Empty(result.Diagnostics);

            return;
        }

        string coordinate = tree.FilePath + ":" + comparison.SpanStart;

        // The converted receiver cannot be certified by exact construction, but its only field is a
        // closed scalar, so the generated comparison itself carries no handle and is certified; the
        // authored Equals it reaches is still traversed, which the test above pins.
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Identity == coordinate && diagnostic.Detail == "RetargetedRecord.op_Equality");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    private static (CSharpCompilation Compilation, SyntaxTree Tree, SemanticModel Model,
        IMethodSymbol Equality, BinaryExpressionSyntax Comparison) InspectRepeatedRecordReceiver(bool retargeted)
    {
        const string helper = "public sealed record class RetargetedRecord(int Number) { public bool Equals(RetargetedRecord? other) { if (Number == 1) System.IO.File.Delete(\"retargeted-record-equality\"); return other is not null && Number == other.Number; } } public sealed class RecordWrapper { public static explicit operator RecordWrapper(RetargetedRecord value) => new RecordWrapper(); public static explicit operator RetargetedRecord(RecordWrapper value) => new RetargetedRecord(1); } public static class RecordConsumer { public static void Invoke(RetargetedRecord value) { RetargetedRecord right = new(0); _ = value == right; } }";

        string body = "RetargetedRecord shared = new(0); RecordConsumer.Invoke(shared); RecordConsumer.Invoke("
            + (retargeted ? "(RetargetedRecord)(RecordWrapper)shared" : "shared") + "); await Task.CompletedTask;";

        CSharpCompilation compilation = CompileGeneratedRecordFixture(body, helper);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        SyntaxNode syntax = tree.GetRoot();

        INamedTypeSymbol record = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("RetargetedRecord"));

        Assert.True(record.IsRecord && record.IsSealed);

        AssertRecordSource(compilation, tree, record);

        AssertClosedBackingFields(record, SpecialType.System_Int32);

        IMethodSymbol equality = AssertAuthoredTypedEquals(tree, model, record);

        AssertGeneratedRecordOperators(compilation, model, tree, record, "value", "right", SyntaxKind.EqualsExpression);

        MethodDeclarationSyntax consumerSyntax = Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "Invoke");

        IMethodSymbol consumer = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(consumerSyntax));

        Assert.Equal(compilation.Assembly.Identity, consumer.ContainingAssembly.Identity);

        IParameterSymbol formal = Assert.Single(consumer.Parameters);

        Assert.Equal(RefKind.None, formal.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(record, formal.Type));

        BinaryExpressionSyntax comparison = FindRecordComparison(tree, "value", "right", SyntaxKind.EqualsExpression);

        IBinaryOperation comparisonOperation = Assert.IsAssignableFrom<IBinaryOperation>(model.GetOperation(comparison));

        Assert.True(SymbolEqualityComparer.Default.Equals(formal,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(comparisonOperation.LeftOperand).Parameter));

        IInvocationOperation[] calls = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => model.GetOperation(call)).OfType<IInvocationOperation>()
            .Where(call => SymbolEqualityComparer.Default.Equals(call.TargetMethod, consumer)).OrderBy(static call => call.Syntax.SpanStart).ToArray();

        Assert.Equal(2, calls.Length);

        Assert.All(calls, call => Assert.True(SymbolEqualityComparer.Default.Equals(formal, Assert.Single(call.Arguments).Parameter)));

        ILocalSymbol shared = Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(calls[0].Arguments).Value).Local;

        VariableDeclaratorSyntax sharedSyntax = Assert.Single(syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "shared");

        Assert.True(SymbolEqualityComparer.Default.Equals(shared, model.GetDeclaredSymbol(sharedSyntax)));

        IObjectCreationOperation allocation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(sharedSyntax.Initializer!.Value));

        IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(allocation.Constructor);

        Assert.True(SymbolEqualityComparer.Default.Equals(record, allocation.Type));

        Assert.Equal(compilation.Assembly.Identity, constructor.ContainingAssembly.Identity);

        Assert.Equal(0, Assert.Single(allocation.Arguments).Value.ConstantValue.Value);

        Assert.True(calls[0].Syntax.Span.End < calls[1].Syntax.SpanStart);

        if (retargeted)
        {
            IConversionOperation outer = Assert.IsAssignableFrom<IConversionOperation>(Assert.Single(calls[1].Arguments).Value);

            IConversionOperation inner = Assert.IsAssignableFrom<IConversionOperation>(outer.Operand);

            IMethodSymbol outerOperator = Assert.IsAssignableFrom<IMethodSymbol>(outer.OperatorMethod);

            IMethodSymbol innerOperator = Assert.IsAssignableFrom<IMethodSymbol>(inner.OperatorMethod);

            INamedTypeSymbol wrapper = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("RecordWrapper"));

            Assert.True(SymbolEqualityComparer.Default.Equals(shared, Assert.IsAssignableFrom<ILocalReferenceOperation>(inner.Operand).Local));

            Assert.True(SymbolEqualityComparer.Default.Equals(record, inner.Operand.Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(wrapper, inner.Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(wrapper, outer.Operand.Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(record, outer.Type));

            foreach (IMethodSymbol selected in new[] { innerOperator, outerOperator })
            {
                Assert.Equal(MethodKind.Conversion, selected.MethodKind);

                Assert.Equal("op_Explicit", selected.Name);

                Assert.Equal(compilation.Assembly.Identity, selected.ContainingAssembly.Identity);

                Assert.True(SymbolEqualityComparer.Default.Equals(wrapper, selected.ContainingType));

                ConversionOperatorDeclarationSyntax declaration = Assert.IsType<ConversionOperatorDeclarationSyntax>(
                    Assert.Single(selected.DeclaringSyntaxReferences).GetSyntax());

                Assert.True(SymbolEqualityComparer.Default.Equals(selected, model.GetDeclaredSymbol(declaration)));
            }

            Assert.True(SymbolEqualityComparer.Default.Equals(record, Assert.Single(innerOperator.Parameters).Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(wrapper, innerOperator.ReturnType));

            Assert.True(SymbolEqualityComparer.Default.Equals(wrapper, Assert.Single(outerOperator.Parameters).Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(record, outerOperator.ReturnType));

            ConversionOperatorDeclarationSyntax outerDeclaration = Assert.IsType<ConversionOperatorDeclarationSyntax>(
                Assert.Single(outerOperator.DeclaringSyntaxReferences).GetSyntax());

            IObjectCreationOperation replacement = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(outerDeclaration.ExpressionBody!.Expression));

            Assert.True(SymbolEqualityComparer.Default.Equals(record, replacement.Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(constructor, replacement.Constructor));

            Assert.Equal(1, Assert.Single(replacement.Arguments).Value.ConstantValue.Value);

            Assert.Empty(outerDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>());
        }
        else
        {
            Assert.True(SymbolEqualityComparer.Default.Equals(shared,
                Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(calls[1].Arguments).Value).Local));
        }

        MethodDeclarationSyntax equalitySyntax = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(equality.DeclaringSyntaxReferences).GetSyntax());

        IfStatementSyntax conditional = Assert.Single(equalitySyntax.DescendantNodes().OfType<IfStatementSyntax>());

        IBinaryOperation condition = Assert.IsAssignableFrom<IBinaryOperation>(model.GetOperation(conditional.Condition));

        Assert.Equal(BinaryOperatorKind.Equals, condition.OperatorKind);

        Assert.Null(condition.OperatorMethod);

        IPropertyReferenceOperation number = Assert.IsAssignableFrom<IPropertyReferenceOperation>(condition.LeftOperand);

        Assert.Equal("Number", number.Property.Name);

        Assert.True(SymbolEqualityComparer.Default.Equals(record, number.Property.ContainingType));

        Assert.Equal(SpecialType.System_Int32, number.Property.Type.SpecialType);

        Assert.Equal(InstanceReferenceKind.ContainingTypeInstance,
            Assert.IsAssignableFrom<IInstanceReferenceOperation>(number.Instance).ReferenceKind);

        Assert.Equal(1, condition.RightOperand.ConstantValue.Value);

        InvocationExpressionSyntax effectSyntax = Assert.Single(equalitySyntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Delete");

        Assert.True(conditional.Statement.Span.Contains(effectSyntax.Span));

        IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

        Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

        Assert.Empty(effect.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);

        return (compilation, tree, model, equality, comparison);
    }
}
