using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.LongRest;

public interface ILongRestSweepCoordinator
{
    Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct);

    Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct);

    /// <summary>
    /// A request for a Covenant-protected partition that carries the live turn authority of the Session turn making it. Only such a
    /// request may retire a protected sweep parked with a restart-generation fault (Fresh-Authority Retire) and start a new
    /// generation in its place; every other outcome is exactly that of the authority-free overload.
    /// </summary>
    Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CovenantTurnLease authority, CancellationToken ct);

    Task<Result<LongRestSweepProgress>> ResumeAsync(Guid operationId, LongRestMaintenanceCapability? capability, CancellationToken ct);
}
