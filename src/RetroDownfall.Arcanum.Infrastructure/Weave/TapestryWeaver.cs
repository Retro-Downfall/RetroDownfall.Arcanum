using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Weaves one Tapestry scope: embed → cluster → summarize → recurse, staged into an immutable
/// generation that only becomes visible through <see cref="ITapestryStore.PublishGenerationAsync"/>
/// (DESIGN §21.11).
///
/// <para><b>Whole-scope re-cluster, not subtree invalidation.</b> K-Means centroids and assignments
/// are relative to the complete layer: one added, removed, or edited leaf can move centroids and
/// reassign nodes that did not themselves change. So any corpus-fingerprint change re-enumerates and
/// re-clusters the entire scope. What that still avoids is unnecessary <i>work</i> — existing leaf
/// embeddings are reused, and a summary is reused whenever its exact sorted child-membership,
/// summarization recipe, model identity, and input hashes all match a prior generation's.</para>
///
/// <para>Bounds are enforced by deterministic repartitioning, never by truncating source text. An
/// oversized or token-heavy cluster is split again; when identical vectors make semantic splitting
/// impossible, a stable-id partition takes over and is recorded as such.</para>
/// </summary>
internal sealed class TapestryWeaver(
    ITapestryStore store,
    IWeaveService weave,
    ITapestrySummarizer summarizer,
    TimeProvider clock,
    ILogger<TapestryWeaver> logger)
{

    /// <summary>Nodes persisted per transaction. Bounds one write, not the build's total work.</summary>
    private const int NodeCheckpointSize = 128;

    /// <summary>
    /// Guard on recursive cluster splitting. Reaching it falls back to the deterministic stable-id
    /// partition rather than looping.
    /// </summary>
    private const int MaxSplitDepth = 8;

    public async Task<TapestryWeaveOutcome> WeaveAsync(
        TapestryScope scope,
        EmbeddingSettings embeddings,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(embeddings);

        Bounds bounds = Bounds.From(embeddings);

        IReadOnlyList<TapestryLeafSource> leaves = await store
            .EnumerateLeafSourcesAsync(scope, bounds.Dimensions, cancellationToken)
            .ConfigureAwait(false);

        if (leaves.Count == 0)
        {

            return new TapestryWeaveOutcome(TapestryWeaveStatus.NoCorpus);

        }

        string corpusFingerprint = TapestryHash.OfCorpus(leaves);

        string settingsFingerprint = TapestryHash.OfSettings(
            bounds.MaxTreeDepth,
            bounds.TargetChildrenPerSummary,
            bounds.MaxChildrenPerSummary,
            bounds.MaxClustersPerLayer,
            bounds.MaxSummaryTokens,
            bounds.Dimensions);

        string? summaryModel = summarizer.ResolveSummaryModel();

        TapestryGeneration? current = await store
            .GetCurrentGenerationAsync(scope, cancellationToken)
            .ConfigureAwait(false);

        if (IsUpToDate(current, corpusFingerprint, settingsFingerprint, summaryModel, bounds))
        {

            return new TapestryWeaveOutcome(
                TapestryWeaveStatus.UpToDate,
                current!.GenerationId,
                current.LayerCount,
                current.NodeCount,
                current.RootNodeCount,
                current.TerminalReason);

        }

        // A corpus that needs any abstraction at all needs a summary model. Publishing a leaves-only
        // tree instead would add nothing over flat retrieval while still competing for the turn's
        // context budget, so the honest degradation is to contribute nothing.
        if (summaryModel is null && leaves.Count > 1)
        {

            logger.LogDebug(
                "Tapestry weave skipped for {ScopeKind} {ScopeId}: no summary model is configured.",
                scope.Kind,
                scope.Id);

            return new TapestryWeaveOutcome(TapestryWeaveStatus.SummaryModelUnavailable);

        }

        if (!weave.IsAvailable)
        {

            return new TapestryWeaveOutcome(TapestryWeaveStatus.EmbeddingUnavailable);

        }

        string generationId = await store
            .BeginGenerationAsync(
                scope,
                SphericalKMeans.AlgorithmVersion,
                settingsFingerprint,
                summaryModel,
                TapestryHash.SummaryRecipeVersion,
                bounds.Dimensions,
                corpusFingerprint,
                clock.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {

            TapestryWeaveOutcome outcome = await BuildAsync(
                scope,
                generationId,
                leaves,
                bounds,
                cancellationToken).ConfigureAwait(false);

            if (outcome.Status != TapestryWeaveStatus.Woven)
            {

                await store.AbandonGenerationAsync(generationId, CancellationToken.None).ConfigureAwait(false);

            }

            return outcome;

        }
        catch (OperationCanceledException)
        {

            // Host shutdown or an explicit cancel: the staging generation is dropped so the last
            // complete generation stays current and the next sweep starts clean.
            await store.AbandonGenerationAsync(generationId, CancellationToken.None).ConfigureAwait(false);

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(
                ex,
                "Tapestry weave failed for {ScopeKind} {ScopeId}; the previous complete generation remains current.",
                scope.Kind,
                scope.Id);

            await store.AbandonGenerationAsync(generationId, CancellationToken.None).ConfigureAwait(false);

            return new TapestryWeaveOutcome(TapestryWeaveStatus.Failed);

        }

    }

    private static bool IsUpToDate(
        TapestryGeneration? current,
        string corpusFingerprint,
        string settingsFingerprint,
        string? summaryModel,
        Bounds bounds) =>
        current is not null
        && string.Equals(current.CorpusFingerprint, corpusFingerprint, StringComparison.Ordinal)
        && string.Equals(current.SettingsFingerprint, settingsFingerprint, StringComparison.Ordinal)
        && string.Equals(current.AlgorithmVersion, SphericalKMeans.AlgorithmVersion, StringComparison.Ordinal)
        && string.Equals(current.SummaryRecipeVersion, TapestryHash.SummaryRecipeVersion, StringComparison.Ordinal)
        && string.Equals(current.SummaryModel ?? string.Empty, summaryModel ?? string.Empty, StringComparison.Ordinal)
        && current.EmbeddingDimension == bounds.Dimensions;

    private async Task<TapestryWeaveOutcome> BuildAsync(
        TapestryScope scope,
        string generationId,
        IReadOnlyList<TapestryLeafSource> leaves,
        Bounds bounds,
        CancellationToken cancellationToken)
    {

        LeafLayer leafLayer = await BuildLeafLayerAsync(
            scope,
            generationId,
            leaves,
            bounds,
            cancellationToken).ConfigureAwait(false);

        if (leafLayer.Nodes.Count == 0)
        {

            return new TapestryWeaveOutcome(TapestryWeaveStatus.EmbeddingUnavailable);

        }

        List<WorkingNode> working = leafLayer.Nodes;

        int totalNodes = working.Count;

        int layerCount = 1;

        int summaryCalls = 0;

        int summariesReused = 0;

        int layer = 0;

        TapestryTerminalReason terminal = TapestryTerminalReason.LeafOnly;

        while (working.Count > 1 && layer < bounds.MaxTreeDepth)
        {

            cancellationToken.ThrowIfCancellationRequested();

            layer++;

            // RAPTOR's stopping rule: once the whole remaining layer fits one summary request there is
            // nothing left to partition — write the root and stop. The child-count bound is checked
            // first because a root is still a summary node subject to it, and because it short-circuits
            // building a whole-layer prompt just to estimate a fit that cannot happen.
            if (working.Count <= bounds.MaxChildrenPerSummary
                && summarizer.FitsOneRequest(
                    new TapestrySummaryRequest(scope.Kind, scope.Id, layer, [.. working.Select(static node => node.Content)])))
            {

                SummaryOutcome root = await CreateSummaryAsync(
                    scope,
                    generationId,
                    layer,
                    0,
                    working,
                    TapestryPartitionReason.None,
                    bounds,
                    cancellationToken).ConfigureAwait(false);

                if (root.Node is null)
                {

                    return new TapestryWeaveOutcome(TapestryWeaveStatus.Failed);

                }

                summaryCalls += root.CalledModel ? 1 : 0;

                summariesReused += root.CalledModel ? 0 : 1;

                working = [root.Node];

                totalNodes++;

                layerCount++;

                terminal = TapestryTerminalReason.SingleRoot;

                break;

            }

            LayerPlan layerPlan = PlanLayer(scope, working, layer, bounds);

            List<ClusterPlan> plans = layerPlan.Clusters;

            if (plans.Count + layerPlan.Carried.Count >= working.Count)
            {

                // Every cluster is a singleton even after merging: another layer would reproduce this
                // one exactly, so recursion cannot make progress. Terminate honestly with a multi-root
                // layer rather than looping to the depth cap. No layer was written, so layerCount is
                // already correct.
                terminal = TapestryTerminalReason.MaxDepth;

                break;

            }

            // Carried nodes keep their own layer and simply re-enter the next round. They still reach
            // retrieval: either a later layer claims them, or they remain roots, which is exactly where
            // tree-traversal starts.
            List<WorkingNode> next = [.. layerPlan.Carried];

            foreach (ClusterPlan plan in plans)
            {

                cancellationToken.ThrowIfCancellationRequested();

                SummaryOutcome summary = await CreateSummaryAsync(
                    scope,
                    generationId,
                    layer,
                    plan.Ordinal,
                    plan.Members,
                    plan.Reason,
                    bounds,
                    cancellationToken).ConfigureAwait(false);

                if (summary.Node is null)
                {

                    return new TapestryWeaveOutcome(TapestryWeaveStatus.Failed);

                }

                summaryCalls += summary.CalledModel ? 1 : 0;

                summariesReused += summary.CalledModel ? 0 : 1;

                next.Add(summary.Node);

                totalNodes++;

            }

            working = next;

            layerCount++;

            terminal = working.Count == 1
                ? TapestryTerminalReason.SingleRoot
                : TapestryTerminalReason.MaxDepth;

        }

        if (working.Count == 1 && layerCount > 1)
        {

            terminal = TapestryTerminalReason.SingleRoot;

        }
        else if (working.Count > 1)
        {

            terminal = TapestryTerminalReason.MaxDepth;

        }

        await store.PublishGenerationAsync(
            generationId,
            layerCount,
            totalNodes,
            working.Count,
            terminal,
            clock.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        logger.LogDebug(
            "Tapestry woven for {ScopeKind} {ScopeId}: {Layers} layer(s), {Nodes} node(s), {Roots} root(s), terminal {Terminal}; {Called} summary call(s), {Reused} reused.",
            scope.Kind,
            scope.Id,
            layerCount,
            totalNodes,
            working.Count,
            terminal,
            summaryCalls,
            summariesReused);

        return new TapestryWeaveOutcome(
            TapestryWeaveStatus.Woven,
            generationId,
            layerCount,
            totalNodes,
            working.Count,
            terminal,
            summaryCalls,
            summariesReused,
            leafLayer.Embedded,
            leafLayer.Quarantined);

    }

    private async Task<LeafLayer> BuildLeafLayerAsync(
        TapestryScope scope,
        string generationId,
        IReadOnlyList<TapestryLeafSource> leaves,
        Bounds bounds,
        CancellationToken cancellationToken)
    {

        List<TapestryLeafSource> needsEmbedding = [.. leaves.Where(static leaf => leaf.ExistingEmbedding is null)];

        Dictionary<string, float[]> minted = new(StringComparer.Ordinal);

        if (needsEmbedding.Count > 0)
        {

            Result<Embedding<float>[]> embedded = await weave
                .EmbedBatchAsync([.. needsEmbedding.Select(static leaf => leaf.Content)], cancellationToken)
                .ConfigureAwait(false);

            if (embedded.IsFailure)
            {

                logger.LogDebug(
                    "Tapestry leaf embedding failed for {ScopeKind} {ScopeId} ({Code}); the previous complete generation remains current.",
                    scope.Kind,
                    scope.Id,
                    embedded.Error.Code);

                return new LeafLayer([], 0, 0);

            }

            for (int index = 0; index < needsEmbedding.Count && index < embedded.Value.Length; index++)
            {

                minted[needsEmbedding[index].SourceId] = embedded.Value[index].Vector.ToArray();

            }

        }

        TapestryLeafSourceKind sourceKind = scope.Kind switch
        {
            TapestryScopeKind.Workspace => TapestryLeafSourceKind.WorkspaceFileChunk,
            TapestryScopeKind.SessionAttachment => TapestryLeafSourceKind.SessionAttachmentChunk,
            _ => TapestryLeafSourceKind.Entry,
        };

        List<WorkingNode> nodes = [];

        List<TapestryNodeWrite> pending = [];

        int quarantined = 0;

        DateTimeOffset now = clock.GetUtcNow();

        foreach (TapestryLeafSource leaf in leaves)
        {

            float[]? vector = leaf.ExistingEmbedding
                ?? (minted.TryGetValue(leaf.SourceId, out float[]? fresh) ? fresh : null);

            // A leaf whose vector is missing, the wrong width, or not finite is quarantined rather
            // than truncated, padded, or allowed to poison the layer's clustering.
            if (vector is null || vector.Length != bounds.Dimensions || !IsUsable(vector))
            {

                quarantined++;

                continue;

            }

            string nodeId = NodeId(generationId, leaf.SourceId);

            nodes.Add(new WorkingNode(
                leaf.SourceId,
                nodeId,
                vector,
                leaf.Content,
                leaf.ContentHash,
                1));

            pending.Add(new TapestryNodeWrite(
                new TapestryNode(
                    nodeId,
                    generationId,
                    scope.Kind,
                    scope.Id,
                    0,
                    TapestryNodeKind.Leaf,
                    null,
                    sourceKind,
                    leaf.SourceId,
                    leaf.Label,
                    null,
                    leaf.ContentHash,
                    null,
                    1,
                    0,
                    TapestryPartitionReason.None,
                    bounds.Dimensions,
                    now),
                vector));

            if (pending.Count >= NodeCheckpointSize)
            {

                await store.AppendNodesAsync(pending, cancellationToken).ConfigureAwait(false);

                pending.Clear();

            }

        }

        if (pending.Count > 0)
        {

            await store.AppendNodesAsync(pending, cancellationToken).ConfigureAwait(false);

        }

        if (quarantined > 0)
        {

            logger.LogWarning(
                "Tapestry quarantined {Count} unusable leaf vector(s) for {ScopeKind} {ScopeId}; the remaining leaves were woven normally.",
                quarantined,
                scope.Kind,
                scope.Id);

        }

        return new LeafLayer(nodes, minted.Count, quarantined);

    }

    /// <summary>
    /// Derives one layer's clusters, then repairs them deterministically: oversized or token-heavy
    /// clusters are split again, and an undersized cluster is merged into its most similar sibling
    /// that still has room. Every tie breaks on stable id, so the plan is a pure function of the
    /// layer.
    /// </summary>
    private LayerPlan PlanLayer(
        TapestryScope scope,
        List<WorkingNode> working,
        int layer,
        Bounds bounds)
    {

        Dictionary<string, WorkingNode> byKey = working.ToDictionary(
            static node => node.StableKey,
            StringComparer.Ordinal);

        SphericalKMeansResult result = SphericalKMeans.ClusterLayer(
            [.. working.Select(static node => new SphericalKMeansPoint(node.StableKey, node.Embedding))],
            bounds.TargetChildrenPerSummary,
            bounds.MaxClustersPerLayer,
            TapestrySeed.Derive(SphericalKMeans.AlgorithmVersion, scope.Kind, scope.Id, layer),
            bounds.Dimensions);

        List<PlanCandidate> candidates = [];

        foreach (SphericalKMeansCluster cluster in result.Clusters)
        {

            List<WorkingNode> members = [.. cluster.MemberIds.Select(id => byKey[id])];

            candidates.AddRange(SplitUntilBounded(scope, members, layer, bounds, TapestryPartitionReason.None, 0));

        }

        MergeUndersized(candidates, bounds);

        candidates.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Members[0].StableKey, right.Members[0].StableKey));

        List<ClusterPlan> plans = [];

        List<WorkingNode> carried = [];

        foreach (PlanCandidate candidate in candidates)
        {

            // A lone node whose own text exceeds one summary request cannot be partitioned further —
            // Arcanum does not re-chunk source material to make a model call fit. Carrying it to the
            // next layer keeps the whole scope's tree buildable instead of letting one oversized
            // excerpt block every generation forever.
            if (candidate.Members.Count == 1
                && !summarizer.FitsOneRequest(
                    new TapestrySummaryRequest(scope.Kind, scope.Id, layer, [candidate.Members[0].Content])))
            {

                logger.LogDebug(
                    "Tapestry carried an unsummarizable node up from layer {Layer} for {ScopeKind} {ScopeId}: its own text exceeds one summary request.",
                    layer,
                    scope.Kind,
                    scope.Id);

                carried.Add(candidate.Members[0]);

                continue;

            }

            plans.Add(new ClusterPlan(plans.Count, candidate.Members, candidate.Reason));

        }

        return new LayerPlan(plans, carried);

    }

    /// <summary>
    /// Splits a cluster until it satisfies both the child-count bound and the selected model's real
    /// context estimate. Source text is never truncated: when re-clustering cannot separate the
    /// members (identical vectors), a stable-id partition takes over and is recorded so the repair is
    /// visible rather than silent.
    /// </summary>
    private List<PlanCandidate> SplitUntilBounded(
        TapestryScope scope,
        List<WorkingNode> members,
        int layer,
        Bounds bounds,
        TapestryPartitionReason inheritedReason,
        int depth)
    {

        bool withinChildCount = members.Count <= bounds.MaxChildrenPerSummary;

        bool fits = summarizer.FitsOneRequest(
            new TapestrySummaryRequest(scope.Kind, scope.Id, layer, [.. members.Select(static node => node.Content)]));

        if (members.Count <= 1 || (withinChildCount && fits))
        {

            return [new PlanCandidate(members, inheritedReason)];

        }

        if (depth >= MaxSplitDepth)
        {

            return PartitionByStableId(members, bounds);

        }

        int parts = Math.Max(2, (members.Count + bounds.MaxChildrenPerSummary - 1) / bounds.MaxChildrenPerSummary);

        // The split seed is derived from the same versioned algorithm identity plus a split-specific
        // scope suffix, so a re-split is reproducible without colliding with the layer's own seed.
        SphericalKMeansResult split = SphericalKMeans.Cluster(
            [.. members.Select(static node => new SphericalKMeansPoint(node.StableKey, node.Embedding))],
            parts,
            TapestrySeed.Derive(
                SphericalKMeans.AlgorithmVersion,
                scope.Kind,
                $"{scope.Id}\u001fsplit\u001f{members[0].StableKey}\u001f{depth}",
                layer),
            bounds.Dimensions);

        if (split.Clusters.Count < 2)
        {

            return PartitionByStableId(members, bounds);

        }

        Dictionary<string, WorkingNode> byKey = members.ToDictionary(
            static node => node.StableKey,
            StringComparer.Ordinal);

        List<PlanCandidate> candidates = [];

        foreach (SphericalKMeansCluster cluster in split.Clusters)
        {

            candidates.AddRange(SplitUntilBounded(
                scope,
                [.. cluster.MemberIds.Select(id => byKey[id])],
                layer,
                bounds,
                TapestryPartitionReason.OversizedSplit,
                depth + 1));

        }

        return candidates;

    }

    /// <summary>
    /// The documented last-resort partition: contiguous stable-id chunks of at most
    /// <c>MaxChildrenPerSummary</c>. Used only when semantic splitting cannot separate the members.
    /// </summary>
    private static List<PlanCandidate> PartitionByStableId(List<WorkingNode> members, Bounds bounds)
    {

        List<WorkingNode> ordered = [.. members.OrderBy(static node => node.StableKey, StringComparer.Ordinal)];

        List<PlanCandidate> candidates = [];

        for (int index = 0; index < ordered.Count; index += bounds.MaxChildrenPerSummary)
        {

            candidates.Add(new PlanCandidate(
                [.. ordered.Skip(index).Take(bounds.MaxChildrenPerSummary)],
                TapestryPartitionReason.IdenticalVectorPartition));

        }

        return candidates;

    }

    /// <summary>
    /// One deterministic rule for undersized clusters: merge a singleton into the sibling whose
    /// members it is most similar to, provided that sibling still has room. A singleton with nowhere
    /// to go stays as its own one-child summary and is recorded as a carry — it is never dropped and
    /// never skips a layer.
    /// </summary>
    private static void MergeUndersized(List<PlanCandidate> candidates, Bounds bounds)
    {

        if (candidates.Count < 2)
        {

            return;

        }

        for (int index = candidates.Count - 1; index >= 0; index--)
        {

            if (candidates[index].Members.Count > 1)
            {

                continue;

            }

            WorkingNode orphan = candidates[index].Members[0];

            int best = -1;

            double bestSimilarity = double.NegativeInfinity;

            for (int other = 0; other < candidates.Count; other++)
            {

                if (other == index
                    || candidates[other].Members.Count + 1 > bounds.MaxChildrenPerSummary)
                {

                    continue;

                }

                double similarity = candidates[other].Members.Max(
                    member => (double)EmbeddingBlobCodec.CosineSimilarity(orphan.Embedding, member.Embedding));

                // Stable-id tie-break keeps the merge target reproducible when two siblings are
                // equally close.
                if (similarity > bestSimilarity
                    || (similarity == bestSimilarity
                        && best >= 0
                        && StringComparer.Ordinal.Compare(
                            candidates[other].Members[0].StableKey,
                            candidates[best].Members[0].StableKey) < 0))
                {

                    bestSimilarity = similarity;

                    best = other;

                }

            }

            if (best < 0)
            {

                candidates[index] = candidates[index] with { Reason = TapestryPartitionReason.SingletonCarry };

                continue;

            }

            List<WorkingNode> merged = [.. candidates[best].Members, orphan];

            merged.Sort(static (left, right) =>
                StringComparer.Ordinal.Compare(left.StableKey, right.StableKey));

            candidates[best] = new PlanCandidate(merged, TapestryPartitionReason.UndersizedMerge);

            // Safe to remove while walking backwards: the merge target keeps at least two members,
            // so a later iteration skips it, and no earlier index shifts.
            candidates.RemoveAt(index);

        }

    }

    private async Task<SummaryOutcome> CreateSummaryAsync(
        TapestryScope scope,
        string generationId,
        int layer,
        int ordinal,
        List<WorkingNode> members,
        TapestryPartitionReason reason,
        Bounds bounds,
        CancellationToken cancellationToken)
    {

        string membershipHash = TapestryHash.OfChildMembership(
            members.Select(static member => member.ContentHash),
            TapestryHash.SummaryRecipeVersion,
            summarizer.ResolveSummaryModel());

        string stableKey = $"L{layer}\u001f{membershipHash}";

        string nodeId = NodeId(generationId, stableKey);

        string content;

        string contentHash;

        float[] embedding;

        bool calledModel;

        TapestrySummaryReuseCandidate? candidate = await store
            .TryGetReusableSummaryAsync(scope, membershipHash, cancellationToken)
            .ConfigureAwait(false);

        // Reuse requires exact membership/recipe/model/input identity. Deterministic clustering does
        // not make model prose reproducible, so identity is the only safe basis for skipping a call.
        if (candidate is not null && candidate.Embedding.Length == bounds.Dimensions)
        {

            content = candidate.Content;

            contentHash = candidate.ContentHash;

            embedding = candidate.Embedding;

            calledModel = false;

        }
        else
        {

            Result<string> summary = await summarizer
                .SummarizeAsync(
                    new TapestrySummaryRequest(
                        scope.Kind,
                        scope.Id,
                        layer,
                        [.. members.Select(static member => member.Content)]),
                    cancellationToken)
                .ConfigureAwait(false);

            if (summary.IsFailure)
            {

                return new SummaryOutcome(null, false);

            }

            Result<Embedding<float>> embedded = await weave
                .EmbedAsync(summary.Value, cancellationToken)
                .ConfigureAwait(false);

            if (embedded.IsFailure)
            {

                return new SummaryOutcome(null, false);

            }

            content = summary.Value;

            contentHash = TapestryHash.OfContent(content);

            embedding = embedded.Value.Vector.ToArray();

            calledModel = true;

        }

        if (embedding.Length != bounds.Dimensions || !IsUsable(embedding))
        {

            logger.LogWarning(
                "Tapestry summary embedding for {ScopeKind} {ScopeId} layer {Layer} was unusable; abandoning this generation.",
                scope.Kind,
                scope.Id,
                layer);

            return new SummaryOutcome(null, calledModel);

        }

        int descendants = members.Sum(static member => member.DescendantLeafCount);

        await store.AppendNodesAsync(
            [
                new TapestryNodeWrite(
                    new TapestryNode(
                        nodeId,
                        generationId,
                        scope.Kind,
                        scope.Id,
                        layer,
                        TapestryNodeKind.Summary,
                        null,
                        null,
                        null,
                        SummaryLabel(scope, layer, ordinal),
                        content,
                        contentHash,
                        membershipHash,
                        descendants,
                        ordinal,
                        reason,
                        bounds.Dimensions,
                        clock.GetUtcNow()),
                    embedding),
            ],
            cancellationToken).ConfigureAwait(false);

        await store.SetParentAsync(
            generationId,
            nodeId,
            [.. members.Select(static member => member.NodeId)],
            cancellationToken).ConfigureAwait(false);

        return new SummaryOutcome(
            new WorkingNode(stableKey, nodeId, embedding, content, contentHash, descendants),
            calledModel);

    }

    private static string SummaryLabel(TapestryScope scope, int layer, int ordinal) =>
        $"{scope.Kind} summary L{layer}#{ordinal}";

    /// <summary>
    /// Node ids are per-generation (the primary key spans generations) but clustering orders by the
    /// generation-independent stable key, so the same corpus always produces the same memberships even
    /// though it produces different node ids.
    /// </summary>
    private static string NodeId(string generationId, string stableKey) =>
        TapestryHash.OfParts([generationId, stableKey]);

    private static bool IsUsable(float[] vector)
    {

        double sumOfSquares = 0;

        foreach (float component in vector)
        {

            if (!float.IsFinite(component))
            {

                return false;

            }

            sumOfSquares += (double)component * component;

        }

        return sumOfSquares > 0;

    }

    private sealed record WorkingNode(
        string StableKey,
        string NodeId,
        float[] Embedding,
        string Content,
        string ContentHash,
        int DescendantLeafCount);

    private sealed record PlanCandidate(List<WorkingNode> Members, TapestryPartitionReason Reason);

    private sealed record ClusterPlan(int Ordinal, List<WorkingNode> Members, TapestryPartitionReason Reason);

    private sealed record LayerPlan(List<ClusterPlan> Clusters, List<WorkingNode> Carried);

    private sealed record SummaryOutcome(WorkingNode? Node, bool CalledModel);

    private sealed record LeafLayer(List<WorkingNode> Nodes, int Embedded, int Quarantined);

    private sealed record Bounds(
        int Dimensions,
        int MaxTreeDepth,
        int TargetChildrenPerSummary,
        int MaxChildrenPerSummary,
        int MaxClustersPerLayer,
        int MaxSummaryTokens)
    {

        public static Bounds From(EmbeddingSettings embeddings)
        {

            TapestryEmbeddingSettings tapestry = embeddings.Tapestry ?? new TapestryEmbeddingSettings();

            int target = ArcanumSettingClamps.EmbeddingsTapestryTargetChildrenPerSummary(
                tapestry.TargetChildrenPerSummary);

            return new Bounds(
                ArcanumSettingClamps.EmbeddingsDimensions(embeddings.Dimensions),
                ArcanumSettingClamps.EmbeddingsTapestryMaxTreeDepth(tapestry.MaxTreeDepth),
                target,
                // A max below the target would make every cluster oversized on arrival, so the max is
                // raised to the target rather than allowed to contradict it.
                Math.Max(
                    target,
                    ArcanumSettingClamps.EmbeddingsTapestryMaxChildrenPerSummary(tapestry.MaxChildrenPerSummary)),
                ArcanumSettingClamps.EmbeddingsTapestryMaxClustersPerLayer(tapestry.MaxClustersPerLayer),
                ArcanumSettingClamps.EmbeddingsTapestryMaxSummaryTokens(tapestry.MaxSummaryTokens));

        }

    }

}
