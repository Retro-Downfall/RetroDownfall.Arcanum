using System.Globalization;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{

    [SkippableTheory]

    [InlineData("pinned-entry", ErrorCodes.Data.Blocked)]

    [InlineData("context-pin", ErrorCodes.Data.Blocked)]

    [InlineData("active-operation", ErrorCodes.Data.Conflict)]

    [InlineData("operator-hold", ErrorCodes.Data.Blocked)]

    public async Task ApplyAsync_DeleteSession_WhenProtectionAppearsAfterGate_FailsClosed(
        string protection,
        string expectedErrorCode)
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        ArcanumSettings settings = new();

        TestCapturingLogger<DataRetentionService> logger = new();

        DataRetentionService service = CreateBoundaryService(
            settings,
            async (_, _) =>
            {

                switch (protection)
                {

                    case "pinned-entry":
                        await ExecuteAsync(
                            "UPDATE Entries SET IsPinned = 1 WHERE lower(replace(Id, '-', '')) = @id",
                            ("@id", entryId.ToString("N")));
                        break;

                    case "context-pin":
                        await SeedContextPinAsync(
                            sessionId,
                            SessionContextPinKind.SessionEntry,
                            entryId.ToString());
                        break;

                    case "active-operation":
                        await SeedActiveOperationAsync(sessionId);
                        break;

                    case "operator-hold":
                        settings.Retention.ProtectedSessionIds = [sessionId];
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(protection),
                            protection,
                            "Unknown boundary protection.");

                }

            },
            logger);

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            sessionId);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Empty(plan.Blockers);

        Assert.Empty(plan.Conflicts);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.True(
            string.Equals(result.Error.Code, expectedErrorCode, StringComparison.Ordinal),
            result.Error.Message
                + System.Environment.NewLine
                + string.Join(
                    System.Environment.NewLine,
                    logger.Entries.Select(static entry => entry.Exception?.ToString())));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("Entries"));

        Assert.Equal(
            1,
            await CountAsync(
                "SessionAttachments",
                "Id",
                attachment.AttachmentId.ToString()));

        Assert.True(File.Exists(attachment.AbsolutePath));

    }

    [SkippableTheory]

    [InlineData("attachment-pin", ErrorCodes.Data.Blocked)]

    [InlineData("session-operation", ErrorCodes.Data.Conflict)]

    [InlineData("operator-hold", ErrorCodes.Data.Blocked)]

    public async Task ApplyAsync_DeleteAttachment_WhenProtectionAppearsAfterGate_FailsClosed(
        string protection,
        string expectedErrorCode)
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        ArcanumSettings settings = new();

        DataRetentionService service = CreateBoundaryService(
            settings,
            async (_, _) =>
            {

                if (string.Equals(
                        protection,
                        "operator-hold",
                        StringComparison.Ordinal))
                {

                    settings.Retention.ProtectedSessionIds = [sessionId];

                    return;

                }

                if (string.Equals(
                        protection,
                        "attachment-pin",
                        StringComparison.Ordinal))
                {

                    await SeedContextPinAsync(
                        sessionId,
                        SessionContextPinKind.Attachment,
                        attachment.AttachmentId.ToString());

                    return;

                }

                await SeedActiveOperationAsync(sessionId);

            });

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteAttachment,
            attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Empty(plan.Blockers);

        Assert.Empty(plan.Conflicts);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.True(
            string.Equals(result.Error.Code, expectedErrorCode, StringComparison.Ordinal),
            result.Error.Message);

        Assert.Equal(
            1,
            await CountAsync(
                "SessionAttachments",
                "Id",
                attachment.AttachmentId.ToString()));

        Assert.True(File.Exists(attachment.AbsolutePath));

    }

    [SkippableFact]

    public async Task PlanAsync_DeleteAttachment_WhenOwningSessionIsHeld_ReportsBlocker()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        ArcanumSettings settings = new();

        settings.Retention.ProtectedSessionIds = [sessionId];

        DataRetentionService service = CreateBoundaryService(
            settings,
            static (_, _) => Task.CompletedTask);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(
                DataRetentionOperation.DeleteAttachment,
                attachment.AttachmentId),
            CancellationToken.None);

        Assert.Contains(
            plan.Blockers,
            blocker => blocker.ReasonCode == "Data.SessionHold");

        Assert.True(File.Exists(attachment.AbsolutePath));

    }

    [SkippableTheory]

    [InlineData(MemoryResetScope.Entry)]

    [InlineData(MemoryResetScope.Attachments)]

    public async Task ApplyAsync_ResetMemory_WhenRelevantWorkAppearsAtBoundary_FailsClosed(
        MemoryResetScope scope)
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        Guid conflictId = Guid.NewGuid();

        string triggerMutation = scope == MemoryResetScope.Entry
            ? $"""
                INSERT INTO "InferenceRuns"
                    ("Id", "RequestId", "Surface", "Purpose", "StartedAt", "Status")
                VALUES
                    ('{conflictId:N}', 'boundary-memory-reset', 'test', 'retention-test',
                     NEW."CreatedAt", {(int)InferenceRunStatus.Running});
                """
            : $"""
                INSERT INTO "LongRunningOperations"
                    ("Id", "Kind", "State", "RecoveryPolicy", "CreatedAt", "PublicSummary")
                VALUES
                    ('{conflictId:N}', '{LongRunningOperationKinds.AttachmentPromotion}',
                     {(int)LongRunningOperationState.Running},
                     {(int)LongRunningOperationRecoveryPolicy.ReconcileAndComplete},
                     NEW."CreatedAt", 'Boundary attachment promotion');
                """;

        await ExecuteAsync(
            $"""
            CREATE TRIGGER protect_memory_after_retention_start
            AFTER INSERT ON "LongRunningOperations"
            WHEN NEW."Kind" = '{LongRunningOperationKinds.DataRetentionMutation}'
            BEGIN
                {triggerMutation}
            END;
            """);

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.ResetMemory,
            MemoryScope: scope);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Empty(plan.Conflicts);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, result.Error.Code);

        Assert.Equal(1, await CountAsync(
            "entry_embeddings",
            "EntryId",
            entryId.ToString()));

        Assert.Equal(1, await CountAsync(
            "session_attachment_chunks",
            "AttachmentId",
            attachment.AttachmentId.ToString()));

    }

    [SkippableFact]

    public async Task ApplyAsync_PruneEntry_WhenActiveWorkAppearsDuringDerivedDelete_RollsBackCandidate()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        Guid conflictId = Guid.NewGuid();

        await ExecuteAsync(
            $"""
            CREATE TRIGGER protect_entry_during_retention_delete
            BEFORE DELETE ON "entry_embeddings"
            WHEN lower(replace(OLD."EntryId", '-', '')) = '{entryId:N}'
            BEGIN
                INSERT INTO "LongRunningOperations"
                    ("Id", "Kind", "State", "RecoveryPolicy", "SessionId", "CreatedAt",
                     "PublicSummary")
                VALUES
                    ('{conflictId:N}', '{LongRunningOperationKinds.WorkspaceIndex}',
                     {(int)LongRunningOperationState.Running},
                     {(int)LongRunningOperationRecoveryPolicy.RestartIdempotently},
                     '{sessionId:N}', '2000-01-01T00:00:00.0000000+00:00',
                     'Boundary workspace indexing');
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        DataRetentionService service = CreateBoundaryService(
            settings,
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains("entry:" + entryId.ToString("D"), plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Conflicts,
            conflict => conflict.Code == ErrorCodes.Data.PlanChanged
                && conflict.ResourceId == "entry:" + entryId.ToString("D"));

        Assert.Equal(1, await CountAllAsync("Entries"));

        Assert.Equal(1, await CountAllAsync("entry_embeddings"));

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteSession_ReportsExactDerivedPlanAndApplyCounts()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        bool entryVector = await TrySeedVectorMirrorAsync(
            "entry_embeddings_vec",
            "EntryId",
            entryId.ToString());

        bool attachmentVector = await TrySeedVectorMirrorAsync(
            "session_attachment_embeddings_vec",
            "ChunkId",
            attachment.ChunkId);

        await ExecuteAsync(
            """
            INSERT INTO attachment_memory_consultations
                (SourceEntryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash,
                 MaterializedAt, SourceType)
            VALUES
                (@entryId, @sessionId, @attachmentId, 'evidence', 1, 'ATTACHMENT-HASH',
                 @at, 'WorkspaceFile')
            """,
            ("@entryId", entryId.ToString().ToUpperInvariant()),
            ("@sessionId", sessionId.ToString().ToUpperInvariant()),
            ("@attachmentId", attachment.AttachmentId.ToString()),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO saga_extraction_watermarks
                (SessionId, LastExtractedEntryCreatedAt)
            VALUES
                (@sessionId, @at)
            """,
            ("@sessionId", sessionId.ToString().ToUpperInvariant()),
            ("@at", OldTimestamp));

        long expectedDerived = 7
            + (entryVector ? 1 : 0)
            + (attachmentVector ? 1 : 0);

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            sessionId);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal(expectedDerived, plan.DerivedRecords);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(plan.DerivedRecords, result.Value.DerivedRecordsDeleted);

    }

    private DataRetentionService CreateBoundaryService(
        ArcanumSettings settings,
        Func<Guid, CancellationToken, Task> acquireSessionGate,
        ILogger<DataRetentionService>? logger = null)
    {

        LongRunningOperationStore operations = new(_db!);

        return new DataRetentionService(
            _db!,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            operations,
            TimeProvider.System,
            logger ?? NullLogger<DataRetentionService>.Instance,
            _attachmentsRoot,
            _filesRoot,
            _logsRoot,
            attachmentStore: new NoOpSessionAttachmentStore(
                acquireSessionGate: acquireSessionGate));

    }

    private Task SeedContextPinAsync(
        Guid sessionId,
        SessionContextPinKind kind,
        string targetIdentifier) =>
        ExecuteAsync(
            """
            INSERT INTO "SessionContextPins"
                ("Id", "SessionId", "Kind", "TargetIdentifier", "DisplayLabel",
                 "CreatedAt", "UpdatedAt")
            VALUES
                (@id,
                 (SELECT Id FROM Sessions WHERE lower(replace(Id, '-', '')) = @sessionId),
                 @kind, @target, 'Boundary pin', @at, @at)
            """,
            ("@id", Guid.NewGuid().ToString()),
            ("@sessionId", sessionId.ToString("N")),
            ("@kind", (int)kind),
            ("@target", targetIdentifier),
            ("@at", OldTimestamp));

    private Task SeedActiveOperationAsync(Guid sessionId) =>
        ExecuteAsync(
            """
            INSERT INTO "LongRunningOperations"
                ("Id", "Kind", "State", "RecoveryPolicy", "SessionId", "CreatedAt",
                 "PublicSummary")
            VALUES
                (@id, @kind, @state, @policy, @sessionId, @at, 'Boundary active work')
            """,
            ("@id", Guid.NewGuid().ToString()),
            ("@kind", LongRunningOperationKinds.WorkspaceIndex),
            ("@state", (int)LongRunningOperationState.Running),
            ("@policy", (int)LongRunningOperationRecoveryPolicy.RestartIdempotently),
            ("@sessionId", sessionId.ToString()),
            ("@at", OldTimestamp));

    private async Task<bool> TrySeedVectorMirrorAsync(
        string table,
        string idColumn,
        string id)
    {

        if (!await TableExistsInTestAsync(table))
        {

            return false;

        }

        string? createSql = await ReadScalarStringAsync(
            "SELECT sql FROM sqlite_master WHERE name = @name",
            ("@name", table));

        const string dimensionMarker = "FLOAT[";

        int marker = createSql?.IndexOf(
            dimensionMarker,
            StringComparison.OrdinalIgnoreCase) ?? -1;

        if (marker < 0)
        {

            return false;

        }

        int start = marker + dimensionMarker.Length;

        int end = createSql!.IndexOf(']', start);

        if (end <= start
            || !int.TryParse(
                createSql[start..end],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int dimensions))
        {

            return false;

        }

        await ExecuteAsync(
            $"INSERT INTO \"{table}\" (\"{idColumn}\", \"Embedding\") VALUES (@id, @embedding)",
            ("@id", id),
            ("@embedding", new byte[dimensions * sizeof(float)]));

        return true;

    }

    private async Task<string?> ReadScalarStringAsync(
        string sql,
        params (string Name, object Value)[] parameters)
    {

        Microsoft.Data.Sqlite.SqliteConnection connection =
            (Microsoft.Data.Sqlite.SqliteConnection)_db!.Database.GetDbConnection();

        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        object? result = await command.ExecuteScalarAsync();

        return result is null || result == DBNull.Value
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);

    }

}
