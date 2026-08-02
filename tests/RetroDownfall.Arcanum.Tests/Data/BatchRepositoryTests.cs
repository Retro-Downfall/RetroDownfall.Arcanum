using Microsoft.EntityFrameworkCore;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class BatchRepositoryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private BatchRepository? _repo;

    public BatchRepositoryTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _repo = new BatchRepository(_db);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]
    public async Task CreateAsync_then_GetByIdAsync_round_trips()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        Guid inputFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(inputFileId);

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        BatchRecord record = new(id, inputFileId, "/v1/chat/completions", BatchStatuses.Validating, createdAt, null, null, null);

        await _repo!.CreateAsync(record, CancellationToken.None);

        BatchRecord? loaded = await _repo.GetByIdAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(id, loaded!.Id);

        Assert.Equal(inputFileId, loaded.InputFileId);

        Assert.Equal("/v1/chat/completions", loaded.Endpoint);

        Assert.Equal(BatchStatuses.Validating, loaded.Status);

        Assert.Null(loaded.CompletedAt);

        Assert.Null(loaded.OutputFileId);

        Assert.Null(loaded.ErrorFileId);

    }

    [SkippableTheory]

    [InlineData("input")]

    [InlineData("output")]

    [InlineData("error")]

    public async Task CreateAsync_rejects_missing_file_references_atomically(
        string missingRole)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = Guid.NewGuid();

        Guid outputFileId = Guid.NewGuid();

        Guid errorFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(inputFileId);

        await SeedUploadedFileAsync(outputFileId);

        await SeedUploadedFileAsync(errorFileId);

        Guid missingFileId = missingRole switch
        {

            "input" => inputFileId,

            "output" => outputFileId,

            "error" => errorFileId,

            _ => throw new ArgumentOutOfRangeException(nameof(missingRole)),

        };

        await DeleteUploadedFileMetadataAsync(missingFileId);

        Guid batchId = Guid.NewGuid();

        BatchRecord record = new(
            batchId,
            inputFileId,
            "/v1/chat/completions",
            BatchStatuses.Completed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            outputFileId,
            errorFileId);

        _ = await Assert.ThrowsAsync<BatchFileReferenceException>(
            () => _repo!.CreateAsync(record, CancellationToken.None));

        Assert.Null(await _repo!.GetByIdAsync(batchId, CancellationToken.None));

    }

    [SkippableTheory]

    [InlineData("output")]

    [InlineData("error")]

    public async Task UpdateStatusAsync_rejects_missing_artifact_references_atomically(
        string missingRole)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(inputFileId);

        Guid batchId = Guid.NewGuid();

        await _repo!.CreateAsync(
            new BatchRecord(
                batchId,
                inputFileId,
                "/v1/chat/completions",
                BatchStatuses.InProgress,
                DateTimeOffset.UtcNow,
                null,
                null,
                null),
            CancellationToken.None);

        Guid missingFileId = Guid.NewGuid();

        Guid? outputFileId = missingRole == "output" ? missingFileId : null;

        Guid? errorFileId = missingRole == "error" ? missingFileId : null;

        _ = await Assert.ThrowsAsync<BatchFileReferenceException>(
            () => _repo.UpdateStatusAsync(
                batchId,
                BatchStatuses.Completed,
                DateTimeOffset.UtcNow,
                outputFileId,
                errorFileId,
                CancellationToken.None));

        BatchRecord? unchanged = await _repo.GetByIdAsync(
            batchId,
            CancellationToken.None);

        Assert.NotNull(unchanged);

        Assert.Equal(BatchStatuses.InProgress, unchanged.Status);

        Assert.Null(unchanged.OutputFileId);

        Assert.Null(unchanged.ErrorFileId);

    }

    [SkippableTheory]

    [InlineData("output")]

    [InlineData("error")]

    public async Task TryCompareAndSetStatusAsync_rejects_missing_artifact_reference(
        string missingRole)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(inputFileId);

        Guid batchId = Guid.NewGuid();

        await _repo!.CreateAsync(
            new BatchRecord(
                batchId,
                inputFileId,
                "/v1/chat/completions",
                BatchStatuses.InProgress,
                DateTimeOffset.UtcNow,
                null,
                null,
                null),
            CancellationToken.None);

        Guid missingFileId = Guid.NewGuid();

        Guid? outputFileId = missingRole == "output" ? missingFileId : null;

        Guid? errorFileId = missingRole == "error" ? missingFileId : null;

        bool updated = await _repo.TryCompareAndSetStatusAsync(
            batchId,
            BatchStatuses.InProgress,
            BatchStatuses.Completed,
            DateTimeOffset.UtcNow,
            outputFileId,
            errorFileId,
            CancellationToken.None);

        Assert.False(updated);

        BatchRecord? unchanged = await _repo.GetByIdAsync(
            batchId,
            CancellationToken.None);

        Assert.Equal(BatchStatuses.InProgress, unchanged!.Status);

        Assert.Null(unchanged.OutputFileId);

        Assert.Null(unchanged.ErrorFileId);

    }

    [SkippableFact]

    public async Task CreateAsync_waits_for_concurrent_file_delete_and_rejects_stale_reference()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(inputFileId);

        await using ArcanumDbContext concurrentDb = _fixture.CreateContext(_dbPath);

        BatchRepository concurrentBatches = new(concurrentDb);

        SqliteConnection deleteConnection = Assert.IsType<SqliteConnection>(
            _db!.Database.GetDbConnection());

        await using SqliteTransaction deleteTransaction =
            deleteConnection.BeginTransaction(deferred: false);

        await using (SqliteCommand delete = deleteConnection.CreateCommand())
        {

            delete.Transaction = deleteTransaction;

            delete.CommandText =
                "DELETE FROM \"UploadedFiles\" WHERE lower(replace(\"Id\", '-', '')) = @id";

            _ = delete.Parameters.AddWithValue("@id", inputFileId.ToString("N"));

            Assert.Equal(
                1,
                await delete.ExecuteNonQueryAsync(CancellationToken.None));

        }

        Guid batchId = Guid.NewGuid();

        TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task create = Task.Run(
            async () =>
            {

                started.SetResult();

                await concurrentBatches.CreateAsync(
                    new BatchRecord(
                        batchId,
                        inputFileId,
                        "/v1/chat/completions",
                        BatchStatuses.Validating,
                        DateTimeOffset.UtcNow,
                        null,
                        null,
                        null),
                    CancellationToken.None);

            });

        await started.Task;

        await Task.Yield();

        await deleteTransaction.CommitAsync(CancellationToken.None);

        _ = await Assert.ThrowsAsync<BatchFileReferenceException>(() => create);

        Assert.Null(await concurrentBatches.GetByIdAsync(batchId, CancellationToken.None));

    }

    [SkippableFact]
    public async Task GetByIdAsync_returns_null_for_missing_id()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        BatchRecord? loaded = await _repo!.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(loaded);

    }

    [SkippableFact]
    public async Task UpdateStatusAsync_sets_completion_fields()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        Guid inputFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(inputFileId);

        await _repo!.CreateAsync(
            new BatchRecord(id, inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null),
            CancellationToken.None);

        Guid outputFileId = Guid.NewGuid();

        Guid errorFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(outputFileId);

        await SeedUploadedFileAsync(errorFileId);

        DateTimeOffset completedAt = DateTimeOffset.UtcNow;

        await _repo.UpdateStatusAsync(id, BatchStatuses.Completed, completedAt, outputFileId, errorFileId, CancellationToken.None);

        BatchRecord? loaded = await _repo.GetByIdAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(BatchStatuses.Completed, loaded!.Status);

        Assert.Equal(outputFileId, loaded.OutputFileId);

        Assert.Equal(errorFileId, loaded.ErrorFileId);

        Assert.NotNull(loaded.CompletedAt);

    }

    [SkippableFact]
    public async Task ListAsync_filters_by_status_when_provided()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await CreateBatchAsync(BatchStatuses.Completed, now, now);

        await CreateBatchAsync(BatchStatuses.Validating, now, null);

        IReadOnlyList<BatchRecord> completedOnly = await _repo!.ListAsync(BatchStatuses.Completed, CancellationToken.None);

        Assert.Single(completedOnly);

        IReadOnlyList<BatchRecord> all = await _repo.ListAsync(null, CancellationToken.None);

        Assert.Equal(2, all.Count);

    }

    [SkippableFact]
    public async Task ListActiveAsync_returns_only_validating_and_inProgress()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await CreateBatchAsync(BatchStatuses.Validating, now, null);

        await CreateBatchAsync(BatchStatuses.InProgress, now, null);

        await CreateBatchAsync(BatchStatuses.Completed, now, now);

        await CreateBatchAsync(BatchStatuses.Cancelled, now, now);

        IReadOnlyList<BatchRecord> active = await _repo!.ListActiveAsync(CancellationToken.None);

        Assert.Equal(2, active.Count);

        Assert.All(active, b => Assert.True(b.Status is BatchStatuses.Validating or BatchStatuses.InProgress));

    }

    [SkippableFact]
    public async Task ListByStatusAsync_returns_only_matching_status()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await CreateBatchAsync(BatchStatuses.InProgress, now, null);

        await CreateBatchAsync(BatchStatuses.InProgress, now, null);

        await CreateBatchAsync(BatchStatuses.Validating, now, null);

        IReadOnlyList<BatchRecord> inProgress = await _repo!.ListByStatusAsync(BatchStatuses.InProgress, CancellationToken.None);

        Assert.Equal(2, inProgress.Count);

        Assert.All(inProgress, b => Assert.Equal(BatchStatuses.InProgress, b.Status));

    }

    [SkippableFact]
    public async Task TryCompareAndSetStatusAsync_updates_only_when_expected_matches()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        Guid inputFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(inputFileId);

        await _repo!.CreateAsync(
            new BatchRecord(id, inputFileId, "/v1/chat/completions", BatchStatuses.InProgress, DateTimeOffset.UtcNow, null, null, null),
            CancellationToken.None);

        bool first = await _repo.TryCompareAndSetStatusAsync(
            id,
            BatchStatuses.InProgress,
            BatchStatuses.Validating,
            null,
            null,
            null,
            CancellationToken.None);

        Assert.True(first);

        bool second = await _repo.TryCompareAndSetStatusAsync(
            id,
            BatchStatuses.InProgress,
            BatchStatuses.Failed,
            DateTimeOffset.UtcNow,
            null,
            null,
            CancellationToken.None);

        Assert.False(second);

        BatchRecord? loaded = await _repo.GetByIdAsync(id, CancellationToken.None);

        Assert.Equal(BatchStatuses.Validating, loaded!.Status);

    }

    [SkippableFact]
    public async Task Migration_creates_Batches_table()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        await using System.Data.Common.DbCommand cmd = connection.CreateCommand();

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {

            await cmd.Connection.OpenAsync(CancellationToken.None);

        }

        cmd.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = 'Batches'
            LIMIT 1;
            """;

        object? result = await cmd.ExecuteScalarAsync(CancellationToken.None);

        Assert.NotNull(result);

    }

    private Task SeedUploadedFileAsync(Guid id)
    {

        UploadedFileRepository files = new(_db!);

        return files.CreateAsync(
            new UploadedFileRecord(
                id,
                id.ToString("N") + ".jsonl",
                1,
                "batch",
                "application/jsonl",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

    }

    private async Task CreateBatchAsync(
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset? completedAt)
    {

        Guid inputFileId = Guid.NewGuid();

        await SeedUploadedFileAsync(inputFileId);

        await _repo!.CreateAsync(
            new BatchRecord(
                Guid.NewGuid(),
                inputFileId,
                "/v1/chat/completions",
                status,
                createdAt,
                completedAt,
                null,
                null),
            CancellationToken.None);

    }

    private Task DeleteUploadedFileMetadataAsync(Guid id)
    {

        UploadedFileRepository files = new(_db!);

        return files.DeleteAsync(id, CancellationToken.None);

    }

}
