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

    private sealed record Materialization(Candidate Candidate, TapestryRetrievedNode Node, TapestrySelectionMetadata Metadata);

    private sealed record Evaluation(string Prefix, bool Accepted);

    private static readonly Comparer<Candidate> CandidateOrder = Comparer<Candidate>.Create(static (a, b) => TapestrySelectionPolicy.Compare(a.Rank, b.Rank));

    private sealed class TraversalState(ScopeRead scope)
    {
        internal ScopeRead Scope { get; } = scope;

        internal Dictionary<string, TapestryRankedNode> Scored { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> Considered { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> Visited { get; } = new(StringComparer.Ordinal);

        internal Stack<string[]> Pending { get; } = new();

        internal bool Truncated { get; set; }
    }

    /// <summary>Scalar cursors/memos persist; hydrated payloads belong only to accepted slices.</summary>
    private sealed class SelectionState
    {
        internal SortedSet<Candidate> Candidates { get; } = new(CandidateOrder);

        internal Dictionary<string, Evaluation> Evaluations { get; } = new(StringComparer.Ordinal);

        internal List<Materialization> Accepted { get; set; } = [];

        internal Candidate? DirtyFrom { get; set; }

        internal Candidate? LastVisited { get; set; }

        internal void Add(Candidate candidate)
        {
            if (Candidates.Add(candidate) && (DirtyFrom is null || CandidateOrder.Compare(candidate, DirtyFrom) < 0))
            {
                DirtyFrom = candidate;
            }
        }
    }

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

        List<TraversalState> traversals = [];

        SelectionState selection = new();

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

                foreach (TapestryRankedNode node in ranked.Value.Nodes)
                {
                    selection.Add(new(read, node));
                }

                truncated |= ranked.Value.Truncated;
            }
            else
            {
                HashSet<string> incoming = new(read.Graph.Vertices.Values.SelectMany(static vertex => vertex.Children), StringComparer.Ordinal);

                TraversalState state = new(read);

                state.Pending.Push([.. read.Graph.Vertices.Keys.Where(id => !incoming.Contains(id)).Order(StringComparer.Ordinal)]);

                traversals.Add(state);
            }
        }

        // Every captured scope contributes its initial beam before global budget selection.
        foreach (TraversalState state in traversals)
        {
            var advanced = await AdvanceTraversalAsync(state, selection, request, maxNodes, cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure)
            {
                return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(advanced.Error);
            }
        }

        while (true)
        {
            var selected = await SelectAsync(selection, request, maxNodes, maxBytes, maxTokens, cancellationToken).ConfigureAwait(false);

            if (selected.IsFailure)
            {
                return selected;
            }

            if (selected.Value.Count == maxNodes)
            {
                break;
            }

            bool advancedAny = false;

            foreach (TraversalState state in traversals.Where(static state => state.Pending.Count != 0))
            {
                var advanced = await AdvanceTraversalAsync(state, selection, request, maxNodes, cancellationToken).ConfigureAwait(false);

                if (advanced.IsFailure)
                {
                    return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(advanced.Error);
                }

                advancedAny |= advanced.Value;
            }

            if (!advancedAny)
            {
                break;
            }
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

        if ((truncated || traversals.Any(static state => state.Truncated)) && selection.Accepted.Count < maxNodes)
        {
            logger.LogWarning("Tapestry retrieval reached its retained candidate bound {CandidateLimit}; eligible vectors were scored completely but backfill is incomplete.", CandidateLimit);

            return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(new("Tapestry.CandidateLimitReached", "Tapestry backfill exceeded its retained candidate bound."));
        }

        return Result<IReadOnlyList<TapestryRetrievedNode>>.Success([.. selection.Accepted.Select(static item => item.Node)]);
    }

    private async Task<Result<bool>> AdvanceTraversalAsync(TraversalState state, SelectionState selection, TapestryRetrievalRequest request, int width, CancellationToken cancellationToken)
    {
        bool advanced = false;

        while (state.Pending.TryPop(out string[]? frontier))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] unseen = [.. frontier.Distinct(StringComparer.Ordinal).Where(id => !state.Considered.Contains(id))];

            if (unseen.Length != 0)
            {
                var ranked = await store.RankRetrievalNodesAsync(state.Scope.Access, request.QueryVector, state.Scope.Graph, unseen, CandidateLimit, cancellationToken).ConfigureAwait(false);

                if (ranked.IsFailure)
                {
                    return Result<bool>.Failure(ranked.Error);
                }

                state.Considered.UnionWith(unseen);

                state.Truncated |= ranked.Value.Truncated;

                foreach (TapestryRankedNode node in ranked.Value.Nodes)
                {
                    state.Scored.Add(node.NodeId, node);
                }
            }

            TapestryRankedNode[] rankedFrontier = [.. frontier.Distinct(StringComparer.Ordinal).Where(id => !state.Visited.Contains(id) && state.Scored.ContainsKey(id))
                .Select(id => state.Scored[id]).Order(Comparer<TapestryRankedNode>.Create(TapestrySelectionPolicy.Compare))];

            TapestryRankedNode[] selected = [.. rankedFrontier.Take(width)];

            if (rankedFrontier.Length > width)
            {
                state.Pending.Push([.. rankedFrontier.Skip(width).Select(static node => node.NodeId)]);
            }

            foreach (TapestryRankedNode node in selected)
            {
                state.Visited.Add(node.NodeId);

                selection.Add(new(state.Scope, node));

                advanced = true;
            }

            string[] children = TapestrySelectionPolicy.Children(state.Scope.Graph, [.. selected.Select(static node => node.NodeId)]);

            if (children.Any(id => !state.Visited.Contains(id)))
            {
                state.Pending.Push(children);

                continue;
            }

            if (advanced)
            {
                break;
            }
        }

        return Result<bool>.Success(advanced);
    }

    private async Task<Result<IReadOnlyList<TapestryRetrievedNode>>> SelectAsync(SelectionState state, TapestryRetrievalRequest request,
        int maxNodes, int maxBytes, int maxTokens, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Candidate? dirty = state.DirtyFrom;

        if (dirty is null || (state.Accepted.Count == maxNodes && state.LastVisited is not null && CandidateOrder.Compare(dirty, state.LastVisited) > 0))
        {
            state.DirtyFrom = null;

            return Result<IReadOnlyList<TapestryRetrievedNode>>.Success([.. state.Accepted.Select(static item => item.Node)]);
        }

        Dictionary<string, Materialization> previous = state.Accepted.ToDictionary(static item => item.Node.NodeId, StringComparer.Ordinal);

        List<Materialization> accepted = [.. state.Accepted.Where(item => CandidateOrder.Compare(item.Candidate, dirty) < 0)];

        string prefix = TapestryHash.OfParts(accepted.Select(static item => item.Node.NodeId));

        foreach (Candidate candidate in state.Candidates.GetViewBetween(dirty, state.Candidates.Max!))
        {
            cancellationToken.ThrowIfCancellationRequested();

            state.LastVisited = candidate;

            string id = candidate.Rank.NodeId;

            if (candidate.Rank.Similarity < request.MinSimilarity
                || candidate.Scope.Graph.Vertices[id].MaterializedBytes > maxBytes)
            {
                continue;
            }

            if (state.Evaluations.TryGetValue(id, out Evaluation? evaluation) && evaluation.Prefix == prefix)
            {
                if (!evaluation.Accepted)
                {
                    continue;
                }

                if (previous.TryGetValue(id, out Materialization? unchanged))
                {
                    accepted.Add(unchanged);

                    prefix = TapestryHash.OfParts(accepted.Select(static item => item.Node.NodeId));

                    if (accepted.Count == maxNodes)
                    {
                        break;
                    }

                    continue;
                }
            }

            TapestryRetrievedNode node;

            TapestrySelectionMetadata coverage;

            if (previous.TryGetValue(id, out Materialization? cached))
            {
                node = cached.Node;

                coverage = cached.Metadata;
            }
            else
            {
                var hydrated = await store.HydrateRetrievedNodesAsync(candidate.Scope.Access,
                    [(id, candidate.Rank.Similarity)], request.Mode, cancellationToken).ConfigureAwait(false);

                if (hydrated.Count == 0)
                {
                    state.Evaluations[id] = new(prefix, false);

                    continue;
                }

                var metadata = await store.ReadSelectionMetadataAsync(candidate.Scope.Access, [id], cancellationToken).ConfigureAwait(false);

                if (metadata.IsFailure)
                {
                    return Result<IReadOnlyList<TapestryRetrievedNode>>.Failure(metadata.Error);
                }

                node = hydrated[0];

                coverage = metadata.Value[id];
            }

            state.Evaluations[id] = new(prefix, false);

            if (!TapestrySelectionPolicy.CanAccept(node, coverage, [.. accepted.Select(static item => (item.Node, item.Metadata))]))
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

            state.Evaluations[id] = new(prefix, true);

            accepted.Add(new(candidate, node, coverage));

            prefix = TapestryHash.OfParts(accepted.Select(static item => item.Node.NodeId));

            if (accepted.Count == maxNodes)
            {
                break;
            }
        }

        state.Accepted = accepted;

        state.DirtyFrom = null;

        return Result<IReadOnlyList<TapestryRetrievedNode>>.Success([.. accepted.Select(static item => item.Node)]);
    }
}
