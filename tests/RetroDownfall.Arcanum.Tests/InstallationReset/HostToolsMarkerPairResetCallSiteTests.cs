using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>Source-level guards on the reset coordinator's narrow Campaign authority.</summary>
public sealed class HostToolsMarkerPairResetCallSiteTests
{

    private const string CoordinatorPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "HostToolsMarkerPairResetCoordinator.cs";

    [Fact]
    public void Coordinator_is_the_only_production_caller_of_full_reset_inventory()
    {

        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        ProductionSource coordinator = Assert.Single(
            sources,
            static source => source.IsExactOwner(CoordinatorPath));

        Assert.True(coordinator.Names(".InventoryFullInstallationResetCleanupAsync("));

        Assert.True(coordinator.Names(".RevalidateFullInstallationResetInventoryAsync("));

        string[] unauthorizedInventoryCallers =
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(CoordinatorPath)
                    && source.Names(".InventoryFullInstallationResetCleanupAsync("))
                .Select(static source => source.RelativePath),
        ];

        string[] unauthorizedRevalidationCallers =
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(CoordinatorPath)
                    && source.Names(".RevalidateFullInstallationResetInventoryAsync("))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            unauthorizedInventoryCallers.Length == 0,
            "Only the authenticated pair coordinator may request the initial full-reset Campaign "
            + "inventory: "
            + string.Join(", ", unauthorizedInventoryCallers));

        Assert.True(
            unauthorizedRevalidationCallers.Length == 0,
            "Only the authenticated pair coordinator may request full-reset Campaign inventory "
            + "revalidation: "
            + string.Join(", ", unauthorizedRevalidationCallers));

    }

    [Fact]
    public void Coordinator_does_not_resolve_campaign_codec_opener_marker_store_or_filesystem_primitives()
    {

        ProductionSource coordinator = Assert.Single(
            ProductionSourceInventory.Sources(),
            static source => source.IsExactOwner(CoordinatorPath));

        string[] forbiddenConstructs =
        [
            "ICampaignPathMarkerCodec",
            "CampaignPathMarkerCodec",
            "PhysicalCampaignRootOpener",
            "CampaignPathMarkerIntentStore",
            "CampaignPathMarkerRootAuthority",
            "System.IO",
            "File.",
            "Directory.",
            "FileStream",
            "FileSystemInfo",
            "SafeFileHandle",
        ];

        string[] resolved =
        [
            .. forbiddenConstructs.Where(coordinator.Names),
        ];

        Assert.True(
            resolved.Length == 0,
            "The coordinator may use only the aggregate Campaign lifecycle port; codec, opener, "
            + "marker-store, root-authority, and filesystem primitives belong behind that port: "
            + string.Join(", ", resolved));

    }

}
