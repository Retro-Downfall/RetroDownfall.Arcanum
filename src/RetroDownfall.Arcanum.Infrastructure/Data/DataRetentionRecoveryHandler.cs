using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionRecoveryHandler(
    DataRetentionService service) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.DataRetentionPrune;

    public int SupportedCheckpointVersion => 3;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        await service.RecoverPruneAsync(operation, cancellationToken).ConfigureAwait(false);
}
