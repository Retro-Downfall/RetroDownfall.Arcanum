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
    public void SqliteCommandFactoryBaseCallDoesNotExecuteSubclassOverride()
    {
        string source = R2Source("_ = new SqliteBaseDispatchProbe().CreateBase(); await Task.CompletedTask;", """
            sealed class SqliteBaseDispatchProbe : Microsoft.Data.Sqlite.SqliteConnection
            {
                internal Microsoft.Data.Sqlite.SqliteCommand CreateBase() => base.CreateCommand();

                public override Microsoft.Data.Sqlite.SqliteCommand CreateCommand()
                {
                    System.IO.File.Delete("unselected-sqlite-override");

                    return new Microsoft.Data.Sqlite.SqliteCommand();
                }
            }
            """);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        MethodDeclarationSyntax forwarder = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "CreateBase");

        InvocationExpressionSyntax baseSyntax = Assert.Single(forwarder.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation call = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(baseSyntax));

        Assert.IsType<BaseExpressionSyntax>(Assert.IsType<MemberAccessExpressionSyntax>(baseSyntax.Expression).Expression);

        Assert.False(call.IsVirtual);

        Assert.True(call.TargetMethod.IsVirtual);

        Assert.Equal("CreateCommand", call.TargetMethod.Name);

        Assert.Empty(call.TargetMethod.Parameters);

        AssertSqliteVirtualProviderMetadata(compilation, call.TargetMethod.ContainingType);

        MethodDeclarationSyntax overrideSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "CreateCommand");

        IMethodSymbol implementation = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(overrideSyntax));

        Assert.True(implementation.IsOverride);

        Assert.True(SymbolEqualityComparer.Default.Equals(call.TargetMethod, implementation.OverriddenMethod));

        InvocationExpressionSyntax effect = Assert.Single(overrideSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>());

        Assert.Equal("System.IO.File.Delete", effect.Expression.ToString());

        IInvocationOperation selectedEffect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effect));

        Assert.Empty(selectedEffect.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, selectedEffect.TargetMethod.ContainingAssembly);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Empty(result.Items);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void SqliteCommandFactoryOverrideRetainsItsExactConstructorFieldReceiver()
    {
        string source = R2Source("await SqliteReceiverRunner.RunAsync(new SqliteReceiverFieldProbe(new SelectedSqliteCleanup()));", """
            static class SqliteReceiverRunner
            {
                internal static async Task RunAsync(Microsoft.Data.Sqlite.SqliteConnection connection)
                {
                    await using System.Data.Common.DbCommand command = connection.CreateCommand();

                    await Task.CompletedTask;
                }
            }

            sealed class SqliteReceiverFieldProbe : Microsoft.Data.Sqlite.SqliteConnection
            {
                private readonly IDisposable _captured;

                internal SqliteReceiverFieldProbe(IDisposable captured)
                {
                    _captured = captured;
                }

                public override Microsoft.Data.Sqlite.SqliteCommand CreateCommand()
                {
                    _captured.Dispose();

                    return new Microsoft.Data.Sqlite.SqliteCommand();
                }
            }

            sealed class SelectedSqliteCleanup : IDisposable
            {
                public void Dispose() => System.IO.File.Delete("selected-sqlite-capture");
            }

            sealed class UnselectedSqliteCleanup : IDisposable
            {
                public void Dispose() => System.IO.File.Delete("unselected-sqlite-capture");
            }
            """);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax probeSyntax = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "SqliteReceiverFieldProbe");

        INamedTypeSymbol probe = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(probeSyntax));

        Assert.True(probe.IsSealed);

        Assert.Equal(compilation.Assembly.Identity, probe.ContainingAssembly.Identity);

        ConstructorDeclarationSyntax constructorSyntax = Assert.Single(probeSyntax.Members.OfType<ConstructorDeclarationSyntax>());

        IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(constructorSyntax));

        IParameterSymbol captured = Assert.Single(constructor.Parameters);

        Assert.Equal("System.IDisposable", captured.Type.ToDisplayString());

        Assert.Equal(RefKind.None, captured.RefKind);

        AssignmentExpressionSyntax assignmentSyntax = Assert.Single(constructorSyntax.DescendantNodes().OfType<AssignmentExpressionSyntax>());

        ISimpleAssignmentOperation assignment = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(model.GetOperation(assignmentSyntax));

        IFieldSymbol field = Assert.IsAssignableFrom<IFieldReferenceOperation>(assignment.Target).Field;

        Assert.True(field.IsReadOnly && !field.IsStatic);

        Assert.Equal(Accessibility.Private, field.DeclaredAccessibility);

        Assert.True(SymbolEqualityComparer.Default.Equals(captured,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(assignment.Value).Parameter));

        ObjectCreationExpressionSyntax allocationSyntax = Assert.Single(syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            static allocation => allocation.Type.ToString() == "SqliteReceiverFieldProbe");

        IObjectCreationOperation allocation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(allocationSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(constructor, allocation.Constructor));

        IArgumentOperation payload = Assert.Single(allocation.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(captured, payload.Parameter));

        IConversionOperation toContract = Assert.IsAssignableFrom<IConversionOperation>(payload.Value);

        Assert.Null(toContract.OperatorMethod);

        Assert.True(toContract.Conversion.IsReference);

        IObjectCreationOperation selectedCapture = Assert.IsAssignableFrom<IObjectCreationOperation>(toContract.Operand);

        Assert.Equal("SelectedSqliteCleanup", selectedCapture.Type!.Name);

        MethodDeclarationSyntax runner = Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "RunAsync");

        IMethodSymbol runnerSymbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(runner));

        IParameterSymbol connection = Assert.Single(runnerSymbol.Parameters);

        AssertSqliteVirtualProviderMetadata(compilation, Assert.IsAssignableFrom<INamedTypeSymbol>(connection.Type));

        InvocationExpressionSyntax factorySyntax = Assert.Single(runner.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation factory = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(factorySyntax));

        Assert.True(factory.IsVirtual && factory.TargetMethod.IsVirtual);

        Assert.True(SymbolEqualityComparer.Default.Equals(connection,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(factory.Instance).Parameter));

        AssertSqliteVirtualProviderMetadata(compilation, factory.TargetMethod.ContainingType);

        MethodDeclarationSyntax overrideSyntax = Assert.Single(probeSyntax.Members.OfType<MethodDeclarationSyntax>());

        IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(overrideSyntax));

        Assert.True(selected.IsOverride);

        Assert.True(SymbolEqualityComparer.Default.Equals(factory.TargetMethod, selected.OverriddenMethod));

        InvocationExpressionSyntax disposalSyntax = Assert.Single(overrideSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation disposal = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(disposalSyntax));

        Assert.Equal("System.IDisposable", disposal.TargetMethod.ContainingType.ToDisplayString());

        Assert.True(SymbolEqualityComparer.Default.Equals(field,
            Assert.IsAssignableFrom<IFieldReferenceOperation>(disposal.Instance).Field));

        InvocationExpressionSyntax[] effects = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static call => call.Expression.ToString() == "System.IO.File.Delete").ToArray();

        Assert.Equal(2, effects.Length);

        InvocationExpressionSyntax selectedEffect = Assert.Single(effects,
            static effect => effect.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText == "SelectedSqliteCleanup");

        InvocationExpressionSyntax unselectedEffect = Assert.Single(effects,
            static effect => effect.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText == "UnselectedSqliteCleanup");

        IInvocationOperation selectedFile = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(selectedEffect));

        Assert.Equal("System.IO.File", selectedFile.TargetMethod.ContainingType.ToDisplayString());

        Assert.Empty(selectedFile.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, selectedFile.TargetMethod.ContainingAssembly);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        string selectedCoordinate = "/site@" + tree.FilePath + ":" + selectedEffect.SpanStart;

        string unselectedCoordinate = "/site@" + tree.FilePath + ":" + unselectedEffect.SpanStart;

        HostedProducerSite[] physical = result.Items.Where(site => site.Callee == "System.IO.File.Delete"
            && site.OperationId.EndsWith(selectedCoordinate, StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(physical);

        Assert.All(physical, static site =>
        {
            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        Assert.DoesNotContain(result.Items, site => site.Callee == "System.IO.File.Delete"
            && site.OperationId.EndsWith(unselectedCoordinate, StringComparison.Ordinal));

        foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.EndsWith(selectedCoordinate, StringComparison.Ordinal));
    }

    private static void AssertSqliteVirtualProviderMetadata(CSharpCompilation compilation, INamedTypeSymbol type)
    {
        Assert.Equal("Microsoft.Data.Sqlite.SqliteConnection", type.ToDisplayString());

        global::System.Reflection.AssemblyName trusted = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName();

        Assert.Equal(trusted.Name, type.ContainingAssembly.Identity.Name);

        Assert.Equal(trusted.Version, type.ContainingAssembly.Identity.Version);

        Assert.Equal(trusted.CultureName ?? string.Empty, type.ContainingAssembly.Identity.CultureName);

        Assert.Equal(Convert.ToHexString(trusted.GetPublicKeyToken() ?? []), Convert.ToHexString(type.ContainingAssembly.Identity.PublicKeyToken.AsSpan()));

        Assert.Empty(type.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, type.ContainingAssembly);
    }
}
