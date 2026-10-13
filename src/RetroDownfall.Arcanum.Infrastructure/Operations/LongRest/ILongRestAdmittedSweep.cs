using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

/// <summary>
/// The hosted worker's door into a Long Rest sweep: the caller has already minted the Long Rest work lease and passes
/// it by value. The sweep never stores, binds or disposes it, and no capability can arrive through this port.
/// </summary>
internal interface ILongRestAdmittedSweep
{
    Task<Result<LongRestSweepProgress>> ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct);
}
