using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class IdempotencyStoreTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private IdempotencyStore? _store;

    public IdempotencyStoreTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _store = new IdempotencyStore(_db);

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
    public async Task SaveAsync_then_TryGetAsync_round_trips()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        await _store!.SaveAsync("hash-1", 200, "application/json", "{\"ok\":true}", createdAt, CancellationToken.None);

        IdempotencyRecord? loaded = await _store.TryGetAsync("hash-1", DateTimeOffset.MinValue, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal("hash-1", loaded!.KeyHash);

        Assert.Equal(200, loaded.StatusCode);

        Assert.Equal("application/json", loaded.ContentType);

        Assert.Equal("{\"ok\":true}", loaded.ResponseBody);

    }

    [SkippableFact]
    public async Task TryGetAsync_returns_null_for_missing_key()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        IdempotencyRecord? loaded = await _store!.TryGetAsync("does-not-exist", DateTimeOffset.MinValue, CancellationToken.None);

        Assert.Null(loaded);

    }

    [SkippableFact]
    public async Task TryGetAsync_returns_null_when_record_older_than_notOlderThan_cutoff()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset createdAt = DateTimeOffset.UtcNow.AddHours(-25);

        await _store!.SaveAsync("hash-expired", 200, null, "body", createdAt, CancellationToken.None);

        IdempotencyRecord? loaded = await _store.TryGetAsync(
            "hash-expired",
            notOlderThan: DateTimeOffset.UtcNow.AddHours(-24),
            CancellationToken.None);

        Assert.Null(loaded);

    }

    [SkippableFact]
    public async Task SaveAsync_upserts_existing_row()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _store!.SaveAsync("hash-upsert", 200, "text/plain", "first", DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.SaveAsync("hash-upsert", 201, "application/json", "second", DateTimeOffset.UtcNow, CancellationToken.None);

        IdempotencyRecord? loaded = await _store.TryGetAsync("hash-upsert", DateTimeOffset.MinValue, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(201, loaded!.StatusCode);

        Assert.Equal("application/json", loaded.ContentType);

        Assert.Equal("second", loaded.ResponseBody);

    }

    [SkippableFact]
    public async Task DeleteExpiredAsync_removes_only_rows_older_than_cutoff()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _store!.SaveAsync("hash-old", 200, null, "old", now.AddHours(-48), CancellationToken.None);

        await _store.SaveAsync("hash-fresh", 200, null, "fresh", now, CancellationToken.None);

        int removed = await _store.DeleteExpiredAsync(now.AddHours(-24), CancellationToken.None);

        Assert.Equal(1, removed);

        Assert.Null(await _store.TryGetAsync("hash-old", DateTimeOffset.MinValue, CancellationToken.None));

        Assert.NotNull(await _store.TryGetAsync("hash-fresh", DateTimeOffset.MinValue, CancellationToken.None));

    }

    [SkippableFact]
    public async Task Migration_creates_IdempotencyKeys_table()
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
            WHERE type = 'table' AND name = 'IdempotencyKeys'
            LIMIT 1;
            """;

        object? result = await cmd.ExecuteScalarAsync(CancellationToken.None);

        Assert.NotNull(result);

    }

}
