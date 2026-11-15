using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Proposal: preserve the actual PreReadinessStartup shape with Effect but no
    // current Work/root. This is structural evidence, not ordinary authority.
    [Fact]
    public void Nonordinary_effect_references_with_no_Work_or_root_remain_strictly_structural()
    {
        HostedProducerOperationEntry operation = OrdinaryRoot() with
        {
            Authority = HostedProducerAuthorityKind.PreReadinessStartup,
            WorkKind = null,
            Proof = "Worker.StartAsync: awaited startup reconciliation",
        };

        HostedProducerSite group = new("Worker", operation.OperationId, "src/Worker.cs", "Worker", "StartAsync",
            HostedProducerSiteKind.EffectFrontier,
            "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup",
            operation.OperationId, "M:Worker.StartAsync", 0, new string('A', 64));

        group = group with { EffectFrontierCapsuleId = group.CapsuleId };

        HostedProducerSite consumer = group with
        {
            SourcePath = "src/Journal.cs",
            EnclosingType = "Fixture.Journal",
            Member = "FlushWorking",
            MemberIdentity = "M:Fixture.Journal.FlushWorking",
            Kind = HostedProducerSiteKind.FileSystemEffect,
            Callee = "System.IO.FileStream.Flush",
            SyntaxFingerprint = new string('B', 64),
        };

        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            HostedGrimoireProducerInventory.RenderCapsuleManifest([group, consumer]).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Empty(parsed.Diagnostics);

        Assert.All(parsed.Sites, static site =>
        {
            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Null(site.RegisteredRootWorkFrontierCapsuleId);
        });

        operation = operation with { Sites = parsed.Sites };

        HostedProducerInventoryValidation validation = HostedGrimoireProducerInventory.Validate(
            [new("Worker", [operation])], [], new(["Worker"], []), new(parsed.Sites, []));

        Assert.Empty(validation.Diagnostics);

        HostedProducerOperationEntry ordinary = OrdinaryRoot() with { Sites = parsed.Sites };

        HostedProducerInventoryValidation ordinaryValidation = HostedGrimoireProducerInventory.Validate(
            [new("Worker", [ordinary])], [], new(["Worker"], []), new(parsed.Sites, []));

        Assert.Contains(ordinaryValidation.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }
}
