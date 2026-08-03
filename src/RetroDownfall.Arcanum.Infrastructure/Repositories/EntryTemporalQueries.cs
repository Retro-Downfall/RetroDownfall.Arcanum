using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// Parameterized raw SQL for <c>Entries</c> temporal reads. EF Core's SQLite provider
/// cannot translate <see cref="DateTimeOffset"/> in ORDER BY or comparisons, so watermark
/// filters run against the sortable UTC <c>CreatedAt</c> text column.
/// </summary>
/// <remarks>
/// Ordering and paging are authoritative on <see cref="Entry.Sequence"/>, never on
/// <c>(CreatedAt, Id)</c>. A turn writes its prompt and answer — and a tool call with its result —
/// under one identical <c>CreatedAt</c>, and <c>Id</c> is a random Guid, so a
/// <c>(CreatedAt, Id)</c> order inverts those pairs about half the time. Watermark reads still
/// filter on <c>CreatedAt</c> because <c>Session.LastSummarizedMessageAt</c> is a timestamp, but
/// they order by <c>Sequence</c>.
/// </remarks>
internal static class EntryTemporalQueries
{

    /// <summary>
    /// Most-recent window: <c>ORDER BY "Sequence" DESC LIMIT</c>. Call sites reverse
    /// the result to obtain ascending chronological order.
    /// </summary>
    public static IQueryable<Entry> LoadRecentDescending(
        ArcanumDbContext db,
        Guid sessionId,
        int limit) =>
        db.Entries.FromSql(
            $"""SELECT * FROM "Entries" WHERE "SessionId" = {sessionId} ORDER BY "Sequence" DESC LIMIT {limit}""");

    /// <summary>
    /// Ascending page after an exclusive <see cref="Entry.Sequence"/> cursor. Pass
    /// <c>0</c> to start from the beginning of the session.
    /// </summary>
    public static IQueryable<Entry> LoadAfterSequence(
        ArcanumDbContext db,
        Guid sessionId,
        long afterSequence,
        int limit) =>
        db.Entries.FromSql(
            $"""
            SELECT * FROM "Entries"
            WHERE "SessionId" = {sessionId} AND "Sequence" > {afterSequence}
            ORDER BY "Sequence"
            LIMIT {limit}
            """);

    /// <summary>
    /// Descending page before an exclusive <see cref="Entry.Sequence"/> cursor.
    /// </summary>
    public static IQueryable<Entry> LoadBeforeSequence(
        ArcanumDbContext db,
        Guid sessionId,
        long beforeSequence,
        int limit) =>
        db.Entries.FromSql(
            $"""
            SELECT * FROM "Entries"
            WHERE "SessionId" = {sessionId} AND "Sequence" < {beforeSequence}
            ORDER BY "Sequence" DESC
            LIMIT {limit}
            """);

    /// <summary>
    /// Descending page before a <c>(CreatedAt, Id)</c> cursor whose entry no longer exists, so its
    /// sequence cannot be resolved. Keeps "load older" working across a deleted cursor entry.
    /// </summary>
    public static IQueryable<Entry> LoadBeforeDeletedKeyset(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset beforeCreatedAt,
        Guid beforeId,
        int limit)
    {

        DateTimeOffset beforeUtc = beforeCreatedAt.ToUniversalTime();

        return db.Entries.FromSql(
            $"""
            SELECT * FROM "Entries"
            WHERE "SessionId" = {sessionId}
              AND ("CreatedAt" < {beforeUtc} OR ("CreatedAt" = {beforeUtc} AND "Id" < {beforeId}))
            ORDER BY "Sequence" DESC
            LIMIT {limit}
            """);

    }

    /// <summary>
    /// Resolves an entry's <see cref="Entry.Sequence"/>, or <c>null</c> when the entry is not in
    /// the session.
    /// </summary>
    public static IQueryable<long?> SequenceOf(
        ArcanumDbContext db,
        Guid sessionId,
        Guid entryId) =>
        db.Database.SqlQuery<long?>(
            $"""
            SELECT "Sequence" AS "Value" FROM "Entries"
            WHERE "SessionId" = {sessionId} AND "Id" = {entryId}
            LIMIT 1
            """);

    /// <summary>
    /// Descending offset page without a cursor: <c>ORDER BY "Sequence" DESC</c>.
    /// </summary>
    public static IQueryable<Entry> LoadDescendingPaged(
        ArcanumDbContext db,
        Guid sessionId,
        int limit,
        int offset) =>
        db.Entries.FromSql(
            $"""
            SELECT * FROM "Entries"
            WHERE "SessionId" = {sessionId}
            ORDER BY "Sequence" DESC
            LIMIT {limit} OFFSET {offset}
            """);

    /// <summary>
    /// Count entries strictly after an exclusive watermark (<c>CreatedAt &gt;</c>).
    /// </summary>
    public static IQueryable<int> CountAfter(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterExclusive) =>
        db.Database.SqlQuery<int>(
            $"""SELECT COUNT(*) AS "Value" FROM "Entries" WHERE "SessionId" = {sessionId} AND "CreatedAt" > {afterExclusive}""");

    /// <summary>
    /// Loads an ascending watermark window through the timestamp group containing the
    /// <paramref name="targetLimit"/>th row. The boundary CTE keeps rows sharing a
    /// <c>CreatedAt</c> value together so advancing a timestamp-only watermark cannot skip the
    /// other half of a tool call/result pair.
    /// </summary>
    public static IQueryable<Entry> LoadAfterWatermarkThroughTimestampGroup(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterExclusive,
        int targetLimit,
        int maxRows)
    {

        DateTimeOffset afterUtc = afterExclusive.ToUniversalTime();

        int boundaryOffset = Math.Max(0, targetLimit - 1);

        return db.Entries.FromSql(
            $"""
            WITH "Boundary" AS
            (
                SELECT "CreatedAt"
                FROM "Entries"
                WHERE "SessionId" = {sessionId}
                  AND "CreatedAt" > {afterUtc}
                ORDER BY "CreatedAt", "Id"
                LIMIT 1 OFFSET {boundaryOffset}
            ),
            "Selected" AS
            (
                SELECT e.*
                FROM "Entries" AS e
                WHERE e."SessionId" = {sessionId}
                  AND e."CreatedAt" > {afterUtc}
                  AND
                  (
                      NOT EXISTS (SELECT 1 FROM "Boundary")
                      OR e."CreatedAt" <= (SELECT "CreatedAt" FROM "Boundary")
                  )
            )
            SELECT s.*
            FROM "Selected" AS s
            WHERE (SELECT COUNT(*) FROM "Selected") <= {maxRows}
            ORDER BY s."Sequence"
            LIMIT {maxRows}
            """);

    }

    public static IQueryable<int> CountAfterWatermarkThroughTimestampGroup(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterExclusive,
        int targetLimit)
    {

        DateTimeOffset afterUtc = afterExclusive.ToUniversalTime();

        int boundaryOffset = Math.Max(0, targetLimit - 1);

        return db.Database.SqlQuery<int>(
            $"""
            WITH "Boundary" AS
            (
                SELECT "CreatedAt"
                FROM "Entries"
                WHERE "SessionId" = {sessionId}
                  AND "CreatedAt" > {afterUtc}
                ORDER BY "CreatedAt", "Id"
                LIMIT 1 OFFSET {boundaryOffset}
            )
            SELECT COUNT(*) AS "Value"
            FROM "Entries" AS e
            WHERE e."SessionId" = {sessionId}
              AND e."CreatedAt" > {afterUtc}
              AND
              (
                  NOT EXISTS (SELECT 1 FROM "Boundary")
                  OR e."CreatedAt" <= (SELECT "CreatedAt" FROM "Boundary")
              )
            """);

    }

}
