using System.Globalization;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>
/// The key range one memory-review request's receipts occupy, shared by all three stores.
/// </summary>
/// <remarks>
/// A receipt's <c>DecisionId</c> begins with the request identity and a colon, and it is the table's
/// primary key. Finding a request's receipts therefore needs a range over that key, not a
/// <c>substr</c> of it: SQLite cannot seek through a function of the key column, so the old predicate
/// scanned every receipt ever written, twice per apply and once of them under the write lock. The
/// range is half open, <c>[prefix, upper)</c>, and <c>upper</c> is the prefix with its final colon
/// (U+003A) replaced by the next code point, a semicolon (U+003B), so under the column's default
/// <c>BINARY</c> collation it holds exactly the identifiers that begin with the prefix.
/// </remarks>
internal static class MemoryReviewReceiptKeys
{
    /// <summary>The inclusive lower bound: every receipt of the request starts with this.</summary>
    internal static string Prefix(Guid requestId) =>
        requestId.ToString("N", CultureInfo.InvariantCulture) + ":";

    /// <summary>The exclusive upper bound of the request's receipts.</summary>
    internal static string UpperBound(Guid requestId) =>
        requestId.ToString("N", CultureInfo.InvariantCulture) + ";";
}
