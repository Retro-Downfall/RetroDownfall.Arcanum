using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.LongRest;

public interface ILongRestMaintenance
{
    Task<Result<LongRestMaintenanceCapability>> CreateCapabilityAsync(Guid operationId, ArcanumInvocationContext invocation, SessionTurnClaimLease claim, CanonicalCampaignContext scope, CovenantTurnLease lease, CancellationToken ct);

    Task<Result<LongRestSweepProgress>> ProcessAsync(Guid operationId, string workerOwner, LongRestMaintenanceCapability? capability, CancellationToken ct);
}
