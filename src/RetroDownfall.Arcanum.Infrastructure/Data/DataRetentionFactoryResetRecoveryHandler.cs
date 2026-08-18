using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionFactoryResetRecoveryHandler(
    DataRetentionService service) : ILongRunningOperationRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.DataRetentionFactoryReset;

    public int SupportedCheckpointVersion => DataRetentionFactoryResetCheckpointV1.CurrentVersion;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        service.RecoverFactoryResetAsync(operation, cancellationToken);

}
