using Microsoft.EntityFrameworkCore;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Centralizes retry-safe EF writes. EF keeps pending entity states when its transactional
/// <c>SaveChangesAsync</c> fails, so retrying the call is safe after SQLite rolls the failed
/// statement transaction or ambient savepoint back.
/// </summary>
internal static class EfSaveChangesRetry
{

    public static Task<int> ExecuteAsync(
        DbContext db,
        CancellationToken cancellationToken = default,
        Func<int, Exception, CancellationToken, ValueTask>? retrying = null) =>
        SqliteBusyRetry.ExecuteAsync(
            () => db.SaveChangesAsync(cancellationToken),
            cancellationToken,
            retrying);

    /// <summary>
    /// Saves once when the caller's enclosing <see cref="SqliteBusyRetry"/> delegate owns the
    /// retry boundary and recreates the complete transaction on every attempt.
    /// </summary>
    public static Task<int> ExecuteOnceAsync(
        DbContext db,
        CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);

}
