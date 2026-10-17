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
    public void AsyncSqliteOpenerCannotReuseAllocationAfterConstructorStoredCallbackMutation(bool mutates)
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
                    Microsoft.Data.Sqlite.SqliteConnection opened = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");

                    try
                    {
                        var setter = new AsyncSqliteSetter(() => { CALLBACK_BODY });

                        setter.Run();

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

            sealed class AsyncSqliteSetter
            {
                private readonly Action callback;

                public AsyncSqliteSetter(Action callback)
                {
                    this.callback = callback;
                }

                public void Run() => callback();
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
            """.Replace("CALLBACK_BODY", mutates ? "opened = supplied;" : "", StringComparison.Ordinal));

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

        VariableDeclaratorSyntax openedSyntax = Assert.Single(openerSyntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "opened");

        ILocalSymbol opened = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(openedSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(supplied.Type, opened.Type));

        IVariableInitializerOperation initializer = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(openedSyntax.Initializer!));

        IObjectCreationOperation allocation = Assert.IsAssignableFrom<IObjectCreationOperation>(initializer.Value);

        Assert.True(SymbolEqualityComparer.Default.Equals(supplied.Type, allocation.Type));

        Assert.Equal("Data Source=:memory:", Assert.Single(allocation.Arguments).Value.ConstantValue.Value);

        Assert.Null(Assert.Single(allocation.Arguments).InConversion.MethodSymbol);

        IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(allocation.Constructor);

        Assert.Empty(constructor.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, constructor.ContainingAssembly);

        ReturnStatementSyntax returnSyntax = Assert.Single(openerSyntax.DescendantNodes().OfType<ReturnStatementSyntax>());

        IReturnOperation returned = Assert.IsAssignableFrom<IReturnOperation>(model.GetOperation(returnSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(opened,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(returned.ReturnedValue).Local));

        TryStatementSyntax attempt = Assert.Single(openerSyntax.DescendantNodes().OfType<TryStatementSyntax>());

        Assert.True(attempt.Block.Span.Contains(returnSyntax.Span));

        CatchClauseSyntax failure = Assert.Single(attempt.Catches);

        Assert.Null(Assert.Single(failure.DescendantNodes().OfType<ThrowStatementSyntax>()).Expression);

        ParenthesizedLambdaExpressionSyntax callbackSyntax = Assert.Single(openerSyntax.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>());

        Assert.Empty(callbackSyntax.ParameterList.Parameters);

        Assert.IsType<BlockSyntax>(callbackSyntax.Body);

        AssignmentExpressionSyntax[] writes = openerSyntax.DescendantNodes().OfType<AssignmentExpressionSyntax>().ToArray();

        Assert.Equal(mutates ? 1 : 0, writes.Length);

        if (mutates)
        {
            AssignmentExpressionSyntax write = Assert.Single(writes);

            Assert.True(callbackSyntax.Body.Span.Contains(write.Span));

            ISimpleAssignmentOperation mutation = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(model.GetOperation(write));

            Assert.True(SymbolEqualityComparer.Default.Equals(opened,
                Assert.IsAssignableFrom<ILocalReferenceOperation>(mutation.Target).Local));

            Assert.True(SymbolEqualityComparer.Default.Equals(supplied,
                Assert.IsAssignableFrom<IParameterReferenceOperation>(mutation.Value).Parameter));

            Assert.True(SymbolEqualityComparer.Default.Equals(opener, opened.ContainingSymbol));

            Assert.True(model.GetConversion(write.Right).IsIdentity);
        }
        else Assert.Empty(Assert.IsType<BlockSyntax>(callbackSyntax.Body).Statements);

        VariableDeclaratorSyntax setterSyntax = Assert.Single(openerSyntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "setter");

        ILocalSymbol setter = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(setterSyntax));

        IObjectCreationOperation storage = Assert.IsAssignableFrom<IObjectCreationOperation>(
            Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(setterSyntax.Initializer!)).Value);

        IMethodSymbol storageConstructor = Assert.IsAssignableFrom<IMethodSymbol>(storage.Constructor);

        Assert.Equal(compilation.Assembly.Identity, storageConstructor.ContainingAssembly.Identity);

        IParameterSymbol callbackParameter = Assert.Single(storageConstructor.Parameters);

        Assert.Equal(RefKind.None, callbackParameter.RefKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(compilation.GetTypeByMetadataName("System.Action"), callbackParameter.Type));

        Assert.Empty(callbackParameter.Type.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, callbackParameter.Type.ContainingAssembly);

        IArgumentOperation storedArgument = Assert.Single(storage.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(callbackParameter, storedArgument.Parameter));

        IDelegateCreationOperation capture = Assert.IsAssignableFrom<IDelegateCreationOperation>(storedArgument.Value);

        IAnonymousFunctionOperation callback = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(capture.Target);

        Assert.Equal(callbackSyntax.Span, callback.Syntax.Span);

        Assert.True(storedArgument.InConversion.IsIdentity && storedArgument.OutConversion.IsIdentity);

        ConstructorDeclarationSyntax storageSyntax = Assert.IsType<ConstructorDeclarationSyntax>(
            Assert.Single(storageConstructor.DeclaringSyntaxReferences).GetSyntax());

        ISimpleAssignmentOperation store = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(model.GetOperation(
            Assert.Single(storageSyntax.DescendantNodes().OfType<AssignmentExpressionSyntax>())));

        IFieldReferenceOperation storedField = Assert.IsAssignableFrom<IFieldReferenceOperation>(store.Target);

        Assert.True(storedField.Field.IsReadOnly && !storedField.Field.IsStatic);

        Assert.Equal(Accessibility.Private, storedField.Field.DeclaredAccessibility);

        Assert.True(SymbolEqualityComparer.Default.Equals(callbackParameter.Type, storedField.Field.Type));

        Assert.IsAssignableFrom<IInstanceReferenceOperation>(storedField.Instance);

        Assert.True(SymbolEqualityComparer.Default.Equals(callbackParameter,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(store.Value).Parameter));

        InvocationExpressionSyntax runSyntax = Assert.Single(openerSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "setter.Run");

        IInvocationOperation run = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(runSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(setter, Assert.IsAssignableFrom<ILocalReferenceOperation>(run.Instance).Local));

        Assert.Equal(MethodKind.Ordinary, run.TargetMethod.MethodKind);

        Assert.True(run.TargetMethod.ReturnsVoid && !run.TargetMethod.IsStatic);

        Assert.Empty(run.Arguments);

        MethodDeclarationSyntax runner = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(run.TargetMethod.DeclaringSyntaxReferences).GetSyntax());

        IInvocationOperation invoke = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(
            Assert.Single(runner.DescendantNodes().OfType<InvocationExpressionSyntax>())));

        Assert.Equal(MethodKind.DelegateInvoke, invoke.TargetMethod.MethodKind);

        Assert.True(SymbolEqualityComparer.Default.Equals(callbackParameter.Type, invoke.TargetMethod.ContainingType));

        Assert.True(SymbolEqualityComparer.Default.Equals(storedField.Field,
            Assert.IsAssignableFrom<IFieldReferenceOperation>(invoke.Instance).Field));

        Assert.True(openedSyntax.Span.End < setterSyntax.SpanStart && setterSyntax.Span.End < runSyntax.SpanStart);

        Assert.True(runSyntax.Span.End < returnSyntax.SpanStart);

        Assert.DoesNotContain(openerSyntax.DescendantNodes().OfType<ArgumentSyntax>(), static argument => argument.RefKindKeyword.RawKind != 0);

        AwaitExpressionSyntax completionSyntax = Assert.Single(attempt.Block.DescendantNodes().OfType<AwaitExpressionSyntax>());

        Assert.True(runSyntax.Span.End < completionSyntax.SpanStart && completionSyntax.Span.End < returnSyntax.SpanStart);

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

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
            or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

        HostedProducerSite acquisition = Assert.Single(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite group = Assert.Single(result.Items, static site => site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        string anchorCoordinate = "/site@" + tree.FilePath + ":" + anchorSyntax.SpanStart;

        HostedProducerSite anchor = Assert.Single(result.Items, site => site.Callee == "System.IO.File.Delete"
            && site.OperationId.EndsWith(anchorCoordinate, StringComparison.Ordinal));

        Assert.NotNull(acquisition.CapsuleId);

        Assert.NotNull(group.CapsuleId);

        Assert.Equal(acquisition.CapsuleId, anchor.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, anchor.EffectFrontierCapsuleId);

        output.WriteLine("ASYNC_SQLITE_CONSTRUCTOR_CAPTURE_PREMISES mutates=" + mutates + " passed");

        foreach (var diagnostic in result.Diagnostics)
            output.WriteLine(diagnostic.Code + "|" + diagnostic.Identity + "|" + diagnostic.Detail);

        if (mutates)
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Identity == tree.FilePath + ":" + factorySyntax.SpanStart
                && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteConnection.CreateCommand");
        else
        {
            string cleanupCoordinate = "/dispose@" + tree.FilePath + ":" + callerLocalSyntax.SpanStart;

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Identity.EndsWith(cleanupCoordinate, StringComparison.Ordinal));

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Identity == tree.FilePath + ":" + factorySyntax.SpanStart);

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Identity == tree.FilePath + ":" + forwardingSyntax.SpanStart
                || diagnostic.Identity.EndsWith("/call@" + tree.FilePath + ":" + forwardingSyntax.SpanStart, StringComparison.Ordinal));

            // Ordinary callback traversal has separate ownership obligations.
            // Require this consumer boundary and its cleanup to close even when
            // unrelated callback-runner diagnostics remain; they cannot satisfy
            // the mutating row's exact own-CreateCommand refusal assertion.
        }
    }
}
