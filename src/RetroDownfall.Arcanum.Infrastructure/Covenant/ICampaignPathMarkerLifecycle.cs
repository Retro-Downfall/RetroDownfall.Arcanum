using System.Collections.Immutable;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The single owner of every Campaign root marker effect.
/// </summary>
/// <remarks>
/// Partial so the later reset slice can add its shutdown-only kind-four methods without reopening
/// this one. The rule the partiality protects is that no other component ever constructs, parses,
/// compares, deletes, exports, or reimports a marker or an intent row: two places that agree on the
/// marker protocol today diverge as soon as either gains a phase, and the first divergence is a
/// cleanup that declines to recognise the marker its own registration wrote (§10.12).
/// </remarks>
internal partial interface ICampaignPathMarkerLifecycle
{

    /// <summary>
    /// Commits one restore-cleanup child per Campaign whose marker this restore must remove, inside
    /// the caller's staged core transaction.
    /// </summary>
    /// <remarks>
    /// Staged rather than live on purpose. Replacement erases the live database, so a cleanup intent
    /// written there would be destroyed by the very swap it exists to survive.
    ///
    /// <para>Borrows <paramref name="stagedConnection"/> and <paramref name="stagedTransaction"/>
    /// without beginning, committing, rolling back, or disposing either. The caller commits, because
    /// only the caller knows what else belongs in the same atomic staged state.</para>
    /// </remarks>
    Task<Result<CampaignPathRestoreCleanupPreparationReceipt>> PrepareRestoreCleanupInStagedDatabaseAsync(
        CampaignPathRestoreCleanupPreparation preparation,
        SqliteConnection stagedConnection,
        SqliteTransaction stagedTransaction,
        CancellationToken cancellationToken);

    /// <summary>
    /// Drives every named child to its pending disposition under a lease the caller already holds.
    /// </summary>
    /// <remarks>
    /// Returns the disposition and finalizer rather than applying them. The caller owns the lease and
    /// is the only thing entitled to spend its one reopening decision, and the finalizer must not run
    /// until that decision has actually succeeded.
    /// </remarks>
    Task<Result<CampaignPathMarkerGateCompletion>> ReconcileGateOwnedAsync(
        CampaignPathMarkerGateReconcileRequest request,
        ICovenantExclusiveOperationLease exclusiveLease,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retains one opened root for a child this process prepared, so reconciliation never has to
    /// resolve a display path a second time.
    /// </summary>
    /// <remarks>
    /// Present on the contract because the retention is part of the protocol, not an implementation
    /// detail: the one path resolution happens while the root is opened, and every later phase names
    /// a bounded leaf at most. A reconciliation that had to reopen by path could be redirected by a
    /// display path that became a symlink in between.
    /// </remarks>
    ValueTask ReleaseRetainedRootsAsync(Guid ownerOperationId);

}

/// <summary>
/// The Campaign identities a restore observed as still owning a marker on the destination.
/// </summary>
internal sealed record CampaignPathRestoreCleanupInventory(
    ImmutableArray<CampaignPathRestoreCleanupSeed> OrderedSeeds);
