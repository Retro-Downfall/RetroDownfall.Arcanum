using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class UnseenServantWatermarkStoreTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private UnseenServantWatermarkStore? _store;

    public UnseenServantWatermarkStoreTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _store = new UnseenServantWatermarkStore(_db);

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
    public async Task SaveAsync_then_GetAsync_round_trips()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string jobKey = "MarketWatcher\0patrol";

        DateTimeOffset lastRunAt = DateTimeOffset.Parse("2026-07-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        await _store!.SaveAsync(jobKey, lastRunAt, 30, CancellationToken.None);

        UnseenServantWatermark? loaded = await _store.GetAsync(jobKey, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(jobKey, loaded!.JobKey);

        Assert.Equal(lastRunAt, loaded.LastRunAt);

        Assert.Equal(30, loaded.EffectiveIntervalMinutes);

    }

    [SkippableFact]
    public async Task SaveAsync_upserts_existing_row()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string jobKey = "MarketWatcher\0patrol";

        DateTimeOffset firstRunAt = DateTimeOffset.Parse("2026-07-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        DateTimeOffset secondRunAt = DateTimeOffset.Parse("2026-07-02T08:30:00Z", System.Globalization.CultureInfo.InvariantCulture);

        await _store!.SaveAsync(jobKey, firstRunAt, 30, CancellationToken.None);

        await _store.SaveAsync(jobKey, secondRunAt, 45, CancellationToken.None);

        UnseenServantWatermark? loaded = await _store.GetAsync(jobKey, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(secondRunAt, loaded!.LastRunAt);

        Assert.Equal(45, loaded.EffectiveIntervalMinutes);

        IReadOnlyList<UnseenServantWatermark> all = await _store.GetAllAsync(CancellationToken.None);

        Assert.Single(all, w => w.JobKey == jobKey);

    }

    [SkippableFact]
    public async Task GetAllAsync_returns_rows_ordered_by_job_key()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _store!.SaveAsync("Zeta\0spell", now, 10, CancellationToken.None);

        await _store.SaveAsync("Alpha\0spell", now, 20, CancellationToken.None);

        await _store.SaveAsync("Mu\0spell", now, 30, CancellationToken.None);

        IReadOnlyList<UnseenServantWatermark> all = await _store.GetAllAsync(CancellationToken.None);

        List<string> keys = all.Select(w => w.JobKey).ToList();

        List<string> sortedKeys = keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(sortedKeys, keys);

        Assert.Contains("Alpha\0spell", keys);

        Assert.Contains("Mu\0spell", keys);

        Assert.Contains("Zeta\0spell", keys);

    }

    [SkippableFact]
    public async Task GetAsync_returns_null_for_missing_key()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        UnseenServantWatermark? loaded = await _store!.GetAsync("DoesNotExist\0spell", CancellationToken.None);

        Assert.Null(loaded);

    }

    [SkippableFact]
    public async Task DeleteAsync_removes_row()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string jobKey = "MarketWatcher\0patrol";

        await _store!.SaveAsync(jobKey, DateTimeOffset.UtcNow, 30, CancellationToken.None);

        await _store.DeleteAsync(jobKey, CancellationToken.None);

        UnseenServantWatermark? loaded = await _store.GetAsync(jobKey, CancellationToken.None);

        Assert.Null(loaded);

    }

    [SkippableFact]
    public async Task Migration_creates_UnseenServantWatermarks_table()
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
            WHERE type = 'table' AND name = 'UnseenServantWatermarks'
            LIMIT 1;
            """;

        object? result = await cmd.ExecuteScalarAsync(CancellationToken.None);

        Assert.NotNull(result);

    }

}
