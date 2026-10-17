using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionFactoryResetRecoveryHandler(
    DataRetentionService service) : IAuthenticatedCovenantErasureRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.DataRetentionFactoryReset;

    public int SupportedCheckpointVersion => DataRetentionFactoryTransitionLaunchV2.CurrentVersion;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        await service.RecoverFactoryResetAsync(operation, cancellationToken).ConfigureAwait(false);

    public async Task<LongRunningOperationRecoveryResult> RecoverAuthenticatedAsync(
        LongRunningOperation operation,
        CovenantErasureCoordinator.AuthenticatedCovenantErasureRecoveryAdmission admission,
        CancellationToken cancellationToken) =>
        await service.RecoverFactoryResetAuthenticatedAsync(operation, admission, cancellationToken)
            .ConfigureAwait(false);
}
