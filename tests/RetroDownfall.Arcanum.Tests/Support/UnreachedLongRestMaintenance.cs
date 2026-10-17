using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>API inference port for Infrastructure-only tests that must never perform provider work.</summary>
internal sealed class UnreachedLongRestMaintenance : ILongRestMaintenance, ILongRestAdmittedMaintenance
{
    public Task<Result<LongRestMaintenanceCapability>> CreateCapabilityAsync(Guid operationId,
        ArcanumInvocationContext invocation, SessionTurnClaimLease claim, CanonicalCampaignContext scope,
        CovenantTurnLease lease, CancellationToken ct) =>
        throw new InvalidOperationException("This Infrastructure fixture must not create Long Rest provider authority.");

    public Task<Result<LongRestSweepProgress>> ProcessAdmittedAsync(Guid operationId, string workerOwner,
        LongRestMaintenanceCapability? capability, IGrimoireWorkLease admission, CancellationToken ct) =>
        throw new InvalidOperationException("This Infrastructure fixture must not run Long Rest provider work.");
}
