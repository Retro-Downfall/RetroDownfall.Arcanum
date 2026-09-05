using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// What startup found, including how the two maintenance records read as a pair.
/// </summary>
/// <remarks>
/// <paramref name="NestedTransitionEvidence"/> is <see langword="null"/> only on the paths that could
/// not read the journal at all — the lock-free probe, which has no lock to read it under. A host that
/// held the lock always has an answer, because "neither record is active" is itself one.
/// </remarks>
internal sealed record InstallationResetStartupRecoveryState(
    ActiveInstallationReset? ActiveReset,
    Guid? ExpectedInstallationId,
    bool IsLegacyV1,
    InstallationResetNestedTransitionEvidenceOutcome? NestedTransitionEvidence = null);

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
    IInstallationResetActiveStore activeStore,
    GrimoireOfflineTransitionLifecycleStore transitionJournal) : IInstallationResetStartupRecovery
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

    private readonly GrimoireOfflineTransitionLifecycleStore _transitionJournal =
        transitionJournal ?? throw new ArgumentNullException(nameof(transitionJournal));

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

            return await PairAsync(heldInstallationLock, recovered.Value, cancellationToken)
                .ConfigureAwait(false);

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

        return await PairAsync(heldInstallationLock, afterCleanup.Value, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Reads the offline-transition journal under the same held lock and resolves the pair.
    /// </summary>
    /// <remarks>
    /// The journal read belongs here rather than beside the projection because it needs the lock, and
    /// this is the one entry point that holds it. The lock-free probe shares the projection and
    /// deliberately does not share this: an answer it could not have computed honestly is worse than
    /// no answer.
    /// </remarks>
    private async Task<Result<InstallationResetStartupRecoveryState>> PairAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActiveRecoveryState recovered,
        CancellationToken cancellationToken)
    {

        Result<InstallationResetStartupRecoveryState> projected = Project(recovered);

        if (projected.IsFailure)
        {

            return projected;

        }

        Result<GrimoireOfflineTransitionTypedRecoveryState> journal = await _transitionJournal
            .RecoverAsync(heldInstallationLock, _guardedRoot, cancellationToken)
            .ConfigureAwait(false);

        if (journal.IsFailure)
        {

            return Result<InstallationResetStartupRecoveryState>.Failure(journal.Error);

        }

        InstallationResetNestedTransitionEvidenceOutcome outcome =
            InstallationResetNestedTransitionEvidence.Resolve(
                Outer(recovered),
                Inner(journal.Value));

        return outcome is InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired
            ? EvidenceFailure()
            : Result<InstallationResetStartupRecoveryState>.Success(
                projected.Value with { NestedTransitionEvidence = outcome });

    }

    private static InstallationResetNestedTransitionEvidence.OuterRecord? Outer(
        InstallationResetActiveRecoveryState recovered) =>
        recovered.Publication is { } publication
            ? new InstallationResetNestedTransitionEvidence.OuterRecord(
                publication.Payload.OperationId,
                publication.Payload.NestedTransitionReceipt)
            : null;

    private static InstallationResetNestedTransitionEvidence.InnerJournal? Inner(
        GrimoireOfflineTransitionTypedRecoveryState journal) =>
        journal.Publication is { } publication
            ? new InstallationResetNestedTransitionEvidence.InnerJournal(
                publication.Payload.Binding.OperationId,
                publication.Payload.Binding.Kind,
                publication.Payload.Binding.EffectDigest,
                publication.Payload.Binding.ParentReceiptBindingDigest,
                publication.Payload.Lifecycle.State,
                publication.Payload.Lifecycle.ReconciliationEvidence?.Step,
                publication.Payload.Lifecycle.ReconciliationEvidence
                    ?.DatabaseTerminalWinnerDigest)
            : null;

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
