using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// Parameterized raw SQL for <c>Entries</c> temporal reads. EF Core's SQLite provider
/// cannot translate <see cref="DateTimeOffset"/> in ORDER BY or comparisons, so these
/// queries run against the sortable UTC <c>CreatedAt</c> text column. Keyset pages use
/// <c>(CreatedAt, Id)</c> tie-breaks; watermark counts use exclusive <c>CreatedAt &gt;</c>.
/// </summary>
internal static class EntryTemporalQueries
{

    /// <summary>
    /// Most-recent window: <c>ORDER BY "CreatedAt" DESC LIMIT</c>. Call sites reverse
    /// the result to obtain ascending chronological order.
    /// </summary>
    public static IQueryable<Entry> LoadRecentDescending(
        ArcanumDbContext db,
        Guid sessionId,
        int limit) =>
        db.Entries.FromSql(
            $"""SELECT * FROM "Entries" WHERE "SessionId" = {sessionId} ORDER BY "CreatedAt" DESC LIMIT {limit}""");

    /// <summary>
    /// Ascending keyset page after an exclusive cursor. Normalizes <paramref name="afterCreatedAt"/>
    /// to UTC; uses <c>CreatedAt &gt;</c> or <c>(CreatedAt = AND Id &gt;)</c>.
    /// </summary>
    public static IQueryable<Entry> LoadAfterKeyset(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterCreatedAt,
        Guid afterId,
        int limit)
    {

        DateTimeOffset afterUtc = afterCreatedAt.ToUniversalTime();

        return db.Entries.FromSql(
            $"""
            SELECT * FROM "Entries"
            WHERE "SessionId" = {sessionId}
              AND ("CreatedAt" > {afterUtc} OR ("CreatedAt" = {afterUtc} AND "Id" > {afterId}))
            ORDER BY "CreatedAt", "Id"
            LIMIT {limit}
            """);

    }

    /// <summary>
    /// Descending keyset page before an exclusive cursor. Normalizes
    /// <paramref name="beforeCreatedAt"/> to UTC; uses <c>CreatedAt &lt;</c> or
    /// <c>(CreatedAt = AND Id &lt;)</c>.
    /// </summary>
    public static IQueryable<Entry> LoadBeforeKeyset(
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
            ORDER BY "CreatedAt" DESC, "Id" DESC
            LIMIT {limit}
            """);

    }

    /// <summary>
    /// Descending offset page without a cursor: <c>ORDER BY "CreatedAt" DESC, "Id" DESC</c>.
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
            ORDER BY "CreatedAt" DESC, "Id" DESC
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
            ORDER BY s."CreatedAt", s."Id"
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

    /// <summary>
    /// Count entries at or before an inclusive <c>(CreatedAt, Id)</c> cursor — used by session
    /// fork's "up to and including this entry" cutoff to pre-check the code-owned per-session entry
    /// limit before copying anything.
    /// </summary>
    public static IQueryable<int> CountAtOrBeforeKeyset(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset atOrBeforeCreatedAt,
        Guid atOrBeforeId)
    {

        DateTimeOffset atOrBeforeUtc = atOrBeforeCreatedAt.ToUniversalTime();

        return db.Database.SqlQuery<int>(
            $"""
            SELECT COUNT(*) AS "Value" FROM "Entries"
            WHERE "SessionId" = {sessionId}
              AND ("CreatedAt" < {atOrBeforeUtc} OR ("CreatedAt" = {atOrBeforeUtc} AND "Id" <= {atOrBeforeId}))
            """);

    }

}
