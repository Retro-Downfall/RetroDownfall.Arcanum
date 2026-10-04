using System.Data;

using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class DataRetentionService
{
    private async Task<Result<DataRetentionApplyResult>> ApplyFactoryResetRouteAsync(
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RequestedOperationId is not null
            && request.ExpectedPlanId is null)
        {
            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.InvalidRequest,
                    "A factory reset's expected plan and requested operation identity must be supplied together."));
        }

        CovenantDigest? namedApplyDigest = null;

        if (request.RequestedOperationId is { } requestedOperationId)
        {
            Result<CovenantDigest> digest = _factoryApplyRequestDigests.Compute(
                new CovenantFactoryErasureApplyRequestDigestInput(request.ExpectedPlanId!));

            if (digest.IsFailure)
            {
                return Result<DataRetentionApplyResult>.Failure(digest.Error);
            }

            namedApplyDigest = digest.Value;

            LongRunningOperationRequestIdentityMatch? existing = await operations
                .FindByRequestedOperationIdAsync(requestedOperationId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return MapFactoryErasureReplay(
                    existing,
                    request.ExpectedPlanId!,
                    digest.Value);
            }
        }

        Result<DataRetentionPlanAdmission> admitted;

        try
        {
            admitted = await PlanAdmissionAsync(
                request.Request,
                cancellationToken,
                DataRetentionPlanAdmissionCapability.Installation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Factory reset refused an inventory that could not be proven safe.");

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The factory-reset inventory could not be proven safely."));
        }

        if (admitted.IsFailure)
        {
            return Result<DataRetentionApplyResult>.Failure(admitted.Error);
        }

        ICovenantSnapshotReadLease? planningLease = admitted.Value.ReadLease;

        if (planningLease is null
            || admitted.Value.Plan.Covenant is null
            || planningLease is not CovenantInstallationReadLease installationLease
            || _covenantResetCheckpointInitiator is null
            || _covenantErasureCoordinator is null)
        {
            if (planningLease is not null)
            {
                _ = await TryDisposeCovenantPlanningLeaseAsync(planningLease).ConfigureAwait(false);
            }

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    admitted.Value.Plan.Covenant is null && planningLease is not null
                        ? ErrorCodes.Covenant.IntegrityFailure
                        : ErrorCodes.Covenant.MaintenanceFailed,
                    "Healthy-catalog factory erasure requires one current installation inventory and its exclusive lifecycle."));
        }

        LongRunningOperation? operation = null;

        string? ownerId = null;

        try
        {
            DataRetentionPlan current = admitted.Value.Plan;

            CovenantOperationLeaseSnapshot snapshot = installationLease.Snapshot;

            if (snapshot.Kind is not CovenantLeaseKind.InstallationRead
                || snapshot.Coverage is not CovenantLeaseCoverage.Installation
                || snapshot.DatasetGeneration is not { } datasetGeneration
                || datasetGeneration == Guid.Empty
                || current.Covenant is not { } inventory)
            {
                return Result<DataRetentionApplyResult>.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "Healthy-catalog factory erasure requires one current installation inventory."));
            }

            if (!string.IsNullOrWhiteSpace(request.ExpectedPlanId)
                && !string.Equals(request.ExpectedPlanId, current.PlanId, StringComparison.Ordinal))
            {
                return Result<DataRetentionApplyResult>.Failure(
                    new Error(
                        ErrorCodes.Data.PlanChanged,
                        "The deletion plan changed after preview; request a new dry-run before applying."));
            }

            if (current.Blockers.Length > 0)
            {
                return Result<DataRetentionApplyResult>.Failure(
                    new Error(ErrorCodes.Data.Blocked, current.Blockers[0].Message));
            }

            if (FirstUndrainableFactoryConflict(current) is { } currentConflict)
            {
                return Result<DataRetentionApplyResult>.Failure(
                    new Error(ErrorCodes.Data.Conflict, currentConflict.Message));
            }

            ownerId = "data-retention:" + Guid.NewGuid().ToString("N");

            DateTimeOffset now = timeProvider.GetUtcNow();

            CovenantErasureEffectDigestInput effect = new(
                CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                current.PlanId,
                datasetGeneration,
                inventory.Rows,
                inventory.ManagedFiles,
                inventory.LocalArtifacts,
                inventory.AffectedSessions,
                inventory.PossibleDisclosures,
                inventory.DisclosureCountKind);

            if (request.RequestedOperationId is { } freshRequestedOperationId)
            {
                if (_requestedOperationStarter is null || namedApplyDigest is null)
                {
                    return Result<DataRetentionApplyResult>.Failure(CovenantMaintenanceFailure());
                }

                Result<CovenantDigest> effectDigest = _covenantErasureEffectDigests.Compute(effect);

                if (effectDigest.IsFailure)
                {
                    return Result<DataRetentionApplyResult>.Failure(effectDigest.Error);
                }

                Result<LongRunningOperationRequestIdentityResult> started =
                    await _requestedOperationStarter.StartRequestedAsync(
                        LongRunningOperationKinds.DataRetentionFactoryReset,
                        LongRunningOperationRecoveryPolicy.RestartIdempotently,
                        $"Applying {request.Request.Operation} data-retention plan {current.PlanId}.",
                        now,
                        freshRequestedOperationId,
                        namedApplyDigest.Value,
                        effectDigest.Value,
                        ownerId,
                        DataRetentionLeaseMaintainer.DefaultLeaseDuration,
                        cancellationToken).ConfigureAwait(false);

                if (started.IsFailure)
                {
                    return Result<DataRetentionApplyResult>.Failure(started.Error);
                }

                if (started.Value.Outcome is LongRunningOperationRequestIdentityOutcome.Replayed)
                {
                    LongRunningOperationRequestIdentityMatch? replayed = await operations
                        .FindByRequestedOperationIdAsync(
                            freshRequestedOperationId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (replayed is null)
                    {
                        return Result<DataRetentionApplyResult>.Failure(CovenantMaintenanceFailure());
                    }

                    return MapFactoryErasureReplay(
                        replayed,
                        request.ExpectedPlanId!,
                        namedApplyDigest.Value);
                }

                operation = started.Value.Operation;
            }
            else
            {
                operation = await operations.TryStartSingleFlightAsync(
                    new LongRunningOperationCreateRequest(
                        LongRunningOperationKinds.DataRetentionFactoryReset,
                        LongRunningOperationRecoveryPolicy.RestartIdempotently,
                        $"Applying {request.Request.Operation} data-retention plan {current.PlanId}.",
                        now),
                    ownerId,
                    now,
                    now.Add(DataRetentionLeaseMaintainer.DefaultLeaseDuration),
                    cancellationToken).ConfigureAwait(false);
            }

            if (operation is null)
            {
                return Result<DataRetentionApplyResult>.Failure(
                    new Error(
                        ErrorCodes.Data.Conflict,
                        await DescribeRetentionConflictAsync(cancellationToken).ConfigureAwait(false)));
            }

            FactoryErasureLaunch launch;

            try
            {
                // Re-plan, healthy-catalog proof and launch publication keep the durable lease renewed,
                // and the maintained scope ends there. The closed period below runs outside the
                // maintainer entirely - not merely on a different token - because a maintainer that is
                // still ticking advances the row's revision whether or not the work it wraps is
                // listening, and that revision is the one the transition journal has bound itself to.
                // With ordinary admission shut the renewal would also be refused outright, and the
                // maintainer would then cancel the erasure or report a finished one as failed.
                launch = await _leaseMaintainer.RunAsync(
                    operation.Id,
                    ownerId,
                    async maintainedToken =>
                    {
                        using CancellationTokenSource maintainedCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken,
                                maintainedToken);

                        DataRetentionPlan revalidated = await BuildFactoryResetPlanCoreAsync(
                            request.Request,
                            operation.Id,
                            maintainedCancellation.Token).ConfigureAwait(false);

                        revalidated = BindCovenantErasurePlanIdentity(revalidated, inventory);

                        DataRetentionConflict? revalidatedConflict = FirstUndrainableFactoryConflict(revalidated);

                        if (revalidated.Blockers.Length > 0 || revalidatedConflict is not null)
                        {
                            Error refusal = revalidated.Blockers.Length > 0
                                ? new Error(ErrorCodes.Data.Blocked, revalidated.Blockers[0].Message)
                                : new Error(ErrorCodes.Data.Conflict, revalidatedConflict!.Message);

                            return FactoryErasureLaunch.Refused(
                                await FailCovenantResetAsync(
                                    operation,
                                    ownerId,
                                    refusal,
                                    LongRunningOperationState.Failed).ConfigureAwait(false));
                        }

                        if (!string.Equals(revalidated.PlanId, current.PlanId, StringComparison.Ordinal))
                        {
                            return FactoryErasureLaunch.Refused(
                                await FailCovenantResetAsync(
                                    operation,
                                    ownerId,
                                    new Error(
                                        ErrorCodes.Data.PlanChanged,
                                        "The deletion plan changed after preview; request a new dry-run before applying."),
                                    LongRunningOperationState.Failed).ConfigureAwait(false));
                        }

                        Result currentLease = await installationLease
                            .RevalidateAsync(maintainedCancellation.Token)
                            .ConfigureAwait(false);

                        if (currentLease.IsFailure)
                        {
                            return FactoryErasureLaunch.Refused(
                                await FailCovenantResetAsync(
                                    operation,
                                    ownerId,
                                    currentLease.Error,
                                    LongRunningOperationState.Failed).ConfigureAwait(false));
                        }

                        Result<CovenantResetCheckpointInitiator.GateAdmission> prepared =
                            await _covenantResetCheckpointInitiator
                                .PrepareFactoryErasureInventoryAsync(
                                    operation,
                                    ownerId,
                                    effect,
                                    request.RequestedOperationId,
                                    installationLease,
                                    maintainedCancellation.Token)
                                .ConfigureAwait(false);

                        if (prepared.IsFailure)
                        {
                            return FactoryErasureLaunch.Refused(
                                await FailCovenantResetAsync(
                                    operation,
                                    ownerId,
                                    prepared.Error,
                                    LongRunningOperationState.Failed).ConfigureAwait(false));
                        }

                        LongRunningOperation? committed = await operations
                            .GetAsync(operation.Id, maintainedCancellation.Token)
                            .ConfigureAwait(false);

                        if (committed?.CheckpointPayload is not { Length: > 0 } payload)
                        {
                            return FactoryErasureLaunch.Refused(
                                await FailCovenantResetAsync(
                                    operation,
                                    ownerId,
                                    new Error(
                                        ErrorCodes.Covenant.ManualRecoveryRequired,
                                        "The committed factory-erasure checkpoint could not be reloaded."),
                                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false));
                        }

                        Result<CovenantErasureCheckpointState> checkpoint =
                            CovenantErasureCheckpointState.FromFactoryResetCheckpoint(
                                committed.Id,
                                committed.CheckpointVersion,
                                payload);

                        // Compared as a whole launch rather than as an owner, for the reason the reset
                        // arm gives: an owner is three of a launch's eleven fields, and the eight it
                        // leaves out are the ones that say which dataset this erasure was admitted to
                        // replace.
                        Result<GrimoireOfflineTransitionLaunchBinding> relaunched =
                            GrimoireOfflineTransitionLaunch.FromCommittedCheckpoint(
                                committed.CheckpointVersion,
                                payload);

                        if (checkpoint.IsFailure
                            || relaunched.IsFailure
                            || relaunched.Value.Digest != prepared.Value.Launch.Digest)
                        {
                            Error invalid = checkpoint.IsFailure
                                ? checkpoint.Error
                                : new Error(
                                    ErrorCodes.Covenant.ManualRecoveryRequired,
                                    "The committed factory-erasure checkpoint did not preserve its admitted owner.");

                            return FactoryErasureLaunch.Refused(
                                await FailCovenantResetAsync(
                                    committed,
                                    ownerId,
                                    invalid,
                                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false));
                        }

                        Result planningLeaseReleased = await TryDisposeCovenantPlanningLeaseAsync(
                            installationLease).ConfigureAwait(false);

                        planningLease = null;

                        if (planningLeaseReleased.IsFailure)
                        {
                            return FactoryErasureLaunch.Refused(
                                await FailCovenantResetAsync(
                                    committed,
                                    ownerId,
                                    planningLeaseReleased.Error,
                                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false));
                        }

                        return FactoryErasureLaunch.Published(committed, checkpoint.Value);
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (DataRetentionLeaseLostException ex)
            {
                logger.LogWarning(
                    ex,
                    "Healthy-catalog factory erasure lost its exact durable owner; recovery must reconcile it.");

                return Result<DataRetentionApplyResult>.Failure(CovenantMaintenanceFailure());
            }

            if (launch is not { Committed: { } launched, Checkpoint: { } launchedCheckpoint })
            {
                return launch.Refusal ?? Result<DataRetentionApplyResult>.Failure(CovenantMaintenanceFailure());
            }

            Result connectionClosed = await CloseFactoryServiceConnectionAsync().ConfigureAwait(false);

            if (connectionClosed.IsFailure)
            {
                return await FailCovenantResetAsync(
                    launched,
                    ownerId,
                    connectionClosed.Error,
                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false);
            }

            DataRetentionApplyResult? ordinaryResult = null;

            // No durable lease is renewed across the closed period, and the coordinator runs on the
            // caller's token exactly as the direct reset arm's does. What the lease was protecting
            // against is held instead by the installation maintenance lock, the journal's own slot,
            // and the process-local ownership the coordinator claims for the length of the run.
            Result<CovenantErasureCompletion> erased;

            try
            {
                erased = await _covenantErasureCoordinator
                    .RunAsync(
                        launched,
                        launchedCheckpoint,
                        ownerId,
                        async continuationToken =>
                        {
                            Result<DataRetentionApplyResult> continued =
                                await ContinueFactoryResetAsync(
                                    operation.Id,
                                    ownerId,
                                    continuationToken).ConfigureAwait(false);

                            if (continued.IsSuccess)
                            {
                                ordinaryResult = continued.Value;

                                return Result.Success();
                            }

                            return Result.Failure(continued.Error);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DataRetentionLeaseLostException ex)
            {
                // The ordinary continuation proves its exact owner by reading the launch row back,
                // and the coordinator rethrows that refusal. It is a lost owner, not an unexpected
                // failure: there is no owner left to record a typed failure under, so recovery
                // reconciles the row, exactly as the recovery arms report the same loss.
                logger.LogWarning(
                    ex,
                    "Healthy-catalog factory erasure lost its exact durable owner; recovery must reconcile it.");

                return Result<DataRetentionApplyResult>.Failure(CovenantMaintenanceFailure());
            }

            if (erased.IsFailure)
            {
                return await FailCovenantResetAsync(
                    launched,
                    ownerId,
                    erased.Error,
                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false);
            }

            if (erased.Value.Disposition is CovenantExclusiveLeaseDisposition.RollbackAndReopen)
            {
                return await FailCovenantResetAsync(
                    launched,
                    ownerId,
                    CovenantResetFailure(erased.Value.BlockingErrorCode),
                    LongRunningOperationState.Failed).ConfigureAwait(false);
            }

            if (erased.Value.Disposition is CovenantExclusiveLeaseDisposition.KeepClosed)
            {
                return Result<DataRetentionApplyResult>.Failure(
                    CovenantResetFailure(erased.Value.BlockingErrorCode));
            }

            if (erased.Value.Disposition is not CovenantExclusiveLeaseDisposition.CommitAndReopen
                || ordinaryResult is null)
            {
                return await FailCovenantResetAsync(
                    launched,
                    ownerId,
                    CovenantResetFailure(erased.Value.BlockingErrorCode),
                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false);
            }

            Result completed = await CompleteCovenantResetAsync(
                operation.Id,
                ownerId).ConfigureAwait(false);

            if (completed.IsFailure)
            {
                return await FailCovenantResetAsync(
                    launched,
                    ownerId,
                    completed.Error,
                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false);
            }

            return Result<DataRetentionApplyResult>.Success(
                ordinaryResult with
                {
                    PlanId = current.PlanId,

                    RequestedOperationId = request.RequestedOperationId,
                });
        }
        catch (OperationCanceledException)
        {
            if (operation is not null && !string.IsNullOrWhiteSpace(ownerId))
            {
                await TryParkCancelledCovenantResetAsync(operation, ownerId).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Healthy-catalog factory erasure failed unexpectedly after durable operation admission.");

            if (operation is null || string.IsNullOrWhiteSpace(ownerId))
            {
                return Result<DataRetentionApplyResult>.Failure(CovenantMaintenanceFailure());
            }

            return await FailUnexpectedCovenantResetAsync(operation, ownerId).ConfigureAwait(false);
        }
        finally
        {
            if (planningLease is not null)
            {
                _ = await TryDisposeCovenantPlanningLeaseAsync(planningLease).ConfigureAwait(false);
            }

            _ = await CloseFactoryServiceConnectionAsync().ConfigureAwait(false);
        }
    }

    // A live admitted inference must finish during stage one. Keep its diagnostic in the
    // confirmed plan; the closed pre-canonical snapshot refuses any Running row left by drain.
    // Every other conflict remains a pre-launch refusal.
    private static DataRetentionConflict? FirstUndrainableFactoryConflict(DataRetentionPlan plan) =>
        plan.Conflicts.FirstOrDefault(static conflict => conflict.Code != "Data.InferenceRunActive");

    // What the lease-maintained segment of a factory erasure hands to the closed period: either the
    // typed refusal it has already recorded durably, or the committed launch row and the checkpoint
    // the coordinator resumes from.
    private sealed record FactoryErasureLaunch(
        LongRunningOperation? Committed,
        CovenantErasureCheckpointState? Checkpoint,
        Result<DataRetentionApplyResult>? Refusal)
    {
        internal static FactoryErasureLaunch Published(
            LongRunningOperation committed,
            CovenantErasureCheckpointState checkpoint) =>
            new(committed, checkpoint, null);

        internal static FactoryErasureLaunch Refused(Result<DataRetentionApplyResult> refusal) =>
            new(null, null, refusal);
    }

    private static Result<DataRetentionApplyResult> MapFactoryErasureReplay(
        LongRunningOperationRequestIdentityMatch match,
        string planId,
        CovenantDigest applyDigest)
    {
        if (!string.Equals(
                match.Operation.Kind,
                LongRunningOperationKinds.DataRetentionFactoryReset,
                StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                match.Identity.ApplyRequestDigest.Bytes,
                applyDigest.Bytes))
        {
            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Security.IdempotencyConflict,
                    "This operation identity was already used for a different request."));
        }

        if (match.Operation.State is LongRunningOperationState.Completed)
        {
            return Result<DataRetentionApplyResult>.Success(
                new DataRetentionApplyResult(
                    match.Operation.Id,
                    planId,
                    RowsDeleted: 0,
                    FilesDeleted: 0,
                    EstimatedBytesDeleted: 0,
                    DerivedRecordsDeleted: 0,
                    Reconciled: true,
                    Blockers: [],
                    Conflicts: [],
                    match.Identity.RequestedOperationId));
        }

        if (match.Operation.State is LongRunningOperationState.Pending
            or LongRunningOperationState.Running
            or LongRunningOperationState.Waiting
            or LongRunningOperationState.Cancelling)
        {
            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Security.IdempotencyInProgress,
                    "The requested factory erasure is still in progress."));
        }

        return Result<DataRetentionApplyResult>.Failure(
            new Error(
                match.Operation.TerminalErrorCode ?? ErrorCodes.Data.ReconciliationFailed,
                "The requested factory erasure did not complete successfully."));
    }

    private async Task<Result<DataRetentionApplyResult>> ContinueFactoryResetAsync(
        Guid operationId,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        try
        {
            DataRetentionPlan plan = await BuildFactoryResetPlanCoreAsync(
                new DataRetentionRequest(DataRetentionOperation.FactoryReset),
                operationId,
                cancellationToken).ConfigureAwait(false);

            if (plan.Blockers.Length > 0)
            {
                return Result<DataRetentionApplyResult>.Failure(
                    new Error(ErrorCodes.Data.Blocked, plan.Blockers[0].Message));
            }

            if (plan.Conflicts.Length > 0)
            {
                return Result<DataRetentionApplyResult>.Failure(
                    new Error(ErrorCodes.Data.Conflict, plan.Conflicts[0].Message));
            }

            DataRetentionApplyResult applied = await ApplyFactoryResetAsync(
                operationId,
                leaseOwner,
                plan,
                cancellationToken).ConfigureAwait(false);

            if (!applied.Reconciled)
            {
                return Result<DataRetentionApplyResult>.Failure(
                    new Error(
                        ErrorCodes.Data.ReconciliationFailed,
                        "Factory reset retained owned data after its ordinary cleanup."));
            }

            Result connectionClosed = await CloseFactoryServiceConnectionAsync().ConfigureAwait(false);

            return connectionClosed.IsSuccess
                ? Result<DataRetentionApplyResult>.Success(applied)
                : Result<DataRetentionApplyResult>.Failure(connectionClosed.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DataRetentionLeaseLostException)
        {
            throw;
        }
        catch (RetentionBlockedException ex)
        {
            return Result<DataRetentionApplyResult>.Failure(
                new Error(ErrorCodes.Data.Blocked, ex.Message));
        }
        catch (RetentionConflictException ex)
        {
            return Result<DataRetentionApplyResult>.Failure(
                new Error(ErrorCodes.Data.Conflict, ex.Message));
        }
        catch (RetentionQuarantineRecoveryRequiredException ex)
        {
            logger.LogWarning(
                ex,
                "Factory-reset ordinary cleanup requires durable reconciliation for operation {OperationId}.",
                operationId);

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.ReconciliationFailed,
                    "Factory-reset ordinary cleanup requires durable reconciliation."));
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Factory-reset ordinary cleanup failed for durable operation {OperationId}.",
                operationId);

            return Result<DataRetentionApplyResult>.Failure(
                new Error(
                    ErrorCodes.Data.ReconciliationFailed,
                    "Factory-reset ordinary cleanup could not be completed safely."));
        }
        finally
        {
            _ = await CloseFactoryServiceConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lets go of the service's own ledger handle before a closed period needs the file.
    /// </summary>
    /// <remarks>
    /// It reports rather than proves now, and the difference matters. Inside a closed period the
    /// coordinator owns this exact connection: it opens it for each durable window and closes it
    /// again before the next exclusive maintenance open, precisely so a live ledger handle cannot
    /// contend with the erasure's own lock. A close asserted here would then be asserting against
    /// the window rather than against a leak - failing whenever the coordinator legitimately had the
    /// connection open, which is every time this runs as a continuation.
    ///
    /// <para>Nothing is lost by not asserting. What the assertion was protecting is proved later and
    /// better: the storage health proof enumerates the database's own directory and refuses on any
    /// surviving sidecar, which is what a handle this process failed to close actually leaves behind.</para>
    /// </remarks>
    private async Task<Result> CloseFactoryServiceConnectionAsync()
    {
        try
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "The data-retention service connection could not be closed for Covenant handle proof.");

            return Result.Failure(CovenantMaintenanceFailure());
        }
    }
}
