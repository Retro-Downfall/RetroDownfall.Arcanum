using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionFactoryResetRecoveryHandler(
    DataRetentionService service) : IAuthenticatedCovenantErasureRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.DataRetentionFactoryReset;

    public int SupportedCheckpointVersion => DataRetentionFactoryTransitionLaunchV2.CurrentVersion;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        service.RecoverFactoryResetAsync(operation, cancellationToken);

    public Task<LongRunningOperationRecoveryResult> RecoverAuthenticatedAsync(
        LongRunningOperation operation,
        CovenantErasureCoordinator.AuthenticatedCovenantErasureRecoveryAdmission admission,
        CancellationToken cancellationToken) =>
        service.RecoverFactoryResetAuthenticatedAsync(operation, admission, cancellationToken);

}
