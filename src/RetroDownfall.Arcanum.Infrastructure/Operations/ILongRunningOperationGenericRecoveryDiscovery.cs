using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

internal interface ILongRunningOperationGenericRecoveryDiscovery
{
    Task<IReadOnlyList<LongRunningOperation>> FindExpiredForGenericRecoveryAsync(
        DateTimeOffset utcNow,
        int limit,
        CancellationToken cancellationToken = default);
}
