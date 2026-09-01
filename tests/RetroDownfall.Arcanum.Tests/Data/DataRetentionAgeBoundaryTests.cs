using System.Globalization;

using System.Text;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{

    [SkippableFact]

    public async Task PlanAsync_Prune_UsesOneFrozenTimestampForSelectionAndGeneratedAt()
    {

        RequireSqlCipher();

        DateTimeOffset initial = DateTimeOffset.Parse("2026-01-31T00:00:00Z");

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
            ("@at", initial.AddDays(-30).AddHours(1).ToString(
                "o",
                CultureInfo.InvariantCulture)));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.CompletedBatches = EnabledRule(days: 30);

        AdvancingTimeProvider time = new(initial, TimeSpan.FromDays(1));

        DataRetentionService service = CreateService(
            settings,
            timeProvider: time);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune));

        Assert.Equal(initial, plan.GeneratedAt);

        Assert.Empty(plan.CandidateIds);

    }

    [SkippableFact]

    public async Task PlanAsync_Prune_WhenEffectivePolicyChanges_ChangesPlanIdentity()
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
            ("@at", DateTimeOffset.UtcNow.AddDays(-100).ToString(
                "o",
                CultureInfo.InvariantCulture)));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.CompletedBatches = EnabledRule(days: 30);

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan first = await service.PlanAsync(request);

        settings.Retention.CompletedBatches = EnabledRule(days: 40);

        DataRetentionPlan second = await service.PlanAsync(request);

        Assert.Equal(first.CandidateIds, second.CandidateIds);

        Assert.Equal(first.Items, second.Items);

        Assert.NotEqual(first.PlanId, second.PlanId);

    }

    [SkippableTheory]

    [InlineData("batch")]

    [InlineData("entry")]

    [InlineData("saga")]

    [InlineData("lexicon")]

    [InlineData("operation")]

    [InlineData("sanctum")]

    [InlineData("audit-log")]

    public async Task ApplyAsync_Prune_WhenCandidateBecomesFreshAtMutationBoundary_PreservesCandidateAndCursor(
        string kind)
    {

        RequireSqlCipher();

        FreshnessCandidate seeded = await SeedFreshnessCandidateAsync(kind);

        ArcanumSettings settings = CreatePruneSettings();

        seeded.Enable(settings.Retention);

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(seeded.CandidateId, Assert.Single(plan.CandidateIds));

        await ArrangeFreshnessChangeAfterPruneStartsAsync(seeded);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Conflicts,
            conflict => conflict.Code == ErrorCodes.Data.PlanChanged
                && conflict.ResourceId == seeded.CandidateId);

        Assert.True(await seeded.Exists());

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionPrune)));

        byte[] checkpoint = Assert.IsType<byte[]>(operation.CheckpointPayload);

        Assert.Equal(2, operation.CheckpointVersion);

        string[] checkpointLines = Encoding.UTF8
            .GetString(checkpoint)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("0", checkpointLines[2]);

        Assert.Equal("ARCADATA2", checkpointLines[0]);

    }

    /// <summary>
    /// The durable cursor must stay at or before the earliest preserved candidate, because recovery
    /// resumes from it and a cursor past a preserved candidate silently drops the re-evaluation that
    /// preservation exists to force. A journal-bearing candidate (<c>file:</c>) writes checkpoints of
    /// its own, and those writes must be clamped the same way the periodic ones are — a preserved
    /// <c>batch:</c> ahead of it carries no journal, so nothing else holds the cursor back.
    /// </summary>
    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenAJournalBearingCandidateFollowsAPreservedOne_KeepsTheCursorAtThePreservedCandidate()
    {

        RequireSqlCipher();

        FreshnessCandidate seeded = await SeedFreshnessCandidateAsync("batch");

        // The batch's own input file must not become a second candidate — this scenario needs
        // exactly one journal-bearing candidate, ordered after the preserved batch.
        await ExecuteAsync(
            """UPDATE "UploadedFiles" SET "CreatedAt" = @at""",
            ("@at", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

        Guid fileId = Guid.NewGuid();

        string absolutePath = Path.Combine(_filesRoot, fileId.ToString("N"));

        await File.WriteAllBytesAsync(absolutePath, [1, 2, 3]);

        await SeedUploadedFileAsync(fileId, 3);

        ArcanumSettings settings = CreatePruneSettings();

        seeded.Enable(settings.Retention);

        settings.Retention.UploadedFiles = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(
            [seeded.CandidateId, "file:" + fileId.ToString("D")],
            plan.CandidateIds);

        await ArrangeFreshnessChangeAfterPruneStartsAsync(seeded);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(await seeded.Exists());

        Assert.False(File.Exists(absolutePath));

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionPrune)));

        string[] checkpointLines = Encoding.UTF8
            .GetString(Assert.IsType<byte[]>(operation.CheckpointPayload))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("0", checkpointLines[2]);

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenEntryFreshensAfterPrecheckButBeforeParentDelete_RollsBackCandidate()
    {

        RequireSqlCipher();

        (_, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        await ExecuteAsync(
            $"""
            CREATE TRIGGER freshen_entry_after_derived_delete
            AFTER DELETE ON entry_embeddings
            WHEN lower(replace(OLD.EntryId, '-', '')) = '{entryId:N}'
            BEGIN
                UPDATE Entries
                SET CreatedAt = '2999-01-01T00:00:00.0000000+00:00'
                WHERE lower(replace(Id, '-', '')) = '{entryId:N}';
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        string candidateId = "entry:" + entryId.ToString("D");

        Assert.Equal(candidateId, Assert.Single(plan.CandidateIds));

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Conflicts,
            conflict => conflict.Code == ErrorCodes.Data.PlanChanged
                && conflict.ResourceId == candidateId);

        Assert.Equal(1, await CountNormalizedKeyAsync("Entries", "Id", entryId.ToString()));

        Assert.Equal(
            1,
            await CountNormalizedKeyAsync(
                "entry_embeddings",
                "EntryId",
                entryId.ToString()));

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = Assert.Single(
            await operations.ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionPrune)));

        byte[] checkpoint = Assert.IsType<byte[]>(operation.CheckpointPayload);

        string[] checkpointLines = Encoding.UTF8
            .GetString(checkpoint)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("0", checkpointLines[2]);

    }

    [SkippableFact]

    public async Task ApplyAsync_Prune_WhenWorkspaceChunkFreshensDuringDerivedDelete_RollsBackCandidate()
    {

        RequireSqlCipher();

        string chunkId = "fresh-workspace-" + Guid.NewGuid().ToString("N");

        await ExecuteAsync(
            """
            INSERT INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset,
                 CharLength, FileLastWriteTime, IndexedAt)
            VALUES
                (@id, '/workspace', 'fresh.cs', 0, 'old', 0, 3, @at, @at)
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
            $"""
            CREATE TRIGGER freshen_workspace_during_derived_delete
            BEFORE DELETE ON workspace_file_embeddings
            WHEN OLD.ChunkId = '{chunkId}'
            BEGIN
                UPDATE workspace_file_chunks
                SET IndexedAt = '2999-01-01T00:00:00.0000000+00:00'
                WHERE ChunkId = '{chunkId}';
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.WorkspaceIndexes = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        string candidateId = "workspace:" + chunkId;

        Assert.Equal(candidateId, Assert.Single(plan.CandidateIds));

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Conflicts,
            conflict => conflict.Code == ErrorCodes.Data.PlanChanged
                && conflict.ResourceId == candidateId);

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

    public async Task ApplyAsync_Prune_WhenEntryEmbeddingParentFreshensDuringDelete_RollsBackCandidate()
    {

        RequireSqlCipher();

        (_, Guid entryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entryId);

        await ExecuteAsync(
            $"""
            CREATE TRIGGER freshen_entry_during_embedding_delete
            BEFORE DELETE ON entry_embeddings
            WHEN lower(replace(OLD.EntryId, '-', '')) = '{entryId:N}'
            BEGIN
                UPDATE Entries
                SET CreatedAt = '2999-01-01T00:00:00.0000000+00:00'
                WHERE lower(replace(Id, '-', '')) = '{entryId:N}';
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        string candidateId = "entry-embedding:" + Canonical(entryId);

        Assert.Equal(candidateId, Assert.Single(plan.CandidateIds));

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Conflicts,
            conflict => conflict.Code == ErrorCodes.Data.PlanChanged
                && conflict.ResourceId == candidateId);

        Assert.Equal(
            1,
            await CountNormalizedKeyAsync(
                "entry_embeddings",
                "EntryId",
                entryId.ToString()));

    }

    [SkippableFact]

    public async Task RecoverPruneAsync_WhenPolicyShortens_EnforcesPersistedOriginalCandidateCutoff()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        Guid batchId = Guid.NewGuid();

        await SeedUploadedFileAsync(fileId, 0);

        DateTimeOffset originallyEligibleAt = DateTimeOffset.UtcNow.AddDays(-40);

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
            ("@at", originallyEligibleAt.ToString("o", CultureInfo.InvariantCulture)));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.CompletedBatches = EnabledRule(days: 30);

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        string candidate = Assert.Single(plan.CandidateIds);

        DateTimeOffset originalCutoff = plan.GeneratedAt.AddDays(-30);

        DateTimeOffset refreshedAt = DateTimeOffset.UtcNow.AddDays(-10);

        await ExecuteAsync(
            "UPDATE Batches SET CompletedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
            ("@at", refreshedAt.ToString("o", CultureInfo.InvariantCulture)),
            ("@id", batchId.ToString("N")));

        settings.Retention.CompletedBatches = EnabledRule(days: 1);

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "frozen-cutoff-recovery-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Interrupted prune with a frozen cutoff.",
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
                Encoding.UTF8.GetBytes(
                    plan.GeneratedAt.ToString("o", CultureInfo.InvariantCulture)))
            + "\nC:"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(candidate))
            + ":"
            + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    originalCutoff.ToString("o", CultureInfo.InvariantCulture)))
            + "\n");

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion: 2,
                checkpoint,
                checkpointReference: null,
                "Interrupted with frozen candidate ages.",
                now));

        LongRunningOperationRecoveryResult recovered = await service.RecoverPruneAsync(
            Assert.IsType<LongRunningOperation>(await operations.GetAsync(operation.Id)),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

        Assert.Equal(
            1,
            await CountNormalizedKeyAsync("Batches", "Id", batchId.ToString()));

    }

    [SkippableFact]

    public async Task RecoverPruneAsync_WithLegacyCheckpoint_FailsClosed()
    {

        RequireSqlCipher();

        DataRetentionService service = CreateService(CreatePruneSettings());

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "legacy-retention-checkpoint-test";

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Legacy checkpoint must fail closed.",
                now));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5));

        Assert.True(lease.Acquired);

        byte[] checkpoint = Encoding.UTF8.GetBytes(
            "ARCADATA1\n"
            + new string('A', 64)
            + "\n0\n");

        Assert.True(
            await operations.SaveCheckpointAsync(
                operation.Id,
                ownerId,
                expectedCheckpointVersion: 0,
                checkpointVersion: 1,
                checkpoint,
                checkpointReference: null,
                "Legacy checkpoint.",
                now));

        LongRunningOperation interrupted = Assert.IsType<LongRunningOperation>(
            await operations.GetAsync(operation.Id));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.RecoverPruneAsync(
                interrupted,
                CancellationToken.None));

    }

    private async Task<FreshnessCandidate> SeedFreshnessCandidateAsync(string kind)
    {

        switch (kind)
        {

            case "batch":
            {

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

                return DatabaseFreshnessCandidate(
                    "batch:" + batchId.ToString("D"),
                    "Batches",
                    "CompletedAt",
                    "Id",
                    batchId.ToString(),
                    static retention => retention.CompletedBatches = EnabledRule());

            }

            case "entry":
            {

                (_, Guid entryId) = await SeedSessionAsync(pinned: false);

                return DatabaseFreshnessCandidate(
                    "entry:" + entryId.ToString("D"),
                    "Entries",
                    "CreatedAt",
                    "Id",
                    entryId.ToString(),
                    static retention => retention.Entries = EnabledRule());

            }

            case "saga":
            {

                string memoryId = "freshness-" + Guid.NewGuid().ToString("N");

                await ExecuteAsync(
                    """
                    INSERT INTO saga_memories (Id, Content, CreatedAt, SessionId, Tags, Source)
                    VALUES (@id, 'memory', @at, NULL, NULL, 'test')
                    """,
                    ("@id", memoryId),
                    ("@at", OldTimestamp));

                return DatabaseFreshnessCandidate(
                    "saga:" + memoryId,
                    "saga_memories",
                    "CreatedAt",
                    "Id",
                    memoryId,
                    static retention => retention.SagaMemories = EnabledRule());

            }

            case "lexicon":
            {

                string entryId = "freshness-" + Guid.NewGuid().ToString("N");

                await ExecuteAsync(
                    """
                    INSERT INTO lexicon_entries
                        (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
                    VALUES
                        (@id, @id, @id, 'concept', '[]', '', @at)
                    """,
                    ("@id", entryId),
                    ("@at", OldTimestamp));

                return DatabaseFreshnessCandidate(
                    "lexicon:" + entryId,
                    "lexicon_entries",
                    "UpdatedAt",
                    "Id",
                    entryId,
                    static retention => retention.LexiconEntries = EnabledRule());

            }

            case "operation":
            {

                LongRunningOperationStore operations = new(
                    _db!,
                    TestOrdinaryConnectionFactory.For(_db!));

                DateTimeOffset createdAt = DateTimeOffset.UtcNow.AddDays(-10);

                LongRunningOperation operation = await operations.CreateAsync(
                    new LongRunningOperationCreateRequest(
                        LongRunningOperationKinds.WorkspaceIndex,
                        LongRunningOperationRecoveryPolicy.RestartIdempotently,
                        "Old completed operation.",
                        createdAt));

                LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
                    operation.Id,
                    "freshness-test",
                    createdAt,
                    createdAt.AddMinutes(5));

                Assert.True(lease.Acquired);

                Assert.True(
                    await operations.TryTransitionAsync(
                        operation.Id,
                        lease.Operation.Revision,
                        "freshness-test",
                        LongRunningOperationState.Completed,
                        createdAt.AddMinutes(1)));

                return DatabaseFreshnessCandidate(
                    "operation:" + operation.Id.ToString("D"),
                    "LongRunningOperations",
                    "CompletedAt",
                    "Id",
                    operation.Id.ToString(),
                    static retention => retention.LongRunningOperations = EnabledRule());

            }

            case "sanctum":
            {

                Guid campaign = Guid.NewGuid();

                string campaignId = Canonical(campaign);

                string campaignName = campaign.ToString("N");

                Guid breachId = Guid.NewGuid();

                await ExecuteAsync(
                    """
                    INSERT INTO "Campaigns"
                        ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
                    VALUES
                        (@campaignId, @campaignName, @campaignName, @path, 0, '{}', @at, @at);

                    INSERT INTO "SanctumBreaches"
                        ("Id", "CampaignId", "OccurredAt", "ToolName", "BreachType", "Description")
                    VALUES
                        (@id, @campaignId, @at, 'test', 'test', 'test');
                    """,
                    ("@campaignId", campaignId),
                    ("@campaignName", campaignName),
                    ("@path", "/tmp/" + campaignName),
                    ("@id", breachId.ToString()),
                    ("@at", OldTimestamp));

                return DatabaseFreshnessCandidate(
                    "sanctum:" + breachId.ToString("D"),
                    "SanctumBreaches",
                    "OccurredAt",
                    "Id",
                    breachId.ToString(),
                    static retention => retention.SanctumBreaches = EnabledRule());

            }

            case "audit-log":
            {

                string fileName = "audit-20000101.jsonl";

                string path = Path.Combine(_logsRoot, fileName);

                await File.WriteAllTextAsync(path, "{}\n");

                File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch);

                return new FreshnessCandidate(
                    "audit-log:" + fileName,
                    null,
                    null,
                    null,
                    null,
                    static retention => retention.AuditLogs = EnabledRule(),
                    () => Task.FromResult(File.Exists(path)),
                    path);

            }

            default:
                throw new InvalidOperationException("Unknown freshness scenario.");

        }

    }

    private sealed class AdvancingTimeProvider(
        DateTimeOffset initial,
        TimeSpan increment) : TimeProvider
    {

        private long _calls;

        public override DateTimeOffset GetUtcNow()
        {

            long call = Interlocked.Increment(ref _calls) - 1;

            return initial.AddTicks(increment.Ticks * call);

        }

    }

    private FreshnessCandidate DatabaseFreshnessCandidate(
        string candidateId,
        string table,
        string timestampColumn,
        string keyColumn,
        string key,
        Action<RetentionSettings> enable) =>
        new(
            candidateId,
            table,
            timestampColumn,
            keyColumn,
            key,
            enable,
            async () => await CountNormalizedKeyAsync(table, keyColumn, key) == 1,
            null);

    private async Task<long> CountNormalizedKeyAsync(
        string table,
        string keyColumn,
        string key)
    {

        SqliteConnection connection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"SELECT COUNT(*) FROM \"{table}\" WHERE lower(replace(\"{keyColumn}\", '-', '')) = @key";

        _ = command.Parameters.AddWithValue(
            "@key",
            key.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant());

        object? value = await command.ExecuteScalarAsync();

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private async Task ArrangeFreshnessChangeAfterPruneStartsAsync(
        FreshnessCandidate candidate)
    {

        if (candidate.FilePath is not null)
        {

            SqliteConnection connection =
                (SqliteConnection)_db!.Database.GetDbConnection();

            connection.CreateFunction(
                "freshen_retention_log",
                () =>
                {

                    File.SetLastWriteTimeUtc(
                        candidate.FilePath,
                        DateTime.UtcNow.AddDays(1));

                    return 1;

                });

            await ExecuteAsync(
                $"""
                CREATE TRIGGER freshen_candidate_after_prune_start
                AFTER INSERT ON LongRunningOperations
                WHEN NEW.Kind = '{LongRunningOperationKinds.DataRetentionPrune}'
                BEGIN
                    SELECT freshen_retention_log();
                END;
                """);

            return;

        }

        await ExecuteAsync(
            $"""
            CREATE TRIGGER freshen_candidate_after_prune_start
            AFTER INSERT ON LongRunningOperations
            WHEN NEW.Kind = '{LongRunningOperationKinds.DataRetentionPrune}'
            BEGIN
                UPDATE "{candidate.Table}"
                SET "{candidate.TimestampColumn}" = '2999-01-01T00:00:00.0000000+00:00'
                WHERE lower(replace("{candidate.KeyColumn}", '-', ''))
                    = lower(replace('{candidate.Key}', '-', ''));
            END;
            """);

    }

    private sealed record FreshnessCandidate(
        string CandidateId,
        string? Table,
        string? TimestampColumn,
        string? KeyColumn,
        string? Key,
        Action<RetentionSettings> Enable,
        Func<Task<bool>> Exists,
        string? FilePath);

}
