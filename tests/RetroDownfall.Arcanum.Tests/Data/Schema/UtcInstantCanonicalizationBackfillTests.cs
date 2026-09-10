using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class UtcInstantCanonicalizationBackfillTests
{
    static UtcInstantCanonicalizationBackfillTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task A_page_is_bounded_resumable_and_restores_an_append_only_guard()
    {
        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE ledger (Id INTEGER PRIMARY KEY, RecordedAtUtc TEXT NULL);
            CREATE TRIGGER ledger_guard_update
            BEFORE UPDATE ON ledger
            BEGIN
                SELECT RAISE(ABORT, 'ledger is append-only');
            END;
            """);

        await using (SqliteTransaction seed = connection.BeginTransaction())
        {
            await using SqliteCommand insert = connection.CreateCommand();

            insert.Transaction = seed;

            insert.CommandText =
                "INSERT INTO ledger (RecordedAtUtc) VALUES ('2026-09-08T23:00:00.0000001-04:00');";

            for (int row = 0; row < 300; row++)
            {
                _ = await insert.ExecuteNonQueryAsync();
            }

            await seed.CommitAsync();
        }

        UtcInstantCanonicalizationBackfill backfill = new(
            "test-utc",
            [new UtcInstantTable("ledger", ["RecordedAtUtc"])]);

        GrimoireSchemaBackfillBatch first;

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            first = await backfill.AdvanceBatchAsync(
                connection,
                transaction,
                cursor: null,
                CancellationToken.None);

            await transaction.CommitAsync();
        }

        Assert.Equal(256, first.RowsProcessed);

        Assert.False(first.IsComplete);

        GrimoireSchemaBackfillBatch second;

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            second = await backfill.AdvanceBatchAsync(
                connection,
                transaction,
                first.NextCursor,
                CancellationToken.None);

            await transaction.CommitAsync();
        }

        Assert.Equal(44, second.RowsProcessed);

        Assert.True(second.IsComplete);

        Assert.Equal(
            300L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM ledger WHERE RecordedAtUtc = '2026-09-09T03:00:00.0000001Z';"));

        SqliteException guard = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(connection, "UPDATE ledger SET RecordedAtUtc = RecordedAtUtc WHERE Id = 1;"));

        Assert.Contains("append-only", guard.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_text_prevalidates_the_whole_page_and_leaves_data_and_guards_untouched()
    {
        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE ledger (Id INTEGER PRIMARY KEY, RecordedAtUtc TEXT NULL);
            INSERT INTO ledger VALUES (1, '2026-09-08T23:00:00-04:00');
            INSERT INTO ledger VALUES (2, 'not-an-instant');
            CREATE TRIGGER ledger_guard_update
            BEFORE UPDATE ON ledger
            BEGIN
                SELECT RAISE(ABORT, 'ledger is append-only');
            END;
            """);

        UtcInstantCanonicalizationBackfill backfill = new(
            "test-utc",
            [new UtcInstantTable("ledger", ["RecordedAtUtc"])]);

        await using SqliteTransaction transaction = connection.BeginTransaction();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => backfill.AdvanceBatchAsync(
                connection,
                transaction,
                cursor: null,
                CancellationToken.None));

        await transaction.RollbackAsync();

        Assert.Contains("ledger.RecordedAtUtc", error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("not-an-instant", error.Message, StringComparison.Ordinal);

        Assert.Equal(
            "2026-09-08T23:00:00-04:00",
            await ScalarStringAsync(connection, "SELECT RecordedAtUtc FROM ledger WHERE Id = 1;"));

        Assert.Equal(
            1L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'trigger' AND name = 'ledger_guard_update';"));
    }

    [Fact]
    public async Task Failed_update_restores_the_guard_inside_the_open_transaction_and_rollback_keeps_both()
    {
        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE ledger (
                Id INTEGER PRIMARY KEY,
                RecordedAtUtc TEXT NOT NULL CHECK (RecordedAtUtc NOT LIKE '%Z')
            );
            INSERT INTO ledger VALUES (1, '2026-09-08T23:00:00-04:00');
            CREATE TRIGGER ledger_guard_update
            BEFORE UPDATE ON ledger
            BEGIN
                SELECT RAISE(ABORT, 'ledger is append-only');
            END;
            """);

        UtcInstantCanonicalizationBackfill backfill = new(
            "test-utc",
            [new UtcInstantTable("ledger", ["RecordedAtUtc"])]);

        await using SqliteTransaction transaction = connection.BeginTransaction();

        _ = await Assert.ThrowsAsync<SqliteException>(
            () => backfill.AdvanceBatchAsync(
                connection,
                transaction,
                cursor: null,
                CancellationToken.None));

        Assert.Equal(
            1L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'trigger' AND name = 'ledger_guard_update';",
                transaction));

        Assert.Equal(
            "2026-09-08T23:00:00-04:00",
            await ScalarStringAsync(
                connection,
                "SELECT RecordedAtUtc FROM ledger WHERE Id = 1;",
                transaction));

        await transaction.RollbackAsync();

        Assert.Equal(
            1L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'trigger' AND name = 'ledger_guard_update';"));

        Assert.Equal(
            "2026-09-08T23:00:00-04:00",
            await ScalarStringAsync(connection, "SELECT RecordedAtUtc FROM ledger WHERE Id = 1;"));
    }

    [Fact]
    public async Task Nulls_are_preserved_and_a_cancelled_page_commits_nothing()
    {
        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE mutable_state (Id INTEGER PRIMARY KEY, UpdatedAtUtc TEXT NULL);
            INSERT INTO mutable_state VALUES (1, NULL);
            INSERT INTO mutable_state VALUES (2, '2026-09-08T23:00:00-04:00');
            """);

        UtcInstantCanonicalizationBackfill backfill = new(
            "test-utc",
            [new UtcInstantTable("mutable_state", ["UpdatedAtUtc"])]);

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => backfill.AdvanceBatchAsync(
                connection,
                transaction,
                cursor: null,
                cancellation.Token));

        await transaction.RollbackAsync();

        Assert.Equal(
            1L,
            await ScalarInt64Async(connection, "SELECT COUNT(*) FROM mutable_state WHERE UpdatedAtUtc IS NULL;"));

        Assert.Equal(
            "2026-09-08T23:00:00-04:00",
            await ScalarStringAsync(connection, "SELECT UpdatedAtUtc FROM mutable_state WHERE Id = 2;"));
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return (string)(await command.ExecuteScalarAsync())!;
    }
}
