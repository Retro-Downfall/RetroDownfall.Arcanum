using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionRecoveryHandler(
    DataRetentionService service) : ILongRunningOperationRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.DataRetentionPrune;

    public int SupportedCheckpointVersion => 2;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        service.RecoverPruneAsync(operation, cancellationToken);

}
