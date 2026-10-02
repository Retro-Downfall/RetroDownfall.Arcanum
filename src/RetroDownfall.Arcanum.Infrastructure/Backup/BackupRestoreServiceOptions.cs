using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The protected-transfer capabilities a selective import runs under when Covenant is enabled.
/// </summary>
/// <remarks>
/// Grouped and optional rather than constructor parameters, because with the gate off there is no
/// operation gate to drain and no transfer store to route through — and a restore that required them
/// anyway would fail on installations that never enabled the feature.
/// </remarks>
internal sealed record CovenantSelectiveImportServices(
    ICovenantOperationGate Gate,
    IProtectedArtifactTransferStore TransferStore);

internal sealed class BackupRestoreServiceOptions
{

    /// <summary>Embedding width this installation is configured for; derived vectors of any other width are rebuilt.</summary>
    public int EmbeddingDimensions { get; init; } = 1536;

    /// <summary>
    /// The protected transfer path, present only while <c>Arcanum:Features:Covenant</c> is on.
    /// </summary>
    /// <remarks>
    /// Absent means the installation never enabled Covenant, and selective import behaves exactly as
    /// it did before this slice. Present means every selective import is routed through the transfer
    /// store under one atomic compound lease, and a Campaign-bound Session without an explicit
    /// destination mapping is refused rather than silently unbound.
    /// </remarks>
    internal CovenantSelectiveImportServices? SelectiveImport { get; init; }

    /// <summary>
    /// The staged protected-state reconciliation path, present only while the gate is on.
    /// </summary>
    /// <remarks>
    /// Absent is the pre-Covenant full restore: no exclusive owner is acquired, no authenticated
    /// journal is published, and the staged snapshot is adopted as the archive carried it, less what the
    /// erasure-evidence step removes and joins, which runs with the gate off too (§10.19.9). Present
    /// means a replace-installation restore closes admission under one owner, strips the
    /// archive's managed-file authority, reissues this dataset's identities, commits its Campaign
    /// marker children before the first displacement, and reopens admission exactly once (§10.19.9).
    /// </remarks>
    internal CovenantRestoreStagingServices? RestoreStaging { get; init; }

    /// <summary>Overrides the measured destination free space so capacity refusal can be exercised.</summary>
    internal long? AvailableBytesOverrideForTests { get; init; }

    /// <summary>Invoked as each phase begins; throwing simulates a fault at that exact boundary.</summary>
    internal Action<BackupRestorePhase>? BeforePhaseForTests { get; init; }

    /// <summary>
    /// Invoked immediately before the first restore-owned filesystem mutation, after coordination
    /// evidence has been published.
    /// </summary>
    internal Action? BeforeFirstRestoreMutationForTests { get; init; }

    /// <summary>
    /// Invoked with each machine-local entry name before it is moved across the swap; throwing
    /// simulates the commit failing partway through preserving, which no phase hook can reach.
    /// </summary>
    internal Action<string>? BeforePreservedEntryMoveForTests { get; init; }

    /// <summary>
    /// Invoked before a reversal's directory renames; throwing simulates a filesystem fault during
    /// rollback, when the displaced installation is the only surviving copy.
    /// </summary>
    internal Action? BeforeReversalRenameForTests { get; init; }

    /// <summary>
    /// Invoked with each archive entry as the staged generation is composed, before that entry is
    /// laid down.
    /// </summary>
    /// <remarks>
    /// The only seam into the second full-size copy of a restored generation. What happens between two
    /// entries of that copy - a cancellation, most of all - cannot be reached from a phase hook,
    /// because the whole composition happens inside one phase.
    /// </remarks>
    internal Action<string>? BeforeStagedEntryComposeForTests { get; init; }

    /// <summary>
    /// Invoked with true once the handle that reads this installation's erasure evidence is open, and
    /// with false once it is closed again.
    /// </summary>
    /// <remarks>
    /// The seam that lets a suite prove the erasure key is never read while that handle, and the
    /// snapshot it holds, is open: a keychain read can sit behind a prompt for as long as the operator
    /// leaves it there, and none runs inside a SQLite transaction.
    /// </remarks>
    internal Action<bool>? DestinationEvidenceHandleForTests { get; init; }

    /// <summary>
    /// Invoked inside the staged erasure-evidence transaction after the purge and the evidence replacement,
    /// before any post-condition is proven.
    /// </summary>
    /// <remarks>
    /// The seam that lets a suite put back what a purge removed, and so prove that the post-conditions,
    /// and not the purge alone, are what stand between a staged generation and its commit.
    /// </remarks>
    internal Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? AfterErasurePurgeForTests { get; init; }

    /// <summary>
    /// Replaces the checked write-ahead-log checkpoint the evidence step takes on the staged database
    /// after its commit.
    /// </summary>
    /// <remarks>
    /// The seam that lets a suite fail that checkpoint outright, which no real database can be made to do
    /// on demand, and so prove a failed checkpoint is reported as a pending scrub.
    /// </remarks>
    internal Func<SqliteConnection, CancellationToken, Task<Result<CovenantWalCheckpointOutcome>>>? StagedCheckpointForTests { get; init; }

}
