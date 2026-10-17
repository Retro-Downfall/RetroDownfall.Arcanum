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
    public void AuthoredPositionalRecordPropertyInitializerDoesNotBorrowConstructorScalar(bool usesConstructorParameter)
    {
        string initializer = usesConstructorParameter ? "Number + 1" : "1";

        string helper = "public sealed record class InitializedPositionalRecord(int Number) { public int Number { get; init; } = "
            + initializer + "; public bool Equals(InitializedPositionalRecord? other) { if (Number == 1) System.IO.File.Delete(\"actual-record-initializer-effect\"); return false; } }";

        const string body = "InitializedPositionalRecord left = new(0); InitializedPositionalRecord right = new(0); _ = left == right; await Task.CompletedTask;";

        CSharpCompilation compilation = CompileGeneratedRecordFixture(body, helper);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        INamedTypeSymbol record = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("InitializedPositionalRecord"));

        Assert.True(record.IsRecord && record.IsSealed && record.TypeKind == TypeKind.Class);

        AssertRecordSource(compilation, tree, record);

        AssertClosedBackingFields(record, SpecialType.System_Int32);

        RecordDeclarationSyntax recordSyntax = Assert.Single(syntax.DescendantNodes().OfType<RecordDeclarationSyntax>(),
            declaration => SymbolEqualityComparer.Default.Equals(record, model.GetDeclaredSymbol(declaration)));

        ParameterSyntax parameterSyntax = Assert.Single(recordSyntax.ParameterList!.Parameters);

        IParameterSymbol parameter = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(parameterSyntax));

        Assert.Equal("Number", parameter.Name);

        Assert.Equal(SpecialType.System_Int32, parameter.Type.SpecialType);

        Assert.Equal(RefKind.None, parameter.RefKind);

        IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(parameter.ContainingSymbol);

        Assert.Equal(MethodKind.Constructor, constructor.MethodKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(record, constructor.ContainingType));

        Assert.True(SymbolEqualityComparer.Default.Equals(parameter, Assert.Single(constructor.Parameters)));

        Assert.Equal(compilation.Assembly.Identity, constructor.ContainingAssembly.Identity);

        Assert.DoesNotContain(recordSyntax.Members, static member => member is ConstructorDeclarationSyntax);

        PropertyDeclarationSyntax propertySyntax = Assert.Single(recordSyntax.Members.OfType<PropertyDeclarationSyntax>());

        IPropertySymbol property = Assert.IsAssignableFrom<IPropertySymbol>(model.GetDeclaredSymbol(propertySyntax));

        Assert.False(property.IsImplicitlyDeclared);

        Assert.False(property.IsStatic);

        Assert.Equal("Number", property.Name);

        Assert.Equal(SpecialType.System_Int32, property.Type.SpecialType);

        Assert.True(SymbolEqualityComparer.Default.Equals(record, property.ContainingType));

        Assert.Equal(propertySyntax.Span, Assert.Single(property.DeclaringSyntaxReferences).Span);

        Assert.True(property.SetMethod is { IsInitOnly: true });

        Assert.Equal(2, propertySyntax.AccessorList!.Accessors.Count);

        Assert.All(propertySyntax.AccessorList.Accessors, static accessor =>
        {
            Assert.Null(accessor.Body);

            Assert.Null(accessor.ExpressionBody);

            Assert.True(accessor.SemicolonToken.IsKind(SyntaxKind.SemicolonToken));
        });

        IFieldSymbol backingField = Assert.Single(record.GetMembers().OfType<IFieldSymbol>(), static field => !field.IsStatic);

        Assert.True(SymbolEqualityComparer.Default.Equals(property, backingField.AssociatedSymbol));

        ExpressionSyntax initializerSyntax = propertySyntax.Initializer!.Value;

        IOperation initializerOperation = Assert.IsAssignableFrom<IOperation>(model.GetOperation(initializerSyntax));

        IPropertyInitializerOperation propertyInitialization = Assert.IsAssignableFrom<IPropertyInitializerOperation>(initializerOperation.Parent);

        Assert.True(SymbolEqualityComparer.Default.Equals(property, Assert.Single(propertyInitialization.InitializedProperties)));

        Assert.Same(initializerOperation, propertyInitialization.Value);

        if (usesConstructorParameter)
        {
            IBinaryOperation addition = Assert.IsAssignableFrom<IBinaryOperation>(initializerOperation);

            Assert.Equal(BinaryOperatorKind.Add, addition.OperatorKind);

            Assert.Null(addition.OperatorMethod);

            Assert.Equal(SpecialType.System_Int32, addition.Type!.SpecialType);

            Assert.True(SymbolEqualityComparer.Default.Equals(parameter,
                Assert.IsAssignableFrom<IParameterReferenceOperation>(addition.LeftOperand).Parameter));

            Assert.Equal(1, addition.RightOperand.ConstantValue.Value);

            Assert.False(addition.ConstantValue.HasValue);
        }
        else
        {
            Assert.IsAssignableFrom<ILiteralOperation>(initializerOperation);

            Assert.Equal(1, initializerOperation.ConstantValue.Value);
        }

        foreach (string name in new[] { "left", "right" })
        {
            VariableDeclaratorSyntax local = Assert.Single(syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                declaration => declaration.Identifier.ValueText == name);

            ImplicitObjectCreationExpressionSyntax allocationSyntax = Assert.IsType<ImplicitObjectCreationExpressionSyntax>(local.Initializer!.Value);

            IObjectCreationOperation allocation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(allocationSyntax));

            Assert.True(SymbolEqualityComparer.Default.Equals(record, allocation.Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(constructor, allocation.Constructor));

            IArgumentOperation argument = Assert.Single(allocation.Arguments);

            Assert.True(SymbolEqualityComparer.Default.Equals(parameter, argument.Parameter));

            Assert.Equal(0, argument.Value.ConstantValue.Value);

            Assert.Null(allocationSyntax.Initializer);
        }

        AssertGeneratedRecordOperators(compilation, model, tree, record, "left", "right", SyntaxKind.EqualsExpression);

        BinaryExpressionSyntax comparison = FindRecordComparison(tree, "left", "right", SyntaxKind.EqualsExpression);

        IMethodSymbol equality = AssertAuthoredTypedEquals(tree, model, record);

        MethodDeclarationSyntax equalitySyntax = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(equality.DeclaringSyntaxReferences).GetSyntax());

        IfStatementSyntax conditional = Assert.Single(equalitySyntax.DescendantNodes().OfType<IfStatementSyntax>());

        IBinaryOperation condition = Assert.IsAssignableFrom<IBinaryOperation>(model.GetOperation(conditional.Condition));

        Assert.Equal(BinaryOperatorKind.Equals, condition.OperatorKind);

        Assert.Null(condition.OperatorMethod);

        IPropertyReferenceOperation actualProperty = Assert.IsAssignableFrom<IPropertyReferenceOperation>(condition.LeftOperand);

        Assert.True(SymbolEqualityComparer.Default.Equals(property, actualProperty.Property));

        Assert.Equal(InstanceReferenceKind.ContainingTypeInstance,
            Assert.IsAssignableFrom<IInstanceReferenceOperation>(actualProperty.Instance).ReferenceKind);

        Assert.Equal(1, condition.RightOperand.ConstantValue.Value);

        InvocationExpressionSyntax effectSyntax = Assert.Single(equalitySyntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        Assert.True(conditional.Statement.Span.Contains(effectSyntax.Span));

        IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

        Assert.Empty(effect.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGeneratedRecordFixture(compilation);

        // The actual initialized property is one despite each constructor argument
        // being zero. Refusal alone cannot replace this physical Equals effect.
        AssertAuthoredEqualsPhysicalEffect(result, tree, model, equality, comparison, "actual-record-initializer-effect");

        AssertNoUnclassifiedGeneratedOperator(result, tree, comparison, record);
    }
}
