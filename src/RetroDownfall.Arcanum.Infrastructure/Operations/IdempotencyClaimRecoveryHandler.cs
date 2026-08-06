using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Settles an idempotency claim whose owner died mid-request (#40, requirement 3).
/// </summary>
/// <remarks>
/// A claim is only replayable as a cached response when the terminal bytes were fully captured.
/// Anything else — a claim still <see cref="IdempotencyClaimState.Running"/>, or one marked complete
/// without a valid terminal stream — is abandoned so the caller re-sends rather than receiving a
/// truncated body forever.
/// </remarks>
internal sealed class IdempotencyClaimRecoveryHandler(
    IIdempotencyClaimStore claims,
    TimeProvider timeProvider,
    ILogger<IdempotencyClaimRecoveryHandler> logger) : ILongRunningOperationRecoveryHandler
{
    private static readonly TimeSpan ReclaimLease = TimeSpan.FromMinutes(2);

    public string Kind => LongRunningOperationKinds.IdempotencyClaim;

    public int SupportedCheckpointVersion => 0;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.IdempotencyClaimId is not { } claimId)
        {
            logger.LogWarning(
                "Durable operation {OperationId} claims to own an idempotency claim but carries no claim id.",
                operation.Id);

            return LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.MissingOperationLink);
        }

        await SettleClaimAsync(claims, claimId, timeProvider, ReclaimLease, logger, cancellationToken)
            .ConfigureAwait(false);

        return LongRunningOperationRecoveryResult.Completed();
    }

    /// <summary>
    /// Shared by this handler and <see cref="InferenceRunRecoveryHandler"/>, because a crashed turn
    /// and a crashed claim have to reach the same conclusion about replayability.
    /// </summary>
    /// <remarks>
    /// The dead process still owns the lease, so recovery reclaims it before transitioning. Both
    /// steps are safe to repeat: a claim already abandoned is no longer reclaimable, so a second
    /// pass falls through without touching it.
    /// </remarks>
    internal static async Task SettleClaimAsync(
        IIdempotencyClaimStore claims,
        Guid claimId,
        TimeProvider timeProvider,
        TimeSpan reclaimLease,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        IdempotencyClaim? claim = await claims.GetByIdAsync(claimId, cancellationToken).ConfigureAwait(false);

        if (claim is null)
        {
            return;
        }

        if (claim is { State: IdempotencyClaimState.Completed, TerminalStreamComplete: true })
        {
            return;
        }

        if (claim.State is not (IdempotencyClaimState.Claimed or IdempotencyClaimState.Running))
        {
            return;
        }

        string ownerId = $"recovery-{Environment.ProcessId}-{claimId:N}";

        bool reclaimed = await claims
            .TryReclaimAsync(
                claimId,
                ownerId,
                timeProvider.GetUtcNow().Add(reclaimLease),
                cancellationToken)
            .ConfigureAwait(false);

        if (!reclaimed)
        {
            return;
        }

        await claims.MarkAbandonedAsync(claimId, ownerId, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Idempotency claim {ClaimId} did not capture terminal bytes before the crash and is now "
            + "abandoned ({Outcome}); the caller must re-send.",
            claimId,
            LongRunningOperationRecoveryOutcomes.ClaimNotReplayable);
    }
}
