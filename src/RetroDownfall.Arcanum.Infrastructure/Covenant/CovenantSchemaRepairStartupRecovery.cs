using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// How a pre-readiness schema-repair pass left the installation.
/// </summary>
/// <remarks>
/// <see cref="KeptClosed"/> is not a failure code. It is the honest report that admission is still
/// shut and the journal is still active, which is the only safe state when a repair's durable outcome
/// cannot be established.
/// </remarks>
internal enum CovenantSchemaRepairStartupRecoveryOutcome : byte
{

    NoActiveJournal = 1,

    RecoveredReady = 2,

    KeptClosed = 3,

}

/// <summary>
/// The sole pre-readiness resumer of an interrupted Covenant schema repair.
/// </summary>
/// <remarks>
/// Runs after the core-only install and before shared pools, optional Covenant services, transfer or
/// managed-file admission, workers, and readiness. The ordering is the point: a repair journal that is
/// still active means the catalog may be half-changed, and every one of those consumers would
/// otherwise open against it.
/// </remarks>
internal interface ICovenantSchemaRepairStartupRecovery
{

    Task<Result<CovenantSchemaRepairStartupRecoveryOutcome>> RecoverBeforeReadinessAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection connection,
        CancellationToken cancellationToken);

}

/// <summary>
/// The production pre-readiness schema-repair recovery.
/// </summary>
/// <remarks>
/// It reconstructs the exact <see cref="CovenantExclusiveRecoveryOwner"/> from immutable journal
/// fields and resumes the gate registration rather than acquiring a new one. Initial acquisition for
/// an existing intent would create a second owner for a scope that is already closed, and the gate
/// would then have two operations each believing it may reopen.
///
/// <para>Every phase resolves to exactly one disposition, and the one-shot finalizer runs only after
/// that disposition succeeds. A changed catalog digest, a changed effect identity, a failed health
/// publication, or any uncertainty about what the interrupted repair actually wrote returns
/// <see cref="CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed"/>, leaves the journal active, and
/// blocks bootstrap (§10.17).</para>
/// </remarks>
internal sealed class CovenantSchemaRepairStartupRecovery(
    CovenantOperationGate gate,
    ICovenantSchemaRepairExecutor executor,
    ICovenantSqliteConnectionInitializer initializer,
    TimeProvider timeProvider) : ICovenantSchemaRepairStartupRecovery
{

    private readonly CovenantOperationGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    private readonly ICovenantSchemaRepairExecutor _executor =
        executor ?? throw new ArgumentNullException(nameof(executor));

    private readonly ICovenantSqliteConnectionInitializer _initializer =
        initializer ?? throw new ArgumentNullException(nameof(initializer));

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<Result<CovenantSchemaRepairStartupRecoveryOutcome>> RecoverBeforeReadinessAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(connection);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<CovenantSchemaRepairIntent?> active = await CovenantSchemaRepairJournal
            .TryReadActiveAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (active.IsFailure)
        {

            return Kept();

        }

        if (active.Value is not { } intent)
        {

            return Result<CovenantSchemaRepairStartupRecoveryOutcome>.Success(
                CovenantSchemaRepairStartupRecoveryOutcome.NoActiveJournal);

        }

        // The gate holds no journal and cannot re-read one, so the owner this journal proves has to be
        // recorded before it can be resumed. Adoption is refused outright once readiness is published,
        // which is exactly why this pass runs before it.
        try
        {

            _gate.AdoptDurableRecoveryOwner(intent.Owner, scope: null, cleanupOnlyHistoricalCampaign: false);

        }
        catch (InvalidOperationException)
        {

            return Kept();

        }

        Result<CovenantExclusiveLease> resumed = await _gate
            .ResumeExclusiveAsync(intent.Owner, cancellationToken)
            .ConfigureAwait(false);

        if (resumed.IsFailure)
        {

            return Kept();

        }

        await using CovenantExclusiveLease lease = resumed.Value;

        Result<CovenantSchemaRepairInspection> inspected = await _executor
            .InspectAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (inspected.IsFailure)
        {

            return Kept();

        }

        CovenantSchemaRepairIntent current = intent;

        bool durablyMutated = current.Phase is not CovenantSchemaRepairPhase.Prepared;

        if (current.Phase == CovenantSchemaRepairPhase.Prepared)
        {

            // The catalog this journal describes is the only one it may repair. A digest that has
            // moved means somebody else changed the schema, and the journal no longer describes
            // reality.
            if (inspected.Value.CatalogDigest != current.InspectedCatalogDigest)
            {

                return Kept();

            }

            Result<bool> repaired = await _executor
                .RepairAsync(connection, current.Action, inspected.Value, cancellationToken)
                .ConfigureAwait(false);

            if (repaired.IsFailure)
            {

                return Kept();

            }

            durablyMutated = repaired.Value;

            if (durablyMutated)
            {

                // The health gate below decides whether admission may reopen, and the snapshot taken
                // above is the catalog the repair was needed for — it says "invalid" for every repair
                // that had anything to do. Re-inspect so the gate reads what the repair actually wrote,
                // exactly as the in-request sibling CovenantMaintenanceService.ExecuteRepairAsync does;
                // an inspection that cannot be taken is still uncertainty, and uncertainty keeps closed.
                Result<CovenantSchemaRepairInspection> verified = await _executor
                    .InspectAsync(connection, cancellationToken)
                    .ConfigureAwait(false);

                if (verified.IsFailure)
                {

                    return Kept();

                }

                inspected = verified;

            }

            Result<CovenantSchemaRepairIntent?> advanced = await AdvanceAsync(
                connection,
                current,
                durablyMutated
                    ? CovenantSchemaRepairPhase.CatalogCommitted
                    : CovenantSchemaRepairPhase.ReopenPending,
                cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure || advanced.Value is not { } afterRepair)
            {

                return Kept();

            }

            current = afterRepair;

        }

        if (current.Phase == CovenantSchemaRepairPhase.CatalogCommitted)
        {

            if (!inspected.Value.CanonicalValid)
            {

                return Kept();

            }

            Result<CovenantSchemaRepairIntent?> verified = await AdvanceAsync(
                connection,
                current,
                CovenantSchemaRepairPhase.HealthVerified,
                cancellationToken).ConfigureAwait(false);

            if (verified.IsFailure || verified.Value is not { } afterHealth)
            {

                return Kept();

            }

            current = afterHealth;

        }

        if (current.Phase == CovenantSchemaRepairPhase.HealthVerified)
        {

            Result<CovenantSchemaRepairIntent?> pending = await AdvanceAsync(
                connection,
                current,
                CovenantSchemaRepairPhase.ReopenPending,
                cancellationToken).ConfigureAwait(false);

            if (pending.IsFailure || pending.Value is not { } afterPending)
            {

                return Kept();

            }

            current = afterPending;

        }

        CovenantExclusiveLeaseDisposition disposition = CovenantExclusiveDisposition.Select(
            new CovenantExclusiveDispositionEvidence(
                StorageVerified: true,
                AuthorityVerified: true,
                DurablyMutated: durablyMutated,
                HealthPublished: !durablyMutated || inspected.Value.CanonicalValid));

        CovenantSchemaRepairIntent terminal = current;

        CovenantSchemaRepairPostDispositionFinalizer finalizer = new(
            (phase, token) => AdvanceToTerminalAsync(connection, terminal, phase, token));

        Result closed = await lease.CompleteAsync(disposition, finalizer, cancellationToken).ConfigureAwait(false);

        return closed.IsFailure || disposition == CovenantExclusiveLeaseDisposition.KeepClosed
            ? Kept()
            : Result<CovenantSchemaRepairStartupRecoveryOutcome>.Success(
                CovenantSchemaRepairStartupRecoveryOutcome.RecoveredReady);

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

    private static Result<CovenantSchemaRepairStartupRecoveryOutcome> Kept() =>
        Result<CovenantSchemaRepairStartupRecoveryOutcome>.Success(
            CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed);

}
