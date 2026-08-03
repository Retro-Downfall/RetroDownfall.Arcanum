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

    public async Task ApplyAsync_DeleteSession_WhenCommitFails_RestoresQuarantinedBytes()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await CreateDeferredCommitFailureAsync(
            "Sessions",
            "SessionId",
            "fail_session_retention_commit");

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            sessionId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(attachment.AbsolutePath));

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteAttachment_WhenCommitFails_RestoresQuarantinedBytes()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await CreateDeferredCommitFailureAsync(
            "SessionAttachments",
            "AttachmentId",
            "fail_attachment_retention_commit");

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteAttachment,
            attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(attachment.AbsolutePath));

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

    }

    [SkippableFact]

    public async Task ApplyAsync_PruneUploadedFile_WhenCommitFails_RestoresQuarantinedBytes()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        byte[] bytes = [4, 3, 2, 1];

        string path = Path.Combine(_filesRoot, fileId.ToString("N"));

        await File.WriteAllBytesAsync(path, bytes);

        await SeedUploadedFileAsync(fileId, bytes.LongLength);

        await CreateDeferredCommitFailureAsync(
            "UploadedFiles",
            "FileId",
            "fail_uploaded_file_retention_commit");

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        DataRetentionService service = CreateBoundaryService(
            settings,
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains("file:" + fileId.ToString("D"), plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(path));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

        Assert.Equal(1, await CountAllAsync("UploadedFiles"));

    }

    [SkippableFact]

    public async Task ApplyAsync_ResetMemory_WhenRowsAppearAfterPreview_PreservesEntireScope()
    {

        RequireSqlCipher();

        (_, Guid firstEntryId) = await SeedSessionAsync(pinned: false);

        (_, Guid secondEntryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(firstEntryId);

        await ExecuteAsync(
            $"""
            CREATE TRIGGER add_memory_after_retention_start
            AFTER INSERT ON LongRunningOperations
            WHEN NEW.Kind = '{LongRunningOperationKinds.DataRetentionMutation}'
            BEGIN
                INSERT INTO entry_embeddings (EntryId, Embedding, Dim)
                VALUES ('{secondEntryId}', X'0000803F', 1);
            END;
            """);

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.ResetMemory,
            MemoryScope: MemoryResetScope.Entry);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(1, plan.DerivedRecords);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, result.Error.Code);

        Assert.Equal(2, await CountAllAsync("entry_embeddings"));

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteSession_WhenWatermarkReappears_FailsReconciliation()
    {

        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync(
            """
            INSERT INTO saga_extraction_watermarks
                (SessionId, LastExtractedEntryCreatedAt)
            VALUES (@sessionId, @at)
            """,
            ("@sessionId", sessionId.ToString()),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            CREATE TRIGGER retain_session_watermark
            AFTER DELETE ON saga_extraction_watermarks
            BEGIN
                INSERT INTO saga_extraction_watermarks
                    (SessionId, LastExtractedEntryCreatedAt)
                VALUES (OLD.SessionId, OLD.LastExtractedEntryCreatedAt);
            END;
            """);

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            sessionId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.Equal(1, await CountAllAsync("saga_extraction_watermarks"));

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteAttachment_WhenEmbeddingReappears_FailsReconciliation()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await ExecuteAsync(
            """
            CREATE TRIGGER retain_attachment_embedding
            AFTER DELETE ON session_attachment_embeddings
            BEGIN
                INSERT INTO session_attachment_embeddings (ChunkId, Embedding, Dim)
                VALUES (OLD.ChunkId, OLD.Embedding, OLD.Dim);
            END;
            """);

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteAttachment,
            attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.Equal(1, await CountAllAsync("session_attachment_embeddings"));

    }

    [SkippableFact]

    public async Task ApplyAsync_PruneEntry_WhenVectorReappears_FailsReconciliation()
    {

        RequireSqlCipher();

        (_, Guid entryId) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS entry_embeddings_vec
                (EntryId TEXT PRIMARY KEY, Embedding BLOB NOT NULL)
            """);

        await ExecuteAsync(
            "INSERT INTO entry_embeddings_vec (EntryId, Embedding) VALUES (@id, X'0000803F')",
            ("@id", entryId.ToString()));

        await ExecuteAsync(
            """
            CREATE TRIGGER retain_entry_vector
            AFTER DELETE ON entry_embeddings_vec
            BEGIN
                INSERT INTO entry_embeddings_vec (EntryId, Embedding)
                VALUES (OLD.EntryId, OLD.Embedding);
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        DataRetentionService service = CreateBoundaryService(
            settings,
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.Equal(1, await CountAllAsync("entry_embeddings_vec"));

    }

    [SkippableFact]

    public async Task PlanAsync_PruneAttachment_WithMaxOne_DoesNotLetBlockedOldestStarveEligible()
    {

        RequireSqlCipher();

        (Guid blockedSessionId, Guid blockedEntryId) = await SeedSessionAsync(pinned: false);

        (Guid eligibleSessionId, Guid eligibleEntryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment blocked = await SeedAttachmentAsync(blockedSessionId, blockedEntryId);

        SeededAttachment eligible = await SeedAttachmentAsync(eligibleSessionId, eligibleEntryId);

        await SeedContextPinAsync(
            blockedSessionId,
            SessionContextPinKind.Attachment,
            blocked.AttachmentId.ToString());

        await ExecuteAsync(
            "UPDATE SessionAttachments SET CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
            ("@at", "1999-01-01T00:00:00.0000000+00:00"),
            ("@id", blocked.AttachmentId.ToString("N")));

        await ExecuteAsync(
            "UPDATE SessionAttachments SET CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
            ("@at", "2000-01-01T00:00:00.0000000+00:00"),
            ("@id", eligible.AttachmentId.ToString("N")));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Attachments = EnabledRule();

        DataRetentionService service = CreateBoundaryService(
            settings,
            static (_, _) => Task.CompletedTask);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune));

        Assert.Contains(
            "attachment:" + eligible.AttachmentId.ToString("D"),
            plan.CandidateIds);

    }

    private Task CreateDeferredCommitFailureAsync(
        string parentTable,
        string foreignKeyColumn,
        string triggerName) =>
        ExecuteAsync(
            $"""
            CREATE TABLE "{triggerName}_guard" (
                Id TEXT PRIMARY KEY,
                "{foreignKeyColumn}" TEXT NOT NULL,
                FOREIGN KEY ("{foreignKeyColumn}") REFERENCES "{parentTable}"(Id)
                    DEFERRABLE INITIALLY DEFERRED
            );

            CREATE TRIGGER "{triggerName}"
            AFTER DELETE ON "{parentTable}"
            BEGIN
                INSERT INTO "{triggerName}_guard" (Id, "{foreignKeyColumn}")
                VALUES (lower(hex(randomblob(16))), OLD.Id);
            END;
            """);

}
