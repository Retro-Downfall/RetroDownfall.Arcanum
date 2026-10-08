using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// What the pre-database half of startup recovery proved about this profile's filesystem.
/// </summary>
/// <remarks>
/// <see cref="TopologyReady"/> is a statement about renames and nothing else. It does not say the
/// replacement is healthy, that its authority may be published, or that admission may reopen — those
/// are the second phase's answers, and keeping them out of this enum is what stops a converged tree
/// from being mistaken for a finished restore (§10.19.8).
/// </remarks>
internal enum BackupRestorePhysicalRecoveryOutcome : byte
{
    NoActiveJournal = 1,

    TopologyReady = 2,

    KeptClosed = 3,
}

/// <summary>
/// How the authority half of startup recovery left this installation.
/// </summary>
/// <remarks>
/// <see cref="KeptClosed"/> is not a failure code. It is startup's verdict that a restore journal is
/// still active and neither the host nor the CLI may publish readiness. Admission is shut in every case
/// but one: when only the one-shot post-disposition finalizer failed after the gate had already
/// reopened, the gate is open in this process, which stops at this verdict and never serves it, and the
/// next start resumes the same journal.
/// <see cref="ReconciliationRequired"/> is the other way startup stops: the authenticated restore is
/// finished and its anchor closed, but the plain journal kept beside it records local secrets the
/// restore's own rollback could not reinstate, so readiness waits for an operator. Only
/// <see cref="NoActiveJournal"/> and <see cref="RecoveredReady"/> let startup continue.
/// </remarks>
internal enum BackupRestoreStartupRecoveryOutcome : byte
{
    NoActiveJournal = 1,

    RecoveredReady = 2,

    KeptClosed = 3,

    ReconciliationRequired = 4,
}

/// <summary>
/// The two-phase pre-readiness resumer of an interrupted restore.
/// </summary>
/// <remarks>
/// The split exists because the two phases cannot assume the same things. A machine that died between
/// the live-root and staged-root renames has no live database to open, so topology has to converge
/// first, from evidence that lives outside the tree being replaced. Only then is there something to
/// classify, install core objects into, and finally resume authority against.
///
/// <para>Both methods assert and borrow the caller's one live <see cref="ArcanumMaintenanceLock"/>.
/// Neither reacquires, replaces, transfers, or disposes it: nesting a second <c>FileShare.None</c> open
/// inside the startup the first one guards would deadlock.</para>
/// </remarks>
internal interface IBackupRestoreStartupRecovery
{
    /// <summary>
    /// Converges the filesystem to exactly one journal-selected live root, before any database opens.
    /// </summary>
    Task<Result<BackupRestorePhysicalRecoveryOutcome>> RecoverPhysicalTopologyBeforeDatabaseAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resumes the exact exclusive owner an interrupted restore left, before any readiness is published.
    /// </summary>
    Task<Result<BackupRestoreStartupRecoveryOutcome>> RecoverAuthorityBeforeReadinessAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken);
}
