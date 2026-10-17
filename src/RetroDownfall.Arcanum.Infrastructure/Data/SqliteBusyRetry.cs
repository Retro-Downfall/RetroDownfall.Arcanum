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
    /// long enough that an ordinary exclusive-maintenance hold still resolves normally on every
    /// production call site this default reaches, short enough that a caller with no
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
        SqliteBusyRetryCursor retry = Begin(deadline);

        while (true)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (
                IsBusyOrLocked(ex))
            {
                await retry.WaitAsync(ex, cancellationToken, retrying, delayAsync).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Starts one retry sequence for a caller that writes the attempt loop itself instead of handing the attempt to
    /// <see cref="ExecuteAsync{T}"/> as a delegate. The policy is the same one, because <see cref="ExecuteAsync{T}"/> is written on
    /// the cursor this returns: the bound runs from this call, a busy failure waits and counts an attempt, and the wait that finds the
    /// bound spent throws <see cref="GrimoireBusyTimeoutException"/>.
    /// </summary>
    /// <remarks>
    /// A caller whose attempt uses an admission lease it was handed needs this form: a delegate would capture the lease, and the
    /// lease is passed by value from where it is acquired to every use, never kept by a closure or any other object. The loop is
    /// <c>while (true) { try { ...one attempt...; return; } catch (Exception ex) when (IsBusyOrLocked(ex)) { await retry.WaitAsync(ex, ct); } }</c>,
    /// with the cursor in a local of the calling method.
    /// </remarks>
    internal static SqliteBusyRetryCursor Begin(TimeSpan? deadline = null) =>
        new(deadline ?? DefaultDeadline, Stopwatch.GetTimestamp());

    /// <summary>
    /// Whether a failure is SQLite's busy or locked answer, which a retry can clear. A cancellation is never
    /// one, whatever it wraps.
    /// </summary>
    internal static bool IsBusyOrLocked(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return false;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
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

    internal static TimeSpan ComputeDelay(int attempt)
    {
        int cappedAttempt = Math.Min(attempt, 10);

        int delayMs = BaseDelayMilliseconds * (1 << (cappedAttempt - 1));

        return TimeSpan.FromMilliseconds(Math.Min(delayMs, 2_000));
    }
}

/// <summary>
/// One busy-retry sequence, advanced by the caller's own loop (<see cref="SqliteBusyRetry.Begin"/>): the clock the bound runs on and
/// the number of the attempt that just failed. It holds nothing of the attempt, and no delegate: the hooks that observe and perform
/// the wait are arguments of <see cref="WaitAsync"/>.
/// </summary>
/// <remarks>
/// A value type kept in a local of the method that runs the attempts. <see cref="WaitAsync"/> advances the attempt number before it
/// first awaits, in a method that is not async, so the number survives on the local across the awaits of an async caller; an async
/// method of a struct would advance a copy.
/// </remarks>
internal struct SqliteBusyRetryCursor
{
    private readonly long _startedAt;

    private readonly TimeSpan _bound;

    private int _attempt;

    internal SqliteBusyRetryCursor(TimeSpan bound, long startedAt, int attempt = 1)
    {
        _bound = bound;

        _startedAt = startedAt;

        _attempt = attempt;
    }

    /// <summary>
    /// Waits out one busy or locked failure of the attempt just made, or gives up on it. Throws
    /// <see cref="GrimoireBusyTimeoutException"/> when the bound has elapsed (before it waits), otherwise reports the failure to
    /// <paramref name="retrying"/>, waits the doubling backoff (50 ms, capped at 2 seconds from the seventh attempt) with
    /// <paramref name="delayAsync"/> or <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, and counts the attempt.
    /// </summary>
    internal Task WaitAsync(
        Exception busy,
        CancellationToken cancellationToken,
        Func<int, Exception, CancellationToken, ValueTask>? retrying = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        // Computed once and reused for both the comparison and the throw below - not
        // re-measured at the throw site, which would report a slightly later timestamp than
        // the one this check actually acted on.
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_startedAt);

        if (elapsed >= _bound)
        {
            throw new GrimoireBusyTimeoutException(_attempt, _bound, elapsed, busy);
        }

        int attempt = _attempt;

        if (_attempt < int.MaxValue)
        {
            _attempt++;
        }

        return PauseAsync(attempt, busy, cancellationToken, retrying, delayAsync);
    }

    private static async Task PauseAsync(
        int attempt,
        Exception busy,
        CancellationToken cancellationToken,
        Func<int, Exception, CancellationToken, ValueTask>? retrying,
        Func<TimeSpan, CancellationToken, Task>? delayAsync)
    {
        TimeSpan delay = SqliteBusyRetry.ComputeDelay(attempt);

        if (retrying is not null)
        {
            await retrying(attempt, busy, cancellationToken).ConfigureAwait(false);
        }

        if (delayAsync is null)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await delayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The Grimoire did not become available within <see cref="ExecuteAsync{T}"/>'s deadline — a
/// connection somewhere is holding an exclusive or reserved lock for longer than any ordinary
/// maintenance operation should. Distinct from letting the underlying <see cref="SqliteException"/>
/// propagate forever: an unbounded retry loop never surfaces the contention as anything a caller
/// can act on, so this bounds the wait and names it.
///
/// <para>Only <see cref="Covenant.CovenantCanonicalErasureTransaction"/> catches it today, turning
/// it into that erasure's own typed refusal. Every other <c>SqliteBusyRetry</c> caller lets it
/// propagate, so on an HTTP path it reaches <c>ArcanumExceptionHandler</c> and is reported as a
/// generic <c>500</c>. There is no endpoint mapping from this exception to a
/// maintenance-unavailable response; adding one is separate work.</para>
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
