using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>Code-owned allocation bounds, independent of corpus size and productive lifetime.</summary>
public static class LongRestDiscoveryLimits
{
    public const int SourcePage = 128;

    public const int ProviderInputUtf8Bytes = 32768;

    public const int SummaryUtf8Bytes = 8192;

    public const int Targets = 16;

    public const int HierarchyChildren = 16;

    public const int HierarchyLevels = 6;

    public const int LexicalTokens = 16;

    public const int LexicalTokenUtf8Bytes = 128;

    public const int LexicalTopK = 8;

    /// <summary>
    /// The share of the anchor's own lexical score (in thousandths) a lexical hit needs to be nominated at all, so a token nearly every
    /// memory shares cannot make an anchor's neighbours and the bar does not drift with the size of the corpus: it is a fraction of what
    /// the anchor scores against itself under the same weights. Lexical evidence only nominates: it is not separable from unrelated text
    /// (the weakest paraphrase pair of a labelled corpus covers about a tenth of the anchor, a pair of different pets covers almost half),
    /// so the Pairing Gate, not this bar, decides which nominees are ever put to the model. Bound into the recipe.
    /// </summary>
    public const int LexicalMinimumCoveragePermille = 200;

    public const int VectorTopK = 8;

    public const int SummaryTopK = 8;

    /// <summary>
    /// The least cosine (in thousandths) a summary needs to the anchor's nearest ancestor-excluded neighbour summaries to be selected by
    /// the hierarchy lane. Leaves, including those a summary expands to, are judged by the Pairing Gate instead. Bound into the recipe.
    /// </summary>
    public const int SummaryMinimumCosinePermille = 920;

    /// <summary>The version of the pairing arithmetic (<see cref="LongRestPairing"/>); a test pins a digest of its outputs to it. Bound into the recipe.</summary>
    public const int PairingRecipeVersion = 2;

    /// <summary>The share (in thousandths) of the distance from an anchor's median similarity to a perfect match a nominee must close to be paired.</summary>
    public const int PairingClosenessPermille = 550;

    /// <summary>The spread multiplier of the pairing floor, in tenths, for a generation of at most sixty-three leaves.</summary>
    public const int PairingSpreadTenths = 35;

    /// <summary>What the spread multiplier grows by, in tenths, for every doubling of the generation beyond sixty-four leaves.</summary>
    public const int PairingSpreadStepTenths = 2;

    /// <summary>The least spread (in thousandths) the pairing floor trusts, so a generation whose sample is degenerate cannot narrow the floor.</summary>
    public const int PairingSpreadMinimumThousandths = 20;

    /// <summary>The leaves, in memory id order, whose pairwise similarities estimate the spread of the generation.</summary>
    public const int PairingSampleLeaves = 64;

    /// <summary>The fewest other leaves a generation needs for its own distribution to set the floor.</summary>
    public const int PairingMinimumLeaves = 16;

    /// <summary>The floor (in thousandths) a generation too small to estimate its own distribution uses.</summary>
    public const int PairingFallbackPermille = 920;

    /// <summary>The lowest floor (in thousandths) any generation may use.</summary>
    public const int PairingAbsoluteMinimumPermille = 500;

    /// <summary>The bins of the fixed histogram over similarities from -1 to 1 that the median and the spread are read from.</summary>
    public const int PairingHistogramBins = 1024;

    /// <summary>The most pair units an anchor queues beside its clique packs.</summary>
    public const int PairFallbackLimit = 15;

    public const int ExpandedLeaves = 32;

    public const int TraversalFrontier = 128;

    public const int MergedNeighbors = 32;
}

/// <summary>Streaming bounded ranking; evidence selects candidates and never authorizes mutation.</summary>
public static class LongRestDiscoveryPolicy
{
    public static Result<ImmutableArray<LongRestCandidate>> MergeCandidates(LongRestTarget anchor, IEnumerable<LongRestCandidate> candidates)
    {
        if (!ValidTarget(anchor) || candidates is null)
        {
            return Invalid<ImmutableArray<LongRestCandidate>>("An anchor requires an exact bounded target and a candidate stream.");
        }

        List<LongRestCandidate>[] lanes = [[], [], [], []];

        foreach (LongRestCandidate candidate in candidates)
        {
            if (candidate is null || !ValidTarget(candidate.Target)
                || candidate.Lane is < LongRestDiscoveryLane.ExactHash or > LongRestDiscoveryLane.Hierarchy
                || !double.IsFinite(candidate.Score)
                || (candidate.Lane == LongRestDiscoveryLane.Vector && candidate.Score is < -1 or > 1))
            {
                return Invalid<ImmutableArray<LongRestCandidate>>("Discovery evidence must have a known lane, exact target and finite valid score.");
            }

            if (candidate.Target.MemoryId == anchor.MemoryId)
            {
                if (!SameTarget(candidate.Target, anchor))
                {
                    return Invalid<ImmutableArray<LongRestCandidate>>("An anchor cannot have conflicting exact versions.");
                }

                continue;
            }

            List<LongRestCandidate> lane = lanes[(int)candidate.Lane - 1];

            LongRestCandidate normalized = candidate with { Target = candidate.Target with { ExpectedContentHash = candidate.Target.ExpectedContentHash.ToUpperInvariant() } };

            int duplicate = lane.FindIndex(item => item.Target.MemoryId == normalized.Target.MemoryId);

            if (duplicate >= 0)
            {
                if (!SameTarget(lane[duplicate].Target, normalized.Target))
                {
                    return Invalid<ImmutableArray<LongRestCandidate>>("One candidate memory cannot name conflicting exact versions.");
                }

                if (Compare(normalized, lane[duplicate]) >= 0)
                {
                    continue;
                }

                lane.RemoveAt(duplicate);
            }

            lane.Add(normalized);

            lane.Sort(Compare);

            int bound = LaneBound(candidate.Lane);

            if (lane.Count > bound)
            {
                lane.RemoveAt(bound);
            }
        }

        List<LongRestCandidate> merged = new(LongRestDiscoveryLimits.MergedNeighbors);

        foreach (List<LongRestCandidate> lane in lanes)
        {
            foreach (LongRestCandidate candidate in lane)
            {
                LongRestCandidate? existing = merged.Find(item => item.Target.MemoryId == candidate.Target.MemoryId);

                if (existing is not null)
                {
                    if (!SameTarget(existing.Target, candidate.Target))
                    {
                        return Invalid<ImmutableArray<LongRestCandidate>>("Discovery lanes disagree about an exact candidate version.");
                    }

                    continue;
                }

                if (merged.Count < LongRestDiscoveryLimits.MergedNeighbors)
                {
                    merged.Add(candidate);
                }
            }
        }

        return merged.ToImmutableArray();
    }

    /// <summary>
    /// Interleaves the leaves of the selected summaries, which arrive in summary rank order, taking one leaf from each summary in turn
    /// so no summary, and no memory id, fills the lane alone. A leaf already taken from an earlier summary, or the anchor, is passed
    /// over. The result is at most <see cref="LongRestDiscoveryLimits.MergedNeighbors"/> candidates ranked by the order they were
    /// taken; each keeps the score of the summary that supplied it.
    /// </summary>
    public static ImmutableArray<LongRestCandidate> InterleaveExpansions(LongRestTarget anchor, ImmutableArray<LongRestHierarchyExpansion> expansions)
    {
        if (anchor is null || expansions.IsDefault || expansions.Any(static expansion => expansion is null || expansion.Leaves.IsDefault))
        {
            throw new ArgumentException("Interleaving requires an anchor and the selected summaries' expansions.", nameof(expansions));
        }

        List<LongRestCandidate> taken = new(LongRestDiscoveryLimits.MergedNeighbors);

        for (int round = 0; round < LongRestDiscoveryLimits.ExpandedLeaves && taken.Count < LongRestDiscoveryLimits.MergedNeighbors; round++)
        {
            foreach (LongRestHierarchyExpansion expansion in expansions)
            {
                if (round >= expansion.Leaves.Length || taken.Count == LongRestDiscoveryLimits.MergedNeighbors)
                {
                    continue;
                }

                LongRestTarget leaf = expansion.Leaves[round];

                if (leaf.MemoryId == anchor.MemoryId || taken.FindIndex(item => item.Target.MemoryId == leaf.MemoryId) >= 0)
                {
                    continue;
                }

                taken.Add(new(leaf, LongRestDiscoveryLane.Hierarchy, expansion.Score, taken.Count));
            }
        }

        return [.. taken];
    }

    /// <summary>
    /// Merges the lanes' nominations and groups them in chunks of fifteen beside the anchor. Every nominee is only a nomination: the group
    /// carries the anchor's pairing floor so that the packer, which holds the nominees' full leaves, admits only those the Pairing Gate pairs
    /// with the anchor (<see cref="LongRestPairing"/>).
    /// </summary>
    public static Result<ImmutableArray<LongRestCandidateGroup>> GroupCandidates(LongRestTarget anchor, ImmutableArray<LongRestCandidate> candidates, int pairingFloorPermille)
    {
        if (candidates.IsDefault || candidates.Length > LongRestDiscoveryLimits.MergedNeighbors
            || pairingFloorPermille is < LongRestDiscoveryLimits.PairingAbsoluteMinimumPermille or > 1000)
        {
            return Invalid<ImmutableArray<LongRestCandidateGroup>>("Candidate grouping requires one bounded merged neighbor vector and a pairing floor within its bounds.");
        }

        Result<ImmutableArray<LongRestCandidate>> merged = MergeCandidates(anchor, candidates);

        if (merged.IsFailure)
        {
            return Result<ImmutableArray<LongRestCandidateGroup>>.Failure(merged.Error);
        }

        ImmutableArray<LongRestCandidateGroup>.Builder groups = ImmutableArray.CreateBuilder<LongRestCandidateGroup>();

        for (int offset = 0; offset < merged.Value.Length; offset += LongRestDiscoveryLimits.Targets - 1)
        {
            ImmutableArray<LongRestCandidate> evidence = merged.Value.Slice(offset, Math.Min(LongRestDiscoveryLimits.Targets - 1, merged.Value.Length - offset));

            groups.Add(new([anchor, .. evidence.Select(item => item.Target)], evidence, pairingFloorPermille));
        }

        return groups.ToImmutable();
    }

    // SummaryTopK limits selected nodes; the hierarchy lane already contains expanded leaves.
    internal static int LaneBound(LongRestDiscoveryLane lane) => lane switch
    {
        LongRestDiscoveryLane.ExactHash => LongRestDiscoveryLimits.MergedNeighbors,

        LongRestDiscoveryLane.Lexical => LongRestDiscoveryLimits.LexicalTopK,

        LongRestDiscoveryLane.Vector => LongRestDiscoveryLimits.VectorTopK,

        _ => LongRestDiscoveryLimits.MergedNeighbors,
    };

    internal static int Order(LongRestCandidate left, LongRestCandidate right) => Compare(left, right);

    private static int Compare(LongRestCandidate left, LongRestCandidate right)
    {
        int score = left.Lane switch
        {
            LongRestDiscoveryLane.ExactHash => 0,

            LongRestDiscoveryLane.Lexical => left.Score.CompareTo(right.Score),

            // The hierarchy lane is ordered by the interleave that produced it; the summary score only separates unranked evidence.
            LongRestDiscoveryLane.Hierarchy when left.Rank != right.Rank => left.Rank.CompareTo(right.Rank),

            _ => right.Score.CompareTo(left.Score),
        };

        if (score != 0)
        {
            return score;
        }

        int memory = StringComparer.Ordinal.Compare(left.Target.MemoryId, right.Target.MemoryId);

        return memory != 0 ? memory : StringComparer.Ordinal.Compare(left.Target.ExpectedVersionId, right.Target.ExpectedVersionId);
    }

    private static bool SameTarget(LongRestTarget left, LongRestTarget right) =>
        left.MemoryId == right.MemoryId
        && left.ExpectedVersionId == right.ExpectedVersionId
        && StringComparer.OrdinalIgnoreCase.Equals(left.ExpectedContentHash, right.ExpectedContentHash);

    private static bool ValidTarget(LongRestTarget target) =>
        target is not null && LongRestPolicy.ValidateRequest(new(LongRestTransformationKind.ExactDuplicates, [target])).IsSuccess;

    private static Result<T> Invalid<T>(string message) => new Error(ErrorCodes.LongRest.InvalidRequest, message);
}

/// <summary>
/// One discovery lane kept as a streaming bounded ranking: the best candidates seen so far under the lane's own order, deduplicated
/// by memory. Feeding a whole corpus through it and merging the result with <see cref="LongRestDiscoveryPolicy.MergeCandidates"/>
/// selects exactly what merging every candidate one at a time did, without rebuilding the policy's lists for every leaf.
/// </summary>
public sealed class LongRestLaneBuffer(LongRestDiscoveryLane lane)
{
    private readonly List<LongRestCandidate> _items = [];

    public LongRestDiscoveryLane Lane { get; } = lane is >= LongRestDiscoveryLane.ExactHash and <= LongRestDiscoveryLane.Hierarchy
        ? lane
        : throw new ArgumentOutOfRangeException(nameof(lane));

    /// <summary>The candidates kept so far, best first.</summary>
    public ImmutableArray<LongRestCandidate> Items => [.. _items];

    public int Count => _items.Count;

    public void Add(LongRestCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Lane != Lane)
        {
            throw new ArgumentException("A lane buffer only keeps its own lane.", nameof(candidate));
        }

        int bound = LongRestDiscoveryPolicy.LaneBound(Lane);

        // A candidate that ranks below everything a full lane holds can neither enter nor replace one of its own memory.
        if (_items.Count == bound && LongRestDiscoveryPolicy.Order(candidate, _items[^1]) >= 0)
        {
            return;
        }

        int duplicate = _items.FindIndex(item => item.Target.MemoryId == candidate.Target.MemoryId);

        if (duplicate >= 0)
        {
            if (LongRestDiscoveryPolicy.Order(candidate, _items[duplicate]) >= 0)
            {
                return;
            }

            _items.RemoveAt(duplicate);
        }

        int position = _items.FindIndex(item => LongRestDiscoveryPolicy.Order(candidate, item) < 0);

        _items.Insert(position < 0 ? _items.Count : position, candidate);

        if (_items.Count > bound)
        {
            _items.RemoveAt(bound);
        }
    }
}
