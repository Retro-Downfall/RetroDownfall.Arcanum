namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Atomically claims a stranded batch and settles accounting runs that were left open by its
/// interrupted 64-line page.
/// </summary>
public interface IBatchAccountingRecoveryStore
{
    /// <summary>
    /// Publishes a private durable claim for an <see cref="BatchStatuses.InProgress"/> batch and, in
    /// the same transaction, reconciles reservations and abandons its still-running accounting runs.
    /// A previously committed claim is resumable after a process crash.
    /// </summary>
    Task<BatchAccountingRecoveryClaimStatus> ClaimRecoveryAsync(
        Guid batchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes the final recovery status only after checkpoint and artifact cleanup has succeeded.
    /// The public batch remains non-dispatchable <c>in_progress</c> until this transition commits.
    /// </summary>
    Task<bool> TryCompleteRecoveryAsync(
        Guid batchId,
        BatchAccountingRecoveryTarget target,
        CancellationToken cancellationToken = default);
}

public enum BatchAccountingRecoveryClaimStatus
{
    Claimed = 0,

    Resumable = 1,

    NotRecoverable = 2,
}

public enum BatchAccountingRecoveryTarget
{
    Requeue = 0,

    Fail = 1,
}
