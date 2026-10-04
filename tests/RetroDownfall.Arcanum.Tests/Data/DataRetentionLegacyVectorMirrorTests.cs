using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Every retention path that reads or deletes from a vector mirror classifies the mirror from the
/// catalog first: a plain table is counted and emptied, and a legacy virtual table is skipped.
/// </summary>
/// <remarks>
/// A legacy <c>vec0</c> mirror also passes an existence probe, and the shipping runtime has no module to
/// open it with, so a statement against it would fail the whole operation. An FTS5 virtual table stands
/// in for it, because it records the same <c>CREATE VIRTUAL TABLE</c> text in <c>sqlite_master</c>, which
/// is all that classifying a mirror reads. The stand-in can be read and deleted from, so a row it still
/// holds afterwards is what shows no statement reached it.
/// </remarks>
public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task GetStatusAsync_CountsAPlainVectorMirrorAndSkipsALegacyVirtualOne()
    {
        RequireSqlCipher();

        (_, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        await RecreateLegacyVectorTableAsync("entry_embeddings_vec", "EntryId");

        await InsertVectorAsync("entry_embeddings_vec", "EntryId", entryId.ToString());

        await RecreateVectorTableAsync("workspace_file_embeddings_vec", "ChunkId");

        await SeedWorkspaceRowsAsync("legacy-status-workspace");

        DataRetentionStatus status = await CreateService().GetStatusAsync(CancellationToken.None);

        // The embedding row alone: the legacy mirror's row is not counted.
        Assert.Equal(
            1,
            Assert.Single(status.Items, item => item.DataClass == RetentionDataClass.SessionEntryEmbeddings).Rows);

        // The embedding row and the plain mirror's row.
        Assert.Equal(
            2,
            Assert.Single(status.Items, item => item.DataClass == RetentionDataClass.WorkspaceEmbeddings).Rows);
    }

    [SkippableFact]
    public async Task PlanAndApplyAsync_Prune_SkipsALegacyVirtualVectorMirrorAndEmptiesThePlainOnes()
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await RecreateLegacyVectorTableAsync("entry_embeddings_vec", "EntryId");

        await RecreateVectorTableAsync("workspace_file_embeddings_vec", "ChunkId");

        await RecreateVectorTableAsync("saga_memory_embeddings_vec", "MemoryId");

        await SeedPrunableDerivedRowsAsync(sessionId, entryId);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

        settings.Retention.WorkspaceIndexes = EnabledRule();

        settings.Retention.SagaMemories = EnabledRule();

        settings.Retention.LexiconEntries = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        // Nine with a plain mirror under every table: the legacy one's row is not counted.
        Assert.Equal(8, plan.DerivedRecords);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.Reconciled);

        Assert.Equal(plan.DerivedRecords, result.Value.DerivedRecordsDeleted);

        Assert.Equal(0, await CountAllAsync("entry_embeddings"));

        Assert.Equal(1, await CountAllAsync("entry_embeddings_vec"));

        Assert.Equal(0, await CountAllAsync("workspace_file_embeddings"));

        Assert.Equal(0, await CountAllAsync("workspace_file_embeddings_vec"));

        Assert.Equal(0, await CountAllAsync("saga_memory_embeddings"));

        Assert.Equal(0, await CountAllAsync("saga_memory_embeddings_vec"));
    }

    [SkippableTheory]
    [InlineData(MemoryResetScope.Entry)]
    [InlineData(MemoryResetScope.Attachments)]
    [InlineData(MemoryResetScope.Workspace)]
    [InlineData(MemoryResetScope.Saga)]
    public async Task ApplyAsync_ResetMemory_SkipsALegacyVirtualVectorMirrorAndStillClearsTheStore(
        MemoryResetScope scope)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        (string mirror, string key, string embeddings) = scope switch
        {
            MemoryResetScope.Entry => ("entry_embeddings_vec", "EntryId", "entry_embeddings"),
            MemoryResetScope.Attachments =>
                ("session_attachment_embeddings_vec", "ChunkId", "session_attachment_embeddings"),
            MemoryResetScope.Workspace =>
                ("workspace_file_embeddings_vec", "ChunkId", "workspace_file_embeddings"),
            _ => ("saga_memory_embeddings_vec", "MemoryId", "saga_memory_embeddings"),
        };

        await RecreateLegacyVectorTableAsync(mirror, key);

        switch (scope)
        {
            case MemoryResetScope.Entry:
                await SeedEntryEmbeddingAsync(entryId);

                await InsertVectorAsync(mirror, key, entryId.ToString());

                break;

            case MemoryResetScope.Attachments:
                SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

                await InsertVectorAsync(mirror, key, attachment.ChunkId);

                break;

            case MemoryResetScope.Workspace:
                await SeedWorkspaceRowsAsync("legacy-reset-workspace");

                break;

            default:
                await SeedSagaRowsAsync("legacy-reset-saga", sessionId, Guid.NewGuid());

                break;
        }

        Assert.Equal(1, await CountAllAsync(mirror));

        Assert.Equal(1, await CountAllAsync(embeddings));

        await ApplyUntargetedResetAsync(scope);

        Assert.Equal(0, await CountAllAsync(embeddings));

        Assert.Equal(1, await CountAllAsync(mirror));
    }

    [SkippableFact]
    public async Task ApplyAsync_DeleteSession_SkipsALegacyVirtualVectorMirrorAndEmptiesThePlainOne()
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await RecreateLegacyVectorTableAsync("entry_embeddings_vec", "EntryId");

        await RecreateVectorTableAsync("session_attachment_embeddings_vec", "ChunkId");

        await InsertVectorAsync("entry_embeddings_vec", "EntryId", Canonical(entryId));

        await InsertVectorAsync("session_attachment_embeddings_vec", "ChunkId", attachment.ChunkId);

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(DataRetentionOperation.DeleteSession, sessionId);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.Reconciled);

        Assert.Equal(plan.DerivedRecords, result.Value.DerivedRecordsDeleted);

        Assert.Equal(0, await CountAllAsync("entry_embeddings"));

        Assert.Equal(1, await CountAllAsync("entry_embeddings_vec"));

        Assert.Equal(0, await CountAllAsync("session_attachment_embeddings"));

        Assert.Equal(0, await CountAllAsync("session_attachment_embeddings_vec"));
    }

    [SkippableFact]
    public async Task ApplyAsync_DeleteAttachment_SkipsALegacyVirtualVectorMirror()
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await RecreateLegacyVectorTableAsync("session_attachment_embeddings_vec", "ChunkId");

        await InsertVectorAsync("session_attachment_embeddings_vec", "ChunkId", attachment.ChunkId);

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(DataRetentionOperation.DeleteAttachment, attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.Reconciled);

        Assert.Equal(plan.DerivedRecords, result.Value.DerivedRecordsDeleted);

        Assert.Equal(0, await CountAllAsync("session_attachment_chunks"));

        Assert.Equal(0, await CountAllAsync("session_attachment_embeddings"));

        Assert.Equal(1, await CountAllAsync("session_attachment_embeddings_vec"));
    }

    [SkippableFact]
    public async Task ApplyAsync_FactoryReset_SkipsALegacyVirtualVectorMirrorAndEmptiesThePlainOnes()
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await RecreateLegacyVectorTableAsync("entry_embeddings_vec", "EntryId");

        await RecreateVectorTableAsync("session_attachment_embeddings_vec", "ChunkId");

        await RecreateVectorTableAsync("workspace_file_embeddings_vec", "ChunkId");

        await RecreateLegacyVectorTableAsync("tapestry_node_embeddings_vec", "NodeId");

        await InsertVectorAsync("entry_embeddings_vec", "EntryId", Canonical(entryId));

        await InsertVectorAsync("session_attachment_embeddings_vec", "ChunkId", attachment.ChunkId);

        await SeedWorkspaceRowsAsync("legacy-factory-workspace");

        await InsertVectorAsync("tapestry_node_embeddings_vec", "NodeId", "legacy-factory-node");

        DataRetentionService service = CreateService();

        (LongRunningOperationReconciliationSummary recovery, _) =
            await ReconcileFactoryResetV0Async(service, "legacy-vector-mirror-factory-test");

        Assert.Equal(1, recovery.Completed);

        Assert.Equal(0, recovery.RequiresAttention);

        Assert.Equal(0, await CountAllAsync("entry_embeddings"));

        Assert.Equal(1, await CountAllAsync("entry_embeddings_vec"));

        Assert.Equal(0, await CountAllAsync("session_attachment_embeddings_vec"));

        Assert.Equal(0, await CountAllAsync("workspace_file_embeddings_vec"));

        Assert.Equal(1, await CountAllAsync("tapestry_node_embeddings_vec"));
    }

    /// <summary>Stands in for a <c>vec0</c> mirror an earlier build left: a virtual table this runtime has no module for.</summary>
    private async Task RecreateLegacyVectorTableAsync(string table, string keyColumn)
    {
        await ExecuteAsync($"DROP TABLE IF EXISTS \"{table}\"");

        await ExecuteAsync($"CREATE VIRTUAL TABLE \"{table}\" USING fts5(\"{keyColumn}\", \"Embedding\")");
    }
}
