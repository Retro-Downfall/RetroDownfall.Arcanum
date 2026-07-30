using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
public sealed class GrimoireSqlSchemaMigratorTests : IAsyncLifetime
{

    private const int ExpectedMigrationCount = 8;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private SqliteConnection? _connection;

    public GrimoireSqlSchemaMigratorTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"migrator-{Guid.NewGuid():N}.db");

        return Task.CompletedTask;

    }

    public Task DisposeAsync()
    {
        if (_connection is not null)
        {
            SqliteConnection.ClearPool(_connection);
        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        return Task.CompletedTask;

    }

    [Fact]
    public void CompiledEfModel_DoesNotMapRawSqlBillableOperationsTable()
    {
        Assert.DoesNotContain(
            ArcanumDbContextModel.Instance.GetEntityTypes(),
            static entityType => string.Equals(
                entityType.GetTableName(),
                "BillableOperations",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompiledEfModel_DoesNotMapRawSqlSessionContextPinsTable()
    {
        Assert.DoesNotContain(
            ArcanumDbContextModel.Instance.GetEntityTypes(),
            static entityType => string.Equals(
                entityType.GetTableName(),
                "SessionContextPins",
                StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ApplyPendingAsync_is_idempotent_when_run_twice()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SqliteConnection connection = await OpenConnectionAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        int firstCount = await CountAppliedMigrationsAsync(connection, CancellationToken.None);

        Assert.Equal(ExpectedMigrationCount, firstCount);

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        int secondCount = await CountAppliedMigrationsAsync(connection, CancellationToken.None);

        Assert.Equal(firstCount, secondCount);

    }

    [SkippableFact]
    public async Task ApplyPendingAsync_records_one_history_row_per_migration()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SqliteConnection connection = await OpenConnectionAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        int count = await CountAppliedMigrationsAsync(connection, CancellationToken.None);

        Assert.Equal(ExpectedMigrationCount, count);

    }

    [SkippableFact]
    public async Task ApplyPendingAsync_rename_migration_leaves_sessions_and_entries()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SqliteConnection connection = await OpenConnectionAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        Assert.True(await TableExistsAsync(connection, "Sessions", CancellationToken.None));

        Assert.True(await TableExistsAsync(connection, "Entries", CancellationToken.None));

        Assert.False(await TableExistsAsync(connection, "Conversations", CancellationToken.None));

        Assert.False(await TableExistsAsync(connection, "ChatMessages", CancellationToken.None));

        Assert.True(await TableExistsAsync(connection, "Entries_fts", CancellationToken.None));

    }

    [SkippableFact]
    public async Task ApplyPendingAsync_creates_SessionAttachments_table()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SqliteConnection connection = await OpenConnectionAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        Assert.True(await TableExistsAsync(connection, "SessionAttachments", CancellationToken.None));
        Assert.True(await ColumnExistsAsync(
            connection,
            "SessionAttachments",
            "SourceKind",
            CancellationToken.None));
        Assert.True(await ColumnExistsAsync(
            connection,
            "SessionAttachments",
            "SourceRelativePath",
            CancellationToken.None));

    }

    [SkippableFact]
    public async Task ApplyPendingAsync_foreign_key_check_passes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SqliteConnection connection = await OpenConnectionAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText = "PRAGMA foreign_key_check;";

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(CancellationToken.None);

        Assert.False(await reader.ReadAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task ApplyMigrationInTransactionAsync_rolls_back_when_script_fails()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SqliteConnection connection = await OpenConnectionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeApplyMigrationInTransactionAsync(
                connection,
                "20260705171559_InitialCreate",
                "CREATE TABLE broken_syntax (;",
                CancellationToken.None));

        Assert.False(await HistoryTableExistsAsync(connection, CancellationToken.None));

        Assert.False(await TableExistsAsync(connection, "broken_syntax", CancellationToken.None));

    }

    [SkippableFact]
    public async Task ApplyPendingAsync_can_reapply_last_migration_after_history_row_removed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SqliteConnection connection = await OpenConnectionAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        const string lastMigrationId = "20260719190000_AddSessionAttachmentUniqueIndexes";

        await using (SqliteCommand deleteHistory = connection.CreateCommand())
        {

            deleteHistory.CommandText = """
                DELETE FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = $migrationId;
                """;

            deleteHistory.Parameters.AddWithValue("$migrationId", lastMigrationId);

            _ = await deleteHistory.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await using (SqliteCommand dropBound = connection.CreateCommand())
        {

            dropBound.CommandText = """DROP INDEX IF EXISTS UX_SessionAttachments_Bound;""";

            _ = await dropBound.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await using (SqliteCommand dropPending = connection.CreateCommand())
        {

            dropPending.CommandText = """DROP INDEX IF EXISTS UX_SessionAttachments_Pending;""";

            _ = await dropPending.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        Assert.Equal(ExpectedMigrationCount, await CountAppliedMigrationsAsync(connection, CancellationToken.None));

        Assert.True(await IndexExistsAsync(connection, "UX_SessionAttachments_Bound", CancellationToken.None));

        Assert.True(await IndexExistsAsync(connection, "UX_SessionAttachments_Pending", CancellationToken.None));

    }

    [SkippableFact]
    public async Task ApplyPendingAsync_creates_session_attachment_partial_unique_indexes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SqliteConnection connection = await OpenConnectionAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        Assert.True(await IndexExistsAsync(connection, "UX_SessionAttachments_Bound", CancellationToken.None));

        Assert.True(await IndexExistsAsync(connection, "UX_SessionAttachments_Pending", CancellationToken.None));

    }

    [SkippableFact]
    public async Task ApplyPendingAsync_creates_non_null_reasoning_tokens_column_with_zero_default()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SqliteConnection connection = await OpenConnectionAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        (bool exists, bool notNull, string? defaultValue) =
            await ReadReasoningTokensColumnAsync(connection, CancellationToken.None);

        Assert.True(exists);
        Assert.True(notNull);
        Assert.Equal("0", defaultValue);
    }

    [SkippableFact]
    public async Task ApplyPendingAsync_can_reapply_inference_accounting_migration_idempotently()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using SqliteConnection connection = await OpenConnectionAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        await using (SqliteCommand deleteHistory = connection.CreateCommand())
        {
            deleteHistory.CommandText = """
                DELETE FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = $migrationId;
                """;
            deleteHistory.Parameters.AddWithValue(
                "$migrationId",
                "20260721010000_AddInferenceAccountingAndIdempotencyClaims");

            _ = await deleteHistory.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        Assert.Equal(ExpectedMigrationCount, await CountAppliedMigrationsAsync(connection, CancellationToken.None));

        (bool exists, bool notNull, string? defaultValue) =
            await ReadReasoningTokensColumnAsync(connection, CancellationToken.None);
        Assert.True(exists);
        Assert.True(notNull);
        Assert.Equal("0", defaultValue);
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {

        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = _fixture.Passphrase,
        }.ToString());

        await connection.OpenAsync(CancellationToken.None);

        _connection = connection;

        return connection;

    }

    private static Task InvokeApplyMigrationInTransactionAsync(
        SqliteConnection connection,
        string migrationId,
        string script,
        CancellationToken cancellationToken)
    {

        MethodInfo? method = typeof(GrimoireSqlSchemaMigrator).GetMethod(
            "ApplyMigrationInTransactionAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        object? task = method.Invoke(null, [connection, migrationId, script, cancellationToken]);

        Assert.NotNull(task);

        return (Task)task;

    }

    private static async Task<int> CountAppliedMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText = """
            SELECT COUNT(*)
            FROM "__EFMigrationsHistory";
            """;

        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);

    }

    private static async Task<bool> HistoryTableExistsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = '__EFMigrationsHistory'
            LIMIT 1;
            """;

        object? result = await cmd.ExecuteScalarAsync(cancellationToken);

        return result is not null && result != DBNull.Value;

    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type IN ('table', 'view') AND name = $tableName
            LIMIT 1;
            """;

        cmd.Parameters.AddWithValue("$tableName", tableName);

        object? result = await cmd.ExecuteScalarAsync(cancellationToken);

        return result is not null && result != DBNull.Value;

    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string indexName, CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'index' AND name = $indexName
            LIMIT 1;
            """;

        cmd.Parameters.AddWithValue("$indexName", indexName);

        object? result = await cmd.ExecuteScalarAsync(cancellationToken);

        return result is not null && result != DBNull.Value;

    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<(bool Exists, bool NotNull, string? DefaultValue)> ReadReasoningTokensColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """PRAGMA table_info("BillableOperations");""";

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (!string.Equals(reader.GetString(1), "ReasoningTokens", StringComparison.Ordinal))
            {
                continue;
            }

            bool notNull = reader.GetInt32(3) == 1;
            string? defaultValue = reader.IsDBNull(4) ? null : reader.GetString(4);

            return (true, notNull, defaultValue);
        }

        return (false, false, null);
    }

}
