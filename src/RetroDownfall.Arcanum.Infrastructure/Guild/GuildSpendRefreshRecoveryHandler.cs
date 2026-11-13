using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Reconciles the exact refresh under the generic reconciler's existing ownership.</summary>
internal sealed class GuildSpendRefreshRecoveryHandler(GuildLocalHostPolicy policy, GuildSpendRefreshService refresh)
    : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.GuildSpendRefresh;

    public int SupportedCheckpointVersion => 1;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
    {
        Result<GuildCallerContext> caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);

        return caller.IsFailure ? LongRunningOperationRecoveryResult.RequiresAttention(caller.Error.Code)
            : await refresh.RecoverClaimedAsync(caller.Value, operation, cancellationToken).ConfigureAwait(false);
    }
}
