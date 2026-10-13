using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionMutationRecoveryHandler(
    DataRetentionService service) : IAuthenticatedCovenantErasureRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.DataRetentionMutation;

    public int SupportedCheckpointVersion => CovenantOfflineTransitionLaunchV4.CurrentVersion;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        await service.RecoverMutationAsync(operation, cancellationToken).ConfigureAwait(false);

    public async Task<LongRunningOperationRecoveryResult> RecoverAuthenticatedAsync(
        LongRunningOperation operation,
        CovenantErasureCoordinator.AuthenticatedCovenantErasureRecoveryAdmission admission,
        CancellationToken cancellationToken) =>
        await service.RecoverMutationAuthenticatedAsync(operation, admission, cancellationToken)
            .ConfigureAwait(false);

}
