using System.Text;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>Task10 supplies invocation-authorized scopes; this service never discovers or grants them.</summary>
internal sealed class TapestryRetriever(ITapestryStore store, IModelTokenEstimator estimator, ILogger<TapestryRetriever> logger) : ITapestryRetriever
{
    internal const int CandidateLimit = 4096;

    private sealed record ScopeRead(TapestryGenerationAccess Access, TapestryRetrievalGraph Graph);

    private sealed record Candidate(ScopeRead Scope, TapestryRankedNode Rank);

    public async Task<Result<IReadOnlyList<TapestryRetrievedNode>>> RetrieveAsync(TapestryRetrievalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        if (request.QueryVector.Length != request.Identity.EmbeddingDimension || request.QueryVector.Length == 0
            || request.QueryVector.Any(static value => !float.IsFinite(value))
            || EmbeddingBlobCodec.NormSquared(request.QueryVector) is not (> 0 and < double.PositiveInfinity)
            || !Enum.IsDefined(request.Mode) || !float.IsFinite(request.MinSimilarity))
        {
            return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(new("Tapestry.InvalidQuery", "The query vector or retrieval mode is invalid."));
        }

        int maxNodes = Math.Clamp(request.MaxNodes, 1, 50);

        int maxBytes = Math.Clamp(request.MaxBytes, 1024, 16 * 1024 * 1024);

        int maxTokens = Math.Clamp(request.MaxTokens, 128, 1048576);

        List<ScopeRead> scopes = [];

        List<Candidate> candidates = [];

        bool truncated = false;

        foreach (TapestryScope scope in request.EligibleScopes.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lookup = await store.GetRetrievableGenerationAsync(scope, request.Identity, cancellationToken).ConfigureAwait(false);

            if (lookup.IsFailure)
            {
                continue;
            }

            var graph = await store.ReadRetrievalGraphAsync(lookup.Value, cancellationToken).ConfigureAwait(false);

            if (graph.IsFailure)
            {
                return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(graph.Error);
            }

            ScopeRead read = new(lookup.Value, graph.Value);

            scopes.Add(read);

            if (request.Mode == TapestryRetrievalMode.CollapsedTree)
            {
                var ranked = await store.RankRetrievalNodesAsync(read.Access, request.QueryVector, read.Graph, null, CandidateLimit, cancellationToken).ConfigureAwait(false);

                if (ranked.IsFailure)
                {
                    return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(ranked.Error);
                }

                candidates.AddRange(ranked.Value.Nodes.Select(rank => new Candidate(read, rank)));

                truncated |= ranked.Value.Truncated;
            }
            else
            {
                var traversed = await TraverseAsync(read, request, candidates, maxNodes, cancellationToken).ConfigureAwait(false);

                if (traversed.IsFailure)
                {
                    return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(traversed.Error);
                }

                candidates.AddRange(traversed.Value.Nodes.Select(rank => new Candidate(read, rank)));

                truncated |= traversed.Value.Truncated;
            }
        }

        var selected = await SelectAsync(candidates, request, maxNodes, maxBytes, maxTokens, cancellationToken).ConfigureAwait(false);

        if (selected.IsFailure)
        {
            return selected;
        }

        // Revalidate every captured scope after materialization; never combine an old scope with its replacement.
        foreach (ScopeRead scope in scopes)
        {
            var current = await store.GetRetrievableGenerationAsync(new(scope.Access.Generation.ScopeKind, scope.Access.Generation.ScopeId), request.Identity, cancellationToken).ConfigureAwait(false);

            if (current.IsFailure || current.Value.Generation.GenerationId != scope.Access.Generation.GenerationId)
            {
                return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(new("Tapestry.SourceChanged", "The captured Tapestry generation is no longer current."));
            }
        }

        if (truncated && selected.Value.Count < maxNodes)
        {
            logger.LogWarning("Tapestry retrieval reached its retained candidate bound {CandidateLimit}; eligible vectors were scored completely but backfill is incomplete.", CandidateLimit);

            return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(new("Tapestry.CandidateLimitReached", "Tapestry backfill exceeded its retained candidate bound."));
        }

        return selected;
    }

    private async Task<Result<TapestryRankedPage>> TraverseAsync(ScopeRead scope, TapestryRetrievalRequest request, IReadOnlyList<Candidate> previousScopes, int width, CancellationToken cancellationToken)
    {
        HashSet<string> incoming = new(scope.Graph.Vertices.Values.SelectMany(static vertex => vertex.Children), StringComparer.Ordinal);

        string[] roots = [.. scope.Graph.Vertices.Keys.Where(id => !incoming.Contains(id)).Order(StringComparer.Ordinal)];

        Dictionary<string, TapestryRankedNode> scored = new(StringComparer.Ordinal);

        HashSet<string> considered = new(StringComparer.Ordinal);

        HashSet<string> visited = new(StringComparer.Ordinal);

        Stack<string[]> pending = new();

        pending.Push(roots);

        List<TapestryRankedNode> candidates = [];

        bool truncated = false;

        int scoredCount = 0;

        while (pending.TryPop(out string[]? frontier))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] unseen = [.. frontier.Distinct(StringComparer.Ordinal).Where(id => !considered.Contains(id))];

            if (unseen.Length != 0)
            {
                var ranked = await store.RankRetrievalNodesAsync(scope.Access, request.QueryVector, scope.Graph, unseen, CandidateLimit, cancellationToken).ConfigureAwait(false);

                if (ranked.IsFailure)
                {
                    return Result<TapestryRankedPage>.Failure(ranked.Error);
                }

                considered.UnionWith(unseen);

                scoredCount += ranked.Value.ScoredCount;

                truncated |= ranked.Value.Truncated;

                foreach (TapestryRankedNode node in ranked.Value.Nodes)
                {
                    scored.Add(node.NodeId, node);
                }
            }

            TapestryRankedNode[] rankedFrontier = [.. frontier.Distinct(StringComparer.Ordinal).Where(id => !visited.Contains(id) && scored.ContainsKey(id))
                .Select(id => scored[id]).Order(Comparer<TapestryRankedNode>.Create(TapestrySelectionPolicy.Compare))];

            TapestryRankedNode[] selected = [.. rankedFrontier.Take(width)];

            if (rankedFrontier.Length > width)
            {
                // Retain the lower ranked continuation, but descend the best distinct union first.
                pending.Push([.. rankedFrontier.Skip(width).Select(static node => node.NodeId)]);
            }

            foreach (TapestryRankedNode node in selected)
            {
                visited.Add(node.NodeId);

                candidates.Add(node);
            }

            string[] children = TapestrySelectionPolicy.Children(scope.Graph, [.. selected.Select(static node => node.NodeId)]);

            if (children.Any(id => !visited.Contains(id)))
            {
                pending.Push(children);

                continue;
            }

            var materialized = await SelectAsync([.. previousScopes, .. candidates.Select(rank => new Candidate(scope, rank))], request, width,
                Math.Clamp(request.MaxBytes, 1024, 16 * 1024 * 1024), Math.Clamp(request.MaxTokens, 128, 1048576), cancellationToken).ConfigureAwait(false);

            if (materialized.IsFailure)
            {
                return Result<TapestryRankedPage>.Failure(materialized.Error);
            }

            if (materialized.Value.Count == width)
            {
                break;
            }
        }

        return Result<TapestryRankedPage>.Success(new(candidates, scoredCount, truncated));
    }

    private async Task<Result<IReadOnlyList<TapestryRetrievedNode>>> SelectAsync(List<Candidate> candidates, TapestryRetrievalRequest request,
        int maxNodes, int maxBytes, int maxTokens, CancellationToken cancellationToken)
    {
        List<(TapestryRetrievedNode Node, TapestrySelectionMetadata Metadata)> accepted = [];

        foreach (Candidate candidate in candidates.Order(Comparer<Candidate>.Create(static (a, b) => TapestrySelectionPolicy.Compare(a.Rank, b.Rank))))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (candidate.Rank.Similarity < request.MinSimilarity
                || candidate.Scope.Graph.Vertices[candidate.Rank.NodeId].MaterializedBytes > maxBytes)
            {
                continue;
            }

            IReadOnlyList<TapestryRetrievedNode> hydrated = await store.HydrateRetrievedNodesAsync(candidate.Scope.Access,
                [(candidate.Rank.NodeId, candidate.Rank.Similarity)], request.Mode, cancellationToken).ConfigureAwait(false);

            if (hydrated.Count == 0)
            {
                continue;
            }

            var metadata = await store.ReadSelectionMetadataAsync(candidate.Scope.Access, [candidate.Rank.NodeId], cancellationToken).ConfigureAwait(false);

            if (metadata.IsFailure)
            {
                return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(metadata.Error);
            }

            TapestryRetrievedNode node = hydrated[0];

            TapestrySelectionMetadata coverage = metadata.Value[node.NodeId];

            if (!TapestrySelectionPolicy.CanAccept(node, coverage, accepted))
            {
                continue;
            }

            string rendered = SystemPromptBuilder.RenderTapestryContext([.. accepted.Select(static item => item.Node), node]);

            if (Encoding.UTF8.GetByteCount(rendered) > maxBytes)
            {
                continue;
            }

            TokenEstimate tokens = estimator.EstimateText(request.Provider, request.CanonicalModel, rendered);

            if (tokens.TokenCount > maxTokens)
            {
                continue;
            }

            accepted.Add((node, coverage));

            if (accepted.Count == maxNodes)
            {
                break;
            }
        }

        return Result<IReadOnlyList<TapestryRetrievedNode>>.Success([.. accepted.Select(static item => item.Node)]);
    }
}
