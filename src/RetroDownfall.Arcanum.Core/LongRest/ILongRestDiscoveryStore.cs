using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.LongRest;

public interface ILongRestDiscoveryStore
{
    Task<Result<CovenantDigest?>> ReadNextProtectedPartitionAsync(CanonicalCampaignContext scope, CovenantDigest? after, CancellationToken ct);

    Task<Result<LongRestDiscoveryGeneration?>> ReadGenerationMetadataAsync(Guid operationId, CancellationToken ct);

    Task<Result<LongRestDiscoveryGeneration>> StartOrResumeAsync(Guid operationId, LongRestSweepScope scope, LongRestSweepFence fence, LongRestRecipeIdentity recipe, CancellationToken ct);

    Task<Result<LongRestSourcePage>> CaptureNextSourcePageAsync(Guid generationId, long expectedRevision, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestHierarchyWork?>> ReadNextFoldAsync(Guid generationId, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestAcceptedHierarchySummary>> CommitAcceptedHierarchySummaryAsync(LongRestAcceptedHierarchySummary summary, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestAcceptedHierarchySummary>> CommitAcceptedHierarchySummaryAsync(LongRestAcceptedHierarchySummary summary, LongRestDispatchCompletion completion, string workerOwner, LongRestMaintenanceCapability? capability, CancellationToken ct, bool committedOnly = false);

    Task<Result<LongRestDiscoveryGeneration>> CommitFoldAsync(LongRestHierarchyPublication publication, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestDiscoveryGeneration>> CommitFoldAsync(LongRestHierarchyPublication publication, LongRestDispatchCompletion completion, string workerOwner, LongRestMaintenanceCapability? capability, CancellationToken ct, bool committedOnly = false);

    Task<Result<LongRestDiscoveryGeneration>> PublishAsync(Guid generationId, long expectedRevision, LongRestSweepFence fence, LongRestMaintenanceCapability? capability, CancellationToken ct);

    Task<Result<LongRestCandidateGroup[]>> DiscoverAsync(Guid generationId, LongRestTarget anchor, LongRestMaintenanceCapability? capability, CancellationToken ct);
}
