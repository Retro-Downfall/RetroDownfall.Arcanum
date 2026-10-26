using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    private static readonly float[] DagQuery = [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];

    private static readonly ProviderSettings DagProvider = new() { Name = "test", Type = AiProviderKind.OpenAICompatible, Models = [new ModelEntry("gpt-4o")] };

    private static IModelTokenEstimator DagEstimator() => new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

    private TapestryRetriever Retriever() => new(_store, DagEstimator(), NullLogger<TapestryRetriever>.Instance);

    private static TapestryRetrievalRequest DagRequest(TapestryRetrievalMode mode = TapestryRetrievalMode.CollapsedTree, int maxNodes = 5, int maxBytes = 262144, int maxTokens = 32768) =>
        new([Scope], DagQuery, QueryIdentity, mode, maxNodes, maxBytes, maxTokens, -1f, DagProvider, "gpt-4o");

    private async Task<string[]> RetrieveIdsAsync(TapestryRetrievalRequest request)
    {
        var result = await Retriever().RetrieveAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);

        return [.. result.Value.Select(static node => node.NodeId)];
    }

    private async Task<string> PublishDagAsync(Func<string, Task>? stage = null)
    {
        (string id, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        if (stage is not null)
        {
            await stage(id);
        }

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        return id;
    }

    [Fact]
    public async Task Collapsed_RanksLeavesAndSummariesTogether()
    {
        string id = await PublishDagAsync();

        Assert.Equal([id + "one", id + "three", id + "two"], await RetrieveIdsAsync(DagRequest()));
    }

    [Fact]
    public async Task AllPathAncestor_IsSuppressed()
    {
        string id = await PublishDagAsync(async generation =>
        {
            await SetDagVectorAsync(generation + "b", 0.9f);

            await SetDagVectorAsync(generation + "one", 0.8f);

            await SetDagVectorAsync(generation + "three", 0.7f);
        });

        Assert.Equal([id + "two", id + "one", id + "three"], await RetrieveIdsAsync(DagRequest()));
    }

    [Fact]
    public async Task FullyCoveredSibling_IsSuppressed()
    {
        string id = await PublishDagAsync(async generation =>
        {
            await _store.AppendGraphCheckpointAsync(new(generation, [Summary(generation, "c", 1)],
                [Edge(generation, "c", "one"), Edge(generation, "c", "two")]), CancellationToken.None);

            await SetDagVectorAsync(generation + "a", 1f);

            await SetDagVectorAsync(generation + "b", 1f);

            await SetDagVectorAsync(generation + "c", 1f);
        });

        Assert.Equal([id + "a", id + "b"], await RetrieveIdsAsync(DagRequest()));
    }

    [Fact]
    public async Task PartlyOverlappingSibling_RemainsEligible()
    {
        string id = await PublishDagAsync(async generation =>
        {
            await SetDagVectorAsync(generation + "a", 1f);

            await SetDagVectorAsync(generation + "b", 1f);
        });

        Assert.Equal([id + "a", id + "b"], await RetrieveIdsAsync(DagRequest(maxNodes: 2)));
    }

    [Fact]
    public async Task EqualCosine_UsesTrustworthyRecencyThenStableId()
    {
        string id = await PublishDagAsync();

        await ExecuteAsync("UPDATE workspace_file_chunks SET FileLastWriteTime='2026-02-01T00:00:00.0000000Z' WHERE ChunkId='two'");

        Assert.Equal([id + "two", id + "one", id + "three"], await RetrieveIdsAsync(DagRequest()));
    }

    [Fact]
    public async Task UnknownTime_AfterKnownThenStableId()
    {
        string id = await PublishDagAsync();

        await ExecuteAsync("UPDATE workspace_file_chunks SET FileLastWriteTime='unknown' WHERE ChunkId='one'");

        Assert.Equal([id + "three", id + "two", id + "one"], await RetrieveIdsAsync(DagRequest()));
    }

    [Fact]
    public async Task HigherCosine_WinsOverAnyDate()
    {
        string id = await PublishDagAsync(generation => SetDagVectorAsync(generation + "two", 0.8f));

        await ExecuteAsync("UPDATE workspace_file_chunks SET FileLastWriteTime='2099-02-01T00:00:00.0000000Z' WHERE ChunkId='two'");

        Assert.Equal([id + "one", id + "three", id + "two"], await RetrieveIdsAsync(DagRequest()));
    }

    [Fact]
    public async Task RejectedTopCandidate_BackfillsNextEligible()
    {
        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='" + new string('x', 3000) + "' WHERE ChunkId='one'");

        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        TapestryNodeWrite large = Leaf(id, "one");

        large = large with { Node = large.Node with { ContentHash = TapestryHash.OfContent(new string('x', 3000)) } };

        await _store.AppendGraphCheckpointAsync(new(id, [large, Leaf(id, "two"), Leaf(id, "three")], []), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        Assert.Equal([id + "three", id + "two"], await RetrieveIdsAsync(DagRequest(maxBytes: 1500)));
    }

    [Theory]
    [InlineData(TapestryRetrievalMode.CollapsedTree)]
    [InlineData(TapestryRetrievalMode.TreeTraversal)]
    public async Task MixedLayerCarry_IsAReachableRoot(TapestryRetrievalMode mode)
    {
        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendGraphCheckpointAsync(new(id, [Leaf(id, "one"), Leaf(id, "two"), Leaf(id, "three"), Summary(id, "a", 1)], [Edge(id, "a", "one"), Edge(id, "a", "two")]), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.MaxDepth, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        Assert.Equal([id + "one", id + "three", id + "two"], await RetrieveIdsAsync(DagRequest(mode)));
    }

    private async Task SetDagVectorAsync(string nodeId, float cosine)
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "UPDATE tapestry_node_embeddings SET Embedding=@embedding WHERE NodeId=@id";

        var embedding = command.CreateParameter();

        embedding.ParameterName = "@embedding";

        float[] vector = [cosine, MathF.Sqrt(1 - cosine * cosine), 0f, 0f, 0f, 0f, 0f, 0f];

        embedding.Value = System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

        command.Parameters.Add(embedding);

        var id = command.CreateParameter();

        id.ParameterName = "@id";

        id.Value = nodeId;

        command.Parameters.Add(id);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
