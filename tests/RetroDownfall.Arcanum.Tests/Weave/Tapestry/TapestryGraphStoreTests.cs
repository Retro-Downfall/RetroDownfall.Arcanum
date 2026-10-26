using System.Data.Common;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

[Collection("Grimoire")]
public sealed partial class TapestryGraphStoreTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private static readonly TapestryScope Scope = new(TapestryScopeKind.Workspace, "/graph");

    private static readonly TapestryGenerationRecipe Recipe = new(RaptorAlgorithmManifest.Version, "settings", "summary-model", TapestryHash.SummaryRecipeVersion, 8, TapestryHash.OfEmbeddingModel("provider", "model"));

    private string _path = string.Empty;

    private ArcanumDbContext _db = null!;

    private TapestryStore _store = null!;

    public async Task InitializeAsync()
    {
        _path = fixture.CopyDatabase();

        _db = fixture.CreateContext(_path);

        _store = new TapestryStore(_db, new WeaveIndexAvailability());

        await ExecuteAsync(Chunk("one") + Chunk("two") + Chunk("three"));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        File.Delete(_path);
    }

    [Fact]
    public async Task Diamond_CountsThreeDistinctLeavesNotFour()
    {
        (string id, _) = await DiamondAsync();

        IReadOnlyDictionary<string, IReadOnlyList<string>> leaves = await _store.GetDescendantLeafIdsAsync(id, [id + "root"], CancellationToken.None);

        Assert.Equal(["one", "three", "two"], leaves[id + "root"]);

        Assert.Equal(3L, await ScalarAsync($"SELECT DescendantLeafCount FROM tapestry_nodes WHERE NodeId='{id}root'"));

        Assert.Equal([id + "root"], await _store.GetRootIdsAsync(id, CancellationToken.None));

        Assert.Equal([id + "one", id + "three", id + "two"], await _store.GetChildIdsAsync(id, [id + "a", id + "b"], CancellationToken.None));
    }

    [Fact]
    public async Task AncestorClosure_FollowsBothParentsOnce()
    {
        (string id, _) = await DiamondAsync();

        IReadOnlyDictionary<string, IReadOnlyList<string>> ancestors = await _store.GetAncestorIdsAsync(id, [id + "two"], CancellationToken.None);

        Assert.Equal([id + "a", id + "b", id + "root"], ancestors[id + "two"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_IsAtomic(bool legacy)
    {
        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        TapestryGenerationRecipe recipe = legacy ? Recipe with { AlgorithmVersion = SphericalKMeans.AlgorithmVersion } : Recipe;

        string id = await _store.BeginGraphGenerationAsync(snapshot, recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendGraphCheckpointAsync(new(id, [Leaf(id, "one")], []), CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => _store.AppendGraphCheckpointAsync(new(id,
            [Summary(id, "a", 1)], [Edge(id, "a", "one"), Edge(id, "a", "missing")], legacy), CancellationToken.None));

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_nodes"));

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_node_embeddings"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_edges"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_nodes WHERE ParentNodeId IS NOT NULL"));
    }

    [Fact]
    public async Task SourceEdit_InvalidatesBothAncestorBranches()
    {
        (string id, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        Assert.NotNull(await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));

        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='edited' WHERE ChunkId='two';");

        Assert.Null(await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));

        Assert.Null(await _store.TryGetReusableSummaryAsync(Scope, "a", CancellationToken.None));

        Assert.Null(await _store.TryGetReusableSummaryAsync(Scope, "b", CancellationToken.None));
    }

    [Fact]
    public async Task PureAppend_KeepsOldSummaryUsableButRequiresNewPublicationSnapshot()
    {
        (string current, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(current, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        (string next, TapestryBuildSnapshot nextSnapshot) = await DiamondAsync();

        await ExecuteAsync(Chunk("four"));

        Assert.Equal(current, (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))?.GenerationId);

        Result refused = await _store.PublishGenerationAsync(new(next, nextSnapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal("Tapestry.SourceChanged", refused.Error.Code);

        Assert.Equal(current, (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))?.GenerationId);
    }

    [Fact]
    public async Task Publish_RejectsChangedRevision()
    {
        (string id, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='changed' WHERE ChunkId='one';");

        Result result = await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Tapestry.SourceChanged", result.Error.Code);

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_generations WHERE Status='Complete'"));
    }

    [Fact]
    public async Task OldValidGeneration_RemainsDuringBuild()
    {
        (string id, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        (string next, _) = await DiamondAsync();

        Assert.Equal(id, (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))?.GenerationId);

        await _store.AbandonGenerationAsync(next, CancellationToken.None);

        Assert.Equal(id, (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))?.GenerationId);
    }

    [Fact]
    public async Task Publish_refuses_missing_coverage_and_invalid_vectors_without_retiring_previous_generation()
    {
        (string current, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(current, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        string incomplete = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendGraphCheckpointAsync(new(incomplete, [Leaf(incomplete, "one")], []), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(incomplete, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsFailure);

        TapestryNodeWrite invalid = Leaf(incomplete, "two") with { Embedding = new float[8] };

        await Assert.ThrowsAnyAsync<Exception>(() => _store.AppendGraphCheckpointAsync(new(incomplete, [invalid], []), CancellationToken.None));

        Assert.Equal(current, (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))?.GenerationId);
    }

    [Fact]
    public async Task Publication_RefusesLayerCountOverflow()
    {
        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendGraphCheckpointAsync(new(id,
            [Leaf(id, "one"), Leaf(id, "two"), Leaf(id, "three"), Summary(id, "root", int.MaxValue)],
            [Edge(id, "root", "one"), Edge(id, "root", "two"), Edge(id, "root", "three")]), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_generations WHERE Status='Complete'"));
    }

    private async Task<(string Id, TapestryBuildSnapshot Snapshot)> DiamondAsync()
    {
        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendGraphCheckpointAsync(new(id,
            [Leaf(id, "one"), Leaf(id, "two"), Leaf(id, "three"), Summary(id, "a", 1), Summary(id, "b", 1), Summary(id, "root", 2)],
            [Edge(id, "a", "one"), Edge(id, "a", "two"), Edge(id, "b", "two"), Edge(id, "b", "three"), Edge(id, "root", "a"), Edge(id, "root", "b")]), CancellationToken.None);

        return (id, snapshot);
    }

    private static TapestryGraphEdge Edge(string generation, string parent, string child) => new(generation + parent, generation + child, 0.5, 0.6, "Posterior");

    private static TapestryNodeWrite Leaf(string generation, string source) => new(
        new(generation + source, generation, Scope.Kind, Scope.Id, 0, TapestryNodeKind.Leaf, null,
            TapestryLeafSourceKind.WorkspaceFileChunk, source, "a.cs", null, TapestryHash.OfContent(source), null, 1, 0, TapestryPartitionReason.None, 8, DateTimeOffset.UtcNow),
        [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f]);

    private static TapestryNodeWrite Summary(string generation, string node, int layer) => new(
        new(generation + node, generation, Scope.Kind, Scope.Id, layer, TapestryNodeKind.Summary, null,
            null, null, "summary", node, TapestryHash.OfContent(node), node, 1, 0, TapestryPartitionReason.None, 8, DateTimeOffset.UtcNow),
        [0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f]);

    private static string Chunk(string id) => $"""
        INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
        VALUES('{id}','/graph','a.cs',0,'{id}',0,3,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
        """;

    private async Task ExecuteAsync(string sql)
    {
        await _db.Database.OpenConnectionAsync();

        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}
