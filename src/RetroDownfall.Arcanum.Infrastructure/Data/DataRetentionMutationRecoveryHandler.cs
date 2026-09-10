using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionMutationRecoveryHandler(
    DataRetentionService service) : ILongRunningOperationRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.DataRetentionMutation;

    public int SupportedCheckpointVersion => CovenantOfflineTransitionLaunchV4.CurrentVersion;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        service.RecoverMutationAsync(operation, cancellationToken);

}
