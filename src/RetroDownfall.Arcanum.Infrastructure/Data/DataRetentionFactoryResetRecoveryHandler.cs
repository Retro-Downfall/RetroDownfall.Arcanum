using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionFactoryResetRecoveryHandler(
    DataRetentionService service) : ILongRunningOperationRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.DataRetentionFactoryReset;

    public int SupportedCheckpointVersion => 0;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        service.RecoverFactoryResetAsync(operation, cancellationToken);

}
