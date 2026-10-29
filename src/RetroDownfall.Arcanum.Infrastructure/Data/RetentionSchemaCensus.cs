using System.Data;

using System.Data.Common;

using System.Globalization;

using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Reuses catalog classification within one retention planning or execution pass, without retaining a read snapshot.
/// </summary>
/// <remarks>
/// Every lookup reads the current connection's schema cookie first. A census and its cookie are read
/// in one statement, so external DDL cannot pair a new cookie with old definitions. Mutable rows and
/// authority are never cached. The lookup/count race is the same as the former catalog-query/count
/// pair; this does not make planning transactional or carry metadata into the writer transaction.
/// Any managed or native transaction bypasses reuse, including savepoint rollback that can repeat a cookie.
/// </remarks>
internal sealed class RetentionSchemaCensus : IDisposable
{
    private SqliteConnection? _connection;

    private object? _handle;

    private long _version;

    private Dictionary<string, bool>? _tables;

    private readonly Dictionary<string, long> _scalars = new(StringComparer.Ordinal);

    internal async Task<bool> ExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using SqliteCommand current = connection.CreateCommand();

        if (current.Transaction is not null || SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0)
        {
            // ROLLBACK TO can reuse a schema cookie even within the same transaction. Retain
            // the original catalog read rather than attempting to track arbitrary savepoints.
            _tables = null;

            _scalars.Clear();

            if (SagaVectorMirror.IsMirrorName(table))
            {
                return await SagaVectorMirror.ClassifyAsync(connection, current.Transaction, table, cancellationToken)
                    .ConfigureAwait(false) is SagaVectorMirrorKind.PlainTable;
            }

            current.CommandText = "SELECT 1 FROM sqlite_master WHERE name = @table AND type IN ('table', 'view') LIMIT 1";

            _ = current.Parameters.AddWithValue("@table", table);

            object? result = await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return result is not null && result != DBNull.Value;
        }

        long version = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (_tables is null || _version != version)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText =
                """
                SELECT version.schema_version, catalog.name, catalog.sql
                FROM pragma_schema_version AS version
                LEFT JOIN main.sqlite_master AS catalog ON catalog.type IN ('table', 'view')
                """;

            Dictionary<string, bool> tables = new(StringComparer.Ordinal);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                version = reader.GetInt64(0);

                if (reader.IsDBNull(1)) continue;

                string name = reader.GetString(1);

                bool legacy = !reader.IsDBNull(2)
                    && reader.GetString(2).Trim().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);

                tables[name] = !SagaVectorMirror.IsMirrorName(name) || !legacy;
            }

            if (_version != version) _scalars.Clear();

            _version = version;

            _tables = tables;
        }

        return _tables.TryGetValue(table, out bool exists) && exists;
    }

    /// <summary>Reads a code-owned, parameterless catalog-only scalar under the same freshness rule.</summary>
    internal async Task<long> ReadScalarAsync(DbCommand source, CancellationToken cancellationToken)
    {
        SqliteConnection connection = (SqliteConnection)source.Connection!;

        await using SqliteCommand current = connection.CreateCommand();

        if (current.Transaction is not null || source.Transaction is not null
            || SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0)
        {
            _tables = null;

            _scalars.Clear();

            return Convert.ToInt64(await source.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        long version = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (_version != version)
        {
            _tables = null;

            _scalars.Clear();

            _version = version;
        }

        string query = source.CommandText.Trim().TrimEnd(';');

        if (_scalars.TryGetValue(query, out long value)) return value;

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = (SqliteTransaction?)source.Transaction;

        command.CommandText = $"SELECT ({query}), schema_version FROM pragma_schema_version";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        value = reader.GetInt64(0);

        version = reader.GetInt64(1);

        if (_version != version)
        {
            _tables = null;

            _scalars.Clear();

            _version = version;
        }

        _scalars[query] = value;

        return value;
    }

    private async Task<long> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ReferenceEquals(_connection, connection))
        {
            Dispose();

            _connection = connection;

            connection.StateChange += OnStateChange;
        }

        if (!ReferenceEquals(_handle, connection.Handle))
        {
            _tables = null;

            _scalars.Clear();

            _handle = connection.Handle;
        }

        await using SqliteCommand versionCommand = connection.CreateCommand();

        versionCommand.CommandText = "PRAGMA main.schema_version";

        long version = Convert.ToInt64(
            await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        return version;
    }

    private void OnStateChange(object? sender, StateChangeEventArgs args)
    {
        _tables = null;

        _scalars.Clear();

        _handle = null;
    }

    public void Dispose()
    {
        if (_connection is not null) _connection.StateChange -= OnStateChange;

        _connection = null;

        _handle = null;

        _tables = null;

        _scalars.Clear();
    }
}
