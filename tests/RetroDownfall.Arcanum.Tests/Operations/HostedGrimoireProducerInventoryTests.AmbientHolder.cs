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
    // Ambient Holder Refusal. The lease travels by value, so a member that reads a lease out of a holder object has no business
    // holding the worker's Work: the holder's lease is ambient state, not the lease the worker minted and handed down. The refusal is
    // of the read. It does not depend on what the holder is called or on how it came by its lease: a binder named Bind, a binder named
    // anything else, no binder at all, a property setter, a constructor, a public field and a conditional access are all the same
    // read, and no witness lifts the refusal. Two things must hold when a send's member reads such a holder:
    //   - the send gets no Work, and says so with HOSTED_SITE_WORK_FRONTIER_MISSING at the send;
    //   - a group begun on the holder's lease is refused as HOSTED_ADMISSION_RECEIVER_MISMATCH, so it is nobody's Effect.
    // The lease-parameter row is the twin: the same chain, with every send on its lease formal, is granted in full and reports
    // nothing, so the refusals are about the holder read and not about the fixture. The second send never reads the holder in
    // any row, and is granted in all of them: the refusal is the reading member's, not the whole run's.
    [Theory]
    [InlineData("lease-parameter")]
    [InlineData("holder-read")]
    [InlineData("holder-group")]
    [InlineData("holder-read-renamed-binder")]
    [InlineData("holder-read-without-binder")]
    [InlineData("holder-read-property-setter")]
    [InlineData("holder-read-constructor-captured")]
    [InlineData("holder-read-field")]
    [InlineData("holder-read-conditional-access")]
    public void AmbientHolderIsRefused(string shape)
    {
        AmbientHolderRun run = RunAmbientHolder(shape);

        HostedProducerDiscovery<HostedProducerSite> result = run.Result;

        // The second send is begun on its lease formal in every row, and the worker's Work reaches it.
        InvocationExpressionSyntax secondGroupSyntax = Assert.Single(run.Groups(run.SecondSend));

        HostedProducerSite secondGroup = run.Site(secondGroupSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        HostedProducerSite second = run.Site(run.PhysicalSend(run.SecondSend), "System.IO.File.Delete");

        Assert.Equal(run.Acquired.CapsuleId, secondGroup.WorkFrontierCapsuleId);

        Assert.Equal(run.Acquired.CapsuleId, second.WorkFrontierCapsuleId);

        Assert.Equal(secondGroup.CapsuleId, second.EffectFrontierCapsuleId);

        InvocationExpressionSyntax physicalFirst = run.PhysicalSend(run.FirstSend);

        HostedProducerSite first = run.Site(physicalFirst, "System.IO.File.Delete");

        string firstCoordinate = "/site@" + run.Tree.FilePath + ":" + physicalFirst.SpanStart;

        if (shape == "lease-parameter")
        {
            InvocationExpressionSyntax firstGroupSyntax = Assert.Single(run.Groups(run.FirstSend));

            HostedProducerSite firstGroup = run.Site(firstGroupSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

            Assert.Equal(run.Acquired.CapsuleId, firstGroup.WorkFrontierCapsuleId);

            Assert.Equal(run.Acquired.CapsuleId, first.WorkFrontierCapsuleId);

            Assert.Equal(firstGroup.CapsuleId, first.EffectFrontierCapsuleId);

            Assert.NotEqual(first.EffectFrontierCapsuleId, second.EffectFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);

            return;
        }

        // A holder read drops the Work of the member that reads it, so the send has none.
        Assert.Null(first.WorkFrontierCapsuleId);

        Assert.Null(first.EffectFrontierCapsuleId);

        HostedProducerInventoryDiagnostic missingWork = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

        Assert.Equal("System.IO.File.Delete", missingWork.Detail);

        Assert.EndsWith(firstCoordinate, missingWork.Identity, StringComparison.Ordinal);

        HostedProducerInventoryDiagnostic missingEffect = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.EndsWith(firstCoordinate, missingEffect.Identity, StringComparison.Ordinal);

        if (shape != "holder-group")
        {
            // Reading the holder is enough: there is no group to refuse, and nothing else is reported.
            Assert.Empty(run.Groups(run.FirstSend));

            Assert.Equal(2, result.Diagnostics.Count);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ADMISSION_RECEIVER_MISMATCH");

            return;
        }

        // A group begun on the holder's lease is refused as a receiver that is no must-alias of the work lease, and grants nobody
        // an Effect: neither itself nor the send under it.
        InvocationExpressionSyntax holderGroupSyntax = Assert.Single(run.Groups(run.FirstSend));

        HostedProducerInventoryDiagnostic mismatch = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_RECEIVER_MISMATCH");

        Assert.EndsWith("/call@" + run.Tree.FilePath + ":" + holderGroupSyntax.SpanStart, mismatch.Identity, StringComparison.Ordinal);

        Assert.StartsWith("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup;", mismatch.Detail, StringComparison.Ordinal);

        HostedProducerSite holderGroup = run.Site(holderGroupSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        Assert.Null(holderGroup.WorkFrontierCapsuleId);

        Assert.Null(holderGroup.EffectFrontierCapsuleId);

        Assert.Equal(3, result.Diagnostics.Count);
    }

    private sealed record AmbientHolderRun(
        SyntaxTree Tree, SemanticModel Model, HostedProducerDiscovery<HostedProducerSite> Result, HostedProducerSite Acquired,
        MethodDeclarationSyntax FirstSend, MethodDeclarationSyntax SecondSend)
    {
        public InvocationExpressionSyntax[] Groups(MethodDeclarationSyntax send) => [.. send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => Model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryBeginExternalEffectGroup" })];

        public InvocationExpressionSyntax PhysicalSend(MethodDeclarationSyntax send) => Assert.Single(send.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Delete");

        public HostedProducerSite Site(SyntaxNode node, string callee) => Assert.Single(Result.Items, site => site.RootType == "Worker"
            && site.Callee == callee && site.OperationId.EndsWith("/site@" + Tree.FilePath + ":" + node.SpanStart, StringComparison.Ordinal));
    }

    private AmbientHolderRun RunAmbientHolder(string shape)
    {
        CSharpCompilation compilation = Compile(OrdinaryLeaseParameterFixture(shape));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

        Assert.Empty(hosted.Diagnostics);

        Assert.Contains("Worker", hosted.Items);

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], hosted, [new("Worker", [root])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"AMBIENT_HOLDER_DIAGNOSTIC shape={shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name && method.Parent is ClassDeclarationSyntax type && type.Identifier.ValueText == owner);

        InvocationExpressionSyntax acquisitionSyntax = Assert.Single(Method("Worker", "RunOneAsync").DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryAcquireWorkLease" });

        AmbientHolderRun run = new(tree, model, result, null!, Method("LongRestSweepCoordinator", "SendFirstAsync"), Method("LongRestSweepCoordinator", "SendSecondAsync"));

        HostedProducerSite acquired = run.Site(acquisitionSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquired.AdmittedWorkKind);

        return run with { Acquired = acquired };
    }
}
