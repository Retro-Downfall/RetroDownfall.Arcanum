using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void AUsingAliasOfAnInheritedLeaseEndsThatOriginBeforeEarlierPhysicalCleanup()
    {
        string path = Path.Combine(TestRepositoryPaths.RepositoryRoot(),
            "src/RetroDownfall.Arcanum.Infrastructure/Coordination/RetainedExclusiveFileLock.cs");

        MethodDeclarationSyntax actualDispose = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)
            .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(static type => type.Identifier.ValueText == "RetainedExclusiveFileLock")
            .Members.OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Dispose");

        string helper = $$"""
            internal static class InheritedUsingAlias
            {
                internal static async Task RunAsync(
                    RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease borrowed,
                    CancellationToken token)
                {
                    using AliasPhysicalCleanup physical = new();

                    using RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease alias = borrowed;

                    await Task.Yield();

                    System.IO.File.Delete("inherited-alias-body");
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

        LocalDeclarationStatementSyntax[] owners = [.. run.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()];

        Assert.Equal(2, owners.Length);

        Assert.All(owners, static owner => Assert.NotEqual(0, owner.UsingKeyword.RawKind));

        Assert.Equal("physical", Assert.Single(owners[0].Declaration.Variables).Identifier.ValueText);

        VariableDeclaratorSyntax physical = Assert.Single(owners[0].Declaration.Variables);

        ILocalSymbol physicalLocal = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(physical));

        Assert.Equal("AliasPhysicalCleanup", physicalLocal.Type.ToDisplayString());

        IObjectCreationOperation created = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(physical.Initializer!.Value));

        Assert.True(SymbolEqualityComparer.Default.Equals(physicalLocal.Type, created.Type));

        VariableDeclaratorSyntax alias = Assert.Single(owners[1].Declaration.Variables);

        Assert.Equal("alias", alias.Identifier.ValueText);

        Assert.True(SymbolEqualityComparer.Default.Equals(runSymbol.Parameters[0], model.GetSymbolInfo(alias.Initializer!.Value).Symbol));

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

        // Only an inherited origin is present. Its later same-block using alias owns its teardown.
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        HostedProducerSite work = Assert.Single(result.Items, static site => site.Callee ==
            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite group = Assert.Single(result.Items, static site => site.Callee ==
            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        HostedProducerSite body = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal(work.CapsuleId, body.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, body.EffectFrontierCapsuleId);

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
