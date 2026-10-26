using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>Independent global/local repair flags; both posterior values remain unmodified.</summary>
[Flags]
public enum RaptorRepairReason
{
    None = 0,
    GlobalNoMembership = 1,
    LocalNoMembership = 2,
    SmallGroup = 4,
    DuplicatePaths = 8,
}

/// <summary>Original caller index, canonical components, and separately calibrated stage probabilities.</summary>
public sealed record RaptorMembership(int PointIndex, int GlobalComponent, int LocalComponent,
    double GlobalPosterior, double LocalPosterior, RaptorRepairReason Repair);

public sealed record RaptorCluster(string StableId, ImmutableArray<RaptorMembership> Members);

public sealed record RaptorClusterResult(ImmutableArray<RaptorCluster> Clusters, ImmutableArray<int> Unassigned);

/// <summary>Pure clustering over original embeddings. Small subsets form one group; layer stopping belongs to the planner.</summary>
public interface ITapestryClusterer
{
    Result<RaptorClusterResult> Cluster(IReadOnlyList<RaptorPoint> points, TapestryClusterIdentity identity, CancellationToken cancellationToken);
}
