using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("declaration")]
    [InlineData("expression")]
    [InlineData("unbraced-declaration")]
    [InlineData("unbraced-expression")]
    public void AnInnerUsingAliasEndsInheritedWorkBeforeItsEnclosingUsingStatementCleanup(string shape)
    {
        string path = Path.Combine(TestRepositoryPaths.RepositoryRoot(),
            "src/RetroDownfall.Arcanum.Infrastructure/Coordination/RetainedExclusiveFileLock.cs");

        MethodDeclarationSyntax actualDispose = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)
            .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(static type => type.Identifier.ValueText == "RetainedExclusiveFileLock")
            .Members.OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Dispose");

        const string aliasType = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease";

        const string bodyEffect = "await Task.Yield(); System.IO.File.Delete(\"inherited-alias-body\");";

        string body = shape switch
        {
            "declaration" => "using (AliasPhysicalCleanup physical = new()) { using " + aliasType + " alias = borrowed; " + bodyEffect + " }",
            "expression" => "AliasPhysicalCleanup physical = new(); using (physical) { using " + aliasType + " alias = borrowed; " + bodyEffect + " }",
            "unbraced-declaration" => "using (AliasPhysicalCleanup physical = new()) using (" + aliasType + " alias = borrowed) { " + bodyEffect + " }",
            "unbraced-expression" => aliasType + " alias = borrowed; using (AliasPhysicalCleanup physical = new()) using (alias) { " + bodyEffect + " }",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        string helper = $$"""
            internal static class InheritedUsingAlias
            {
                internal static async Task RunAsync(
                    RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease borrowed,
                    CancellationToken token)
                {
                    {{body}}
                }
            }

            internal sealed class AliasPhysicalCleanup : IDisposable
            {
                private FileStream? _stream = new FileStream(
                    "inherited-alias-handle", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                {{actualDispose.ToFullString()}}
            }
            """;

        string source = "#nullable enable\nusing System.IO;\n" + R2Source(
            R2Admission + " await InheritedUsingAlias.RunAsync(lease, token);", helper);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode syntax = compilation.SyntaxTrees.Single().GetRoot();

        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        MethodDeclarationSyntax run = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "RunAsync");

        IMethodSymbol runSymbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(run));

        Assert.Equal(RefKind.None, runSymbol.Parameters[0].RefKind);

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", runSymbol.Parameters[0].Type.ToDisplayString());

        UsingStatementSyntax[] statements = [.. run.DescendantNodes().OfType<UsingStatementSyntax>()];

        Assert.Equal(shape.StartsWith("unbraced-", StringComparison.Ordinal) ? 2 : 1, statements.Length);

        UsingStatementSyntax outer = statements[0];

        VariableDeclarationSyntax physicalDeclaration = shape == "expression"
            ? Assert.Single(run.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()).Declaration
            : Assert.IsType<VariableDeclarationSyntax>(outer.Declaration);

        VariableDeclaratorSyntax physical = Assert.Single(physicalDeclaration.Variables);

        Assert.Equal("physical", physical.Identifier.ValueText);

        ILocalSymbol physicalLocal = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(physical));

        Assert.Equal("AliasPhysicalCleanup", physicalLocal.Type.ToDisplayString());

        IObjectCreationOperation created = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(physical.Initializer!.Value));

        Assert.True(SymbolEqualityComparer.Default.Equals(physicalLocal.Type, created.Type));

        VariableDeclarationSyntax aliasDeclaration;

        int aliasDisposalPosition;

        if (shape.StartsWith("unbraced-", StringComparison.Ordinal))
        {
            Assert.Same(statements[1], outer.Statement);

            aliasDeclaration = shape == "unbraced-declaration"
                ? Assert.IsType<VariableDeclarationSyntax>(statements[1].Declaration)
                : Assert.Single(run.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()).Declaration;

            aliasDisposalPosition = statements[1].Statement.Span.End - 1;
        }
        else
        {
            BlockSyntax block = Assert.IsType<BlockSyntax>(outer.Statement);

            LocalDeclarationStatementSyntax aliasOwner = Assert.Single(block.Statements.OfType<LocalDeclarationStatementSyntax>());

            Assert.NotEqual(0, aliasOwner.UsingKeyword.RawKind);

            aliasDeclaration = aliasOwner.Declaration;

            aliasDisposalPosition = block.CloseBraceToken.SpanStart;
        }

        VariableDeclaratorSyntax alias = Assert.Single(aliasDeclaration.Variables);

        Assert.Equal("alias", alias.Identifier.ValueText);

        Assert.True(SymbolEqualityComparer.Default.Equals(runSymbol.Parameters[0], model.GetSymbolInfo(alias.Initializer!.Value).Symbol));

        if (shape == "unbraced-expression")
        {
            Assert.Null(statements[1].Declaration);

            Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(alias), model.GetSymbolInfo(statements[1].Expression!).Symbol));

            Assert.Same(outer.Statement, statements[1]);
        }
        else
        {
            Assert.Contains(outer.Statement, aliasDeclaration.Ancestors());
        }

        // The existing dispatcher gives both lifetimes this coordinate. The actual
        // inner owner has nevertheless unwound before the enclosing resource.
        Assert.Equal(aliasDisposalPosition, outer.Statement.Span.End - 1);

        if (shape == "expression")
        {
            Assert.Null(outer.Declaration);

            Assert.True(SymbolEqualityComparer.Default.Equals(physicalLocal, model.GetSymbolInfo(outer.Expression!).Symbol));
        }

        MethodDeclarationSyntax cleanupMethod = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(static type => type.Identifier.ValueText == "AliasPhysicalCleanup")
            .Members.OfType<MethodDeclarationSyntax>().Single();

        Assert.True(SyntaxFactory.AreEquivalent(actualDispose, cleanupMethod));

        IInvocationOperation dispose = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(
            Assert.Single(cleanupMethod.DescendantNodes().OfType<InvocationExpressionSyntax>())));

        Assert.Equal("System.IO.Stream", dispose.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(typeof(Stream).Assembly.GetName().Name, dispose.TargetMethod.ContainingAssembly.Name);

        Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "gate.TryAcquireWorkLease");

        Assert.DoesNotContain(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString().Contains("TryAcquireWorkLease", StringComparison.Ordinal)
                || call.Expression.ToString().Contains("TryBeginExternalEffectGroup", StringComparison.Ordinal));

        // Only an inherited origin is present. The inner owner ends it before outer cleanup.
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        HostedProducerSite work = Assert.Single(result.Items, static site => site.Callee ==
            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite group = Assert.Single(result.Items, static site => site.Callee ==
            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        HostedProducerSite bodySite = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal(work.CapsuleId, bodySite.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, bodySite.EffectFrontierCapsuleId);

        HostedProducerSite cleanup = Assert.Single(result.Items, static site => site.EnclosingType == "AliasPhysicalCleanup"
            && site.Member == "Dispose" && site.Callee == "System.IO.Stream.Dispose");

        Assert.Null(cleanup.WorkFrontierCapsuleId);

        Assert.Null(cleanup.RegisteredRootWorkFrontierCapsuleId);

        // The independent group handle is still live; it cannot supply missing Work.
        Assert.Equal(group.CapsuleId, cleanup.EffectFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.Stream.Dispose");
    }
}
