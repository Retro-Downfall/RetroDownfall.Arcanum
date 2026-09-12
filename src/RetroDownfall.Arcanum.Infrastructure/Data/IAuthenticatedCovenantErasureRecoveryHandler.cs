using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// An owner-bound recovery handler whose coordinator durably settles the row it was given.
/// </summary>
internal interface IAuthenticatedCovenantErasureRecoveryHandler :
    ILongRunningOperationRecoveryHandler
{

    Task<LongRunningOperationRecoveryResult> RecoverAuthenticatedAsync(
        LongRunningOperation operation,
        CovenantErasureCoordinator.AuthenticatedCovenantErasureRecoveryAdmission admission,
        CancellationToken cancellationToken);

}
