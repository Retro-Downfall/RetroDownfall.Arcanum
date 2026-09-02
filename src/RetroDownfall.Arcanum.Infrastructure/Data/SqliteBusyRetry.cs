using System.Diagnostics;

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

    /// <summary>
    /// How long <see cref="ExecuteAsync{T}"/> keeps retrying SQLITE_BUSY/LOCKED before it gives up,
    /// for a caller that supplies no deadline of its own.
    /// </summary>
    /// <remarks>
    /// The bound a single attempt can consume is not the 5000 ms
    /// <c>CovenantSqliteConnectionInitializer.BusyTimeoutMs</c> PRAGMA — that only governs the native
    /// busy-handler inside one step. <c>Microsoft.Data.Sqlite</c> wraps every command, including
    /// <c>BEGIN IMMEDIATE</c>, in its own retry loop bounded by
    /// <see cref="Microsoft.Data.Sqlite.SqliteConnection.DefaultTimeout"/>, which defaults to 30
    /// seconds; <c>CampaignRepository.AddAsync</c> already lowers both <c>DefaultTimeout</c> and
    /// <c>busy_timeout</c> before its own <c>BeginTransaction(deferred: false)</c> for exactly this
    /// reason. A deadline set to that same 30 second per-attempt bound would let a single SQLITE_BUSY
    /// exhaust it before this loop ever retries, so the default has to clear several multiples of it:
    /// long enough that an ordinary exclusive-maintenance hold still resolves normally on every one of
    /// the 155 production call sites this default reaches, short enough that a caller with no
    /// cancellation of its own — an HTTP request the host never put a server-side timeout on — fails
    /// closed instead of hanging the process forever behind a handle that will not let go.
    /// </remarks>
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromMinutes(2);

    public static async Task ExecuteAsync(
        Func<Task> action,
        CancellationToken cancellationToken = default,
        Func<int, Exception, CancellationToken, ValueTask>? retrying = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? deadline = null)
    {
        _ = await ExecuteAsync(
            async () =>
            {
                await action().ConfigureAwait(false);

                return true;
            },
            cancellationToken,
            retrying,
            delayAsync,
            deadline).ConfigureAwait(false);
    }

    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default,
        Func<int, Exception, CancellationToken, ValueTask>? retrying = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? deadline = null)
    {
        int attempt = 1;

        long startedAt = Stopwatch.GetTimestamp();

        TimeSpan bound = deadline ?? DefaultDeadline;

        while (true)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (
                IsBusyOrLocked(ex))
            {
                // Computed once and reused for both the comparison and the throw below - not
                // re-measured at the throw site, which would report a slightly later timestamp than
                // the one this check actually acted on.
                TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

                if (elapsed >= bound)
                {

                    throw new GrimoireBusyTimeoutException(attempt, bound, elapsed, ex);

                }

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

/// <summary>
/// The Grimoire did not become available within <see cref="ExecuteAsync{T}"/>'s deadline — a
/// connection somewhere is holding an exclusive or reserved lock for longer than any ordinary
/// maintenance operation should. Distinct from letting the underlying <see cref="SqliteException"/>
/// propagate forever, which is the failure W3b-4 exists to close: an unbounded retry loop never
/// surfaces this as anything the endpoint layer can map to a maintenance-unavailable response.
/// </summary>
internal sealed class GrimoireBusyTimeoutException : Exception
{

    internal GrimoireBusyTimeoutException(int attempts, TimeSpan deadline, TimeSpan elapsed, Exception lastBusyException)
        : base(
            $"The Grimoire did not become available after {attempts} attempt(s) over {elapsed} "
            + $"(deadline {deadline}); another handle is likely holding an exclusive or reserved lock.",
            lastBusyException)
    {

        Attempts = attempts;

        Deadline = deadline;

        Elapsed = elapsed;

    }

    internal int Attempts { get; }

    /// <summary>The configured bound <see cref="ExecuteAsync{T}"/> was given or defaulted to.</summary>
    internal TimeSpan Deadline { get; }

    /// <summary>
    /// The real time this retry loop spent, measured once at the point the deadline check fired.
    /// Not the same number as <see cref="Deadline"/>: the check runs only after an attempt's own
    /// busy exception is caught, so a single slow attempt can carry the total past the deadline by
    /// as much as that one attempt's own duration before this loop ever gets to check again.
    /// </summary>
    internal TimeSpan Elapsed { get; }

}
