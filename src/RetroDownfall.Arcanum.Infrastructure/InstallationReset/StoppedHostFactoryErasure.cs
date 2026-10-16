using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// Where the one durable operation a global installation reset launched stands, read from the ledger
/// and the journal slot under the held installation lock.
/// </summary>
/// <remarks>
/// The reset record names the nested operation before any row for it exists, so this is a question
/// asked of an identity the record already vouches for, never a scan of rows that merely look alike.
/// The four answers are the four things a resume can do about it: run it, settle a launch that never
/// happened, finish a launch that did, or report what a finished one reported.
/// </remarks>
internal enum StoppedHostNestedLaunchState : byte
{
    /// <summary>No row has the requested identity, so the erasure has never been started.</summary>
    Absent = 1,

    /// <summary>
    /// The row was created and its launch checkpoint was never committed, and no journal exists.
    /// Effects cannot happen structurally before the launch checkpoint, so nothing was erased.
    /// </summary>
    Unlaunched = 2,

    /// <summary>The launch checkpoint is committed, or a journal exists: the erasure may have acted.</summary>
    Launched = 3,

    /// <summary>The row reached a terminal state after its launch, and replays what it reported.</summary>
    Terminal = 4,
}

/// <summary>
/// The only way a stopped-host process runs the protected factory erasure a global installation reset
/// owes, as a reset-only facade over the CLI container's own composition.
/// </summary>
/// <remarks>
/// The protected lifecycle has a closed period in which ordinary admission is shut, every enrolled
/// handle is drained, the ledger connection is permitted through, and exactly one maintenance lane
/// opens the database. A stopped-host lease is invisible to the drain and is not closed by the context
/// that was composed over it, so the period cannot run over one. This facade runs it instead over the
/// ordinary composition the CLI container already holds, which is the graph the launch-gap recovery in
/// that container composes: the scoped context, the operation store, the process gate, the coordinator,
/// the requested-operation starter, and a checkpoint initiator. No <c>IDataRetentionService</c> is
/// registered, because this type is not one: it constructs the service for one call, exposes one entry
/// point, and refuses every operation but a factory reset.
///
/// <para>It acts only on a claim. Before it does anything it rereads the authenticated reset record
/// under the held lock and requires the reset to be prepared and past its point of no return, with a
/// nested-transition receipt that names the requested operation. A record that does not say so
/// authorizes nothing, however plausible the request: the CLI is not a host for durable work, it is the
/// one process an authenticated record named a single operation to.</para>
///
/// <para>Every durable thing is found by exact requested identity, never by summary text. A row at
/// version zero is never restarted; if the launch checkpoint was never committed, the row is settled
/// failed by an ownerless compare-exchange on its exact identifier and revision and the erasure is
/// reported refused, which lets the caller retire the claim.</para>
/// </remarks>
internal sealed class StoppedHostFactoryErasure(
    ArcanumDbContext db,
    IOptionsMonitor<ArcanumSettings> settings,
    ILongRunningOperationStore operations,
    TimeProvider timeProvider,
    ILogger<DataRetentionService> logger,
    ICovenantLabeledArtifactTransactionGuard labeledArtifactGuard,
    ICovenantOperationGate gate,
    CovenantErasureCoordinator coordinator,
    CovenantRequestedOperationStarter requestedOperationStarter,
    LongRunningOperationOwnership ownership,
    CovenantResetCheckpointInitiator checkpointInitiator,
    ICovenantFactoryErasureApplyRequestDigestCalculator applyDigests,
    ICovenantErasureEffectDigestCalculator effectDigests,
    IInstallationResetActiveStore activeStore,
    GrimoireOfflineTransitionLifecycleStore journal,
    InstallationResetMaintenanceLockAccessor lockAccessor,
    StoppedHostCovenantAuthority covenantAuthority,
    IStoppedHostGrimoireConnectionFactory stoppedHostConnections,
    CovenantErasureStartupRecoveryOwnerAdopter ownerAdopter,
    IGrimoireOfflineTransitionHandlerDispatch dispatch,
    ICovenantConnectionDrain drain)
{
    /// <summary>
    /// Runs the erasure the record claimed for <paramref name="request"/>, or reports what it reported.
    /// </summary>
    internal async Task<Result<DataRetentionApplyResult>> ApplyAsync(
        DataRetentionApplyRequest request,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(issuer);

        if (request.Request.Operation is not DataRetentionOperation.FactoryReset
            || request.Request.Workspace is not null)
        {
            return Result<DataRetentionApplyResult>.Failure(new Error(
                ErrorCodes.Data.InvalidRequest,
                "The stopped-host protected erasure runs only a factory reset."));
        }

        Result<IStoppedHostGrimoireConnectionAuthority> issued =
            issuer.IssueStoppedHostFactoryErasureLockLoan();

        if (issued.IsFailure)
        {
            return Result<DataRetentionApplyResult>.Failure(issued.Error);
        }

        await using IStoppedHostGrimoireConnectionAuthority authority = issued.Value;

        Result<IAsyncDisposable> lent = StoppedHostGrimoireAuthorityIssuer.LendHeldLock(
            authority,
            lockAccessor,
            Path.GetFullPath(ArcanumPaths.GrimoireDatabaseFile));

        if (lent.IsFailure)
        {
            return Result<DataRetentionApplyResult>.Failure(lent.Error);
        }

        // The loan ends with this call, in this block's finally, whichever way it ends. Nothing else in
        // the process can borrow the held lock once it has.
        await using IAsyncDisposable loan = lent.Value;

        try
        {
            return await ApplyUnderLoanAsync(request, issuer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseOrdinaryHandlesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads where the nested erasure of a claim stands, under the held lock.
    /// </summary>
    internal async Task<Result<StoppedHostNestedLaunchState>> ReadLaunchStateAsync(
        Guid nestedOperationId,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        Result<IStoppedHostGrimoireConnectionAuthority> issued =
            issuer.IssueStoppedHostFactoryErasureLockLoan();

        if (issued.IsFailure)
        {
            return Result<StoppedHostNestedLaunchState>.Failure(issued.Error);
        }

        await using IStoppedHostGrimoireConnectionAuthority authority = issued.Value;

        Result<IAsyncDisposable> lent = StoppedHostGrimoireAuthorityIssuer.LendHeldLock(
            authority,
            lockAccessor,
            Path.GetFullPath(ArcanumPaths.GrimoireDatabaseFile));

        if (lent.IsFailure)
        {
            return Result<StoppedHostNestedLaunchState>.Failure(lent.Error);
        }

        await using IAsyncDisposable loan = lent.Value;

        try
        {
            Result<ArcanumMaintenanceLock> held = lockAccessor.BorrowHeldLock(activeStore.GuardedRoot);

            if (held.IsFailure)
            {
                return Result<StoppedHostNestedLaunchState>.Failure(held.Error);
            }

            return await ClassifyAsync(held.Value, nestedOperationId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await ReleaseOrdinaryHandlesAsync().ConfigureAwait(false);
        }
    }

    private async Task<Result<DataRetentionApplyResult>> ApplyUnderLoanAsync(
        DataRetentionApplyRequest request,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken)
    {
        Result<ArcanumMaintenanceLock> held = lockAccessor.BorrowHeldLock(activeStore.GuardedRoot);

        if (held.IsFailure)
        {
            return Result<DataRetentionApplyResult>.Failure(Forbidden());
        }

        Result claim = await RequireClaimAsync(held.Value, request, cancellationToken).ConfigureAwait(false);

        if (claim.IsFailure)
        {
            return Result<DataRetentionApplyResult>.Failure(claim.Error);
        }

        Guid nestedOperationId = request.RequestedOperationId!.Value;

        Result<StoppedHostNestedLaunchState> classified = await ClassifyAsync(
            held.Value,
            nestedOperationId,
            cancellationToken).ConfigureAwait(false);

        if (classified.IsFailure)
        {
            return Result<DataRetentionApplyResult>.Failure(classified.Error);
        }

        switch (classified.Value)
        {
            case StoppedHostNestedLaunchState.Unlaunched:

                return await SettleUnlaunchedAsync(nestedOperationId, cancellationToken).ConfigureAwait(false);

            case StoppedHostNestedLaunchState.Launched:

                {
                    Result resumed = await ResumeLaunchedAsync(
                        held.Value,
                        issuer,
                        nestedOperationId,
                        cancellationToken).ConfigureAwait(false);

                    if (resumed.IsFailure)
                    {
                        return Result<DataRetentionApplyResult>.Failure(resumed.Error);
                    }

                    // The launch has reached a durable verdict. Reporting it is the route's own replay of
                    // that verdict, so the answer is the one a host would have given.
                    return await RunRouteAsync(request, cancellationToken).ConfigureAwait(false);
                }

            case StoppedHostNestedLaunchState.Terminal:

                return await RunRouteAsync(request, cancellationToken).ConfigureAwait(false);

            default:

                {
                    Result ensured = await EnsureCovenantAuthorityAsync(issuer, cancellationToken)
                        .ConfigureAwait(false);

                    if (ensured.IsFailure)
                    {
                        return Result<DataRetentionApplyResult>.Failure(ensured.Error);
                    }

                    return await RunRouteAsync(request, cancellationToken).ConfigureAwait(false);
                }
        }
    }

    /// <summary>
    /// The unchanged factory-erasure route: requested start, inventory checkpoint, the coordinator's
    /// closed period and the ordinary continuation, or the replay of an identity that already ran.
    /// </summary>
    private Task<Result<DataRetentionApplyResult>> RunRouteAsync(
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken)
    {
        DataRetentionService retention = new(
            db,
            settings,
            operations,
            timeProvider,
            logger,
            labeledArtifactGuard,
            covenantGate: gate,
            covenantResetCheckpointInitiator: checkpointInitiator,
            covenantErasureCoordinator: coordinator,
            requestedOperationStarter: requestedOperationStarter,
            factoryApplyRequestDigests: applyDigests,
            covenantErasureEffectDigests: effectDigests,
            operationOwnership: ownership);

        return retention.ApplyAsync(request, cancellationToken);
    }

    /// <summary>
    /// Requires an authenticated record that claimed exactly this operation for this plan.
    /// </summary>
    private async Task<Result> RequireClaimAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RequestedOperationId is not { } requested
            || requested == Guid.Empty
            || string.IsNullOrWhiteSpace(request.ExpectedPlanId))
        {
            return Result.Failure(Forbidden());
        }

        Result<InstallationResetActiveRecoveryState> recovered = await activeStore
            .RecoverAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure
            || recovered.Value.Outcome is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            || recovered.Value.Publication is not { } publication)
        {
            return Result.Failure(Forbidden());
        }

        InstallationResetActiveRecord record = publication.Payload.ToRecord();

        bool claimed = record.Scope is InstallationResetScope.Global or InstallationResetScope.All
            && record.Phase is InstallationResetPhase.Prepared
            && record.PointOfNoReturn
            && record.DataHandoff is null
            && record.AcceptedBinding.DataPlanIds.Length == 1
            && string.Equals(
                record.AcceptedBinding.DataPlanIds[0],
                request.ExpectedPlanId,
                StringComparison.Ordinal)
            && record.NestedTransitionReceipt is { } receipt
            && receipt.NestedOperationId == requested
            && receipt.Phase is InstallationResetNestedTransitionPhase.Claimed
                or InstallationResetNestedTransitionPhase.Completed;

        return claimed ? Result.Success() : Result.Failure(Forbidden());
    }

    private async Task<Result<StoppedHostNestedLaunchState>> ClassifyAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        Guid nestedOperationId,
        CancellationToken cancellationToken)
    {
        LongRunningOperationRequestIdentityMatch? match = await operations
            .FindByRequestedOperationIdAsync(nestedOperationId, cancellationToken)
            .ConfigureAwait(false);

        Result<GrimoireOfflineTransitionTypedRecoveryState> slot = await journal
            .RecoverAsync(heldInstallationLock, activeStore.GuardedRoot, cancellationToken)
            .ConfigureAwait(false);

        if (slot.IsFailure)
        {
            return Result<StoppedHostNestedLaunchState>.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The offline-transition journal could not be read, so the erasure's position is unknown."));
        }

        return Result<StoppedHostNestedLaunchState>.Success(
            Classify(
                match?.Operation,
                slot.Value.Outcome is GrimoireOfflineTransitionTypedRecoveryOutcome.Authenticated));
    }

    /// <summary>
    /// Maps one ledger row and the journal slot to the position they prove.
    /// </summary>
    /// <remarks>
    /// Pure so its cases can be pinned without a database. A journal in the slot means the erasure may
    /// have acted whatever the row says; a row at the launch version means the same; and a row at
    /// version zero is only ever settled, never resumed, whether or not it is still running.
    /// </remarks>
    internal static StoppedHostNestedLaunchState Classify(
        LongRunningOperation? row,
        bool journalActive)
    {
        if (journalActive)
        {
            return StoppedHostNestedLaunchState.Launched;
        }

        if (row is null)
        {
            return StoppedHostNestedLaunchState.Absent;
        }

        if (row.CheckpointVersion == 0 && row.CheckpointPayload is null or { Length: 0 })
        {
            return row.State is LongRunningOperationState.Completed
                ? StoppedHostNestedLaunchState.Terminal
                : StoppedHostNestedLaunchState.Unlaunched;
        }

        return row.State is LongRunningOperationState.Completed
            or LongRunningOperationState.Failed
            or LongRunningOperationState.Abandoned
                ? StoppedHostNestedLaunchState.Terminal
                : StoppedHostNestedLaunchState.Launched;
    }

    /// <summary>
    /// Settles a row whose launch checkpoint was never committed, and refuses the erasure.
    /// </summary>
    /// <remarks>
    /// By the row's exact identifier and revision, ownerless, because the process that created it is
    /// gone and its lease means nothing to this one. The refusal is the code the reset service already
    /// treats as proof that nothing was done, which together with the read that classified this row is
    /// what lets it retire the claim. The legacy restart that a row at this version used to get is not
    /// run: it deletes the ordinary tables without the protected lifecycle.
    /// </remarks>
    private async Task<Result<DataRetentionApplyResult>> SettleUnlaunchedAsync(
        Guid nestedOperationId,
        CancellationToken cancellationToken)
    {
        LongRunningOperationRequestIdentityMatch? match = await operations
            .FindByRequestedOperationIdAsync(nestedOperationId, cancellationToken)
            .ConfigureAwait(false);

        if (match is null)
        {
            return Result<DataRetentionApplyResult>.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The nested erasure's ledger row disappeared while it was being settled."));
        }

        LongRunningOperation row = match.Operation;

        if (row.State is not (LongRunningOperationState.Failed or LongRunningOperationState.Abandoned))
        {
            bool settled = await operations.TryTransitionAsync(
                row.Id,
                row.Revision,
                ownerId: null,
                LongRunningOperationState.Failed,
                timeProvider.GetUtcNow(),
                ErrorCodes.Data.Conflict,
                cancellationToken).ConfigureAwait(false);

            if (!settled)
            {
                return Result<DataRetentionApplyResult>.Failure(new Error(
                    ErrorCodes.Data.RecoveryRequired,
                    "The nested erasure's ledger row changed while it was being settled."));
            }
        }

        return Result<DataRetentionApplyResult>.Failure(new Error(
            ErrorCodes.Data.Conflict,
            "A previous attempt to start this erasure never reached its launch checkpoint, so nothing was "
            + "erased. It has been settled; request a new dry-run and apply again."));
    }

    /// <summary>
    /// Finishes a launch the previous process committed and never journaled.
    /// </summary>
    /// <remarks>
    /// The host's own pair: the adopter reconstructs the one durable owner and closes the gate around
    /// it, and the launch-gap resumption dispatches it to the registered owner-bound handler. A journal
    /// that is still active here is refused rather than resumed: the resume ordering finishes a journal
    /// before it ever reaches this type, so one that survives it is a pair this build cannot reconcile.
    /// </remarks>
    private async Task<Result> ResumeLaunchedAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        Guid nestedOperationId,
        CancellationToken cancellationToken)
    {
        Result<GrimoireOfflineTransitionTypedRecoveryState> slot = await journal
            .RecoverAsync(heldInstallationLock, activeStore.GuardedRoot, cancellationToken)
            .ConfigureAwait(false);

        if (slot.IsFailure
            || slot.Value.Outcome is GrimoireOfflineTransitionTypedRecoveryOutcome.Authenticated)
        {
            return Result.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "An offline Grimoire transition journal is active, so the erasure must be resumed through "
                + "its journal before this reset can continue."));
        }

        Result ensured = await EnsureCovenantAuthorityAsync(issuer, cancellationToken).ConfigureAwait(false);

        if (ensured.IsFailure)
        {
            return ensured;
        }

        Result<IStoppedHostGrimoireConnectionAuthority> readAuthority =
            issuer.IssueStoppedHostInstallationResetPlanReadAuthority();

        if (readAuthority.IsFailure)
        {
            return Result.Failure(readAuthority.Error);
        }

        await using IStoppedHostGrimoireConnectionAuthority authority = readAuthority.Value;

        Result<IStoppedHostGrimoireConnectionLease> opened = await stoppedHostConnections
            .OpenStoppedHostInstallationResetPlanReadAsync(authority, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return Result.Failure(opened.Error);
        }

        CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner? adopted;

        await using (IStoppedHostGrimoireConnectionLease lease = opened.Value)
        {
            Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> adoption = await ownerAdopter
                .AdoptBeforeReadinessAsync(lease.Connection, cancellationToken)
                .ConfigureAwait(false);

            if (adoption.IsFailure)
            {
                return Result.Failure(adoption.Error);
            }

            adopted = adoption.Value;
        }

        // The record names the identity the erasure was requested under, which the ledger keeps
        // distinct from the identity of the row it created, so the adopted owner is compared with the
        // row that identity resolves to and never with the identity itself.
        LongRunningOperationRequestIdentityMatch? claimed = await operations
            .FindByRequestedOperationIdAsync(nestedOperationId, cancellationToken)
            .ConfigureAwait(false);

        if (adopted is null
            || claimed is null
            || adopted.ExpectedOperation.OperationId != claimed.Operation.Id)
        {
            return Result.Failure(new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "The unfinished erasure the ledger holds is not the one the reset record claimed."));
        }

        return await CovenantOfflineTransitionLaunchGapResumption
            .ResumeBeforeReadinessAsync(
                dispatch,
                heldInstallationLock,
                activeStore.GuardedRoot,
                adopted,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Establishes, or verifies, this process's Covenant runtime on a lease that is released before the
    /// erasure starts.
    /// </summary>
    private async Task<Result> EnsureCovenantAuthorityAsync(
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken)
    {
        Result<HostProcessToolsRecoveryMarkerSnapshot> marker = covenantAuthority.CaptureMarker();

        if (marker.IsFailure)
        {
            return Result.Failure(marker.Error);
        }

        Result<IStoppedHostGrimoireConnectionAuthority> issued =
            issuer.IssueStoppedHostInstallationResetPlanReadAuthority();

        if (issued.IsFailure)
        {
            return Result.Failure(issued.Error);
        }

        await using IStoppedHostGrimoireConnectionAuthority authority = issued.Value;

        Result<IStoppedHostGrimoireConnectionLease> opened = await stoppedHostConnections
            .OpenStoppedHostInstallationResetPlanReadAsync(authority, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return Result.Failure(new Error(
                ErrorCodes.Data.InventoryUnavailable,
                "The existing Grimoire could not be opened without creating or repairing state."));
        }

        await using IStoppedHostGrimoireConnectionLease lease = opened.Value;

        return await covenantAuthority
            .EnsureAsync(lease.Connection, marker.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lets go of every ordinary handle on the database before offline cleanup needs the file.
    /// </summary>
    /// <remarks>
    /// The context's own connection first, then the drain, which closes every enrolled handle and
    /// clears the idle pools. Neither can fail the erasure that has already ended: what a handle left
    /// behind would break is the cleanup that follows, and that proves its own preconditions.
    /// </remarks>
    private async Task ReleaseOrdinaryHandlesAsync()
    {
        try
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "The stopped-host erasure could not close its context connection ({FailureType}).",
                exception.GetType());
        }

        try
        {
            _ = await drain.DrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "The stopped-host erasure could not drain its ordinary handles ({FailureType}).",
                exception.GetType());
        }
    }

    private static Error Forbidden() =>
        new(
            ErrorCodes.Covenant.ForbiddenAuthority,
            "A stopped-host factory erasure runs only for the operation an authenticated installation "
            + "reset record claimed.");
}
