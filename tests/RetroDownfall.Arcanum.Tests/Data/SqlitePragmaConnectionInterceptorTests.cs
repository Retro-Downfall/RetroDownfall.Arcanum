using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Security;
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

    /// <summary>
    /// Raw-SQL stores share the scoped DbContext's connection, so whichever one touches the Grimoire
    /// first in a request decides the pragmas for the whole scope. Opening that connection outside
    /// EF's pipeline never fires <see cref="SqlitePragmaConnectionInterceptor"/>, leaving
    /// <c>busy_timeout</c> and <c>synchronous</c> at SQLite's defaults instead of the contract in
    /// DESIGN §5.4.4 — a contended write then fails immediately rather than waiting out the lock.
    /// </summary>
    [Fact]
    public async Task RawSqlStore_opening_the_connection_first_still_applies_the_pragmas()
    {

        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "arcanum-pragma-" + Guid.NewGuid().ToString("N") + ".db");

        string connectionString = new SqliteConnectionStringBuilder
        {

            DataSource = databasePath,

            Pooling = false,

        }.ToString();

        try
        {

            await using (SqliteConnection install = new(connectionString))
            {

                await install.OpenAsync(CancellationToken.None);

                _ = await GrimoireSchemaInstaller.InstallAsync(
                    install,
                    1536,
                    logger: null,
                    CancellationToken.None);

            }

            DbContextOptions<ArcanumDbContext> options = new DbContextOptionsBuilder<ArcanumDbContext>()
                .UseSqlite(connectionString)
                .UseModel(ArcanumDbContextModel.Instance)
                .AddInterceptors(SqlitePragmaConnectionInterceptor.Instance)
                .Options;

            await using ArcanumDbContext db = new(
                options,
                new UnusedSecretStore(),
                new UnusedPassphraseSource());

            IdempotencyClaimStore store = new(db);

            Assert.Null(await store.TryGetAsync("no-such-claim", CancellationToken.None));

            SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

            Assert.Equal(
                (long)SqliteConnectionPragmas.BusyTimeoutMs,
                await ReadPragmaLongAsync(connection, "busy_timeout", CancellationToken.None));

            Assert.Equal(
                1L,
                await ReadPragmaLongAsync(connection, "synchronous", CancellationToken.None));

        }
        finally
        {

            foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {

                if (File.Exists(path))
                {

                    File.Delete(path);

                }

            }

        }

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

    private sealed class UnusedSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            throw new NotSupportedException("Unused: the options are pre-configured.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new NotSupportedException("Unused: the options are pre-configured.");

        public Task SaveApiKeyAsync(string apiKey) =>
            throw new NotSupportedException("Unused: the options are pre-configured.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            throw new NotSupportedException("Unused: the options are pre-configured.");

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            throw new NotSupportedException("Unused: the options are pre-configured.");

    }

    private sealed class UnusedPassphraseSource : IGrimoireDbPassphraseSource
    {

        public string Passphrase =>
            throw new NotSupportedException("Unused: the options are pre-configured.");

        public void SetPassphrase(string passphrase) =>
            throw new NotSupportedException("Unused: the options are pre-configured.");

    }

}
