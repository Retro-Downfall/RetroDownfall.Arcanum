using System.Data.Common;
using System.Globalization;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// One encrypted, temporary Grimoire at the current Core schema for Saga curation suites that need a
/// live connection but not the xunit collection-fixture wiring <see cref="GrimoireFixture"/> otherwise
/// requires.
/// </summary>
/// <remarks>
/// <see cref="GrimoireFixture"/> already does the real work — building and caching the schema template
/// once per process and handing out cheap copies — so this wraps one rather than repeating it. What it
/// adds is a call site a single test method can use without joining <c>[Collection("Grimoire")]</c> and
/// implementing <see cref="IAsyncLifetime"/> itself.
/// </remarks>
public sealed class SagaStoreHarness : IAsyncDisposable
{

    private readonly GrimoireFixture _fixture;

    private readonly ArcanumDbContext _db;

    private bool _disposed;

    private SagaStoreHarness(GrimoireFixture fixture, ArcanumDbContext db)
    {

        _fixture = fixture;

        _db = db;

    }

    /// <summary>The open connection into the temporary Grimoire.</summary>
    public DbConnection Connection => _db.Database.GetDbConnection();

    /// <summary>Builds a fresh temporary Grimoire, skipping the calling test when SQLCipher is unavailable.</summary>
    public static Task<SagaStoreHarness> CreateAsync()
    {

        // Must run before the fixture is constructed: GrimoireFixture's constructor silently no-ops
        // when SQLCipher is unavailable rather than throwing, so CopyDatabase() below would fail with a
        // FileNotFoundException instead of a clean skip if this check came after it.
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireFixture fixture = new();

        ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        return Task.FromResult(new SagaStoreHarness(fixture, db));

    }

    /// <summary>Counts rows in one table matching a caller-supplied predicate.</summary>
    public async Task<int> CountAsync(string table, string predicate)
    {

        ArgumentException.ThrowIfNullOrEmpty(table);

        ArgumentException.ThrowIfNullOrEmpty(predicate);

        await using DbCommand command = Connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {predicate}";

        object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);

        return Convert.ToInt32(result, CultureInfo.InvariantCulture);

    }

    public async ValueTask DisposeAsync()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        await _db.DisposeAsync().ConfigureAwait(false);

        // Deletes the one copy CreateAsync made, including its -wal/-shm/.kdf siblings.
        _fixture.Dispose();

    }

}
