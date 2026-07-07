using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Applies embedded Grimoire SQL migration scripts without EF <c>MigrateAsync</c> (AOT-safe).
/// Each script and its <c>__EFMigrationsHistory</c> row run inside one SQLite transaction.
/// </summary>
internal static class GrimoireSqlSchemaMigrator
{

    private static readonly string[] MigrationOrder =
    [
        "20260705171559_InitialCreate",
        "20260706040000_AddSessionTotalCostUsd",
        "20260706040100_AddBudgetAlerts",
        "20260706040200_AddEntriesIsPinned",
    ];

    private const string ResourcePrefix = "RetroDownfall.Arcanum.Infrastructure.Data.SqlMigrations.";

    private const string ProductVersion = "10.0.8";

    public static async Task ApplyPendingAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        HashSet<string> applied = await ReadAppliedMigrationIdsAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (string migrationId in MigrationOrder)
        {

            if (applied.Contains(migrationId))
            {

                continue;

            }

            string script = LoadEmbeddedScript(migrationId);

            await ApplyMigrationInTransactionAsync(connection, migrationId, script, cancellationToken).ConfigureAwait(false);

            applied.Add(migrationId);

        }

    }

    private static async Task ApplyMigrationInTransactionAsync(
        SqliteConnection connection,
        string migrationId,
        string script,
        CancellationToken cancellationToken)
    {

        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            ExecuteScript(connection, script, cancellationToken);

            await RecordMigrationAsync(connection, migrationId, transaction, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;

        }

    }

    private static async Task RecordMigrationAsync(
        SqliteConnection connection,
        string migrationId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.Transaction = transaction;

        cmd.CommandText = """
            INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ($migrationId, $productVersion);
            """;

        cmd.Parameters.AddWithValue("$migrationId", migrationId);

        cmd.Parameters.AddWithValue("$productVersion", ProductVersion);

        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task<HashSet<string>> ReadAppliedMigrationIdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        HashSet<string> applied = new(StringComparer.Ordinal);

        if (!await HistoryTableExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {

            return applied;

        }

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText = """
            SELECT "MigrationId"
            FROM "__EFMigrationsHistory";
            """;

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string id = reader.GetString(0);

            _ = applied.Add(id);

        }

        return applied;

    }

    private static async Task<bool> HistoryTableExistsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = '__EFMigrationsHistory'
            LIMIT 1;
            """;

        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is not null && result != DBNull.Value;

    }

    private static string LoadEmbeddedScript(string migrationId)
    {

        string resourceName = ResourcePrefix + migrationId + ".sql";

        Assembly assembly = typeof(GrimoireSqlSchemaMigrator).Assembly;

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {

            throw new InvalidOperationException($"Embedded Grimoire migration resource not found: {resourceName}");

        }

        using StreamReader reader = new(stream, Encoding.UTF8);

        return reader.ReadToEnd();

    }

    private static void ExecuteScript(SqliteConnection connection, string script, CancellationToken cancellationToken)
    {

        if (connection.State != System.Data.ConnectionState.Open)
        {

            throw new InvalidOperationException("Grimoire migration requires an open SQLite connection.");

        }

        sqlite3 db = connection.Handle ?? throw new InvalidOperationException("SQLite connection handle is not available.");

        cancellationToken.ThrowIfCancellationRequested();

        string guardedScript = GuardExistingAddColumns(connection, script);

        if (TrySqliteExec(db, guardedScript, out string? error))
        {

            return;

        }

        throw new InvalidOperationException(error);

    }

    private static string GuardExistingAddColumns(SqliteConnection connection, string script)
    {

        string[] lines = script.Split('\n');

        List<string> kept = new(lines.Length);

        foreach (string line in lines)
        {

            string trimmed = line.Trim();

            if (TryParseAddColumn(trimmed, out string? tableName, out string? columnName))
            {

                if (ColumnExists(connection, tableName!, columnName!))
                {

                    continue;

                }

            }

            kept.Add(line);

        }

        return string.Join('\n', kept);

    }

    private static bool TryParseAddColumn(string statement, out string? tableName, out string? columnName)
    {

        tableName = null;

        columnName = null;

        if (!statement.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase))
        {

            return false;

        }

        if (!statement.Contains(" ADD", StringComparison.OrdinalIgnoreCase))
        {

            return false;

        }

        int tableStart = statement.IndexOf('"');

        if (tableStart < 0)
        {

            return false;

        }

        int tableEnd = statement.IndexOf('"', tableStart + 1);

        if (tableEnd < 0)
        {

            return false;

        }

        tableName = statement.Substring(tableStart + 1, tableEnd - tableStart - 1);

        int columnStart = statement.IndexOf('"', tableEnd + 1);

        if (columnStart < 0)
        {

            return false;

        }

        int columnEnd = statement.IndexOf('"', columnStart + 1);

        if (columnEnd < 0)
        {

            return false;

        }

        columnName = statement.Substring(columnStart + 1, columnEnd - columnStart - 1);

        return true;

    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {

        try
        {

            using SqliteCommand cmd = connection.CreateCommand();

            cmd.CommandText = """
                SELECT 1
                FROM pragma_table_info($tableName)
                WHERE name = $columnName
                LIMIT 1;
                """;

            cmd.Parameters.AddWithValue("$tableName", tableName);

            cmd.Parameters.AddWithValue("$columnName", columnName);

            object? result = cmd.ExecuteScalar();

            return result is not null && result != DBNull.Value;

        }
        catch
        {

            return false;

        }

    }

    private static bool TrySqliteExec(sqlite3 db, string script, out string error)
    {

        int rc = raw.sqlite3_exec(db, script);

        if (rc == raw.SQLITE_OK)
        {

            error = string.Empty;

            return true;

        }

        utf8z err = raw.sqlite3_errmsg(db);

        error = err.utf8_to_string();

        if (string.IsNullOrEmpty(error))
        {

            error = $"sqlite3_exec failed with code {rc}";

        }

        return false;

    }

}
