using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

    private const int BaseDelayMilliseconds = 50;

    public static async Task ExecuteAsync(
        Func<Task> action,
        CancellationToken cancellationToken = default,
        Func<int, Exception, CancellationToken, ValueTask>? retrying = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _ = await ExecuteAsync(
            async () =>
            {
                await action().ConfigureAwait(false);

                return true;
            },
            cancellationToken,
            retrying,
            delayAsync).ConfigureAwait(false);
    }

    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default,
        Func<int, Exception, CancellationToken, ValueTask>? retrying = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        int attempt = 1;

        while (true)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (
                IsBusyOrLocked(ex))
            {
                TimeSpan delay = ComputeDelay(attempt);

                if (retrying is not null)
                {
                    await retrying(attempt, ex, cancellationToken).ConfigureAwait(false);
                }

                if (delayAsync is null)
                {

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                }
                else
                {

                    await delayAsync(delay, cancellationToken).ConfigureAwait(false);

                }

                if (attempt < int.MaxValue)
                {

                    attempt++;

                }
            }
        }
    }

    private static bool IsBusyOrLocked(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return false;
        }

        if (exception is SqliteException direct)
        {
            return direct.SqliteErrorCode is 5 or 6;
        }

        if (exception is not DbUpdateException)
        {
            return false;
        }

        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return false;
            }

            if (current is SqliteException sqlite)
            {
                return sqlite.SqliteErrorCode is 5 or 6;
            }
        }

        return false;
    }

    private static TimeSpan ComputeDelay(int attempt)
    {
        int cappedAttempt = Math.Min(attempt, 10);

        int delayMs = BaseDelayMilliseconds * (1 << (cappedAttempt - 1));

        return TimeSpan.FromMilliseconds(Math.Min(delayMs, 2_000));
    }

}
