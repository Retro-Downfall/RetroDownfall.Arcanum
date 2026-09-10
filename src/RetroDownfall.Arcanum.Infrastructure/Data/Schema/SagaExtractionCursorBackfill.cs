using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Resolves each timestamp-only Saga extraction watermark to the conservative Entry-sequence prefix
/// that timestamp proves was consumed.
/// </summary>
/// <remarks>
/// There is no opaque cursor in Saga's own table. The transition gives every inherited row a null
/// sequence, and only the batch that finishes examining one Session replaces that transition-only
/// value. Long Sessions page through Entries with a journal cursor containing the raw watermark
/// identity and last proven sequence; the runner commits that cursor in the same transaction as the
/// page, so a crash repeats at most one bounded page and never advances past work that did not commit.
///
/// <para>The legacy watermark meant entries at or before its timestamp were consumed, but CreatedAt is
/// not guaranteed to increase with Sequence. The first later timestamp therefore ends the proven
/// prefix. A qualifying row behind that gap is conservatively replayed instead of being skipped.</para>
/// </remarks>
internal sealed class SagaExtractionCursorBackfill : IGrimoireSchemaBackfill
{
    private const string CursorVersion = "v1";

    private const int WatermarkWriteCost = 1;

    public string Name => "saga-extraction-entry-sequence";

    public int MaxRowsPerBatch => 200;

    private int EntryPageSize => MaxRowsPerBatch - WatermarkWriteCost;

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        BackfillPosition? position = ParseCursor(cursor);

        PendingWatermark? pending = await ReadPendingWatermarkAsync(
            connection,
            transaction,
            position,
            cancellationToken).ConfigureAwait(false);

        if (pending is null)
        {
            if (position is not null)
            {
                throw new InvalidOperationException(
                    "The Saga extraction cursor names a watermark that is no longer pending.");
            }

            return new GrimoireSchemaBackfillBatch(NextCursor: null, RowsProcessed: 0, IsComplete: true);
        }

        long afterEntrySequence = position?.LastProvenEntrySequence ?? 0;

        SagaExtractionCursorPage page = await SagaExtractionCursorResolver.ReadContiguousPrefixPageAsync(
            connection,
            transaction,
            pending.SessionId,
            pending.EntryCreatedAt,
            afterEntrySequence,
            EntryPageSize,
            cancellationToken).ConfigureAwait(false);

        if (!page.IsComplete)
        {
            return new GrimoireSchemaBackfillBatch(
                SerializeCursor(new BackfillPosition(pending.SessionId, page.LastProvenEntrySequence)),
                page.EntriesExamined,
                IsComplete: false);
        }

        await SetResolvedSequenceAsync(
            connection,
            transaction,
            pending.SessionId,
            page.LastProvenEntrySequence,
            cancellationToken).ConfigureAwait(false);

        return new GrimoireSchemaBackfillBatch(
            NextCursor: null,
            page.EntriesExamined + WatermarkWriteCost,
            IsComplete: false);
    }

    private static async Task<PendingWatermark?> ReadPendingWatermarkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackfillPosition? position,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand read = connection.CreateCommand();

        read.Transaction = transaction;

        read.CommandText = position is null
            ?
            """
            SELECT SessionId, LastExtractedEntryCreatedAt
            FROM saga_extraction_watermarks
            WHERE LastExtractedEntrySequence IS NULL
            ORDER BY SessionId
            LIMIT 1;
            """
            :
            """
            SELECT SessionId, LastExtractedEntryCreatedAt
            FROM saga_extraction_watermarks
            WHERE SessionId = $sessionId
              AND LastExtractedEntrySequence IS NULL
            LIMIT 1;
            """;

        if (position is not null)
        {
            _ = read.Parameters.AddWithValue("$sessionId", position.SessionId);
        }

        await using SqliteDataReader reader =
            await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PendingWatermark(
            reader.GetString(0),
            UtcInstantText.Parse(reader.GetString(1)));
    }

    private static async Task SetResolvedSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        long entrySequence,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand update = connection.CreateCommand();

        update.Transaction = transaction;

        update.CommandText =
            """
            UPDATE saga_extraction_watermarks
            SET LastExtractedEntrySequence = $entrySequence
            WHERE SessionId = $sessionId
              AND LastExtractedEntrySequence IS NULL;
            """;

        _ = update.Parameters.AddWithValue("$entrySequence", entrySequence);

        _ = update.Parameters.AddWithValue("$sessionId", sessionId);

        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "The Saga extraction watermark moved while its sequence cursor was being backfilled.");
        }
    }

    private static string SerializeCursor(BackfillPosition position)
    {
        string encodedSessionId = Convert.ToBase64String(Encoding.UTF8.GetBytes(position.SessionId));

        return string.Join(
            ':',
            CursorVersion,
            position.LastProvenEntrySequence.ToString(CultureInfo.InvariantCulture),
            encodedSessionId);
    }

    private static BackfillPosition? ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        string[] parts = cursor.Split(':', 3, StringSplitOptions.None);

        if (parts.Length != 3
            || !string.Equals(parts[0], CursorVersion, StringComparison.Ordinal)
            || !long.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long lastProvenEntrySequence)
            || lastProvenEntrySequence < 0)
        {
            throw InvalidCursor();
        }

        try
        {
            string sessionId = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw InvalidCursor();
            }

            return new BackfillPosition(sessionId, lastProvenEntrySequence);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "The Saga extraction schema backfill cursor is malformed.",
                exception);
        }
    }

    private static InvalidOperationException InvalidCursor() =>
        new("The Saga extraction schema backfill cursor is malformed.");

    private sealed record BackfillPosition(string SessionId, long LastProvenEntrySequence);

    private sealed record PendingWatermark(string SessionId, DateTimeOffset EntryCreatedAt);
}
