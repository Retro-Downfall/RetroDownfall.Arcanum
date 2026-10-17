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
    public void AsyncSqliteOpenerRetainsExactOrUnknownReceiverProvenance(bool unknown)
    {
        string source = R2Source(R2Admission + """
            System.IO.File.Delete("async-opener-anchor");
            await using Microsoft.Data.Sqlite.SqliteConnection connection = await AsyncSqliteOpener.OpenAsync(UnprovedAsyncSqliteInput.Value).ConfigureAwait(false);
            AsyncSqliteConsumer.Invoke(connection);
            """, """
            static class UnprovedAsyncSqliteInput
            {
                internal static Microsoft.Data.Sqlite.SqliteConnection Value { get; set; } = null!;
            }

            static class AsyncSqliteOpener
            {
                internal static async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenAsync(Microsoft.Data.Sqlite.SqliteConnection supplied)
                {
                    Microsoft.Data.Sqlite.SqliteConnection opened = INITIALIZER;

                    try
                    {
                        await Task.CompletedTask;

                        return opened;
                    }
                    catch
                    {
                        await opened.DisposeAsync().ConfigureAwait(false);

                        throw;
                    }
                }
            }

            static class AsyncSqliteConsumer
            {
                internal static void Invoke(Microsoft.Data.Sqlite.SqliteConnection connection)
                {
                    _ = connection.CreateCommand();
                }
            }

            sealed class PossibleAsyncSqliteOverride : Microsoft.Data.Sqlite.SqliteConnection
            {
                public override Microsoft.Data.Sqlite.SqliteCommand CreateCommand()
                {
                    System.IO.File.Delete("possible-async-sqlite-override");

                    return new Microsoft.Data.Sqlite.SqliteCommand();
                }
            }
            """.Replace("INITIALIZER", unknown ? "supplied" : "new Microsoft.Data.Sqlite.SqliteConnection(\"Data Source=:memory:\")", StringComparison.Ordinal));

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        MethodDeclarationSyntax openerSyntax = Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "OpenAsync");

        IMethodSymbol opener = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(openerSyntax));

        Assert.True(opener.IsStatic && opener.IsAsync);

        Assert.Equal(compilation.Assembly.Identity, opener.ContainingAssembly.Identity);

        IParameterSymbol supplied = Assert.Single(opener.Parameters);

        Assert.Equal(RefKind.None, supplied.RefKind);

        AssertBorrowedSqliteMetadata(compilation, Assert.IsAssignableFrom<INamedTypeSymbol>(supplied.Type), typeof(Microsoft.Data.Sqlite.SqliteConnection));

        INamedTypeSymbol task = Assert.IsAssignableFrom<INamedTypeSymbol>(opener.ReturnType);

        Assert.True(SymbolEqualityComparer.Default.Equals(compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1"), task.OriginalDefinition));

        Assert.True(SymbolEqualityComparer.Default.Equals(supplied.Type, Assert.Single(task.TypeArguments)));

        Assert.Empty(task.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, task.ContainingAssembly);

        VariableDeclaratorSyntax openedSyntax = Assert.Single(openerSyntax.DescendantNodes().OfType<VariableDeclaratorSyntax>());

        ILocalSymbol opened = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(openedSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(supplied.Type, opened.Type));

        IVariableInitializerOperation initializer = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(openedSyntax.Initializer!));

        if (unknown)
        {
            Assert.True(SymbolEqualityComparer.Default.Equals(supplied,
                Assert.IsAssignableFrom<IParameterReferenceOperation>(initializer.Value).Parameter));
        }
        else
        {
            IObjectCreationOperation allocation = Assert.IsAssignableFrom<IObjectCreationOperation>(initializer.Value);

            Assert.True(SymbolEqualityComparer.Default.Equals(supplied.Type, allocation.Type));

            Assert.Equal("Data Source=:memory:", Assert.Single(allocation.Arguments).Value.ConstantValue.Value);

            Assert.Null(Assert.Single(allocation.Arguments).InConversion.MethodSymbol);

            IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(allocation.Constructor);

            Assert.Empty(constructor.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, constructor.ContainingAssembly);
        }

        ReturnStatementSyntax returnSyntax = Assert.Single(openerSyntax.DescendantNodes().OfType<ReturnStatementSyntax>());

        IReturnOperation returned = Assert.IsAssignableFrom<IReturnOperation>(model.GetOperation(returnSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(opened,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(returned.ReturnedValue).Local));

        TryStatementSyntax attempt = Assert.Single(openerSyntax.DescendantNodes().OfType<TryStatementSyntax>());

        Assert.True(attempt.Block.Span.Contains(returnSyntax.Span));

        CatchClauseSyntax failure = Assert.Single(attempt.Catches);

        Assert.Null(Assert.Single(failure.DescendantNodes().OfType<ThrowStatementSyntax>()).Expression);

        Assert.Empty(openerSyntax.DescendantNodes().OfType<AssignmentExpressionSyntax>());

        Assert.DoesNotContain(openerSyntax.DescendantNodes().OfType<ArgumentSyntax>(), static argument => argument.RefKindKeyword.RawKind != 0);

        AwaitExpressionSyntax completionSyntax = Assert.Single(attempt.Block.DescendantNodes().OfType<AwaitExpressionSyntax>());

        IAwaitOperation completion = Assert.IsAssignableFrom<IAwaitOperation>(model.GetOperation(completionSyntax));

        IPropertyReferenceOperation completedTask = Assert.IsAssignableFrom<IPropertyReferenceOperation>(completion.Operation);

        Assert.Equal("CompletedTask", completedTask.Property.Name);

        Assert.Equal("System.Threading.Tasks.Task", completedTask.Property.ContainingType.ToDisplayString());

        Assert.Empty(completedTask.Property.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, completedTask.Property.ContainingAssembly);

        InvocationExpressionSyntax cleanupSyntax = Assert.Single(failure.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "opened.DisposeAsync");

        IInvocationOperation cleanup = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(cleanupSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(opened,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(cleanup.Instance).Local));

        Assert.Equal("DisposeAsync", cleanup.TargetMethod.Name);

        Assert.True(cleanup.TargetMethod.IsVirtual && !cleanup.TargetMethod.IsStatic);

        Assert.Equal("System.Threading.Tasks.ValueTask", cleanup.TargetMethod.ReturnType.ToDisplayString());

        Assert.Empty(cleanup.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, cleanup.TargetMethod.ContainingAssembly);

        VariableDeclaratorSyntax callerLocalSyntax = Assert.Single(syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "connection");

        ILocalSymbol callerLocal = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(callerLocalSyntax));

        LocalDeclarationStatementSyntax owned = Assert.IsType<LocalDeclarationStatementSyntax>(callerLocalSyntax.Parent!.Parent);

        Assert.Equal(SyntaxKind.AwaitKeyword, owned.AwaitKeyword.Kind());

        Assert.Equal(SyntaxKind.UsingKeyword, owned.UsingKeyword.Kind());

        IVariableInitializerOperation callerInitializer = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(callerLocalSyntax.Initializer!));

        IAwaitOperation awaited = Assert.IsAssignableFrom<IAwaitOperation>(callerInitializer.Value);

        Assert.True(SymbolEqualityComparer.Default.Equals(supplied.Type, awaited.Type));

        IInvocationOperation configured = Assert.IsAssignableFrom<IInvocationOperation>(awaited.Operation);

        Assert.Equal("ConfigureAwait", configured.TargetMethod.Name);

        Assert.True(SymbolEqualityComparer.Default.Equals(task, configured.TargetMethod.ContainingType));

        Assert.False(Assert.IsType<bool>(Assert.Single(configured.Arguments).Value.ConstantValue.Value));

        Assert.Empty(configured.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, configured.TargetMethod.ContainingAssembly);

        IInvocationOperation sourceCall = Assert.IsAssignableFrom<IInvocationOperation>(configured.Instance);

        Assert.True(SymbolEqualityComparer.Default.Equals(opener, sourceCall.TargetMethod));

        IArgumentOperation incoming = Assert.Single(sourceCall.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(supplied, incoming.Parameter));

        Assert.True(incoming.InConversion.IsIdentity && incoming.OutConversion.IsIdentity);

        IPropertyReferenceOperation unknownInput = Assert.IsAssignableFrom<IPropertyReferenceOperation>(incoming.Value);

        Assert.Equal("UnprovedAsyncSqliteInput", unknownInput.Property.ContainingType.Name);

        Assert.True(unknownInput.Property.IsStatic);

        Assert.NotNull(unknownInput.Property.SetMethod);

        Assert.Equal(compilation.Assembly.Identity, unknownInput.Property.ContainingAssembly.Identity);

        Assert.True(SymbolEqualityComparer.Default.Equals(supplied.Type, unknownInput.Property.Type));

        MethodDeclarationSyntax consumerSyntax = Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "Invoke");

        IMethodSymbol consumer = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(consumerSyntax));

        Assert.True(consumer.IsStatic && consumer.ReturnsVoid);

        Assert.Equal(compilation.Assembly.Identity, consumer.ContainingAssembly.Identity);

        IParameterSymbol formal = Assert.Single(consumer.Parameters);

        Assert.Equal(RefKind.None, formal.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(supplied.Type, formal.Type));

        InvocationExpressionSyntax factorySyntax = Assert.Single(consumerSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation factory = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(factorySyntax));

        AssertDiscardedSqliteFactoryMetadata(compilation, factory);

        Assert.True(SymbolEqualityComparer.Default.Equals(formal,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(factory.Instance).Parameter));

        Assert.IsAssignableFrom<IDiscardOperation>(Assert.IsAssignableFrom<ISimpleAssignmentOperation>(model.GetOperation(factorySyntax.Parent!)).Target);

        InvocationExpressionSyntax forwardingSyntax = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "AsyncSqliteConsumer.Invoke");

        IInvocationOperation forwarding = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(forwardingSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(consumer, forwarding.TargetMethod));

        IArgumentOperation borrowed = Assert.Single(forwarding.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(formal, borrowed.Parameter));

        Assert.True(SymbolEqualityComparer.Default.Equals(callerLocal, Assert.IsAssignableFrom<ILocalReferenceOperation>(borrowed.Value).Local));

        Assert.True(borrowed.InConversion.IsIdentity && borrowed.OutConversion.IsIdentity);

        Assert.True(callerLocalSyntax.SpanStart < forwardingSyntax.SpanStart);

        ClassDeclarationSyntax overrideTypeSyntax = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "PossibleAsyncSqliteOverride");

        INamedTypeSymbol overrideType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(overrideTypeSyntax));

        Assert.True(overrideType.IsSealed);

        Assert.Equal(compilation.Assembly.Identity, overrideType.ContainingAssembly.Identity);

        IMethodSymbol actualOverride = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(Assert.Single(overrideTypeSyntax.Members.OfType<MethodDeclarationSyntax>())));

        Assert.True(actualOverride.IsOverride);

        Assert.True(SymbolEqualityComparer.Default.Equals(factory.TargetMethod, actualOverride.OverriddenMethod));

        InvocationExpressionSyntax possibleEffect = Assert.Single(overrideTypeSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Delete");

        Assert.True(Assert.Single(actualOverride.DeclaringSyntaxReferences).Span.Contains(possibleEffect.Span));

        InvocationExpressionSyntax anchorSyntax = Assert.Single(owned.Parent!.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Delete");

        Assert.True(anchorSyntax.SpanStart < callerLocalSyntax.SpanStart);

        IInvocationOperation anchorOperation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(anchorSyntax));

        Assert.Equal("System.IO.File", anchorOperation.TargetMethod.ContainingType.ToDisplayString());

        Assert.Empty(anchorOperation.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, anchorOperation.TargetMethod.ContainingAssembly);

        Assert.True(SymbolEqualityComparer.Default.Equals(anchorOperation.TargetMethod,
            Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(possibleEffect)).TargetMethod));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        HostedProducerSite acquisition = Assert.Single(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite group = Assert.Single(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        string anchorCoordinate = "/site@" + tree.FilePath + ":" + anchorSyntax.SpanStart;

        HostedProducerSite anchor = Assert.Single(result.Items, site => site.Callee == "System.IO.File.Delete"
            && site.OperationId.EndsWith(anchorCoordinate, StringComparison.Ordinal));

        Assert.NotNull(acquisition.CapsuleId);

        Assert.NotNull(group.CapsuleId);

        Assert.Equal(acquisition.CapsuleId, anchor.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, anchor.EffectFrontierCapsuleId);

        if (unknown)
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Identity == tree.FilePath + ":" + factorySyntax.SpanStart
                && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteConnection.CreateCommand");
        else
        {
            string cleanupCoordinate = "/dispose@" + tree.FilePath + ":" + callerLocalSyntax.SpanStart;

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Identity.EndsWith(cleanupCoordinate, StringComparison.Ordinal));

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Identity == tree.FilePath + ":" + factorySyntax.SpanStart
                && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteConnection.CreateCommand");

            Assert.Empty(result.Diagnostics);
        }
    }
}
