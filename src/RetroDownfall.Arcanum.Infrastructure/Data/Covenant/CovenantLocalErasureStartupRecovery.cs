using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// What a pre-readiness local-erasure pass found and resolved.
/// </summary>
/// <remarks>
/// Closed and numbered because the outcome decides whether the host may become ready.
/// <see cref="ManualEvidenceReady"/> and <see cref="Blocked"/> are the pair that matters: a proven
/// mismatch is a terminal fact an operator has to act on and deliberately does not block unrelated
/// startup, while an unreadable row, a missing producer, or a storage failure means the pass cannot
/// say what is on disk and readiness would be a claim nobody can support.
/// </remarks>
internal enum CovenantLocalErasureStartupRecoveryOutcome : byte
{

    NoActiveWork = 1,

    ReconciledReady = 2,

    ManualEvidenceReady = 3,

    Blocked = 4,

}

/// <summary>
/// The sole pre-readiness adopter of unfinished managed-file erasure work.
/// </summary>
/// <remarks>
/// A file deletion is an effect SQLite cannot roll back, so a crash between the unlink and the label
/// removal leaves a row whose truth is on disk rather than in the database. This pass is what makes
/// that observable: it runs under the installation lock the caller already holds, before any pool,
/// optional Covenant service, workspace writer, worker, endpoint, or readiness signal, and it can only
/// finish work the database already authorized.
/// </remarks>
internal interface ICovenantLocalErasureStartupRecovery
{

    Task<Result<CovenantLocalErasureStartupRecoveryOutcome>> RecoverBeforeReadinessAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection connection,
        CancellationToken cancellationToken);

}

/// <summary>
/// The production pre-readiness local-erasure recovery.
/// </summary>
/// <remarks>
/// It never reconstructs the ordinary write lease or the operator context the crashed operation held.
/// Those are capabilities, not facts, and a restart cannot honestly reissue one. The narrowly scoped
/// restart authority is instead the pair the database and the operating system can both still prove:
/// a durable work item the insert guard already authorized, plus the caller's live installation lock.
///
/// <para>It can only advance <c>Prepared -&gt; DeletionVerified -&gt; Completed</c> or terminalize a
/// proven mismatch. It cannot begin a new erasure, change a root, revision, parent segment, parent
/// identity, artifact, or label, substitute ownership, or delete any other file (§10.17).</para>
/// </remarks>
internal sealed class CovenantLocalErasureStartupRecovery(ManagedFileErasureStateMachine stateMachine)
    : ICovenantLocalErasureStartupRecovery
{

    private readonly ManagedFileErasureStateMachine _stateMachine =
        stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));

    public async Task<Result<CovenantLocalErasureStartupRecoveryOutcome>> RecoverBeforeReadinessAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(connection);

        // Asserted, never acquired and never disposed. The caller owns this lock for the whole
        // bootstrap, and a recovery pass that reacquired it would either deadlock against the owner or
        // release an installation claim the rest of startup is still relying on.
        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<IReadOnlyList<LocalErasureWorkItemRow>> pending = await LocalErasureWorkItemStore
            .ListNonTerminalAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (pending.IsFailure)
        {

            return Blocked();

        }

        if (pending.Value.Count == 0)
        {

            return Result<CovenantLocalErasureStartupRecoveryOutcome>.Success(
                CovenantLocalErasureStartupRecoveryOutcome.NoActiveWork);

        }

        bool manual = false;

        foreach (LocalErasureWorkItemRow item in pending.Value)
        {

            Result<CovenantArtifactErasureProgress> resolved = await _stateMachine.ResolveAsync(
                connection,
                item,
                CovenantSqliteAuthorizationKind.SensitivityRetentionPurge,
                revalidator: null,
                cancellationToken).ConfigureAwait(false);

            if (resolved.IsFailure)
            {

                return Blocked();

            }

            switch (resolved.Value.Blocker)
            {

                case CovenantErasureBlocker.None:

                    continue;

                case CovenantErasureBlocker.ManualOwnershipMismatch:

                    manual = true;

                    continue;

                default:

                    return Blocked();

            }

        }

        return Result<CovenantLocalErasureStartupRecoveryOutcome>.Success(
            manual
                ? CovenantLocalErasureStartupRecoveryOutcome.ManualEvidenceReady
                : CovenantLocalErasureStartupRecoveryOutcome.ReconciledReady);

    }

    private static Result<CovenantLocalErasureStartupRecoveryOutcome> Blocked() =>
        Result<CovenantLocalErasureStartupRecoveryOutcome>.Success(
            CovenantLocalErasureStartupRecoveryOutcome.Blocked);

}
