using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Connection policy and the authorization functions schema triggers consult.
/// </summary>
/// <remarks>
/// The invariant under test is that privilege is opt-in and scoped. Every authorization function
/// starts denied on every connection — including a pooled connection handed back out — so a
/// privileged write is only ever possible inside an explicit scope that something has to open by
/// name and close again.
/// </remarks>
public sealed class CovenantSqliteConnectionInitializerTests : IDisposable
{

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "arcanum-initializer-tests",
        Path.GetRandomFileName());

    public CovenantSqliteConnectionInitializerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {

        SqliteConnection.ClearAllPools();

        try
        {

            if (Directory.Exists(_directory))
            {

                Directory.Delete(_directory, recursive: true);

            }

        }
        catch (IOException)
        {

            // Scratch under the OS temp root.

        }

    }

    [Fact]
    public async Task InitializeAsync_applies_foreign_keys_busy_timeout_secure_delete_and_cipher_policy()
    {

        await using SqliteConnection connection = await OpenAsync(CovenantSqliteConnectionMode.ReadWrite);

        Assert.Equal("1", await ScalarAsync(connection, "PRAGMA foreign_keys;"));

        Assert.Equal("1", await ScalarAsync(connection, "PRAGMA secure_delete;"));

        Assert.Equal("5000", await ScalarAsync(connection, "PRAGMA busy_timeout;"));

        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));

        Assert.Equal("4.17.0 community", await ScalarAsync(connection, "PRAGMA cipher_version;"));

    }

    /// <summary>
    /// A read-only handle must not try to change journal mode: on a database another connection
    /// holds, that attempt fails and would abort an otherwise valid read.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_read_only_mode_does_not_attempt_journal_mode_change()
    {

        string path = Path.Combine(_directory, "read-only-policy.db");

        await using (SqliteConnection writer = await OpenAsync(CovenantSqliteConnectionMode.ReadWrite, path))
        {

            await ExecuteAsync(writer, "CREATE TABLE t (id INTEGER PRIMARY KEY);");

        }

        await using SqliteConnection reader = await OpenAsync(CovenantSqliteConnectionMode.ReadOnly, path);

        Assert.Equal("1", await ScalarAsync(reader, "PRAGMA foreign_keys;"));

        Assert.Equal("1", await ScalarAsync(reader, "PRAGMA secure_delete;"));

    }

    [Fact]
    public async Task Every_authorization_function_begins_false()
    {

        await using SqliteConnection connection = await OpenAsync(CovenantSqliteConnectionMode.ReadWrite);

        foreach (CovenantSqliteAuthorizationKind kind in Enum.GetValues<CovenantSqliteAuthorizationKind>())
        {

            string function = CovenantSqliteConnectionInitializer.FunctionName(kind);

            Assert.Equal("0", await ScalarAsync(connection, $"SELECT {function}();"));

        }

    }

    [Fact]
    public async Task Authorize_enables_only_the_requested_function_until_disposed()
    {

        await using SqliteConnection connection = await OpenAsync(CovenantSqliteConnectionMode.ReadWrite);

        string owner = CovenantSqliteConnectionInitializer.FunctionName(
            CovenantSqliteAuthorizationKind.OwnerCleanup);

        string retention = CovenantSqliteConnectionInitializer.FunctionName(
            CovenantSqliteAuthorizationKind.SessionRetention);

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(
            connection,
            CovenantSqliteAuthorizationKind.OwnerCleanup))
        {

            Assert.Equal("1", await ScalarAsync(connection, $"SELECT {owner}();"));

            Assert.Equal("0", await ScalarAsync(connection, $"SELECT {retention}();"));

        }

        Assert.Equal("0", await ScalarAsync(connection, $"SELECT {owner}();"));

    }

    /// <summary>
    /// An inner scope closing must not revoke an outer one that is still open, or a nested helper
    /// would silently strip the privilege its caller is relying on.
    /// </summary>
    [Fact]
    public async Task Nested_authorization_scopes_restore_false_after_the_last_dispose()
    {

        await using SqliteConnection connection = await OpenAsync(CovenantSqliteConnectionMode.ReadWrite);

        string function = CovenantSqliteConnectionInitializer.FunctionName(
            CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(
            connection,
            CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance))
        {

            using (CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance))
            {

                Assert.Equal("1", await ScalarAsync(connection, $"SELECT {function}();"));

            }

            Assert.Equal("1", await ScalarAsync(connection, $"SELECT {function}();"));

        }

        Assert.Equal("0", await ScalarAsync(connection, $"SELECT {function}();"));

    }

    [Fact]
    public async Task Authorization_scope_is_bound_to_its_own_connection()
    {

        await using SqliteConnection first = await OpenAsync(CovenantSqliteConnectionMode.ReadWrite);

        await using SqliteConnection second = await OpenAsync(CovenantSqliteConnectionMode.ReadWrite);

        string function = CovenantSqliteConnectionInitializer.FunctionName(
            CovenantSqliteAuthorizationKind.ArtifactReplacement);

        using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(
            first,
            CovenantSqliteAuthorizationKind.ArtifactReplacement);

        Assert.True(scope.AppliesTo(first));

        Assert.False(scope.AppliesTo(second));

        // The grant on one connection must not authorize anything on another.
        Assert.Equal("0", await ScalarAsync(second, $"SELECT {function}();"));

    }

    /// <summary>
    /// Sanitizing managed authority out of restore staging rewrites authority state rather than
    /// data, so it is not reachable through the general entry point.
    /// </summary>
    [Fact]
    public async Task General_authorize_rejects_restore_staging_sanitization()
    {

        await using SqliteConnection connection = await OpenAsync(CovenantSqliteConnectionMode.ReadWrite);

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.RestoreStagingManagedAuthoritySanitization));

    }

    [Fact]
    public async Task Uninitialized_connection_cannot_be_authorized()
    {

        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "uninitialized.db"),

                Password = "key",

                Pooling = false,
            }.ToString());

        await connection.OpenAsync(CancellationToken.None);

        _ = Assert.Throws<InvalidOperationException>(
            () => CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.OwnerCleanup));

    }

    [Fact]
    public async Task Reinitialized_connection_resets_every_authorization()
    {

        await using SqliteConnection connection = await OpenAsync(CovenantSqliteConnectionMode.ReadWrite);

        string function = CovenantSqliteConnectionInitializer.FunctionName(
            CovenantSqliteAuthorizationKind.TurnCapacityMutation);

        // Deliberately leaked: a scope that is never disposed must still not survive the next
        // initialization of the same connection, which is what a pooled handout looks like.
        _ = CovenantSqliteConnectionInitializer.Instance.Authorize(
            connection,
            CovenantSqliteAuthorizationKind.TurnCapacityMutation);

        Assert.Equal("1", await ScalarAsync(connection, $"SELECT {function}();"));

        await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.Equal("0", await ScalarAsync(connection, $"SELECT {function}();"));

    }

    [Fact]
    public async Task InitializeAsync_rejects_a_connection_that_is_not_open()
    {

        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "closed.db"),

                Password = "key",
            }.ToString());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
                connection,
                CovenantSqliteConnectionMode.ReadWrite,
                CancellationToken.None));

    }

    private async Task<SqliteConnection> OpenAsync(
        CovenantSqliteConnectionMode mode,
        string? path = null)
    {

        // The provider has to be installed before a connection is constructed, which is the
        // ordering every production open follows.
        SqliteNativeRuntime.Instance.Initialize();

        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path ?? Path.Combine(_directory, $"{Path.GetRandomFileName()}.db"),

                Password = "initializer-test-key",

                Pooling = false,
            }.ToString());

        await connection.OpenAsync(CancellationToken.None);

        await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
            connection,
            mode,
            CancellationToken.None);

        return connection;

    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(CancellationToken.None);

        return value is null or DBNull ? null : Convert.ToString(value);

    }

}
