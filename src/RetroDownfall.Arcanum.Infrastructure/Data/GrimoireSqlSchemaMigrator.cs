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
        "20260508212137_InitialCreate",
        "20260508215834_AddChatMessagesFts",
        "20260509005818_AddCampaignLogFields",
        "20260509195722_EvolveWorkspaceContextForChronosync",
        "20260510205005_AddTotalTokensUsed",
        "20260615225822_AddTheForgeCampaignsAndPrompts",
        "20260615234706_AddApprentices",
        "20260616001002_AddCampaignSanctumConfig",
        "20260616010843_RenameSessionAndEntry",
        "20260616020000_AddSessionQueryIndexes",
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

        if (TrySqliteExec(db, script, out string? error))
        {

            return;

        }

        if (error.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {

            string withoutAddColumn = StripAddColumnStatements(script);

            if (TrySqliteExec(db, withoutAddColumn, out error))
            {

                return;

            }

        }

        throw new InvalidOperationException(error);

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

    private static string StripAddColumnStatements(string script)
    {

        string[] lines = script.Split('\n');

        List<string> kept = new(lines.Length);

        for (int i = 0; i < lines.Length; i++)
        {

            string line = lines[i];

            if (line.TrimStart().StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase)
                && line.Contains(" ADD", StringComparison.OrdinalIgnoreCase))
            {

                continue;

            }

            kept.Add(line);

        }

        return string.Join('\n', kept);

    }

}
