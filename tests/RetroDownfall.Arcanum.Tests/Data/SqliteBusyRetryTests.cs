using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class SqliteBusyRetryTests
{

    [Fact]
    public async Task ExecuteAsync_SucceedsFirstTime_ReturnsValue()
    {

        int result = await SqliteBusyRetry.ExecuteAsync(() => Task.FromResult(42));

        Assert.Equal(42, result);

    }

    [Fact]
    public async Task ExecuteAsync_RetriesOnBusyThenSucceeds_ReturnsValue()
    {

        int attempts = 0;

        int result = await SqliteBusyRetry.ExecuteAsync(() =>
        {

            attempts++;

            if (attempts < 3)
            {

                throw new SqliteException("busy", 5);

            }

            return Task.FromResult(42);

        });

        Assert.Equal(42, result);

        Assert.Equal(3, attempts);

    }

    [Fact]
    public async Task ExecuteAsync_RetriesOnWrappedLockedThenSucceeds_ReturnsValue()
    {

        int attempts = 0;

        int result = await SqliteBusyRetry.ExecuteAsync(() =>
        {

            attempts++;

            if (attempts < 3)
            {

                throw new DbUpdateException(
                    "save failed",
                    new InvalidOperationException(
                        "provider wrapper",
                        new SqliteException("locked", 6)));

            }

            return Task.FromResult(42);

        });

        Assert.Equal(42, result);

        Assert.Equal(3, attempts);

    }

    [Fact]
    public async Task ExecuteAsync_RetriesBeyondFormerAttemptCeiling_ThenSucceeds()
    {

        int attempts = 0;

        int result = await SqliteBusyRetry.ExecuteAsync(() =>
        {

            attempts++;

            if (attempts <= 7)
            {

                throw new SqliteException("busy", 5);

            }

            return Task.FromResult(42);

        }, delayAsync: static (_, _) => Task.CompletedTask);

        Assert.Equal(42, result);

        Assert.Equal(8, attempts);

    }

    /// <summary>
    /// W3b-4: the loop retried SQLITE_BUSY/LOCKED forever, bounded only by the caller's own
    /// cancellation token — a caller with no token of its own (an HTTP request the host put no
    /// server-side timeout on) never got the maintenance-unavailable answer at all.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_StopsRetryingAfterTheDeadlineElapses()
    {

        int attempts = 0;

        GrimoireBusyTimeoutException thrown = await Assert.ThrowsAsync<GrimoireBusyTimeoutException>(
            () => SqliteBusyRetry.ExecuteAsync(
                () =>
                {

                    attempts++;

                    throw new SqliteException("busy", 5);

                },
                CancellationToken.None,
                deadline: TimeSpan.FromMilliseconds(120)));

        Assert.True(attempts >= 2, $"Expected at least one retry before the deadline; observed {attempts}.");

        Assert.Equal(attempts, thrown.Attempts);

    }

    [Fact]
    public async Task ExecuteAsync_RetriesWrappedBusyBeyondFormerAttemptCeiling_ThenSucceeds()
    {

        int attempts = 0;

        int result = await SqliteBusyRetry.ExecuteAsync(() =>
        {

            attempts++;

            if (attempts <= 7)
            {

                throw new DbUpdateException("save failed", new SqliteException("busy", 5));

            }

            return Task.FromResult(42);

        }, delayAsync: static (_, _) => Task.CompletedTask);

        Assert.Equal(42, result);

        Assert.Equal(8, attempts);

    }

    [Fact]
    public async Task ExecuteAsync_NonBusyException_DoesNotRetry()
    {

        int attempts = 0;

        SqliteException thrown = await Assert.ThrowsAsync<SqliteException>(() => SqliteBusyRetry.ExecuteAsync(() =>
        {

            attempts++;

            throw new SqliteException("constraint", 19);

        }));

        Assert.Equal(1, attempts);

        Assert.Equal(19, thrown.SqliteErrorCode);

    }

    [Fact]
    public async Task ExecuteAsync_UnrelatedDbUpdateException_DoesNotRetry()
    {

        int attempts = 0;

        DbUpdateException thrown = await Assert.ThrowsAsync<DbUpdateException>(() => SqliteBusyRetry.ExecuteAsync(() =>
        {

            attempts++;

            throw new DbUpdateException("constraint", new SqliteException("constraint", 19));

        }));

        Assert.Equal(1, attempts);

        Assert.Equal(19, Assert.IsType<SqliteException>(thrown.InnerException).SqliteErrorCode);

    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_StopsRetrying()
    {

        using CancellationTokenSource cts = new();

        cts.Cancel();

        int attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteBusyRetry.ExecuteAsync(() =>
        {

            attempts++;

            throw new SqliteException("busy", 5);

        }, cts.Token));

        Assert.Equal(1, attempts);

    }

    [Fact]
    public async Task ExecuteAsync_ActionCancellation_DoesNotRetry()
    {

        int attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteBusyRetry.ExecuteAsync<int>(() =>
        {

            attempts++;

            throw new OperationCanceledException(
                "cancelled",
                new DbUpdateException("save failed", new SqliteException("busy", 5)));

        }));

        Assert.Equal(1, attempts);

    }

    [Fact]
    public async Task ExecuteAsync_VoidOverload_RetriesOnBusyThenSucceeds()
    {

        int attempts = 0;

        await SqliteBusyRetry.ExecuteAsync(() =>
        {

            attempts++;

            if (attempts < 2)
            {

                throw new SqliteException("busy", 5);

            }

            return Task.CompletedTask;

        });

        Assert.Equal(2, attempts);

    }

}
