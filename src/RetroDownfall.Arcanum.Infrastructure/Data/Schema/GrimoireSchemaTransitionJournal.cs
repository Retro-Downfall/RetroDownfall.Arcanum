using System.Globalization;

using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// One tier's in-flight version run.
/// </summary>
/// <remarks>
/// The row's existence is the phase. <c>(CompletedThroughVersion, BackfillName)</c> says the rest:
/// a null name means the step leaving <c>CompletedThroughVersion</c> has not run its DDL yet, and a
/// name means that DDL committed and the sweep is draining at <c>BackfillCursor</c>.
/// </remarks>
internal sealed record GrimoireSchemaTransitionJournalRow(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    int FromVersion,
    int TargetVersion,
    int CompletedThroughVersion,
    string TargetSourceDefinitionFingerprint,
    string? BackfillName,
    string? BackfillCursor,
    long BackfillRowsProcessed,
    long Revision);

/// <summary>
/// Reads and writes <c>grimoire_schema_transitions</c>, and nothing else.
/// </summary>
/// <remarks>
/// Every mutation is conditional on the revision the caller read. A host coordinator and a CLI
/// bootstrap can both hold the encrypted file, and the loser of that race has to fail its transaction
/// rather than move a cursor past work only the winner committed.
/// </remarks>
internal static class GrimoireSchemaTransitionJournal
{

    private const string Projection = """
        SELECT FamilyCode, TransactionTierCode, FromVersion, TargetVersion, CompletedThroughVersion,
               TargetSourceDefinitionFingerprint, BackfillName, BackfillCursor, BackfillRowsProcessed,
               Revision
        FROM grimoire_schema_transitions
        """;

    internal static async Task<GrimoireSchemaTransitionJournalRow?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        GrimoireSchemaTransactionTier tier,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = Projection + " WHERE TransactionTierCode = $tierCode;";

        _ = command.Parameters.AddWithValue("$tierCode", (long)tier);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Project(reader)
            : null;

    }

    /// <summary>
    /// Every in-flight run, for a driver that has to find work without being told where to look.
    /// </summary>
    internal static async Task<IReadOnlyList<GrimoireSchemaTransitionJournalRow>> ReadAllAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = Projection + " ORDER BY TransactionTierCode;";

        List<GrimoireSchemaTransitionJournalRow> rows = [];

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            rows.Add(Project(reader));

        }

        return rows;

    }

    /// <summary>
    /// Opens a run. Called inside the same transaction as the first step's DDL, so a crash before
    /// that commit leaves neither the row nor the DDL.
    /// </summary>
    internal static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaTransitionJournalRow row,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(row);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO grimoire_schema_transitions (
                FamilyCode, TransactionTierCode, FromVersion, TargetVersion, CompletedThroughVersion,
                TargetSourceDefinitionFingerprint, BackfillName, BackfillCursor, BackfillRowsProcessed,
                Revision, LastDurableErrorCode, StartedAtUtc, UpdatedAtUtc)
            VALUES ($familyCode, $tierCode, $from, $target, $through, $fingerprint, $backfillName,
                    $cursor, $rows, $revision, NULL, $now, $now);
            """;

        _ = command.Parameters.AddWithValue("$familyCode", (long)row.Family);

        _ = command.Parameters.AddWithValue("$tierCode", (long)row.TransactionTier);

        _ = command.Parameters.AddWithValue("$from", row.FromVersion);

        _ = command.Parameters.AddWithValue("$target", row.TargetVersion);

        _ = command.Parameters.AddWithValue("$through", row.CompletedThroughVersion);

        _ = command.Parameters.AddWithValue("$fingerprint", row.TargetSourceDefinitionFingerprint);

        _ = command.Parameters.AddWithValue("$backfillName", (object?)row.BackfillName ?? DBNull.Value);

        _ = command.Parameters.AddWithValue("$cursor", (object?)row.BackfillCursor ?? DBNull.Value);

        _ = command.Parameters.AddWithValue("$rows", row.BackfillRowsProcessed);

        _ = command.Parameters.AddWithValue("$revision", row.Revision);

        _ = command.Parameters.AddWithValue("$now", nowUtc.ToString("o", CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Advances a run conditionally on the revision it was read at, reporting whether it won.
    /// </summary>
    internal static async Task<bool> AdvanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaTransitionJournalRow row,
        int completedThroughVersion,
        string? backfillName,
        string? backfillCursor,
        long backfillRowsProcessed,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(row);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE grimoire_schema_transitions
            SET CompletedThroughVersion = $through,
                BackfillName = $backfillName,
                BackfillCursor = $cursor,
                BackfillRowsProcessed = $rows,
                Revision = Revision + 1,
                LastDurableErrorCode = NULL,
                UpdatedAtUtc = $now
            WHERE TransactionTierCode = $tierCode AND Revision = $revision;
            """;

        _ = command.Parameters.AddWithValue("$through", completedThroughVersion);

        _ = command.Parameters.AddWithValue("$backfillName", (object?)backfillName ?? DBNull.Value);

        _ = command.Parameters.AddWithValue("$cursor", (object?)backfillCursor ?? DBNull.Value);

        _ = command.Parameters.AddWithValue("$rows", backfillRowsProcessed);

        _ = command.Parameters.AddWithValue("$now", nowUtc.ToString("o", CultureInfo.InvariantCulture));

        _ = command.Parameters.AddWithValue("$tierCode", (long)row.TransactionTier);

        _ = command.Parameters.AddWithValue("$revision", row.Revision);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

    }

    /// <summary>
    /// Closes a run, in the same transaction that records the version it reached.
    /// </summary>
    internal static async Task<bool> DeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaTransitionJournalRow row,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(row);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            DELETE FROM grimoire_schema_transitions
            WHERE TransactionTierCode = $tierCode AND Revision = $revision;
            """;

        _ = command.Parameters.AddWithValue("$tierCode", (long)row.TransactionTier);

        _ = command.Parameters.AddWithValue("$revision", row.Revision);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

    }

    /// <summary>
    /// Records why the last pass over a run failed, without touching its progress.
    /// </summary>
    /// <remarks>
    /// The code is a closed constant supplied by the caller and bounded here as well. An exception
    /// message would be both an unbounded row and a place for content to leak into a core journal.
    /// </remarks>
    internal static async Task RecordErrorAsync(
        SqliteConnection connection,
        GrimoireSchemaTransactionTier tier,
        string errorCode,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            UPDATE grimoire_schema_transitions
            SET LastDurableErrorCode = $code, UpdatedAtUtc = $now
            WHERE TransactionTierCode = $tierCode;
            """;

        _ = command.Parameters.AddWithValue(
            "$code",
            errorCode.Length <= 64 ? errorCode : errorCode[..64]);

        _ = command.Parameters.AddWithValue("$now", nowUtc.ToString("o", CultureInfo.InvariantCulture));

        _ = command.Parameters.AddWithValue("$tierCode", (long)tier);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static GrimoireSchemaTransitionJournalRow Project(SqliteDataReader reader) =>
        new(
            (GrimoireSchemaFamily)reader.GetInt64(0),
            (GrimoireSchemaTransactionTier)reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt64(8),
            reader.GetInt64(9));

}
