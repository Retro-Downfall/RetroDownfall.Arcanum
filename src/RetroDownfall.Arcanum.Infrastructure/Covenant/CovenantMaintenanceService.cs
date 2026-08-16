using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one implementation of the frozen Covenant maintenance port.
/// </summary>
/// <remarks>
/// Four methods, three owners. Schema repair is owned here because it is the only maintenance
/// operation whose whole lifecycle fits inside one request; index rebuild and family reinitialize are
/// delegated to their coordinators because both outlive the request that started them, and returning a
/// completed result for either would mean the HTTP connection was the only thing tracking a
/// database-replacing operation (§10.16).
///
/// <para>No request, result, enum, carrier, or JSON entry is defined here. Those were frozen with the
/// public contracts, and a second shape for the same effect is the defect that freeze exists to
/// prevent.</para>
/// </remarks>
internal sealed class CovenantMaintenanceService(
    ICovenantConnectionSource connections,
    ICovenantOperationGate gate,
    ICovenantSchemaRepairExecutor executor,
    ICovenantSqliteConnectionInitializer initializer,
    ICovenantAvailability availability,
    CovenantIndexRebuildCoordinator indexRebuild,
    TimeProvider timeProvider) : ICovenantMaintenanceService
{

    private const string OwnerId = "covenant-maintenance";

    private readonly ICovenantConnectionSource _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private readonly ICovenantOperationGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    private readonly ICovenantSchemaRepairExecutor _executor =
        executor ?? throw new ArgumentNullException(nameof(executor));

    private readonly ICovenantSqliteConnectionInitializer _initializer =
        initializer ?? throw new ArgumentNullException(nameof(initializer));

    private readonly ICovenantAvailability _availability =
        availability ?? throw new ArgumentNullException(nameof(availability));

    private readonly CovenantIndexRebuildCoordinator _indexRebuild =
        indexRebuild ?? throw new ArgumentNullException(nameof(indexRebuild));

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async ValueTask<Result<CovenantSchemaRepairResultDto>> RepairSchemaAsync(
        CovenantSchemaRepairRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result valid = request.Validate();

        if (valid.IsFailure)
        {

            return Result<CovenantSchemaRepairResultDto>.Failure(valid.Error);

        }

        SqliteConnection connection = await _connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        Result<CovenantSchemaRepairInspection> inspected = await _executor
            .InspectAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (inspected.IsFailure)
        {

            return Result<CovenantSchemaRepairResultDto>.Failure(inspected.Error);

        }

        Guid operationId = Guid.NewGuid();

        CovenantExclusiveRecoveryOwner owner = new(
            operationId,
            CovenantExclusiveOperation.SchemaRepair,
            inspected.Value.CatalogDigest);

        Result<CovenantExclusiveLease> acquired = await _gate
            .AcquireExclusiveAsync(owner, cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            return Result<CovenantSchemaRepairResultDto>.Failure(acquired.Error);

        }

        await using CovenantExclusiveLease lease = acquired.Value;

        CovenantSchemaRepairIntent intent = new(
            operationId,
            inspected.Value.CatalogDigest,
            inspected.Value.CatalogDigest,
            request.Action,
            Data.Schema.GrimoireSchemaTransactionTier.CovenantCanonical,
            request.Action == CovenantSchemaRepairAction.InstallAbsentCanonicalFamily
                ? null
                : _availability.Current.DatasetGeneration,
            Math.Max(1, lease.Snapshot.AuthorityEpoch),
            CovenantSchemaRepairPhase.Prepared,
            Revision: 0);

        // The journal is committed before the first repair statement. A journal written afterwards
        // would be a record of an operation that already happened, which recovery cannot tell apart
        // from one that never started.
        Result committed = await CovenantSchemaRepairJournal
            .CommitPreparedAsync(connection, _initializer, intent, _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        if (committed.IsFailure)
        {

            _ = await lease
                .CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, cancellationToken)
                .ConfigureAwait(false);

            return Result<CovenantSchemaRepairResultDto>.Failure(committed.Error);

        }

        return await ExecuteRepairAsync(connection, lease, intent, inspected.Value, cancellationToken)
            .ConfigureAwait(false);

    }

    public async ValueTask<Result<LongRunningOperationDto>> RebuildIndexAsync(
        CovenantIndexRebuildRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result valid = request.Validate();

        if (valid.IsFailure)
        {

            return Result<LongRunningOperationDto>.Failure(valid.Error);

        }

        // The caller's operation identity is deliberately not adopted. A rebuild has no authenticated
        // preflight and no apply digest, so a caller-named row would be a durable identity nothing can
        // ever replay against.
        Result<LongRunningOperation> started = await _indexRebuild
            .StartAsync(OwnerId, cancellationToken)
            .ConfigureAwait(false);

        return started.IsFailure
            ? Result<LongRunningOperationDto>.Failure(started.Error)
            : Result<LongRunningOperationDto>.Success(LongRunningOperationDto.FromOperation(started.Value));

    }

    public ValueTask<Result<CovenantFamilyReinitializePlanDto>> PrepareFamilyReinitializeAsync(
        CovenantFamilyReinitializePrepareRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result valid = request.Validate();

        // Planning needs the authenticated all-installation read snapshot and the preflight token
        // issuer, both of which arrive with the operator surfaces. Refusing with the frozen
        // manual-recovery code is honest; returning an unsigned plan would hand a caller a token that
        // nothing can later verify.
        return ValueTask.FromResult(
            Result<CovenantFamilyReinitializePlanDto>.Failure(
                valid.IsFailure
                    ? valid.Error
                    : new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "Covenant family reinitialize planning requires the operator surfaces.")));

    }

    public ValueTask<Result<LongRunningOperationDto>> ApplyFamilyReinitializeAsync(
        CovenantFamilyReinitializeApplyRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result valid = request.Validate();

        return ValueTask.FromResult(
            Result<LongRunningOperationDto>.Failure(
                valid.IsFailure
                    ? valid.Error
                    : new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "Covenant family reinitialize apply requires the operator surfaces.")));

    }

    /// <summary>
    /// Performs the repair, advances the journal one edge at a time, and selects exactly one
    /// disposition.
    /// </summary>
    private async Task<Result<CovenantSchemaRepairResultDto>> ExecuteRepairAsync(
        SqliteConnection connection,
        CovenantExclusiveLease lease,
        CovenantSchemaRepairIntent intent,
        CovenantSchemaRepairInspection inspected,
        CancellationToken cancellationToken)
    {

        Result<bool> repaired = await _executor
            .RepairAsync(connection, intent.Action, inspected, cancellationToken)
            .ConfigureAwait(false);

        if (repaired.IsFailure)
        {

            return await FinishAsync(
                connection,
                lease,
                intent,
                CovenantExclusiveDisposition.Select(
                    new CovenantExclusiveDispositionEvidence(true, true, false, true)),
                CovenantSchemaRepairDisposition.ManualRecoveryRequired,
                inspected,
                repaired.Error.Code,
                cancellationToken).ConfigureAwait(false);

        }

        CovenantSchemaRepairIntent current = intent;

        if (repaired.Value)
        {

            Result<CovenantSchemaRepairIntent?> committed = await AdvanceAsync(
                connection,
                current,
                CovenantSchemaRepairPhase.CatalogCommitted,
                cancellationToken).ConfigureAwait(false);

            if (committed.IsFailure || committed.Value is not { } afterCommit)
            {

                return await FinishAsync(
                    connection,
                    lease,
                    current,
                    CovenantExclusiveLeaseDisposition.KeepClosed,
                    CovenantSchemaRepairDisposition.ManualRecoveryRequired,
                    inspected,
                    ErrorCodes.Covenant.MaintenanceFailed,
                    cancellationToken).ConfigureAwait(false);

            }

            current = afterCommit;

            Result<CovenantSchemaRepairInspection> verified = await _executor
                .InspectAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            if (verified.IsFailure || !verified.Value.CanonicalValid)
            {

                return await FinishAsync(
                    connection,
                    lease,
                    current,
                    CovenantExclusiveLeaseDisposition.KeepClosed,
                    CovenantSchemaRepairDisposition.ManualRecoveryRequired,
                    verified.IsSuccess ? verified.Value : inspected,
                    ErrorCodes.Covenant.IntegrityFailure,
                    cancellationToken).ConfigureAwait(false);

            }

            inspected = verified.Value;

            Result<CovenantSchemaRepairIntent?> healthy = await AdvanceAsync(
                connection,
                current,
                CovenantSchemaRepairPhase.HealthVerified,
                cancellationToken).ConfigureAwait(false);

            if (healthy.IsFailure || healthy.Value is not { } afterHealth)
            {

                return await FinishAsync(
                    connection,
                    lease,
                    current,
                    CovenantExclusiveLeaseDisposition.KeepClosed,
                    CovenantSchemaRepairDisposition.ManualRecoveryRequired,
                    inspected,
                    ErrorCodes.Covenant.MaintenanceFailed,
                    cancellationToken).ConfigureAwait(false);

            }

            current = afterHealth;

        }

        Result<CovenantSchemaRepairIntent?> pending = await AdvanceAsync(
            connection,
            current,
            CovenantSchemaRepairPhase.ReopenPending,
            cancellationToken).ConfigureAwait(false);

        if (pending.IsFailure || pending.Value is not { } afterPending)
        {

            return await FinishAsync(
                connection,
                lease,
                current,
                CovenantExclusiveLeaseDisposition.KeepClosed,
                CovenantSchemaRepairDisposition.ManualRecoveryRequired,
                inspected,
                ErrorCodes.Covenant.MaintenanceFailed,
                cancellationToken).ConfigureAwait(false);

        }

        CovenantExclusiveLeaseDisposition disposition = CovenantExclusiveDisposition.Select(
            new CovenantExclusiveDispositionEvidence(
                StorageVerified: true,
                AuthorityVerified: true,
                DurablyMutated: repaired.Value,
                HealthPublished: true));

        return await FinishAsync(
            connection,
            lease,
            afterPending,
            disposition,
            repaired.Value
                ? CovenantSchemaRepairDisposition.Repaired
                : CovenantSchemaRepairDisposition.AlreadyHealthy,
            inspected,
            blockingCode: null,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Applies the one disposition and, only on success, lets the journal reach a terminal phase.
    /// </summary>
    private async Task<Result<CovenantSchemaRepairResultDto>> FinishAsync(
        SqliteConnection connection,
        CovenantExclusiveLease lease,
        CovenantSchemaRepairIntent intent,
        CovenantExclusiveLeaseDisposition disposition,
        CovenantSchemaRepairDisposition reported,
        CovenantSchemaRepairInspection inspected,
        string? blockingCode,
        CancellationToken cancellationToken)
    {

        CovenantSchemaRepairPostDispositionFinalizer finalizer = new(
            (phase, token) => AdvanceToTerminalAsync(connection, intent, phase, token));

        Result closed = await lease
            .CompleteAsync(disposition, finalizer, cancellationToken)
            .ConfigureAwait(false);

        CovenantSchemaRepairPhase phase = closed.IsSuccess && finalizer.WasInvoked
            ? disposition == CovenantExclusiveLeaseDisposition.CommitAndReopen
                ? CovenantSchemaRepairPhase.Completed
                : CovenantSchemaRepairPhase.Abandoned
            : CovenantSchemaRepairPhase.ReopenPending;

        CovenantAvailabilitySnapshot snapshot = _availability.Current;

        return Result<CovenantSchemaRepairResultDto>.Success(
            new CovenantSchemaRepairResultDto(
                closed.IsSuccess ? reported : CovenantSchemaRepairDisposition.ManualRecoveryRequired,
                intent.Action,
                phase,
                inspected.CanonicalValid,
                inspected.AcceleratorValid,
                snapshot.Generation,
                inspected.CatalogFingerprint,
                closed.IsSuccess ? blockingCode : closed.Error.Code));

    }

    private async Task<Result<CovenantSchemaRepairIntent?>> AdvanceAsync(
        SqliteConnection connection,
        CovenantSchemaRepairIntent intent,
        CovenantSchemaRepairPhase next,
        CancellationToken cancellationToken)
    {

        Result<bool> advanced = await CovenantSchemaRepairJournal.TryAdvanceAsync(
            connection,
            _initializer,
            intent,
            next,
            lastDurableErrorCode: null,
            _time.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        if (advanced.IsFailure)
        {

            return Result<CovenantSchemaRepairIntent?>.Failure(advanced.Error);

        }

        return Result<CovenantSchemaRepairIntent?>.Success(
            advanced.Value
                ? intent with { Phase = next, Revision = intent.Revision + 1 }
                : null);

    }

    private async Task<Result> AdvanceToTerminalAsync(
        SqliteConnection connection,
        CovenantSchemaRepairIntent intent,
        CovenantSchemaRepairPhase terminal,
        CancellationToken cancellationToken)
    {

        Result<CovenantSchemaRepairIntent?> advanced = await AdvanceAsync(
            connection,
            intent,
            terminal,
            cancellationToken).ConfigureAwait(false);

        if (advanced.IsFailure)
        {

            return Result.Failure(advanced.Error);

        }

        return advanced.Value is null
            ? Result.Failure(
                new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    "The schema repair journal moved before its finalizer ran."))
            : Result.Success();

    }

}

/// <summary>
/// The bounded, content-free inventory a destructive Covenant plan is computed from.
/// </summary>
/// <remarks>
/// Counts only. An operator confirming the most destructive Covenant operation needs to know how much
/// is being lost, and showing them what is being lost would make the confirmation dialog the largest
/// single disclosure the feature has (§10.16).
/// </remarks>
internal sealed record CovenantProtectedInventory(
    long LabelCount,
    long TaintedSessionCount,
    long ManagedFileCount,
    long PendingLocalErasureCount,
    long ManualBlockerCount,
    long NonrevocableDisclosureCount);

/// <summary>
/// Bounded indexed counts over every protected surface, under a borrowed installation read lease.
/// </summary>
/// <remarks>
/// The lease is borrowed and revalidated, never acquired or disposed here: the endpoint takes it once
/// for the whole preflight and retains it through plan serialization, so a reset that lands mid-plan
/// is refused rather than discovered by a caller holding counts that no longer describe anything.
/// </remarks>
internal sealed class CovenantProtectedInventoryService(ICovenantConnectionSource connections)
{

    private readonly ICovenantConnectionSource _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    internal async Task<Result<CovenantProtectedInventory>> ReadAsync(
        CovenantInstallationReadLease lease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(lease);

        Result current = await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Result<CovenantProtectedInventory>.Failure(current.Error);

        }

        SqliteConnection connection = await _connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {

            return Result<CovenantProtectedInventory>.Success(
                new CovenantProtectedInventory(
                    await CountAsync(connection, "SELECT COUNT(*) FROM artifact_sensitivity;", cancellationToken)
                        .ConfigureAwait(false),
                    await CountAsync(
                        connection,
                        "SELECT COUNT(*) FROM session_sensitivity_state WHERE TaintedArtifactCount > 0;",
                        cancellationToken).ConfigureAwait(false),
                    await CountAsync(
                        connection,
                        "SELECT COUNT(*) FROM managed_file_write_intents WHERE PhaseCode = 7;",
                        cancellationToken).ConfigureAwait(false),
                    await CountAsync(
                        connection,
                        "SELECT COUNT(*) FROM local_erasure_work_items WHERE StateCode IN (1, 2);",
                        cancellationToken).ConfigureAwait(false),
                    await CountAsync(
                        connection,
                        "SELECT COUNT(*) FROM local_erasure_work_items WHERE StateCode = 4;",
                        cancellationToken).ConfigureAwait(false),
                    await CountAsync(
                        connection,
                        "SELECT COUNT(*) FROM external_disclosure_receipts WHERE RevocabilityCode = 2;",
                        cancellationToken).ConfigureAwait(false)));

        }
        catch (SqliteException exception)
        {

            return Result<CovenantProtectedInventory>.Failure(
                new Error(ErrorCodes.Covenant.MaintenanceFailed, exception.Message));

        }

    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

    }

}
