using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Catalog-only classification retained for the published Core26 transition.</summary>
internal static class GrimoireLegacyVectorMirrors
{
    internal static async Task<bool> IsPlainAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=$table AND type IN ('table','view') LIMIT 1;";

        _ = command.Parameters.AddWithValue("$table", table);

        object? definition = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return definition is string sql
            && !sql.Trim().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);
    }
}
