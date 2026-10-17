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
    [InlineData("exact")]
    [InlineData("unknown")]
    [InlineData("authored-override")]
    public void NullableBorrowedSqliteFieldRetainsOnlyExactProviderCleanup(string shape)
    {
        string input = shape switch
        {
            "unknown" => "Microsoft.Data.Sqlite.SqliteConnection? borrowed = UnknownBorrowedConnection.Value; ",
            "authored-override" => "await using var owned = new AuthoredBorrowedConnection(); Microsoft.Data.Sqlite.SqliteConnection? borrowed = owned; ",
            _ => "await using var owned = new Microsoft.Data.Sqlite.SqliteConnection(); Microsoft.Data.Sqlite.SqliteConnection? borrowed = owned; ",
        };

        string source = R2Source((shape == "authored-override" ? "" : R2Admission) + input
            + "if (NullableBorrowedRecovery.Create(borrowed) is not { } recovery) return; await recovery.CommitAsync(token);", """
            internal static class UnknownBorrowedConnection
            {
                internal static Microsoft.Data.Sqlite.SqliteConnection? Value { get; set; }

                internal static Microsoft.Data.Sqlite.SqliteCommand? Command { get; set; }
            }

            internal sealed class AuthoredBorrowedConnection : Microsoft.Data.Sqlite.SqliteConnection
            {
                public override Microsoft.Data.Sqlite.SqliteCommand CreateCommand()
                {
                    System.IO.File.Delete("borrowed-connection-override");

                    return UnknownBorrowedConnection.Command!;
                }
            }

            internal sealed class NullableBorrowedRecovery
            {
                private readonly Microsoft.Data.Sqlite.SqliteConnection? _committedDatabase;

                private NullableBorrowedRecovery(Microsoft.Data.Sqlite.SqliteConnection? committedDatabase = null)
                {
                    _committedDatabase = committedDatabase;
                }

                internal static NullableBorrowedRecovery? Create(Microsoft.Data.Sqlite.SqliteConnection? committedDatabase)
                {
                    if (committedDatabase is null) return null;

                    return new NullableBorrowedRecovery(committedDatabase);
                }

                internal async Task CommitAsync(CancellationToken token)
                {
                    if (_committedDatabase is null) return;

                    await BorrowedSanitation.VerifyAsync(_committedDatabase, token);
                }
            }

            internal static class BorrowedSanitation
            {
                internal static async Task VerifyAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken token)
                {
                    await using System.Data.Common.DbCommand command = connection.CreateCommand();

                    await using Microsoft.Data.Sqlite.SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

                    command.Transaction = transaction;

                    await command.ExecuteNonQueryAsync(token);

                    await transaction.RollbackAsync(token);
                }
            }
            """);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax recoveryType = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "NullableBorrowedRecovery");

        INamedTypeSymbol recoverySymbol = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(recoveryType));

        Assert.Equal(compilation.Assembly.Identity, recoverySymbol.ContainingAssembly.Identity);

        ConstructorDeclarationSyntax constructorSyntax = Assert.Single(recoveryType.Members.OfType<ConstructorDeclarationSyntax>());

        IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(constructorSyntax));

        IParameterSymbol constructorParameter = Assert.Single(constructor.Parameters);

        Assert.Equal("committedDatabase", constructorParameter.Name);

        Assert.True(constructorParameter.IsOptional && constructorParameter.HasExplicitDefaultValue);

        Assert.Null(constructorParameter.ExplicitDefaultValue);

        Assert.Equal(NullableAnnotation.Annotated, constructorParameter.NullableAnnotation);

        Assert.Equal(RefKind.None, constructorParameter.RefKind);

        VariableDeclaratorSyntax fieldSyntax = Assert.Single(recoveryType.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(static declaration => declaration.Declaration.Variables));

        IFieldSymbol field = Assert.IsAssignableFrom<IFieldSymbol>(model.GetDeclaredSymbol(fieldSyntax));

        Assert.Equal("_committedDatabase", field.Name);

        Assert.True(field.IsReadOnly && !field.IsStatic);

        Assert.Equal(Accessibility.Private, field.DeclaredAccessibility);

        Assert.Equal(NullableAnnotation.Annotated, field.NullableAnnotation);

        Assert.True(SymbolEqualityComparer.Default.Equals(constructorParameter.Type, field.Type));

        AssignmentExpressionSyntax assignmentSyntax = Assert.Single(constructorSyntax.DescendantNodes().OfType<AssignmentExpressionSyntax>());

        ISimpleAssignmentOperation assignment = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(model.GetOperation(assignmentSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(field,
            Assert.IsAssignableFrom<IFieldReferenceOperation>(assignment.Target).Field));

        Assert.IsAssignableFrom<IInstanceReferenceOperation>(Assert.IsAssignableFrom<IFieldReferenceOperation>(assignment.Target).Instance);

        Assert.True(SymbolEqualityComparer.Default.Equals(constructorParameter,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(assignment.Value).Parameter));

        MethodDeclarationSyntax factorySyntax = Assert.Single(recoveryType.Members.OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "Create");

        IMethodSymbol factory = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(factorySyntax));

        Assert.True(factory.IsStatic);

        Assert.Equal(NullableAnnotation.Annotated, factory.ReturnNullableAnnotation);

        Assert.True(SymbolEqualityComparer.Default.Equals(recoverySymbol, factory.ReturnType));

        IObjectCreationOperation creation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(
            Assert.Single(factorySyntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())));

        Assert.True(SymbolEqualityComparer.Default.Equals(constructor, creation.Constructor));

        IArgumentOperation constructorArgument = Assert.Single(creation.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(constructorParameter, constructorArgument.Parameter));

        Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(factory.Parameters),
            Assert.IsAssignableFrom<IParameterReferenceOperation>(constructorArgument.Value).Parameter));

        InvocationExpressionSyntax factoryCallSyntax = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "NullableBorrowedRecovery.Create");

        IInvocationOperation factoryCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(factoryCallSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(factory, factoryCall.TargetMethod));

        ILocalReferenceOperation factoryInput = Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(factoryCall.Arguments).Value);

        Assert.Equal("borrowed", factoryInput.Local.Name);

        Assert.True(SymbolEqualityComparer.Default.Equals(field.Type, factoryInput.Local.Type));

        VariableDeclaratorSyntax borrowedSyntax = Assert.Single(syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "borrowed");

        Assert.True(SymbolEqualityComparer.Default.Equals(factoryInput.Local, model.GetDeclaredSymbol(borrowedSyntax)));

        if (shape != "unknown")
        {
            IVariableInitializerOperation borrowedInitialization = Assert.IsAssignableFrom<IVariableInitializerOperation>(model.GetOperation(borrowedSyntax.Initializer!));

            IOperation value = borrowedInitialization.Value;

            if (value is IConversionOperation conversion)
            {
                Assert.Null(conversion.OperatorMethod);

                Assert.True(conversion.Conversion.IsReference || conversion.Conversion.IsIdentity);

                value = conversion.Operand;
            }

            ILocalReferenceOperation ownedReference = Assert.IsAssignableFrom<ILocalReferenceOperation>(value);

            VariableDeclaratorSyntax ownedVariable = Assert.Single(syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                static variable => variable.Identifier.ValueText == "owned");

            Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(ownedVariable), ownedReference.Local));
        }

        MethodDeclarationSyntax commitSyntax = Assert.Single(recoveryType.Members.OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "CommitAsync");

        IfStatementSyntax guard = Assert.Single(commitSyntax.DescendantNodes().OfType<IfStatementSyntax>());

        IIsPatternOperation condition = Assert.IsAssignableFrom<IIsPatternOperation>(model.GetOperation(guard.Condition));

        Assert.True(SymbolEqualityComparer.Default.Equals(field,
            Assert.IsAssignableFrom<IFieldReferenceOperation>(condition.Value).Field));

        IConstantPatternOperation nullPattern = Assert.IsAssignableFrom<IConstantPatternOperation>(condition.Pattern);

        Assert.True(nullPattern.Value.ConstantValue.HasValue);

        Assert.Null(nullPattern.Value.ConstantValue.Value);

        Assert.IsType<ReturnStatementSyntax>(guard.Statement);

        InvocationExpressionSyntax verifySyntax = Assert.Single(commitSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation verify = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(verifySyntax));

        Assert.True(guard.Span.End < verifySyntax.SpanStart);

        Assert.True(verify.TargetMethod.IsStatic);

        Assert.Equal("BorrowedSanitation", verify.TargetMethod.ContainingType.Name);

        Assert.Equal(compilation.Assembly.Identity, verify.TargetMethod.ContainingAssembly.Identity);

        Assert.True(SymbolEqualityComparer.Default.Equals(field,
            Assert.IsAssignableFrom<IFieldReferenceOperation>(verify.Arguments[0].Value).Field));

        IParameterSymbol receiverParameter = verify.TargetMethod.Parameters[0];

        Assert.Equal(RefKind.None, receiverParameter.RefKind);

        AssertBorrowedSqliteMetadata(compilation, Assert.IsAssignableFrom<INamedTypeSymbol>(receiverParameter.Type), typeof(Microsoft.Data.Sqlite.SqliteConnection));

        MethodDeclarationSyntax sanitationSyntax = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(verify.TargetMethod.DeclaringSyntaxReferences).GetSyntax());

        VariableDeclaratorSyntax commandSyntax = Assert.Single(sanitationSyntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "command");

        VariableDeclaratorSyntax transactionSyntax = Assert.Single(sanitationSyntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "transaction");

        foreach (VariableDeclaratorSyntax resource in new[] { commandSyntax, transactionSyntax })
        {
            LocalDeclarationStatementSyntax declaration = Assert.IsType<LocalDeclarationStatementSyntax>(resource.Parent!.Parent);

            Assert.True(declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword));

            Assert.True(declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword));

            IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(resource.Initializer!.Value));

            Assert.True(SymbolEqualityComparer.Default.Equals(receiverParameter,
                Assert.IsAssignableFrom<IParameterReferenceOperation>(selected.Instance).Parameter));

            AssertBorrowedSqliteMetadata(compilation, selected.TargetMethod.ContainingType, typeof(Microsoft.Data.Sqlite.SqliteConnection));

            Assert.Equal(resource == commandSyntax ? "CreateCommand" : "BeginTransaction", selected.TargetMethod.Name);

            Assert.Equal(resource == commandSyntax ? "Microsoft.Data.Sqlite.SqliteCommand" : "Microsoft.Data.Sqlite.SqliteTransaction",
                selected.TargetMethod.ReturnType.ToDisplayString());

            ILocalSymbol local = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(resource));

            string cleanupOwner = resource == commandSyntax ? "System.Data.Common.DbCommand" : "System.Data.Common.DbTransaction";

            INamedTypeSymbol cleanupType = Assert.IsAssignableFrom<INamedTypeSymbol>(local.Type);

            IMethodSymbol? cleanup = null;

            for (INamedTypeSymbol? current = cleanupType; current is not null && cleanup is null; current = current.BaseType)
                cleanup = current.GetMembers("DisposeAsync").OfType<IMethodSymbol>().SingleOrDefault(static method => method.Parameters.IsEmpty);

            Assert.NotNull(cleanup);

            Assert.Equal(cleanupOwner, cleanup.ContainingType.ToDisplayString());

            Assert.True(cleanup.IsVirtual && !cleanup.IsStatic);

            Assert.Equal("System.Threading.Tasks.ValueTask", cleanup.ReturnType.ToDisplayString());

            Assert.Empty(cleanup.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, cleanup.ContainingAssembly);
        }

        if (shape != "unknown")
        {
            ObjectCreationExpressionSyntax ownedSyntax = Assert.Single(syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
                static expression => expression.Type.ToString() is "Microsoft.Data.Sqlite.SqliteConnection" or "AuthoredBorrowedConnection");

            IObjectCreationOperation owned = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(ownedSyntax));

            Assert.Equal(shape == "exact" ? "Microsoft.Data.Sqlite.SqliteConnection" : "AuthoredBorrowedConnection", owned.Type!.ToDisplayString());

            if (shape == "exact")
                AssertBorrowedSqliteMetadata(compilation, owned.Constructor!.ContainingType, typeof(Microsoft.Data.Sqlite.SqliteConnection));
            else
                Assert.Equal(compilation.Assembly.Identity, owned.Constructor!.ContainingAssembly.Identity);
        }
        else
        {
            MemberAccessExpressionSyntax unknownSyntax = Assert.Single(syntax.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
                static expression => expression.ToString() == "UnknownBorrowedConnection.Value");

            IPropertyReferenceOperation unknown = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(unknownSyntax));

            Assert.Equal(compilation.Assembly.Identity, unknown.Property.ContainingAssembly.Identity);

            Assert.NotNull(unknown.Property.SetMethod);

            Assert.True(SymbolEqualityComparer.Default.Equals(field.Type, unknown.Property.Type));
        }

        InvocationExpressionSyntax executionSyntax = Assert.Single(sanitationSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "command.ExecuteNonQueryAsync");

        IInvocationOperation execution = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(executionSyntax));

        Assert.Equal("System.Data.Common.DbCommand", execution.TargetMethod.ContainingType.ToDisplayString());

        Assert.Empty(execution.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, execution.TargetMethod.ContainingAssembly);

        InvocationExpressionSyntax? effectSyntax = null;

        if (shape == "authored-override")
        {
            ClassDeclarationSyntax subclass = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
                static type => type.Identifier.ValueText == "AuthoredBorrowedConnection");

            IMethodSymbol authored = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(Assert.Single(subclass.Members.OfType<MethodDeclarationSyntax>())));

            Assert.True(authored.IsOverride);

            Assert.Equal("CreateCommand", authored.Name);

            Assert.Equal(compilation.Assembly.Identity, authored.ContainingAssembly.Identity);

            IMethodSymbol overridden = Assert.IsAssignableFrom<IMethodSymbol>(authored.OverriddenMethod);

            AssertBorrowedSqliteMetadata(compilation, overridden.ContainingType, typeof(Microsoft.Data.Sqlite.SqliteConnection));

            Assert.True(overridden.IsVirtual);

            effectSyntax = Assert.Single(subclass.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static call => call.Expression.ToString() == "System.IO.File.Delete");

            Assert.True(Assert.Single(authored.DeclaringSyntaxReferences).Span.Contains(effectSyntax.Span));

            IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

            Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

            Assert.Empty(effect.TargetMethod.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);
        }

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        foreach (VariableDeclaratorSyntax resource in new[] { commandSyntax, transactionSyntax })
        {
            string coordinate = "/dispose@" + tree.FilePath + ":" + resource.SpanStart;

            string detail = resource == commandSyntax ? "System.Data.Common.DbCommand.DisposeAsync;" : "Microsoft.Data.Sqlite.SqliteTransaction.DisposeAsync;";

            if (shape == "unknown" || shape == "authored-override" && resource == commandSyntax)
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                    && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal)
                    && diagnostic.Detail.StartsWith(detail, StringComparison.Ordinal));
            else if (shape == "exact")
                Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                    && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
        }

        string executionCoordinate = "/site@" + tree.FilePath + ":" + executionSyntax.SpanStart;

        HostedProducerSite[] physicalSql = result.Items.Where(site => site.Callee == "System.Data.Common.DbCommand.ExecuteNonQueryAsync"
            && site.OperationId.EndsWith(executionCoordinate, StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(physicalSql);

        if (shape == "exact")
        {
            Assert.All(physicalSql, static site => Assert.NotNull(site.WorkFrontierCapsuleId));

            Assert.Empty(result.Diagnostics);
        }

        if (effectSyntax is not null)
        {
            string coordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

            HostedProducerSite[] physical = result.Items.Where(site => site.Callee == "System.IO.File.Delete"
                && site.OperationId.EndsWith(coordinate, StringComparison.Ordinal)).ToArray();

            Assert.NotEmpty(physical);

            Assert.All(physical, static site =>
            {
                Assert.Null(site.WorkFrontierCapsuleId);

                Assert.Null(site.EffectFrontierCapsuleId);
            });

            foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == "System.IO.File.Delete"
                    && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
        }
    }

    private static void AssertBorrowedSqliteMetadata(CSharpCompilation compilation, INamedTypeSymbol type, Type expected)
    {
        Assert.Equal(expected.FullName, type.ToDisplayString());

        global::System.Reflection.AssemblyName trusted = expected.Assembly.GetName();

        Assert.Equal(trusted.Name, type.ContainingAssembly.Identity.Name);

        Assert.Equal(trusted.Version, type.ContainingAssembly.Identity.Version);

        Assert.Equal(trusted.CultureName ?? string.Empty, type.ContainingAssembly.Identity.CultureName);

        Assert.Equal(Convert.ToHexString(trusted.GetPublicKeyToken() ?? []), Convert.ToHexString(type.ContainingAssembly.Identity.PublicKeyToken.AsSpan()));

        Assert.Empty(type.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, type.ContainingAssembly);
    }
}
