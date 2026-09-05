using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

/// <summary>What the pre-bootstrap pass did about the journal it was handed.</summary>
internal enum GrimoireOfflineTransitionStartupRecoveryOutcome : byte
{

    /// <summary>No journal was active, so this pass did nothing at all.</summary>
    NoActiveJournal = 1,

    /// <summary>The transition reached a durable verdict and ordinary bootstrap may proceed.</summary>
    Resumed = 2,

}

/// <summary>
/// Resumes an authenticated offline transition before the database is opened for ordinary use.
/// </summary>
/// <remarks>
/// #249 taught startup to read the two maintenance records as a pair and to refuse when the pair says
/// a transformation is unfinished. That refusal is correct and is the wrong place to stop: an
/// installation whose erasure crashed cannot start at all until somebody finishes the erasure, and
/// nothing else in the product finishes it. This is the somebody.
/// </remarks>
internal interface IGrimoireOfflineTransitionStartupRecovery
{

    Task<Result<GrimoireOfflineTransitionStartupRecoveryOutcome>> RecoverBeforeBootstrapAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        string databasePath,
        InstallationResetNestedTransitionEvidenceOutcome? evidence,
        GrimoireOfflineTransitionRecoveryEvidence? journal,
        CancellationToken cancellationToken);

}

/// <summary>
/// Runs the registered typed handler for one named operation whose lease this pass adopts first.
/// </summary>
/// <remarks>
/// A seam of its own because it owns the one thing the recovery pass must not: a service scope, and
/// through it a database context. The pass reads external evidence and decides; this runs the handler
/// the decision named, in the scope that handler needs.
/// </remarks>
internal interface IGrimoireOfflineTransitionHandlerDispatch
{

    Task<Result<LongRunningOperationSettlementOutcome>> DispatchAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        Guid operationId,
        CancellationToken cancellationToken);

}

/// <summary>The production dispatch, over one scope per resumed operation.</summary>
internal sealed class GrimoireOfflineTransitionHandlerDispatch(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IGrimoireOfflineTransitionHandlerDispatch
{

    /// <summary>
    /// The same recovery lease the periodic pass takes, and for the same reason.
    /// </summary>
    /// <remarks>
    /// Nothing renews it across the closed period, because a renewal advances the row revision the
    /// journal binds itself to. What the renewal would have guarded — a second recovery starting
    /// beside this one — is guarded here by the installation maintenance lock and by the coordinator's
    /// own process-local claim.
    /// </remarks>
    private static readonly TimeSpan RecoveryLease = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<Result<LongRunningOperationSettlementOutcome>> DispatchAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        Guid operationId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        ILongRunningOperationMaintenanceLeaseAdoption adoption = scope.ServiceProvider
            .GetRequiredService<ILongRunningOperationMaintenanceLeaseAdoption>();

        string ownerId = $"transition-recovery-{Environment.ProcessId}-{Guid.NewGuid():N}";

        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Before the handler, not after. The coordinator compares the row's lease owner with the owner
        // it is given, so a handler dispatched under an owner the row does not name is refused after
        // the gate has already closed around an adopted owner — which strands the installation in the
        // exact posture this pass exists to leave.
        LongRunningOperationLeaseResult adopted = await adoption.AdoptUnderInstallationLockAsync(
            heldInstallationLock,
            guardedDirectory,
            operationId,
            ownerId,
            now,
            now.Add(RecoveryLease),
            cancellationToken).ConfigureAwait(false);

        if (!adopted.Acquired)
        {

            return Result<LongRunningOperationSettlementOutcome>.Failure(
                GrimoireOfflineTransitionStartupRecovery.Refusal().Error);

        }

        LongRunningOperationSettlementOutcome settled = await scope.ServiceProvider
            .GetRequiredService<LongRunningOperationReconciler>()
            .SettleExactlyAsync(operationId, ownerId, cancellationToken)
            .ConfigureAwait(false);

        return Result<LongRunningOperationSettlementOutcome>.Success(settled);

    }

}

/// <summary>The production pre-bootstrap recovery pass.</summary>
internal sealed class GrimoireOfflineTransitionStartupRecovery(
    IGrimoireRecoveryOnlyUnlock unlock,
    ICovenantRecoveryAuthorityBootstrapper authority,
    IGrimoireOfflineTransitionHandlerDispatch dispatch) : IGrimoireOfflineTransitionStartupRecovery
{

    private readonly IGrimoireRecoveryOnlyUnlock _unlock =
        unlock ?? throw new ArgumentNullException(nameof(unlock));

    private readonly ICovenantRecoveryAuthorityBootstrapper _authority =
        authority ?? throw new ArgumentNullException(nameof(authority));

    private readonly IGrimoireOfflineTransitionHandlerDispatch _dispatch =
        dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    public async Task<Result<GrimoireOfflineTransitionStartupRecoveryOutcome>> RecoverBeforeBootstrapAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        string databasePath,
        InstallationResetNestedTransitionEvidenceOutcome? evidence,
        GrimoireOfflineTransitionRecoveryEvidence? journal,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        switch (evidence)
        {

            case null:
            case InstallationResetNestedTransitionEvidenceOutcome.NeitherActive:
            case InstallationResetNestedTransitionEvidenceOutcome.NestedNotStarted:
            case InstallationResetNestedTransitionEvidenceOutcome.NestedRetired:

                return GrimoireOfflineTransitionStartupRecoveryOutcome.NoActiveJournal;

            case InstallationResetNestedTransitionEvidenceOutcome.StandaloneTransition:
            case InstallationResetNestedTransitionEvidenceOutcome.NestedBound:
            case InstallationResetNestedTransitionEvidenceOutcome.NestedReceiptStoredRetirementSuffix:

                break;

            default:

                // Every fail-closed arm of the pair matrix, and any value a future build adds without
                // deciding what it means here.
                return Result<GrimoireOfflineTransitionStartupRecoveryOutcome>.Failure(Refusal().Error);

        }

        // A journal-active answer with no journal to act on is the pair disagreeing with itself one
        // layer further in. The matrix computed its outcome from a publication this pass was not given.
        if (journal is null)
        {

            return Result<GrimoireOfflineTransitionStartupRecoveryOutcome>.Failure(Refusal().Error);

        }

        Result<GrimoireRecoveryUnlockedCatalog> unlocked = await _unlock
            .OpenExistingAsync(heldInstallationLock, guardedDirectory, databasePath, cancellationToken)
            .ConfigureAwait(false);

        if (unlocked.IsFailure)
        {

            return Result<GrimoireOfflineTransitionStartupRecoveryOutcome>.Failure(unlocked.Error);

        }

        Result<Guid> prepared = await PrepareAsync(
            heldInstallationLock,
            guardedDirectory,
            journal,
            unlocked.Value,
            cancellationToken).ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            return Result<GrimoireOfflineTransitionStartupRecoveryOutcome>.Failure(prepared.Error);

        }

        // Physically gone before the handler enters its own maintenance lane. The handler closes the
        // Grimoire and waits for every enrolled handle to close; a pass still holding its probe would
        // be waiting for itself, and a startup that hangs is worse than one that refuses.
        Result<LongRunningOperationSettlementOutcome> dispatched = await _dispatch
            .DispatchAsync(heldInstallationLock, guardedDirectory, prepared.Value, cancellationToken)
            .ConfigureAwait(false);

        if (dispatched.IsFailure)
        {

            return Result<GrimoireOfflineTransitionStartupRecoveryOutcome>.Failure(dispatched.Error);

        }

        // Only a durable verdict is a resumption. A parked transition has closed admission behind it,
        // and reporting it resumed would let the host bootstrap and publish readiness over a catalog
        // that is still part way through being remade.
        return dispatched.Value is LongRunningOperationSettlementOutcome.Completed
            or LongRunningOperationSettlementOutcome.Failed
            or LongRunningOperationSettlementOutcome.Abandoned
                ? GrimoireOfflineTransitionStartupRecoveryOutcome.Resumed
                : Result<GrimoireOfflineTransitionStartupRecoveryOutcome>.Failure(Refusal().Error);

    }

    /// <summary>
    /// Loads and spends the authority handoff, and closes the probe on every way out.
    /// </summary>
    /// <remarks>
    /// A separate method so the probe's lifetime is a scope rather than a thing each exit has to
    /// remember. Both of the failing exits below leave the catalog exactly as they found it, which is
    /// the property that lets a refused start be retried.
    /// </remarks>
    private async Task<Result<Guid>> PrepareAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        GrimoireOfflineTransitionRecoveryEvidence journal,
        GrimoireRecoveryUnlockedCatalog catalog,
        CancellationToken cancellationToken)
    {

        await using GrimoireRecoveryUnlockedCatalog owned = catalog;

        Result<ICovenantClosedRecoveryHandoff> handoff = await _authority
            .LoadAsync(
                heldInstallationLock,
                guardedDirectory,
                owned.Connection,
                journal,
                cancellationToken)
            .ConfigureAwait(false);

        if (handoff.IsFailure)
        {

            return Result<Guid>.Failure(handoff.Error);

        }

        Result consumed = await handoff.Value
            .ConsumeAsync(heldInstallationLock, guardedDirectory, journal, owned.Connection, cancellationToken)
            .ConfigureAwait(false);

        return consumed.IsFailure
            ? Result<Guid>.Failure(consumed.Error)
            : Result<Guid>.Success(handoff.Value.OperationId);

    }

    /// <summary>
    /// The one refusal this pass makes, which never says which of its endings it was.
    /// </summary>
    /// <remarks>
    /// Every one of them has the same remedy, and which of a dozen states an installation is in is
    /// exactly the detail the parent design keeps out of operator-visible text.
    /// </remarks>
    internal static Result Refusal() =>
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The authenticated offline Grimoire transition could not be recovered before bootstrap.");

}
