using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreProtectedStatePurgeTests
{
    [Fact]
    public async Task Staged_purge_removes_every_affected_shared_leaf_generation_and_its_plain_mirror_before_activation()
    {
        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedAssistantEntryWithTapestryAsync(labelSessionId: LedgerSessionId);

        await SeedTapestryGenerationAsync("Session", SessionId, "building-generation", status: "Building");

        await _staged.InstallCoreObjectsAsync(
            ["tapestry_edges", "tapestry_scope_revisions", "tapestry_leaf_hashes", "Entries_tapestry_revision_delete", "Entries_drop_tapestry_leaf_hash_delete"],
            CancellationToken.None);

        await _staged.ExecuteAsync($"""
            CREATE TABLE tapestry_node_embeddings_vec(NodeId TEXT PRIMARY KEY,Embedding BLOB NOT NULL);
            INSERT INTO tapestry_scope_revisions(ScopeKind,ScopeId,CorpusRevision,InvalidationRevision) VALUES('Session','{SessionId}',10,9);
            INSERT INTO tapestry_leaf_hashes(SourceKind,SourceId,ContentSha256,IsBlank) VALUES('Entry','{EntryArtifactId}','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',0);
            UPDATE tapestry_generations SET Status='Building' WHERE GenerationId='building-generation';
            """, CancellationToken.None);

        foreach (string generation in new[] { "session-generation", "building-generation" })
        {
            await _staged.ExecuteAsync($"""
                INSERT INTO tapestry_nodes(NodeId,GenerationId,ScopeKind,ScopeId,Layer,NodeKind,ParentScopeKey,SourceKind,SourceId,SourceLabel,Content,ContentHash,EmbeddingDimension,CreatedAt)
                VALUES('{generation}-other','{generation}','Session','{SessionId}',1,'Summary','{generation}#root',NULL,NULL,'summary','private derived summary','hash',1,'{Iso()}'),
                      ('{generation}-leaf','{generation}','Session','{SessionId}',0,'Leaf','{generation}#root','Entry','{EntryArtifactId}','source',NULL,'hash',1,'{Iso()}');
                INSERT INTO tapestry_edges(GenerationId,ParentNodeId,ChildNodeId,MembershipReason)
                VALUES('{generation}','{generation}-node','{generation}-leaf','Legacy'),('{generation}','{generation}-other','{generation}-leaf','Legacy');
                INSERT INTO tapestry_node_embeddings(NodeId,Embedding,Dim) VALUES('{generation}-other',X'0000803F',1),('{generation}-leaf',X'0000803F',1);
                """, CancellationToken.None);
        }

        await _staged.ExecuteAsync("INSERT INTO tapestry_node_embeddings_vec SELECT NodeId,Embedding FROM tapestry_node_embeddings;", CancellationToken.None);

        await _staged.InstallCoreObjectsAsync(
            ["tapestry_nodes_guard_delete", "tapestry_edges_guard_delete", "tapestry_node_embeddings_guard_delete"],
            CancellationToken.None);

        var receipt = await ReconcileAsync(purgeProtectedState: true);

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(0, await CountAsync("Entries"));

        Assert.Equal(0, await CountAsync("tapestry_edges"));

        Assert.Equal(0, await CountAsync("tapestry_leaf_hashes"));

        Assert.Equal(2, await CountAsync("tapestry_generations"));

        Assert.Equal(2, await CountAsync("tapestry_nodes"));

        Assert.Equal(2, await CountAsync("tapestry_node_embeddings"));

        Assert.Equal(2, await CountAsync("tapestry_node_embeddings_vec"));

        Assert.Equal(1, await CountAsync("tapestry_generations WHERE GenerationId='attachment-generation'"));

        Assert.Equal(1, await CountAsync("tapestry_generations WHERE GenerationId='other-session-generation'"));

        Assert.Equal(1, await CountAsync("tapestry_scope_revisions WHERE CorpusRevision=11 AND InvalidationRevision=10"));

        Assert.True((await ReconcileAsync(purgeProtectedState: true)).IsSuccess);

        Assert.Equal(2, await CountAsync("tapestry_generations"));

        Assert.Equal(0, await CountAsync("tapestry_edges"));
    }
}
