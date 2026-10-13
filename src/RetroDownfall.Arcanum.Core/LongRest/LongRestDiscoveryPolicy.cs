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

            int bound = candidate.Lane switch
            {
                LongRestDiscoveryLane.ExactHash => LongRestDiscoveryLimits.MergedNeighbors,

                LongRestDiscoveryLane.Lexical => LongRestDiscoveryLimits.LexicalTopK,

                LongRestDiscoveryLane.Vector => LongRestDiscoveryLimits.VectorTopK,

                // SummaryTopK limits selected nodes; this lane already contains expanded leaves.
                _ => LongRestDiscoveryLimits.MergedNeighbors,
            };

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

    private static int Compare(LongRestCandidate left, LongRestCandidate right)
    {
        int score = left.Lane switch
        {
            LongRestDiscoveryLane.ExactHash => 0,

            LongRestDiscoveryLane.Lexical => left.Score.CompareTo(right.Score),

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
