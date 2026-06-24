using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

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
