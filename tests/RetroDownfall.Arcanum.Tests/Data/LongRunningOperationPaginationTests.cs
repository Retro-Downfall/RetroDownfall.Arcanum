using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LongRunningOperationPaginationTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _dbPath = string.Empty;

    private ArcanumDbContext _db = null!;

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await _db.DisposeAsync();

        SqliteConnection.ClearPool(connection);

        File.Delete(_dbPath);
    }

    [SkippableTheory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(501, 0)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(100, -1)]
    [InlineData(100, int.MaxValue)]
    [InlineData(100, int.MaxValue - 50)]
    public async Task List_refuses_invalid_page_arguments_instead_of_silently_clamping(int limit, int offset)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using LongRunningOperationStore store = new(_db, TestOrdinaryConnectionFactory.For(_db));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ListAsync(new LongRunningOperationQuery(Limit: limit, Offset: offset)));
    }

    [SkippableTheory]
    [InlineData(100, 0, 100, false)]
    [InlineData(500, 0, 500, false)]
    [InlineData(100, 500, 20, false)]
    [InlineData(200, 0, 200, false)]
    [InlineData(1, 0, 1, false)]
    [InlineData(100, 0, 101, true)]
    [InlineData(500, 0, 501, true)]
    [InlineData(100, 500, 20, true)]
    [InlineData(100, int.MaxValue - 100, 0, false)]
    [InlineData(100, int.MaxValue - 100, 0, true)]
    public async Task Ordinary_store_call_preserves_requested_page_size_and_date_then_identity_order(int limit, int offset, int expectedCount, bool includeLookahead)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using LongRunningOperationStore store = new(_db, TestOrdinaryConnectionFactory.For(_db));

        DateTimeOffset older = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        List<LongRunningOperation> created = [];

        for (int index = 0; index < 520; index++)
        {
            created.Add(await store.CreateAsync(new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.BlobEncryptionMigration,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Page fixture operation.",
                index % 2 == 0 ? older : older.AddDays(1))));
        }

        LongRunningOperation[] expected = created
            .OrderByDescending(static row => row.CreatedAt)
            .ThenBy(static row => row.Id.ToString("N"), StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit + (includeLookahead ? 1 : 0))
            .ToArray();

        IReadOnlyList<LongRunningOperation> actual = await store.ListAsync(new LongRunningOperationQuery(
            Limit: limit, Offset: offset, IncludeLookahead: includeLookahead));

        Assert.Equal(expectedCount, actual.Count);

        Assert.Equal(expected.Select(static row => row.Id), actual.Select(static row => row.Id));
    }
}
