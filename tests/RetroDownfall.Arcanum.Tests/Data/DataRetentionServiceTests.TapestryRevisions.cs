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

        Assert.Equal(remaining * 2, await CountAllAsync("tapestry_nodes"));

        Assert.Equal(remaining, await CountAllAsync("tapestry_edges"));

        Assert.Equal(remaining * 2, await CountAllAsync("tapestry_node_embeddings"));

        Assert.Equal(remaining * 2, await CountAllAsync("tapestry_node_embeddings_vec"));

        Assert.Equal(rollback ? 3 : deleteSession ? 1 : 2, await CountAllAsync("tapestry_leaf_hashes"));

        Assert.Equal(deleteSession ? 32 : 16, plan.Items.Where(item => item.DataClass == RetentionDataClass.Tapestry).Sum(item => item.DerivedRecords));
    }

    private async Task SeedRetentionGraphAsync(Guid session, string kind, string status)
    {
        string id = Guid.NewGuid().ToString("N");

        await ExecuteAsync($"""
            INSERT INTO tapestry_generations(GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt)
            VALUES('{id}','{kind}','{session:N}','Building','legacy','settings','recipe',1,'corpus','2026-01-01T00:00:00.0000000Z');
            INSERT INTO tapestry_nodes(NodeId,GenerationId,ScopeKind,ScopeId,Layer,NodeKind,ParentScopeKey,SourceLabel,Content,ContentHash,EmbeddingDimension,CreatedAt)
            VALUES('{id}p','{id}','{kind}','{session:N}',1,'Summary','{id}#root','summary','sensitive derived content','hash',1,'2026-01-01T00:00:00.0000000Z'),
                  ('{id}c','{id}','{kind}','{session:N}',0,'Leaf','{id}#root','source',NULL,'hash',1,'2026-01-01T00:00:00.0000000Z');
            INSERT INTO tapestry_edges(GenerationId,ParentNodeId,ChildNodeId,MembershipReason) VALUES('{id}','{id}p','{id}c','Legacy');
            INSERT INTO tapestry_node_embeddings(NodeId,Embedding,Dim) VALUES('{id}p',X'0000803F',1),('{id}c',X'0000803F',1);
            INSERT INTO tapestry_node_embeddings_vec(NodeId,Embedding) VALUES('{id}p',X'0000803F'),('{id}c',X'0000803F');
            UPDATE tapestry_generations SET Status='{status}' WHERE GenerationId='{id}';
            """);
    }

    [Fact]
    public async Task FactoryReset_DeletesRevisionBookkeepingAfterEverySourceDeletion()
    {
        _ = await SeedSessionAsync(pinned: false);

        await ExecuteAsync("INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt) VALUES('reset-source','/reset','a.cs',0,'body',0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')");

        Assert.True(await CountAllAsync("tapestry_scope_revisions") >= 2);

        DataRetentionService service = CreateService();

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.FactoryReset), CancellationToken.None);

        Assert.True(plan.DerivedRecords >= 2);

        (LongRunningOperationReconciliationSummary summary, LongRunningOperation operation) = await ReconcileFactoryResetV0Async(service, "tapestry-revision-reset");

        Assert.Equal(0, await CountAllAsync("Entries"));

        Assert.Equal(0, await CountAllAsync("workspace_file_chunks"));

        Assert.Equal(0, await CountAllAsync("session_attachment_chunks"));

        Assert.Equal(0, await CountAllAsync("tapestry_scope_revisions"));

        Assert.True(summary.Completed == 1, operation.PublicSummary + " | " + operation.TerminalErrorCode);

        Assert.Equal(0, summary.RequiresAttention);
    }
}
