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
    // holding the worker's Work: the holder's lease is ambient state, not the lease the worker minted and handed down. The refusal
    // is kept for good, even after the holder is gone from production, because a holder is the shape a future change would
    // reach for. Two things must hold when a send's member reads a holder:
    //   - the send gets no Work, and says so with HOSTED_SITE_WORK_FRONTIER_MISSING at the send;
    //   - a group begun on the holder's lease is refused as HOSTED_ADMISSION_RECEIVER_MISMATCH, so it is nobody's Effect.
    // The lease-parameter row is the twin: the same chain, with every send on its lease formal, is granted in full and reports
    // nothing, so the two refusals are about the holder read and not about the fixture. The second send never reads the holder in
    // any row, and is granted in all three: the refusal is the reading member's, not the whole run's.
    [Theory]
    [InlineData("lease-parameter")]
    [InlineData("holder-read")]
    [InlineData("holder-group")]
    public void AmbientHolderIsRefused(string shape)
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

        MethodDeclarationSyntax firstSend = Method("LongRestSweepCoordinator", "SendFirstAsync");

        MethodDeclarationSyntax secondSend = Method("LongRestSweepCoordinator", "SendSecondAsync");

        InvocationExpressionSyntax acquisitionSyntax = Assert.Single(Method("Worker", "RunOneAsync").DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryAcquireWorkLease" });

        InvocationExpressionSyntax[] Groups(MethodDeclarationSyntax send) => [.. send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryBeginExternalEffectGroup" })];

        InvocationExpressionSyntax PhysicalSend(MethodDeclarationSyntax send) => Assert.Single(send.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Delete");

        HostedProducerSite Site(SyntaxNode node, string callee) => Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.Callee == callee && site.OperationId.EndsWith("/site@" + tree.FilePath + ":" + node.SpanStart, StringComparison.Ordinal));

        HostedProducerSite acquired = Site(acquisitionSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquired.AdmittedWorkKind);

        // The second send is begun on its lease formal in every row, and the worker's Work reaches it.
        InvocationExpressionSyntax secondGroupSyntax = Assert.Single(Groups(secondSend));

        HostedProducerSite secondGroup = Site(secondGroupSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        HostedProducerSite second = Site(PhysicalSend(secondSend), "System.IO.File.Delete");

        Assert.Equal(acquired.CapsuleId, secondGroup.WorkFrontierCapsuleId);

        Assert.Equal(acquired.CapsuleId, second.WorkFrontierCapsuleId);

        Assert.Equal(secondGroup.CapsuleId, second.EffectFrontierCapsuleId);

        InvocationExpressionSyntax physicalFirst = PhysicalSend(firstSend);

        HostedProducerSite first = Site(physicalFirst, "System.IO.File.Delete");

        string firstCoordinate = "/site@" + tree.FilePath + ":" + physicalFirst.SpanStart;

        if (shape == "lease-parameter")
        {
            InvocationExpressionSyntax firstGroupSyntax = Assert.Single(Groups(firstSend));

            HostedProducerSite firstGroup = Site(firstGroupSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

            Assert.Equal(acquired.CapsuleId, firstGroup.WorkFrontierCapsuleId);

            Assert.Equal(acquired.CapsuleId, first.WorkFrontierCapsuleId);

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

        if (shape == "holder-read")
        {
            // Reading the holder is enough: there is no group to refuse, and nothing else is reported.
            Assert.Empty(Groups(firstSend));

            Assert.Equal(2, result.Diagnostics.Count);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ADMISSION_RECEIVER_MISMATCH");

            return;
        }

        // A group begun on the holder's lease is refused as a receiver that is no must-alias of the work lease, and grants nobody
        // an Effect: neither itself nor the send under it.
        InvocationExpressionSyntax holderGroupSyntax = Assert.Single(Groups(firstSend));

        HostedProducerInventoryDiagnostic mismatch = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_RECEIVER_MISMATCH");

        Assert.EndsWith("/call@" + tree.FilePath + ":" + holderGroupSyntax.SpanStart, mismatch.Identity, StringComparison.Ordinal);

        Assert.StartsWith("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup;", mismatch.Detail, StringComparison.Ordinal);

        HostedProducerSite holderGroup = Site(holderGroupSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        Assert.Null(holderGroup.WorkFrontierCapsuleId);

        Assert.Null(holderGroup.EffectFrontierCapsuleId);

        Assert.Equal(3, result.Diagnostics.Count);
    }
}
