using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalBackfill_ResumesEarlierScopeAfterLaterBudgetDisplacement(bool reverse)
    {
        await ExecuteAsync("DELETE FROM workspace_file_chunks");

        async Task<string> Publish(TapestryScope scope, (string Id, string Content, float Cosine)[] sources)
        {
            foreach (var source in sources)
            {
                await ExecuteAsync(Chunk(source.Id).Replace("'/graph'", "'" + scope.Id + "'", StringComparison.Ordinal)
                    .Replace("'" + source.Id + "',0,3", "'" + source.Content + "',0,3", StringComparison.Ordinal));
            }

            var snapshot = await _store.ReadBuildSnapshotAsync(scope, CancellationToken.None);

            string generation = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

            var nodes = sources.Select(source =>
            {
                var node = Leaf(generation, source.Id);

                return node with
                {
                    Node = node.Node with { ScopeId = scope.Id, ContentHash = TapestryHash.OfContent(source.Content) },
                    Embedding = [source.Cosine, MathF.Sqrt(1 - source.Cosine * source.Cosine), 0f, 0f, 0f, 0f, 0f, 0f],
                };
            }).ToArray();

            await _store.AppendGraphCheckpointAsync(new(generation, nodes, []), CancellationToken.None);

            Assert.True((await _store.PublishGenerationAsync(new(generation, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

            return generation;
        }

        TapestryScope a = new(TapestryScopeKind.Workspace, "/alpha");

        TapestryScope b = new(TapestryScopeKind.Workspace, "/beta");

        string ga = await Publish(a, [("A1", new string('a', 300), 0.9f), ("A2", new string('b', 300), 0.8f), ("A3", "tail", 0.7f)]);

        string gb = await Publish(b, [("B1", new string('c', 850), 1f)]);

        var accessA = (await _store.GetRetrievableGenerationAsync(a, QueryIdentity, CancellationToken.None)).Value;

        var accessB = (await _store.GetRetrievableGenerationAsync(b, QueryIdentity, CancellationToken.None)).Value;

        var nodesA = await _store.HydrateRetrievedNodesAsync(accessA, [(ga + "A1", 0.9f), (ga + "A2", 0.8f), (ga + "A3", 0.7f)], TapestryRetrievalMode.TreeTraversal, CancellationToken.None);

        var nodeB = Assert.Single(await _store.HydrateRetrievedNodesAsync(accessB, [(gb + "B1", 1f)], TapestryRetrievalMode.TreeTraversal, CancellationToken.None));

        int Bytes(params TapestryRetrievedNode[] nodes) => Encoding.UTF8.GetByteCount(SystemPromptBuilder.RenderTapestryContext(nodes));

        int budget = Bytes(nodeB, nodesA[2]);

        Assert.True(Bytes(nodesA[0], nodesA[1]) < budget);

        Assert.True(Bytes(nodeB, nodesA[0]) > budget);

        Assert.True(Bytes(nodeB, nodesA[1]) > budget);

        var request = DagRequest(TapestryRetrievalMode.TreeTraversal, maxNodes: 2, maxBytes: budget)
            with
        { EligibleScopes = reverse ? [b, a] : [a, b] };

        var result = await Retriever().RetrieveAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal([gb + "B1", ga + "A3"], result.Value.Select(static node => node.NodeId));

        Assert.Equal(budget, Bytes(result.Value.ToArray()));

        Assert.True(DagEstimator().EstimateText(DagProvider, "gpt-4o", SystemPromptBuilder.RenderTapestryContext(result.Value)).TokenCount < request.MaxTokens);
    }

    [Theory]
    [InlineData(300, false)]
    [InlineData(600, false)]
    [InlineData(1200, false)]
    [InlineData(300, true)]
    [InlineData(600, true)]
    [InlineData(1200, true)]
    public async Task SelectedFrontier_NativeWorkDoesNotScanUnrelatedGenerationRows(int generationNodes, bool empty)
    {
        string id = await PublishWideDagAsync(generationNodes, false);

        var access = (await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None)).Value;

        var graph = (await _store.ReadRetrievalGraphAsync(access, CancellationToken.None)).Value;

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        long instructions = 0;

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => { instructions++; return 0; }, null);

        try
        {
            string[] ids = empty ? [] : [id + "wide" + (generationNodes - 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)];

            var ranked = await _store.RankRetrievalNodesAsync(access, DagQuery, graph, ids, 32, CancellationToken.None);

            Assert.True(ranked.IsSuccess);

            Assert.Equal(empty ? 0 : 1, ranked.Value.ScoredCount);

            Assert.Equal(empty ? 0 : 1, ranked.Value.Nodes.Count);

            output.WriteLine("Generation={0}; selected={1}; native VM steps={2}", generationNodes, ids.Length, instructions);

            Assert.InRange(instructions, 1, empty ? 1000 : 2000);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    public async Task TraversalContinuations_DoNotRehydrateOrRerenderUnchangedAcceptedPrefixes(int count)
    {
        string id = await PublishDuplicateLeavesAsync(count);

        DagStoreProbe probe = new(_store);

        DagEstimateProbe estimator = new(DagEstimator());

        var result = await new TapestryRetriever(probe, estimator, NullLogger<TapestryRetriever>.Instance)
            .RetrieveAsync(DagRequest(TapestryRetrievalMode.TreeTraversal, maxNodes: 2), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(id + "dup0000", Assert.Single(result.Value).NodeId);

        output.WriteLine("Candidates={0}; hydrations={1}; metadata={2}; render estimates={3}", count, probe.HydrationCount, probe.MetadataCount, estimator.TextCalls);

        Assert.Equal(count, probe.ScoredCount);

        Assert.Equal(count, probe.HydrationCount);

        Assert.Equal(count, probe.MetadataCount);

        Assert.Equal(1, estimator.TextCalls);
    }

    [Fact]
    public async Task SelectedFrontier_ParameterPagesPreserveDeduplicationTopRankAndCapturedIdentity()
    {
        string id = await PublishWideDagAsync(600, false);

        var access = (await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None)).Value;

        var graph = (await _store.ReadRetrievalGraphAsync(access, CancellationToken.None)).Value;

        string[] ids = [.. Enumerable.Range(0, 600).Select(index => id + "wide" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)), id + "wide0000", "absent", "'); DROP TABLE tapestry_nodes; --"];

        var all = await _store.RankRetrievalNodesAsync(access, DagQuery, graph, ids, 601, CancellationToken.None);

        Assert.True(all.IsSuccess);

        Assert.Equal(600, all.Value.ScoredCount);

        Assert.Equal(600, all.Value.Nodes.Count);

        Assert.False(all.Value.Truncated);

        var top = await _store.RankRetrievalNodesAsync(access, DagQuery, graph, ids, 7, CancellationToken.None);

        Assert.True(top.IsSuccess);

        Assert.Equal(600, top.Value.ScoredCount);

        Assert.True(top.Value.Truncated);

        Assert.Equal(Enumerable.Range(0, 7).Select(index => id + "wide" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)), top.Value.Nodes.Select(static node => node.NodeId));

        var forged = access with { Identity = QueryIdentity with { EmbeddingIdentity = "other" } };

        Assert.True((await _store.RankRetrievalNodesAsync(forged, DagQuery, graph, [], 32, CancellationToken.None)).IsFailure);

        using CancellationTokenSource cancellation = new();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        raw.sqlite3_progress_handler(connection.Handle, 100, (object _) => { cancellation.Cancel(); return 0; }, null);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.RankRetrievalNodesAsync(access, DagQuery, graph, ids, 32, cancellation.Token));
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        Assert.True((await _store.RankRetrievalNodesAsync(access, DagQuery, graph, [id + "wide0599"], 32, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task SelectionPrefix_ChangeRechecksEarlierBudgetAndCoverageRejections()
    {
        string large = new('x', 850);

        string independent = new('z', 300);

        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='" + independent + "' WHERE ChunkId='three'");

        var snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        var third = Leaf(id, "three");

        var x = Summary(id, "X", 1);

        var y = Summary(id, "Y", 1);

        await _store.AppendGraphCheckpointAsync(new(id,
            [Leaf(id, "one"), Leaf(id, "two"), third with { Node = third.Node with { ContentHash = TapestryHash.OfContent(independent) } },
             x with { Node = x.Node with { Content = large, ContentHash = TapestryHash.OfContent(large) } },
             y with { Node = y.Node with { Content = "tail", ContentHash = TapestryHash.OfContent("tail") } }, Summary(id, "R", 2)],
            [Edge(id, "X", "one"), Edge(id, "X", "two"), Edge(id, "Y", "one"), Edge(id, "Y", "two"), Edge(id, "R", "Y")]), CancellationToken.None);

        await SetDagVectorAsync(id + "X", 0.9f);

        await SetDagVectorAsync(id + "Y", 1f);

        await SetDagVectorAsync(id + "three", 0.8f);

        await SetDagVectorAsync(id + "one", 0f);

        await SetDagVectorAsync(id + "two", 0f);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.MaxDepth, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        var access = (await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None)).Value;

        var initial = await _store.HydrateRetrievedNodesAsync(access, [(id + "X", 0.9f)], TapestryRetrievalMode.TreeTraversal, CancellationToken.None);

        int bytes = Encoding.UTF8.GetByteCount(SystemPromptBuilder.RenderTapestryContext(initial));

        DagStoreProbe probe = new(_store);

        var result = await new TapestryRetriever(probe, DagEstimator(), NullLogger<TapestryRetriever>.Instance)
            .RetrieveAsync(DagRequest(TapestryRetrievalMode.TreeTraversal, maxNodes: 2, maxBytes: bytes), CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal([id + "Y", id + "three"], result.Value.Select(static node => node.NodeId));

        Assert.Equal(6, probe.ScoredCount);

        Assert.InRange(probe.HydrationCount, 1, 8);
    }

    private async Task<string> PublishDuplicateLeavesAsync(int count)
    {
        await ExecuteAsync("DELETE FROM workspace_file_chunks");

        await ExecuteAsync($"""
            WITH RECURSIVE numbers(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM numbers WHERE n<{count - 1})
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            SELECT printf('dup%04d',n),'/graph','a.cs',n,'same',0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM numbers
            """);

        var snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        var nodes = Enumerable.Range(0, count).Select(index =>
        {
            var node = Leaf(id, "dup" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));

            return node with { Node = node.Node with { ContentHash = TapestryHash.OfContent("same") } };
        }).ToArray();

        await _store.AppendGraphCheckpointAsync(new(id, nodes, []), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        return id;
    }

    [Fact]
    public async Task CachedSelection_StillRefusesSourceChangeDuringLaterContinuation()
    {
        await PublishDuplicateLeavesAsync(6);

        DagStoreProbe probe = new(_store);

        int hydration = 0;

        async Task BeforeHydrate()
        {
            hydration++;

            if (hydration == 3)
            {
                await ExecuteAsync("UPDATE workspace_file_chunks SET Content='changed' WHERE ChunkId='dup0000'");
            }
            else
            {
                probe.BeforeHydrate = BeforeHydrate;
            }
        }

        probe.BeforeHydrate = BeforeHydrate;

        var result = await new TapestryRetriever(probe, DagEstimator(), NullLogger<TapestryRetriever>.Instance)
            .RetrieveAsync(DagRequest(TapestryRetrievalMode.TreeTraversal, maxNodes: 2), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Tapestry.SourceChanged", result.Error.Code);

        Assert.True(hydration >= 3);
    }

    private sealed class DagEstimateProbe(IModelTokenEstimator inner) : IModelTokenEstimator
    {
        internal int TextCalls { get; private set; }

        public ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string canonicalModel) => inner.ResolveProfile(provider, canonicalModel);

        public ResolvedModelTokenizationProfile ResolveEffectiveProfile(ProviderSettings provider, string canonicalModel) => inner.ResolveEffectiveProfile(provider, canonicalModel);

        public TokenEstimate EstimateText(ProviderSettings provider, string canonicalModel, string? text)
        {
            TextCalls++;

            return inner.EstimateText(provider, canonicalModel, text);
        }

        public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request) => inner.EstimateContext(request);
    }
}
