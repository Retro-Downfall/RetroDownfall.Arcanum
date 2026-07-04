using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Metrics: <c>arcanum_grimoire_operation_duration_seconds</c> (catalog, low priority for the initial
/// <c>/metrics</c> implementation) would most naturally wrap this retry loop or individual repository
/// methods — every Grimoire write already funnels through <see cref="ExecuteAsync{T}"/>, so a single
/// <c>Stopwatch</c> + <c>ArcanumMetrics.GrimoireOperationDuration.Record(...)</c> pair here would cover
/// the whole repository layer without touching each call site. Not implemented yet — deliberately
/// deferred since it needs an <c>operation</c> label the retry loop does not currently have.
/// </summary>
internal static class SqliteBusyRetry
{

    private const int MaxAttempts = 5;

    private const int BaseDelayMilliseconds = 50;

    private static readonly TimeSpan MaxTotalDelay = TimeSpan.FromSeconds(10);

    public static async Task ExecuteAsync(
        Func<Task> action,
        CancellationToken cancellationToken = default)
    {
        _ = await ExecuteAsync(
            async () =>
            {
                await action().ConfigureAwait(false);

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsBusyOrLocked(ex) && attempt < MaxAttempts && stopwatch.Elapsed < MaxTotalDelay)
            {
                await Task.Delay(ComputeDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("SqliteBusyRetry loop exited without returning a value.");
    }


    private static bool IsBusyOrLocked(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6;

    private static TimeSpan ComputeDelay(int attempt)
    {
        int cappedAttempt = Math.Min(attempt, 10);

        int delayMs = BaseDelayMilliseconds * (1 << (cappedAttempt - 1));

        return TimeSpan.FromMilliseconds(Math.Min(delayMs, 2_000));
    }

}
