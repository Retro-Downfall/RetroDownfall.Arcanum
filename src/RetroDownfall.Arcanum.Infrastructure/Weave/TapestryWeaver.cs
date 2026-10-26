using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Recursive RAPTOR over original provider vectors. Each admitted layer strictly contracts before
/// summary effects; overlapping memberships are persisted as DAG edges in atomic checkpoints.
/// Whole-scope source changes rebuild the graph. Reuse requires exact ordered prepared prompt,
/// algorithm, recipe, resolved provider/model and embedding identity; deterministic clustering does
/// not imply deterministic provider prose or immutable remote model weights.
/// </summary>
internal sealed class TapestryWeaver(
    ITapestryStore store,
    IWeaveService weave,
    ITapestrySummarizer summarizer,
    TapestryBuildBackoff backoff,
    TimeProvider clock,
    ILogger<TapestryWeaver> logger,
    ITapestryClusterer clusterer)
{
    private readonly ITapestryClusterer _clusterer = clusterer ?? throw new ArgumentNullException(nameof(clusterer));

    /// <summary>Nodes persisted per transaction. Bounds one write, not the build's total work.</summary>
    private const int NodeCheckpointSize = 128;

    /// <summary>
    /// ASCII unit separator (U+001F), the delimiter between the fields of a summary node's stable key.
    /// It cannot occur inside a layer number or a hex hash, so the key stays unambiguous.
    /// </summary>
    private const char StableKeyFieldSeparator = (char)0x1F;

    /// <summary>
    /// Drops the failed-build record of every scope a completed sweep did not find. The sweep calls it with
    /// the scopes it discovered, once the pass is over, because only the sweep knows which scopes exist.
    /// </summary>
    public void ForgetScopesNotIn(IReadOnlyCollection<TapestryScope> live) => backoff.RetainOnly(live);

    public async Task<TapestryWeaveOutcome> WeaveAsync(
        TapestryScope scope,
        EmbeddingSettings embeddings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        if (!embeddings.Enabled || !embeddings.TapestryEnabled)
        {
            return new TapestryWeaveOutcome(TapestryWeaveStatus.FeatureDisabled);
        }

        Bounds bounds = Bounds.From(embeddings);

        // Identity pass first, from the content hashes the store keeps beside each leaf. The fingerprint
        // decides whether this scope needs anything done at all, and it consumes only leaf ids and content
        // hashes — so on the unchanged path (every sweep of a scope nobody has touched) this reads no chunk
        // text and decodes no embedding BLOB. The corpus itself, embeddings included, is loaded below only
        // once a rebuild is known to be necessary, where one extra pass is lost in the noise of clustering
        // and model calls. A scope past the ceiling is refused here, by counting, before anything is loaded.
        TapestryCorpusIdentity identity = await store
            .GetCorpusIdentityAsync(scope, TapestryLimits.MaxLeavesPerScope, cancellationToken)
            .ConfigureAwait(false);

        if (identity.ExceedsCeiling)
        {
            LogTooLarge(scope);

            return new TapestryWeaveOutcome(TapestryWeaveStatus.TooLarge);
        }

        if (identity.LeafCount == 0)
        {
            backoff.RecordSuccess(scope);

            return new TapestryWeaveOutcome(TapestryWeaveStatus.NoCorpus);
        }

        string corpusFingerprint = identity.Fingerprint;

        TapestrySummaryPreparation? summaryIdentity = summarizer.Prepare(new(scope.Kind, scope.Id, 0, []));

        string? summaryModel = summaryIdentity?.Model;

        string summaryRecipe = summaryIdentity?.RecipeFingerprint ?? "raptor-no-summary-v1";

        string settingsFingerprint = TapestryHash.OfParts([
            RaptorAlgorithmManifest.Current.Fingerprint(),
            bounds.MaxTreeDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.MaxChildrenPerSummary.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.EmbeddingModel, summaryIdentity?.Provider ?? string.Empty, summaryModel ?? string.Empty, summaryRecipe]);

        TapestryGeneration? current = await store
            .GetCurrentGenerationAsync(scope, cancellationToken)
            .ConfigureAwait(false);

        if (IsUpToDate(current, corpusFingerprint, settingsFingerprint, summaryModel, summaryRecipe, bounds))
        {
            backoff.RecordSuccess(scope);

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
        if (summaryModel is null && identity.LeafCount > 1)
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

        // Only now, once a rebuild is certain and nothing has been spent, so a scope that is merely
        // current or merely unconfigured never consults the record. A build that already failed on this
        // very corpus, under these settings and this model, has paid for its earlier summaries and
        // discarded them; starting it again on every sweep repeats that spend for the same result.
        TimeSpan sweepInterval = TimeSpan.FromMinutes(
            ArcanumSettingClamps.EmbeddingsTapestryRebuildIntervalMinutes(
                (embeddings.Tapestry ?? new TapestryEmbeddingSettings()).RebuildIntervalMinutes));

        if (backoff.IsBackingOff(
            scope,
            BuildIdentity(corpusFingerprint, settingsFingerprint, summaryModel),
            clock.GetUtcNow()))
        {
            logger.LogDebug(
                "Tapestry weave skipped for {ScopeKind} {ScopeId}: this build failed recently and is backing off.",
                scope.Kind,
                scope.Id);

            return new TapestryWeaveOutcome(TapestryWeaveStatus.BackingOff);
        }

        // Load a revision-stable source snapshot in bounded pages. Historical leaf vector rows do not
        // prove model identity, so this build re-embeds them before any paid summary effect.
        TapestryBuildSnapshot snapshot = await store.ReadBuildSnapshotAsync(scope, cancellationToken).ConfigureAwait(false);

        List<TapestryLeafSource> leaves = [];

        await foreach (IReadOnlyList<TapestryLeafSource> page in store
            .EnumerateLeafPagesAsync(scope, bounds.Dimensions, cancellationToken)
            .ConfigureAwait(false))
        {
            leaves.AddRange(page);

            // The scope can grow between the identity pass and this one. Stop loading rather than holding
            // more than the ceiling allows.
            if (leaves.Count > TapestryLimits.MaxLeavesPerScope)
            {
                LogTooLarge(scope);

                return new TapestryWeaveOutcome(TapestryWeaveStatus.TooLarge);
            }
        }

        if (leaves.Count == 0)
        {
            return new TapestryWeaveOutcome(TapestryWeaveStatus.NoCorpus);
        }

        corpusFingerprint = TapestryHash.OfCorpus(leaves);

        if (snapshot.Corpus.ExceedsCeiling || snapshot.Corpus.Fingerprint != corpusFingerprint)
        {
            return new TapestryWeaveOutcome(TapestryWeaveStatus.CorpusChanged);
        }

        TapestryGenerationRecipe recipe = new(RaptorAlgorithmManifest.Version, settingsFingerprint, summaryModel,
            summaryRecipe, bounds.Dimensions, bounds.EmbeddingModel);

        string generationId;

        try
        {
            generationId = await store.BeginGraphGenerationAsync(snapshot,
                recipe,
                clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Begin compares the exact revision observation, including edit/revert histories.
            return new TapestryWeaveOutcome(TapestryWeaveStatus.CorpusChanged);
        }

        string buildIdentity = BuildIdentity(corpusFingerprint, settingsFingerprint, summaryModel);

        try
        {
            TapestryBuildSnapshot started = await store.ReadBuildSnapshotAsync(scope, cancellationToken).ConfigureAwait(false);

            if (started != snapshot)
            {
                await AbandonGenerationBestEffortAsync(generationId).ConfigureAwait(false);

                return new TapestryWeaveOutcome(TapestryWeaveStatus.CorpusChanged);
            }

            using IDisposable observation = TapestryBuildContext.Enter(new(snapshot,
                summaryIdentity?.Provider ?? string.Empty, summaryModel ?? string.Empty,
                embeddings.Provider?.Trim() ?? string.Empty, embeddings.Model?.Trim() ?? string.Empty, summaryIdentity));

            TapestryWeaveOutcome outcome = await BuildAsync(
                scope,
                generationId,
                leaves,
                bounds,
                snapshot,
                recipe,
                cancellationToken).ConfigureAwait(false);

            // Recorded before the cancellation check: a build that published must be forgotten, and one
            // that failed must be remembered, unless the host is stopping as it returns.
            RecordOutcome(scope, buildIdentity, outcome.Status, sweepInterval, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (outcome.Status != TapestryWeaveStatus.Woven)
            {
                await AbandonGenerationBestEffortAsync(generationId).ConfigureAwait(false);
            }

            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown or an explicit cancel: drop only Building state. Publication may already
            // have committed, in which case the new complete generation must remain current.
            await AbandonGenerationBestEffortAsync(generationId).ConfigureAwait(false);

            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Tapestry weave failed for {ScopeKind} {ScopeId}; completed generations are preserved.",
                scope.Kind,
                scope.Id);

            await AbandonGenerationBestEffortAsync(generationId).ConfigureAwait(false);

            RecordOutcome(scope, buildIdentity, TapestryWeaveStatus.Failed, sweepInterval, cancellationToken);

            return new TapestryWeaveOutcome(TapestryWeaveStatus.Failed);
        }
    }

    private void LogTooLarge(TapestryScope scope) =>
        logger.LogInformation(
            "Tapestry weave skipped for {ScopeKind} {ScopeId}: the scope holds more than {Ceiling} leaves, so no tree is built or refreshed for it.",
            scope.Kind,
            scope.Id,
            TapestryLimits.MaxLeavesPerScope);

    /// <summary>
    /// Identifies one build for the failure record: the corpus it covers, the tree-shaping settings, and
    /// the model that writes the summaries. Anything that changes the outcome of a build is in it.
    /// </summary>
    private static string BuildIdentity(string corpusFingerprint, string settingsFingerprint, string? summaryModel) =>
        TapestryHash.OfParts([corpusFingerprint, settingsFingerprint, summaryModel ?? string.Empty]);

    private void RecordOutcome(
        TapestryScope scope,
        string buildIdentity,
        TapestryWeaveStatus status,
        TimeSpan sweepInterval,
        CancellationToken cancellationToken)
    {
        switch (status)
        {
            case TapestryWeaveStatus.Woven:
                backoff.RecordSuccess(scope);

                break;

            // A failure that arrives while the sweep is being cancelled is the host stopping, not a build
            // that failed on its corpus: a provider whose call the token cut short can answer with a
            // failure instead of throwing, and remembering that would hold the next sweep off a build
            // that never had a fair attempt.
            case TapestryWeaveStatus.WorkLimit when !cancellationToken.IsCancellationRequested:
            case TapestryWeaveStatus.Failed when !cancellationToken.IsCancellationRequested:
                DateTimeOffset retryAfter = backoff.RecordFailure(scope, buildIdentity, clock.GetUtcNow(), sweepInterval);

                logger.LogInformation(
                    "Tapestry build failed for {ScopeKind} {ScopeId}; it will not be retried before {RetryAfter:O} unless the corpus, settings or summary model change.",
                    scope.Kind,
                    scope.Id,
                    retryAfter);

                break;
        }
    }

    private async Task AbandonGenerationBestEffortAsync(string generationId)
    {
        try
        {
            await store.AbandonGenerationAsync(generationId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Cleanup must not replace the primary cancellation/failure. A Building generation is
            // invisible and is retried by reconciliation; a committed Complete generation is retained.
            logger.LogWarning(ex, "Tapestry generation {GenerationId} abandonment failed; reconciliation will retry.", generationId);
        }
    }

    private static bool IsUpToDate(
        TapestryGeneration? current,
        string corpusFingerprint,
        string settingsFingerprint,
        string? summaryModel,
        string summaryRecipe,
        Bounds bounds) =>
        current is not null
        && string.Equals(current.CorpusFingerprint, corpusFingerprint, StringComparison.Ordinal)
        && string.Equals(current.SettingsFingerprint, settingsFingerprint, StringComparison.Ordinal)
        && string.Equals(current.AlgorithmVersion, RaptorAlgorithmManifest.Version, StringComparison.Ordinal)
        && string.Equals(current.SummaryRecipeVersion, summaryRecipe, StringComparison.Ordinal)
        && string.Equals(current.SummaryModel ?? string.Empty, summaryModel ?? string.Empty, StringComparison.Ordinal)
        && current.EmbeddingDimension == bounds.Dimensions;

    private async Task<TapestryWeaveOutcome> BuildAsync(
        TapestryScope scope,
        string generationId,
        IReadOnlyList<TapestryLeafSource> leaves,
        Bounds bounds,
        TapestryBuildSnapshot snapshot,
        TapestryGenerationRecipe recipe,
        CancellationToken cancellationToken)
    {
        LeafLayer leafLayer = await BuildLeafLayerAsync(scope, generationId, leaves, bounds, cancellationToken).ConfigureAwait(false);

        if (leafLayer.Nodes.Count != leaves.Count)
        {
            return new(TapestryWeaveStatus.Failed, LeavesEmbedded: leafLayer.Embedded, QuarantinedVectors: leafLayer.Quarantined);
        }

        List<WorkingNode> working = leafLayer.Nodes;

        int totalNodes = working.Count;

        int layerCount = 1;

        int summaryCalls = 0;

        int summariesReused = 0;

        TapestryTerminalReason terminal = TapestryTerminalReason.LeafOnly;

        TapestryWeaveOutcome Refused(TapestryWeaveStatus status) => new(status, LayerCount: layerCount,
            NodeCount: totalNodes, SummaryCallsMade: summaryCalls, SummariesReused: summariesReused,
            LeavesEmbedded: leafLayer.Embedded, QuarantinedVectors: leafLayer.Quarantined);

        TapestryBuildPlanner planner = new(bounds.MaxChildrenPerSummary);

        for (int layer = 1; layer <= bounds.MaxTreeDepth; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Result<TapestryLayerPlan> planned = planner.Plan(scope,
                [.. working.Select(node => new TapestryWorkingNode(node.StableKey, node.Content, node.Embedding))],
                layer, _clusterer, summarizer, cancellationToken);

            if (planned.IsFailure)
            {
                return Refused(TapestryWeaveStatus.Failed);
            }

            TapestryLayerPlan plan = planned.Value;

            if (plan.Disposition == TapestryPlanDisposition.Incomplete)
            {
                return Refused(TapestryWeaveStatus.WorkLimit);
            }

            if (plan.Disposition == TapestryPlanDisposition.Terminal)
            {
                terminal = plan.TerminalReason == TapestryPlanTerminalReason.NonContracting
                    ? TapestryTerminalReason.NonContracting
                    : TapestryTerminalReason.SmallLayer;

                break;
            }

            Dictionary<string, WorkingNode> byId = working.ToDictionary(node => node.StableKey, StringComparer.Ordinal);

            List<WorkingNode> next = [.. plan.Carries.Select(carry => byId[carry.StableId])];

            foreach (TapestryPlannedCluster cluster in plan.Summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SummaryOutcome summary = await CreateSummaryAsync(scope, generationId, layer, next.Count,
                    [.. cluster.Children.Select(child => byId[child.StableId])], TapestryPartitionReason.None,
                    bounds, cancellationToken, cluster).ConfigureAwait(false);

                summaryCalls += summary.CalledModel ? 1 : 0;

                if (summary.Node is null) return Refused(TapestryWeaveStatus.Failed);

                summariesReused += summary.CalledModel ? 0 : 1;

                next.Add(summary.Node);

                totalNodes++;
            }

            working = next;

            layerCount++;

            terminal = TapestryTerminalReason.MaxDepth;
        }

        Result published = await store.PublishGenerationAsync(new(generationId, snapshot, recipe, terminal, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);

        if (published.IsFailure) return Refused(TapestryWeaveStatus.Failed);

        int roots = (await store.GetRootIdsAsync(generationId, cancellationToken).ConfigureAwait(false)).Count;

        return new(TapestryWeaveStatus.Woven, generationId, layerCount, totalNodes, roots, terminal,
            summaryCalls, summariesReused, leafLayer.Embedded, leafLayer.Quarantined);
    }

    private async Task<LeafLayer> BuildLeafLayerAsync(
        TapestryScope scope,
        string generationId,
        IReadOnlyList<TapestryLeafSource> leaves,
        Bounds bounds,
        CancellationToken cancellationToken)
    {
        // Historical source vectors have no provider/model provenance in this contract.
        // Width or finite values cannot prove they inhabit this build's embedding space.
        List<TapestryLeafSource> needsEmbedding = [.. leaves];

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

            if (embedded.Value.Any(value => !MatchesEmbeddingIdentity(value, bounds)))
            {
                return new LeafLayer([], 0, needsEmbedding.Count);
            }

            // IWeaveService answers exactly one vector per input or fails, so a short answer can
            // never pair a leaf with its neighbour's vector here.
            for (int index = 0; index < needsEmbedding.Count; index++)
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
            float[]? vector = minted.TryGetValue(leaf.SourceId, out float[]? fresh) ? fresh : null;

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
                await store.AppendGraphCheckpointAsync(new(generationId, pending, []), cancellationToken).ConfigureAwait(false);

                pending.Clear();
            }
        }

        if (pending.Count > 0)
        {
            await store.AppendGraphCheckpointAsync(new(generationId, pending, []), cancellationToken).ConfigureAwait(false);
        }

        if (quarantined > 0)
        {
            logger.LogWarning(
                "Tapestry quarantined {Count} unusable leaf vector(s) for {ScopeKind} {ScopeId}; complete source coverage is unavailable, so the build is refused.",
                quarantined,
                scope.Kind,
                scope.Id);
        }

        return new LeafLayer(nodes, minted.Count, quarantined);
    }

    private async Task<SummaryOutcome> CreateSummaryAsync(
        TapestryScope scope,
        string generationId,
        int layer,
        int ordinal,
        List<WorkingNode> members,
        TapestryPartitionReason reason,
        Bounds bounds,
        CancellationToken cancellationToken,
        TapestryPlannedCluster? plannedCluster = null)
    {
        TapestrySummaryRequest request = new(scope.Kind, scope.Id, layer, [.. members.Select(member => member.Content)]);

        TapestrySummaryPreparation? prepared = summarizer.Prepare(request);

        TapestryBuildObservation observation = TapestryBuildContext.Current!;

        if (prepared is null || prepared.Provider != observation.SummaryProvider || prepared.Model != observation.SummaryModel
            || prepared.RecipeFingerprint != observation.SummaryPreparation?.RecipeFingerprint) return new(null, false);

        using IDisposable summaryObservation = TapestryBuildContext.Enter(observation with { SummaryPreparation = prepared });

        // Ordered actual prompt bytes, recipe and actual configuration identities authorize reuse.
        // Stable source identities separately distinguish different nodes with byte-identical prompts.
        string membershipHash = TapestryHash.OfParts([RaptorAlgorithmManifest.Current.Fingerprint(),
            prepared.InputFingerprint, prepared.RecipeFingerprint, TapestryHash.OfContent(prepared.Provider),
            TapestryHash.OfContent(prepared.Model), bounds.EmbeddingModel,
            bounds.Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        string identityHash = TapestryHash.OfParts(members.Select(member => TapestryHash.OfContent(member.StableKey)));

        string stableKey = $"L{layer}{StableKeyFieldSeparator}{membershipHash}{StableKeyFieldSeparator}{identityHash}";

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
                    request,
                    cancellationToken)
                .ConfigureAwait(false);

            if (summary.IsFailure)
            {
                return new SummaryOutcome(null, true);
            }

            Result<Embedding<float>> embedded = await weave
                .EmbedAsync(summary.Value, cancellationToken)
                .ConfigureAwait(false);

            if (embedded.IsFailure || !MatchesEmbeddingIdentity(embedded.Value, bounds))
            {
                return new SummaryOutcome(null, true);
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

        IReadOnlyDictionary<string, IReadOnlyList<string>> closures = await store.GetDescendantLeafIdsAsync(
            generationId, [.. members.Select(member => member.NodeId)], cancellationToken).ConfigureAwait(false);

        int descendants = closures.Values.SelectMany(ids => ids).Distinct(StringComparer.Ordinal).Count();

        List<TapestryGraphEdge> edges = [];

        foreach (WorkingNode member in members)
        {
            TapestryPlannedChild? child = plannedCluster?.Children.First(item => item.StableId == member.StableKey);

            TapestryMembershipPath? path = child?.Paths.OrderBy(item => item.InputMemberDigest, StringComparer.Ordinal)
                .ThenBy(item => item.ClusterId, StringComparer.Ordinal).FirstOrDefault();

            string membershipReason = $"{plannedCluster?.Reason ?? TapestryPlanReason.None}:{path?.Membership.Repair ?? RaptorRepairReason.None}";

            edges.Add(new(nodeId, member.NodeId, path?.Membership.GlobalPosterior, path?.Membership.LocalPosterior, membershipReason));
        }

        await store.AppendGraphCheckpointAsync(new(generationId,
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
            edges), cancellationToken).ConfigureAwait(false);

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

    private static bool MatchesEmbeddingIdentity(Embedding<float> embedding, Bounds bounds) =>
        embedding.AdditionalProperties?.TryGetValue(EmbeddingProvenance.MetadataKey, out object? value) == true
        && value is EmbeddingProvenance provenance
        && provenance.Identity == bounds.EmbeddingModel;

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

    private sealed record SummaryOutcome(WorkingNode? Node, bool CalledModel);

    private sealed record LeafLayer(List<WorkingNode> Nodes, int Embedded, int Quarantined);

    private sealed record Bounds(int Dimensions, int MaxTreeDepth, int MaxChildrenPerSummary, string EmbeddingModel)
    {
        public static Bounds From(EmbeddingSettings embeddings)
        {
            TapestryEmbeddingSettings tapestry = embeddings.Tapestry ?? new TapestryEmbeddingSettings();

            return new Bounds(
                ArcanumSettingClamps.EmbeddingsDimensions(embeddings.Dimensions),
                ArcanumSettingClamps.EmbeddingsTapestryMaxTreeDepth(tapestry.MaxTreeDepth),
                ArcanumSettingClamps.EmbeddingsTapestryMaxChildrenPerSummary(tapestry.MaxChildrenPerSummary),
                TapestryHash.OfEmbeddingModel(embeddings.Provider, embeddings.Model));
        }
    }
}
