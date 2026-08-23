using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

using FullInstallationResetMarkerCleanupAuthority =
    RetroDownfall.Arcanum.Infrastructure.InstallationReset.HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

internal partial interface ICampaignPathMarkerLifecycle
{

    Task<Result<CampaignPathFullInstallationResetInventory>>
        InventoryFullInstallationResetCleanupAsync(
            Guid ownerOperationId,
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken);

    Task<Result> RevalidateFullInstallationResetInventoryAsync(
        CampaignPathFullInstallationResetInventory inventory,
        SqliteConnection liveCoreConnection,
        CancellationToken cancellationToken);

    Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
        PrepareFullInstallationResetCleanupAsync(
            CampaignPathFullInstallationResetCleanupPreparation preparation,
            CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
            FullInstallationResetMarkerCleanupAuthority authority,
            SqliteConnection liveCoreConnection,
            SqliteTransaction liveCoreTransaction,
            CancellationToken cancellationToken);

    Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
        ReconcileFullInstallationResetCleanupAsync(
            CampaignPathFullInstallationResetCleanupReceipt prepared,
            FullInstallationResetMarkerCleanupAuthority authority,
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken);

}
