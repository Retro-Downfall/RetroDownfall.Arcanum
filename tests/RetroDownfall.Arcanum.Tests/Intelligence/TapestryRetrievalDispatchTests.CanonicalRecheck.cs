using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class TapestryRetrievalDispatchTests
{
    [Theory]
    [InlineData(false, "canonical", false)]
    [InlineData(true, "canonical", false)]
    [InlineData(false, "physical-root", false)]
    [InlineData(true, "physical-root", false)]
    [InlineData(false, "canonical", true)]
    [InlineData(true, "canonical", true)]
    [InlineData(false, "physical-root", true)]
    [InlineData(true, "physical-root", true)]
    public async Task CanonicalChangeDuringPhysicalGenerationReadRefusesBeforePhysicalSend(bool streaming, string change, bool mutate)
    {
        await using Harness h = await Harness.CreateAsync(holdGeneration: true);

        Task<bool> send = h.SendAsync(streaming, h.Observation);

        try
        {
            await h.Held!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Lookup one was direct admission; lookup two is the final native BeforeTransport fence.
            Assert.Equal(2, h.Held.Reads);

            Assert.True(h.Held.Captured!.IsSuccess);

            Assert.Equal(h.Observation.Generations.Single().GenerationId, h.Held.Captured.Value.Generation.GenerationId);

            Assert.Equal(0, h.Http.Sends);

            if (mutate) await h.ChangeAsync(change);
        }
        finally
        {
            h.Held!.Release.TrySetResult();
        }

        Assert.Equal(!mutate, await send.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(mutate ? 0 : 1, h.Http.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoObservationDoesNotEnterHeldGenerationReadOrResolveAgain(bool streaming)
    {
        await using Harness h = await Harness.CreateAsync(holdGeneration: true);

        int canonical = h.CanonicalCalls;

        Assert.True(await h.SendAsync(streaming, null));

        Assert.Equal(0, h.Held!.Reads);

        Assert.False(h.Held.Entered.Task.IsCompleted);

        Assert.Equal(canonical, h.CanonicalCalls);

        Assert.Equal(1, h.Http.Sends);
    }

    [Theory]
    [InlineData(false, "embedding")]
    [InlineData(true, "embedding")]
    [InlineData(false, "epoch")]
    [InlineData(true, "epoch")]
    public async Task FinalCanonicalAwaitIsFollowedByFreshEpochAndEmbeddingIdentity(bool streaming, string change)
    {
        await using Harness h = await Harness.CreateAsync();

        h.HoldFinalCanonicalResolution();

        Task<bool> send = h.SendAsync(streaming, h.Observation);

        try
        {
            await h.FinalCanonicalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(5, h.CanonicalCalls);

            Assert.Equal(0, h.Http.Sends);

            await h.ChangeAsync(change);
        }
        finally
        {
            h.FinalCanonicalRelease.TrySetResult();
        }

        Assert.False(await send.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(0, h.Http.Sends);
    }

    [Theory]
    [InlineData(false, "configured-dimension")]
    [InlineData(true, "configured-dimension")]
    [InlineData(false, "configured-summary-model")]
    [InlineData(true, "configured-summary-model")]
    public async Task ValidRaptorGenerationKeepsActualQueryProofAcrossConfiguredWidthOrSummaryModelChanges(bool streaming, string change)
    {
        await using Harness h = await Harness.CreateAsync();

        Assert.Equal(3, h.Observation.Identity.EmbeddingDimension);

        Assert.NotEqual(3, h.Settings.CurrentValue.Integrations.Embeddings.Dimensions);

        await h.ChangeAsync(change);

        Assert.True(await h.SendAsync(streaming, h.Observation));

        Assert.Equal(1, h.Http.Sends);
    }

    private sealed class ChangingPhysicalPath : ICampaignPathIdentityReader
    {
        internal static readonly Guid Campaign = Guid.Parse("109CBE8F-3B7B-4D30-A747-D607219D7D36");

        internal bool Changed { get; set; }

        public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(string? path, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(Changed
                ? new(Campaign, 1, 1, 1, new CovenantDigest(Enumerable.Repeat((byte)0x51, 32).ToArray())) : null));

        public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(Guid campaignId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(null));
    }

    private sealed class HeldGenerationStore(ITapestryStore inner) : ITapestryStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Reads { get; private set; }

        internal Result<TapestryGenerationAccess>? Captured { get; private set; }

        public Task<Result<TapestryRetrievalGraph>> ReadRetrievalGraphAsync(TapestryGenerationAccess access, CancellationToken cancellationToken) => inner.ReadRetrievalGraphAsync(access, cancellationToken);

        public Task<Result<TapestryRankedPage>> RankRetrievalNodesAsync(TapestryGenerationAccess access, float[] queryVector, TapestryRetrievalGraph graph, IReadOnlyList<string>? nodeIds, int limit, CancellationToken cancellationToken) => inner.RankRetrievalNodesAsync(access, queryVector, graph, nodeIds, limit, cancellationToken);

        public Task<Result<IReadOnlyDictionary<string, TapestrySelectionMetadata>>> ReadSelectionMetadataAsync(TapestryGenerationAccess access, IReadOnlyList<string> nodeIds, CancellationToken cancellationToken) => inner.ReadSelectionMetadataAsync(access, nodeIds, cancellationToken);

        public async Task<Result<TapestryGenerationAccess>> GetRetrievableGenerationAsync(TapestryScope scope, TapestryRetrievalIdentity identity, CancellationToken cancellationToken)
        {
            Reads++;

            Result<TapestryGenerationAccess> access = await inner.GetRetrievableGenerationAsync(scope, identity, cancellationToken);

            if (Reads == 2)
            {
                Captured = access;

                Entered.TrySetResult();

                await Release.Task.WaitAsync(cancellationToken);
            }

            return access;
        }

        public Task<IReadOnlyList<TapestryRetrievedNode>> HydrateRetrievedNodesAsync(TapestryGenerationAccess access, IReadOnlyList<(string NodeId, float Similarity)> hits, TapestryRetrievalMode mode, CancellationToken cancellationToken) => inner.HydrateRetrievedNodesAsync(access, hits, mode, cancellationToken);

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
