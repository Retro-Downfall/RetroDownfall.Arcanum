using System.Data;

using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

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

            if (current.Conflicts.Length > 0)
            {

                return Result<DataRetentionApplyResult>.Failure(
                    new Error(ErrorCodes.Data.Conflict, current.Conflicts[0].Message));

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

            return await _leaseMaintainer.RunAsync(
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

            if (revalidated.Blockers.Length > 0 || revalidated.Conflicts.Length > 0)
            {

                Error refusal = revalidated.Blockers.Length > 0
                    ? new Error(ErrorCodes.Data.Blocked, revalidated.Blockers[0].Message)
                    : new Error(ErrorCodes.Data.Conflict, revalidated.Conflicts[0].Message);

                return await FailCovenantResetAsync(
                    operation,
                    ownerId,
                    refusal,
                    LongRunningOperationState.Failed).ConfigureAwait(false);

            }

            if (!string.Equals(revalidated.PlanId, current.PlanId, StringComparison.Ordinal))
            {

                return await FailCovenantResetAsync(
                    operation,
                    ownerId,
                    new Error(
                        ErrorCodes.Data.PlanChanged,
                        "The deletion plan changed after preview; request a new dry-run before applying."),
                    LongRunningOperationState.Failed).ConfigureAwait(false);

            }

            Result currentLease = await installationLease
                .RevalidateAsync(maintainedCancellation.Token)
                .ConfigureAwait(false);

            if (currentLease.IsFailure)
            {

                return await FailCovenantResetAsync(
                    operation,
                    ownerId,
                    currentLease.Error,
                    LongRunningOperationState.Failed).ConfigureAwait(false);

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

                return await FailCovenantResetAsync(
                    operation,
                    ownerId,
                    prepared.Error,
                    LongRunningOperationState.Failed).ConfigureAwait(false);

            }

            LongRunningOperation? committed = await operations
                .GetAsync(operation.Id, maintainedCancellation.Token)
                .ConfigureAwait(false);

            if (committed?.CheckpointPayload is not { Length: > 0 } payload)
            {

                return await FailCovenantResetAsync(
                    operation,
                    ownerId,
                    new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "The committed factory-erasure checkpoint could not be reloaded."),
                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false);

            }

            Result<CovenantErasureCheckpointState> checkpoint =
                CovenantErasureCheckpointState.FromFactoryResetCheckpoint(
                    committed.Id,
                    committed.CheckpointVersion,
                    payload);

            if (checkpoint.IsFailure || checkpoint.Value.Owner != prepared.Value.Owner)
            {

                Error invalid = checkpoint.IsFailure
                    ? checkpoint.Error
                    : new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "The committed factory-erasure checkpoint did not preserve its admitted owner.");

                return await FailCovenantResetAsync(
                    committed,
                    ownerId,
                    invalid,
                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false);

            }

            Result planningLeaseReleased = await TryDisposeCovenantPlanningLeaseAsync(
                installationLease).ConfigureAwait(false);

            planningLease = null;

            if (planningLeaseReleased.IsFailure)
            {

                return await FailCovenantResetAsync(
                    committed,
                    ownerId,
                    planningLeaseReleased.Error,
                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false);

            }

            Result connectionClosed = await CloseFactoryServiceConnectionAsync().ConfigureAwait(false);

            if (connectionClosed.IsFailure)
            {

                return await FailCovenantResetAsync(
                    committed,
                    ownerId,
                    connectionClosed.Error,
                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false);

            }

            DataRetentionApplyResult? ordinaryResult = null;

            Result<CovenantErasureCompletion> erased = await _covenantErasureCoordinator
                .RunAsync(
                    committed,
                    checkpoint.Value,
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
                    maintainedCancellation.Token)
                .ConfigureAwait(false);

            if (erased.IsFailure)
            {

                return await FailCovenantResetAsync(
                    committed,
                    ownerId,
                    erased.Error,
                    LongRunningOperationState.ReconciliationRequired).ConfigureAwait(false);

            }

            if (erased.Value.Disposition is CovenantExclusiveLeaseDisposition.RollbackAndReopen)
            {

                return await FailCovenantResetAsync(
                    committed,
                    ownerId,
                    CovenantResetFailure(erased.Value.BlockingErrorCode),
                    LongRunningOperationState.Failed).ConfigureAwait(false);

            }

            if (erased.Value.Disposition is not CovenantExclusiveLeaseDisposition.CommitAndReopen
                || ordinaryResult is null)
            {

                return await FailCovenantResetAsync(
                    committed,
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
                    committed,
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

    private async Task<Result> CloseFactoryServiceConnectionAsync()
    {

        try
        {

            await db.Database.CloseConnectionAsync().ConfigureAwait(false);

            return db.Database.GetDbConnection().State is ConnectionState.Closed
                ? Result.Success()
                : Result.Failure(CovenantMaintenanceFailure());

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
