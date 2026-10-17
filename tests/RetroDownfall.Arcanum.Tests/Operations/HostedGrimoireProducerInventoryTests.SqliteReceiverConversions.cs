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
    public void DiscardedSqliteFactoryDispatchRequiresExactReceiver(bool unknown)
    {
        string source = R2Source("DiscardedSqliteFactory.Invoke(" + (unknown ? "UnprovedSqliteInput.Value" : "new Microsoft.Data.Sqlite.SqliteConnection()")
            + "); await Task.CompletedTask;", """
            static class UnprovedSqliteInput
            {
                internal static Microsoft.Data.Sqlite.SqliteConnection Value { get; set; } = null!;
            }

            static class DiscardedSqliteFactory
            {
                internal static void Invoke(Microsoft.Data.Sqlite.SqliteConnection connection)
                {
                    _ = connection.CreateCommand();
                }
            }
            """);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        MethodDeclarationSyntax helper = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "Invoke");

        IMethodSymbol helperSymbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(helper));

        Assert.True(helperSymbol.IsStatic && helperSymbol.ReturnsVoid);

        Assert.Equal(compilation.Assembly.Identity, helperSymbol.ContainingAssembly.Identity);

        IParameterSymbol parameter = Assert.Single(helperSymbol.Parameters);

        Assert.Equal(RefKind.None, parameter.RefKind);

        InvocationExpressionSyntax factorySyntax = Assert.Single(helper.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation factory = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(factorySyntax));

        AssertDiscardedSqliteFactoryMetadata(compilation, factory);

        Assert.True(SymbolEqualityComparer.Default.Equals(parameter,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(factory.Instance).Parameter));

        ISimpleAssignmentOperation discard = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(model.GetOperation(factorySyntax.Parent!));

        Assert.IsAssignableFrom<IDiscardOperation>(discard.Target);

        Assert.Empty(helper.DescendantNodes().OfType<UsingStatementSyntax>());

        Assert.Empty(helper.DescendantNodes().OfType<LocalDeclarationStatementSyntax>());

        InvocationExpressionSyntax callerSyntax = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "DiscardedSqliteFactory.Invoke");

        IInvocationOperation caller = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(callerSyntax));

        Assert.True(SymbolEqualityComparer.Default.Equals(helperSymbol, caller.TargetMethod));

        IArgumentOperation payload = Assert.Single(caller.Arguments);

        Assert.True(SymbolEqualityComparer.Default.Equals(parameter, payload.Parameter));

        Assert.True(payload.InConversion.IsIdentity && payload.OutConversion.IsIdentity);

        if (unknown)
        {
            IPropertyReferenceOperation input = Assert.IsAssignableFrom<IPropertyReferenceOperation>(payload.Value);

            Assert.Equal("UnprovedSqliteInput", input.Property.ContainingType.Name);

            Assert.True(input.Property.IsStatic);

            Assert.NotNull(input.Property.SetMethod);

            Assert.Equal(compilation.Assembly.Identity, input.Property.ContainingAssembly.Identity);

            Assert.True(SymbolEqualityComparer.Default.Equals(parameter.Type, input.Property.Type));
        }
        else
        {
            IObjectCreationOperation input = Assert.IsAssignableFrom<IObjectCreationOperation>(payload.Value);

            Assert.True(SymbolEqualityComparer.Default.Equals(parameter.Type, input.Type));

            Assert.Empty(input.Arguments);

            IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(input.Constructor);

            Assert.Empty(constructor.DeclaringSyntaxReferences);

            AssertMetadataReferenceOwns(compilation, constructor.ContainingAssembly);
        }

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Empty(result.Items);

        if (unknown)
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Identity == tree.FilePath + ":" + factorySyntax.SpanStart
                && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteConnection.CreateCommand");
        else
            Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SqliteReceiverConversionCannotBorrowTheStrippedAllocation(bool builtIn)
    {
        string receiver = builtIn ? "(Microsoft.Data.Sqlite.SqliteConnection)new EffectfulSqliteReceiver()"
            : "(Microsoft.Data.Sqlite.SqliteConnection)(SqliteReceiverWrapper)new BenignSqliteReceiver()";

        string source = R2Source("_ = (" + receiver + ").CreateCommand(); await Task.CompletedTask;", """
            sealed class BenignSqliteReceiver : Microsoft.Data.Sqlite.SqliteConnection
            {
                public override Microsoft.Data.Sqlite.SqliteCommand CreateCommand() => new Microsoft.Data.Sqlite.SqliteCommand();
            }

            sealed class SqliteReceiverWrapper
            {
                public static explicit operator SqliteReceiverWrapper(BenignSqliteReceiver value) => new SqliteReceiverWrapper();

                public static explicit operator Microsoft.Data.Sqlite.SqliteConnection(SqliteReceiverWrapper value) => new EffectfulSqliteReceiver();
            }

            sealed class EffectfulSqliteReceiver : Microsoft.Data.Sqlite.SqliteConnection
            {
                public override Microsoft.Data.Sqlite.SqliteCommand CreateCommand()
                {
                    System.IO.File.Delete("effective-sqlite-receiver");

                    return new Microsoft.Data.Sqlite.SqliteCommand();
                }
            }
            """);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax factorySyntax = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateCommand" });

        IInvocationOperation factory = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(factorySyntax));

        AssertDiscardedSqliteFactoryMetadata(compilation, factory);

        IConversionOperation outer = Assert.IsAssignableFrom<IConversionOperation>(factory.Instance);

        Assert.Equal("Microsoft.Data.Sqlite.SqliteConnection", outer.Type!.ToDisplayString());

        ClassDeclarationSyntax effectfulSyntax = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "EffectfulSqliteReceiver");

        INamedTypeSymbol effectfulType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(effectfulSyntax));

        Assert.True(effectfulType.IsSealed);

        Assert.Equal(compilation.Assembly.Identity, effectfulType.ContainingAssembly.Identity);

        MethodDeclarationSyntax overrideSyntax = Assert.Single(effectfulSyntax.Members.OfType<MethodDeclarationSyntax>());

        IMethodSymbol actualOverride = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(overrideSyntax));

        Assert.True(actualOverride.IsOverride);

        Assert.True(SymbolEqualityComparer.Default.Equals(factory.TargetMethod, actualOverride.OverriddenMethod));

        if (builtIn)
        {
            Assert.Null(outer.OperatorMethod);

            Assert.True(outer.Conversion.IsReference && !outer.Conversion.IsUserDefined);

            Assert.True(SymbolEqualityComparer.Default.Equals(effectfulType,
                Assert.IsAssignableFrom<IObjectCreationOperation>(outer.Operand).Type));
        }
        else
        {
            IMethodSymbol outerOperator = Assert.IsAssignableFrom<IMethodSymbol>(outer.OperatorMethod);

            Assert.Equal(MethodKind.Conversion, outerOperator.MethodKind);

            Assert.True(outerOperator.IsStatic);

            Assert.Equal("op_Explicit", outerOperator.Name);

            Assert.Equal(compilation.Assembly.Identity, outerOperator.ContainingAssembly.Identity);

            Assert.Equal("SqliteReceiverWrapper", outerOperator.ContainingType.Name);

            Assert.Equal("SqliteReceiverWrapper", Assert.Single(outerOperator.Parameters).Type.Name);

            Assert.True(SymbolEqualityComparer.Default.Equals(outer.Type, outerOperator.ReturnType));

            IConversionOperation inner = Assert.IsAssignableFrom<IConversionOperation>(outer.Operand);

            IMethodSymbol innerOperator = Assert.IsAssignableFrom<IMethodSymbol>(inner.OperatorMethod);

            Assert.Equal(MethodKind.Conversion, innerOperator.MethodKind);

            Assert.True(innerOperator.IsStatic);

            Assert.Equal("op_Explicit", innerOperator.Name);

            Assert.Equal(compilation.Assembly.Identity, innerOperator.ContainingAssembly.Identity);

            Assert.Equal("SqliteReceiverWrapper", innerOperator.ReturnType.Name);

            Assert.Equal("BenignSqliteReceiver", Assert.Single(innerOperator.Parameters).Type.Name);

            Assert.Equal("BenignSqliteReceiver", Assert.IsAssignableFrom<IObjectCreationOperation>(inner.Operand).Type!.Name);

            ConversionOperatorDeclarationSyntax outerBody = Assert.IsType<ConversionOperatorDeclarationSyntax>(Assert.Single(outerOperator.DeclaringSyntaxReferences).GetSyntax());

            IObjectCreationOperation actualReturn = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(outerBody.ExpressionBody!.Expression));

            Assert.True(SymbolEqualityComparer.Default.Equals(effectfulType, actualReturn.Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(effectfulType, actualReturn.Constructor!.ContainingType));
        }

        InvocationExpressionSyntax effectSyntax = Assert.Single(overrideSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation effect = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(effectSyntax));

        Assert.Equal("System.IO.File", effect.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", effect.TargetMethod.Name);

        Assert.Empty(effect.TargetMethod.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, effect.TargetMethod.ContainingAssembly);

        Assert.True(Assert.Single(actualOverride.DeclaringSyntaxReferences).Span.Contains(effectSyntax.Span));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        string consumerCoordinate = tree.FilePath + ":" + factorySyntax.SpanStart;

        if (!builtIn)
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Identity == consumerCoordinate
                && diagnostic.Detail == "Microsoft.Data.Sqlite.SqliteConnection.CreateCommand");

            return;
        }

        string effectCoordinate = "/site@" + tree.FilePath + ":" + effectSyntax.SpanStart;

        HostedProducerSite[] physical = result.Items.Where(site => site.Callee == "System.IO.File.Delete"
            && site.OperationId.EndsWith(effectCoordinate, StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(physical);

        Assert.All(physical, static site =>
        {
            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.EndsWith(effectCoordinate, StringComparison.Ordinal));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == consumerCoordinate);
    }

    private static void AssertDiscardedSqliteFactoryMetadata(CSharpCompilation compilation, IInvocationOperation invocation)
    {
        IMethodSymbol method = invocation.TargetMethod;

        Assert.True(invocation.IsVirtual && method.IsVirtual && !method.IsStatic);

        Assert.Equal("CreateCommand", method.Name);

        Assert.Empty(method.Parameters);

        Assert.Equal("Microsoft.Data.Sqlite.SqliteConnection", method.ContainingType.ToDisplayString());

        Assert.Equal("Microsoft.Data.Sqlite.SqliteCommand", method.ReturnType.ToDisplayString());

        Assert.Equal(method.ContainingAssembly.Identity, method.ReturnType.ContainingAssembly.Identity);

        global::System.Reflection.AssemblyName trusted = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName();

        Assert.Equal(trusted.Name, method.ContainingAssembly.Identity.Name);

        Assert.Equal(trusted.Version, method.ContainingAssembly.Identity.Version);

        Assert.Equal(trusted.CultureName ?? string.Empty, method.ContainingAssembly.Identity.CultureName);

        Assert.Equal(Convert.ToHexString(trusted.GetPublicKeyToken() ?? []), Convert.ToHexString(method.ContainingAssembly.Identity.PublicKeyToken.AsSpan()));

        Assert.Empty(method.DeclaringSyntaxReferences);

        AssertMetadataReferenceOwns(compilation, method.ContainingAssembly);
    }
}
