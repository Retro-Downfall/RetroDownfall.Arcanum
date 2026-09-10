using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

internal interface ILongRunningOperationClassifiedRecoveryLeaseAcquisition
{
    Task<LongRunningOperationLeaseResult> TryAcquireClassifiedRecoveryLeaseAsync(
        LongRunningOperationRecoveryFingerprint expected,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);
}
