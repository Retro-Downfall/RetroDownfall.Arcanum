using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>Source-level guards on the reset coordinator's narrow Campaign authority.</summary>
public sealed class HostToolsMarkerPairResetCallSiteTests
{

    private const string CoordinatorPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "HostToolsMarkerPairResetCoordinator.cs";

    private const string ServicePath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetService.cs";

    private const string CompositionRootPath =
        "src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/"
        + "ServiceCollectionExtensions.cs";

    private const string LifecyclePath =
        "src/RetroDownfall.Arcanum.Infrastructure/Covenant/"
        + "CampaignPathMarkerLifecycle.FullInstallationReset.cs";

    private const string CoordinatorPortPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "HostToolsMarkerPairResetContracts.cs";

    private const string LifecyclePortPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Covenant/"
        + "ICampaignPathMarkerLifecycle.FullInstallationReset.cs";

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

    [Fact]
    public void Locked_reset_service_is_the_only_production_caller_of_the_coordinator()
    {

        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        string[] callers =
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(CoordinatorPath)
                    && source.Names("IHostToolsMarkerPairResetCoordinator"))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        // The contracts file declares the port, the composition root registers it, and the locked
        // service calls it. A fourth name would be a second seam into the one operation that deletes
        // both host-tools markers, and the narrowness of that authority is the whole design.
        string[] expected =
        [
            .. new[] { CompositionRootPath, CoordinatorPortPath, ServicePath }
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            expected,
            callers.Select(NormalizeSeparators).Order(StringComparer.Ordinal));

        ProductionSource service = Assert.Single(
            sources,
            static source => source.IsExactOwner(ServicePath));

        Assert.True(service.Names("coordinator.BeginAsync("));

        Assert.True(service.Names("coordinator.ResumeAsync("));

    }

    [Fact]
    public void Only_the_composition_root_constructs_the_coordinator()
    {

        string[] constructors =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    source.Names("new HostToolsMarkerPairResetCoordinator("))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            [CompositionRootPath],
            constructors.Select(NormalizeSeparators));

    }

    [Fact]
    public void The_journal_proof_is_named_by_nothing_outside_the_coordinator()
    {

        string[] namers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    source.Names("AuthenticatedFullInstallationResetJournalProof"))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        // Private construction inside a sealed type is only a guarantee while nothing else can name
        // it. The proof is what the cleanup authority is minted from, so a second namer would be a
        // second way to mint authority over every Campaign root the reset can reach.
        Assert.Equal(
            [CoordinatorPath],
            namers.Select(NormalizeSeparators));

    }

    [Fact]
    public void The_cleanup_authority_is_named_only_by_its_owner_and_the_seam_it_authorizes()
    {

        string[] namers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    source.Names("FullInstallationResetMarkerCleanupAuthority"))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        // The coordinator mints it; the Campaign lifecycle port declares it as a parameter and the
        // implementation revalidates it. Nothing else may hold, pass, or store one.
        string[] expected =
        [
            .. new[] { LifecyclePath, LifecyclePortPath, CoordinatorPath }
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            expected,
            namers.Select(NormalizeSeparators).Order(StringComparer.Ordinal));

    }

    private static string NormalizeSeparators(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

}
