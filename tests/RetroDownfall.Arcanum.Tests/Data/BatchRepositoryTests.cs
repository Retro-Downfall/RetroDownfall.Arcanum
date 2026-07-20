using Microsoft.EntityFrameworkCore;
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

        await _repo!.CreateAsync(
            new BatchRecord(id, Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null),
            CancellationToken.None);

        Guid outputFileId = Guid.NewGuid();

        Guid errorFileId = Guid.NewGuid();

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

        await _repo!.CreateAsync(new BatchRecord(Guid.NewGuid(), Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.Completed, now, now, null, null), CancellationToken.None);

        await _repo.CreateAsync(new BatchRecord(Guid.NewGuid(), Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.Validating, now, null, null, null), CancellationToken.None);

        IReadOnlyList<BatchRecord> completedOnly = await _repo.ListAsync(BatchStatuses.Completed, CancellationToken.None);

        Assert.Single(completedOnly);

        IReadOnlyList<BatchRecord> all = await _repo.ListAsync(null, CancellationToken.None);

        Assert.Equal(2, all.Count);

    }

    [SkippableFact]
    public async Task ListActiveAsync_returns_only_validating_and_inProgress()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _repo!.CreateAsync(new BatchRecord(Guid.NewGuid(), Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.Validating, now, null, null, null), CancellationToken.None);

        await _repo.CreateAsync(new BatchRecord(Guid.NewGuid(), Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.InProgress, now, null, null, null), CancellationToken.None);

        await _repo.CreateAsync(new BatchRecord(Guid.NewGuid(), Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.Completed, now, now, null, null), CancellationToken.None);

        await _repo.CreateAsync(new BatchRecord(Guid.NewGuid(), Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.Cancelled, now, now, null, null), CancellationToken.None);

        IReadOnlyList<BatchRecord> active = await _repo.ListActiveAsync(CancellationToken.None);

        Assert.Equal(2, active.Count);

        Assert.All(active, b => Assert.True(b.Status is BatchStatuses.Validating or BatchStatuses.InProgress));

    }

    [SkippableFact]
    public async Task ListByStatusAsync_returns_only_matching_status()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _repo!.CreateAsync(new BatchRecord(Guid.NewGuid(), Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.InProgress, now, null, null, null), CancellationToken.None);

        await _repo.CreateAsync(new BatchRecord(Guid.NewGuid(), Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.InProgress, now, null, null, null), CancellationToken.None);

        await _repo.CreateAsync(new BatchRecord(Guid.NewGuid(), Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.Validating, now, null, null, null), CancellationToken.None);

        IReadOnlyList<BatchRecord> inProgress = await _repo.ListByStatusAsync(BatchStatuses.InProgress, CancellationToken.None);

        Assert.Equal(2, inProgress.Count);

        Assert.All(inProgress, b => Assert.Equal(BatchStatuses.InProgress, b.Status));

    }

    [SkippableFact]
    public async Task TryCompareAndSetStatusAsync_updates_only_when_expected_matches()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        await _repo!.CreateAsync(
            new BatchRecord(id, Guid.NewGuid(), "/v1/chat/completions", BatchStatuses.InProgress, DateTimeOffset.UtcNow, null, null, null),
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

}
