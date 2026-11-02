using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Retires obsolete vector mirrors before the installation admits ordinary readers or writers.</summary>
internal sealed class LegacyVectorMirrorRetirementBackfill : IGrimoireQuiescentBackfill
{
    private static readonly (string Table, string Key)[] Mirrors =
    [
        ("entry_embeddings_vec", "EntryId"),
        ("workspace_file_embeddings_vec", "ChunkId"),
        ("session_attachment_embeddings_vec", "ChunkId"),
        ("saga_memory_embeddings_vec", "MemoryId"),
        ("tapestry_node_embeddings_vec", "NodeId"),
    ];

    public string Name => "legacy-vector-mirror-retirement";

    public int MaxRowsPerBatch => 5;

    internal static bool IsRetiredName(string name) => Mirrors.Any(mirror => string.Equals(mirror.Table, name, StringComparison.OrdinalIgnoreCase));

    public Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(SqliteConnection connection,
        SqliteTransaction transaction, string? cursor, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Legacy vector mirror retirement requires quiescent schema admission.");

    public async Task<GrimoireSchemaBackfillBatch> AdvanceQuiescentBatchAsync(SqliteConnection connection,
        SqliteTransaction transaction, string? cursor, GrimoireSchemaQuiescentExecution execution,
        CancellationToken cancellationToken)
    {
        execution.Require(connection);

        if (cursor is not null)
        {
            throw new InvalidOperationException("Legacy vector mirror retirement has an invalid checkpoint.");
        }

        List<string> retired = [];

        // Validate the complete closed set before dropping anything. In particular, DROP TABLE on an
        // unsupported virtual table needs its missing module; catalog surgery is not a migration path.
        foreach ((string table, string key) in Mirrors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ValidateAsync(connection, transaction, table, key, cancellationToken).ConfigureAwait(false))
            {
                retired.Add(table);
            }
        }

        foreach (string table in retired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using SqliteCommand drop = connection.CreateCommand();

            drop.Transaction = transaction;

            // Identifiers come exclusively from Mirrors above; no catalog or operator text is executed.
            drop.CommandText = $"""DROP TABLE "{table}";""";

            _ = await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new(null, retired.Count, true);
    }

    private static async Task<bool> ValidateAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string key, CancellationToken cancellationToken)
    {
        await using SqliteCommand definition = connection.CreateCommand();

        definition.Transaction = transaction;

        definition.CommandText = "SELECT type,sql FROM sqlite_schema WHERE name=$table COLLATE NOCASE LIMIT 1;";

        _ = definition.Parameters.AddWithValue("$table", table);

        await using (SqliteDataReader reader = await definition.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (reader.GetString(0) != "table" || reader.IsDBNull(1)
                || !reader.GetString(1).TrimStart().StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
            {
                throw Unsupported(table);
            }
        }

        await using SqliteCommand columns = connection.CreateCommand();

        columns.Transaction = transaction;

        columns.CommandText = "SELECT name,type,\"notnull\",pk,hidden,dflt_value FROM pragma_table_xinfo($table) ORDER BY cid;";

        _ = columns.Parameters.AddWithValue("$table", table);

        await using (SqliteDataReader reader = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || !string.Equals(reader.GetString(0), key, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(1), "TEXT", StringComparison.OrdinalIgnoreCase)
                || reader.GetInt64(3) != 1 || reader.GetInt64(4) != 0 || !reader.IsDBNull(5)
                || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetString(0) != "Embedding"
                || !string.Equals(reader.GetString(1), "BLOB", StringComparison.OrdinalIgnoreCase)
                || reader.GetInt64(2) != 1 || reader.GetInt64(3) != 0 || reader.GetInt64(4) != 0 || !reader.IsDBNull(5)
                || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw Unsupported(table);
            }
        }

        await using SqliteCommand dependencies = connection.CreateCommand();

        dependencies.Transaction = transaction;

        // Refuse extra triggers, explicit indexes, views, and foreign-key owners rather than dropping
        // an object that could have effects outside this obsolete mirror. SQLite-owned indexes have no SQL.
        dependencies.CommandText = """
            SELECT EXISTS(SELECT 1 FROM pragma_foreign_key_list($table))
                OR EXISTS(SELECT 1 FROM sqlite_schema WHERE name<>$table COLLATE NOCASE
                    AND sql IS NOT NULL AND instr(lower(sql),lower($table))>0);
            """;

        _ = dependencies.Parameters.AddWithValue("$table", table);

        if (Convert.ToInt64(await dependencies.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            throw Unsupported(table);
        }

        return true;
    }

    private static InvalidOperationException Unsupported(string table) => new(
        $"Legacy vector mirror {table} cannot be retired safely. Preserve this database and rebuild it offline without unsupported vector mirror objects before retrying admission or restore.");
}
