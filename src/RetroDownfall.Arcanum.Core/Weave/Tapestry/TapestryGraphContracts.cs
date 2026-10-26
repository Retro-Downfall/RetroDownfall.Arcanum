using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>One current frontier node; Content and Embedding remain original summary-input data.</summary>
public sealed record TapestryWorkingNode(string StableId, string Content, ReadOnlyMemory<float> Embedding);

/// <summary>Incomplete is a refusal: retain the old generation, back off and never publish the diagnostic carries.</summary>
public enum TapestryPlanDisposition
{
    Ready,

    Terminal,

    Incomplete,
}

public enum TapestryPlanTerminalReason
{
    SmallLayer,

    NonContracting,

    WorkLimit,
}

[Flags]
public enum TapestryPlanReason
{
    None = 0,

    NonContractingPartition = 1,

    DegeneratePartition = 2,

    SingletonCarry = 4,

    OversizedCarry = 8,

    DuplicatePaths = 16,
}

/// <summary>PointIndex is relative to the InputIds in the matching per-split evidence, not the outer layer.</summary>
public sealed record TapestryMembershipPath(string InputMemberDigest, string ClusterId, RaptorMembership Membership);

public sealed record TapestryPlannedChild(string StableId, ImmutableArray<TapestryMembershipPath> Paths);

public sealed record TapestryPlannedCluster(string StableId, ImmutableArray<TapestryPlannedChild> Children, TapestryPlanReason Reason);

public sealed record TapestryPlannedCarry(string StableId, TapestryPlanReason Reason, ImmutableArray<TapestryMembershipPath> Paths);

/// <summary>Flat immutable evidence preserves every prior split without recursively copying shared ancestry.</summary>
public sealed record TapestrySplitEvidence(string InputMemberDigest, ImmutableArray<string> InputIds, RaptorClusterResult Result);

public sealed record TapestryLayerPlan(ImmutableArray<TapestryPlannedCluster> Summaries, ImmutableArray<TapestryPlannedCarry> Carries, TapestryPlanTerminalReason? TerminalReason, TapestryPlanDisposition Disposition = TapestryPlanDisposition.Ready, ImmutableArray<TapestrySplitEvidence> Evidence = default);
