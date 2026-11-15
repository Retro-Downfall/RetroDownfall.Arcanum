using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // This test uses no new field and compiles before the representation patch.
    // Its RED is the missing explicit v2 evidence, not an undefined-member error.
    [Fact]
    public void Registered_root_witness_has_a_versioned_manifest_column()
    {
        HostedProducerDiscovery<HostedProducerSite> discovered = DiscoverNestedAdmission("simple");

        string[] lines = HostedGrimoireProducerInventory.RenderCapsuleManifest(discovered.Items)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("# arcanum-hosted-producer-capsules v2", lines[0]);

        Assert.Equal("registered_root_work_frontier_capsule_sha256", lines[1].Split('\t')[13]);

        foreach (string row in lines.Skip(2))
        {
            Assert.Equal(14, row.Split('\t').Length);
        }
    }
}
