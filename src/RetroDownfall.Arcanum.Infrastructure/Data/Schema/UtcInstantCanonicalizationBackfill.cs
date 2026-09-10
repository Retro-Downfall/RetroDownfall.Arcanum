using System.Globalization;

using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Rewrites one transaction-bounded page of inherited instant text into the fixed UTC form.
/// </summary>
/// <remarks>
/// A page prevalidates every selected value before its first write. Update triggers are then removed
/// only for the current table and recreated from their installed definitions in the same supplied
/// transaction, so append-only and state-machine guards never observe a maintenance-only spelling
/// repair and can never be left absent by a crash or rollback.
/// </remarks>
internal sealed class UtcInstantCanonicalizationBackfill(
    string name,
    IReadOnlyList<UtcInstantTable> tables) : IGrimoireSchemaBackfill
{
    private const string CursorVersion = "v1";

    private readonly IReadOnlyList<UtcInstantTable> _tables =
        tables ?? throw new ArgumentNullException(nameof(tables));

    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("A UTC instant backfill needs a stable name.", nameof(name))
        : name;

    public int MaxRowsPerBatch => 256;

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        BackfillPosition position = ParseCursor(cursor);

        if (position.TableIndex == _tables.Count)
        {
            return new GrimoireSchemaBackfillBatch(null, 0, IsComplete: true);
        }

        UtcInstantTable table = _tables[position.TableIndex];

        IReadOnlyList<CanonicalRow> rows = await ReadAndPrevalidateAsync(
            connection,
            transaction,
            table,
            position.AfterRowId,
            cancellationToken).ConfigureAwait(false);

        if (rows.Any(static row => row.RequiresWrite))
        {
            IReadOnlyList<InstalledTrigger> triggers = await ReadUpdateTriggersAsync(
                connection,
                transaction,
                table.TableName,
                cancellationToken).ConfigureAwait(false);

            List<InstalledTrigger> droppedTriggers = [];

            try
            {
                await DropTriggersAsync(
                    connection,
                    transaction,
                    triggers,
                    droppedTriggers).ConfigureAwait(false);

                foreach (CanonicalRow row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (row.RequiresWrite)
                    {
                        await UpdateRowAsync(
                            connection,
                            transaction,
                            table,
                            row,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                await RestoreTriggersAsync(
                    connection,
                    transaction,
                    droppedTriggers,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await RestoreTriggersAsync(
                        connection,
                        transaction,
                        droppedTriggers,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The owner rolls the supplied transaction back, which restores the trigger
                    // catalog atomically. Preserve the data/validation failure that caused it.
                }

                throw;
            }
        }

        if (rows.Count == MaxRowsPerBatch)
        {
            return new GrimoireSchemaBackfillBatch(
                SerializeCursor(new BackfillPosition(position.TableIndex, rows[^1].RowId)),
                rows.Count,
                IsComplete: false);
        }

        int nextTable = position.TableIndex + 1;

        return new GrimoireSchemaBackfillBatch(
            nextTable == _tables.Count
                ? null
                : SerializeCursor(new BackfillPosition(nextTable, AfterRowId: null)),
            rows.Count,
            IsComplete: nextTable == _tables.Count);
    }

    private async Task<IReadOnlyList<CanonicalRow>> ReadAndPrevalidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UtcInstantTable table,
        long? afterRowId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand read = connection.CreateCommand();

        read.Transaction = transaction;

        string columns = string.Join(", ", table.Columns.Select(QuoteIdentifier));

        read.CommandText = afterRowId is null
            ? $"SELECT rowid, {columns} FROM {QuoteIdentifier(table.TableName)} ORDER BY rowid LIMIT $limit;"
            : $"SELECT rowid, {columns} FROM {QuoteIdentifier(table.TableName)} WHERE rowid > $after ORDER BY rowid LIMIT $limit;";

        if (afterRowId is long lastRowId)
        {
            _ = read.Parameters.AddWithValue("$after", lastRowId);
        }

        _ = read.Parameters.AddWithValue("$limit", MaxRowsPerBatch);

        List<CanonicalRow> rows = [];

        await using SqliteDataReader reader =
            await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long rowId = reader.GetInt64(0);

            string?[] values = new string?[table.Columns.Count];

            bool requiresWrite = false;

            for (int index = 0; index < table.Columns.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.IsDBNull(index + 1))
                {
                    continue;
                }

                string stored = reader.GetString(index + 1);

                try
                {
                    string canonical = UtcInstantText.Normalize(stored);

                    values[index] = canonical;

                    requiresWrite |= !string.Equals(stored, canonical, StringComparison.Ordinal);
                }
                catch (FormatException exception)
                {
                    throw new InvalidOperationException(
                        $"The {table.TableName}.{table.Columns[index]} instant at rowid {rowId.ToString(CultureInfo.InvariantCulture)} is invalid; the UTC canonicalization batch made no changes.",
                        exception);
                }
            }

            rows.Add(new CanonicalRow(rowId, values, requiresWrite));
        }

        return rows;
    }

    private static async Task UpdateRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UtcInstantTable table,
        CanonicalRow row,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand update = connection.CreateCommand();

        update.Transaction = transaction;

        string assignments = string.Join(
            ", ",
            table.Columns.Select(static (column, index) => $"{QuoteIdentifier(column)} = $instant{index}"));

        update.CommandText =
            $"UPDATE {QuoteIdentifier(table.TableName)} SET {assignments} WHERE rowid = $rowid;";

        for (int index = 0; index < row.Values.Count; index++)
        {
            _ = update.Parameters.AddWithValue(
                $"$instant{index}",
                (object?)row.Values[index] ?? DBNull.Value);
        }

        _ = update.Parameters.AddWithValue("$rowid", row.RowId);

        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"The {table.TableName} row at rowid {row.RowId.ToString(CultureInfo.InvariantCulture)} moved while its UTC instants were being canonicalized.");
        }
    }

    private static async Task<IReadOnlyList<InstalledTrigger>> ReadUpdateTriggersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand read = connection.CreateCommand();

        read.Transaction = transaction;

        read.CommandText =
            "SELECT name, sql FROM sqlite_schema WHERE type = 'trigger' AND tbl_name = $table AND sql IS NOT NULL ORDER BY name;";

        _ = read.Parameters.AddWithValue("$table", tableName);

        List<InstalledTrigger> triggers = [];

        await using SqliteDataReader reader =
            await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string sql = reader.GetString(1);

            if (FiresOnUpdate(sql))
            {
                triggers.Add(new InstalledTrigger(reader.GetString(0), sql));
            }
        }

        return triggers;
    }

    private static bool FiresOnUpdate(string sql)
    {
        int body = sql.IndexOf("BEGIN", StringComparison.OrdinalIgnoreCase);

        string header = body < 0 ? sql : sql[..body];

        foreach (string token in header.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task DropTriggersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<InstalledTrigger> triggers,
        ICollection<InstalledTrigger> droppedTriggers)
    {
        foreach (InstalledTrigger trigger in triggers)
        {
            await using SqliteCommand drop = connection.CreateCommand();

            drop.Transaction = transaction;

            drop.CommandText = $"DROP TRIGGER {QuoteIdentifier(trigger.Name)};";

            _ = await drop.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);

            droppedTriggers.Add(trigger);
        }
    }

    private static async Task RestoreTriggersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<InstalledTrigger> triggers,
        CancellationToken cancellationToken)
    {
        foreach (InstalledTrigger trigger in triggers)
        {
            await using SqliteCommand restore = connection.CreateCommand();

            restore.Transaction = transaction;

            restore.CommandText = trigger.Sql;

            _ = await restore.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private BackfillPosition ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return new BackfillPosition(TableIndex: 0, AfterRowId: null);
        }

        string[] parts = cursor.Split(':', StringSplitOptions.None);

        if (parts.Length != 3
            || !string.Equals(parts[0], CursorVersion, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int tableIndex)
            || tableIndex < 0
            || tableIndex > _tables.Count
            || (parts[2].Length > 0
                && !long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            || (tableIndex == _tables.Count && parts[2].Length > 0))
        {
            throw InvalidCursor();
        }

        long? afterRowId = parts[2].Length == 0
            ? null
            : long.Parse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        return new BackfillPosition(tableIndex, afterRowId);
    }

    private static string SerializeCursor(BackfillPosition position) =>
        string.Join(
            ':',
            CursorVersion,
            position.TableIndex.ToString(CultureInfo.InvariantCulture),
            position.AfterRowId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static InvalidOperationException InvalidCursor() =>
        new("The UTC instant schema backfill cursor is malformed.");

    private sealed record BackfillPosition(int TableIndex, long? AfterRowId);

    private sealed record CanonicalRow(long RowId, IReadOnlyList<string?> Values, bool RequiresWrite);

    private sealed record InstalledTrigger(string Name, string Sql);
}
