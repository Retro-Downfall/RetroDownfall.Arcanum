namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>Exact cosine, trustworthy source/event recency, then ordinal stable identity.</summary>
public static class TapestrySelectionPolicy
{
    public static int Compare(TapestryRankedNode left, TapestryRankedNode right)
    {
        int score = right.Similarity.CompareTo(left.Similarity);

        if (score != 0)
        {
            return score;
        }

        int recency = Nullable.Compare(right.SourceEventAt, left.SourceEventAt);

        return recency != 0 ? recency : StringComparer.Ordinal.Compare(left.NodeId, right.NodeId);
    }

    public static bool CanAccept(TapestryRetrievedNode candidate, TapestrySelectionMetadata metadata,
        IReadOnlyList<(TapestryRetrievedNode Node, TapestrySelectionMetadata Metadata)> accepted)
    {
        if (accepted.Any(item => item.Node.Content == candidate.Content))
        {
            return false;
        }

        var sameGeneration = accepted.Where(item => item.Node.ScopeKind == candidate.ScopeKind
            && item.Node.ScopeId == candidate.ScopeId && item.Node.GenerationId == candidate.GenerationId).ToArray();

        if (sameGeneration.Any(item => item.Node.NodeId == candidate.NodeId
            || metadata.Ancestors.Contains(item.Node.NodeId, StringComparer.Ordinal)
            || item.Metadata.Ancestors.Contains(candidate.NodeId, StringComparer.Ordinal)))
        {
            return false;
        }

        HashSet<string> covered = new(sameGeneration.SelectMany(static item => item.Metadata.CoveredSourceIds), StringComparer.Ordinal);

        return metadata.CoveredSourceIds.Count != 0 && !metadata.CoveredSourceIds.All(covered.Contains);
    }

    /// <summary>Routes through the distinct child union, independently of materialization budgets.</summary>
    public static string[] Children(TapestryRetrievalGraph graph, IReadOnlyList<string> parentIds) =>
        [.. parentIds.SelectMany(id => graph.Vertices[id].Children).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
