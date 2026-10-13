using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void GeneratedRecordInstanceInitializerRefusesAuthorityButRetainsAuthoredEqualsSite()
    {
        string source = OrdinaryScopedHolderFixture("same-scoped-lease");

        const string anchorText = "System.IO.File.Delete(\"ordinary-scoped-first\");";

        Assert.Equal(1, source.Split(anchorText, StringSplitOptions.None).Length - 1);

        source = "#nullable enable\n" + source.Replace(anchorText,
            anchorText + " _ = new InitializedEqualityRecord() == new InitializedEqualityRecord();", StringComparison.Ordinal)
            + "\npublic sealed record class InitializedEqualityRecord { public int Number { get; init; } = InitializeNumber(); private static int InitializeNumber() { System.IO.File.Delete(\"record-instance-initializer\"); return 4; } public bool Equals(InitializedEqualityRecord? other) { System.IO.File.Delete(\"initialized-record-equals\"); return other is not null; } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name && method.Parent is ClassDeclarationSyntax type && type.Identifier.ValueText == owner);

        MethodDeclarationSyntax run = Method("Worker", "RunOneAsync");

        MethodDeclarationSyntax firstSend = Method("LongRestSweepCoordinator", "SendFirstAsync");

        InvocationExpressionSyntax acquisitionSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryAcquireWorkLease" });

        IInvocationOperation acquisition = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(acquisitionSyntax));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate", acquisition.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, acquisition.TargetMethod.ContainingAssembly.Identity);

        Assert.Single(acquisition.TargetMethod.DeclaringSyntaxReferences);

        Assert.Equal(17, Convert.ToInt32(acquisition.Arguments[0].Value.ConstantValue.Value));

        Assert.Equal(RefKind.Out, acquisition.Arguments[1].Parameter!.RefKind);

        IfStatementSyntax acquireGuard = Assert.Single(acquisitionSyntax.Ancestors().OfType<IfStatementSyntax>());

        Assert.True(Assert.IsType<PrefixUnaryExpressionSyntax>(acquireGuard.Condition).IsKind(SyntaxKind.LogicalNotExpression));

        Assert.Contains(acquireGuard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>(), static statement => statement.Expression is null);

        VariableDeclaratorSyntax lease = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(), static local => local.Identifier.ValueText == "lease");

        Assert.Equal("admitted", Assert.IsType<IdentifierNameSyntax>(lease.Initializer!.Value).Identifier.ValueText);

        Assert.True(lease.Parent!.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0, AwaitKeyword.RawKind: not 0 });

        InvocationExpressionSyntax bindingSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "Bind" });

        IInvocationOperation binding = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(bindingSyntax));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission", binding.TargetMethod.ContainingType.ToDisplayString());

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(lease),
            Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(binding.Arguments).Value).Local));

        InvocationExpressionSyntax groupSyntax = Assert.Single(firstSend.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryBeginExternalEffectGroup" });

        IInvocationOperation group = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(groupSyntax));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", group.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, group.TargetMethod.ContainingAssembly.Identity);

        Assert.Single(group.TargetMethod.DeclaringSyntaxReferences);

        Assert.Equal(RefKind.Out, Assert.Single(group.Arguments).Parameter!.RefKind);

        ILocalSymbol groupLocal = Assert.IsAssignableFrom<ILocalReferenceOperation>(
            Assert.IsAssignableFrom<IDeclarationExpressionOperation>(Assert.Single(group.Arguments).Value).Expression).Local;

        IfStatementSyntax groupGuard = Assert.Single(groupSyntax.Ancestors().OfType<IfStatementSyntax>());

        Assert.True(Assert.IsType<PrefixUnaryExpressionSyntax>(groupGuard.Condition).IsKind(SyntaxKind.LogicalNotExpression));

        Assert.Contains(groupGuard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>(), static statement => statement.Expression is null);

        VariableDeclaratorSyntax owned = Assert.Single(firstSend.DescendantNodes().OfType<VariableDeclaratorSyntax>(), static local => local.Identifier.ValueText == "owned");

        Assert.True(SymbolEqualityComparer.Default.Equals(groupLocal, model.GetSymbolInfo(owned.Initializer!.Value).Symbol));

        Assert.True(owned.Parent!.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } retained && retained.Parent == firstSend.Body);

        BinaryExpressionSyntax comparison = Assert.Single(firstSend.DescendantNodes().OfType<BinaryExpressionSyntax>(), static binary => binary.IsKind(SyntaxKind.EqualsExpression));

        Assert.True(groupSyntax.Span.End < owned.SpanStart && owned.Span.End < comparison.SpanStart);

        INamedTypeSymbol record = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("InitializedEqualityRecord"));

        Assert.True(record.IsRecord && record.IsSealed && record.TypeKind == TypeKind.Class);

        AssertRecordSource(compilation, tree, record);

        RecordDeclarationSyntax recordSyntax = Assert.Single(syntax.DescendantNodes().OfType<RecordDeclarationSyntax>(),
            candidate => SymbolEqualityComparer.Default.Equals(record, model.GetDeclaredSymbol(candidate)));

        Assert.Null(recordSyntax.ParameterList);

        Assert.DoesNotContain(recordSyntax.Members, static member => member is ConstructorDeclarationSyntax or BaseFieldDeclarationSyntax);

        AssertClosedBackingFields(record, SpecialType.System_Int32);

        PropertyDeclarationSyntax propertySyntax = Assert.Single(recordSyntax.Members.OfType<PropertyDeclarationSyntax>());

        IPropertySymbol property = Assert.IsAssignableFrom<IPropertySymbol>(model.GetDeclaredSymbol(propertySyntax));

        IFieldSymbol backingField = Assert.Single(record.GetMembers().OfType<IFieldSymbol>(), static field => !field.IsStatic);

        Assert.True(SymbolEqualityComparer.Default.Equals(backingField.AssociatedSymbol, property));

        Assert.False(property.IsStatic);

        Assert.Equal(SpecialType.System_Int32, property.Type.SpecialType);

        Assert.True(property.SetMethod is { IsInitOnly: true });

        ExpressionSyntax initializerSyntax = Assert.IsType<InvocationExpressionSyntax>(propertySyntax.Initializer!.Value);

        IInvocationOperation initializer = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(initializerSyntax));

        Assert.False(initializer.ConstantValue.HasValue);

        Assert.True(initializer.TargetMethod.IsStatic);

        Assert.Equal("InitializeNumber", initializer.TargetMethod.Name);

        Assert.True(SymbolEqualityComparer.Default.Equals(initializer.TargetMethod.ContainingType, record));

        Assert.Equal(compilation.Assembly.Identity, initializer.TargetMethod.ContainingAssembly.Identity);

        MethodDeclarationSyntax initializerMethod = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(initializer.TargetMethod.DeclaringSyntaxReferences).GetSyntax());

        InvocationExpressionSyntax initializerEffectSyntax = Assert.Single(initializerMethod.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation initializerEffect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(initializerEffectSyntax));

        Assert.Equal("System.IO.File", initializerEffect.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", initializerEffect.TargetMethod.Name);

        Assert.Equal("record-instance-initializer", Assert.Single(initializerEffect.Arguments).Value.ConstantValue.Value);

        Assert.Empty(initializerEffect.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, initializerEffect.TargetMethod.ContainingAssembly);

        AssertGeneratedRecordOperators(compilation, model, tree, record,
            "new InitializedEqualityRecord()", "new InitializedEqualityRecord()", SyntaxKind.EqualsExpression);

        foreach (ExpressionSyntax operand in new[] { comparison.Left, comparison.Right })
        {
            IObjectCreationOperation creation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(operand));

            Assert.True(SymbolEqualityComparer.Default.Equals(record, creation.Type));

            IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(creation.Constructor);

            Assert.True(constructor.IsImplicitlyDeclared);

            Assert.Empty(constructor.Parameters);

            Assert.Empty(creation.Arguments);

            Assert.Null(creation.Initializer);

            Assert.True(model.GetConversion(operand).IsIdentity);
        }

        IMethodSymbol authoredEquals = AssertAuthoredTypedEquals(tree, model, record);

        INamedTypeSymbol equatableDefinition = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName("System.IEquatable`1"));

        Assert.Equal("System.Runtime", equatableDefinition.ContainingAssembly.Identity.Name);

        Assert.Equal(10, equatableDefinition.ContainingAssembly.Identity.Version.Major);

        Assert.Equal("b03f5f7f11d50a3a", Convert.ToHexString(equatableDefinition.ContainingAssembly.Identity.PublicKeyToken.ToArray()).ToLowerInvariant());

        Assert.True(SymbolEqualityComparer.Default.Equals(compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly,
            equatableDefinition.ContainingAssembly));

        Assert.Empty(equatableDefinition.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, equatableDefinition.ContainingAssembly);

        INamedTypeSymbol equatable = Assert.Single(record.AllInterfaces, candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, equatableDefinition)
                && SymbolEqualityComparer.Default.Equals(Assert.Single(candidate.TypeArguments), record));

        IMethodSymbol equalsSlot = Assert.Single(equatable.GetMembers("Equals").OfType<IMethodSymbol>());

        Assert.True(SymbolEqualityComparer.Default.Equals(record.FindImplementationForInterfaceMember(equalsSlot), authoredEquals));

        MethodDeclarationSyntax equalsSyntax = Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(method), authoredEquals));

        InvocationExpressionSyntax equalsEffectSyntax = Assert.Single(equalsSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation equalsEffect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(equalsEffectSyntax));

        Assert.Equal("System.IO.File", equalsEffect.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", equalsEffect.TargetMethod.Name);

        Assert.Equal("initialized-record-equals", Assert.Single(equalsEffect.Arguments).Value.ConstantValue.Value);

        Assert.Empty(equalsEffect.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, equalsEffect.TargetMethod.ContainingAssembly);

        Assert.DoesNotContain(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            model.GetOperation(call) is IInvocationOperation invocation
                && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod, authoredEquals));

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]), [new("Worker", [root])], []);

        HostedProducerSite acquired = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite firstGroup = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.Member == "SendFirstAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        Assert.EndsWith("/site@" + tree.FilePath + ":" + acquisitionSyntax.SpanStart, acquired.OperationId);

        Assert.EndsWith("/site@" + tree.FilePath + ":" + groupSyntax.SpanStart, firstGroup.OperationId);

        HostedProducerSite firstAnchor = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.Member == "SendFirstAsync" && site.Callee == "System.IO.File.Delete");

        Assert.Equal(acquired.CapsuleId, firstAnchor.WorkFrontierCapsuleId);

        Assert.Equal(firstGroup.CapsuleId, firstAnchor.EffectFrontierCapsuleId);

        string effectCoordinate = "/site@" + tree.FilePath + ":" + equalsEffectSyntax.SpanStart;

        string operatorBoundary = "/operator@" + tree.FilePath + ":" + comparison.SpanStart;

        HostedProducerSite[] physical = result.Items.Where(site => site.RootType == "Worker"
            && site.Callee == "System.IO.File.Delete" && site.Member == "Equals"
            && site.OperationId.EndsWith(effectCoordinate, StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(physical);

        Assert.All(physical, site =>
        {
            Assert.Equal(firstAnchor.AuthorityOperationId + effectCoordinate, site.OperationId);

            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        AssertNoUnclassifiedGeneratedOperator(result, tree, comparison, record);

        foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code
                && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.Contains(operatorBoundary, StringComparison.Ordinal)
                && diagnostic.Identity.EndsWith(effectCoordinate, StringComparison.Ordinal));
    }
}
