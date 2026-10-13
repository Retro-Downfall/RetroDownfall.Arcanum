using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.LongRest;

public interface ILongRestSweepStore
{
    /// <summary>One exact-current, never-claimed clean request; a candidate only, never execution authority.</summary>
    Task<Result<Guid?>> FindNextPendingCleanAsync(LongRestConfigurationStamp configuration, Guid? afterOperationId, CancellationToken ct);

    Task<Result<LongRestSweep>> BeginOrResumeAsync(Guid operationId, LongRestSweepScope scope, LongRestConfigurationStamp configuration, CancellationToken ct);

    Task<Result<Guid?>> FindCompletedReplayAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, LongRestRecipeIdentity recipe, CancellationToken ct);

    Task<Result<Guid?>> FindCompletedReplayAsync(CanonicalCampaignContext scope, LongRestRecipeIdentity recipe, CancellationToken ct);

    Task<Result<LongRestSweepWork?>> ReadNextWorkAsync(LongRestSweep expected, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationAsync(LongRestSweep expected, LongRestAdjudication accepted, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationAsync(LongRestSweep expected, LongRestAdjudication accepted, LongRestDispatchCompletion completion, string workerOwner, LongRestMaintenanceCapability? capability, CancellationToken ct, bool committedOnly = false);

    Task<Result<LongRestSweep>> CommitExactDuplicatesAsync(LongRestSweep expected, LongRestWorkIdentity work, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestSweep>> CommitDecisionAsync(LongRestSweep expected, LongRestAdjudication decision, LongRestMaintenanceCapability? capability, CancellationToken ct);
}
