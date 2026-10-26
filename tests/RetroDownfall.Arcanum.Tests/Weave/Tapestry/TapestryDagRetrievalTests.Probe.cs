using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    private sealed class DagStoreProbe(ITapestryStore inner) : ITapestryStore
    {
        internal int ScoredCount { get; private set; }

        internal int LookupCount { get; private set; }

        internal List<string[]> RankRequests { get; } = [];

        internal Func<Task>? BeforeHydrate { get; set; }

        public Task<Result<TapestryRetrievalGraph>> ReadRetrievalGraphAsync(TapestryGenerationAccess access, CancellationToken cancellationToken) => inner.ReadRetrievalGraphAsync(access, cancellationToken);

        public async Task<Result<TapestryRankedPage>> RankRetrievalNodesAsync(TapestryGenerationAccess access, float[] queryVector, TapestryRetrievalGraph graph, IReadOnlyList<string>? nodeIds, int limit, CancellationToken cancellationToken)
        {
            RankRequests.Add(nodeIds?.ToArray() ?? []);

            var result = await inner.RankRetrievalNodesAsync(access, queryVector, graph, nodeIds, limit, cancellationToken);

            if (result.IsSuccess)
            {
                ScoredCount += result.Value.ScoredCount;
            }

            return result;
        }

        public Task<Result<IReadOnlyDictionary<string, TapestrySelectionMetadata>>> ReadSelectionMetadataAsync(TapestryGenerationAccess access, IReadOnlyList<string> nodeIds, CancellationToken cancellationToken) => inner.ReadSelectionMetadataAsync(access, nodeIds, cancellationToken);

        public Task<Result<TapestryGenerationAccess>> GetRetrievableGenerationAsync(TapestryScope scope, TapestryRetrievalIdentity identity, CancellationToken cancellationToken)
        {
            LookupCount++;

            return inner.GetRetrievableGenerationAsync(scope, identity, cancellationToken);
        }

        public async Task<IReadOnlyList<TapestryRetrievedNode>> HydrateRetrievedNodesAsync(TapestryGenerationAccess access, IReadOnlyList<(string NodeId, float Similarity)> hits, TapestryRetrievalMode mode, CancellationToken cancellationToken)
        {
            if (BeforeHydrate is { } before)
            {
                BeforeHydrate = null;

                await before();
            }

            return await inner.HydrateRetrievedNodesAsync(access, hits, mode, cancellationToken);
        }

        public Task<TapestryBuildSnapshot> ReadBuildSnapshotAsync(TapestryScope scope, CancellationToken cancellationToken) => inner.ReadBuildSnapshotAsync(scope, cancellationToken);

        public Task<string> BeginGraphGenerationAsync(TapestryBuildSnapshot snapshot, TapestryGenerationRecipe recipe, DateTimeOffset startedAt, CancellationToken cancellationToken) => inner.BeginGraphGenerationAsync(snapshot, recipe, startedAt, cancellationToken);

        public Task AppendGraphCheckpointAsync(TapestryGraphCheckpoint checkpoint, CancellationToken cancellationToken) => inner.AppendGraphCheckpointAsync(checkpoint, cancellationToken);

        public Task<Result> PublishGenerationAsync(TapestryPublishRequest request, CancellationToken cancellationToken) => inner.PublishGenerationAsync(request, cancellationToken);

        public Task<IReadOnlyList<string>> GetRootIdsAsync(string generationId, CancellationToken cancellationToken) => inner.GetRootIdsAsync(generationId, cancellationToken);

        public Task<IReadOnlyList<string>> GetChildIdsAsync(string generationId, IReadOnlyList<string> parentIds, CancellationToken cancellationToken) => inner.GetChildIdsAsync(generationId, parentIds, cancellationToken);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAncestorIdsAsync(string generationId, IReadOnlyList<string> nodeIds, CancellationToken cancellationToken) => inner.GetAncestorIdsAsync(generationId, nodeIds, cancellationToken);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetDescendantLeafIdsAsync(string generationId, IReadOnlyList<string> nodeIds, CancellationToken cancellationToken) => inner.GetDescendantLeafIdsAsync(generationId, nodeIds, cancellationToken);

        public Task<IReadOnlyList<TapestryScope>> DiscoverScopesAsync(bool includeWorkspace, bool includeSessionAttachments, bool includeSessions, CancellationToken cancellationToken) => inner.DiscoverScopesAsync(includeWorkspace, includeSessionAttachments, includeSessions, cancellationToken);

        public Task<TapestryCorpusIdentity> GetCorpusIdentityAsync(TapestryScope scope, int maxLeaves, CancellationToken cancellationToken) => inner.GetCorpusIdentityAsync(scope, maxLeaves, cancellationToken);

        public IAsyncEnumerable<IReadOnlyList<TapestryLeafSource>> EnumerateLeafPagesAsync(TapestryScope scope, int expectedDimensions, CancellationToken cancellationToken) => inner.EnumerateLeafPagesAsync(scope, expectedDimensions, cancellationToken);

        public Task<TapestryGeneration?> GetCurrentGenerationAsync(TapestryScope scope, CancellationToken cancellationToken) => inner.GetCurrentGenerationAsync(scope, cancellationToken);

        public Task<string> BeginGenerationAsync(TapestryScope scope, string algorithmVersion, string settingsFingerprint, string? summaryModel, string summaryRecipeVersion, int embeddingDimension, string corpusFingerprint, DateTimeOffset startedAt, CancellationToken cancellationToken) => inner.BeginGenerationAsync(scope, algorithmVersion, settingsFingerprint, summaryModel, summaryRecipeVersion, embeddingDimension, corpusFingerprint, startedAt, cancellationToken);

        public Task AppendNodesAsync(IReadOnlyList<TapestryNodeWrite> nodes, CancellationToken cancellationToken) => inner.AppendNodesAsync(nodes, cancellationToken);

        public Task SetParentAsync(string generationId, string parentNodeId, IReadOnlyList<string> childNodeIds, CancellationToken cancellationToken) => inner.SetParentAsync(generationId, parentNodeId, childNodeIds, cancellationToken);

        public Task PublishGenerationAsync(string generationId, int layerCount, int nodeCount, int rootNodeCount, TapestryTerminalReason terminalReason, DateTimeOffset completedAt, CancellationToken cancellationToken) => inner.PublishGenerationAsync(generationId, layerCount, nodeCount, rootNodeCount, terminalReason, completedAt, cancellationToken);

        public Task AbandonGenerationAsync(string generationId, CancellationToken cancellationToken) => inner.AbandonGenerationAsync(generationId, cancellationToken);

        public Task<int> ReconcileGenerationsAsync(CancellationToken cancellationToken) => inner.ReconcileGenerationsAsync(cancellationToken);

        public Task<int> PruneRemovedScopesAsync(bool includeWorkspace, bool includeSessionAttachments, bool includeSessions, CancellationToken cancellationToken) => inner.PruneRemovedScopesAsync(includeWorkspace, includeSessionAttachments, includeSessions, cancellationToken);

        public Task<IReadOnlyList<TapestryNode>> GetLayerNodesAsync(string generationId, int layer, CancellationToken cancellationToken) => inner.GetLayerNodesAsync(generationId, layer, cancellationToken);

        public Task<IReadOnlyDictionary<string, float[]>> GetNodeEmbeddingsAsync(IReadOnlyList<string> nodeIds, CancellationToken cancellationToken) => inner.GetNodeEmbeddingsAsync(nodeIds, cancellationToken);

        public Task<TapestrySummaryReuseCandidate?> TryGetReusableSummaryAsync(TapestryScope scope, string childMembershipHash, CancellationToken cancellationToken) => inner.TryGetReusableSummaryAsync(scope, childMembershipHash, cancellationToken);

        public Task<int> GetTerminalLayerAsync(string generationId, CancellationToken cancellationToken) => inner.GetTerminalLayerAsync(generationId, cancellationToken);

        public Task<IReadOnlyList<TapestryScopeStatus>> GetScopeStatusesAsync(Guid? sessionId, CancellationToken cancellationToken) => inner.GetScopeStatusesAsync(sessionId, cancellationToken);

        public Task<int> CountPublishedNodesAsync(Guid? sessionId, CancellationToken cancellationToken) => inner.CountPublishedNodesAsync(sessionId, cancellationToken);
    }
}
