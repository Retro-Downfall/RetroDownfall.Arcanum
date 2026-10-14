using System.Diagnostics;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The retry cursor is the busy-retry policy for a caller that writes its own attempt loop. These tests pin the policy the cursor
/// owns - the backoff, the bound, the attempt count and its saturation - and that <c>SqliteBusyRetry.ExecuteAsync</c>, which is
/// written on the cursor, waits exactly as the inline loop does.
/// </summary>
public sealed class SqliteBusyRetryCursorTests
{
    private static SqliteException Busy(int code = 5) => new("busy", code);

    private static Func<TimeSpan, CancellationToken, Task> Recording(List<TimeSpan> delays) =>
        (delay, _) =>
        {
            delays.Add(delay);

            return Task.CompletedTask;
        };

    [Fact]
    public async Task WaitAsync_waits_the_doubling_backoff_and_caps_it_at_two_seconds()
    {
        List<TimeSpan> delays = [];

        SqliteBusyRetryCursor retry = SqliteBusyRetry.Begin();

        for (int attempt = 1; attempt <= 12; attempt++)
        {
            await retry.WaitAsync(Busy(), CancellationToken.None, delayAsync: Recording(delays));
        }

        Assert.Equal(
            [50, 100, 200, 400, 800, 1600, 2000, 2000, 2000, 2000, 2000, 2000],
            delays.Select(static delay => (int)delay.TotalMilliseconds));
    }

    [Fact]
    public async Task WaitAsync_reports_each_failure_with_its_attempt_number_before_the_wait()
    {
        List<string> events = [];

        List<Exception> seen = [];

        Func<int, Exception, CancellationToken, ValueTask> retrying = (attempt, exception, _) =>
        {
            events.Add("retrying " + attempt);

            seen.Add(exception);

            return ValueTask.CompletedTask;
        };

        Func<TimeSpan, CancellationToken, Task> delayAsync = (_, _) =>
        {
            events.Add("wait");

            return Task.CompletedTask;
        };

        SqliteBusyRetryCursor retry = SqliteBusyRetry.Begin();

        SqliteException first = Busy(5);

        SqliteException second = Busy(6);

        await retry.WaitAsync(first, CancellationToken.None, retrying, delayAsync);

        await retry.WaitAsync(second, CancellationToken.None, retrying, delayAsync);

        Assert.Equal(["retrying 1", "wait", "retrying 2", "wait"], events);

        Assert.Same(first, seen[0]);

        Assert.Same(second, seen[1]);
    }

    [Fact]
    public async Task WaitAsync_throws_the_busy_timeout_without_waiting_once_the_bound_is_spent()
    {
        List<TimeSpan> delays = [];

        SqliteBusyRetryCursor retry = SqliteBusyRetry.Begin(TimeSpan.Zero);

        SqliteException busy = Busy();

        GrimoireBusyTimeoutException timeout = await Assert.ThrowsAsync<GrimoireBusyTimeoutException>(
            () => retry.WaitAsync(busy, CancellationToken.None, delayAsync: Recording(delays)));

        Assert.Equal(1, timeout.Attempts);

        Assert.Equal(TimeSpan.Zero, timeout.Deadline);

        Assert.True(timeout.Elapsed >= timeout.Deadline);

        Assert.Same(busy, timeout.InnerException);

        Assert.Empty(delays);
    }

    [Fact]
    public async Task WaitAsync_names_the_attempts_that_waited_before_the_bound_ran_out()
    {
        // The first wait outlasts the bound, so the second busy failure finds it spent and the timeout counts both attempts. The bound
        // is generous against a loaded machine: the first failure is judged right after the cursor starts.
        SqliteBusyRetryCursor retry = SqliteBusyRetry.Begin(TimeSpan.FromMilliseconds(1500));

        Func<TimeSpan, CancellationToken, Task> slow = static (_, token) => Task.Delay(1600, token);

        await retry.WaitAsync(Busy(), CancellationToken.None, delayAsync: slow);

        GrimoireBusyTimeoutException timeout = await Assert.ThrowsAsync<GrimoireBusyTimeoutException>(
            () => retry.WaitAsync(Busy(), CancellationToken.None, delayAsync: slow));

        Assert.Equal(2, timeout.Attempts);

        Assert.True(timeout.Elapsed >= TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task WaitAsync_stops_waiting_when_the_caller_cancels()
    {
        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        SqliteBusyRetryCursor retry = SqliteBusyRetry.Begin();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.WaitAsync(Busy(), cancellation.Token));
    }

    [Fact]
    public async Task WaitAsync_saturates_the_attempt_counter_instead_of_wrapping()
    {
        List<TimeSpan> delays = [];

        List<int> attempts = [];

        Func<int, Exception, CancellationToken, ValueTask> retrying = (attempt, _, _) =>
        {
            attempts.Add(attempt);

            return ValueTask.CompletedTask;
        };

        SqliteBusyRetryCursor retry = new(TimeSpan.FromHours(1), Stopwatch.GetTimestamp(), attempt: int.MaxValue);

        await retry.WaitAsync(Busy(), CancellationToken.None, retrying, Recording(delays));

        await retry.WaitAsync(Busy(), CancellationToken.None, retrying, Recording(delays));

        Assert.Equal([int.MaxValue, int.MaxValue], attempts);

        Assert.All(delays, static delay => Assert.Equal(TimeSpan.FromMilliseconds(2000), delay));
    }

    [Fact]
    public async Task The_inline_loop_waits_exactly_as_ExecuteAsync_does_and_keeps_its_count_across_the_awaits()
    {
        const int failures = 9;

        List<TimeSpan> inline = [];

        SqliteBusyRetryCursor retry = SqliteBusyRetry.Begin();

        int attempts = 0;

        int result;

        while (true)
        {
            try
            {
                attempts++;

                if (attempts <= failures)
                {
                    throw Busy();
                }

                result = 42;

                break;
            }
            catch (Exception ex) when (SqliteBusyRetry.IsBusyOrLocked(ex))
            {
                await retry.WaitAsync(ex, CancellationToken.None, delayAsync: Recording(inline));
            }
        }

        List<TimeSpan> delegated = [];

        int delegatedAttempts = 0;

        int delegatedResult = await SqliteBusyRetry.ExecuteAsync(
            () => ++delegatedAttempts <= failures ? throw Busy() : Task.FromResult(42),
            delayAsync: Recording(delegated));

        Assert.Equal(42, result);

        Assert.Equal(42, delegatedResult);

        Assert.Equal(failures + 1, attempts);

        Assert.Equal(failures + 1, delegatedAttempts);

        Assert.Equal(inline, delegated);

        Assert.Equal(failures, inline.Count);

        Assert.Equal(TimeSpan.FromMilliseconds(2000), inline[^1]);
    }

    [Fact]
    public async Task The_inline_loop_does_not_wait_on_a_failure_that_is_not_busy()
    {
        List<TimeSpan> delays = [];

        SqliteBusyRetryCursor retry = SqliteBusyRetry.Begin();

        int attempts = 0;

        SqliteException constraint = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            while (true)
            {
                try
                {
                    attempts++;

                    throw Busy(19);
                }
                catch (Exception ex) when (SqliteBusyRetry.IsBusyOrLocked(ex))
                {
                    await retry.WaitAsync(ex, CancellationToken.None, delayAsync: Recording(delays));
                }
            }
        });

        Assert.Equal(19, constraint.SqliteErrorCode);

        Assert.Equal(1, attempts);

        Assert.Empty(delays);
    }
}
