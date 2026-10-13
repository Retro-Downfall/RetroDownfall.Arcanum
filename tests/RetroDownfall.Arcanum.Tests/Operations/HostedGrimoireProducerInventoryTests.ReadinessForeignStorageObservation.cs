using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Readiness_storage_observation_requires_the_actual_current_holder_instance()
    {
        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        ClassDeclarationSyntax holder = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "LongRestWorkAdmission");

        PropertyDeclarationSyntax original = Assert.Single(holder.Members.OfType<PropertyDeclarationSyntax>(), static property =>
            property.Identifier.ValueText == "HasLease");

        ClassDeclarationSyntax changed = holder.ReplaceNode(original,
            SyntaxFactory.ParseMemberDeclaration("internal bool HasLease => Foreign.current is not null;")!)
            .AddMembers(SyntaxFactory.ParseMemberDeclaration("private static readonly LongRestWorkAdmission Foreign = new();")!);

        source = unit.ReplaceNode(holder, changed).ToFullString();

        AssertExplicitReadinessLeasePremises(source, "PerUnitRecoveryHandler");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax actualHolder = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "LongRestWorkAdmission");

        PropertyDeclarationSyntax property = Assert.Single(actualHolder.Members.OfType<PropertyDeclarationSyntax>(), static candidate =>
            candidate.Identifier.ValueText == "HasLease");

        IPropertySymbol observed = Assert.IsAssignableFrom<IPropertySymbol>(model.GetDeclaredSymbol(property));

        IsPatternExpressionSyntax observation = Assert.IsType<IsPatternExpressionSyntax>(property.ExpressionBody!.Expression);

        IFieldReferenceOperation read = Assert.IsAssignableFrom<IFieldReferenceOperation>(model.GetOperation(observation.Expression));

        MethodDeclarationSyntax bind = Assert.Single(actualHolder.Members.OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "Bind");

        AssignmentExpressionSyntax store = Assert.Single(bind.DescendantNodes().OfType<AssignmentExpressionSyntax>());

        IFieldSymbol retained = Assert.IsAssignableFrom<IFieldSymbol>(model.GetSymbolInfo(store.Left).Symbol);

        // The field declaration is identical; only the actual owning instance differs.
        Assert.True(SymbolEqualityComparer.Default.Equals(read.Field, retained));
        Assert.True(SymbolEqualityComparer.Default.Equals(observed.ContainingType, retained.ContainingType));
        Assert.False(read.Field.IsStatic);
        Assert.False(read.Instance is IInstanceReferenceOperation);

        IFieldReferenceOperation foreign = Assert.IsAssignableFrom<IFieldReferenceOperation>(read.Instance);

        Assert.Equal("Foreign", foreign.Field.Name);
        Assert.Equal(Accessibility.Private, foreign.Field.DeclaredAccessibility);
        Assert.True(foreign.Field.IsStatic);
        Assert.True(foreign.Field.IsReadOnly);
        Assert.Null(foreign.Instance);
        Assert.True(SymbolEqualityComparer.Default.Equals(foreign.Field.Type, retained.ContainingType));

        VariableDeclaratorSyntax foreignDeclaration = Assert.IsType<VariableDeclaratorSyntax>(Assert.Single(foreign.Field.DeclaringSyntaxReferences).GetSyntax());

        IObjectCreationOperation created = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(foreignDeclaration.Initializer!.Value));

        Assert.True(created.Constructor!.IsImplicitlyDeclared);
        Assert.Empty(created.Constructor.Parameters);
        Assert.True(SymbolEqualityComparer.Default.Equals(created.Type, retained.ContainingType));

        IfStatementSyntax guard = Assert.Single(tree.GetRoot().DescendantNodes().OfType<IfStatementSyntax>(), candidate =>
            model.GetSymbolInfo(candidate.Condition).Symbol is IPropertySymbol selected
                && SymbolEqualityComparer.Default.Equals(selected, observed));

        IPropertyReferenceOperation selectedRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(guard.Condition));

        Assert.True(SymbolEqualityComparer.Default.Equals(selectedRead.Property.GetMethod, observed.GetMethod));

        output.WriteLine("R_READINESS_FOREIGN_STORAGE_PREMISE=sameExactField|differentStaticReadonlyHolderInstance|selectedGetter=passed");

        AssertExplicitReadinessDiscovery(source, "PerUnitRecoveryHandler", "foreign-storage-observation");
    }
}
