using System.Data.Common;
using System.Text;
using System.Globalization;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>Disposable compact projections, written within the authoritative embedding transaction.</summary>
internal static class QuantizedEmbeddingStore
{
    internal static readonly (string Table, string Key)[] Families =
    [
        ("entry_embeddings", "EntryId"),
        ("workspace_file_embeddings", "ChunkId"),
        ("session_attachment_embeddings", "ChunkId"),
        ("saga_memory_embeddings", "MemoryId"),
        ("tapestry_node_embeddings", "NodeId"),
    ];

    internal static string KeyFor(string tableName) => Families.First(family => family.Table == tableName).Key;

    internal static async Task UpsertAsync(DbConnection connection, DbTransaction? transaction, string tableName,
        string id, ReadOnlyMemory<float> vector, CancellationToken cancellationToken)
    {
        _ = KeyFor(tableName);

        if (!await IsAvailableAsync(connection, transaction, tableName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await WriteAsync(connection, transaction, tableName, id,
            QuantizedEmbeddingCodec.Encode(vector.Span, Encoding.UTF8.GetBytes(id)), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> IsAvailableAsync(DbConnection connection, DbTransaction? transaction,
        string tableName, CancellationToken cancellationToken, RetentionSchemaCensus? schemaCensus = null)
    {
        await using DbCommand available = connection.CreateCommand();

        available.Transaction = transaction;

        available.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=@table";

        AddParameter(available, "@table", tableName + "_quantized");

        bool exists = schemaCensus is not null && connection is SqliteConnection sqlite
            ? await schemaCensus.ExistsAsync(sqlite, tableName + "_quantized", cancellationToken, tableOnly: true).ConfigureAwait(false)
            : Convert.ToInt64(await available.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;

        if (exists)
        {
            return true;
        }

        // Older pending Core sweeps deliberately keep ordinary Saga writes available. Those writes
        // retain their authoritative BLOB now; the compact backfill catches them after Core37 lands.
        if (await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken, transaction).ConfigureAwait(false) < 37)
        {
            return false;
        }

        throw new InvalidDataException("The admitted compact embedding schema is missing.");
    }

    private static async Task WriteAsync(DbConnection connection, DbTransaction? transaction, string tableName,
        string id, byte[]? representation, CancellationToken cancellationToken)
    {
        string key = KeyFor(tableName);

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = representation is null
            ? $"DELETE FROM \"{tableName}_quantized\" WHERE \"{key}\" = @id"
            : $"INSERT INTO \"{tableName}_quantized\" (\"{key}\", Representation) VALUES (@id, @representation) ON CONFLICT(\"{key}\") DO UPDATE SET Representation = excluded.Representation";

        AddParameter(command, "@id", id);

        if (representation is not null)
        {
            AddParameter(command, "@representation", representation);
        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pages missing projections inside a write transaction. The cursor advances over unsafe vectors
    /// too, so an inherited malformed row cannot starve later rows. A new sweep starts at null; missing
    /// rows, rather than a permanent watermark, make cancellation and restart naturally resumable.
    /// </summary>
    internal static async Task<(string? Cursor, int Examined)> BackfillBatchAsync(SqliteConnection connection,
        SqliteTransaction transaction, string tableName, string? cursor, int batchSize,
        CancellationToken cancellationToken)
    {
        string key = KeyFor(tableName);

        if (!await IsAvailableAsync(connection, transaction, tableName, cancellationToken).ConfigureAwait(false))
        {
            return (null, 0);
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"""
            WITH page AS MATERIALIZED (
                SELECT "{key}"
                FROM "{tableName}"
                {(cursor is null ? "" : $"WHERE \"{key}\" > @cursor")}
                ORDER BY "{key}"
                LIMIT @batch)
            SELECT page."{key}", CASE WHEN q."{key}" IS NULL THEN
                (SELECT source.Embedding FROM "{tableName}" source
                 WHERE source."{key}" = page."{key}"
                   AND CASE WHEN typeof(source.Embedding) = 'blob' THEN
                       length(source.Embedding) BETWEEN @minimumBytes AND @maximumBytes
                       AND length(source.Embedding) % @floatBytes = 0
                       ELSE 0 END)
                ELSE NULL END
            FROM page
            LEFT JOIN "{tableName}_quantized" q ON q."{key}" = page."{key}"
            ORDER BY page."{key}"
            """;

        command.Parameters.AddWithValue("@cursor", (object?)cursor ?? DBNull.Value);

        command.Parameters.AddWithValue("@batch", Math.Clamp(batchSize, 1, 128));

        // SQLite can reject a BLOB by stored length without loading its overflow pages. Apply these
        // guards before projecting the value, since inspecting its borrowed span is already too late.
        command.Parameters.AddWithValue("@minimumBytes", sizeof(float));

        command.Parameters.AddWithValue("@maximumBytes", QuantizedEmbeddingCodec.MaximumDimensions * sizeof(float));

        command.Parameters.AddWithValue("@floatBytes", sizeof(float));

        List<(string Id, byte[] Representation)> writes = [];

        int examined = 0;

        string? last = null;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                last = reader.GetString(0);

                examined++;

                byte[]? representation = EncodeCurrentRow(reader, last);

                if (representation is not null)
                {
                    writes.Add((last, representation));
                }
            }
        }

        foreach ((string id, byte[] representation) in writes)
        {
            await WriteAsync(connection, transaction, tableName, id, representation, cancellationToken).ConfigureAwait(false);
        }

        return (last, examined);
    }

    private static byte[]? EncodeCurrentRow(SqliteDataReader reader, string id)
    {
        SQLitePCL.sqlite3_stmt statement = reader.Handle!;

        if (SQLitePCL.raw.sqlite3_column_type(statement, 1) != SQLitePCL.raw.SQLITE_BLOB)
        {
            return null;
        }

        ReadOnlySpan<byte> blob = SQLitePCL.raw.sqlite3_column_blob(statement, 1);

        if (blob.Length == 0 || blob.Length % sizeof(float) != 0)
        {
            return null;
        }

        return QuantizedEmbeddingCodec.Encode(EmbeddingBlobCodec.AsVector(blob), Encoding.UTF8.GetBytes(id));
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);
    }
}
