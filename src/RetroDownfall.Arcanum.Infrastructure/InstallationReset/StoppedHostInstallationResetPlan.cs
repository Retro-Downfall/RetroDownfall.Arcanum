using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed record StoppedHostInstallationResetPlan(
    InstallationResetPlan Plan,
    DataRetentionCovenantInventory? CovenantDisclosure);

internal interface IInstallationResetStoppedHostPlanner
{

    Task<Result<StoppedHostInstallationResetPlan>> PlanUnderStoppedHostLockAsync(
        InstallationResetPlanRequest request,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken);

}
