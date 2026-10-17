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
    public void RepeatedSqliteConsumerPreservesActualReceiverConversionSource(bool retargeted)
    {
        string second = retargeted ? "(Microsoft.Data.Sqlite.SqliteConnection)(RepeatedSqliteWrapper)shared" : "shared";

        string source = R2Source("var shared = new Microsoft.Data.Sqlite.SqliteConnection(); RepeatedSqliteConsumer.Invoke(shared); "
            + "RepeatedSqliteConsumer.Invoke(" + second + "); await Task.CompletedTask;", """
            static class RepeatedSqliteConsumer
            {
                internal static void Invoke(Microsoft.Data.Sqlite.SqliteConnection connection)
                {
                    _ = connection.CreateCommand();
                }
            }

            sealed class RepeatedSqliteWrapper
            {
                public static explicit operator RepeatedSqliteWrapper(Microsoft.Data.Sqlite.SqliteConnection value) => new RepeatedSqliteWrapper();

                public static explicit operator Microsoft.Data.Sqlite.SqliteConnection(RepeatedSqliteWrapper value) => new RepeatedEffectfulSqlite();
            }

            sealed class RepeatedEffectfulSqlite : Microsoft.Data.Sqlite.SqliteConnection
            {
                public override Microsoft.Data.Sqlite.SqliteCommand CreateCommand()
                {
                    System.IO.File.Delete("repeated-actual-sqlite-receiver");

                    return new Microsoft.Data.Sqlite.SqliteCommand();
                }
            }
            """);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax[] calls = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static call => call.Expression.ToString() == "RepeatedSqliteConsumer.Invoke")
            .OrderBy(static call => call.SpanStart).ToArray();

        Assert.Equal(2, calls.Length);

        IInvocationOperation firstCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[0]));

        IInvocationOperation secondCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(calls[1]));

        Assert.True(SymbolEqualityComparer.Default.Equals(firstCall.TargetMethod, secondCall.TargetMethod));

        Assert.True(firstCall.TargetMethod.IsStatic && firstCall.TargetMethod.ReturnsVoid);

        Assert.Equal(compilation.Assembly.Identity, firstCall.TargetMethod.ContainingAssembly.Identity);

        IParameterSymbol formal = Assert.Single(firstCall.TargetMethod.Parameters);

        Assert.Equal(RefKind.None, formal.RefKind);

        IArgumentOperation firstArgument = Assert.Single(firstCall.Arguments);

        IArgumentOperation secondArgument = Assert.Single(secondCall.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(formal, firstArgument.Parameter));

        Assert.True(SymbolEqualityComparer.Default.Equals(formal, secondArgument.Parameter));

        Assert.True(firstArgument.InConversion.IsIdentity && secondArgument.InConversion.IsIdentity);

        ILocalSymbol shared = Assert.IsAssignableFrom<ILocalReferenceOperation>(firstArgument.Value).Local;

        VariableDeclaratorSyntax sharedDeclaration = Assert.IsType<VariableDeclaratorSyntax>(Assert.Single(shared.DeclaringSyntaxReferences).GetSyntax());

        IObjectCreationOperation sharedCreation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(sharedDeclaration.Initializer!.Value));

        Assert.True(SymbolEqualityComparer.Default.Equals(formal.Type, sharedCreation.Type));

        IMethodSymbol sharedConstructor = Assert.IsAssignableFrom<IMethodSymbol>(sharedCreation.Constructor);

        Assert.Empty(sharedConstructor.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, sharedConstructor.ContainingAssembly);

        MethodDeclarationSyntax consumer = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(firstCall.TargetMethod.DeclaringSyntaxReferences).GetSyntax());

        InvocationExpressionSyntax factorySyntax = Assert.Single(consumer.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation factory = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(factorySyntax));

        AssertDiscardedSqliteFactoryMetadata(compilation, factory);

        Assert.True(SymbolEqualityComparer.Default.Equals(formal,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(factory.Instance).Parameter));

        if (retargeted)
        {
            IConversionOperation outer = Assert.IsAssignableFrom<IConversionOperation>(secondArgument.Value);

            IConversionOperation inner = Assert.IsAssignableFrom<IConversionOperation>(outer.Operand);

            Assert.True(SymbolEqualityComparer.Default.Equals(shared,
                Assert.IsAssignableFrom<ILocalReferenceOperation>(inner.Operand).Local));

            foreach (IConversionOperation conversion in new[] { inner, outer })
            {
                IMethodSymbol selected = Assert.IsAssignableFrom<IMethodSymbol>(conversion.OperatorMethod);

                Assert.Equal(MethodKind.Conversion, selected.MethodKind);

                Assert.Equal("op_Explicit", selected.Name);

                Assert.True(selected.IsStatic && conversion.Conversion.IsUserDefined);

                Assert.Equal("RepeatedSqliteWrapper", selected.ContainingType.Name);

                Assert.Equal(compilation.Assembly.Identity, selected.ContainingAssembly.Identity);

                Assert.True(SymbolEqualityComparer.Default.Equals(conversion.Type, selected.ReturnType));

                Assert.True(SymbolEqualityComparer.Default.Equals(conversion.Operand.Type, Assert.Single(selected.Parameters).Type));
            }

            ConversionOperatorDeclarationSyntax outerDeclaration = Assert.IsType<ConversionOperatorDeclarationSyntax>(
                Assert.Single(outer.OperatorMethod!.DeclaringSyntaxReferences).GetSyntax());

            IObjectCreationOperation returned = Assert.IsAssignableFrom<IObjectCreationOperation>(
                model.GetOperation(outerDeclaration.ExpressionBody!.Expression));

            INamedTypeSymbol effectful = Assert.IsAssignableFrom<INamedTypeSymbol>(returned.Type);

            Assert.True(effectful.IsSealed);

            Assert.Equal("RepeatedEffectfulSqlite", effectful.Name);

            IMethodSymbol actual = Assert.Single(effectful.GetMembers("CreateCommand").OfType<IMethodSymbol>());

            Assert.True(actual.IsOverride);

            Assert.True(SymbolEqualityComparer.Default.Equals(factory.TargetMethod, actual.OverriddenMethod));
        }
        else
        {
            Assert.True(SymbolEqualityComparer.Default.Equals(shared,
                Assert.IsAssignableFrom<ILocalReferenceOperation>(secondArgument.Value).Local));
        }

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (retargeted)
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Identity == tree.FilePath + ":" + factorySyntax.SpanStart
                && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteConnection.CreateCommand");
        else
        {
            Assert.Empty(result.Items);

            Assert.Empty(result.Diagnostics);
        }
    }
}
