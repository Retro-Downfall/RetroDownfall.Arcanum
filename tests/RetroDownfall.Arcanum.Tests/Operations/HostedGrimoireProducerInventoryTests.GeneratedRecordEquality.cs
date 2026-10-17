using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void GeneratedPrimitiveRecordStructEqualityHasOnlyClosedScalarBackingFields()
    {
        const string helper = "public readonly record struct PrimitiveIdentity(int Number, bool Enabled);";

        const string body = "PrimitiveIdentity left = new(1, true); PrimitiveIdentity right = new(1, true); _ = left == right; _ = left != right; await Task.CompletedTask;";

        CSharpCompilation compilation = CompileGeneratedRecordFixture(body, helper);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        INamedTypeSymbol record = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("PrimitiveIdentity"));

        Assert.Equal(TypeKind.Struct, record.TypeKind);

        Assert.True(record.IsRecord);

        AssertRecordSource(compilation, tree, record);

        AssertGeneratedRecordEquals(record);

        AssertGeneratedRecordOperators(compilation, model, tree, record, "left", "right");

        AssertClosedBackingFields(record, SpecialType.System_Boolean, SpecialType.System_Int32);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGeneratedRecordFixture(compilation);

        Assert.Empty(result.Items);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void GeneratedPrimitiveStringRecordClassEqualityHasOnlyClosedBackingFields()
    {
        const string helper = "public sealed record class PrimitiveIdentity(string Name, int Revision);";

        const string body = "PrimitiveIdentity left = new(\"chapter\", 4); PrimitiveIdentity right = new(\"chapter\", 4); _ = left == right; _ = left != right; await Task.CompletedTask;";

        CSharpCompilation compilation = CompileGeneratedRecordFixture(body, helper);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        INamedTypeSymbol record = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("PrimitiveIdentity"));

        Assert.Equal(TypeKind.Class, record.TypeKind);

        Assert.True(record.IsSealed);

        Assert.True(record.IsRecord);

        AssertRecordSource(compilation, tree, record);

        AssertGeneratedRecordEquals(record);

        AssertGeneratedRecordOperators(compilation, model, tree, record, "left", "right");

        AssertClosedBackingFields(record, SpecialType.System_Int32, SpecialType.System_String);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGeneratedRecordFixture(compilation);

        Assert.Empty(result.Items);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void CompilerGeneratedRecordOperatorDoesNotHideAuthoredTypedEqualsEffect()
    {
        const string helper = "public sealed record class EffectfulIdentity { public int Number { get; init; } public bool Equals(EffectfulIdentity? other) { System.IO.File.Delete(\"typed-equals-effect\"); return other is not null && Number == other.Number; } }";

        const string body = "EffectfulIdentity left = new() { Number = 4 }; EffectfulIdentity right = new() { Number = 4 }; _ = left == right; await Task.CompletedTask;";

        CSharpCompilation compilation = CompileGeneratedRecordFixture(body, helper);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        INamedTypeSymbol record = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("EffectfulIdentity"));

        AssertRecordSource(compilation, tree, record);

        AssertGeneratedRecordOperators(compilation, model, tree, record, "left", "right", SyntaxKind.EqualsExpression);

        IMethodSymbol authoredEquals = AssertAuthoredTypedEquals(tree, model, record);

        AssertClosedBackingFields(record, SpecialType.System_Int32);

        Assert.Equal(compilation.Assembly.Identity, authoredEquals.ContainingAssembly.Identity);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGeneratedRecordFixture(compilation);

        AssertNoUnclassifiedGeneratedOperator(result, tree, FindRecordComparison(tree, "left", "right", SyntaxKind.EqualsExpression), record);

        AssertAuthoredEqualsPhysicalEffect(result, tree, model, authoredEquals,
            FindRecordComparison(tree, "left", "right", SyntaxKind.EqualsExpression), "typed-equals-effect");
    }

    [Fact]
    public void OuterGeneratedRecordEqualityTraversesNestedAuthoredEqualsEffect()
    {
        const string helper = "public sealed record class EffectfulValue { public int Number { get; init; } public bool Equals(EffectfulValue? other) { System.IO.File.Delete(\"nested-equals-effect\"); return other is not null && Number == other.Number; } } public sealed record class EffectfulEnvelope(EffectfulValue Value);";

        const string body = "EffectfulEnvelope left = new(new EffectfulValue { Number = 4 }); EffectfulEnvelope right = new(new EffectfulValue { Number = 4 }); _ = left == right; await Task.CompletedTask;";

        CSharpCompilation compilation = CompileGeneratedRecordFixture(body, helper);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        INamedTypeSymbol outer = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("EffectfulEnvelope"));

        INamedTypeSymbol nested = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("EffectfulValue"));

        AssertRecordSource(compilation, tree, outer);

        AssertRecordSource(compilation, tree, nested);

        AssertGeneratedRecordOperators(compilation, model, tree, outer, "left", "right", SyntaxKind.EqualsExpression);

        AssertGeneratedRecordEquals(outer);

        AssertClosedBackingFields(outer, nested);

        IMethodSymbol nestedEquals = AssertAuthoredTypedEquals(tree, model, nested);

        INamedTypeSymbol equatableDefinition = Assert.IsAssignableFrom<INamedTypeSymbol>(
            compilation.GetTypeByMetadataName("System.IEquatable`1"));

        Assert.Equal(TypeKind.Interface, equatableDefinition.TypeKind);

        Assert.Empty(equatableDefinition.DeclaringSyntaxReferences);

        Assert.True(SymbolEqualityComparer.Default.Equals(
            compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly,
            equatableDefinition.ContainingAssembly));

        Assert.Equal("System.Runtime", equatableDefinition.ContainingAssembly.Identity.Name);

        Assert.Equal(10, equatableDefinition.ContainingAssembly.Identity.Version.Major);

        Assert.Equal("b03f5f7f11d50a3a", Convert.ToHexString(
            equatableDefinition.ContainingAssembly.Identity.PublicKeyToken.ToArray()).ToLowerInvariant());

        Assert.Contains(compilation.References, reference => reference is PortableExecutableReference
            && SymbolEqualityComparer.Default.Equals(
                compilation.GetAssemblyOrModuleSymbol(reference), equatableDefinition.ContainingAssembly));

        INamedTypeSymbol nestedEquatable = Assert.Single(nested.AllInterfaces, candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, equatableDefinition)
                && SymbolEqualityComparer.Default.Equals(Assert.Single(candidate.TypeArguments), nested));

        IMethodSymbol equatableSlot = Assert.Single(nestedEquatable.GetMembers("Equals").OfType<IMethodSymbol>());

        Assert.False(equatableSlot.IsStatic);

        Assert.Equal(SpecialType.System_Boolean, equatableSlot.ReturnType.SpecialType);

        IParameterSymbol equatableParameter = Assert.Single(equatableSlot.Parameters);

        Assert.Equal(RefKind.None, equatableParameter.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(equatableParameter.Type, nested));

        Assert.True(SymbolEqualityComparer.Default.Equals(
            nested.FindImplementationForInterfaceMember(equatableSlot), nestedEquals));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGeneratedRecordFixture(compilation);

        BinaryExpressionSyntax comparison = FindRecordComparison(tree, "left", "right", SyntaxKind.EqualsExpression);

        AssertNoUnclassifiedGeneratedOperator(result, tree, comparison, outer);

        AssertAuthoredEqualsPhysicalEffect(result, tree, model, nestedEquals, comparison, "nested-equals-effect");
    }

    private static CSharpCompilation CompileGeneratedRecordFixture(string body, string helper)
    {
        string source = "#nullable enable\n" + R2Source(body, helper);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverGeneratedRecordFixture(CSharpCompilation compilation) =>
        HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation],
            new(["Worker"], []),
            [new("Worker", [OrdinaryRoot()])],
            []);

    private static void AssertRecordSource(CSharpCompilation compilation, SyntaxTree tree, INamedTypeSymbol record)
    {
        Assert.Equal(compilation.Assembly.Identity, record.ContainingAssembly.Identity);

        RecordDeclarationSyntax declaration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<RecordDeclarationSyntax>(),
            syntax => syntax.Identifier.ValueText == record.Name);

        Assert.True(SymbolEqualityComparer.Default.Equals(record, Assert.IsAssignableFrom<INamedTypeSymbol>(
            compilation.GetSemanticModel(tree).GetDeclaredSymbol(declaration))));

        Assert.Equal(declaration.Span, Assert.Single(record.DeclaringSyntaxReferences).Span);
    }

    private static void AssertGeneratedRecordEquals(INamedTypeSymbol record)
    {
        IMethodSymbol equals = Assert.Single(record.GetMembers("Equals").OfType<IMethodSymbol>(), method =>
            !method.IsStatic
                && method.Parameters.Length == 1
                && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, record));

        Assert.Equal(MethodKind.Ordinary, equals.MethodKind);

        Assert.True(equals.IsImplicitlyDeclared);

        Assert.All(equals.DeclaringSyntaxReferences, static reference => Assert.IsType<RecordDeclarationSyntax>(reference.GetSyntax()));

        Assert.Equal(SpecialType.System_Boolean, equals.ReturnType.SpecialType);

        Assert.Equal(RefKind.None, Assert.Single(equals.Parameters).RefKind);
    }

    private static void AssertGeneratedRecordOperators(
        CSharpCompilation compilation,
        SemanticModel model,
        SyntaxTree tree,
        INamedTypeSymbol record,
        string left,
        string right,
        SyntaxKind? onlyKind = null)
    {
        BinaryExpressionSyntax[] comparisons = tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(binary => (onlyKind is null || binary.IsKind(onlyKind.Value))
                && binary.Left.ToString() == left
                && binary.Right.ToString() == right)
            .ToArray();

        Assert.NotEmpty(comparisons);

        foreach (BinaryExpressionSyntax comparison in comparisons)
        {
            IBinaryOperation operation = Assert.IsAssignableFrom<IBinaryOperation>(model.GetOperation(comparison));

            IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(operation.OperatorMethod);

            Assert.Equal(MethodKind.UserDefinedOperator, selected.MethodKind);

            Assert.Equal(comparison.IsKind(SyntaxKind.EqualsExpression) ? "op_Equality" : "op_Inequality", selected.Name);

            Assert.True(selected.IsStatic);

            Assert.True(selected.IsImplicitlyDeclared);

            Assert.All(selected.DeclaringSyntaxReferences, static reference => Assert.IsType<RecordDeclarationSyntax>(reference.GetSyntax()));

            Assert.True(SymbolEqualityComparer.Default.Equals(record, selected.ContainingType));

            Assert.Equal(compilation.Assembly.Identity, selected.ContainingAssembly.Identity);

            Assert.Equal(SpecialType.System_Boolean, selected.ReturnType.SpecialType);

            Assert.All(selected.Parameters, parameter =>
            {
                Assert.Equal(RefKind.None, parameter.RefKind);

                Assert.True(SymbolEqualityComparer.Default.Equals(record, parameter.Type));
            });

            Assert.Equal(SpecialType.System_Boolean, operation.Type!.SpecialType);
        }
    }

    private static void AssertClosedBackingFields(INamedTypeSymbol record, params SpecialType[] expectedTypes)
    {
        IFieldSymbol[] fields = record.GetMembers().OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic)
            .ToArray();

        Assert.Equal(expectedTypes.Length, fields.Length);

        Assert.All(fields, static field => Assert.True(field.IsImplicitlyDeclared));

        Assert.Equal(
            expectedTypes.OrderBy(static type => type).ToArray(),
            fields.Select(static field => field.Type.SpecialType).OrderBy(static type => type).ToArray());
    }

    private static void AssertClosedBackingFields(INamedTypeSymbol record, INamedTypeSymbol nestedType)
    {
        IFieldSymbol field = Assert.Single(record.GetMembers().OfType<IFieldSymbol>(), static field => !field.IsStatic);

        Assert.True(field.IsImplicitlyDeclared);

        Assert.True(SymbolEqualityComparer.Default.Equals(nestedType, field.Type));
    }

    private static IMethodSymbol AssertAuthoredTypedEquals(SyntaxTree tree, SemanticModel model, INamedTypeSymbol record)
    {
        MethodDeclarationSyntax declaration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            syntax => syntax.Identifier.ValueText == "Equals"
                && syntax.ParameterList.Parameters.Count == 1
                && syntax.ParameterList.Parameters[0].Type?.ToString() == record.Name + "?");

        IMethodSymbol method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(declaration));

        Assert.Equal(MethodKind.Ordinary, method.MethodKind);

        Assert.False(method.IsImplicitlyDeclared);

        Assert.True(SymbolEqualityComparer.Default.Equals(record, method.ContainingType));

        Assert.Equal(model.Compilation.Assembly.Identity, method.ContainingAssembly.Identity);

        Assert.True(SymbolEqualityComparer.Default.Equals(record, Assert.Single(method.Parameters).Type));

        Assert.Equal(SpecialType.System_Boolean, method.ReturnType.SpecialType);

        InvocationExpressionSyntax effectSyntax = Assert.Single(declaration.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "System.IO.File.Delete");

        Assert.True(declaration.Span.Contains(effectSyntax.Span));

        return method;
    }

    private static BinaryExpressionSyntax FindRecordComparison(SyntaxTree tree, string left, string right, SyntaxKind kind) =>
        Assert.Single(tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>(), binary =>
            binary.IsKind(kind)
                && binary.Left.ToString() == left
                && binary.Right.ToString() == right);

    private static void AssertNoUnclassifiedGeneratedOperator(
        HostedProducerDiscovery<HostedProducerSite> result,
        SyntaxTree tree,
        BinaryExpressionSyntax comparison,
        INamedTypeSymbol record)
    {
        string coordinate = tree.FilePath + ":" + comparison.SpanStart;

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == record.ToDisplayString() + ".op_Equality"
            && diagnostic.Identity == coordinate);
    }

    private static void AssertAuthoredEqualsPhysicalEffect(
        HostedProducerDiscovery<HostedProducerSite> result,
        SyntaxTree tree,
        SemanticModel model,
        IMethodSymbol authoredEquals,
        BinaryExpressionSyntax comparison,
        string marker,
        string? canonicalRecord = null)
    {
        MethodDeclarationSyntax declaration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            syntax => SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(syntax), authoredEquals));

        InvocationExpressionSyntax effectSyntax = Assert.Single(declaration.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString() == "System.IO.File.Delete"
                && invocation.ArgumentList.Arguments.Single().Expression.ToString() == "\"" + marker + "\"");

        IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

        Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", effect.TargetMethod.Name);

        AssertMetadataReferenceOwns(Assert.IsAssignableFrom<CSharpCompilation>(model.Compilation), effect.TargetMethod.ContainingAssembly);

        string effectCoordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

        // Exact-construction certification names the site; Canonical Equals Traversal names the type.
        string operatorBoundary = canonicalRecord is null
            ? "/operator@" + tree.FilePath + ":" + comparison.SpanStart
            : "/equals@" + canonicalRecord;

        HostedProducerSite[] physical = result.Items.Where(site => site.RootType == "Worker"
            && site.Callee == "System.IO.File.Delete").ToArray();

        Assert.NotEmpty(physical);

        Assert.All(physical, site =>
        {
            Assert.EndsWith(effectCoordinate, site.OperationId);

            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code
                && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.Contains(operatorBoundary, StringComparison.Ordinal)
                && diagnostic.Identity.EndsWith(effectCoordinate, StringComparison.Ordinal));
    }
}
