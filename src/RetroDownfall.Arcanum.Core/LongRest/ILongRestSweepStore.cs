using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.LongRest;

public interface ILongRestSweepStore
{
    /// <summary>
    /// The next clean sweep the worker may drive: an exact-current, never-claimed request, or a parked clean sweep whose
    /// fault is resumable, whose failure budget is unspent and whose cooldown has elapsed. A candidate only, never
    /// execution authority; the result also carries when the next cooling parked sweep becomes due.
    /// </summary>
    Task<Result<LongRestRunnableSweep>> FindNextRunnableCleanAsync(LongRestConfigurationStamp configuration, Guid? afterOperationId, CancellationToken ct);

    Task<Result<LongRestSweep>> BeginOrResumeAsync(Guid operationId, LongRestSweepScope scope, LongRestConfigurationStamp configuration, CancellationToken ct);

    Task<Result<Guid?>> FindCompletedReplayAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, LongRestRecipeIdentity recipe, CancellationToken ct);

    Task<Result<Guid?>> FindCompletedReplayAsync(CanonicalCampaignContext scope, LongRestRecipeIdentity recipe, CancellationToken ct);

    Task<Result<LongRestSweepWork?>> ReadNextWorkAsync(LongRestSweep expected, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationAsync(LongRestSweep expected, LongRestAdjudication accepted, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationAsync(LongRestSweep expected, LongRestAdjudication accepted, LongRestDispatchCompletion completion, string workerOwner, LongRestMaintenanceCapability? capability, CancellationToken ct, bool committedOnly = false);

    Task<Result<LongRestSweep>> CommitExactDuplicatesAsync(LongRestSweep expected, LongRestWorkIdentity work, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestSweep>> CommitDecisionAsync(LongRestSweep expected, LongRestAdjudication decision, LongRestMaintenanceCapability? capability, CancellationToken ct);
}
