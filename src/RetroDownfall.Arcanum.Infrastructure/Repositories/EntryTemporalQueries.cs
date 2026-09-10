using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// Parameterized SQLite commands for <c>Entries</c> temporal reads.
/// </summary>
/// <remarks>
/// Ordering and paging are authoritative on <see cref="Entry.Sequence"/>, never on
/// <c>(CreatedAt, Id)</c>. Every identity is bound in the canonical uppercase dashed form stored by
/// the schema, so SQLite can seek the <c>SessionId</c>-led indexes without runtime query generation.
/// </remarks>
internal static class EntryTemporalQueries
{
    public static async Task<List<Entry>> LoadRecentDescendingAsync(
        ArcanumDbContext db,
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await CreateLoadRecentDescendingCommandAsync(
            db,
            sessionId,
            limit,
            cancellationToken).ConfigureAwait(false);

        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<List<Entry>> LoadAfterSequenceAsync(
        ArcanumDbContext db,
        Guid sessionId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken,
        long maximumSequence = long.MaxValue)
    {
        await using SqliteCommand command = maximumSequence == long.MaxValue
            ? await CreateLoadAfterSequenceCommandAsync(
                db,
                sessionId,
                afterSequence,
                limit,
                cancellationToken).ConfigureAwait(false)
            : await CreateLoadAfterSequenceThroughCommandAsync(
                db,
                sessionId,
                afterSequence,
                maximumSequence,
                limit,
                cancellationToken).ConfigureAwait(false);

        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<List<Entry>> LoadBeforeSequenceAsync(
        ArcanumDbContext db,
        Guid sessionId,
        long beforeSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await CreateLoadBeforeSequenceCommandAsync(
            db,
            sessionId,
            beforeSequence,
            limit,
            cancellationToken).ConfigureAwait(false);

        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<List<Entry>> LoadBeforeDeletedKeysetAsync(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset beforeCreatedAt,
        Guid beforeId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await CreateLoadBeforeDeletedKeysetCommandAsync(
            db,
            sessionId,
            beforeCreatedAt,
            beforeId,
            limit,
            cancellationToken).ConfigureAwait(false);

        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<long?> SequenceOfAsync(
        ArcanumDbContext db,
        Guid sessionId,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await CreateSequenceOfCommandAsync(
            db,
            sessionId,
            entryId,
            cancellationToken).ConfigureAwait(false);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<List<Entry>> LoadDescendingPagedAsync(
        ArcanumDbContext db,
        Guid sessionId,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await CreateLoadDescendingPagedCommandAsync(
            db,
            sessionId,
            limit,
            offset,
            cancellationToken).ConfigureAwait(false);

        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> CountAfterAsync(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterExclusive,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await CreateCountAfterCommandAsync(
            db,
            sessionId,
            afterExclusive,
            cancellationToken).ConfigureAwait(false);

        return await ReadInt32Async(command, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<List<Entry>> LoadAfterWatermarkThroughTimestampGroupAsync(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterExclusive,
        int targetLimit,
        int maxRows,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await CreateLoadAfterWatermarkThroughTimestampGroupCommandAsync(
            db,
            sessionId,
            afterExclusive,
            targetLimit,
            maxRows,
            cancellationToken).ConfigureAwait(false);

        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> CountAfterWatermarkThroughTimestampGroupAsync(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterExclusive,
        int targetLimit,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await CreateCountAfterWatermarkThroughTimestampGroupCommandAsync(
            db,
            sessionId,
            afterExclusive,
            targetLimit,
            cancellationToken).ConfigureAwait(false);

        return await ReadInt32Async(command, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<List<Entry>> LoadSagaExtractionPageAsync(
        ArcanumDbContext db,
        Guid sessionId,
        long afterSequence,
        long throughSequence,
        int targetLimit,
        int maxRows,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await CreateLoadSagaExtractionPageCommandAsync(
            db,
            sessionId,
            afterSequence,
            throughSequence,
            targetLimit,
            maxRows,
            cancellationToken).ConfigureAwait(false);

        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> CountSagaExtractionPageAsync(
        ArcanumDbContext db,
        Guid sessionId,
        long afterSequence,
        long throughSequence,
        int targetLimit,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await CreateCountSagaExtractionPageCommandAsync(
            db,
            sessionId,
            afterSequence,
            throughSequence,
            targetLimit,
            cancellationToken).ConfigureAwait(false);

        return await ReadInt32Async(command, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SqliteCommand> CreateLoadRecentDescendingCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            $"SELECT {GrimoireEntitySql.EntryColumns} FROM \"Entries\" "
            + "WHERE \"SessionId\" = $sessionId ORDER BY \"Sequence\" DESC LIMIT $limit;",
            cancellationToken).ConfigureAwait(false);
        BindSession(command, sessionId);
        GrimoireEntitySql.AddParameter(command, "$limit", limit);

        return command;
    }

    internal static async Task<SqliteCommand> CreateLoadAfterSequenceCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            $"SELECT {GrimoireEntitySql.EntryColumns} FROM \"Entries\" "
            + "WHERE \"SessionId\" = $sessionId AND \"Sequence\" > $afterSequence "
            + "ORDER BY \"Sequence\" LIMIT $limit;",
            cancellationToken).ConfigureAwait(false);
        BindSession(command, sessionId);
        GrimoireEntitySql.AddParameter(command, "$afterSequence", afterSequence);
        GrimoireEntitySql.AddParameter(command, "$limit", limit);

        return command;
    }

    private static async Task<SqliteCommand> CreateLoadAfterSequenceThroughCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        long afterSequence,
        long throughSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            $"SELECT {GrimoireEntitySql.EntryColumns} FROM \"Entries\" "
            + "WHERE \"SessionId\" = $sessionId AND \"Sequence\" > $afterSequence "
            + "AND \"Sequence\" <= $throughSequence ORDER BY \"Sequence\" LIMIT $limit;",
            cancellationToken).ConfigureAwait(false);
        BindSession(command, sessionId);
        GrimoireEntitySql.AddParameter(command, "$afterSequence", afterSequence);
        GrimoireEntitySql.AddParameter(command, "$throughSequence", throughSequence);
        GrimoireEntitySql.AddParameter(command, "$limit", limit);

        return command;
    }

    internal static async Task<SqliteCommand> CreateLoadBeforeSequenceCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        long beforeSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            $"SELECT {GrimoireEntitySql.EntryColumns} FROM \"Entries\" "
            + "WHERE \"SessionId\" = $sessionId AND \"Sequence\" < $beforeSequence "
            + "ORDER BY \"Sequence\" DESC LIMIT $limit;",
            cancellationToken).ConfigureAwait(false);
        BindSession(command, sessionId);
        GrimoireEntitySql.AddParameter(command, "$beforeSequence", beforeSequence);
        GrimoireEntitySql.AddParameter(command, "$limit", limit);

        return command;
    }

    internal static async Task<SqliteCommand> CreateLoadBeforeDeletedKeysetCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset beforeCreatedAt,
        Guid beforeId,
        int limit,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            $"SELECT {GrimoireEntitySql.EntryColumns} FROM \"Entries\" "
            + "WHERE \"SessionId\" = $sessionId "
            + "AND (\"CreatedAt\" < $beforeCreatedAt "
            + "OR (\"CreatedAt\" = $beforeCreatedAt AND \"Id\" < $beforeId)) "
            + "ORDER BY \"Sequence\" DESC LIMIT $limit;",
            cancellationToken).ConfigureAwait(false);
        BindSession(command, sessionId);
        GrimoireEntitySql.AddParameter(
            command,
            "$beforeCreatedAt",
            GrimoireEntitySql.Format(beforeCreatedAt.ToUniversalTime()));
        GrimoireEntitySql.AddParameter(command, "$beforeId", Format(beforeId));
        GrimoireEntitySql.AddParameter(command, "$limit", limit);

        return command;
    }

    internal static async Task<SqliteCommand> CreateSequenceOfCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            "SELECT \"Sequence\" FROM \"Entries\" "
            + "WHERE \"SessionId\" = $sessionId AND \"Id\" = $entryId LIMIT 1;",
            cancellationToken).ConfigureAwait(false);
        BindSession(command, sessionId);
        GrimoireEntitySql.AddParameter(command, "$entryId", Format(entryId));

        return command;
    }

    internal static async Task<SqliteCommand> CreateLoadDescendingPagedCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            $"SELECT {GrimoireEntitySql.EntryColumns} FROM \"Entries\" "
            + "WHERE \"SessionId\" = $sessionId ORDER BY \"Sequence\" DESC "
            + "LIMIT $limit OFFSET $offset;",
            cancellationToken).ConfigureAwait(false);
        BindSession(command, sessionId);
        GrimoireEntitySql.AddParameter(command, "$limit", limit);
        GrimoireEntitySql.AddParameter(command, "$offset", offset);

        return command;
    }

    internal static async Task<SqliteCommand> CreateCountAfterCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterExclusive,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            "SELECT COUNT(*) FROM \"Entries\" "
            + "WHERE \"SessionId\" = $sessionId AND \"CreatedAt\" > $afterExclusive;",
            cancellationToken).ConfigureAwait(false);
        BindSession(command, sessionId);
        GrimoireEntitySql.AddParameter(
            command,
            "$afterExclusive",
            GrimoireEntitySql.Format(afterExclusive.ToUniversalTime()));

        return command;
    }

    internal static async Task<SqliteCommand> CreateLoadAfterWatermarkThroughTimestampGroupCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterExclusive,
        int targetLimit,
        int maxRows,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            $"""
            WITH "Boundary" AS
            (
                SELECT "CreatedAt"
                FROM "Entries"
                WHERE "SessionId" = $sessionId
                  AND "CreatedAt" > $afterExclusive
                ORDER BY "CreatedAt", "Id"
                LIMIT 1 OFFSET $boundaryOffset
            ),
            "Selected" AS
            (
                SELECT e.*
                FROM "Entries" AS e
                WHERE e."SessionId" = $sessionId
                  AND e."CreatedAt" > $afterExclusive
                  AND
                  (
                      NOT EXISTS (SELECT 1 FROM "Boundary")
                      OR e."CreatedAt" <= (SELECT "CreatedAt" FROM "Boundary")
                  )
            )
            SELECT {GrimoireEntitySql.EntryColumns}
            FROM "Selected"
            WHERE (SELECT COUNT(*) FROM "Selected") <= $maxRows
            ORDER BY "Sequence"
            LIMIT $maxRows;
            """,
            cancellationToken).ConfigureAwait(false);
        BindWatermarkParameters(command, sessionId, afterExclusive, targetLimit);
        GrimoireEntitySql.AddParameter(command, "$maxRows", maxRows);

        return command;
    }

    internal static async Task<SqliteCommand> CreateCountAfterWatermarkThroughTimestampGroupCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterExclusive,
        int targetLimit,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            """
            WITH "Boundary" AS
            (
                SELECT "CreatedAt"
                FROM "Entries"
                WHERE "SessionId" = $sessionId
                  AND "CreatedAt" > $afterExclusive
                ORDER BY "CreatedAt", "Id"
                LIMIT 1 OFFSET $boundaryOffset
            )
            SELECT COUNT(*)
            FROM "Entries" AS e
            WHERE e."SessionId" = $sessionId
              AND e."CreatedAt" > $afterExclusive
              AND
              (
                  NOT EXISTS (SELECT 1 FROM "Boundary")
                  OR e."CreatedAt" <= (SELECT "CreatedAt" FROM "Boundary")
              );
            """,
            cancellationToken).ConfigureAwait(false);
        BindWatermarkParameters(command, sessionId, afterExclusive, targetLimit);

        return command;
    }

    internal static async Task<SqliteCommand> CreateLoadSagaExtractionPageCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        long afterSequence,
        long throughSequence,
        int targetLimit,
        int maxRows,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            $"""
            WITH "Boundary" AS
            (
                SELECT "CreatedAt"
                FROM "Entries"
                WHERE "SessionId" = $sessionId
                  AND "Sequence" > $afterSequence
                  AND "Sequence" <= $throughSequence
                ORDER BY "Sequence"
                LIMIT 1 OFFSET $boundaryOffset
            ),
            "BoundaryEnd" AS
            (
                SELECT MAX("Sequence") AS "Value"
                FROM "Entries"
                WHERE "SessionId" = $sessionId
                  AND "Sequence" > $afterSequence
                  AND "Sequence" <= $throughSequence
                  AND "CreatedAt" = (SELECT "CreatedAt" FROM "Boundary")
            ),
            "Selected" AS
            (
                SELECT e.*
                FROM "Entries" AS e
                WHERE e."SessionId" = $sessionId
                  AND e."Sequence" > $afterSequence
                  AND e."Sequence" <= $throughSequence
                  AND
                  (
                      NOT EXISTS (SELECT 1 FROM "Boundary")
                      OR e."Sequence" <= (SELECT "Value" FROM "BoundaryEnd")
                  )
            )
            SELECT {GrimoireEntitySql.EntryColumns}
            FROM "Selected"
            WHERE (SELECT COUNT(*) FROM "Selected") <= $maxRows
            ORDER BY "Sequence"
            LIMIT $maxRows;
            """,
            cancellationToken).ConfigureAwait(false);
        BindSequencePageParameters(
            command,
            sessionId,
            afterSequence,
            throughSequence,
            targetLimit);
        GrimoireEntitySql.AddParameter(command, "$maxRows", maxRows);

        return command;
    }

    internal static async Task<SqliteCommand> CreateCountSagaExtractionPageCommandAsync(
        ArcanumDbContext db,
        Guid sessionId,
        long afterSequence,
        long throughSequence,
        int targetLimit,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            """
            WITH "Boundary" AS
            (
                SELECT "CreatedAt"
                FROM "Entries"
                WHERE "SessionId" = $sessionId
                  AND "Sequence" > $afterSequence
                  AND "Sequence" <= $throughSequence
                ORDER BY "Sequence"
                LIMIT 1 OFFSET $boundaryOffset
            ),
            "BoundaryEnd" AS
            (
                SELECT MAX("Sequence") AS "Value"
                FROM "Entries"
                WHERE "SessionId" = $sessionId
                  AND "Sequence" > $afterSequence
                  AND "Sequence" <= $throughSequence
                  AND "CreatedAt" = (SELECT "CreatedAt" FROM "Boundary")
            )
            SELECT COUNT(*)
            FROM "Entries" AS e
            WHERE e."SessionId" = $sessionId
              AND e."Sequence" > $afterSequence
              AND e."Sequence" <= $throughSequence
              AND
              (
                  NOT EXISTS (SELECT 1 FROM "Boundary")
                  OR e."Sequence" <= (SELECT "Value" FROM "BoundaryEnd")
              );
            """,
            cancellationToken).ConfigureAwait(false);
        BindSequencePageParameters(
            command,
            sessionId,
            afterSequence,
            throughSequence,
            targetLimit);

        return command;
    }

    private static void BindSession(SqliteCommand command, Guid sessionId) =>
        GrimoireEntitySql.AddParameter(command, "$sessionId", Format(sessionId));

    private static void BindWatermarkParameters(
        SqliteCommand command,
        Guid sessionId,
        DateTimeOffset afterExclusive,
        int targetLimit)
    {
        BindSession(command, sessionId);
        GrimoireEntitySql.AddParameter(
            command,
            "$afterExclusive",
            GrimoireEntitySql.Format(afterExclusive.ToUniversalTime()));
        GrimoireEntitySql.AddParameter(command, "$boundaryOffset", Math.Max(0, targetLimit - 1));
    }

    private static void BindSequencePageParameters(
        SqliteCommand command,
        Guid sessionId,
        long afterSequence,
        long throughSequence,
        int targetLimit)
    {
        BindSession(command, sessionId);
        GrimoireEntitySql.AddParameter(command, "$afterSequence", afterSequence);
        GrimoireEntitySql.AddParameter(command, "$throughSequence", throughSequence);
        GrimoireEntitySql.AddParameter(command, "$boundaryOffset", Math.Max(0, targetLimit - 1));
    }

    private static async Task<List<Entry>> ReadEntriesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        List<Entry> entries = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(GrimoireEntitySql.ReadEntry(reader));
        }

        return entries;
    }

    private static async Task<int> ReadInt32Async(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();
}
