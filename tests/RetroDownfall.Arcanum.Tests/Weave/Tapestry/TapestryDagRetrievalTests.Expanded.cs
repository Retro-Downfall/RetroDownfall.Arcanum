using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    [Fact]
    public async Task Traversal_SelectsBestUnionBeforeExpandingLowerRankedParent()
    {
        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendGraphCheckpointAsync(new(id,
            [Leaf(id, "one"), Leaf(id, "two"), Leaf(id, "three"), Summary(id, "a", 1), Summary(id, "b", 1), Summary(id, "c", 1), Summary(id, "root", 2)],
            [Edge(id, "a", "one"), Edge(id, "b", "two"), Edge(id, "c", "three"), Edge(id, "root", "a"), Edge(id, "root", "b"), Edge(id, "root", "c")]), CancellationToken.None);

        await SetDagVectorAsync(id + "a", 1f);

        await SetDagVectorAsync(id + "b", 0.9f);

        await SetDagVectorAsync(id + "one", 0.8f);

        await SetDagVectorAsync(id + "two", 0.7f);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        Assert.Equal([id + "a", id + "b"], await RetrieveIdsAsync(DagRequest(TapestryRetrievalMode.TreeTraversal, maxNodes: 2)));
    }

    [Fact]
    public async Task Traversal_RanksDistinctUnionOfSelectedChildren_AndScoresSharedChildOnce()
    {
        string id = await PublishDagAsync();

        DagStoreProbe probe = new(_store);

        TapestryRetriever retriever = new(probe, DagEstimator(), NullLogger<TapestryRetriever>.Instance);

        var result = await retriever.RetrieveAsync(DagRequest(TapestryRetrievalMode.TreeTraversal, maxNodes: 2), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal([id + "one", id + "three"], result.Value.Select(static node => node.NodeId));

        Assert.Equal(6, probe.ScoredCount);

        Assert.Contains(probe.RankRequests, ids => ids.SequenceEqual(new[] { id + "one", id + "three", id + "two" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryRecency_UsesAllDistinctDescendants_NotBuildTime(bool unknown)
    {
        string id = await PublishDagAsync(async generation =>
        {
            await SetDagVectorAsync(generation + "a", 1f);

            await SetDagVectorAsync(generation + "b", 1f);
        });

        await ExecuteAsync("UPDATE workspace_file_chunks SET FileLastWriteTime='2026-02-01T00:00:00.0000000Z' WHERE ChunkId='two'");

        await ExecuteAsync("UPDATE workspace_file_chunks SET FileLastWriteTime='" + (unknown ? "unknown" : "2026-03-01T00:00:00.0000000Z") + "' WHERE ChunkId='one'");

        Assert.Equal([id + (unknown ? "b" : "a")], await RetrieveIdsAsync(DagRequest(maxNodes: 1)));

        var access = await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None);

        var graph = await _store.ReadRetrievalGraphAsync(access.Value, CancellationToken.None);

        Assert.Equal(unknown ? null : new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), graph.Value.Vertices[id + "root"].SourceEventAt);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("revert")]
    [InlineData("delete")]
    [InlineData("publish")]
    public async Task CapturedAccess_RefusesChangeDuringMaterialization(string change)
    {
        await PublishDagAsync();

        DagStoreProbe probe = new(_store)
        {
            BeforeHydrate = async () =>
            {
                if (change == "publish")
                {
                    await PublishDagAsync();
                }
                else
                {
                    await ExecuteAsync(change == "delete" ? "DELETE FROM workspace_file_chunks WHERE ChunkId='two'" : "UPDATE workspace_file_chunks SET Content='changed' WHERE ChunkId='two'");

                    if (change == "revert")
                    {
                        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='two' WHERE ChunkId='two'");
                    }
                }
            }
        };

        TapestryRetriever retriever = new(probe, DagEstimator(), NullLogger<TapestryRetriever>.Instance);

        var result = await retriever.RetrieveAsync(DagRequest(), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Tapestry.SourceChanged", result.Error.Code);
    }

    [Fact]
    public async Task PureAppend_KeepsCapturedFactsEligibleDuringMaterialization()
    {
        string id = await PublishDagAsync();

        DagStoreProbe probe = new(_store) { BeforeHydrate = () => ExecuteAsync(Chunk("four")) };

        var result = await new TapestryRetriever(probe, DagEstimator(), NullLogger<TapestryRetriever>.Instance).RetrieveAsync(DagRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal([id + "one", id + "three", id + "two"], result.Value.Select(static node => node.NodeId));
    }

    [Fact]
    public async Task RenderedTokens_RespectExactBudget_AndRejectedStateDoesNotSuppressBackfill()
    {
        string content = string.Concat(Enumerable.Repeat("Unicode 👩🏽‍💻 café ``` hostile ``` text. ", 30));

        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='" + content + "' WHERE ChunkId='one'");

        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        TapestryNodeWrite large = Leaf(id, "one");

        large = large with { Node = large.Node with { ContentHash = TapestryHash.OfContent(content), SourceLabel = "hostile\nlabel ```" } };

        await _store.AppendGraphCheckpointAsync(new(id, [large, Leaf(id, "two"), Leaf(id, "three")], []), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        var result = await Retriever().RetrieveAsync(DagRequest(maxTokens: 2000), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal([id + "three", id + "two"], result.Value.Select(static node => node.NodeId));

        string rendered = SystemPromptBuilder.RenderTapestryContext(result.Value);

        Assert.InRange(DagEstimator().EstimateText(DagProvider, "gpt-4o", rendered).TokenCount, 1, 2000);

        Assert.InRange(Encoding.UTF8.GetByteCount(rendered), 1, 262144);

        Assert.Contains("UNTRUSTED DATA", rendered, StringComparison.Ordinal);

        Assert.DoesNotContain(content, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlreadyMarginedEstimate_IsNotChargedTwice()
    {
        string id = await PublishDagAsync();

        var access = await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None);

        var one = await _store.HydrateRetrievedNodesAsync(access.Value, [(id + "one", 1f)], TapestryRetrievalMode.CollapsedTree, CancellationToken.None);

        var estimate = DagEstimator().EstimateText(DagProvider, "gpt-4o", SystemPromptBuilder.RenderTapestryContext(one));

        Assert.True(estimate.SafetyMarginTokens > 0);

        Assert.InRange(estimate.TokenCount, 128, 1048576);

        Assert.Equal([id + "one"], await RetrieveIdsAsync(DagRequest(maxNodes: 1, maxTokens: estimate.TokenCount)));
    }

    [Fact]
    public async Task RenderedBytes_IncludeHeadingMetadataAndFences()
    {
        string content = new('x', 1000);

        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='" + content + "' WHERE ChunkId='one'");

        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        TapestryNodeWrite large = Leaf(id, "one");

        large = large with { Node = large.Node with { ContentHash = TapestryHash.OfContent(content) } };

        await _store.AppendGraphCheckpointAsync(new(id, [large, Leaf(id, "two"), Leaf(id, "three")], []), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        Assert.Equal([id + "three", id + "two"], await RetrieveIdsAsync(DagRequest(maxBytes: 1500)));
    }

    [Fact]
    public async Task ExactContentAcrossDistinctSourceIds_IsReturnedOnce()
    {
        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='same' WHERE ChunkId IN ('one','two')");

        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        TapestryNodeWrite one = Leaf(id, "one");

        TapestryNodeWrite two = Leaf(id, "two");

        await _store.AppendGraphCheckpointAsync(new(id,
            [one with { Node = one.Node with { ContentHash = TapestryHash.OfContent("same") } },
             two with { Node = two.Node with { ContentHash = TapestryHash.OfContent("same") } }, Leaf(id, "three")], []), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        Assert.Equal([id + "one", id + "three"], await RetrieveIdsAsync(DagRequest()));
    }

    [Fact]
    public async Task Traversal_BackfillsAfterTheCombinedScopeRenderRejectsLocalWinners()
    {
        string alpha = new('a', 850);

        string beta = new string('b', 299) + "1";

        string gamma = new string('b', 299) + "2";

        await ExecuteAsync("UPDATE workspace_file_chunks SET WorkspacePath='/alpha',Content='" + alpha + "' WHERE ChunkId='one'");

        await ExecuteAsync("UPDATE workspace_file_chunks SET WorkspacePath='/beta',Content='" + beta + "' WHERE ChunkId='two'");

        await ExecuteAsync("UPDATE workspace_file_chunks SET WorkspacePath='/beta',Content='" + gamma + "' WHERE ChunkId='three'");

        await ExecuteAsync(Chunk("four").Replace("'/graph'", "'/beta'", StringComparison.Ordinal));

        async Task<string> Publish(TapestryScope scope, (string Source, string Content, float Cosine)[] sources)
        {
            var snapshot = await _store.ReadBuildSnapshotAsync(scope, CancellationToken.None);

            string generation = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

            var nodes = sources.Select(source =>
            {
                var node = Leaf(generation, source.Source);

                return node with
                {
                    Node = node.Node with { ScopeId = scope.Id, ContentHash = TapestryHash.OfContent(source.Content) },
                    Embedding = [source.Cosine, MathF.Sqrt(1 - source.Cosine * source.Cosine), 0f, 0f, 0f, 0f, 0f, 0f]
                };
            }).ToArray();

            await _store.AppendGraphCheckpointAsync(new(generation, nodes, []), CancellationToken.None);

            Assert.True((await _store.PublishGenerationAsync(new(generation, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

            return generation;
        }

        TapestryScope a = new(TapestryScopeKind.Workspace, "/alpha");

        TapestryScope b = new(TapestryScopeKind.Workspace, "/beta");

        string first = await Publish(a, [("one", alpha, 1f)]);

        string second = await Publish(b, [("two", beta, 0.9f), ("three", gamma, 0.8f), ("four", "four", 0.7f)]);

        var accessA = await _store.GetRetrievableGenerationAsync(a, QueryIdentity, CancellationToken.None);

        var accessB = await _store.GetRetrievableGenerationAsync(b, QueryIdentity, CancellationToken.None);

        var nodeA = await _store.HydrateRetrievedNodesAsync(accessA.Value, [(first + "one", 1f)], TapestryRetrievalMode.TreeTraversal, CancellationToken.None);

        var tail = await _store.HydrateRetrievedNodesAsync(accessB.Value, [(second + "four", 0.7f)], TapestryRetrievalMode.TreeTraversal, CancellationToken.None);

        int bytes = Encoding.UTF8.GetByteCount(SystemPromptBuilder.RenderTapestryContext([nodeA[0], tail[0]]));

        var result = await Retriever().RetrieveAsync(DagRequest(TapestryRetrievalMode.TreeTraversal, maxNodes: 2, maxBytes: bytes) with { EligibleScopes = [a, b] }, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal([first + "one", second + "four"], result.Value.Select(static node => node.NodeId));
    }

    [Theory]
    [InlineData(TapestryRetrievalMode.CollapsedTree)]
    [InlineData(TapestryRetrievalMode.TreeTraversal)]
    public async Task TypedRetriever_PreservesProvedLegacyEmbeddingIdentity(TapestryRetrievalMode mode)
    {
        var legacy = new TapestryLegacySettings(5, 8, 24, 256, 512);

        var snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string settings = TapestryHash.OfSettings(5, 8, 24, 256, 512, 8, QueryIdentity.EmbeddingIdentity);

        string id = await _store.BeginGenerationAsync(Scope, SphericalKMeans.AlgorithmVersion, settings, "summary", TapestryHash.SummaryRecipeVersion, 8, snapshot.Corpus.Fingerprint, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendNodesAsync([Leaf(id, "one"), Leaf(id, "two"), Leaf(id, "three")], CancellationToken.None);

        await _store.PublishGenerationAsync(id, 1, 3, 3, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal([id + "one", id + "three", id + "two"], await RetrieveIdsAsync(DagRequest(mode) with { Identity = QueryIdentity with { LegacySettings = legacy } }));

        Assert.Empty(await RetrieveIdsAsync(DagRequest(mode)));
    }

    [Fact]
    public async Task OversizedBelowThresholdSummary_StillRoutesToUsefulChildren()
    {
        string content = new('x', 2048);

        string id = await PublishDagAsync(generation => ExecuteAsync("UPDATE tapestry_nodes SET Content='" + content + "',ContentHash='" + TapestryHash.OfContent(content) + "' WHERE NodeId='" + generation + "root'"));

        Assert.Equal([id + "one", id + "three"], await RetrieveIdsAsync(DagRequest(TapestryRetrievalMode.TreeTraversal, maxNodes: 2, maxBytes: 1500) with { MinSimilarity = 0.5f }));
    }

    [Fact]
    public async Task EmptyEligibleScopes_AndInvalidActualWidth_DoNotQueryStore()
    {
        DagStoreProbe probe = new(_store);

        TapestryRetriever retriever = new(probe, DagEstimator(), NullLogger<TapestryRetriever>.Instance);

        Assert.Empty((await retriever.RetrieveAsync(DagRequest() with { EligibleScopes = [] }, CancellationToken.None)).Value);

        var invalid = await retriever.RetrieveAsync(DagRequest() with { QueryVector = [1f] }, CancellationToken.None);

        Assert.True(invalid.IsFailure);

        Assert.Equal("Tapestry.InvalidQuery", invalid.Error.Code);

        Assert.Equal(0, probe.LookupCount);
    }
}
