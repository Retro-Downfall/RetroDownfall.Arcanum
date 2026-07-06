using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class UploadedFileRepositoryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private UploadedFileRepository? _repo;

    public UploadedFileRepositoryTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _repo = new UploadedFileRepository(_db);

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

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        UploadedFileRecord record = new(id, "batch-input.jsonl", 1024, "batch", "application/jsonl", createdAt);

        await _repo!.CreateAsync(record, CancellationToken.None);

        UploadedFileRecord? loaded = await _repo.GetByIdAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(id, loaded!.Id);

        Assert.Equal("batch-input.jsonl", loaded.Filename);

        Assert.Equal(1024, loaded.Bytes);

        Assert.Equal("batch", loaded.Purpose);

        Assert.Equal("application/jsonl", loaded.MimeType);

    }

    [SkippableFact]
    public async Task GetByIdAsync_returns_null_for_missing_id()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        UploadedFileRecord? loaded = await _repo!.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(loaded);

    }

    [SkippableFact]
    public async Task ListAsync_filters_by_purpose_when_provided()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _repo!.CreateAsync(new UploadedFileRecord(Guid.NewGuid(), "a.jsonl", 10, "batch", "application/jsonl", now), CancellationToken.None);

        await _repo.CreateAsync(new UploadedFileRecord(Guid.NewGuid(), "b.png", 20, "vision", "image/png", now), CancellationToken.None);

        IReadOnlyList<UploadedFileRecord> batchOnly = await _repo.ListAsync("batch", CancellationToken.None);

        Assert.Single(batchOnly);

        Assert.Equal("a.jsonl", batchOnly[0].Filename);

        IReadOnlyList<UploadedFileRecord> all = await _repo.ListAsync(null, CancellationToken.None);

        Assert.Equal(2, all.Count);

    }

    [SkippableFact]
    public async Task DeleteAsync_removes_row()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        await _repo!.CreateAsync(new UploadedFileRecord(id, "c.txt", 5, "assistants", "text/plain", DateTimeOffset.UtcNow), CancellationToken.None);

        await _repo.DeleteAsync(id, CancellationToken.None);

        UploadedFileRecord? loaded = await _repo.GetByIdAsync(id, CancellationToken.None);

        Assert.Null(loaded);

    }

    [SkippableFact]
    public void ResolvePath_is_deterministic_and_disk_independent()
    {

        Guid id = Guid.NewGuid();

        string path1 = UploadedFileStorage.ResolvePath(id);

        string path2 = UploadedFileStorage.ResolvePath(id);

        Assert.Equal(path1, path2);

        Assert.EndsWith(id.ToString("N"), path1, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task Migration_creates_UploadedFiles_table()
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
            WHERE type = 'table' AND name = 'UploadedFiles'
            LIMIT 1;
            """;

        object? result = await cmd.ExecuteScalarAsync(CancellationToken.None);

        Assert.NotNull(result);

    }

}
