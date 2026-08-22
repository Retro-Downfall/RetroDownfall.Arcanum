using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed record InstallationResetStartupRecoveryState(
    ActiveInstallationReset? ActiveReset,
    Guid? ExpectedInstallationId,
    bool IsLegacyV1);

internal interface IInstallationResetStartupRecovery
{

    Task<Result<InstallationResetStartupRecoveryState>> RecoverBeforeBootstrapAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default);

}

/// <summary>
/// Authenticates and converges reset-active evidence while the hosted service owns the installation
/// lock, before any database bootstrap or readiness publication begins.
/// </summary>
internal sealed class InstallationResetStartupRecovery(
    string guardedRoot,
    IInstallationResetActiveStore activeStore) : IInstallationResetStartupRecovery
{

    private readonly string _guardedRoot = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(
            string.IsNullOrWhiteSpace(guardedRoot)
                ? throw new ArgumentException(
                    "The guarded installation root is required.",
                    nameof(guardedRoot))
                : guardedRoot));

    private readonly IInstallationResetActiveStore _activeStore =
        activeStore ?? throw new ArgumentNullException(nameof(activeStore));

    public async Task<Result<InstallationResetStartupRecoveryState>> RecoverBeforeBootstrapAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(_guardedRoot);

        Result<InstallationResetActiveRecoveryState> recovered = await _activeStore
            .RecoverAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsSuccess)
        {

            return Project(recovered.Value);

        }

        Result cleanup = await _activeStore
            .CompleteStartupCleanupAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (cleanup.IsFailure)
        {

            return Result<InstallationResetStartupRecoveryState>.Failure(cleanup.Error);

        }

        Result<InstallationResetActiveRecoveryState> afterCleanup = await _activeStore
            .RecoverAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (afterCleanup.IsFailure)
        {

            return Result<InstallationResetStartupRecoveryState>.Failure(afterCleanup.Error);

        }

        if (afterCleanup.Value.Outcome is not InstallationResetActiveRecoveryOutcome.NoActiveRecord)
        {

            return EvidenceFailure();

        }

        return Project(afterCleanup.Value);

    }

    internal static Result<InstallationResetStartupRecoveryState> Project(
        InstallationResetActiveRecoveryState recovered) =>
        recovered.Outcome switch
        {
            InstallationResetActiveRecoveryOutcome.NoActiveRecord
                when recovered.Publication is null && recovered.LegacyRecord is null =>
                new InstallationResetStartupRecoveryState(
                    ActiveReset: null,
                    ExpectedInstallationId: null,
                    IsLegacyV1: false),
            InstallationResetActiveRecoveryOutcome.AuthenticatedV2
                when recovered.Publication is { } publication
                    && recovered.LegacyRecord is null =>
                new InstallationResetStartupRecoveryState(
                    ToActiveReset(publication.Payload.ToRecord()),
                    publication.Envelope.InstallationId,
                    IsLegacyV1: false),
            InstallationResetActiveRecoveryOutcome.LegacyV1
                when recovered.Publication is null
                    && recovered.LegacyRecord is { } legacy =>
                new InstallationResetStartupRecoveryState(
                    ToActiveReset(legacy),
                    ExpectedInstallationId: null,
                    IsLegacyV1: true),
            _ => EvidenceFailure(),
        };

    private static ActiveInstallationReset ToActiveReset(
        InstallationResetActiveRecord record)
    {

        bool requiresExternalRemediation =
            record.FullInstallationResetRemediationClaim is not null;

        InstallationResetHostHandoff? hostHandoff =
            !requiresExternalRemediation
            && record.DataHandoff is InstallationResetDataHandoff.HostFactoryErasure
                ? new InstallationResetHostHandoff(
                    record.OperationId,
                    record.PlanId,
                    record.Scope,
                    record.Workspace,
                    record.AcceptedBinding)
                : null;

        return new ActiveInstallationReset(
            record.Scope,
            record.Workspace?.WorkspaceRoot,
            record.PlanId,
            record.OperationId,
            record.Phase,
            record.DataHandoff,
            record.OnlineDataCompletion is not null,
            hostHandoff,
            requiresExternalRemediation);

    }

    private static Result<InstallationResetStartupRecoveryState> EvidenceFailure() =>
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The installation-reset active evidence requires authenticated recovery.");

}
