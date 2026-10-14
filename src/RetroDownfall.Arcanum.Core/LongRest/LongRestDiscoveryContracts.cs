using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.LongRest;

public enum LongRestDiscoveryLane : byte
{
    ExactHash = 1,

    Lexical = 2,

    Vector = 3,

    Hierarchy = 4,
}

/// <summary>
/// One piece of discovery evidence. <see cref="Rank"/> is the position a lane's own traversal gave the candidate when that order is
/// part of the evidence (the hierarchy lane interleaves its selected summaries, so a candidate's rank, not the summary's score
/// and not its memory id, orders it); it is zero for lanes the score alone orders.
/// </summary>
public sealed record LongRestCandidate(LongRestTarget Target, LongRestDiscoveryLane Lane, double Score, int Rank = 0);

/// <summary>The leaves one selected summary expanded to, in the order its own traversal produced them.</summary>
public sealed record LongRestHierarchyExpansion(double Score, ImmutableArray<LongRestTarget> Leaves);

public sealed record LongRestCandidateGroup(ImmutableArray<LongRestTarget> Targets, ImmutableArray<LongRestCandidate> Evidence);

public sealed record LongRestEmbeddingBinding(string Provider, string Model, int Dimensions, CovenantDigest ValueDigest);

/// <summary>Observed authoritative Saga bytes; historical producer identity is explicitly unknown.</summary>
public enum LongRestSourceEmbeddingKind : byte
{
    ObservedSaga = 1,
}

public sealed record LongRestSourceEmbeddingBinding(LongRestSourceEmbeddingKind Kind, int Dimensions, CovenantDigest ValueDigest);

public sealed record LongRestSourceLeaf(
    LongRestSnapshot Snapshot,
    long InsertionSequence,
    long MutationEpoch,
    LongRestSourceEmbeddingBinding? EmbeddingBinding,
    ContentSensitivity Sensitivity,
    GenerationProvenance Provenance,
    ImmutableArray<CovenantDigest> LabelDigests,
    string? Content = null,
    ImmutableArray<float> Vector = default,
    ArtifactSensitivityLabel? CanonicalLabel = null,
    CovenantDigest? SagaContentDigest = null,
    ulong? SagaRevision = null)
{
    public LongRestTarget Target => new(Snapshot.MemoryId, Snapshot.VersionId, Snapshot.ContentHash);
}

public sealed record LongRestHierarchyChild(
    Guid NodeId,
    int Layer,
    long Group,
    CovenantDigest MembershipDigest,
    CovenantDigest SourceDigest,
    string Content,
    ContentSensitivity Sensitivity,
    GenerationProvenance Provenance,
    ImmutableArray<CovenantDigest> LabelDigests,
    ImmutableArray<LongRestTarget> ExpandedTargets);

public sealed record LongRestHierarchyFold(LongRestWorkIdentity Work, int Layer, long Group, CovenantDigest MembershipDigest, ImmutableArray<LongRestHierarchyChild> Children);

public sealed record LongRestHierarchyWork(LongRestHierarchyFold Fold, LongRestAcceptedHierarchySummary? AcceptedSummary);

/// <summary>Exact model/recipe facts and fixed bounds that govern reusable derived work.</summary>
public sealed record LongRestRecipeIdentity(
    int SummaryRecipeVersion,
    int ClusteringRecipeVersion,
    string SummaryProvider,
    string SummaryModel,
    CovenantDigest SummaryDestinationDigest,
    string EmbeddingProvider,
    string EmbeddingModel,
    CovenantDigest EmbeddingDestinationDigest,
    int EmbeddingDimensions,
    int SourcePageSize = LongRestDiscoveryLimits.SourcePage,
    int ProviderInputUtf8Bytes = LongRestDiscoveryLimits.ProviderInputUtf8Bytes,
    int SummaryUtf8Bytes = LongRestDiscoveryLimits.SummaryUtf8Bytes,
    int TargetLimit = LongRestDiscoveryLimits.Targets,
    int HierarchyChildLimit = LongRestDiscoveryLimits.HierarchyChildren,
    int HierarchyLevelLimit = LongRestDiscoveryLimits.HierarchyLevels,
    int LexicalTokenLimit = LongRestDiscoveryLimits.LexicalTokens,
    int LexicalTokenUtf8Bytes = LongRestDiscoveryLimits.LexicalTokenUtf8Bytes,
    int LexicalTopK = LongRestDiscoveryLimits.LexicalTopK,
    int VectorTopK = LongRestDiscoveryLimits.VectorTopK,
    int SummaryTopK = LongRestDiscoveryLimits.SummaryTopK,
    double MinimumVectorCosine = LongRestDiscoveryLimits.MinimumVectorCosine,
    int ExpandedLeafLimit = LongRestDiscoveryLimits.ExpandedLeaves,
    int TraversalFrontierLimit = LongRestDiscoveryLimits.TraversalFrontier,
    int MergedNeighborLimit = LongRestDiscoveryLimits.MergedNeighbors,
    int LexicalMinimumScore = LongRestDiscoveryLimits.LexicalMinimumScore);

public sealed record LongRestDiscoveryGeneration(
    Guid GenerationId,
    Guid OperationId,
    LongRestSweepScope Scope,
    LongRestSweepFence Fence,
    LongRestRecipeIdentity Recipe,
    LongRestSweepPhase Phase,
    long Revision,
    string? CaptureCursor,
    int FoldLayer,
    long FoldGroup,
    bool Published);

public sealed record LongRestSourcePage(
    Guid GenerationId,
    long Revision,
    ImmutableArray<LongRestSourceLeaf> Leaves,
    CovenantDigest SourcePageDigest,
    string? CommittedCaptureCursor,
    bool HasMore);

/// <summary>Accepted paid summary staging; committing this evidence does not advance the fold.</summary>
public sealed record LongRestAcceptedHierarchySummary(
    Guid GenerationId,
    Guid NodeId,
    LongRestHierarchyFold Fold,
    CovenantDigest RecipeDigest,
    CovenantDigest PayloadDigest,
    string Summary,
    CovenantDigest SummaryDigest,
    ContentSensitivity Sensitivity,
    GenerationProvenance Provenance,
    ImmutableArray<CovenantDigest> LabelDigests,
    LongRestPaidEvidence PaidEvidence);

public sealed record LongRestHierarchyPublication(
    LongRestAcceptedHierarchySummary AcceptedSummary,
    LongRestEmbeddingBinding EmbeddingBinding,
    ImmutableArray<float> Vector,
    LongRestPaidEvidence EmbeddingPaidEvidence);
