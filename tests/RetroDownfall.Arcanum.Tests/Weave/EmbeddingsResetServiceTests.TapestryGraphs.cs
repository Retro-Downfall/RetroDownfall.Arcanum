using System.Data.Common;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class EmbeddingsResetServiceTests
{
    [Theory]
    [InlineData(EmbeddingsResetScope.Tapestry)]
    [InlineData(EmbeddingsResetScope.All)]
    public async Task Reset_removes_real_shared_leaf_graphs_with_exact_pre_cascade_counts(EmbeddingsResetScope scope)
    {
        await SeedResetGraphsAsync();

        EmbeddingsResetResult result = await _resetService!.ResetAsync(scope, CancellationToken.None);

        foreach ((string table, long expected) in new (string, long)[]
        {
            ("tapestry_generations", 2),
            ("tapestry_nodes", 6),
            ("tapestry_edges", 4),
            ("tapestry_node_embeddings", 6),
        })
        {
            Assert.Equal(expected, result.DeletedRowCounts[table]);

            Assert.Equal(0, await ResetScalarAsync("SELECT COUNT(*) FROM " + table));
        }

        if (scope == EmbeddingsResetScope.Tapestry)
        {
            Assert.Equal(1, await ResetScalarAsync("SELECT COUNT(*) FROM workspace_file_chunks WHERE ChunkId='reset-source'"));

            Assert.Equal(1, await ResetScalarAsync("SELECT COUNT(*) FROM tapestry_leaf_hashes WHERE SourceId='reset-source'"));

            Assert.Equal(7, await ResetScalarAsync("SELECT CorpusRevision FROM tapestry_scope_revisions WHERE ScopeKind='Workspace' AND ScopeId='/reset-graph'"));

            Assert.Equal(5, await ResetScalarAsync("SELECT InvalidationRevision FROM tapestry_scope_revisions WHERE ScopeKind='Workspace' AND ScopeId='/reset-graph'"));
        }
    }

    [Fact]
    public async Task Failed_graph_reset_rolls_back_embeddings_and_preserves_build_backoff()
    {
        await SeedResetGraphsAsync();

        await ResetExecuteAsync("CREATE TRIGGER reject_graph_reset BEFORE DELETE ON tapestry_generations BEGIN SELECT RAISE(ABORT,'injected reset failure'); END;");

        TapestryBuildBackoff backoff = new();

        TapestryScope scope = new(TapestryScopeKind.Workspace, "/reset-graph");

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = backoff.RecordFailure(scope, "failed", now, TimeSpan.FromHours(1));

        EmbeddingsResetService service = new(_db!, Services(backoff: backoff));

        await Assert.ThrowsAnyAsync<Exception>(() => service.ResetAsync(EmbeddingsResetScope.Tapestry, CancellationToken.None));

        Assert.Equal(2, await ResetScalarAsync("SELECT COUNT(*) FROM tapestry_generations"));

        Assert.Equal(6, await ResetScalarAsync("SELECT COUNT(*) FROM tapestry_node_embeddings"));

        Assert.Equal(4, await ResetScalarAsync("SELECT COUNT(*) FROM tapestry_edges"));

        Assert.True(backoff.IsBackingOff(scope, "failed", now));
    }

    [Fact]
    public async Task Canceled_graph_reset_preserves_every_graph_and_source_row()
    {
        await SeedResetGraphsAsync();

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _resetService!.ResetAsync(EmbeddingsResetScope.Tapestry, cancellation.Token));

        Assert.Equal(2, await ResetScalarAsync("SELECT COUNT(*) FROM tapestry_generations"));

        Assert.Equal(4, await ResetScalarAsync("SELECT COUNT(*) FROM tapestry_edges"));

        Assert.Equal(6, await ResetScalarAsync("SELECT COUNT(*) FROM tapestry_node_embeddings"));

        Assert.Equal(1, await ResetScalarAsync("SELECT COUNT(*) FROM workspace_file_chunks WHERE ChunkId='reset-source'"));
    }

    private async Task SeedResetGraphsAsync()
    {
        await ResetExecuteAsync("""
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            VALUES('reset-source','/reset-graph','source.cs',0,'private lifecycle source',0,24,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
            INSERT INTO tapestry_leaf_hashes(SourceKind,SourceId,ContentSha256,IsBlank) VALUES('WorkspaceFileChunk','reset-source','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',0);
            UPDATE tapestry_scope_revisions SET CorpusRevision=7,InvalidationRevision=5 WHERE ScopeKind='Workspace' AND ScopeId='/reset-graph';
            """);

        foreach (string status in new[] { "Complete", "Building" })
        {
            string id = status.ToLowerInvariant();

            await ResetExecuteAsync($"""
                INSERT INTO tapestry_generations(GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt)
                VALUES('{id}','Workspace','/reset-graph','Building','legacy','settings','recipe',1,'corpus','2026-01-01T00:00:00.0000000Z');
                INSERT INTO tapestry_nodes(NodeId,GenerationId,ScopeKind,ScopeId,Layer,NodeKind,ParentScopeKey,SourceLabel,Content,ContentHash,EmbeddingDimension,CreatedAt)
                VALUES('{id}-a','{id}','Workspace','/reset-graph',1,'Summary','{id}#root','summary','private summary','hash',1,'2026-01-01T00:00:00.0000000Z'),
                      ('{id}-b','{id}','Workspace','/reset-graph',1,'Summary','{id}#root','summary','private summary','hash',1,'2026-01-01T00:00:00.0000000Z'),
                      ('{id}-leaf','{id}','Workspace','/reset-graph',0,'Leaf','{id}#root','source',NULL,'hash',1,'2026-01-01T00:00:00.0000000Z');
                INSERT INTO tapestry_edges(GenerationId,ParentNodeId,ChildNodeId,MembershipReason)
                VALUES('{id}','{id}-a','{id}-leaf','Legacy'),('{id}','{id}-b','{id}-leaf','Legacy');
                INSERT INTO tapestry_node_embeddings(NodeId,Embedding,Dim) VALUES('{id}-a',X'0000803F',1),('{id}-b',X'0000803F',1),('{id}-leaf',X'0000803F',1);
                UPDATE tapestry_nodes SET SourceKind='WorkspaceFileChunk',SourceId='reset-source' WHERE NodeId='{id}-leaf';
                UPDATE tapestry_generations SET Status='{status}' WHERE GenerationId='{id}';
                """);

        }
    }

    private async Task ResetExecuteAsync(string sql)
    {
        await _db!.Database.OpenConnectionAsync();

        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ResetScalarAsync(string sql)
    {
        await _db!.Database.OpenConnectionAsync();

        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
