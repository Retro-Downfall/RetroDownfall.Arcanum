using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>Settles only the currently leased Guild refresh after observing the clock under its writer.</summary>
internal interface IGuildRefreshOwnedSettlement
{
    Task<LongRunningOperationState?> TrySettleGuildRefreshAsync(Guid operationId, long revision, string owner,
        LongRunningOperationState proposedState, string? errorCode, TimeProvider time, CancellationToken cancellationToken);
}
