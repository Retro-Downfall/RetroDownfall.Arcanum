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
    /// The least <see cref="LongRestLexicalScorer"/> score (sixteenths of a bit) a lexical hit needs to be evidence at all, so a
    /// token nearly every memory shares cannot make an anchor's neighbours. Bound into the recipe. This initial value is the largest
    /// round number the retained small-corpus expectations allow (a term in 3 of 4 documents, twice, still counts); it is calibrated
    /// against a real corpus during the live qualification.
    /// </summary>
    public const int LexicalMinimumScore = 12;

    public const int VectorTopK = 8;

    public const int SummaryTopK = 8;

    public const double MinimumVectorCosine = 0.92;

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

            if (candidate.Lane == LongRestDiscoveryLane.Vector && candidate.Score < LongRestDiscoveryLimits.MinimumVectorCosine)
            {
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

                if (leaf.MemoryId == anchor.MemoryId || taken.Exists(item => item.Target.MemoryId == leaf.MemoryId))
                {
                    continue;
                }

                taken.Add(new(leaf, LongRestDiscoveryLane.Hierarchy, expansion.Score, taken.Count));
            }
        }

        return [.. taken];
    }

    public static Result<ImmutableArray<LongRestCandidateGroup>> GroupCandidates(LongRestTarget anchor, ImmutableArray<LongRestCandidate> candidates)
    {
        if (candidates.IsDefault || candidates.Length > LongRestDiscoveryLimits.MergedNeighbors)
        {
            return Invalid<ImmutableArray<LongRestCandidateGroup>>("Candidate grouping requires one bounded merged neighbor vector.");
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

            groups.Add(new([anchor, .. evidence.Select(item => item.Target)], evidence));
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
