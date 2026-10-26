using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Data;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    [Fact]
    public async Task Graph_reset_preserves_source_revisions_and_refuses_replay_of_removed_generation()
    {
        (string id, TapestryBuildSnapshot before) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(id, before, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        (string building, _) = await DiamondAsync();

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(new RecordingScopedOrdinaryConnectionFactory());

        services.AddSingleton(FixtureLabeledArtifactGuard.For(_db));

        await using ServiceProvider provider = services.BuildServiceProvider();

        EmbeddingsResetService reset = new(_db, provider);

        _ = await reset.ResetAsync(EmbeddingsResetScope.Tapestry, CancellationToken.None);

        Assert.Equal(before, await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None));

        Assert.Equal(3L, await ScalarAsync("SELECT COUNT(*) FROM workspace_file_chunks"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_edges"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_node_embeddings"));

        var replay = await _store.PublishGenerationAsync(new(building, before, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.True(replay.IsFailure);

        Assert.Equal("Tapestry.NotBuilding", replay.Error.Code);

        await ExecuteAsync("DELETE FROM workspace_file_chunks WHERE ChunkId='two';" + Chunk("two"));

        TapestryBuildSnapshot after = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        Assert.Equal(before.Corpus.Fingerprint, after.Corpus.Fingerprint);

        Assert.True(after.InvalidationRevision > before.InvalidationRevision);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.BeginGraphGenerationAsync(before, Recipe, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_generations"));
    }

    [Fact]
    public async Task Removed_scope_pruning_keeps_disabled_shared_leaf_graph_then_cascades_every_edge_when_enabled()
    {
        (string id, TapestryBuildSnapshot before) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(id, before, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        await ExecuteAsync("CREATE TABLE tapestry_node_embeddings_vec(NodeId TEXT PRIMARY KEY,Embedding BLOB NOT NULL); INSERT INTO tapestry_node_embeddings_vec SELECT NodeId,Embedding FROM tapestry_node_embeddings; DELETE FROM workspace_file_chunks;");

        Assert.Equal(0, await _store.PruneRemovedScopesAsync(false, true, true, CancellationToken.None));

        Assert.Equal(6L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_edges"));

        Assert.Equal(6L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_node_embeddings_vec"));

        Assert.Equal(1, await _store.PruneRemovedScopesAsync(true, true, true, CancellationToken.None));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_generations"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_edges"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_node_embeddings"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_node_embeddings_vec"));

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_scope_revisions WHERE ScopeKind='Workspace' AND ScopeId='/graph' AND InvalidationRevision > 0"));
    }
}
