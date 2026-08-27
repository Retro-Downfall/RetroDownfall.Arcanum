using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

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
/// <para><b>Every identity here compares through <see cref="CovenantIdentitySql"/>.</b> These reads
/// used to interpolate the <see cref="Guid"/> straight into the query, which EF binds through its
/// own type mapping as uppercase dashed TEXT — the spelling the object-relational writer stores, and
/// not the one the protected transfer store writes. A Session imported from a backup therefore had a
/// history none of these reads could see: no error, no empty-state distinction, just a conversation
/// that looked like it had never been spoken in. Nothing before now could produce such a Session,
/// because the selective-import planner refused every archive; making that import work is what made
/// this reachable, so the two belong together.</para>
///
/// <para><b>The cost, and it is the largest this shape has been asked to pay.</b> A normalised column
/// cannot use a BINARY-collated index, so these forfeit <c>IX_Entries_SessionId_Sequence</c>,
/// <c>IX_Entries_SessionId_CreatedAt</c> and <c>IX_Entries_SessionId_IsPinned</c>: each read becomes
/// a scan of <c>"Entries"</c>, and the ones that order by <c>Sequence</c> additionally sort rather
/// than walking an index in order. Unlike the erasure and backup call sites, this is the conversation
/// read path and it runs per turn. It is taken because the alternative is a correct-looking read that
/// silently returns nothing for an imported Session, and a wrong answer is not a performance
/// optimisation — but it is the site where an index-preserving answer would be worth building, and
/// the shape of one is known: resolve the Session's stored spelling once, as
/// <see cref="CovenantIdentitySql.ResolveStoredSessionIdAsync"/> already does for foreign keys, and
/// compare exactly against it.</para>
///
/// <para><b>Why the shape is composed rather than interpolated.</b> <c>FromSql</c> turns every
/// interpolation hole into a bound parameter, so SQL text cannot reach it through one — a
/// <c>$"...{CovenantIdentitySql.Keyed(...)}"</c> would send the predicate itself to SQLite as a
/// string literal. <see cref="FormattableStringFactory"/> keeps the predicate in the format string
/// and the identity in the arguments, which is what lets these share the one comparison shape instead
/// of spelling a twelfth copy of it by hand.</para>
/// </remarks>
internal static class EntryTemporalQueries
{

    /// <summary>The one predicate every read here filters a Session by.</summary>
    private static string SessionKeyed(string parameter) =>
        CovenantIdentitySql.Keyed("\"SessionId\"", parameter);

    /// <summary>The same predicate against the aliased <c>Entries</c> of a windowed read.</summary>
    private static string AliasedSessionKeyed(string parameter) =>
        CovenantIdentitySql.Keyed("e.\"SessionId\"", parameter);

    /// <summary>
    /// Most-recent window: <c>ORDER BY "Sequence" DESC LIMIT</c>. Call sites reverse
    /// the result to obtain ascending chronological order.
    /// </summary>
    public static IQueryable<Entry> LoadRecentDescending(
        ArcanumDbContext db,
        Guid sessionId,
        int limit) =>
        db.Entries.FromSql(
            FormattableStringFactory.Create(
                "SELECT * FROM \"Entries\" WHERE " + SessionKeyed("{0}")
                + " ORDER BY \"Sequence\" DESC LIMIT {1}",
                CovenantIdentitySql.Key(sessionId),
                limit));

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
            FormattableStringFactory.Create(
                "SELECT * FROM \"Entries\" WHERE " + SessionKeyed("{0}")
                + " AND \"Sequence\" > {1} ORDER BY \"Sequence\" LIMIT {2}",
                CovenantIdentitySql.Key(sessionId),
                afterSequence,
                limit));

    /// <summary>
    /// Descending page before an exclusive <see cref="Entry.Sequence"/> cursor.
    /// </summary>
    public static IQueryable<Entry> LoadBeforeSequence(
        ArcanumDbContext db,
        Guid sessionId,
        long beforeSequence,
        int limit) =>
        db.Entries.FromSql(
            FormattableStringFactory.Create(
                "SELECT * FROM \"Entries\" WHERE " + SessionKeyed("{0}")
                + " AND \"Sequence\" < {1} ORDER BY \"Sequence\" DESC LIMIT {2}",
                CovenantIdentitySql.Key(sessionId),
                beforeSequence,
                limit));

    /// <summary>
    /// Descending page before a <c>(CreatedAt, Id)</c> cursor whose entry no longer exists, so its
    /// sequence cannot be resolved. Keeps "load older" working across a deleted cursor entry.
    /// </summary>
    /// <remarks>
    /// The cursor's own <c>"Id"</c> is compared with <c>&lt;</c> rather than for equality, so it
    /// orders text rather than matching a row and the shape does not apply to it. It is a tiebreak
    /// inside one <c>CreatedAt</c> group and any total order over the stored text serves.
    /// </remarks>
    public static IQueryable<Entry> LoadBeforeDeletedKeyset(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset beforeCreatedAt,
        Guid beforeId,
        int limit)
    {

        DateTimeOffset beforeUtc = beforeCreatedAt.ToUniversalTime();

        return db.Entries.FromSql(
            FormattableStringFactory.Create(
                "SELECT * FROM \"Entries\" WHERE " + SessionKeyed("{0}")
                + " AND (\"CreatedAt\" < {1} OR (\"CreatedAt\" = {1} AND \"Id\" < {2}))"
                + " ORDER BY \"Sequence\" DESC LIMIT {3}",
                CovenantIdentitySql.Key(sessionId),
                beforeUtc,
                beforeId,
                limit));

    }

    /// <summary>
    /// Resolves an entry's <see cref="Entry.Sequence"/>, or <c>null</c> when the entry is not in
    /// the session.
    /// </summary>
    /// <remarks>
    /// Both identities are normalised. <c>"Entries"."Id"</c> carries the same two spellings its
    /// <c>"SessionId"</c> does, so resolving a cursor inside an imported Session would otherwise
    /// answer "not in this session" for an entry plainly in it, and the caller would fall back to a
    /// keyset page it did not need.
    /// </remarks>
    public static IQueryable<long?> SequenceOf(
        ArcanumDbContext db,
        Guid sessionId,
        Guid entryId) =>
        db.Database.SqlQuery<long?>(
            FormattableStringFactory.Create(
                "SELECT \"Sequence\" AS \"Value\" FROM \"Entries\" WHERE " + SessionKeyed("{0}")
                + " AND " + CovenantIdentitySql.Keyed("\"Id\"", "{1}") + " LIMIT 1",
                CovenantIdentitySql.Key(sessionId),
                CovenantIdentitySql.Key(entryId)));

    /// <summary>
    /// Descending offset page without a cursor: <c>ORDER BY "Sequence" DESC</c>.
    /// </summary>
    public static IQueryable<Entry> LoadDescendingPaged(
        ArcanumDbContext db,
        Guid sessionId,
        int limit,
        int offset) =>
        db.Entries.FromSql(
            FormattableStringFactory.Create(
                "SELECT * FROM \"Entries\" WHERE " + SessionKeyed("{0}")
                + " ORDER BY \"Sequence\" DESC LIMIT {1} OFFSET {2}",
                CovenantIdentitySql.Key(sessionId),
                limit,
                offset));

    /// <summary>
    /// Count entries strictly after an exclusive watermark (<c>CreatedAt &gt;</c>).
    /// </summary>
    public static IQueryable<int> CountAfter(
        ArcanumDbContext db,
        Guid sessionId,
        DateTimeOffset afterExclusive) =>
        db.Database.SqlQuery<int>(
            FormattableStringFactory.Create(
                "SELECT COUNT(*) AS \"Value\" FROM \"Entries\" WHERE " + SessionKeyed("{0}")
                + " AND \"CreatedAt\" > {1}",
                CovenantIdentitySql.Key(sessionId),
                afterExclusive));

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
            FormattableStringFactory.Create(
                """
                WITH "Boundary" AS
                (
                    SELECT "CreatedAt"
                    FROM "Entries"
                    WHERE
                """
                + " " + SessionKeyed("{0}")
                + """
                      AND "CreatedAt" > {1}
                    ORDER BY "CreatedAt", "Id"
                    LIMIT 1 OFFSET {2}
                ),
                "Selected" AS
                (
                    SELECT e.*
                    FROM "Entries" AS e
                    WHERE
                """
                + " " + AliasedSessionKeyed("{0}")
                + """
                      AND e."CreatedAt" > {1}
                      AND
                      (
                          NOT EXISTS (SELECT 1 FROM "Boundary")
                          OR e."CreatedAt" <= (SELECT "CreatedAt" FROM "Boundary")
                      )
                )
                SELECT s.*
                FROM "Selected" AS s
                WHERE (SELECT COUNT(*) FROM "Selected") <= {3}
                ORDER BY s."Sequence"
                LIMIT {3}
                """,
                CovenantIdentitySql.Key(sessionId),
                afterUtc,
                boundaryOffset,
                maxRows));

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
            FormattableStringFactory.Create(
                """
                WITH "Boundary" AS
                (
                    SELECT "CreatedAt"
                    FROM "Entries"
                    WHERE
                """
                + " " + SessionKeyed("{0}")
                + """
                      AND "CreatedAt" > {1}
                    ORDER BY "CreatedAt", "Id"
                    LIMIT 1 OFFSET {2}
                )
                SELECT COUNT(*) AS "Value"
                FROM "Entries" AS e
                WHERE
                """
                + " " + AliasedSessionKeyed("{0}")
                + """
                  AND e."CreatedAt" > {1}
                  AND
                  (
                      NOT EXISTS (SELECT 1 FROM "Boundary")
                      OR e."CreatedAt" <= (SELECT "CreatedAt" FROM "Boundary")
                  )
                """,
                CovenantIdentitySql.Key(sessionId),
                afterUtc,
                boundaryOffset));

    }

}
