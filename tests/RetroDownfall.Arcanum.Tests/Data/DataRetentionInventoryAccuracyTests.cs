using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{

    [SkippableFact]

    public async Task GetStatusAsync_IncludesOwnedDerivedRecordsAndActualFileBytes()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await RecreateVectorTableAsync("entry_embeddings_vec", "EntryId");

        await RecreateVectorTableAsync("session_attachment_embeddings_vec", "ChunkId");

        await RecreateVectorTableAsync("workspace_file_embeddings_vec", "ChunkId");

        await RecreateVectorTableAsync("saga_memory_embeddings_vec", "MemoryId");

        await SeedInventoryDerivedRowsAsync(
            sessionId,
            entryId,
            attachment.AttachmentId,
            attachment.ChunkId);

        Guid uploadedFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(uploadedFileId, 999);

        byte[] uploadedBytes = [1, 2, 3, 4, 5, 6, 7];

        await File.WriteAllBytesAsync(
            Path.Combine(_filesRoot, uploadedFileId.ToString("N")),
            uploadedBytes);

        IDataRetentionService service = CreateService();

        DataRetentionStatus status = await service.GetStatusAsync(
            CancellationToken.None);

        AssertStatusRows(
            status,
            RetentionDataClass.Entries,
            await SumTableCountsAsync(
                "Entries",
                "Entries_fts",
                "attachment_memory_consultations"),
            "Entries_fts");

        AssertStatusRows(
            status,
            RetentionDataClass.AttachmentEmbeddings,
            await SumTableCountsAsync(
                "session_attachment_embeddings",
                "session_attachment_embeddings_vec",
                "session_attachment_index_state"),
            "session_attachment_index_state");

        AssertStatusRows(
            status,
            RetentionDataClass.WorkspaceEmbeddings,
            await SumTableCountsAsync(
                "workspace_file_embeddings",
                "workspace_file_embeddings_vec"),
            "workspace_file_embeddings_vec");

        AssertStatusRows(
            status,
            RetentionDataClass.SessionEntryEmbeddings,
            await SumTableCountsAsync(
                "entry_embeddings",
                "entry_embeddings_vec"),
            "entry_embeddings_vec");

        AssertStatusRows(
            status,
            RetentionDataClass.SagaMemories,
            await SumTableCountsAsync(
                "saga_memories",
                "saga_memory_embeddings",
                "saga_memory_embeddings_vec",
                "saga_memory_attachment_provenance",
                "saga_extraction_watermarks"),
            "saga_extraction_watermarks");

        AssertStatusRows(
            status,
            RetentionDataClass.LexiconEntries,
            await SumTableCountsAsync(
                "lexicon_entries",
                "lexicon_fts",
                "lexicon_fact_attachment_provenance"),
            "lexicon_fts");

        DataRetentionStatusItem attachmentBytes = Assert.Single(
            status.Items,
            static item => item.DataClass == RetentionDataClass.AttachmentBytes);

        Assert.Equal(1, attachmentBytes.Files);

        Assert.Equal(attachment.Bytes.LongLength, attachmentBytes.EstimatedBytes);

        DataRetentionStatusItem uploadedFiles = Assert.Single(
            status.Items,
            static item => item.DataClass == RetentionDataClass.UploadedFiles);

        Assert.Equal(1, uploadedFiles.Files);

        Assert.Equal(uploadedBytes.LongLength, uploadedFiles.EstimatedBytes);

        Assert.Equal(
            status.Items
                .Where(static item => item.DataClass is not (
                    RetentionDataClass.BatchInputFiles
                    or RetentionDataClass.BatchOutputFiles
                    or RetentionDataClass.BatchErrorFiles))
                .Sum(static item => item.Rows),
            status.Rows);

    }

    [SkippableFact]

    public async Task PlanAndApplyAsync_Prune_DerivedCountsMatchEveryDeletedCompanionRow()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await RecreateVectorTableAsync("entry_embeddings_vec", "EntryId");

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

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal(9, plan.DerivedRecords);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(plan.DerivedRecords, result.Value.DerivedRecordsDeleted);

        Assert.Equal(0, await CountAllAsync("entry_embeddings"));

        Assert.Equal(0, await CountAllAsync("entry_embeddings_vec"));

        Assert.Equal(0, await CountAllAsync("workspace_file_chunks"));

        Assert.Equal(0, await CountAllAsync("workspace_file_embeddings"));

        Assert.Equal(0, await CountAllAsync("workspace_file_embeddings_vec"));

        Assert.Equal(0, await CountAllAsync("saga_memories"));

        Assert.Equal(0, await CountAllAsync("saga_memory_embeddings"));

        Assert.Equal(0, await CountAllAsync("saga_memory_embeddings_vec"));

        Assert.Equal(0, await CountAllAsync("saga_memory_attachment_provenance"));

        Assert.Equal(0, await CountAllAsync("lexicon_entries"));

        Assert.Equal(0, await CountAllAsync("lexicon_fts"));

        Assert.Equal(0, await CountAllAsync("lexicon_fact_attachment_provenance"));

    }

    [SkippableFact]

    public async Task PlanAndApplyAsync_PruneBatch_DoesNotClaimReferencedFilesAsDeletedRecords()
    {

        RequireSqlCipher();

        Guid inputFileId = Guid.NewGuid();

        Guid outputFileId = Guid.NewGuid();

        Guid errorFileId = Guid.NewGuid();

        Guid batchId = Guid.NewGuid();

        await SeedUploadedFileAsync(inputFileId, 1);

        await SeedUploadedFileAsync(outputFileId, 2);

        await SeedUploadedFileAsync(errorFileId, 3);

        await ExecuteAsync(
            """
            INSERT INTO "Batches"
                ("Id", "InputFileId", "OutputFileId", "ErrorFileId", "Endpoint", "Status",
                 "CreatedAt", "CompletedAt")
            VALUES
                (@id, @input, @output, @error, '/v1/chat/completions', @status, @at, @at)
            """,
            ("@id", batchId.ToString()),
            ("@input", inputFileId.ToString()),
            ("@output", outputFileId.ToString()),
            ("@error", errorFileId.ToString()),
            ("@status", BatchStatuses.Completed),
            ("@at", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.CompletedBatches = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal(0, plan.DerivedRecords);

        Assert.DoesNotContain(
            plan.Items,
            static item => item.DataClass is RetentionDataClass.BatchInputFiles
                or RetentionDataClass.BatchOutputFiles
                or RetentionDataClass.BatchErrorFiles);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(plan.DerivedRecords, result.Value.DerivedRecordsDeleted);

        Assert.Equal(3, await CountAllAsync("UploadedFiles"));

    }

    [SkippableFact]

    public async Task PlanAsync_PruneUploadedFiles_UsesActualExistingFileCountsAndBytes()
    {

        RequireSqlCipher();

        Guid existingFileId = Guid.NewGuid();

        Guid missingFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(existingFileId, 900);

        await SeedUploadedFileAsync(missingFileId, 800);

        byte[] actualBytes = [1, 2, 3, 4, 5];

        await File.WriteAllBytesAsync(
            Path.Combine(_filesRoot, existingFileId.ToString("N")),
            actualBytes);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            CancellationToken.None);

        DataRetentionPlanItem uploaded = Assert.Single(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.UploadedFiles);

        Assert.Equal(2, uploaded.Rows);

        Assert.Equal(1, uploaded.Files);

        Assert.Equal(actualBytes.LongLength, uploaded.EstimatedBytes);

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenCandidateBecomesProtected_ContinuesWithoutAdvancingPastSkippedCandidate()
    {

        RequireSqlCipher();

        Guid fileId = Guid.Parse("10000000-0000-0000-0000-000000000001");

        Guid batchId = Guid.Parse("20000000-0000-0000-0000-000000000001");

        Guid laterFileId = Guid.Parse("10000000-0000-0000-0000-000000000002");

        Guid laterBatchId = Guid.Parse("20000000-0000-0000-0000-000000000002");

        await SeedUploadedFileAsync(fileId, 0);

        await SeedUploadedFileAsync(laterFileId, 0);

        await ExecuteAsync(
            """
            INSERT INTO "Batches"
                ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt")
            VALUES
                (@id, @fileId, '/v1/chat/completions', @status, @at, @at)
            """,
            ("@id", batchId.ToString()),
            ("@fileId", fileId.ToString()),
            ("@status", BatchStatuses.Completed),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO "Batches"
                ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt")
            VALUES
                (@id, @fileId, '/v1/chat/completions', @status, @at, @at)
            """,
            ("@id", laterBatchId.ToString()),
            ("@fileId", laterFileId.ToString()),
            ("@status", BatchStatuses.Completed),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            $"""
            CREATE TRIGGER protect_terminal_batch_during_retention
            BEFORE DELETE ON "Batches"
            WHEN lower(replace(OLD."Id", '-', '')) = '{batchId:N}'
            BEGIN
                UPDATE "Batches"
                SET "Status" = '{BatchStatuses.InProgress}'
                WHERE lower(replace("Id", '-', '')) = '{batchId:N}';

                SELECT RAISE(IGNORE);
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.CompletedBatches = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Conflicts,
            static conflict => conflict.Code == ErrorCodes.Data.PlanChanged);

        LongRunningOperationStore operations = new(_db!);

        LongRunningOperation operation = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionPrune)));

        byte[] checkpointPayload = Assert.IsType<byte[]>(operation.CheckpointPayload);

        string[] checkpointLines = Encoding.UTF8
            .GetString(checkpointPayload)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("0", checkpointLines[2]);

        Assert.Equal(1, await CountAsync(
            "Batches",
            "Id",
            batchId.ToString()));

        Assert.Equal(0, await CountAsync(
            "Batches",
            "Id",
            laterBatchId.ToString()));

    }

    private async Task SeedInventoryDerivedRowsAsync(
        Guid sessionId,
        Guid entryId,
        Guid attachmentId,
        string attachmentChunkId)
    {

        await SeedEntryEmbeddingAsync(entryId);

        await InsertVectorAsync(
            "entry_embeddings_vec",
            "EntryId",
            entryId.ToString());

        await InsertVectorAsync(
            "session_attachment_embeddings_vec",
            "ChunkId",
            attachmentChunkId);

        string workspaceChunkId = "inventory-workspace-" + Guid.NewGuid().ToString("N");

        await SeedWorkspaceRowsAsync(workspaceChunkId);

        const string sagaId = "inventory-saga";

        await SeedSagaRowsAsync(sagaId, sessionId, attachmentId);

        await ExecuteAsync(
            """
            INSERT INTO saga_extraction_watermarks (SessionId, LastExtractedEntryCreatedAt)
            VALUES (@sessionId, @at)
            """,
            ("@sessionId", sessionId.ToString()),
            ("@at", OldTimestamp));

        await SeedLexiconRowsAsync("inventory-lexicon", sessionId, attachmentId);

        await ExecuteAsync(
            """
            INSERT INTO attachment_memory_consultations
                (SourceEntryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash,
                 MaterializedAt, SourceType)
            VALUES
                ((SELECT Id FROM Entries WHERE lower(replace(Id, '-', '')) = @entryId),
                 @sessionId, @attachmentId, 'evidence', 1, 'HASH', @at, 'WorkspaceFile')
            """,
            ("@entryId", entryId.ToString("N")),
            ("@sessionId", sessionId.ToString()),
            ("@attachmentId", attachmentId.ToString()),
            ("@at", OldTimestamp));

    }

    private async Task SeedPrunableDerivedRowsAsync(
        Guid sessionId,
        Guid entryId)
    {

        await SeedEntryEmbeddingAsync(entryId);

        await InsertVectorAsync(
            "entry_embeddings_vec",
            "EntryId",
            entryId.ToString());

        await SeedWorkspaceRowsAsync("prunable-workspace");

        await SeedSagaRowsAsync(
            "prunable-saga",
            sessionId,
            Guid.NewGuid());

        await SeedLexiconRowsAsync(
            "prunable-lexicon",
            sessionId,
            Guid.NewGuid());

    }

    private async Task SeedWorkspaceRowsAsync(string chunkId)
    {

        await ExecuteAsync(
            """
            INSERT INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset,
                 CharLength, FileLastWriteTime, IndexedAt)
            VALUES
                (@id, '/workspace', 'retention.cs', 0, 'old', 0, 3, @at, @at)
            """,
            ("@id", chunkId),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO workspace_file_embeddings (ChunkId, Embedding, Dim)
            VALUES (@id, @embedding, 1)
            """,
            ("@id", chunkId),
            ("@embedding", new byte[] { 0, 0, 128, 63 }));

        await InsertVectorAsync(
            "workspace_file_embeddings_vec",
            "ChunkId",
            chunkId);

    }

    private async Task SeedSagaRowsAsync(
        string memoryId,
        Guid sessionId,
        Guid attachmentId)
    {

        await ExecuteAsync(
            """
            INSERT INTO saga_memories (Id, Content, CreatedAt, SessionId, Tags, Source)
            VALUES (@id, 'Remembered fact', @at, @sessionId, NULL, 'attachment')
            """,
            ("@id", memoryId),
            ("@at", OldTimestamp),
            ("@sessionId", sessionId.ToString()));

        await ExecuteAsync(
            """
            INSERT INTO saga_memory_embeddings (MemoryId, Embedding, Dim)
            VALUES (@id, @embedding, 1)
            """,
            ("@id", memoryId),
            ("@embedding", new byte[] { 0, 0, 128, 63 }));

        await InsertVectorAsync(
            "saga_memory_embeddings_vec",
            "MemoryId",
            memoryId);

        await ExecuteAsync(
            """
            INSERT INTO saga_memory_attachment_provenance
                (MemoryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash,
                 MaterializedAt, SourceType)
            VALUES
                (@id, @sessionId, @attachmentId, 'evidence', 1, 'HASH', @at, 'WorkspaceFile')
            """,
            ("@id", memoryId),
            ("@sessionId", sessionId.ToString()),
            ("@attachmentId", attachmentId.ToString()),
            ("@at", OldTimestamp));

    }

    private Task SeedLexiconRowsAsync(
        string entryId,
        Guid sessionId,
        Guid attachmentId) =>
        ExecuteAsync(
            """
            INSERT INTO lexicon_entries
                (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
            VALUES
                (@id, @id, @id, 'concept', '["fact"]', 'fact', @at);

            INSERT INTO lexicon_fact_attachment_provenance
                (EntryId, FactHash, Fact, SessionId, AttachmentId, LogicalKey, Version,
                 ContentHash, MaterializedAt, SourceType)
            VALUES
                (@id, 'FACT-HASH', 'fact', @sessionId, @attachmentId, 'evidence', 1,
                 'HASH', @at, 'WorkspaceFile');
            """,
            ("@id", entryId),
            ("@sessionId", sessionId.ToString()),
            ("@attachmentId", attachmentId.ToString()),
            ("@at", OldTimestamp));

    private async Task RecreateVectorTableAsync(
        string table,
        string keyColumn)
    {

        await ExecuteAsync($"DROP TABLE IF EXISTS \"{table}\"");

        await ExecuteAsync(
            $"""
            CREATE TABLE "{table}" (
                "{keyColumn}" TEXT PRIMARY KEY,
                "Embedding" BLOB NOT NULL
            )
            """);

    }

    private Task InsertVectorAsync(
        string table,
        string keyColumn,
        string id) =>
        ExecuteAsync(
            $"""
            INSERT INTO "{table}" ("{keyColumn}", "Embedding")
            VALUES (@id, @embedding)
            """,
            ("@id", id),
            ("@embedding", new byte[] { 0, 0, 128, 63 }));

    private async Task<long> SumTableCountsAsync(params string[] tables)
    {

        long rows = 0;

        foreach (string table in tables)
        {

            rows += await CountAllAsync(table);

        }

        return rows;

    }

    private static void AssertStatusRows(
        DataRetentionStatus status,
        RetentionDataClass dataClass,
        long expectedRows,
        string expectedStore)
    {

        DataRetentionStatusItem item = Assert.Single(
            status.Items,
            candidate => candidate.DataClass == dataClass);

        Assert.Equal(expectedRows, item.Rows);

        Assert.Contains(expectedStore, item.Store, StringComparison.Ordinal);

    }

}
