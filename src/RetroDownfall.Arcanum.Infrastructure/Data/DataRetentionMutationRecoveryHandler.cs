using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionMutationRecoveryHandler(
    DataRetentionService service) : ILongRunningOperationRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.DataRetentionMutation;

    public int SupportedCheckpointVersion => 2;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        service.RecoverMutationAsync(operation, cancellationToken);

}
