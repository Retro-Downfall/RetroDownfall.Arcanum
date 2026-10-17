using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

/// <summary>
/// The coordinator's door into one bounded unit of committed Long Rest work. It carries the work lease across the
/// Core/Api seam by value, so the lease is never exposed through the public Core maintenance contract.
/// </summary>
internal interface ILongRestAdmittedMaintenance
{
    Task<Result<LongRestSweepProgress>> ProcessAdmittedAsync(Guid operationId, string workerOwner,
        LongRestMaintenanceCapability? capability, IGrimoireWorkLease admission, CancellationToken ct);
}
