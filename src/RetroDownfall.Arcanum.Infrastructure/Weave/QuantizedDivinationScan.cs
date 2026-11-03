using System.Data.Common;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal readonly record struct QuantizedDivinationSource(string Table, string Key, string Embedding, string Alias);

/// <summary>One compact cursor and one reusable exact lookup, sharing the cursor's SQLite snapshot.</summary>
internal sealed class QuantizedDivinationScan : IDisposable
{
    // At 768 dimensions this is at least 3 MiB of authoritative payload. Below this bound keep the
    // original scoring statement unchanged; the capped count uses the existing primary-key index.
    internal const int EngagementRows = 1024;

    private readonly sqlite3 _connection;

    private readonly sqlite3_stmt _lookup;

    private readonly QuantizedEmbeddingCodec.PreparedQuery _query;

    private QuantizedDivinationScan(sqlite3 connection, sqlite3_stmt lookup, QuantizedEmbeddingCodec.PreparedQuery query)
    {
        _connection = connection;

        _lookup = lookup;

        _query = query;
    }

    internal long CompactBytes { get; private set; }

    internal long AuthoritativeBytes { get; private set; }

    internal int RowsRescored { get; private set; }

    internal static async Task<QuantizedDivinationScan?> TryCreateAsync(
        DbCommand command,
        QuantizedDivinationSource source,
        float[] query,
        CancellationToken cancellationToken,
        string? compactCommandText = null)
    {
        // Identifiers are the existing code-owned corpus contracts. Arbitrary internal callers keep
        // the exhaustive path, including older fixtures and tables that deliberately end in _vec.
        if (!Supported(source) || command.Connection is not SqliteConnection connection)
        {
            return null;
        }

        await using SqliteCommand probe = connection.CreateCommand();

        probe.Transaction = command.Transaction as SqliteTransaction;

        probe.CommandText = $"SELECT COUNT(*) FROM (SELECT 1 FROM \"{source.Table}\" LIMIT {EngagementRows})";

        if (Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) < EngagementRows)
        {
            return null;
        }

        QuantizedEmbeddingCodec.PreparedQuery prepared = QuantizedEmbeddingCodec.PrepareQuery(query);

        if (!prepared.IsSafe)
        {
            return null;
        }

        probe.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=@name)";

        probe.Parameters.AddWithValue("@name", source.Table + "_quantized");

        if (Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
        {
            return null;
        }

        string alias = source.Alias.Length == 0 ? "e" : source.Alias;

        string sql = command.CommandText;

        if (source.Alias.Length == 0)
        {
            sql = sql.Replace($"SELECT \"{source.Key}\", \"{source.Embedding}\"",
                $"SELECT {alias}.\"{source.Key}\", {alias}.\"{source.Embedding}\"", StringComparison.Ordinal)
                .Replace($"FROM \"{source.Table}\"", $"FROM \"{source.Table}\" {alias}", StringComparison.Ordinal);
        }

        // Project only the compact BLOB: the parent primary-key index covers its id, so the scan
        // does not decrypt the authoritative payload pages for candidates excluded by the bound.
        sql = sql.Replace($"{alias}.\"{source.Embedding}\"", "quantized.\"Representation\"", StringComparison.Ordinal)
            .Replace($"FROM \"{source.Table}\" {alias}",
                $"FROM \"{source.Table}\" {alias}\nLEFT JOIN \"{source.Table}_quantized\" quantized ON quantized.\"{source.Key}\" = {alias}.\"{source.Key}\"", StringComparison.Ordinal);

        // The Saga query historically uses an unquoted FROM identifier.
        sql = sql.Replace($"FROM {source.Table} {alias}",
            $"FROM {source.Table} {alias}\nLEFT JOIN \"{source.Table}_quantized\" quantized ON quantized.\"{source.Key}\" = {alias}.\"{source.Key}\"", StringComparison.Ordinal);

        int result = raw.sqlite3_prepare_v2(connection.Handle,
            $"SELECT \"{source.Embedding}\" FROM \"{source.Table}\" WHERE \"{source.Key}\"=?1 /* quantized exact rescore */", out sqlite3_stmt lookup);

        SqliteException.ThrowExceptionForRC(result, connection.Handle);

        // SQLite can inspect a BLOB's type and byte length without loading its overflow pages.
        // Keep the type test outside the length test: length(TEXT) would read the entire value.
        // Malformed companions become NULL and take the existing authoritative exact fallback.
        string compactProjection = $"CASE WHEN typeof(quantized.\"Representation\")='blob' THEN CASE WHEN length(quantized.\"Representation\")={query.Length + QuantizedEmbeddingCodec.RepresentationOverheadBytes} THEN quantized.\"Representation\" END END";

        command.CommandText = (compactCommandText ?? sql)
            .Replace("quantized.Representation", "quantized.\"Representation\"", StringComparison.Ordinal)
            .Replace("quantized.\"Representation\"", compactProjection, StringComparison.Ordinal);

        return new(connection.Handle!, lookup, prepared);
    }

    internal bool TryUpperBound(SqliteDataReader reader, int embeddingOrdinal, out float upper)
    {
        upper = 0f;

        if (raw.sqlite3_column_type(reader.Handle, embeddingOrdinal) != raw.SQLITE_BLOB)
        {
            return false;
        }

        ReadOnlySpan<byte> representation = raw.sqlite3_column_blob(reader.Handle, embeddingOrdinal);

        CompactBytes += representation.Length;

        return QuantizedEmbeddingCodec.TryGetUpperBound(representation, _query, out upper,
            raw.sqlite3_column_blob(reader.Handle, 0));
    }

    internal DivinationService.RowScore Score(SqliteDataReader reader, float[] query, double norm, out float similarity)
    {
        similarity = 0f;

        // Bind directly from the source cursor's native UTF-8 bytes. The binding copies natively;
        // neither a BLOB array, an id encoding, nor a new reader/command is allocated for each row.
        SqliteException.ThrowExceptionForRC(raw.sqlite3_bind_text(_lookup, 1,
            raw.sqlite3_column_text(reader.Handle, 0)), _connection);

        try
        {
            int result = raw.sqlite3_step(_lookup);

            if (result == raw.SQLITE_DONE)
            {
                return DivinationService.RowScore.Unreadable;
            }

            SqliteException.ThrowExceptionForRC(result, _connection);

            RowsRescored++;

            AuthoritativeBytes += raw.sqlite3_column_bytes(_lookup, 0);

            return DivinationService.TryScoreStatement(_lookup, 0, query, norm, out similarity);
        }
        finally
        {
            _ = raw.sqlite3_reset(_lookup);
        }
    }

    public void Dispose() => _lookup.Dispose();

    private static bool Supported(QuantizedDivinationSource source) => source.Embedding == "Embedding" && (source.Table, source.Key) is
        ("entry_embeddings", "EntryId") or
        ("workspace_file_embeddings", "ChunkId") or
        ("session_attachment_embeddings", "ChunkId") or
        ("saga_memory_embeddings", "MemoryId") or
        ("tapestry_node_embeddings", "NodeId");
}
