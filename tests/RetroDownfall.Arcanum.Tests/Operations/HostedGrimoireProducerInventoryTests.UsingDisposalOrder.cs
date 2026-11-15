using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("early-outer-pair")]
    [InlineData("late-inner-pair")]
    [InlineData("early-no-outer-effect")]
    [InlineData("wrong-live-group")]
    public void SameBlockUsingCleanupRetainsOnlyThePairStillLiveAtItsReverseDisposal(string shape)
    {
        string path = Path.Combine(TestRepositoryPaths.RepositoryRoot(),
            "src/RetroDownfall.Arcanum.Infrastructure/Coordination/RetainedExclusiveFileLock.cs");

        MethodDeclarationSyntax actualDispose = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)
            .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(static type => type.Identifier.ValueText == "RetainedExclusiveFileLock")
            .Members.OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Dispose");

        Assert.Equal("void", actualDispose.ReturnType.ToString());

        InvocationExpressionSyntax actualPhysicalDispose = Assert.Single(
            actualDispose.DescendantNodes().OfType<InvocationExpressionSyntax>());

        Assert.Equal("stream.Dispose()", actualPhysicalDispose.ToString());

        bool early = shape.StartsWith("early-", StringComparison.Ordinal);

        bool outerEffect = shape != "early-no-outer-effect";

        bool innerEffect = shape != "wrong-live-group";

        const string physicalDeclaration = "using RetainedCleanup physical = new();";

        string helper = $$"""
            internal static class ReverseUsingFixture
            {
                internal static async Task RunAsync(
                    RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate,
                    CancellationToken token)
                {
                    {{(early ? physicalDeclaration : "")}}

                    if (!gate.TryAcquireWorkLease(
                        RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing,
                        out var acquired)) return;

                    using var innerLease = acquired;

                    {{(innerEffect ? "if (!innerLease.TryBeginExternalEffectGroup(out var innerGroup)) return; using var innerHeld = innerGroup;" : "")}}

                    {{(early ? "" : physicalDeclaration)}}

                    await Task.Yield();

                    System.IO.File.Delete("reverse-using-body-effect");
                }
            }

            internal sealed class RetainedCleanup : IDisposable
            {
                private FileStream? _stream = new FileStream(
                    "reverse-using-retained-handle", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                {{actualDispose.ToFullString()}}
            }
            """;

        string outerAdmission = outerEffect
            ? R2Admission
            : AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal);

        string source = "#nullable enable\nusing System.IO;\n" + R2Source(
            outerAdmission + " await ReverseUsingFixture.RunAsync(gate, token);", helper);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode syntax = compilation.SyntaxTrees.Single().GetRoot();

        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        ClassDeclarationSyntax cleanupTypeSyntax = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(static type => type.Identifier.ValueText == "RetainedCleanup");

        MethodDeclarationSyntax copiedDispose = Assert.Single(cleanupTypeSyntax.Members.OfType<MethodDeclarationSyntax>());

        Assert.True(SyntaxFactory.AreEquivalent(actualDispose, copiedDispose));

        INamedTypeSymbol cleanupType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(cleanupTypeSyntax));

        INamedTypeSymbol disposable = compilation.GetTypeByMetadataName("System.IDisposable")!;

        IMethodSymbol disposeContract = Assert.Single(disposable.GetMembers("Dispose").OfType<IMethodSymbol>());

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(copiedDispose),
            cleanupType.FindImplementationForInterfaceMember(disposeContract)));

        InvocationExpressionSyntax physicalDispose = Assert.Single(copiedDispose.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation selectedDispose = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(physicalDispose));

        Assert.Equal("System.IO.Stream", selectedDispose.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(typeof(Stream).Assembly.GetName().Name, selectedDispose.TargetMethod.ContainingAssembly.Name);

        ObjectCreationExpressionSyntax handleCreation = Assert.Single(cleanupTypeSyntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>());

        IObjectCreationOperation selectedHandle = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(handleCreation));

        Assert.Equal("System.IO.FileStream", selectedHandle.Constructor!.ContainingType.ToDisplayString());

        Assert.Equal(typeof(FileStream).Assembly.GetName().Name, selectedHandle.Constructor.ContainingAssembly.Name);

        MethodDeclarationSyntax run = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "RunAsync");

        LocalDeclarationStatementSyntax physical = run.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()
            .Single(static statement => statement.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "physical"));

        LocalDeclarationStatementSyntax innerLease = run.Body.Statements.OfType<LocalDeclarationStatementSyntax>()
            .Single(static statement => statement.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "innerLease"));

        Assert.NotEqual(0, physical.UsingKeyword.RawKind);

        Assert.NotEqual(0, innerLease.UsingKeyword.RawKind);

        Assert.Same(physical.Parent, innerLease.Parent);

        Assert.Equal(early, physical.SpanStart < innerLease.SpanStart);

        ILocalSymbol physicalLocal = Assert.IsAssignableFrom<ILocalSymbol>(
            model.GetDeclaredSymbol(Assert.Single(physical.Declaration.Variables)));

        Assert.True(SymbolEqualityComparer.Default.Equals(cleanupType, physicalLocal.Type));

        InvocationExpressionSyntax innerAcquire = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "gate.TryAcquireWorkLease");

        IInvocationOperation selectedAcquire = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(innerAcquire));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate",
            selectedAcquire.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("TryAcquireWorkLease", selectedAcquire.TargetMethod.Name);

        Assert.Equal(2, selectedAcquire.TargetMethod.Parameters.Length);

        Assert.Equal(RefKind.Out, selectedAcquire.TargetMethod.Parameters[1].RefKind);

        Assert.Same(run.Body, innerAcquire.Ancestors().OfType<IfStatementSyntax>().First().Parent);

        Assert.Equal(early, physical.SpanStart < innerAcquire.SpanStart);

        ISymbol acquiredLocal = model.GetDeclaredSymbol(Assert.Single(innerAcquire.DescendantNodes().OfType<SingleVariableDesignationSyntax>()))!;

        VariableDeclaratorSyntax innerLeaseVariable = Assert.Single(innerLease.Declaration.Variables);

        ILocalSymbol innerLeaseLocal = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(innerLeaseVariable));

        Assert.True(SymbolEqualityComparer.Default.Equals(acquiredLocal,
            model.GetSymbolInfo(innerLeaseVariable.Initializer!.Value).Symbol));

        InvocationExpressionSyntax[] selectedGroupCalls = [.. run.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static call => call.Expression.ToString() == "innerLease.TryBeginExternalEffectGroup")];

        Assert.Equal(innerEffect ? 1 : 0, selectedGroupCalls.Length);

        if (innerEffect)
        {
            IInvocationOperation selectedGroup = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(selectedGroupCalls[0]));

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease",
                selectedGroup.TargetMethod.ContainingType.ToDisplayString());

            ILocalReferenceOperation receiver = Assert.IsAssignableFrom<ILocalReferenceOperation>(selectedGroup.Instance);

            Assert.True(SymbolEqualityComparer.Default.Equals(innerLeaseLocal, receiver.Local));

            LocalDeclarationStatementSyntax held = run.Body.Statements.OfType<LocalDeclarationStatementSyntax>()
                .Single(static statement => statement.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "innerHeld"));

            Assert.NotEqual(0, held.UsingKeyword.RawKind);

            Assert.Same(physical.Parent, held.Parent);

            Assert.Equal(early, physical.SpanStart < held.SpanStart);
        }

        // All source, symbol and scope premises above must pass before RED frontier assertions.
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        const string acquireCallee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease";

        const string groupCallee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup";

        HostedProducerSite outerWork = Assert.Single(result.Items, static site => site.EnclosingType == "Worker" && site.Callee == acquireCallee);

        HostedProducerSite innerWork = Assert.Single(result.Items, static site => site.EnclosingType == "ReverseUsingFixture" && site.Callee == acquireCallee);

        HostedProducerSite? outerGroup = outerEffect
            ? Assert.Single(result.Items, static site => site.EnclosingType == "Worker" && site.Callee == groupCallee)
            : null;

        HostedProducerSite? innerGroup = innerEffect
            ? Assert.Single(result.Items, static site => site.EnclosingType == "ReverseUsingFixture" && site.Callee == groupCallee)
            : null;

        HostedProducerSite cleanup = Assert.Single(result.Items, static site => site.EnclosingType == "RetainedCleanup"
            && site.Member == "Dispose" && site.Callee == "System.IO.Stream.Dispose");

        HostedProducerSite body = Assert.Single(result.Items, static site => site.EnclosingType == "ReverseUsingFixture"
            && site.Callee == "System.IO.File.Delete");

        Assert.NotEqual(outerWork.CapsuleId, innerWork.CapsuleId);

        Assert.Equal(innerWork.CapsuleId, body.WorkFrontierCapsuleId);

        if (innerEffect) Assert.Equal(innerGroup!.CapsuleId, body.EffectFrontierCapsuleId);

        Assert.Equal(early ? outerWork.CapsuleId : innerWork.CapsuleId, cleanup.WorkFrontierCapsuleId);

        string? expectedEffect = shape switch
        {
            "early-outer-pair" => outerGroup!.CapsuleId,
            "late-inner-pair" => innerGroup!.CapsuleId,
            _ => null,
        };

        Assert.Equal(expectedEffect, cleanup.EffectFrontierCapsuleId);

        Assert.Equal(shape == "wrong-live-group", result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == HostedGrimoireProducerInventory.EffectLeaseMismatchCode
                && diagnostic.Detail.StartsWith("System.IO.Stream.Dispose;", StringComparison.Ordinal)));

        Assert.Equal(shape is "early-no-outer-effect" or "wrong-live-group", result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.Stream.Dispose"));
    }
}
