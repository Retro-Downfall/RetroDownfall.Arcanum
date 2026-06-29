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

}
