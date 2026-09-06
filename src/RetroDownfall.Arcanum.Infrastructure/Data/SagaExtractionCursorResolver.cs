using System.Data.Common;
using System.Globalization;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// One bounded scan toward the conservative Entry-sequence prefix a timestamp-only Saga watermark
/// proves was consumed.
/// </summary>
internal sealed record SagaExtractionCursorPage(
    long LastProvenEntrySequence,
    int EntriesExamined,
    bool IsComplete);

/// <summary>
/// Resolves timestamp-only Saga watermarks without using SQLite's precision-losing date functions.
/// </summary>
internal static class SagaExtractionCursorResolver
{

    private const int LegacyPageSize = 200;

    /// <summary>
    /// Resolves a legacy live-store write. Each database read is bounded even though this compatibility
    /// path deliberately drains all its pages before it returns.
    /// </summary>
    internal static async Task<long> ResolveContiguousPrefixAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sessionId,
        DateTimeOffset lastExtractedEntryCreatedAt,
        CancellationToken cancellationToken)
    {

        long lastProvenEntrySequence = 0;

        while (true)
        {

            SagaExtractionCursorPage page = await ReadContiguousPrefixPageAsync(
                connection,
                transaction,
                sessionId,
                lastExtractedEntryCreatedAt,
                lastProvenEntrySequence,
                LegacyPageSize,
                cancellationToken).ConfigureAwait(false);

            lastProvenEntrySequence = page.LastProvenEntrySequence;

            if (page.IsComplete)
            {

                return lastProvenEntrySequence;

            }

        }

    }

    /// <summary>
    /// Examines at most <paramref name="limit"/> Entries after <paramref name="afterEntrySequence"/>.
    /// The first timestamp newer than the watermark ends the prefix, even when a later sequence carries
    /// an older timestamp.
    /// </summary>
    internal static async Task<SagaExtractionCursorPage> ReadContiguousPrefixPageAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sessionId,
        DateTimeOffset lastExtractedEntryCreatedAt,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        ArgumentOutOfRangeException.ThrowIfNegative(afterEntrySequence);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (!Guid.TryParse(sessionId, out Guid parsedSessionId))
        {

            // A managed watermark always names a Guid Session. If a hand-edited legacy row does not,
            // resolving it to zero is the conservative answer: replay can cost work, advancing past an
            // Entry that was never examined can lose it.
            return new SagaExtractionCursorPage(afterEntrySequence, EntriesExamined: 0, IsComplete: true);

        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        // Core version 5 verified Entries.SessionId as uppercase dashed Guid text, and version 6 ships
        // IX_Entries_SessionId_Sequence. Binding that exact spelling lets SQLite seek directly to the
        // requested continuation and stop at LIMIT instead of sorting every Entry in the Session.
        command.CommandText =
            """
            SELECT "Sequence", "CreatedAt"
            FROM "Entries"
            WHERE "SessionId" = @sessionId
              AND "Sequence" > @afterEntrySequence
            ORDER BY "Sequence"
            LIMIT @limit;
            """;

        DbParameter sessionParameter = command.CreateParameter();

        sessionParameter.ParameterName = "@sessionId";

        sessionParameter.Value = parsedSessionId.ToString("D").ToUpperInvariant();

        _ = command.Parameters.Add(sessionParameter);

        DbParameter sequenceParameter = command.CreateParameter();

        sequenceParameter.ParameterName = "@afterEntrySequence";

        sequenceParameter.Value = afterEntrySequence;

        _ = command.Parameters.Add(sequenceParameter);

        DbParameter limitParameter = command.CreateParameter();

        limitParameter.ParameterName = "@limit";

        limitParameter.Value = limit;

        _ = command.Parameters.Add(limitParameter);

        await using DbDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        long lastProvenEntrySequence = afterEntrySequence;

        int examined = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            examined++;

            long entrySequence = reader.GetInt64(0);

            DateTimeOffset entryCreatedAt = DateTimeOffset.Parse(
                reader.GetString(1),
                CultureInfo.InvariantCulture);

            if (entryCreatedAt > lastExtractedEntryCreatedAt)
            {

                return new SagaExtractionCursorPage(
                    lastProvenEntrySequence,
                    examined,
                    IsComplete: true);

            }

            lastProvenEntrySequence = entrySequence;

        }

        return new SagaExtractionCursorPage(
            lastProvenEntrySequence,
            examined,
            IsComplete: examined < limit);

    }

}
