using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitRetention_ErasesAllAffectedGraphsInTheSourceTransaction(bool deleteSession, bool rollback)
    {
        (Guid session, Guid entry) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(session, entry);

        (Guid other, Guid otherEntry) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("INSERT INTO tapestry_leaf_hashes(SourceKind,SourceId,ContentSha256,IsBlank) VALUES('Entry',@entry,@hash,0),('Entry',@other,@hash,0),('SessionAttachmentChunk',@chunk,@hash,0)",
            ("@entry", Canonical(entry)), ("@other", Canonical(otherEntry)), ("@chunk", attachment.ChunkId), ("@hash", new string('a', 64)));

        await ExecuteAsync("CREATE TABLE IF NOT EXISTS tapestry_node_embeddings_vec(NodeId TEXT PRIMARY KEY, Embedding BLOB NOT NULL)");

        foreach (string kind in new[] { "Session", "SessionAttachment" })
        {
            await SeedRetentionGraphAsync(session, kind, "Complete");

            await SeedRetentionGraphAsync(session, kind, "Building");
        }

        await SeedRetentionGraphAsync(other, "Session", "Complete");

        if (rollback)
        {
            await ExecuteAsync(deleteSession
                ? "CREATE TRIGGER fail_erasure BEFORE DELETE ON Entries BEGIN SELECT RAISE(ABORT,'injected erasure failure'); END"
                : "CREATE TRIGGER fail_erasure BEFORE DELETE ON session_attachment_chunks BEGIN SELECT RAISE(ABORT,'injected erasure failure'); END");
        }

        DataRetentionService service = CreateService();

        DataRetentionRequest request = new(deleteSession ? DataRetentionOperation.DeleteSession : DataRetentionOperation.DeleteAttachment,
            TargetId: deleteSession ? session : attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        var applied = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);

        Assert.Equal(!rollback, applied.IsSuccess);

        long remaining = rollback ? 5 : deleteSession ? 1 : 3;

        Assert.Equal(remaining, await CountAllAsync("tapestry_generations"));

        Assert.Equal(remaining * 3, await CountAllAsync("tapestry_nodes"));

        Assert.Equal(remaining * 2, await CountAllAsync("tapestry_edges"));

        Assert.Equal(remaining * 3, await CountAllAsync("tapestry_node_embeddings"));

        Assert.Equal(remaining * 3, await CountAllAsync("tapestry_node_embeddings_vec"));

        Assert.Equal(rollback ? 3 : deleteSession ? 1 : 2, await CountAllAsync("tapestry_leaf_hashes"));

        Assert.Equal(deleteSession ? 48 : 24, plan.Items.Where(item => item.DataClass == RetentionDataClass.Tapestry).Sum(item => item.DerivedRecords));
    }

    private async Task SeedRetentionGraphAsync(Guid session, string kind, string status)
    {
        string id = Guid.NewGuid().ToString("N");

        await ExecuteAsync($"""
            INSERT INTO tapestry_generations(GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt)
            VALUES('{id}','{kind}','{session:N}','Building','legacy','settings','recipe',1,'corpus','2026-01-01T00:00:00.0000000Z');
            INSERT INTO tapestry_nodes(NodeId,GenerationId,ScopeKind,ScopeId,Layer,NodeKind,ParentScopeKey,SourceLabel,Content,ContentHash,EmbeddingDimension,CreatedAt)
            VALUES('{id}p','{id}','{kind}','{session:N}',1,'Summary','{id}#root','summary','sensitive derived content','hash',1,'2026-01-01T00:00:00.0000000Z'),
                  ('{id}q','{id}','{kind}','{session:N}',1,'Summary','{id}#root','summary','another private summary','hash',1,'2026-01-01T00:00:00.0000000Z'),
                  ('{id}c','{id}','{kind}','{session:N}',0,'Leaf','{id}#root','source',NULL,'hash',1,'2026-01-01T00:00:00.0000000Z');
            INSERT INTO tapestry_edges(GenerationId,ParentNodeId,ChildNodeId,MembershipReason) VALUES('{id}','{id}p','{id}c','Legacy'),('{id}','{id}q','{id}c','Legacy');
            INSERT INTO tapestry_node_embeddings(NodeId,Embedding,Dim) VALUES('{id}p',X'0000803F',1),('{id}q',X'0000803F',1),('{id}c',X'0000803F',1);
            INSERT INTO tapestry_node_embeddings_vec(NodeId,Embedding) VALUES('{id}p',X'0000803F'),('{id}q',X'0000803F'),('{id}c',X'0000803F');
            UPDATE tapestry_nodes SET SourceKind='{(kind == "Session" ? "Entry" : "SessionAttachmentChunk")}',SourceId=(
                SELECT {(kind == "Session" ? "Id" : "ChunkId")} FROM {(kind == "Session" ? "Entries" : "session_attachment_chunks")}
                WHERE lower(replace(SessionId,'-',''))='{session:N}' LIMIT 1) WHERE NodeId='{id}c';
            UPDATE tapestry_generations SET Status='{status}' WHERE GenerationId='{id}';
            """);
    }

    [Fact]
    public async Task FactoryReset_DeletesRevisionBookkeepingAfterEverySourceDeletion()
    {
        (Guid session, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("CREATE TABLE tapestry_node_embeddings_vec(NodeId TEXT PRIMARY KEY,Embedding BLOB NOT NULL)");

        await SeedRetentionGraphAsync(session, "Session", "Complete");

        await SeedRetentionGraphAsync(session, "Session", "Building");

        await ExecuteAsync("INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt) VALUES('reset-source','/reset','a.cs',0,'body',0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')");

        Assert.True(await CountAllAsync("tapestry_scope_revisions") >= 2);

        DataRetentionService service = CreateService();

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.FactoryReset), CancellationToken.None);

        long exactGraphRows = 0;

        foreach (string table in new[] { "tapestry_generations", "tapestry_nodes", "tapestry_edges", "tapestry_node_embeddings", "tapestry_node_embeddings_vec", "tapestry_scope_revisions" })
        {
            exactGraphRows += await CountAllAsync(table);
        }

        Assert.Equal(exactGraphRows, plan.Items.Where(item => item.DataClass == RetentionDataClass.Tapestry).Sum(item => item.DerivedRecords));

        (LongRunningOperationReconciliationSummary summary, LongRunningOperation operation) = await ReconcileFactoryResetV0Async(service, "tapestry-revision-reset");

        Assert.Equal(0, await CountAllAsync("Entries"));

        Assert.Equal(0, await CountAllAsync("workspace_file_chunks"));

        Assert.Equal(0, await CountAllAsync("session_attachment_chunks"));

        Assert.Equal(0, await CountAllAsync("tapestry_scope_revisions"));

        foreach (string table in new[] { "tapestry_generations", "tapestry_nodes", "tapestry_edges", "tapestry_node_embeddings", "tapestry_node_embeddings_vec" })
        {
            Assert.Equal(0, await CountAllAsync(table));
        }

        Assert.True(summary.Completed == 1, operation.PublicSummary + " | " + operation.TerminalErrorCode);

        Assert.Equal(0, summary.RequiresAttention);
    }

    [Fact]
    public async Task Workspace_reset_fingerprint_detects_same_count_shared_edge_replacement()
    {
        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        await ExecuteAsync("""
            INSERT INTO tapestry_generations(GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt)
            VALUES('workspace-dag','Workspace',@root,'Building','legacy','settings','recipe',1,'corpus','2026-01-01T00:00:00.0000000Z');
            INSERT INTO tapestry_nodes(NodeId,GenerationId,ScopeKind,ScopeId,Layer,NodeKind,ParentScopeKey,SourceLabel,ContentHash,EmbeddingDimension,CreatedAt)
            VALUES('workspace-a','workspace-dag','Workspace',@root,1,'Summary','workspace-dag#root','summary','hash',1,'2026-01-01T00:00:00.0000000Z'),
                  ('workspace-b','workspace-dag','Workspace',@root,1,'Summary','workspace-dag#root','summary','hash',1,'2026-01-01T00:00:00.0000000Z'),
                  ('workspace-c','workspace-dag','Workspace',@root,1,'Summary','workspace-dag#root','summary','hash',1,'2026-01-01T00:00:00.0000000Z'),
                  ('workspace-leaf','workspace-dag','Workspace',@root,0,'Leaf','workspace-dag#root','source','hash',1,'2026-01-01T00:00:00.0000000Z');
            INSERT INTO tapestry_edges(GenerationId,ParentNodeId,ChildNodeId,MembershipReason)
            VALUES('workspace-dag','workspace-a','workspace-leaf','Legacy'),('workspace-dag','workspace-b','workspace-leaf','Legacy');
            """, ("@root", graph.TargetRoot));

        DataRetentionService service = CreateService();

        DataRetentionRequest request = WorkspaceResetRequest(graph);

        DataRetentionPlan before = await service.PlanAsync(request, CancellationToken.None);

        await ExecuteAsync("UPDATE tapestry_edges SET ParentNodeId='workspace-c' WHERE GenerationId='workspace-dag' AND ParentNodeId='workspace-b'");

        DataRetentionPlan after = await service.PlanAsync(request, CancellationToken.None);

        Assert.Equal(before.DerivedRecords, after.DerivedRecords);

        Assert.NotEqual(before.PlanId, after.PlanId);

        var refused = await service.ApplyAsync(new(request, before.PlanId), CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(2, await CountAllAsync("tapestry_edges"));

        Assert.Equal(1, await CountAsync("workspace_file_chunks", "ChunkId", graph.TargetChunkId));
    }
}
