using System.Globalization;

using System.Text;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed partial class DataRetentionServiceTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private string _filesRoot = string.Empty;

    private string _logsRoot = string.Empty;

    private ArcanumDbContext? _db;

    public DataRetentionServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        string root = Path.Combine(
            Path.GetTempPath(),
            "arcanum-retention-tests-" + Guid.NewGuid().ToString("N"));

        _attachmentsRoot = Path.Combine(root, "attachments");

        _filesRoot = Path.Combine(root, "files");

        _logsRoot = Path.Combine(root, "logs");

        Directory.CreateDirectory(_attachmentsRoot);

        Directory.CreateDirectory(_filesRoot);

        Directory.CreateDirectory(_logsRoot);

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            SqliteConnection connection =
                (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        string? root = Directory.GetParent(_attachmentsRoot)?.FullName;

        if (root is not null && Directory.Exists(root))
        {

            Directory.Delete(root, recursive: true);

        }

    }

    [SkippableFact]

    public async Task PlanAsync_DeleteSession_ReportsImpactAndPinnedEntryBlocker()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: true);

        await SeedEntryEmbeddingAsync(entryId);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            TargetId: sessionId,
            MemoryScope: null);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal(request, plan.Request);

        Assert.False(string.IsNullOrWhiteSpace(plan.PlanId));

        Assert.True(plan.Rows >= 3);

        Assert.Equal(1, plan.Files);

        Assert.True(plan.EstimatedBytes >= attachment.Bytes.Length);

        Assert.True(plan.DerivedRecords >= 3);

        Assert.Contains(
            plan.Items,
            item => item.DataClass == RetentionDataClass.Entries
                && item.Rows == 1);

        Assert.Contains(
            plan.Blockers,
            blocker => blocker.DataClass == RetentionDataClass.Entries
                && blocker.ResourceId == entryId.ToString("D")
                && blocker.ReasonCode.Contains("pin", StringComparison.OrdinalIgnoreCase));

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteSession_RemovesDerivedIndexesAndBytesButPreservesMemoryProvenance()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await SeedSagaAndLexiconProvenanceAsync(
            sessionId,
            attachment.AttachmentId);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            TargetId: sessionId,
            MemoryScope: null);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(0, await CountAsync(
            "Sessions",
            "Id",
            Canonical(sessionId)));

        Assert.Equal(0, await CountAsync(
            "Entries",
            "Id",
            Canonical(entryId)));

        Assert.Equal(0, await CountAsync(
            "entry_embeddings",
            "EntryId",
            Canonical(entryId)));

        Assert.Equal(0, await CountAsync(
            "SessionAttachments",
            "Id",
            Canonical(attachment.AttachmentId)));

        Assert.Equal(0, await CountAsync(
            "session_attachment_chunks",
            "AttachmentId",
            Canonical(attachment.AttachmentId)));

        Assert.Equal(0, await CountAsync(
            "session_attachment_embeddings",
            "ChunkId",
            attachment.ChunkId));

        Assert.Equal(0, await CountAsync(
            "session_attachment_index_state",
            "AttachmentId",
            Canonical(attachment.AttachmentId)));

        Assert.False(File.Exists(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("saga_memories"));

        Assert.Equal(1, await CountAllAsync(
            "saga_memory_attachment_provenance"));

        Assert.Equal(1, await CountAllAsync("lexicon_entries"));

        Assert.Equal(1, await CountAllAsync(
            "lexicon_fact_attachment_provenance"));

        Assert.Equal(0, await ReadAttachmentAvailabilityAsync(
            "saga_memory_attachment_provenance"));

        Assert.Equal(0, await ReadAttachmentAvailabilityAsync(
            "lexicon_fact_attachment_provenance"));

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteSession_TouchesTheDerivedVectorTableABoundedNumberOfTimes()
    {

        RequireSqlCipher();

        const int entryCount = 40;

        Guid[] entryIds = await SeedEntryEmbeddingBatchAsync(entryCount);

        Guid sessionId = await ReadEntrySessionIdAsync(entryIds[0]);

        int vectorStatements = 0;

        SqliteConnection connection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        connection.CreateFunction(
            "count_vector_statement",
            () =>
            {

                _ = Interlocked.Increment(ref vectorStatements);

                return "no-such-entry";

            });

        // Stands in for the sqlite-vec shadow table. It yields exactly one row per statement that
        // touches it, so the counter measures SQL statements rather than rows, and the INSTEAD OF
        // trigger keeps the production DELETE working against a view.
        await ExecuteAsync(
            """
            CREATE VIEW entry_embeddings_vec AS
            SELECT count_vector_statement() AS EntryId;
            """);

        await ExecuteAsync(
            """
            CREATE TRIGGER entry_embeddings_vec_instead_of_delete
            INSTEAD OF DELETE ON entry_embeddings_vec
            BEGIN
                SELECT 1;
            END;
            """);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            TargetId: sessionId,
            MemoryScope: null);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.True(applied.Value.Reconciled);

        Assert.Equal(0, await CountAllAsync("entry_embeddings"));

        Assert.True(
            vectorStatements < entryCount,
            "Deleting a session must not issue derived-index work per entry, but it touched "
                + $"entry_embeddings_vec {vectorStatements} times for {entryCount} entries.");

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteSession_WhenReconciliationFails_DoesNotCompleteDurableOperation()
    {

        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync(
            """
            CREATE TRIGGER retain_deleted_session
            AFTER DELETE ON "Sessions"
            BEGIN
                INSERT INTO "Sessions" ("Id", "Status", "CreatedAt", "UpdatedAt")
                VALUES (OLD."Id", OLD."Status", OLD."CreatedAt", OLD."UpdatedAt");
            END;
            """);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            TargetId: sessionId,
            MemoryScope: null);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionMutation)));

        Assert.Equal(LongRunningOperationState.Failed, operation.State);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, operation.TerminalErrorCode);

        Assert.Equal(1, await CountAllAsync("Sessions"));

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteAttachment_WhenFileIdentityChanges_PreservesMetadataAndBytes()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        Assert.True(
            FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                attachment.AbsolutePath,
                out FileHandleMetadata originalMetadata));

        Func<string, FileHandleMetadata?>? previousSeam =
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

        int identityReads = 0;

        try
        {

            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            {

                if (!string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(attachment.AbsolutePath),
                        StringComparison.Ordinal))
                {

                    // Pass foreign paths through rather than returning null. The seam is
                    // process-global, so null here means "this file cannot be stat'ed" for every
                    // concurrently running test as well, for as long as a real PlanAsync/ApplyAsync
                    // takes — which fails whichever unrelated test happens to touch the filesystem
                    // in that window.
                    return ReadActualNoFollowMetadata(path);

                }

                identityReads++;

                return identityReads == 1
                    ? originalMetadata
                    : originalMetadata with
                    {

                        Identity = new FileHandleIdentity(
                            originalMetadata.Identity.VolumeId,
                            originalMetadata.Identity.FileId + 1),

                    };

            };

            IDataRetentionService service = CreateService();

            DataRetentionRequest request = new(
                DataRetentionOperation.DeleteAttachment,
                TargetId: attachment.AttachmentId,
                MemoryScope: null);

            DataRetentionPlan plan = await service.PlanAsync(
                request,
                CancellationToken.None);

            Result<DataRetentionApplyResult> result = await service.ApplyAsync(
                new DataRetentionApplyRequest(request, plan.PlanId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

            Assert.True(File.Exists(attachment.AbsolutePath));

            Assert.Equal(1, await CountAsync(
                "SessionAttachments",
                "Id",
                Canonical(attachment.AttachmentId)));

        }
        finally
        {

            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previousSeam;

        }

    }

    [SkippableFact]

    public async Task PlanAsync_Prune_BlocksUploadedFileReferencedByInProgressBatch()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        Guid batchId = Guid.NewGuid();

        string absolutePath = Path.Combine(
            _filesRoot,
            fileId.ToString("N"));

        byte[] bytes = [1, 2, 3, 4, 5];

        await File.WriteAllBytesAsync(absolutePath, bytes);

        await ExecuteAsync(
            """
            INSERT INTO "UploadedFiles"
                ("Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt")
            VALUES
                (@id, 'batch.jsonl', @bytes, 'batch', 'application/jsonl', @createdAt)
            """,
            ("@id", fileId.ToString()),
            ("@bytes", bytes.Length),
            ("@createdAt", "2000-01-01T00:00:00.0000000+00:00"));

        await ExecuteAsync(
            """
            INSERT INTO "Batches"
                ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt")
            VALUES
                (@id, @fileId, '/v1/chat/completions', @status, @createdAt)
            """,
            ("@id", batchId.ToString()),
            ("@fileId", fileId.ToString()),
            ("@status", BatchStatuses.InProgress),
            ("@createdAt", "2000-01-01T00:00:00.0000000+00:00"));

        ArcanumSettings settings = new()
        {

            Retention = new RetentionSettings
            {

                AutomaticSweepsEnabled = false,

                UploadedFiles = new RetentionRuleSettings
                {

                    Enabled = true,

                    Days = 1,

                },

                CompletedBatches = new RetentionRuleSettings
                {

                    Enabled = true,

                    Days = 1,

                },

            },

        };

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(
            DataRetentionOperation.Prune,
            TargetId: null,
            MemoryScope: null);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains(
            plan.Blockers,
            blocker => blocker.DataClass == RetentionDataClass.UploadedFiles
                && blocker.ResourceId == fileId.ToString("D")
                && blocker.ReasonCode.Contains("batch", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            plan.Conflicts,
            conflict => conflict.ResourceId == batchId.ToString("D")
                && conflict.Code.Contains("batch", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, await CountAsync(
            "UploadedFiles",
            "Id",
            fileId.ToString()));

        Assert.True(File.Exists(absolutePath));

    }

    [SkippableTheory]

    [InlineData("session")]

    [InlineData("batch")]

    [InlineData("entry")]

    [InlineData("idempotency")]

    [InlineData("accounting")]

    public async Task PlanAsync_Prune_DoesNotLetOldestBlockedRowStarveEligibleRow(
        string scenario)
    {

        RequireSqlCipher();

        (ArcanumSettings settings, string expectedCandidate) =
            await SeedStarvationScenarioAsync(scenario);

        IDataRetentionService service = CreateService(settings);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            CancellationToken.None);

        Assert.Single(plan.CandidateIds);

        Assert.Contains(expectedCandidate, plan.CandidateIds);

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenEntryPinAppearsAtBoundary_PreservesEmbedding()
    {

        RequireSqlCipher();

        (_, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        Guid pinId = Guid.NewGuid();

        await ExecuteAsync(
            $"""
            CREATE TRIGGER pin_entry_after_retention_start
            AFTER INSERT ON "LongRunningOperations"
            WHEN NEW."Kind" = '{LongRunningOperationKinds.DataRetentionPrune}'
            BEGIN
                INSERT INTO "SessionContextPins"
                    ("Id", "SessionId", "Kind", "TargetIdentifier", "DisplayLabel",
                     "ContentVersion", "CreatedAt", "UpdatedAt")
                SELECT
                    '{pinId:N}', entry."SessionId", {(int)SessionContextPinKind.SessionEntry},
                    entry."Id", 'Boundary pin', NULL, NEW."CreatedAt", NEW."CreatedAt"
                FROM "Entries" entry
                WHERE lower(replace(entry."Id", '-', '')) = '{entryId:N}';
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains(
            "entry-embedding:" + Canonical(entryId),
            plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Conflicts,
            static conflict => conflict.Code == ErrorCodes.Data.PlanChanged);

        Assert.Equal(1, await CountAsync(
            "entry_embeddings",
            "EntryId",
            Canonical(entryId)));

        Assert.Equal(1, await CountAllAsync("SessionContextPins"));

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenEarlyCandidateIsPreserved_KeepsRenewingTheDurableLease()
    {

        RequireSqlCipher();

        const int candidateCount = PruneCheckpointIntervalInTest + 1;

        _ = await SeedEntryEmbeddingBatchAsync(candidateCount);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

        HeartbeatCountingOperationStore operations = new(
            new LongRunningOperationStore(
                _db!,
                TestOrdinaryConnectionFactory.For(_db!)));

        IDataRetentionService service = CreateService(
            settings,
            operationStore: operations);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal(candidateCount, plan.CandidateIds.Length);

        const string candidatePrefix = "entry-embedding:";

        string firstCandidate = plan.CandidateIds[0];

        Assert.StartsWith(candidatePrefix, firstCandidate, StringComparison.Ordinal);

        Guid preservedEntryId = Guid.Parse(
            firstCandidate[candidatePrefix.Length..],
            CultureInfo.InvariantCulture);

        await ExecuteAsync(
            $"""
            CREATE TRIGGER pin_first_candidate_after_retention_start
            AFTER INSERT ON "LongRunningOperations"
            WHEN NEW."Kind" = '{LongRunningOperationKinds.DataRetentionPrune}'
            BEGIN
                INSERT INTO "SessionContextPins"
                    ("Id", "SessionId", "Kind", "TargetIdentifier", "DisplayLabel",
                     "ContentVersion", "CreatedAt", "UpdatedAt")
                SELECT
                    '{Guid.NewGuid():N}', entry."SessionId",
                    {(int)SessionContextPinKind.SessionEntry},
                    entry."Id", 'Boundary pin', NULL, NEW."CreatedAt", NEW."CreatedAt"
                FROM "Entries" entry
                WHERE lower(replace(entry."Id", '-', '')) = '{preservedEntryId:N}';
            END;
            """);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Conflicts,
            static conflict => conflict.Code == ErrorCodes.Data.PlanChanged);

        Assert.Equal(1, await CountAllAsync("entry_embeddings"));

        Assert.True(
            operations.Heartbeats > 0,
            "A sweep that preserved one candidate must keep renewing its durable lease for the "
                + $"remaining {candidateCount - 1} candidates, but it renewed nothing.");

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenCandidatesOutlastTheLeaseBeforeACheckpointBoundary_KeepsRenewingTheDurableLease()
    {

        RequireSqlCipher();

        // Fewer candidates than PruneCheckpointInterval, each slower than the heartbeat interval:
        // renewal has to be wall-clock driven, because no candidate-count boundary is ever reached.
        const int candidateCount = 4;

        _ = await SeedEntryEmbeddingBatchAsync(candidateCount);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

        FakeTimeProvider time = new();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        connection.CreateFunction(
            "advance_retention_clock",
            () =>
            {

                time.Advance(TimeSpan.FromMinutes(2));

                return 1L;

            });

        HeartbeatCountingOperationStore operations = new(
            new LongRunningOperationStore(
                _db!,
                TestOrdinaryConnectionFactory.For(_db!)));

        IDataRetentionService service = CreateService(
            settings,
            timeProvider: time,
            operationStore: operations);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal(candidateCount, plan.CandidateIds.Length);

        await ExecuteAsync(
            """
            CREATE TRIGGER advance_clock_after_embedding_delete
            AFTER DELETE ON "entry_embeddings"
            BEGIN
                SELECT advance_retention_clock();
            END;
            """);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, await CountAllAsync("entry_embeddings"));

        Assert.True(
            operations.Heartbeats > 0,
            "A sweep whose candidates outlast the heartbeat interval must renew its durable lease "
                + "before the next candidate, but it renewed nothing.");

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenCancelled_ReleasesTheDurableLeaseAndNamesItselfInTheNextConflict()
    {

        RequireSqlCipher();

        _ = await SeedEntryEmbeddingBatchAsync(4);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

        using CancellationTokenSource cancellation = new();

        SqliteConnection connection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        connection.CreateFunction(
            "cancel_retention_apply",
            () =>
            {

                cancellation.Cancel();

                return 1L;

            });

        await ExecuteAsync(
            $"""
            CREATE TRIGGER cancel_apply_after_retention_start
            AFTER INSERT ON "LongRunningOperations"
            WHEN NEW."Kind" = '{LongRunningOperationKinds.DataRetentionPrune}'
            BEGIN
                SELECT cancel_retention_apply();
            END;
            """);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        IDataRetentionService service = CreateService(
            settings,
            operationStore: operations);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ApplyAsync(
                new DataRetentionApplyRequest(request, plan.PlanId),
                cancellation.Token));

        await ExecuteAsync("DROP TRIGGER cancel_apply_after_retention_start;");

        LongRunningOperation stranded = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionPrune)));

        // The lease must be surrendered so the background reconciler can adopt the row on its very
        // next pass instead of waiting out a five-minute lease nobody is holding.
        Assert.Null(stranded.LeaseOwner);

        Assert.Null(stranded.LeaseExpiresAt);

        Result<DataRetentionApplyResult> blocked = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(blocked.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, blocked.Error.Code);

        Assert.Contains(
            stranded.Id.ToString("D"),
            blocked.Error.Message,
            StringComparison.Ordinal);

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenSessionBecomesHeldAtBoundary_PreservesEntryEmbedding()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

        ArcanumSettings heldSettings = CreatePruneSettings();

        heldSettings.Retention.SessionEntryEmbeddings = EnabledRule();

        RetentionSettings held = heldSettings.Retention;

        held.ProtectedSessionIds = [sessionId];

        SequencedRetentionPolicyStore policy = new(
            settings.Retention,
            held,
            initialReads: 2);

        IDataRetentionService service = CreateService(
            settings,
            policy);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains(
            "entry-embedding:" + Canonical(entryId),
            plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Conflicts,
            static conflict => conflict.Code == ErrorCodes.Data.PlanChanged);

        Assert.Equal(1, await CountAsync(
            "entry_embeddings",
            "EntryId",
            Canonical(entryId)));

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenSessionOperationAppearsAtBoundary_PreservesEntryEmbedding()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        Guid activeOperationId = Guid.NewGuid();

        await ExecuteAsync(
            $"""
            CREATE TRIGGER protect_entry_after_retention_start
            AFTER INSERT ON "LongRunningOperations"
            WHEN NEW."Kind" = '{LongRunningOperationKinds.DataRetentionPrune}'
            BEGIN
                INSERT INTO "LongRunningOperations"
                    ("Id", "Kind", "State", "RecoveryPolicy", "SessionId", "CreatedAt",
                     "PublicSummary")
                SELECT
                    '{activeOperationId:N}', '{LongRunningOperationKinds.WorkspaceIndex}',
                    {(int)LongRunningOperationState.Running},
                    {(int)LongRunningOperationRecoveryPolicy.RestartIdempotently},
                    session."Id", NEW."CreatedAt", 'Boundary session operation'
                FROM "Sessions" session
                WHERE lower(replace(session."Id", '-', '')) = '{sessionId:N}';
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

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

        Assert.Equal(1, await CountAsync(
            "entry_embeddings",
            "EntryId",
            Canonical(entryId)));

        Assert.Equal(1, await CountAsync(
            "LongRunningOperations",
            "Id",
            activeOperationId.ToString("N")));

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenWorkspaceIndexStartsAtBoundary_PreservesWorkspaceCandidate()
    {

        RequireSqlCipher();

        string chunkId = "boundary-workspace-" + Guid.NewGuid().ToString("N");

        await ExecuteAsync(
            """
            INSERT INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset,
                 CharLength, FileLastWriteTime, IndexedAt)
            VALUES
                (@id, '/workspace', 'boundary.cs', 0, 'old', 0, 3, @at, @at)
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

        Guid activeOperationId = Guid.NewGuid();

        await ExecuteAsync(
            $"""
            CREATE TRIGGER protect_workspace_after_retention_start
            AFTER INSERT ON "LongRunningOperations"
            WHEN NEW."Kind" = '{LongRunningOperationKinds.DataRetentionPrune}'
            BEGIN
                INSERT INTO "LongRunningOperations"
                    ("Id", "Kind", "State", "RecoveryPolicy", "CreatedAt", "PublicSummary")
                VALUES
                    ('{activeOperationId:N}', '{LongRunningOperationKinds.WorkspaceIndex}',
                     {(int)LongRunningOperationState.Running},
                     {(int)LongRunningOperationRecoveryPolicy.RestartIdempotently},
                     NEW."CreatedAt", 'Boundary workspace operation');
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.WorkspaceIndexes = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains("workspace:" + chunkId, plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Conflicts,
            static conflict => conflict.Code == ErrorCodes.Data.PlanChanged);

        Assert.Equal(1, await CountAsync(
            "workspace_file_chunks",
            "ChunkId",
            chunkId));

        Assert.Equal(1, await CountAsync(
            "workspace_file_embeddings",
            "ChunkId",
            chunkId));

    }

    [SkippableFact]

    public async Task ApplyAsync_RepeatedDeleteSession_ReturnsNotFoundAfterFirstDelete()
    {

        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            TargetId: sessionId,
            MemoryScope: null);

        DataRetentionPlan firstPlan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> first = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, firstPlan.PlanId),
            CancellationToken.None);

        DataRetentionPlan secondPlan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> second = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, secondPlan.PlanId),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.True(second.IsFailure);

        Assert.Equal(ErrorCodes.Data.NotFound, second.Error.Code);

    }

    [SkippableFact]

    public async Task PlanAsync_DeleteSession_ReportsActiveOperationAndReservationConflicts()
    {

        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        Guid runId = Guid.NewGuid();

        Guid reservationId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await ExecuteAsync(
            """
            INSERT INTO "InferenceRuns"
                ("Id", "RequestId", "SessionId", "Surface", "Purpose", "StartedAt", "Status")
            VALUES
                (@id, 'request-retention', @sessionId, 'test', 'test', @startedAt, @status)
            """,
            ("@id", runId.ToString("N")),
            ("@sessionId", sessionId.ToString("N")),
            ("@startedAt", now.ToString("o", CultureInfo.InvariantCulture)),
            ("@status", (int)InferenceRunStatus.Running));

        await ExecuteAsync(
            """
            INSERT INTO "BudgetReservations"
                ("Id", "RunId", "BudgetPeriod", "ReservedUsd", "ReconciledUsd", "Status",
                 "ExpiresAt", "CreatedAt", "UpdatedAt")
            VALUES
                (@id, @runId, '2026-08-02', 1.25, 0, @status, @expiresAt, @createdAt, @updatedAt)
            """,
            ("@id", reservationId.ToString("N")),
            ("@runId", runId.ToString("N")),
            ("@status", (int)BudgetReservationStatus.Reserved),
            ("@expiresAt", now.AddHours(1).ToString("o", CultureInfo.InvariantCulture)),
            ("@createdAt", now.ToString("o", CultureInfo.InvariantCulture)),
            ("@updatedAt", now.ToString("o", CultureInfo.InvariantCulture)));

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.WorkspaceIndex,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Indexing session workspace.",
                now,
                SessionId: sessionId,
                RunId: runId,
                InferenceRunId: runId,
                BudgetReservationId: reservationId));

        _ = await operations.TryAcquireLeaseAsync(
            operation.Id,
            "retention-test-worker",
            now,
            now.AddMinutes(1));

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            TargetId: sessionId,
            MemoryScope: null);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains(
            plan.Conflicts,
            conflict => conflict.ResourceId == operation.Id.ToString("D")
                && conflict.Code.Contains("operation", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            plan.Conflicts,
            conflict => conflict.ResourceId == reservationId.ToString("D")
                && conflict.Code.Contains("reservation", StringComparison.OrdinalIgnoreCase));

    }

    /// <summary>
    /// Every typed class is reported exactly once, and the one conditional class is conditional for a
    /// stated reason rather than by omission.
    /// </summary>
    /// <remarks>
    /// <see cref="RetentionDataClass.Covenant"/> is reported only when an installation read capability
    /// can actually be taken, which requires the feature on and the canonical tier healthy. An
    /// installation with no Covenant arm reports no Covenant row rather than a row of zeroes, because a
    /// zero is a measurement and the honest answer there is that nothing was measured. That is asserted
    /// here — and its positive case is asserted in <c>CovenantRetentionTests</c> — so the absence can
    /// never be mistaken for a class somebody forgot to add (issue #116, §10.20.1).
    /// </remarks>
    [SkippableFact]

    public async Task GetStatusAsync_ReportsEveryTypedRetentionClassExactlyOnce()
    {

        RequireSqlCipher();

        IDataRetentionService service = CreateService();

        DataRetentionStatus status = await service.GetStatusAsync(
            CancellationToken.None);

        RetentionDataClass[] expected =
            [.. Enum.GetValues<RetentionDataClass>()
                .Where(static dataClass => dataClass is not RetentionDataClass.Covenant)];

        Assert.Equal(
            expected.Order(),
            status.Items.Select(static item => item.DataClass).Order());

        Assert.Equal(
            expected.Length,
            status.Items.Select(static item => item.DataClass).Distinct().Count());

        Assert.Null(status.Covenant);

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_RemovesTerminalBatchAndItsUnreferencedUploadedFile()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        Guid batchId = Guid.NewGuid();

        string absolutePath = Path.Combine(
            _filesRoot,
            fileId.ToString("N"));

        await File.WriteAllBytesAsync(absolutePath, [1, 2, 3]);

        await SeedUploadedFileAsync(fileId, 3);

        await ExecuteAsync(
            """
            INSERT INTO "Batches"
                ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt")
            VALUES
                (@id, @fileId, '/v1/chat/completions', @status, @createdAt, @completedAt)
            """,
            ("@id", batchId.ToString()),
            ("@fileId", fileId.ToString()),
            ("@status", BatchStatuses.Completed),
            ("@createdAt", OldTimestamp),
            ("@completedAt", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        settings.Retention.CompletedBatches = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.CompletedBatches
                && item.Rows == 1);

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.UploadedFiles
                && item.Rows == 1
                && item.Files == 1);

        Assert.DoesNotContain(
            plan.Blockers,
            blocker => blocker.ResourceId == fileId.ToString("D"));

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, await CountAsync("Batches", "Id", batchId.ToString()));

        Assert.Equal(0, await CountAsync("UploadedFiles", "Id", fileId.ToString()));

        Assert.False(File.Exists(absolutePath));

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenAnotherPruneIsActive_ReturnsConflict()
    {

        RequireSqlCipher();

        IDataRetentionService service = CreateService(CreatePruneSettings());

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation? active = await operations.TryStartSingleFlightAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Apply a hosted retention sweep.",
                now),
            "hosted-retention",
            now,
            now.AddMinutes(5));

        Assert.NotNull(active);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, result.Error.Code);

        IReadOnlyList<LongRunningOperation> pruneOperations = await operations.ListAsync(
            new LongRunningOperationQuery(
                Kind: LongRunningOperationKinds.DataRetentionPrune));

        Assert.Single(pruneOperations);

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteSession_WhenPruneIsActive_ReturnsConflictWithoutPendingMutation()
    {

        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            sessionId);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation? active = await operations.TryStartSingleFlightAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Apply a hosted retention sweep.",
                now),
            "hosted-retention",
            now,
            now.AddMinutes(5));

        Assert.NotNull(active);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, result.Error.Code);

        Assert.True(
            await _db!.Sessions
                .AsNoTracking()
                .AnyAsync(
                    session => session.Id == sessionId,
                    CancellationToken.None));

        IReadOnlyList<LongRunningOperation> mutations = await operations.ListAsync(
            new LongRunningOperationQuery(
                Kind: LongRunningOperationKinds.DataRetentionMutation));

        Assert.Empty(mutations);

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_EnforcesWorkspaceIdempotencyAndAuditPoliciesWithCheckpoint()
    {

        RequireSqlCipher();

        string chunkId = "workspace-" + Guid.NewGuid().ToString("N");

        Guid claimId = Guid.NewGuid();

        await ExecuteAsync(
            """
            INSERT INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset,
                 CharLength, FileLastWriteTime, IndexedAt)
            VALUES
                (@id, '/workspace', 'old.cs', 0, 'old', 0, 3, @at, @at)
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

        await ExecuteAsync(
            """
            INSERT INTO "IdempotencyClaims"
                ("Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId",
                 "LeaseExpiresAt", "HeartbeatAt", "TerminalStreamComplete", "CreatedAt", "UpdatedAt")
            VALUES
                (@id, @key, 'fingerprint', @state, 'completed-owner', @at, @at, 1, @at, @at)
            """,
            ("@id", claimId.ToString()),
            ("@key", "key-" + claimId.ToString("N")),
            ("@state", (int)IdempotencyClaimState.Completed),
            ("@at", OldTimestamp));

        string auditPath = Path.Combine(_logsRoot, "audit-20000101.jsonl");

        await File.WriteAllTextAsync(auditPath, "{}\n");

        File.SetLastWriteTimeUtc(auditPath, DateTime.UnixEpoch);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.WorkspaceIndexes = EnabledRule();

        settings.Retention.IdempotencyClaims = EnabledRule();

        settings.Retention.AuditLogs = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.WorkspaceChunks
                && item.Rows == 1);

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.WorkspaceEmbeddings
                && item.DerivedRecords == 1);

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.IdempotencyClaims
                && item.Rows == 1);

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.AuditLogs
                && item.Files == 1);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, await CountAsync(
            "workspace_file_chunks",
            "ChunkId",
            chunkId));

        Assert.Equal(0, await CountAsync(
            "workspace_file_embeddings",
            "ChunkId",
            chunkId));

        Assert.Equal(0, await CountAsync(
            "IdempotencyClaims",
            "Id",
            claimId.ToString()));

        Assert.False(File.Exists(auditPath));

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionPrune,
                    Limit: 10)));

        Assert.True(operation.CheckpointVersion > 0);

        Assert.NotNull(operation.CheckpointPayload);

        Assert.Equal(LongRunningOperationState.Completed, operation.State);

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenCandidateReconciliationFails_DoesNotAdvanceCheckpointOrComplete()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        await SeedUploadedFileAsync(fileId, 0);

        await ExecuteAsync(
            """
            CREATE TRIGGER retain_deleted_uploaded_file
            AFTER DELETE ON "UploadedFiles"
            BEGIN
                INSERT INTO "UploadedFiles"
                    ("Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt")
                VALUES
                    (OLD."Id", OLD."Filename", OLD."Bytes", OLD."Purpose", OLD."MimeType", OLD."CreatedAt");
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Single(plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.Equal(LongRunningOperationState.Failed, operation.State);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, operation.TerminalErrorCode);

        byte[] checkpointPayload = Assert.IsType<byte[]>(operation.CheckpointPayload);

        string[] checkpointLines = Encoding.UTF8
            .GetString(checkpointPayload)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("0", checkpointLines[2]);

        Assert.Equal(1, await CountAsync(
            "UploadedFiles",
            "Id",
            fileId.ToString()));

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenTerminalBatchReappears_DoesNotCheckpointPastCandidate()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        Guid batchId = Guid.NewGuid();

        await SeedUploadedFileAsync(fileId, 0);

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
            CREATE TRIGGER retain_deleted_terminal_batch
            AFTER DELETE ON "Batches"
            BEGIN
                INSERT INTO "Batches"
                    ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt",
                     "OutputFileId", "ErrorFileId")
                VALUES
                    (OLD."Id", OLD."InputFileId", OLD."Endpoint", OLD."Status", OLD."CreatedAt",
                     OLD."CompletedAt", OLD."OutputFileId", OLD."ErrorFileId");
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.CompletedBatches = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal(
            "batch:" + batchId.ToString("D"),
            Assert.Single(plan.CandidateIds));

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.Equal(LongRunningOperationState.Failed, operation.State);

        byte[] checkpointPayload = Assert.IsType<byte[]>(operation.CheckpointPayload);

        string[] checkpointLines = Encoding.UTF8
            .GetString(checkpointPayload)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("0", checkpointLines[2]);

        Assert.Equal(1, await CountAsync(
            "Batches",
            "Id",
            batchId.ToString()));

    }

    [SkippableFact]

    public async Task ApplyAsync_RepeatedPrune_ConvergesWithoutRemovingAdditionalData()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        Guid batchId = Guid.NewGuid();

        string absolutePath = Path.Combine(
            _filesRoot,
            fileId.ToString("N"));

        await File.WriteAllBytesAsync(absolutePath, [1, 2, 3]);

        await SeedUploadedFileAsync(fileId, 3);

        await ExecuteAsync(
            """
            INSERT INTO "Batches"
                ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt")
            VALUES
                (@id, @fileId, '/v1/chat/completions', @status, @createdAt, @completedAt)
            """,
            ("@id", batchId.ToString()),
            ("@fileId", fileId.ToString()),
            ("@status", BatchStatuses.Completed),
            ("@createdAt", OldTimestamp),
            ("@completedAt", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        settings.Retention.CompletedBatches = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan firstPlan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> first = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, firstPlan.PlanId),
            CancellationToken.None);

        DataRetentionPlan secondPlan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> second = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, secondPlan.PlanId),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Empty(secondPlan.CandidateIds);

        Assert.Equal(0, second.Value.RowsDeleted);

        Assert.Equal(0, await CountAllAsync("Batches"));

        Assert.Equal(0, await CountAllAsync("UploadedFiles"));

        Assert.False(File.Exists(absolutePath));

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WithDisabledRules_PreservesEligibleLookingData()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        string absolutePath = Path.Combine(
            _filesRoot,
            fileId.ToString("N"));

        await File.WriteAllBytesAsync(absolutePath, [4, 5, 6]);

        await SeedUploadedFileAsync(fileId, 3);

        IDataRetentionService service = CreateService(CreatePruneSettings());

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.Empty(plan.CandidateIds);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1, await CountAsync(
            "UploadedFiles",
            "Id",
            fileId.ToString()));

        Assert.True(File.Exists(absolutePath));

    }

    [SkippableFact]

    public async Task RecoverPruneAsync_ResumesCheckpointedCandidateAfterInterruption()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        string absolutePath = Path.Combine(
            _filesRoot,
            fileId.ToString("N"));

        await File.WriteAllBytesAsync(absolutePath, [7, 8, 9]);

        await SeedUploadedFileAsync(fileId, 3);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            CancellationToken.None);

        string candidate = Assert.Single(plan.CandidateIds);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "interrupted-retention-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Interrupted retention test.",
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        byte[] checkpoint = Encoding.UTF8.GetBytes(
            "ARCADATA2\n"
            + plan.PlanId
            + "\n0\nG:"
            + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(plan.GeneratedAt.ToString("o")))
            + "\nC:"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(candidate))
            + ":"
            + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(plan.GeneratedAt.AddDays(-30).ToString("o")))
            + "\n");

        bool saved = await operations.SaveCheckpointAsync(
            operation.Id,
            ownerId,
            expectedCheckpointVersion: 0,
            checkpointVersion: 2,
            checkpoint,
            checkpointReference: "retention-prune:" + operation.Id.ToString("N"),
            "Interrupted before the next candidate.",
            now);

        Assert.True(saved);

        LongRunningOperation interrupted = Assert.IsType<LongRunningOperation>(
            await operations.GetAsync(operation.Id));

        LongRunningOperationRecoveryResult recovered = await service.RecoverPruneAsync(
            interrupted,
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

        Assert.Equal(0, await CountAsync(
            "UploadedFiles",
            "Id",
            fileId.ToString()));

        Assert.False(File.Exists(absolutePath));

    }

    [SkippableFact]

    public async Task PlanAsync_FactoryReset_ReportsActiveIdempotencyAndBatchConflicts()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        Guid batchId = Guid.NewGuid();

        Guid claimId = Guid.NewGuid();

        DateTimeOffset future = DateTimeOffset.UtcNow.AddHours(1);

        await SeedUploadedFileAsync(fileId, 0);

        await ExecuteAsync(
            """
            INSERT INTO "Batches"
                ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt")
            VALUES
                (@id, @fileId, '/v1/chat/completions', @status, @createdAt)
            """,
            ("@id", batchId.ToString()),
            ("@fileId", fileId.ToString()),
            ("@status", BatchStatuses.InProgress),
            ("@createdAt", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO "IdempotencyClaims"
                ("Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId",
                 "LeaseExpiresAt", "HeartbeatAt", "TerminalStreamComplete", "CreatedAt", "UpdatedAt")
            VALUES
                (@id, @key, 'fingerprint', @state, 'active-owner', @lease, @at, 0, @at, @at)
            """,
            ("@id", claimId.ToString()),
            ("@key", "key-" + claimId.ToString("N")),
            ("@state", (int)IdempotencyClaimState.Running),
            ("@lease", future.ToString("o", CultureInfo.InvariantCulture)),
            ("@at", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

        IDataRetentionService service = CreateService();

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.FactoryReset),
            CancellationToken.None);

        Assert.Contains(
            plan.Conflicts,
            conflict => conflict.ResourceId == batchId.ToString("D")
                && conflict.Code.Contains("batch", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            plan.Conflicts,
            conflict => conflict.ResourceId == claimId.ToString("D")
                && conflict.Code.Contains("idempotency", StringComparison.OrdinalIgnoreCase));

    }

    [SkippableFact]

    public async Task PlanAsync_FactoryReset_CountsPhysicalAndDerivedRecordsOnce()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await SeedSagaAndLexiconProvenanceAsync(
            sessionId,
            attachment.AttachmentId);

        await ExecuteAsync(
            """
            INSERT INTO attachment_memory_consultations
                (SourceEntryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash,
                 MaterializedAt, SourceType)
            VALUES
                ((SELECT Id FROM Entries WHERE lower(replace(Id, '-', '')) = @entryId),
                 @sessionId, @attachmentId, 'evidence', 1, 'ATTACHMENT-HASH',
                 @at, 'WorkspaceFile')
            """,
            ("@entryId", entryId.ToString("N")),
            ("@sessionId", sessionId.ToString()),
            ("@attachmentId", Canonical(attachment.AttachmentId)),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO "SessionContextPins"
                ("Id", "SessionId", "Kind", "TargetIdentifier", "DisplayLabel",
                 "CreatedAt", "UpdatedAt")
            VALUES
                (@id,
                 (SELECT Id FROM Sessions WHERE lower(replace(Id, '-', '')) = @sessionId),
                 0, @entryId, 'Pinned entry', @at, @at)
            """,
            ("@id", Guid.NewGuid().ToString()),
            ("@sessionId", sessionId.ToString("N")),
            ("@entryId", entryId.ToString()),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO "WorkspaceContexts"
                ("Id", "RootPath", "SerializedSnapshot", "CreatedAt")
            VALUES
                (@id, '/workspace', '{}', @at)
            """,
            ("@id", Guid.NewGuid().ToString()),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO "UnseenServantWatermarks"
                ("JobKey", "LastRunAt", "EffectiveIntervalMinutes")
            VALUES
                ('factory-plan-job', @at, 30)
            """,
            ("@at", OldTimestamp));

        Guid fileId = Guid.NewGuid();

        byte[] fileBytes = [1, 2, 3, 4];

        await File.WriteAllBytesAsync(
            Path.Combine(_filesRoot, fileId.ToString("N")),
            fileBytes);

        await SeedUploadedFileAsync(fileId, fileBytes.LongLength);

        await ExecuteAsync(
            """
            INSERT INTO "Batches"
                ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt",
                 "OutputFileId", "ErrorFileId")
            VALUES
                (@id, @fileId, '/v1/chat/completions', 'completed', @at, @at,
                 @fileId, @fileId)
            """,
            ("@id", Guid.NewGuid().ToString()),
            ("@fileId", fileId.ToString()),
            ("@at", OldTimestamp));

        IDataRetentionService service = CreateService();

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.FactoryReset),
            CancellationToken.None);

        Assert.DoesNotContain(
            plan.Items,
            static item => item.DataClass is RetentionDataClass.BatchInputFiles
                or RetentionDataClass.BatchOutputFiles
                or RetentionDataClass.BatchErrorFiles);

        DataRetentionPlanItem uploaded = Assert.Single(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.UploadedFiles);

        Assert.Equal(1, uploaded.Rows);

        Assert.Equal(1, uploaded.Files);

        Assert.Equal(fileBytes.LongLength, uploaded.EstimatedBytes);

        Assert.Equal(
            1,
            Assert.Single(
                plan.Items,
                static item => item.DataClass == RetentionDataClass.CompletedBatches).Rows);

        DataRetentionPlanItem workspace = Assert.Single(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.WorkspaceChunks);

        Assert.Equal(1, workspace.Rows);

        Assert.Equal(
            1,
            Assert.Single(
                plan.Items,
                static item => item.DataClass == RetentionDataClass.DaemonExecutions).Rows);

        Assert.True(
            plan.DerivedRecords >= 10,
            $"Expected dependency/index/provenance rows in the factory plan, got {plan.DerivedRecords}.");

        Assert.Equal(2, plan.Files);

        Assert.Equal(
            attachment.Bytes.LongLength + fileBytes.LongLength,
            plan.EstimatedBytes);

    }

    [SkippableFact]

    public async Task FactoryReset_RecoversDatedLogFromInterruptedQuarantine()
    {

        RequireSqlCipher();

        string quarantineDirectory = Directory.CreateDirectory(
            Path.Combine(
                _logsRoot,
                ".arcanum-cleanup-" + Guid.NewGuid().ToString("N"))).FullName;

        string auditPath = Path.Combine(
            quarantineDirectory,
            "audit-20000101.jsonl");

        byte[] bytes = Encoding.UTF8.GetBytes("{\"event\":\"retained\"}\n");

        await File.WriteAllBytesAsync(auditPath, bytes);

        DataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        DataRetentionPlanItem logs = Assert.Single(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.AuditLogs);

        Assert.Equal(1, logs.Files);

        Assert.Equal(bytes.LongLength, logs.EstimatedBytes);

        (LongRunningOperationReconciliationSummary recovery, _) =
            await ReconcileFactoryResetV0Async(
                service,
                "dated-log-factory-recovery-test");

        Assert.Equal(1, recovery.Completed);

        Assert.Equal(0, recovery.RequiresAttention);

        Assert.False(File.Exists(auditPath));

        Assert.False(Directory.Exists(quarantineDirectory));

    }

    [SkippableFact]

    public async Task ApplyAsync_FactoryReset_WithoutCovenantLifecycle_FailsClosedBeforeOperationOrDeletion()
    {

        RequireSqlCipher();

        _ = await SeedSessionAsync(pinned: false);

        DataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, result.Error.Code);

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Empty(
            await new LongRunningOperationStore(
                _db!,
                TestOrdinaryConnectionFactory.For(_db!)).ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionFactoryReset,
                    Limit: 10)));

    }

    [Fact]

    public void FactoryReset_UsesDedicatedRestartableOperationPolicy()
    {

        Assert.True(
            LongRunningOperationPolicyCatalog.IsRegistered(
                LongRunningOperationKinds.DataRetentionFactoryReset,
                LongRunningOperationRecoveryPolicy.RestartIdempotently));

    }

    [SkippableFact]

    public async Task PlanAsync_Prune_AccountingFloorPreservesRetainedSessionCosts()
    {

        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        Guid retainedRunId = Guid.NewGuid();

        Guid orphanRunId = Guid.NewGuid();

        await SeedCompletedInferenceRunAsync(retainedRunId, sessionId);

        await SeedCompletedInferenceRunAsync(orphanRunId, null);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = EnabledRule(days: 1);

        settings.Retention.AccountingMinimumDays = 30;

        IDataRetentionService service = CreateService(settings);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune),
            CancellationToken.None);

        Assert.Contains(
            "accounting:" + orphanRunId.ToString("D"),
            plan.CandidateIds);

        Assert.DoesNotContain(
            "accounting:" + retainedRunId.ToString("D"),
            plan.CandidateIds);

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_RemovesOldStandaloneAdjustmentsAndBudgetAlerts()
    {

        RequireSqlCipher();

        Guid adjustmentId = Guid.NewGuid();

        Guid alertId = Guid.NewGuid();

        await ExecuteAsync(
            """
            INSERT INTO "CostAdjustments"
                ("Id", "BillableOperationId", "RunId", "AmountUsd", "Reason", "CreatedAt")
            VALUES
                (@id, NULL, NULL, -1.0, 'standalone correction', @at)
            """,
            ("@id", adjustmentId.ToString()),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO "BudgetAlerts"
                ("Id", "Threshold", "AlertedAt", "SpendUsd", "DailyLimitUsd")
            VALUES
                (@id, 0.8, @at, 8.0, 10.0)
            """,
            ("@id", alertId.ToString()),
            ("@at", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = EnabledRule(days: 1);

        settings.Retention.AccountingMinimumDays = 30;

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains(
            "accounting-adjustment:" + adjustmentId.ToString("D"),
            plan.CandidateIds);

        Assert.Contains(
            "budget-alert:" + alertId.ToString("D"),
            plan.CandidateIds);

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.CostAdjustments
                && item.Rows == 2);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, await CountAllAsync("CostAdjustments"));

        Assert.Equal(0, await CountAllAsync("BudgetAlerts"));

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenStandaloneAccountingEligibilityChangesAtBoundary_PreservesRows()
    {

        RequireSqlCipher();

        Guid adjustmentId = Guid.NewGuid();

        Guid alertId = Guid.NewGuid();

        await ExecuteAsync(
            """
            INSERT INTO "CostAdjustments"
                ("Id", "BillableOperationId", "RunId", "AmountUsd", "Reason", "CreatedAt")
            VALUES
                (@id, NULL, NULL, -1.0, 'standalone correction', @at)
            """,
            ("@id", adjustmentId.ToString()),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO "BudgetAlerts"
                ("Id", "Threshold", "AlertedAt", "SpendUsd", "DailyLimitUsd")
            VALUES
                (@id, 0.8, @at, 8.0, 10.0)
            """,
            ("@id", alertId.ToString()),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            $"""
            CREATE TRIGGER protect_standalone_accounting_after_retention_start
            AFTER INSERT ON "LongRunningOperations"
            WHEN NEW."Kind" = '{LongRunningOperationKinds.DataRetentionPrune}'
            BEGIN
                UPDATE "CostAdjustments"
                SET "RunId" = 'linked-after-plan'
                WHERE lower(replace("Id", '-', '')) = '{adjustmentId:N}';
                UPDATE "BudgetAlerts"
                SET "AlertedAt" = NEW."CreatedAt"
                WHERE lower(replace("Id", '-', '')) = '{alertId:N}';
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = EnabledRule(days: 1);

        settings.Retention.AccountingMinimumDays = 30;

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains(
            "accounting-adjustment:" + adjustmentId.ToString("D"),
            plan.CandidateIds);

        Assert.Contains(
            "budget-alert:" + alertId.ToString("D"),
            plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1, await CountAllAsync("CostAdjustments"));

        Assert.Equal(1, await CountAllAsync("BudgetAlerts"));

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenStandaloneAdjustmentReappears_DoesNotAdvanceCheckpoint()
    {

        RequireSqlCipher();

        Guid adjustmentId = Guid.NewGuid();

        await ExecuteAsync(
            """
            INSERT INTO "CostAdjustments"
                ("Id", "BillableOperationId", "RunId", "AmountUsd", "Reason", "CreatedAt")
            VALUES
                (@id, NULL, NULL, -1.0, 'standalone correction', @at)
            """,
            ("@id", adjustmentId.ToString()),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            CREATE TRIGGER retain_deleted_standalone_adjustment
            AFTER DELETE ON "CostAdjustments"
            BEGIN
                INSERT INTO "CostAdjustments"
                    ("Id", "BillableOperationId", "RunId", "AmountUsd", "Reason", "CreatedAt")
                VALUES
                    (OLD."Id", OLD."BillableOperationId", OLD."RunId", OLD."AmountUsd",
                     OLD."Reason", OLD."CreatedAt");
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = EnabledRule(days: 1);

        settings.Retention.AccountingMinimumDays = 30;

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Single(plan.CandidateIds);

        Assert.Contains(
            "accounting-adjustment:" + adjustmentId.ToString("D"),
            plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.Equal(LongRunningOperationState.Failed, operation.State);

        byte[] checkpointPayload = Assert.IsType<byte[]>(operation.CheckpointPayload);

        string[] checkpointLines = Encoding.UTF8
            .GetString(checkpointPayload)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("0", checkpointLines[2]);

        Assert.Equal(1, await CountAllAsync("CostAdjustments"));

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_RollsBackEntryIndexesWhenEntryDeletionFails()
    {

        RequireSqlCipher();

        (_, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        await ExecuteAsync(
            """
            CREATE TRIGGER fail_retention_entry_delete
            BEFORE DELETE ON "Entries"
            BEGIN
                SELECT RAISE(ABORT, 'retention entry delete test');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                "DELETE FROM \"Entries\" WHERE lower(replace(\"Id\", '-', '')) = @id",
                ("@id", entryId.ToString("N"))));

        Assert.Equal(1, await CountAllAsync("Entries"));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Single(plan.CandidateIds);

        Assert.Contains(
            "entry:" + entryId.ToString("D"),
            plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, await CountAllAsync("Entries"));

        Assert.Equal(1, await CountAllAsync("entry_embeddings"));

    }

    [SkippableFact]

    public async Task ApplyAsync_ResetMemory_RollsBackScopeWhenDependentDeletionFails()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        _ = await SeedAttachmentAsync(sessionId, entryId);

        await ExecuteAsync(
            """
            CREATE TRIGGER fail_retention_chunk_delete
            BEFORE DELETE ON session_attachment_chunks
            BEGIN
                SELECT RAISE(ABORT, 'retention chunk delete test');
            END;
            """);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(
            DataRetentionOperation.ResetMemory,
            TargetId: null,
            MemoryResetScope.Attachments);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, await CountAllAsync("session_attachment_chunks"));

        Assert.Equal(1, await CountAllAsync("session_attachment_embeddings"));

    }

    [SkippableFact]

    public async Task ApplyAsync_FactoryReset_RemovesOwnedDataAndDerivedIndexesButPreservesExternalAndOperationalFiles()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        _ = await SeedAttachmentAsync(sessionId, entryId);

        string chunkId = "factory-" + Guid.NewGuid().ToString("N");

        await ExecuteAsync(
            """
            INSERT INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset,
                 CharLength, FileLastWriteTime, IndexedAt)
            VALUES
                (@id, '/workspace', 'factory.cs', 0, 'old', 0, 3, @at, @at)
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

        Guid alertId = Guid.NewGuid();

        await ExecuteAsync(
            """
            INSERT INTO "BudgetAlerts"
                ("Id", "Threshold", "AlertedAt", "SpendUsd", "DailyLimitUsd")
            VALUES
                (@id, 0.8, @at, 8.0, 10.0)
            """,
            ("@id", alertId.ToString()),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO "UnseenServantWatermarks"
                ("JobKey", "LastRunAt", "EffectiveIntervalMinutes")
            VALUES
                ('factory-reset-job', @at, 30)
            """,
            ("@at", OldTimestamp));

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation priorOperation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.WorkspaceIndex,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Completed before factory reset.",
                now.AddDays(-2)));

        LongRunningOperationLeaseResult priorLease = await operations.TryAcquireLeaseAsync(
            priorOperation.Id,
            "completed-before-factory-reset",
            now.AddDays(-2),
            now.AddDays(-2).AddMinutes(5));

        Assert.True(priorLease.Acquired);

        bool priorCompleted = await operations.TryTransitionAsync(
            priorOperation.Id,
            priorLease.Operation.Revision,
            "completed-before-factory-reset",
            LongRunningOperationState.Completed,
            now.AddDays(-2).AddMinutes(1));

        Assert.True(priorCompleted);

        string auditPath = Path.Combine(_logsRoot, "audit-20000101.jsonl");

        string operationalLogPath = Path.Combine(
            _logsRoot,
            "arcanum-api-20260802.json");

        await File.WriteAllTextAsync(auditPath, "{}\n");

        await File.WriteAllTextAsync(operationalLogPath, "{}\n");

        string externalBackup = Path.Combine(
            Directory.GetParent(_attachmentsRoot)!.FullName,
            "external-backup.arc");

        await File.WriteAllTextAsync(externalBackup, "backup");

        DataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.CostAdjustments
                && item.Rows == 1);

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.LongRunningOperations
                && item.Rows == 1);

        Assert.Contains(
            plan.Items,
            static item => item.DataClass == RetentionDataClass.DaemonExecutions
                && item.Rows == 1);

        (LongRunningOperationReconciliationSummary recovery, LongRunningOperation recoveryOperation) =
            await ReconcileFactoryResetV0Async(
                service,
                "owned-data-factory-recovery-test");

        Assert.Equal(1, recovery.Completed);

        Assert.Equal(0, recovery.RequiresAttention);

        Assert.Equal(0, await CountAllAsync("Sessions"));

        Assert.Equal(0, await CountAllAsync("Entries"));

        Assert.Equal(0, await CountAllAsync("entry_embeddings"));

        Assert.Equal(0, await CountAllAsync("workspace_file_chunks"));

        Assert.Equal(0, await CountAllAsync("workspace_file_embeddings"));

        Assert.Equal(0, await CountAllAsync("BudgetAlerts"));

        Assert.Equal(0, await CountAllAsync("UnseenServantWatermarks"));

        if (await TableExistsInTestAsync("entry_embeddings_vec"))
        {

            Assert.Equal(0, await CountAllAsync("entry_embeddings_vec"));

        }

        if (await TableExistsInTestAsync("workspace_file_embeddings_vec"))
        {

            Assert.Equal(0, await CountAllAsync("workspace_file_embeddings_vec"));

        }

        Assert.False(File.Exists(auditPath));

        Assert.True(File.Exists(operationalLogPath));

        Assert.True(File.Exists(externalBackup));

        LongRunningOperation remainingOperation = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(Limit: 10)));

        Assert.Equal(recoveryOperation.Id, remainingOperation.Id);

        Assert.NotEqual(priorOperation.Id, remainingOperation.Id);

        Assert.Equal(LongRunningOperationState.Completed, remainingOperation.State);

    }

    /// <summary>
    /// A whole-store Saga reset clears the retirement evidence and the key that made it.
    /// </summary>
    /// <remarks>
    /// The two go together. Clearing the digests alone would leave a key nothing can use, and clearing
    /// the key alone would leave rows that can never match again while still reading as evidence — an
    /// operator asking what this installation still suppresses would be told rows that suppress nothing.
    ///
    /// <para>The write that follows the reset is the half a row count cannot see: a reset that emptied
    /// <c>saga_memories</c> and left the evidence standing would report every table it named as clear,
    /// and the next extraction pass would still refuse the conclusion the operator had just asked to be
    /// forgotten, with nothing anywhere saying why.</para>
    /// </remarks>
    [SkippableFact]

    public async Task ApplyAsync_ResetMemory_Saga_ClearsItsSuppressionsAndTheKeyThatMadeThem()
    {

        RequireSqlCipher();

        const string retired = "the operator prefers tabs";

        _ = await WriteAndRetireSagaMemoryAsync(sessionId: null, retired);

        Assert.Equal(1, await CountAllAsync("saga_retirement_suppressions"));

        Assert.Equal(1, await CountAllAsync("saga_suppression_key"));

        await ApplyUntargetedResetAsync(MemoryResetScope.Saga);

        Assert.Equal(0, await CountAllAsync("saga_retirement_suppressions"));

        Assert.Equal(0, await CountAllAsync("saga_suppression_key"));

        Assert.Equal(
            SagaMemoryWriteOutcome.Written,
            await CreateSagaMemoryStore().InsertAsync(
                Guid.NewGuid().ToString(), retired, DateTimeOffset.UtcNow, sessionId: null,
                tags: null, source: "test", SagaEmbedding(), CancellationToken.None));

    }

    /// <summary>
    /// A factory reset leaves neither curation table behind.
    /// </summary>
    /// <remarks>
    /// Both are durable rows an operator's own action created, so a factory reset that returned the
    /// installation to its shipped state while keeping them would hand the next owner keyed evidence of
    /// what the last one had rejected, and a store that silently refused to record it again.
    /// </remarks>
    [SkippableFact]

    public async Task ApplyAsync_FactoryReset_LeavesNeitherCurationTableBehind()
    {

        RequireSqlCipher();

        _ = await WriteAndRetireSagaMemoryAsync(sessionId: null, "the operator prefers tabs");

        Assert.Equal(1, await CountAllAsync("saga_retirement_suppressions"));

        Assert.Equal(1, await CountAllAsync("saga_suppression_key"));

        (LongRunningOperationReconciliationSummary recovery, _) =
            await ReconcileFactoryResetV0Async(
                CreateService(),
                "curation-factory-recovery-test");

        Assert.Equal(1, recovery.Completed);

        Assert.Equal(0, recovery.RequiresAttention);

        Assert.Equal(0, await CountAllAsync("saga_retirement_suppressions"));

        Assert.Equal(0, await CountAllAsync("saga_suppression_key"));

    }

    [SkippableFact]

    public async Task ApplyAsync_FactoryReset_ErasesTapestrySummariesOfDeletedCorpora()
    {

        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        await SeedTapestryGenerationAsync(sessionId);

        Assert.Equal(1, await CountAllAsync("tapestry_generations"));

        Assert.Equal(2, await CountAllAsync("tapestry_nodes"));

        Assert.Equal(1, await CountAllAsync("tapestry_node_embeddings"));

        DataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal(
            4,
            plan.Items
                .Where(static item => item.DataClass == RetentionDataClass.Tapestry)
                .Sum(static item => item.DerivedRecords));

        (LongRunningOperationReconciliationSummary recovery, _) =
            await ReconcileFactoryResetV0Async(
                service,
                "tapestry-factory-recovery-test");

        Assert.Equal(1, recovery.Completed);

        Assert.Equal(0, recovery.RequiresAttention);

        Assert.Equal(0, await CountAllAsync("tapestry_nodes"));

        Assert.Equal(0, await CountAllAsync("tapestry_generations"));

        Assert.Equal(0, await CountAllAsync("tapestry_node_embeddings"));

        if (await TableExistsInTestAsync("tapestry_node_embeddings_vec"))
        {

            Assert.Equal(0, await CountAllAsync("tapestry_node_embeddings_vec"));

        }

    }

    [SkippableFact]

    public async Task ApplyAsync_FactoryReset_RollsBackEveryDatabaseDeletionWhenDependencyFails()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await ExecuteAsync(
            """
            CREATE TRIGGER fail_factory_session_delete
            BEFORE DELETE ON "Sessions"
            BEGIN
                SELECT RAISE(ABORT, 'factory transaction rollback test');
            END;
            """);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("Entries"));

        Assert.Equal(1, await CountAllAsync("entry_embeddings"));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

        Assert.Equal(1, await CountAllAsync("session_attachment_chunks"));

        Assert.True(File.Exists(attachment.AbsolutePath));

    }

    [SkippableFact]

    public async Task ApplyAsync_FactoryReset_WhenManagedFileIdentityChanges_PreservesDatabaseAndBytes()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        Assert.True(
            FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                attachment.AbsolutePath,
                out FileHandleMetadata originalMetadata));

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.FactoryReset);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Func<string, FileHandleMetadata?>? previousSeam =
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

        int attachmentReads = 0;

        try
        {

            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            {

                if (!string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(attachment.AbsolutePath),
                        StringComparison.Ordinal))
                {

                    return ReadActualNoFollowMetadata(path);

                }

                attachmentReads++;

                return attachmentReads == 1
                    ? originalMetadata
                    : originalMetadata with
                    {

                        Identity = new FileHandleIdentity(
                            originalMetadata.Identity.VolumeId,
                            originalMetadata.Identity.FileId + 1),

                    };

            };

            Result<DataRetentionApplyResult> result = await service.ApplyAsync(
                new DataRetentionApplyRequest(request, plan.PlanId),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(1, await CountAllAsync("Sessions"));

            Assert.Equal(1, await CountAllAsync("SessionAttachments"));

            Assert.True(File.Exists(attachment.AbsolutePath));

        }
        finally
        {

            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previousSeam;

        }

    }

    [SkippableFact]

    public async Task PlanAsync_FactoryReset_WhenManagedTreeContainsSymlink_FailsClosedWithoutFollowingTarget()
    {

        RequireSqlCipher();

        Skip.If(OperatingSystem.IsWindows(), "Symbolic-link creation requires platform privileges on Windows.");

        string externalPath = Path.Combine(
            Directory.GetParent(_attachmentsRoot)!.FullName,
            "factory-reset-external.txt");

        string linkPath = Path.Combine(
            _attachmentsRoot,
            "factory-reset-link.txt");

        await File.WriteAllTextAsync(externalPath, "preserve me");

        File.CreateSymbolicLink(linkPath, externalPath);

        try
        {

            IDataRetentionService service = CreateService();

            await Assert.ThrowsAnyAsync<IOException>(
                () => service.PlanAsync(
                    new DataRetentionRequest(DataRetentionOperation.FactoryReset),
                    CancellationToken.None));

            Assert.Equal("preserve me", await File.ReadAllTextAsync(externalPath));

        }
        finally
        {

            File.Delete(linkPath);

        }

    }

    [SkippableTheory]

    [InlineData(false)]

    [InlineData(true)]

    public async Task PlanAsync_FactoryReset_WhenManagedRootIsNotOrdinaryDirectory_FailsClosed(
        bool symbolicLink)
    {

        RequireSqlCipher();

        if (symbolicLink && OperatingSystem.IsWindows())
        {

            Skip.If(true, "Symbolic-link creation requires platform privileges on Windows.");

            return;

        }

        Directory.Delete(_attachmentsRoot);

        string externalDirectory = Path.Combine(
            Directory.GetParent(_attachmentsRoot)!.FullName,
            "factory-reset-root-target");

        if (symbolicLink)
        {

            Directory.CreateDirectory(externalDirectory);

            Directory.CreateSymbolicLink(_attachmentsRoot, externalDirectory);

        }
        else
        {

            await File.WriteAllTextAsync(_attachmentsRoot, "wrong kind");

        }

        try
        {

            IDataRetentionService service = CreateService();

            await Assert.ThrowsAnyAsync<IOException>(
                () => service.PlanAsync(
                    new DataRetentionRequest(DataRetentionOperation.FactoryReset),
                    CancellationToken.None));

        }
        finally
        {

            if (symbolicLink)
            {

                Directory.Delete(_attachmentsRoot);

            }
            else
            {

                File.Delete(_attachmentsRoot);

            }

            Directory.CreateDirectory(_attachmentsRoot);

        }

    }

    [SkippableFact]

    public async Task PlanAsync_FactoryReset_WhenManagedDirectoryIsInaccessible_FailsClosedAndPreservesData()
    {

        RequireSqlCipher();

        if (OperatingSystem.IsWindows())
        {

            Skip.If(true, "Unix permission behavior is required by this test.");

            return;

        }

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        string attachmentDirectory = Path.GetDirectoryName(attachment.AbsolutePath)!;

        UnixFileMode originalMode = File.GetUnixFileMode(attachmentDirectory);

        File.SetUnixFileMode(attachmentDirectory, UnixFileMode.None);

        try
        {

            IDataRetentionService service = CreateService();

            await Assert.ThrowsAnyAsync<IOException>(
                () => service.PlanAsync(
                    new DataRetentionRequest(DataRetentionOperation.FactoryReset),
                    CancellationToken.None));

            Assert.Equal(1, await CountAllAsync("Sessions"));

            Assert.Equal(1, await CountAllAsync("SessionAttachments"));

        }
        finally
        {

            File.SetUnixFileMode(attachmentDirectory, originalMode);

        }

        Assert.True(File.Exists(attachment.AbsolutePath));

    }

    [SkippableFact]

    public async Task PersistAttachment_WhenFactoryResetWinsBeforeRowInsert_DoesNotPublishMissingBytes()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SessionAttachmentStore attachments = new(
            _db!,
            Options.Create(new ArcanumSettings()),
            _attachmentsRoot,
            new EncryptedBlobStore(
                new RetentionTestFileEncryptionKeyProvider(),
                new EncryptedBlobStoreOptions { ChunkSize = 64 }));

        DataRetentionService retention = CreateService();

        LongRunningOperationReconciliationSummary? reset = null;

        attachments.AfterBytesCommittedBeforeDbForTesting = async cancellationToken =>
        {

            (reset, _) = await ReconcileFactoryResetV0Async(
                retention,
                "attachment-race-factory-recovery-test");

            Assert.Equal(1, reset.Completed);

            Assert.Equal(0, reset.RequiresAttention);

        };

        await Assert.ThrowsAnyAsync<IOException>(
            () => attachments.PersistNewAsync(
                sessionId,
                pendingTurnId: null,
                entryId,
                "factory-race.txt",
                "factory-race.txt",
                Encoding.UTF8.GetBytes("factory reset race"),
                "text/plain",
                SessionAttachmentKind.Text));

        Assert.NotNull(reset);

        Assert.Equal(1, reset!.Completed);

        Assert.Equal(0, await CountAllAsync("Sessions"));

        Assert.Equal(0, await CountAllAsync("SessionAttachments"));

        Assert.Empty(
            Directory.EnumerateFiles(
                _attachmentsRoot,
                "*",
                SearchOption.AllDirectories));

    }

    [SkippableFact]

    public async Task PersistAttachment_WhenOwnedBlobIsReplacedBeforeRowInsert_PreservesReplacement()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SessionAttachmentStore attachments = new(
            _db!,
            Options.Create(new ArcanumSettings()),
            _attachmentsRoot,
            new EncryptedBlobStore(
                new RetentionTestFileEncryptionKeyProvider(),
                new EncryptedBlobStoreOptions { ChunkSize = 64 }));

        byte[] replacement = Encoding.UTF8.GetBytes("replacement owned by another actor");

        string? attachmentPath = null;

        attachments.AfterBytesCommittedBeforeDbForTesting = async _ =>
        {

            attachmentPath = Assert.Single(
                Directory.EnumerateFiles(
                    _attachmentsRoot,
                    "*",
                    SearchOption.AllDirectories));

            File.Delete(attachmentPath);

            await File.WriteAllBytesAsync(attachmentPath, replacement);

        };

        await Assert.ThrowsAnyAsync<IOException>(
            () => attachments.PersistNewAsync(
                sessionId,
                pendingTurnId: null,
                entryId,
                "replacement-race.txt",
                "replacement-race.txt",
                Encoding.UTF8.GetBytes("original encrypted bytes"),
                "text/plain",
                SessionAttachmentKind.Text));

        Assert.NotNull(attachmentPath);

        Assert.Equal(replacement, await File.ReadAllBytesAsync(attachmentPath));

        Assert.Equal(0, await CountAllAsync("SessionAttachments"));

    }

    [SkippableFact]

    public async Task ApplyAsync_FactoryReset_WhenPostCommitFinalizationFails_RemainsRecoverableAndRetries()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        DataRetentionService service = CreateService();

        Func<string, FileHandleMetadata?>? previousSeam =
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

        int quarantinedFileReads = 0;

        LongRunningOperationReconciliationSummary? firstRecovery = null;

        try
        {

            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            {

                string? parentName = Path.GetFileName(Path.GetDirectoryName(path));

                if (string.Equals(
                        Path.GetFileName(path),
                        Path.GetFileName(attachment.AbsolutePath),
                        StringComparison.Ordinal)
                    && parentName?.StartsWith(
                        ".arcanum-cleanup-",
                        StringComparison.Ordinal) == true
                    && ++quarantinedFileReads >= 2)
                {

                    return null;

                }

                return ReadActualNoFollowMetadata(path);

            };

            (firstRecovery, _) = await ReconcileFactoryResetV0Async(
                service,
                "factory-finalizer-first-recovery-test");

            Assert.Equal(0, firstRecovery.Completed);

            Assert.Equal(1, firstRecovery.RequiresAttention);

        }
        finally
        {

            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previousSeam;

        }

        Assert.Equal(0, await CountAllAsync("Sessions"));

        Assert.NotNull(firstRecovery);

        Assert.Equal(0, await CountAllAsync("SessionAttachments"));

        Assert.False(File.Exists(attachment.AbsolutePath));

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation pending = Assert.Single(
            await operations.ListAsync(new LongRunningOperationQuery(Limit: 10)),
            static operation =>
                operation.Kind == LongRunningOperationKinds.DataRetentionFactoryReset);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, pending.State);

        LongRunningOperationReconciler reconciler = new(
            operations,
            [new DataRetentionFactoryResetRecoveryHandler(service)],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance);

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync(
            "factory-finalizer-recovery-test",
            maxOperations: 10,
            maxConcurrency: 1,
            CancellationToken.None);

        Assert.Equal(1, summary.Completed);

        Assert.Equal(0, summary.RequiresAttention);

        LongRunningOperation completed = Assert.IsType<LongRunningOperation>(
            await operations.GetAsync(pending.Id));

        Assert.Equal(LongRunningOperationState.Completed, completed.State);

        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                _attachmentsRoot,
                "*",
                SearchOption.AllDirectories),
            path => path.Contains(".arcanum-cleanup-", StringComparison.Ordinal));

    }

    [SkippableFact]

    public async Task RecoverFactoryResetAsync_RerunsInterruptedCleanupAndReconcilesOwnMarker()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        DataRetentionService service = CreateService();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionFactoryReset,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Interrupted factory reset.",
                startedAt));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            "crashed-factory-owner",
            startedAt,
            startedAt.AddMinutes(1));

        Assert.True(lease.Acquired);

        LongRunningOperationReconciler reconciler = new(
            operations,
            [new DataRetentionFactoryResetRecoveryHandler(service)],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance);

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync(
            "factory-recovery-test",
            maxOperations: 10,
            maxConcurrency: 1,
            CancellationToken.None);

        Assert.Equal(1, summary.Completed);

        Assert.Equal(0, summary.RequiresAttention);

        Assert.Equal(0, await CountAllAsync("Sessions"));

        Assert.Equal(0, await CountAllAsync("Entries"));

        Assert.Equal(0, await CountAllAsync("SessionAttachments"));

        Assert.False(File.Exists(attachment.AbsolutePath));

        LongRunningOperation recovered = Assert.IsType<LongRunningOperation>(
            await operations.GetAsync(operation.Id));

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactoryReset_lost_owner_cannot_renew_or_delete_for_the_new_owner(bool expired)
    {

        RequireSqlCipher();

        _ = await SeedSessionAsync(pinned: false);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        FakeTimeProvider clock = new();

        DateTimeOffset now = clock.GetUtcNow();

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionFactoryReset,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Factory owner-loss guard.",
                now));

        const string formerOwner = "factory-former-owner";

        LongRunningOperationLeaseResult firstLease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            formerOwner,
            now,
            now.AddMinutes(5));

        Assert.True(firstLease.Acquired);

        LongRunningOperation stale = firstLease.Operation;

        LongRunningOperation before;

        if (expired)
        {

            clock.Advance(TimeSpan.FromMinutes(6));

            before = (await operations.GetAsync(operation.Id))!;

        }
        else
        {

            Assert.True(await operations.TryTransitionAsync(
                stale.Id,
                stale.Revision,
                formerOwner,
                LongRunningOperationState.ReconciliationRequired,
                now,
                ErrorCodes.Covenant.MaintenanceFailed));

            LongRunningOperationLeaseResult adopted = await operations.TryAcquireLeaseAsync(
                operation.Id,
                "factory-adopted-owner",
                now.AddSeconds(1),
                now.AddMinutes(6));

            Assert.True(adopted.Acquired);

            before = adopted.Operation;

        }

        LongRunningOperationRecoveryResult result = await CreateService(
            timeProvider: clock,
            operationStore: operations).RecoverFactoryResetAsync(
                stale,
                CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, result.ErrorCode);

        Assert.Equal(1, await _db!.Sessions.LongCountAsync());

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(
            await operations.GetAsync(operation.Id));

        Assert.Equal(before.LeaseOwner, after.LeaseOwner);

        Assert.Equal(before.Revision, after.Revision);

        Assert.Equal(before.LeaseExpiresAt, after.LeaseExpiresAt);

    }

    /// <summary>
    /// A pin has to reach both halves of retention. Planning stops selecting the memory, and the apply
    /// that follows leaves it where it is: a planner and an executor that disagree is how a pinned
    /// memory gets deleted with nothing to show for it.
    /// </summary>
    [SkippableFact]

    public async Task PlanAndApplyAsync_Prune_WhenAMemoryIsPinned_LeavesItAndReportsWhatThePinExempted()
    {

        RequireSqlCipher();

        string pinnedId = await SeedAgedSagaMemoryAsync("pinned, and old");

        string prunableId = await SeedAgedSagaMemoryAsync("unpinned, and old");

        // Through the store rather than through a statement the test composed itself: what retention
        // has to honour is the column the production pin writes.
        SagaCurationOutcome outcome = await CreateSagaMemoryStore().SetPinAsync(
            pinnedId,
            pinned: true,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SagaMemories = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.DoesNotContain("saga:" + pinnedId, plan.CandidateIds);

        Assert.Equal("saga:" + prunableId, Assert.Single(plan.CandidateIds));

        // A dry-run that silently omitted the exempted row would tell an operator their rule reaches
        // further than it does.
        Assert.NotNull(plan.SagaCuration);

        Assert.Equal(1, plan.SagaCuration.PinnedRows);

        Assert.Equal(1, plan.SagaCuration.PinnedRowsExemptFromPlan);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(1, await CountAsync("saga_memories", "Id", pinnedId));

        Assert.Equal(1, await CountAsync("saga_memory_embeddings", "MemoryId", pinnedId));

        Assert.Equal(0, await CountAsync("saga_memories", "Id", prunableId));

        Assert.Equal(0, await CountAsync("saga_memory_embeddings", "MemoryId", prunableId));

    }

    /// <summary>
    /// The window a pin has to survive is inside apply itself, which rebuilds the plan before it starts
    /// deleting. A pin taken before that rebuild changes the plan's identity and is refused as a stale
    /// preview, which proves nothing about the delete — so this one lands from a trigger on the
    /// operation row apply writes for itself, the first moment after the rebuild.
    /// </summary>
    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenAPinLandsAfterTheApplyPlanIsBuilt_PreservesTheMemory()
    {

        RequireSqlCipher();

        string memoryId = await SeedAgedSagaMemoryAsync("old, pinned between plan and apply");

        await ExecuteAsync(
            $"""
            CREATE TRIGGER pin_saga_memory_after_prune_start
            AFTER INSERT ON LongRunningOperations
            WHEN NEW.Kind = '{LongRunningOperationKinds.DataRetentionPrune}'
            BEGIN
                UPDATE saga_memories
                SET PinnedAtUtc = '{OldTimestamp}'
                WHERE Id = '{memoryId}';
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SagaMemories = EnabledRule();

        IDataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.Equal("saga:" + memoryId, Assert.Single(plan.CandidateIds));

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(1, await CountAsync("saga_memories", "Id", memoryId));

        // The delete removes the embedding before it reaches the memory row, so the embedding surviving
        // is what says the refusal rolled the whole transaction back rather than half of it.
        Assert.Equal(1, await CountAsync("saga_memory_embeddings", "MemoryId", memoryId));

        Assert.Equal(0, applied.Value.RowsDeleted);

        Assert.Contains(
            applied.Value.Conflicts,
            conflict => conflict.Code == ErrorCodes.Data.PlanChanged
                && conflict.ResourceId == "saga:" + memoryId);

    }

    /// <summary>
    /// One Saga memory older than an enabled rule's cutoff, with the embedding a prune takes with it.
    /// </summary>
    private async Task<string> SeedAgedSagaMemoryAsync(string content)
    {

        string memoryId = "curation-" + Guid.NewGuid().ToString("N");

        await ExecuteAsync(
            """
            INSERT INTO saga_memories (Id, Content, CreatedAt, SessionId, Tags, Source)
            VALUES (@id, @content, @at, NULL, NULL, 'test')
            """,
            ("@id", memoryId),
            ("@content", content),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO saga_memory_embeddings (MemoryId, Embedding, Dim)
            VALUES (@id, @embedding, 1)
            """,
            ("@id", memoryId),
            ("@embedding", new byte[] { 0, 0, 128, 63 }));

        return memoryId;

    }

    private DataRetentionService CreateService(
        ArcanumSettings? settings = null,
        IDataRetentionPolicyStore? policyStore = null,
        TimeProvider? timeProvider = null,
        ILongRunningOperationStore? operationStore = null,
        ILogger<DataRetentionService>? logger = null,
        CovenantErasureCoordinator? erasureCoordinator = null,
        DataRetentionLeaseMaintainer? leaseMaintainer = null)
    {

        ILongRunningOperationStore operations = operationStore
            ?? new LongRunningOperationStore(
                _db!,
                TestOrdinaryConnectionFactory.For(_db!));

        return new DataRetentionService(
            _db!,
            new TestOptionsMonitor<ArcanumSettings>(settings ?? new ArcanumSettings()),
            operations,
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<DataRetentionService>.Instance,
            FixtureLabeledArtifactGuard.For(_db!),
            _attachmentsRoot,
            _filesRoot,
            _logsRoot,
            policyStore,
            covenantErasureCoordinator: erasureCoordinator,
            leaseMaintainer: leaseMaintainer);

    }

    private async Task<(
        LongRunningOperationReconciliationSummary Summary,
        LongRunningOperation Operation)> ReconcileFactoryResetV0Async(
            DataRetentionService service,
            string workerId)
    {

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionFactoryReset,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Interrupted version-0 factory reset.",
                startedAt));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            "crashed-factory-owner",
            startedAt,
            startedAt.AddMinutes(1));

        Assert.True(lease.Acquired);

        LongRunningOperationReconciler reconciler = new(
            operations,
            [new DataRetentionFactoryResetRecoveryHandler(service)],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance);

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync(
            workerId,
            maxOperations: 10,
            maxConcurrency: 1,
            CancellationToken.None);

        return (
            summary,
            Assert.IsType<LongRunningOperation>(await operations.GetAsync(operation.Id)));

    }

    private const string OldTimestamp =
        "2000-01-01T00:00:00.0000000+00:00";

    /// <summary>
    /// Mirrors the service's own private <c>PruneCheckpointInterval</c>; a sweep shorter than this
    /// never reaches a checkpoint boundary and so proves nothing about periodic lease renewal.
    /// </summary>
    private const int PruneCheckpointIntervalInTest = 50;

    private static RetentionRuleSettings EnabledRule(int days = 1) =>
        new()
        {

            Enabled = true,

            Days = days,

        };

    private static RetentionRuleSettings DisabledRule() =>
        new()
        {

            Enabled = false,

            Days = 30,

        };

    private static ArcanumSettings CreatePruneSettings() =>
        new()
        {

            Retention = new RetentionSettings
            {

                AutomaticSweepsEnabled = false,

                ActiveSessions = DisabledRule(),

                ArchivedSessions = DisabledRule(),

                Entries = DisabledRule(),

                Attachments = DisabledRule(),

                UploadedFiles = DisabledRule(),

                CompletedBatches = DisabledRule(),

                SagaMemories = DisabledRule(),

                LexiconEntries = DisabledRule(),

                WorkspaceIndexes = DisabledRule(),

                SessionEntryEmbeddings = DisabledRule(),

                AuditLogs = DisabledRule(),

                GuardrailLogs = DisabledRule(),

                IdempotencyClaims = DisabledRule(),

                Accounting = DisabledRule(),

                LongRunningOperations = DisabledRule(),

                SanctumBreaches = DisabledRule(),

                DaemonHistory = DisabledRule(),

            },

        };

    private async Task<(ArcanumSettings Settings, string ExpectedCandidate)>
        SeedStarvationScenarioAsync(string scenario)
    {

        const string blockedAt = "1999-01-01T00:00:00.0000000+00:00";

        const string eligibleAt = "2000-01-01T00:00:00.0000000+00:00";

        ArcanumSettings settings = CreatePruneSettings();

        switch (scenario)
        {

            case "session":
            {

                (Guid blockedSessionId, _) = await SeedSessionAsync(pinned: true);

                (Guid eligibleSessionId, _) = await SeedSessionAsync(pinned: false);

                await ExecuteAsync(
                    "UPDATE \"Sessions\" SET \"UpdatedAt\" = @at WHERE lower(replace(\"Id\", '-', '')) = @id",
                    ("@at", blockedAt),
                    ("@id", blockedSessionId.ToString("N")));

                await ExecuteAsync(
                    "UPDATE \"Sessions\" SET \"UpdatedAt\" = @at WHERE lower(replace(\"Id\", '-', '')) = @id",
                    ("@at", eligibleAt),
                    ("@id", eligibleSessionId.ToString("N")));

                settings.Retention.ArchivedSessions = EnabledRule();

                return (
                    settings,
                    "session:" + eligibleSessionId.ToString("D"));

            }

            case "batch":
            {

                Guid blockedFileId = Guid.NewGuid();

                Guid eligibleFileId = Guid.NewGuid();

                Guid blockedBatchId = Guid.NewGuid();

                Guid eligibleBatchId = Guid.NewGuid();

                await SeedUploadedFileAsync(blockedFileId, 0);

                await SeedUploadedFileAsync(eligibleFileId, 0);

                await ExecuteAsync(
                    """
                    INSERT INTO "Batches"
                        ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt")
                    VALUES
                        (@id, @fileId, '/v1/chat/completions', @status, @at)
                    """,
                    ("@id", blockedBatchId.ToString()),
                    ("@fileId", blockedFileId.ToString()),
                    ("@status", BatchStatuses.InProgress),
                    ("@at", blockedAt));

                await ExecuteAsync(
                    """
                    INSERT INTO "Batches"
                        ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt")
                    VALUES
                        (@id, @fileId, '/v1/chat/completions', @status, @at, @at)
                    """,
                    ("@id", eligibleBatchId.ToString()),
                    ("@fileId", eligibleFileId.ToString()),
                    ("@status", BatchStatuses.Completed),
                    ("@at", eligibleAt));

                settings.Retention.CompletedBatches = EnabledRule();

                return (
                    settings,
                    "batch:" + eligibleBatchId.ToString("D"));

            }

            case "entry":
            {

                (_, Guid blockedEntryId) = await SeedSessionAsync(pinned: true);

                (_, Guid eligibleEntryId) = await SeedSessionAsync(pinned: false);

                await ExecuteAsync(
                    "UPDATE \"Entries\" SET \"CreatedAt\" = @at WHERE lower(replace(\"Id\", '-', '')) = @id",
                    ("@at", blockedAt),
                    ("@id", blockedEntryId.ToString("N")));

                await ExecuteAsync(
                    "UPDATE \"Entries\" SET \"CreatedAt\" = @at WHERE lower(replace(\"Id\", '-', '')) = @id",
                    ("@at", eligibleAt),
                    ("@id", eligibleEntryId.ToString("N")));

                settings.Retention.Entries = EnabledRule();

                return (
                    settings,
                    "entry:" + eligibleEntryId.ToString("D"));

            }

            case "idempotency":
            {

                Guid blockedClaimId = Guid.NewGuid();

                Guid eligibleClaimId = Guid.NewGuid();

                await ExecuteAsync(
                    """
                    INSERT INTO "IdempotencyClaims"
                        ("Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId",
                         "LeaseExpiresAt", "HeartbeatAt", "TerminalStreamComplete", "CreatedAt", "UpdatedAt")
                    VALUES
                        (@id, @key, 'blocked-fingerprint', @state, 'blocked-owner',
                         @at, @at, 0, @at, @at)
                    """,
                    ("@id", blockedClaimId.ToString()),
                    ("@key", "blocked-" + blockedClaimId.ToString("N")),
                    ("@state", (int)IdempotencyClaimState.Running),
                    ("@at", blockedAt));

                await ExecuteAsync(
                    """
                    INSERT INTO "IdempotencyClaims"
                        ("Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId",
                         "LeaseExpiresAt", "HeartbeatAt", "TerminalStreamComplete", "CreatedAt", "UpdatedAt")
                    VALUES
                        (@id, @key, 'eligible-fingerprint', @state, 'eligible-owner',
                         @at, @at, 1, @at, @at)
                    """,
                    ("@id", eligibleClaimId.ToString()),
                    ("@key", "eligible-" + eligibleClaimId.ToString("N")),
                    ("@state", (int)IdempotencyClaimState.Completed),
                    ("@at", eligibleAt));

                settings.Retention.IdempotencyClaims = EnabledRule();

                return (
                    settings,
                    "idempotency-claim:" + eligibleClaimId.ToString("D"));

            }

            case "accounting":
            {

                (Guid retainedSessionId, _) = await SeedSessionAsync(pinned: false);

                Guid blockedRunId = Guid.NewGuid();

                Guid eligibleRunId = Guid.NewGuid();

                await SeedCompletedInferenceRunAsync(
                    blockedRunId,
                    retainedSessionId);

                await SeedCompletedInferenceRunAsync(
                    eligibleRunId,
                    null);

                await ExecuteAsync(
                    "UPDATE \"InferenceRuns\" SET \"StartedAt\" = @at, \"CompletedAt\" = @at WHERE lower(replace(\"Id\", '-', '')) = @id",
                    ("@at", blockedAt),
                    ("@id", blockedRunId.ToString("N")));

                await ExecuteAsync(
                    "UPDATE \"InferenceRuns\" SET \"StartedAt\" = @at, \"CompletedAt\" = @at WHERE lower(replace(\"Id\", '-', '')) = @id",
                    ("@at", eligibleAt),
                    ("@id", eligibleRunId.ToString("N")));

                settings.Retention.Accounting = EnabledRule();

                settings.Retention.AccountingMinimumDays = 1;

                return (
                    settings,
                    "accounting:" + eligibleRunId.ToString("D"));

            }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(scenario),
                    scenario,
                    "Unknown starvation scenario.");

        }

    }

    private async Task<(Guid SessionId, Guid EntryId)> SeedSessionAsync(
        bool pinned)
    {

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        DateTimeOffset createdAt = DateTimeOffset.Parse(
            "2000-01-01T00:00:00Z",
            CultureInfo.InvariantCulture);

        _db!.Sessions.Add(
            new Session
            {

                Id = sessionId,

                Status = "archived",

                CreatedAt = createdAt,

                UpdatedAt = createdAt,

            });

        _db.Entries.Add(
            new Entry
            {

                Id = entryId,

                SessionId = sessionId,

                Role = MessageRole.User,

                Content = "Retain this test entry.",

                ModelUsed = "test-model",

                CreatedAt = createdAt,

                Sequence = 1,

                IsPinned = pinned,

            });

        await _db.SaveChangesAsync();

        return (sessionId, entryId);

    }

    private Task SeedUploadedFileAsync(Guid fileId, long bytes) =>
        ExecuteAsync(
            """
            INSERT INTO "UploadedFiles"
                ("Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt")
            VALUES
                (@id, 'retention.jsonl', @bytes, 'batch', 'application/jsonl', @createdAt)
            """,
            ("@id", fileId.ToString()),
            ("@bytes", bytes),
            ("@createdAt", OldTimestamp));

    private Task SeedCompletedInferenceRunAsync(
        Guid runId,
        Guid? sessionId) =>
        ExecuteAsync(
            """
            INSERT INTO "InferenceRuns"
                ("Id", "RequestId", "SessionId", "Surface", "Purpose", "StartedAt",
                 "CompletedAt", "Status")
            VALUES
                (@id, @requestId, @sessionId, 'test', 'retention-test', @at, @at, @status)
            """,
            ("@id", runId.ToString()),
            ("@requestId", "request-" + runId.ToString("N")),
            ("@sessionId", sessionId is Guid value
                ? (object)value.ToString()
                : DBNull.Value),
            ("@at", OldTimestamp),
            ("@status", (int)InferenceRunStatus.Completed));

    private Task SeedEntryEmbeddingAsync(Guid entryId) =>
        ExecuteAsync(
            """
            INSERT INTO entry_embeddings (EntryId, Embedding, Dim)
            VALUES (@entryId, @embedding, 1)
            """,
            ("@entryId", Canonical(entryId)),
            ("@embedding", new byte[] { 0, 0, 128, 63 }));

    /// <summary>
    /// Seeds one archived session carrying <paramref name="count"/> embedded Entries, which is
    /// <paramref name="count"/> independent <c>entry-embedding:</c> prune candidates.
    /// </summary>
    private async Task<Guid[]> SeedEntryEmbeddingBatchAsync(int count)
    {

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset createdAt = DateTimeOffset.Parse(
            "2000-01-01T00:00:00Z",
            CultureInfo.InvariantCulture);

        _db!.Sessions.Add(
            new Session
            {

                Id = sessionId,

                Status = "archived",

                CreatedAt = createdAt,

                UpdatedAt = createdAt,

            });

        Guid[] entryIds = new Guid[count];

        for (int index = 0; index < count; index++)
        {

            entryIds[index] = Guid.NewGuid();

            _db.Entries.Add(
                new Entry
                {

                    Id = entryIds[index],

                    SessionId = sessionId,

                    Role = MessageRole.User,

                    Content = "Retain this test entry.",

                    ModelUsed = "test-model",

                    CreatedAt = createdAt,

                    Sequence = index + 1,

                    IsPinned = false,

                });

        }

        await _db.SaveChangesAsync();

        foreach (Guid entryId in entryIds)
        {

            await SeedEntryEmbeddingAsync(entryId);

        }

        return entryIds;

    }

    /// <summary>
    /// Seeds one published Tapestry generation over a session scope: a null-content leaf plus a
    /// layer-1 summary whose <c>Content</c> is model prose about the transcript, and the summary's
    /// embedding blob.
    /// </summary>
    private async Task SeedTapestryGenerationAsync(Guid sessionId)
    {

        string generationId = "generation-" + Guid.NewGuid().ToString("N");

        string leafNodeId = "leaf-" + Guid.NewGuid().ToString("N");

        string summaryNodeId = "summary-" + Guid.NewGuid().ToString("N");

        await ExecuteAsync(
            """
            INSERT INTO tapestry_generations
                (GenerationId, ScopeKind, ScopeId, Status, AlgorithmVersion,
                 SettingsFingerprint, SummaryModel, SummaryRecipeVersion, EmbeddingDimension,
                 CorpusFingerprint, LayerCount, NodeCount, RootNodeCount, StartedAt, CompletedAt)
            VALUES
                (@generationId, 'Session', @scopeId, 'Published', '1', 'FINGERPRINT',
                 'test-model', '1', 1, 'CORPUS', 2, 2, 1, @at, @at)
            """,
            ("@generationId", generationId),
            ("@scopeId", sessionId.ToString("N")),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO tapestry_nodes
                (NodeId, GenerationId, ScopeKind, ScopeId, Layer, ParentScopeKey, NodeKind,
                 ParentNodeId, SourceKind, SourceId, SourceLabel, Content, ContentHash,
                 EmbeddingDimension, CreatedAt)
            VALUES
                (@leafNodeId, @generationId, 'Session', @scopeId, 0, @scopeId, 'Leaf',
                 @summaryNodeId, 'Entry', @scopeId, 'Transcript entry', NULL, 'LEAF-HASH',
                 1, @at),
                (@summaryNodeId, @generationId, 'Session', @scopeId, 1, @scopeId, 'Summary',
                 NULL, NULL, NULL, 'Session summary',
                 'The operator described their medical history in detail.', 'SUMMARY-HASH',
                 1, @at)
            """,
            ("@leafNodeId", leafNodeId),
            ("@summaryNodeId", summaryNodeId),
            ("@generationId", generationId),
            ("@scopeId", sessionId.ToString("N")),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO tapestry_node_embeddings (NodeId, Embedding, Dim)
            VALUES (@nodeId, @embedding, 1)
            """,
            ("@nodeId", summaryNodeId),
            ("@embedding", new byte[] { 0, 0, 128, 63 }));

    }

    /// <summary>
    /// The spelling every writer of these identity columns renders: uppercase, dashed, 36 characters.
    /// </summary>
    /// <remarks>
    /// The Session and the Entry are written by the object-relational writer, and the attachment family
    /// by the attachment store and its index repository - all of which render the canonical form, and the
    /// SQLite value binder uppercases a raw Guid unconditionally. A bare <c>ToString()</c> here seeded a
    /// pairing no installation holds: an embedding that its own Entry's join would miss, and an
    /// attachment whose Session no session-scoped sweep would find.
    /// </remarks>
    private static string Canonical(Guid identity) => identity.ToString("D").ToUpperInvariant();

    private async Task<SeededAttachment> SeedAttachmentAsync(
        Guid sessionId,
        Guid entryId)
    {

        Guid attachmentId = Guid.NewGuid();

        string chunkId = "chunk-" + Guid.NewGuid().ToString("N");

        byte[] bytes = [9, 8, 7, 6, 5, 4];

        string relativePath = Path.Combine(
            sessionId.ToString("N"),
            attachmentId.ToString("N") + ".bin");

        string absolutePath = Path.Combine(
            _attachmentsRoot,
            relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        await File.WriteAllBytesAsync(absolutePath, bytes);

        await ExecuteAsync(
            """
            INSERT INTO "SessionAttachments"
                ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey",
                 "OriginalFileName", "Version", "RelativePath", "ContentSha256", "MimeType",
                 "ByteLength", "Kind", "CreatedAt")
            VALUES
                (@id, @sessionId, @entryId, NULL, 'Bound', 'evidence', 'evidence.txt', 1,
                 @relativePath, 'ATTACHMENT-HASH', 'text/plain', @byteLength, 'Text', @createdAt)
            """,
            ("@id", Canonical(attachmentId)),
            ("@sessionId", Canonical(sessionId)),
            ("@entryId", Canonical(entryId)),
            ("@relativePath", relativePath),
            ("@byteLength", bytes.Length),
            ("@createdAt", "2000-01-01T00:00:00.0000000+00:00"));

        await ExecuteAsync(
            """
            INSERT INTO session_attachment_chunks
                (ChunkId, SessionId, AttachmentId, LogicalKey, Version, OriginalFileName, MimeType,
                 ContentSha256, ChunkIndex, CharacterStart, CharacterEnd, StartLine, EndLine, Content,
                 EmbeddingDimension, ExtractedAt, IndexedAt, RetrievalScope)
            VALUES
                (@chunkId, @sessionId, @attachmentId, 'evidence', 1, 'evidence.txt', 'text/plain',
                 'ATTACHMENT-HASH', 0, 0, 8, 1, 1, 'evidence', 1, @at, @at, 'Latest')
            """,
            ("@chunkId", chunkId),
            // Not Canonical, and that is the one deliberate exception in this seed:
            // session_attachment_chunks.SessionId is ruled to stay in the minority spelling, because the
            // tapestry reads it as its live scope-id set and moving it would orphan every
            // attachment-scoped generation. Its AttachmentId does move, under a foreign key to the
            // parent this seed just wrote.
            ("@sessionId", sessionId.ToString()),
            ("@attachmentId", Canonical(attachmentId)),
            ("@at", "2000-01-01T00:00:00.0000000+00:00"));

        await ExecuteAsync(
            """
            INSERT INTO session_attachment_embeddings (ChunkId, Embedding, Dim)
            VALUES (@chunkId, @embedding, 1)
            """,
            ("@chunkId", chunkId),
            ("@embedding", new byte[] { 0, 0, 128, 63 }));

        await ExecuteAsync(
            """
            INSERT INTO session_attachment_index_state
                (AttachmentId, Status, ContentSha256, AttemptCount, UpdatedAt)
            VALUES
                (@attachmentId, 'Indexed', 'ATTACHMENT-HASH', 1, @at)
            """,
            ("@attachmentId", Canonical(attachmentId)),
            ("@at", "2000-01-01T00:00:00.0000000+00:00"));

        return new SeededAttachment(
            attachmentId,
            chunkId,
            bytes,
            absolutePath);

    }

    private async Task SeedSagaAndLexiconProvenanceAsync(
        Guid sessionId,
        Guid attachmentId)
    {

        await ExecuteAsync(
            """
            INSERT INTO saga_memories (Id, Content, CreatedAt, SessionId, Tags, Source)
            VALUES ('memory-retained', 'Remembered fact', @at, @sessionId, NULL, 'attachment')
            """,
            ("@at", "2000-01-01T00:00:00.0000000+00:00"),
            ("@sessionId", sessionId.ToString()));

        await ExecuteAsync(
            """
            INSERT INTO saga_memory_embeddings (MemoryId, Embedding, Dim)
            VALUES ('memory-retained', @embedding, 1)
            """,
            ("@embedding", new byte[] { 0, 0, 128, 63 }));

        await ExecuteAsync(
            """
            INSERT INTO saga_memory_attachment_provenance
                (MemoryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash,
                 MaterializedAt, SourceType)
            VALUES
                ('memory-retained', @sessionId, @attachmentId, 'evidence', 1, 'ATTACHMENT-HASH',
                 @at, 'WorkspaceFile')
            """,
            ("@sessionId", sessionId.ToString()),
            ("@attachmentId", Canonical(attachmentId)),
            ("@at", "2000-01-01T00:00:00.0000000+00:00"));

        await ExecuteAsync(
            """
            INSERT INTO lexicon_entries
                (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
            VALUES
                ('lexicon-retained', 'Retained', 'retained', 'concept',
                 '["retained fact"]', 'retained fact', @at)
            """,
            ("@at", "2000-01-01T00:00:00.0000000+00:00"));

        await ExecuteAsync(
            """
            INSERT INTO lexicon_fact_attachment_provenance
                (EntryId, FactHash, Fact, SessionId, AttachmentId, LogicalKey, Version,
                 ContentHash, MaterializedAt, SourceType)
            VALUES
                ('lexicon-retained', 'FACT-HASH', 'retained fact', @sessionId, @attachmentId,
                 'evidence', 1, 'ATTACHMENT-HASH', @at, 'WorkspaceFile')
            """,
            ("@sessionId", sessionId.ToString()),
            ("@attachmentId", Canonical(attachmentId)),
            ("@at", "2000-01-01T00:00:00.0000000+00:00"));

    }

    private async Task<int> ReadAttachmentAvailabilityAsync(
        string provenanceTable)
    {

        SqliteConnection connection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT EXISTS(
                SELECT 1
                FROM "SessionAttachments" attachment
                WHERE attachment."Id" = provenance.AttachmentId
                  AND attachment."State" = 'Bound')
            FROM "{provenanceTable}" provenance
            LIMIT 1
            """;

        object? value = await command.ExecuteScalarAsync();

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);

    }

    private async Task<int> CountAllAsync(string table)
    {

        SqliteConnection connection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";

        object? value = await command.ExecuteScalarAsync();

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);

    }

    private async Task<Guid> ReadEntrySessionIdAsync(Guid entryId)
    {

        SqliteConnection connection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT "SessionId"
            FROM "Entries"
            WHERE lower(replace("Id", '-', '')) = @id
            LIMIT 1
            """;

        _ = command.Parameters.AddWithValue("@id", entryId.ToString("N"));

        object? value = await command.ExecuteScalarAsync();

        return Guid.Parse(
            Convert.ToString(value, CultureInfo.InvariantCulture)!,
            CultureInfo.InvariantCulture);

    }

    private async Task<bool> TableExistsInTestAsync(string table)
    {

        SqliteConnection connection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name = @name)";

        command.Parameters.AddWithValue("@name", table);

        object? value = await command.ExecuteScalarAsync();

        return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;

    }

    private async Task<int> CountAsync(
        string table,
        string column,
        string value)
    {

        SqliteConnection connection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT COUNT(*)
            FROM "{table}"
            WHERE "{column}" = @value
            """;

        command.Parameters.AddWithValue("@value", value);

        object? count = await command.ExecuteScalarAsync();

        return Convert.ToInt32(count, CultureInfo.InvariantCulture);

    }

    private async Task ExecuteAsync(
        string sql,
        params (string Name, object Value)[] parameters)
    {

        SqliteConnection connection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            command.Parameters.AddWithValue(name, value);

        }

        _ = await command.ExecuteNonQueryAsync();

    }

    /// <summary>
    /// Runs a Session retention statement the way production runs it, under an open retention
    /// scope.
    /// </summary>
    /// <remarks>
    /// A test that stands in for a committed retention transaction has to stand in for its
    /// authorization too. The cascade into the per-Session turn capacity ledger is guarded and
    /// begins denied on every connection, including a pooled one handed back out, so a bare delete
    /// here would be simulating something production never does.
    /// </remarks>
    private async Task ExecuteSessionRetentionAsync(
        string sql,
        params (string Name, object Value)[] parameters)
    {

        using CovenantSqliteAuthorizationScope retention =
            CovenantSqliteConnectionInitializer.Instance.Authorize(
                (SqliteConnection)_db!.Database.GetDbConnection(),
                CovenantSqliteAuthorizationKind.SessionRetention);

        await ExecuteAsync(sql, parameters);

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

    /// <summary>
    /// Answers honestly for paths this test is not faking, without writing the process-global seam
    /// from whichever thread happens to ask. See the matching helper in
    /// <c>UploadedFileRepositoryTests</c>.
    /// </summary>
    private static FileHandleMetadata? ReadActualNoFollowMetadata(string path) =>
        FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(
            path,
            out FileHandleMetadata metadata)
            ? metadata
            : null;

    private sealed class SequencedRetentionPolicyStore(
        RetentionSettings initial,
        RetentionSettings subsequent,
        int initialReads) : IDataRetentionPolicyStore
    {

        private int _reads;

        public RetentionSettings Current =>
            Interlocked.Increment(ref _reads) <= initialReads
                ? initial
                : subsequent;

        public Task<Result<RetentionSettings>> UpdateRuleAsync(
            RetentionRuleUpdateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This test policy store is read-only.");

    }

    /// <summary>
    /// Counts the sweep's own periodic lease renewals and delegates everything else to the real
    /// store, so a test can assert that a long prune keeps its durable lease alive. The
    /// <see cref="DataRetentionLeaseMaintainer"/>'s in-candidate renewals go through
    /// <c>RenewLeaseAsync</c> and are deliberately not counted here.
    /// </summary>
    private sealed class HeartbeatCountingOperationStore(ILongRunningOperationStore inner)
        : ILongRunningOperationStore
    {

        private int _heartbeats;

        public int Heartbeats => Volatile.Read(ref _heartbeats);

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {

            _ = Interlocked.Increment(ref _heartbeats);

            return inner.HeartbeatAsync(
                operationId,
                ownerId,
                utcNow,
                leaseExpiresAt,
                cancellationToken);

        }

        public Task<bool> RenewLeaseAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.RenewLeaseAsync(
                operationId,
                ownerId,
                utcNow,
                leaseExpiresAt,
                cancellationToken);

        public Task<LongRunningOperation> CreateAsync(
            LongRunningOperationCreateRequest request,
            CancellationToken cancellationToken = default) =>
            inner.CreateAsync(request, cancellationToken);

        public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            CancellationToken cancellationToken = default) =>
            inner.ResolveOrCreateAsync(request, identity, cancellationToken);

        public Task<LongRunningOperation?> TryStartSingleFlightAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.TryStartSingleFlightAsync(
                request,
                ownerId,
                utcNow,
                leaseExpiresAt,
                cancellationToken);

        public Task<LongRunningOperation?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(operationId, cancellationToken);

        public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            inner.FindRequestIdentityAsync(operationId, cancellationToken);

        public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
            Guid requestedOperationId,
            CancellationToken cancellationToken = default) =>
            inner.FindByRequestedOperationIdAsync(requestedOperationId, cancellationToken);

        public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
            LongRunningOperationQuery query,
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(query, cancellationToken);

        public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
            DateTimeOffset utcNow,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.FindExpiredAsync(utcNow, limit, cancellationToken);

        public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.TryAcquireLeaseAsync(
                operationId,
                ownerId,
                utcNow,
                leaseExpiresAt,
                cancellationToken);

        public Task<bool> SaveCheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            inner.SaveCheckpointAsync(
                operationId,
                ownerId,
                expectedCheckpointVersion,
                checkpointVersion,
                checkpointPayload,
                checkpointReference,
                publicSummary,
                utcNow,
                cancellationToken);

        public Task<bool> TryTransitionAsync(
            Guid operationId,
            long expectedRevision,
            string? ownerId,
            LongRunningOperationState state,
            DateTimeOffset utcNow,
            string? terminalErrorCode = null,
            CancellationToken cancellationToken = default) =>
            inner.TryTransitionAsync(
                operationId,
                expectedRevision,
                ownerId,
                state,
                utcNow,
                terminalErrorCode,
                cancellationToken);

        public Task<bool> RequestCancellationAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            inner.RequestCancellationAsync(
                operationId,
                expectedRevision,
                utcNow,
                cancellationToken);

        public Task<bool> ResetForRetryAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            inner.ResetForRetryAsync(
                operationId,
                expectedRevision,
                utcNow,
                cancellationToken);

        public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetCountsAsync(cancellationToken);

    }

    private sealed class RetentionTestFileEncryptionKeyProvider : IFileEncryptionKeyProvider
    {

        private readonly FileEncryptionKeyMaterial _material =
            FileEncryptionKeyMaterial.Create(
                Enumerable.Range(0, 32)
                    .Select(static value => (byte)value)
                    .ToArray());

        public ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_material);

        public ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(
            string keyId,
            CancellationToken cancellationToken = default)
        {

            if (!string.Equals(keyId, _material.KeyId, StringComparison.Ordinal))
            {

                throw new EncryptedBlobKeyException("The test encryption key is unavailable.");

            }

            return ValueTask.FromResult(_material);

        }

    }

    private sealed record SeededAttachment(
        Guid AttachmentId,
        string ChunkId,
        byte[] Bytes,
        string AbsolutePath);

}
