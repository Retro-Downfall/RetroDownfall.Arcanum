using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("simple")]
    [InlineData("null-guard")]
    [InlineData("formal-handoff")]
    public void Existing_owned_work_preserves_an_explicit_root_witness(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> discovered = DiscoverNestedAdmission(shape);

        HostedProducerSite send = Assert.Single(discovered.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.NotNull(send.WorkFrontierCapsuleId);

        Assert.Equal(send.WorkFrontierCapsuleId, send.RegisteredRootWorkFrontierCapsuleId);

        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            HostedGrimoireProducerInventory.RenderCapsuleManifest(discovered.Items)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Empty(parsed.Diagnostics);

        Assert.Equal(send.RegisteredRootWorkFrontierCapsuleId,
            Assert.Single(parsed.Sites, static site => site.Callee == "System.IO.File.Delete")
                .RegisteredRootWorkFrontierCapsuleId);
    }

    [Fact]
    public void Root_witness_is_part_of_site_identity_and_cannot_be_recovered_from_operation_text()
    {
        HostedProducerDiscovery<HostedProducerSite> discovered = DiscoverNestedAdmission("simple");

        HostedProducerSite send = Assert.Single(discovered.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.NotNull(send.RegisteredRootWorkFrontierCapsuleId);

        HostedProducerSite missing = send with { RegisteredRootWorkFrontierCapsuleId = null };

        Assert.NotEqual(HostedGrimoireProducerInventory.StableSiteIdentity(send),
            HostedGrimoireProducerInventory.StableSiteIdentity(missing));

        Assert.Contains(RootWitnessCatalog(discovered.Items.Select(site => site == send ? missing : site).ToArray()).Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData("valid", false)]
    [InlineData("wrong-root-kind", true)]
    [InlineData("missing-root", true)]
    [InlineData("missing-physical", true)]
    public void Catalog_checks_registered_root_and_physical_admission_independently(string shape, bool refused)
    {
        // This is a catalog/serialization contract fixture, not a claim that the current
        // source selector already supports a distinct-kind nested acquisition.
        HostedProducerDiscovery<HostedProducerSite> discovered = DiscoverNestedAdmission("simple");

        const string acquire = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease";

        HostedProducerSite outer = Assert.Single(discovered.Items,
            static site => site.Callee == acquire && site.Member == "StartAsync");

        HostedProducerSite inner = Assert.Single(discovered.Items,
            static site => site.Callee == acquire && site.Member == "Nested");

        HostedProducerSite[] independent = discovered.Items.Select(site =>
        {
            HostedProducerSite result = site;

            if (result.RegisteredRootWorkFrontierCapsuleId == inner.CapsuleId)
            {
                result = result with { RegisteredRootWorkFrontierCapsuleId = outer.CapsuleId };
            }

            if (site == inner)
            {
                result = result with { AdmittedWorkKind = GrimoireWorkKind.GuildStorage };
            }

            if (site == outer && shape == "wrong-root-kind")
            {
                result = result with { AdmittedWorkKind = GrimoireWorkKind.BatchProcessing };
            }

            if (site.Callee == "System.IO.File.Delete" && shape == "missing-root")
            {
                result = result with { RegisteredRootWorkFrontierCapsuleId = null };
            }

            return result;
        }).Where(site => shape != "missing-physical" || site.CapsuleId != inner.CapsuleId).ToArray();

        HostedProducerInventoryValidation validation = RootWitnessCatalog(independent);

        Assert.Equal(refused, validation.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"));

        if (shape == "missing-root")
        {
            HostedProducerCapsuleManifest missingRoot = HostedGrimoireProducerInventory.ParseCapsuleManifest(
                HostedGrimoireProducerInventory.RenderCapsuleManifest(independent)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries));

            Assert.Empty(missingRoot.Diagnostics);

            Assert.Contains(RootWitnessCatalog(missingRoot.Sites).Diagnostics, static diagnostic =>
                diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
        }

        if (!refused)
        {
            HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
                HostedGrimoireProducerInventory.RenderCapsuleManifest(independent)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries));

            Assert.Empty(parsed.Diagnostics);

            HostedProducerSite send = Assert.Single(parsed.Sites, static site => site.Callee == "System.IO.File.Delete");

            Assert.Equal(inner.CapsuleId, send.WorkFrontierCapsuleId);

            Assert.Equal(outer.CapsuleId, send.RegisteredRootWorkFrontierCapsuleId);
        }
    }

    [Fact]
    public void Manifest_refuses_an_unresolved_registered_root_capsule()
    {
        HostedProducerDiscovery<HostedProducerSite> discovered = DiscoverNestedAdmission("simple");

        HostedProducerSite send = Assert.Single(discovered.Items, static site => site.Callee == "System.IO.File.Delete");

        HostedProducerSite forged = send with { RegisteredRootWorkFrontierCapsuleId = new string('A', 64) };

        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            HostedGrimoireProducerInventory.RenderCapsuleManifest(discovered.Items.Select(site => site == send ? forged : site))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(parsed.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED");
    }

    private static HostedProducerInventoryValidation RootWitnessCatalog(IReadOnlyList<HostedProducerSite> sites) =>
        HostedGrimoireProducerInventory.Validate(
            [new("Worker", [OrdinaryRoot() with { Sites = sites }])],
            [],
            new(["Worker"], []),
            new(sites, []));
}
