using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal interface IHostToolsMarkerPairResetCoordinator
{

    Task<Result<InstallationResetActivePublication>> BeginAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication acceptedClaim,
        FullInstallationResetExternalRemediationAttestation attestation,
        CancellationToken cancellationToken);

    Task<Result<InstallationResetActivePublication>> ResumeAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication checkpoint,
        CancellationToken cancellationToken);

}

internal interface IFullInstallationResetCampaignSchemaReadiness
{

    Task<Result> RequireExactAsync(
        SqliteConnection liveCoreConnection,
        CancellationToken cancellationToken);

}

internal enum HostToolsMarkerPairResetOsOpenStatus : byte
{

    Opened = 1,

    Absent = 2,

    Mismatch = 3,

    Unavailable = 4,

}

internal enum HostToolsMarkerPairResetOsDeleteStatus : byte
{

    Deleted = 1,

    Mismatch = 2,

    Unavailable = 3,

}

internal enum HostToolsMarkerPairResetOsAbsenceStatus : byte
{

    Absent = 1,

    Mismatch = 2,

    Unavailable = 3,

}

internal interface IHostToolsMarkerPairResetOsCapability : IDisposable
{
}

internal sealed class HostToolsMarkerPairResetOsOpenResult
{

    private HostToolsMarkerPairResetOsOpenResult(
        HostToolsMarkerPairResetOsOpenStatus status,
        HostProcessToolsOsMarkerEvidence? evidence,
        IHostToolsMarkerPairResetOsCapability? capability)
    {

        Status = status;

        Evidence = evidence;

        Capability = capability;

    }

    internal HostToolsMarkerPairResetOsOpenStatus Status { get; }

    internal HostProcessToolsOsMarkerEvidence? Evidence { get; }

    internal IHostToolsMarkerPairResetOsCapability? Capability { get; }

    internal static HostToolsMarkerPairResetOsOpenResult Opened(
        HostProcessToolsOsMarkerEvidence evidence,
        IHostToolsMarkerPairResetOsCapability capability) =>
        new(
            HostToolsMarkerPairResetOsOpenStatus.Opened,
            evidence ?? throw new ArgumentNullException(nameof(evidence)),
            capability ?? throw new ArgumentNullException(nameof(capability)));

    internal static HostToolsMarkerPairResetOsOpenResult Absent() =>
        new(HostToolsMarkerPairResetOsOpenStatus.Absent, null, null);

    internal static HostToolsMarkerPairResetOsOpenResult Mismatch() =>
        new(HostToolsMarkerPairResetOsOpenStatus.Mismatch, null, null);

    internal static HostToolsMarkerPairResetOsOpenResult Unavailable() =>
        new(HostToolsMarkerPairResetOsOpenStatus.Unavailable, null, null);

}

internal interface IHostToolsMarkerPairResetOsPort
{

    HostToolsMarkerPairResetOsOpenResult OpenExact();

    HostToolsMarkerPairResetOsOpenResult ReopenExact(
        HostProcessToolsOsMarkerEvidence expectedEvidence);

    Task<HostToolsMarkerPairResetOsDeleteStatus> CompareDeleteExactAsync(
        IHostToolsMarkerPairResetOsCapability capability,
        HostProcessToolsOsMarkerEvidence expectedEvidence,
        CancellationToken cancellationToken);

    Task<HostToolsMarkerPairResetOsAbsenceStatus> ProveExactAbsenceAsync(
        CancellationToken cancellationToken);

}
