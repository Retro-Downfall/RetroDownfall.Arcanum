using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>RAG Phase 4 — <see cref="SagaMemoryStore"/> round-trip persistence against the real (managed-fallback) Grimoire schema.</summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SagaMemoryStoreTests : IAsyncLifetime
{

    /// <summary>
    /// Matches <see cref="ArcanumSettingClamps.EmbeddingsDimensions"/>'s 64-dimension floor — the
    /// smallest configured value that is not itself clamped up, so the store's dimension-validation
    /// guard (see <see cref="SagaMemoryStore.InsertAsync"/>) sees exactly this length.
    /// </summary>
    private const int TestDimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SagaMemoryStore? _store;

    public SagaMemoryStoreTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _store = new SagaMemoryStore(
            _db,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings { Embeddings = new EmbeddingSettings { Dimensions = TestDimensions } }));

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

    /// <summary>Builds a <see cref="TestDimensions"/>-length vector with <paramref name="leading"/> in its first slots and zeros elsewhere.</summary>
    private static float[] Vec(params float[] leading)
    {

        float[] result = new float[TestDimensions];

        leading.AsSpan().CopyTo(result);

        return result;

    }

    [SkippableFact]
    public async Task InsertAsync_then_ListAsync_RoundTrips()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await _store!.InsertAsync(
            "mem-1",
            "The operator prefers dark mode.",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            sessionId,
            tags: null,
            source: "extraction",
            Vec(1f),
            CancellationToken.None);

        SagaMemoryDto[] page = await _store.ListAsync(null, null, 100, 0, CancellationToken.None);

        SagaMemoryDto memory = Assert.Single(page);

        Assert.Equal("mem-1", memory.Id);

        Assert.Equal("The operator prefers dark mode.", memory.Content);

        Assert.Equal(sessionId, memory.SessionId);

        Assert.Equal("extraction", memory.Source);

    }

    [SkippableFact]
    public async Task ListAsync_FiltersBySessionIdAndQuery()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionA = Guid.NewGuid();

        Guid sessionB = Guid.NewGuid();

        await _store!.InsertAsync("mem-a", "prefers dark mode", DateTimeOffset.UtcNow, sessionA, null, "extraction", Vec(1f), CancellationToken.None);

        await _store.InsertAsync("mem-b", "uses xUnit for tests", DateTimeOffset.UtcNow, sessionB, null, "extraction", Vec(1f), CancellationToken.None);

        SagaMemoryDto[] bySession = await _store.ListAsync(null, sessionA, 100, 0, CancellationToken.None);

        Assert.Single(bySession);

        Assert.Equal("mem-a", bySession[0].Id);

        SagaMemoryDto[] byQuery = await _store.ListAsync("xunit", null, 100, 0, CancellationToken.None);

        Assert.Single(byQuery);

        Assert.Equal("mem-b", byQuery[0].Id);

    }

    [SkippableFact]
    public async Task ListAsync_RespectsLimitAndOffset()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        for (int i = 0; i < 5; i++)
        {

            await _store!.InsertAsync($"mem-{i}", $"memory {i}", DateTimeOffset.UtcNow.AddMinutes(i), null, null, "extraction", Vec(1f), CancellationToken.None);

        }

        SagaMemoryDto[] page1 = await _store!.ListAsync(null, null, 2, 0, CancellationToken.None);

        SagaMemoryDto[] page2 = await _store.ListAsync(null, null, 2, 2, CancellationToken.None);

        Assert.Equal(2, page1.Length);

        Assert.Equal(2, page2.Length);

        Assert.DoesNotContain(page2, m => page1.Any(p => p.Id == m.Id));

    }

    [SkippableFact]
    public async Task CountAsync_And_CountBySessionAsync_ReflectInserts()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await _store!.InsertAsync("mem-1", "a", DateTimeOffset.UtcNow, sessionId, null, "extraction", Vec(1f), CancellationToken.None);

        await _store.InsertAsync("mem-2", "b", DateTimeOffset.UtcNow, sessionId, null, "extraction", Vec(1f), CancellationToken.None);

        await _store.InsertAsync("mem-3", "c", DateTimeOffset.UtcNow, null, null, "extraction", Vec(1f), CancellationToken.None);

        Assert.Equal(3, await _store.CountAsync(CancellationToken.None));

        Assert.Equal(2, await _store.CountBySessionAsync(sessionId, CancellationToken.None));

    }

    [SkippableFact]
    public async Task GetByIdsAsync_ReturnsOnlyMatchingIds_MissingIdsAreAbsent()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _store!.InsertAsync("mem-1", "a", DateTimeOffset.UtcNow, null, null, "extraction", Vec(1f), CancellationToken.None);

        IReadOnlyDictionary<string, SagaMemoryDto> result = await _store.GetByIdsAsync(["mem-1", "mem-missing"], CancellationToken.None);

        Assert.Single(result);

        Assert.True(result.ContainsKey("mem-1"));

        Assert.False(result.ContainsKey("mem-missing"));

    }

    [SkippableFact]
    public async Task InsertAsync_RejectsEmbeddingWithWrongDimensions()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // The fixture's store is configured for Dimensions=64 (see TestDimensions); a shorter vector
        // must be rejected before any row is written, rather than silently corrupting the vec0 index.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store!.InsertAsync("mem-bad-dims", "a", DateTimeOffset.UtcNow, null, null, "extraction", [1f, 0f], CancellationToken.None));

        Assert.Equal(0, await _store!.CountAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task DeleteAsync_RemovesMemoryAndEmbedding_ReturnsFalseWhenMissing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _store!.InsertAsync("mem-1", "a", DateTimeOffset.UtcNow, null, null, "extraction", Vec(1f), CancellationToken.None);

        Assert.True(await _store.DeleteAsync("mem-1", CancellationToken.None));

        Assert.Equal(0, await _store.CountAsync(CancellationToken.None));

        Assert.False(await _store.DeleteAsync("mem-1", CancellationToken.None));

    }

    [SkippableFact]
    public async Task DeleteAllAsync_ClearsEverythingIncludingWatermarks()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await _store!.InsertAsync("mem-1", "a", DateTimeOffset.UtcNow, sessionId, null, "extraction", Vec(1f), CancellationToken.None);

        await _store.SetWatermarkAsync(sessionId, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.DeleteAllAsync(CancellationToken.None);

        Assert.Equal(0, await _store.CountAsync(CancellationToken.None));

        Assert.Null(await _store.GetWatermarkAsync(sessionId, CancellationToken.None));

    }

    [SkippableFact]
    public async Task GetStatsAsync_ReportsCountsAndTimestampBounds()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionA = Guid.NewGuid();

        Guid sessionB = Guid.NewGuid();

        await _store!.InsertAsync("mem-1", "a", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), sessionA, null, "extraction", Vec(1f), CancellationToken.None);

        await _store.InsertAsync("mem-2", "b", DateTimeOffset.Parse("2026-03-01T00:00:00Z"), sessionB, null, "extraction", Vec(1f), CancellationToken.None);

        SagaStats stats = await _store.GetStatsAsync(CancellationToken.None);

        Assert.Equal(2, stats.TotalCount);

        Assert.Equal(2, stats.SessionCount);

        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), stats.OldestCreatedAt);

        Assert.Equal(DateTimeOffset.Parse("2026-03-01T00:00:00Z"), stats.NewestCreatedAt);

    }

    [SkippableFact]
    public async Task GetStatsAsync_NoMemories_ReturnsZeroCountsAndNullBounds()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SagaStats stats = await _store!.GetStatsAsync(CancellationToken.None);

        Assert.Equal(0, stats.TotalCount);

        Assert.Equal(0, stats.SessionCount);

        Assert.Null(stats.OldestCreatedAt);

        Assert.Null(stats.NewestCreatedAt);

    }

    [SkippableFact]
    public async Task Watermark_GetIsNullInitially_SetThenGetRoundTrips_UpsertOverwrites()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        Assert.Null(await _store!.GetWatermarkAsync(sessionId, CancellationToken.None));

        DateTimeOffset first = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await _store.SetWatermarkAsync(sessionId, first, CancellationToken.None);

        Assert.Equal(first, await _store.GetWatermarkAsync(sessionId, CancellationToken.None));

        DateTimeOffset second = DateTimeOffset.Parse("2026-02-01T00:00:00Z");

        await _store.SetWatermarkAsync(sessionId, second, CancellationToken.None);

        Assert.Equal(second, await _store.GetWatermarkAsync(sessionId, CancellationToken.None));

    }

}
