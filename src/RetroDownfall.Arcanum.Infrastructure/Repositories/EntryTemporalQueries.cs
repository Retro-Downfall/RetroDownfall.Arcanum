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
///
/// <para><b>The identity is interpolated straight into the statement, and that is now correct rather
/// than merely fast.</b> <c>FromSql</c> turns every hole into a bound parameter, and the SQLite
/// provider renders a <see cref="Guid"/> as uppercase dashed text — the one form
/// <c>"Entries"."Id"</c> and <c>"Entries"."SessionId"</c> are permitted to hold. These reads spent an
/// interval comparing <c>lower(replace(col, '-', ''))</c> instead, because those two columns could
/// then hold a second spelling and an exact comparison returned a conversation that looked like it
/// had never been spoken in. Both columns are settled to the canonical form, refused at the write by
/// a guard trigger, and swept on upgrade, so the reason for normalising them is gone.</para>
///
/// <para><b>What that buys, which is why the settlement was worth doing.</b> A normalised column
/// cannot use a BINARY-collated index, so every read here forfeited the <c>SessionId</c>-led index it
/// would otherwise seek. Three of these were measured in that state: the recent window and the
/// ascending sequence page each planned as <c>SCAN Entries</c> followed by
/// <c>USE TEMP B-TREE FOR ORDER BY</c> — a walk of the largest table in the database and then a sort
/// of the result, on the conversation read path that runs once per turn for every user — and the
/// cursor resolution planned as a bare <c>SCAN Entries</c> to return one row. Exactly, every read here
/// seeks: a <c>SessionId</c>-led index for those that filter on that column, with the ordering served
/// by the same index where there is one, and the <c>"Entries"</c> identity index for the cursor
/// resolution, which filters on <c>"Id"</c> as well. <c>EntryTemporalQueryPlanTests</c> pins the plan
/// of the statement each entry point actually issues and compares it whole, so neither the scan nor
/// anything else about those plans can move unremarked.</para>
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
