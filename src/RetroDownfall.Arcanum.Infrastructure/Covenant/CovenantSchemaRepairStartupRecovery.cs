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
/// <see cref="KeptClosed"/> is not a failure code. It is the verdict that the journal is still active,
/// which is the only safe state when a repair's durable outcome cannot be established. Startup does not
/// stop on it: the bootstrapper logs one warning and goes on to publish readiness, so the process serves
/// with Covenant admission shut behind the recovery owner this pass adopted. A later start resumes
/// only a proven health/finalization suffix; unresolved Prepared effects need manual recovery and are
/// never re-executed. (A restore's kept-closed verdict is the one that stops startup.) Admission is shut in
/// every case but one: when only the one-shot post-disposition finalizer failed after the gate had
/// already applied a reopening disposition, the gate is open in this process and serves, and the journal
/// stays at <c>ReopenPending</c> for the next start to finish.
/// </remarks>
internal enum CovenantSchemaRepairStartupRecoveryOutcome : byte
{
    NoActiveJournal = 1,

    RecoveredReady = 2,

    KeptClosed = 3,
}

/// <summary>
/// The exact effect-free journal decision prepared before any protected startup recovery acts.
/// </summary>
internal sealed record CovenantSchemaRepairStartupRecoveryPreparation(
    CovenantSchemaRepairIntent? Intent);

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
    Task<Result<CovenantSchemaRepairStartupRecoveryPreparation>> PrepareBeforeEffectsAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection connection,
        CancellationToken cancellationToken);

    Task<Result<CovenantSchemaRepairStartupRecoveryOutcome>> RecoverPreparedAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection connection,
        CovenantSchemaRepairStartupRecoveryPreparation preparation,
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
/// keeps the adopted owner's Covenant admission shut for the process (§10.17).</para>
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

    public async Task<Result<CovenantSchemaRepairStartupRecoveryPreparation>> PrepareBeforeEffectsAsync(
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
            return Result<CovenantSchemaRepairStartupRecoveryPreparation>.Failure(active.Error);
        }

        if (active.Value is not { } intent)
        {
            return Result<CovenantSchemaRepairStartupRecoveryPreparation>.Success(
                new CovenantSchemaRepairStartupRecoveryPreparation(Intent: null));
        }

        // The gate holds no journal and cannot re-read one, so the owner this journal proves has to be
        // recorded before it can be resumed. Adoption is refused outright once readiness is published,
        // which is exactly why this pass runs before it.
        try
        {
            _gate.AdoptDurableRecoveryOwner(intent.Owner, scope: null, cleanupOnlyHistoricalCampaign: false);
        }
        catch (InvalidOperationException exception)
        {
            CovenantSchemaRepairDiagnostics.Warn(CovenantSchemaRepairStage.OwnerAdoption,
                CovenantSchemaRepairFailure.OwnerAdoptionRefused, intent, cause: exception);

            return PreparationFailure();
        }
        catch (ArgumentException exception)
        {
            bool emptyIdentifier = intent.OperationId == Guid.Empty;

            CovenantSchemaRepairDiagnostics.Warn(CovenantSchemaRepairStage.OwnerAdoption,
                emptyIdentifier ? CovenantSchemaRepairFailure.InvalidOwnerIdentifier : CovenantSchemaRepairFailure.OwnerAdoptionRefused,
                intent, cause: exception);

            return PreparationFailure(emptyIdentifier);
        }

        return Result<CovenantSchemaRepairStartupRecoveryPreparation>.Success(
            new CovenantSchemaRepairStartupRecoveryPreparation(intent));
    }

    public async Task<Result<CovenantSchemaRepairStartupRecoveryOutcome>> RecoverPreparedAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection connection,
        CovenantSchemaRepairStartupRecoveryPreparation preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(preparation);

        if (connection.GetType() != typeof(SqliteConnection))
        {
            throw new InvalidOperationException(
                "Schema repair recovery requires an exact SQLite connection.");
        }

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        if (preparation.Intent is not { } intent)
        {
            return Result<CovenantSchemaRepairStartupRecoveryOutcome>.Success(
                CovenantSchemaRepairStartupRecoveryOutcome.NoActiveJournal);
        }

        Result<CovenantExclusiveLease> resumed = await _gate
            .ResumeExclusiveAsync(intent.Owner, cancellationToken)
            .ConfigureAwait(false);

        if (resumed.IsFailure)
        {
            return Kept(CovenantSchemaRepairStage.Resume, resumed.Error, intent);
        }

        await using CovenantExclusiveLease lease = resumed.Value;

        CovenantSchemaRepairResult<CovenantSchemaRepairInspection> inspected = await _executor
            .InspectAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (inspected.IsFailure)
        {
            return Kept(CovenantSchemaRepairStage.Inspect, inspected.Error, intent, inspected.Fault);
        }

        CovenantSchemaRepairIntent current = intent;

        bool durablyMutated = current.Phase is CovenantSchemaRepairPhase.CatalogCommitted or CovenantSchemaRepairPhase.HealthVerified
            || current.Phase == CovenantSchemaRepairPhase.ReopenPending && current.Revision == 3;

        if (current.Phase == CovenantSchemaRepairPhase.Prepared)
        {
            // The catalog this journal describes is the only one it may repair. A digest that has
            // moved means somebody else changed the schema, and the journal no longer describes
            // reality.
            if (inspected.Value.CatalogDigest != current.InspectedCatalogDigest)
            {
                return Kept(CovenantSchemaRepairStage.CatalogMoved, intent: current);
            }

            // A matching catalog does not prove an interrupted action had no data effects.
            // Only this pure dispatch refusal proves that the original action could not enter a mutator.
            if (!CovenantSchemaRepairExecution.RefusesBeforeAnyMutator(current.Action, inspected.Value))
            {
                return Kept(CovenantSchemaRepairStage.PreparedEffectsUnknown, intent: current);
            }

            CovenantSchemaRepairResult<CovenantSchemaRepairIntent?> advanced = await AdvanceAsync(
                connection,
                current,
                durablyMutated
                    ? CovenantSchemaRepairPhase.CatalogCommitted
                    : CovenantSchemaRepairPhase.ReopenPending,
                cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure || advanced.Value is not { } afterRepair)
            {
                return Kept(CovenantSchemaRepairStage.AdvanceAfterRepair, advanced.IsFailure ? advanced.Error : null, current, advanced.Fault);
            }

            current = afterRepair;
        }

        if (current.Phase == CovenantSchemaRepairPhase.CatalogCommitted)
        {
            if (!inspected.Value.CanonicalValid)
            {
                return Kept(CovenantSchemaRepairStage.CanonicalInvalid, intent: current);
            }

            CovenantSchemaRepairResult<CovenantSchemaRepairIntent?> verified = await AdvanceAsync(
                connection,
                current,
                CovenantSchemaRepairPhase.HealthVerified,
                cancellationToken).ConfigureAwait(false);

            if (verified.IsFailure || verified.Value is not { } afterHealth)
            {
                return Kept(CovenantSchemaRepairStage.AdvanceAfterHealth, verified.IsFailure ? verified.Error : null, current, verified.Fault);
            }

            current = afterHealth;
        }

        if (current.Phase == CovenantSchemaRepairPhase.HealthVerified)
        {
            CovenantSchemaRepairResult<CovenantSchemaRepairIntent?> pending = await AdvanceAsync(
                connection,
                current,
                CovenantSchemaRepairPhase.ReopenPending,
                cancellationToken).ConfigureAwait(false);

            if (pending.IsFailure || pending.Value is not { } afterPending)
            {
                return Kept(CovenantSchemaRepairStage.AdvanceToReopen, pending.IsFailure ? pending.Error : null, current, pending.Fault);
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

        CovenantSchemaRepairResult<bool>? finalization = null;

        CovenantSchemaRepairPostDispositionFinalizer finalizer = new(async (phase, token) =>
        {
            finalization = await AdvanceToTerminalAsync(connection, terminal, phase, token).ConfigureAwait(false);

            return finalization;
        });

        // The repair is over and its evidence selected this disposition, so completing it takes no token.
        Result closed = await lease.CompleteAsync(disposition, finalizer, CancellationToken.None).ConfigureAwait(false);

        // A failed completion is either the gate refusing the disposition or, after the gate had already
        // acted, the finalizer failing; the lease reports either as the same failure and names only the
        // exception type, so the code is the one record of why this verdict is KeptClosed.
        if (closed.IsFailure)
        {
            return Kept(CovenantSchemaRepairStage.Complete, closed.Error, current, finalization?.Fault);
        }

        return disposition == CovenantExclusiveLeaseDisposition.KeepClosed
            ? Kept(CovenantSchemaRepairStage.EvidenceSelectedKeepClosed, intent: current)
            : Result<CovenantSchemaRepairStartupRecoveryOutcome>.Success(
                CovenantSchemaRepairStartupRecoveryOutcome.RecoveredReady);
    }

    /// <summary>
    /// Compatibility seam for the focused journal tests. Startup composition uses the two explicit
    /// passes above so protected effects cannot begin before every durable owner is resolved.
    /// </summary>
    internal async Task<Result<CovenantSchemaRepairStartupRecoveryOutcome>> RecoverBeforeReadinessAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        Result<CovenantSchemaRepairStartupRecoveryPreparation> prepared = await PrepareBeforeEffectsAsync(
            heldInstallationLock,
            guardedDirectory,
            connection,
            cancellationToken).ConfigureAwait(false);

        return prepared.IsFailure
            ? Result<CovenantSchemaRepairStartupRecoveryOutcome>.Failure(prepared.Error)
            : await RecoverPreparedAsync(
                heldInstallationLock,
                guardedDirectory,
                connection,
                prepared.Value,
                cancellationToken).ConfigureAwait(false);
    }

    private async Task<CovenantSchemaRepairResult<CovenantSchemaRepairIntent?>> AdvanceAsync(
        SqliteConnection connection,
        CovenantSchemaRepairIntent intent,
        CovenantSchemaRepairPhase next,
        CancellationToken cancellationToken)
    {
        CovenantSchemaRepairResult<bool> advanced = await CovenantSchemaRepairJournal.TryAdvanceAsync(
            connection,
            _initializer,
            intent,
            next,
            lastDurableErrorCode: null,
            _time.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        if (advanced.IsFailure)
        {
            return CovenantSchemaRepairResult<CovenantSchemaRepairIntent?>.Failure(advanced.Error, advanced.Fault);
        }

        return Result<CovenantSchemaRepairIntent?>.Success(
            advanced.Value
                ? intent with { Phase = next, Revision = intent.Revision + 1 }
                : null);
    }

    private async Task<CovenantSchemaRepairResult<bool>> AdvanceToTerminalAsync(
        SqliteConnection connection,
        CovenantSchemaRepairIntent intent,
        CovenantSchemaRepairPhase terminal,
        CancellationToken cancellationToken)
    {
        CovenantSchemaRepairResult<CovenantSchemaRepairIntent?> advanced = await AdvanceAsync(
            connection, intent, terminal, cancellationToken).ConfigureAwait(false);

        if (advanced.IsFailure)
        {
            return CovenantSchemaRepairResult<bool>.Failure(advanced.Error, advanced.Fault);
        }

        return advanced.Value is null
            ? Result<bool>.Failure(new Error(ErrorCodes.Covenant.RevisionConflict,
                "The schema repair journal moved before its finalizer ran."))
            : Result<bool>.Success(true);
    }

    /// <summary>
    /// Reports closed stage/reason and validated owner facts with numerical storage diagnostics.
    /// Driver messages, journal bytes and arbitrary collaborator data never enter the warning.
    /// </summary>
    private static Result<CovenantSchemaRepairStartupRecoveryOutcome> Kept(
        CovenantSchemaRepairStage stage, Error? error = null, CovenantSchemaRepairIntent? intent = null,
        CovenantSchemaRepairStorageFault? fault = null)
    {
        CovenantSchemaRepairDiagnostics.Warn(stage,
            stage == CovenantSchemaRepairStage.PreparedEffectsUnknown
                ? CovenantSchemaRepairFailure.EffectsUnproven : CovenantSchemaRepairFailure.RecoveryRefused,
            intent, error?.Code, error: error, fault: fault);

        return Result<CovenantSchemaRepairStartupRecoveryOutcome>.Success(
            CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed);
    }

    private static Result<CovenantSchemaRepairStartupRecoveryPreparation> PreparationFailure(
        bool emptyIdentifier = false) =>
        Result<CovenantSchemaRepairStartupRecoveryPreparation>.Failure(
            new Error(ErrorCodes.Covenant.MaintenanceFailed,
                emptyIdentifier
                    ? "Covenant schema-repair ownership could not be reconstructed safely: operation identifier cannot be empty. Preserve the profile and seek manual recovery."
                    : "Covenant schema-repair ownership could not be reconstructed safely. Preserve the profile and seek manual recovery."));
}
