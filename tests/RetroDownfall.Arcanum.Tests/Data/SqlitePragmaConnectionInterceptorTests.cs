using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
public sealed class SqlitePragmaConnectionInterceptorTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public SqlitePragmaConnectionInterceptorTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

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
    public async Task ConnectionOpened_applies_wal_and_foreign_keys()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _ = await _db!.Sessions.CountAsync(CancellationToken.None);

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(CancellationToken.None);

        }

        string journalMode = await ReadPragmaAsync(connection, "journal_mode", CancellationToken.None);

        long foreignKeys = await ReadPragmaLongAsync(connection, "foreign_keys", CancellationToken.None);

        Assert.Equal("wal", journalMode, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(1L, foreignKeys);

    }

    private static async Task<string> ReadPragmaAsync(SqliteConnection connection, string pragma, CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText = $"PRAGMA {pragma};";

        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken);

        return Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    }

    private static async Task<long> ReadPragmaLongAsync(SqliteConnection connection, string pragma, CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText = $"PRAGMA {pragma};";

        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);

    }

}
