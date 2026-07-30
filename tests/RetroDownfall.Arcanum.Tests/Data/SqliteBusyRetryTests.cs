using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class SqliteBusyRetryTests
{

    [Fact]
    public void RetryBudget_CountsOnlyScheduledBackoff_AndRejectsOverflow()
    {

        SqliteRetryBudget budget = new(TimeSpan.FromSeconds(10));

        Assert.True(budget.TryReserve(TimeSpan.FromSeconds(6)));

        Assert.True(budget.TryReserve(TimeSpan.FromSeconds(4)));

        Assert.False(budget.TryReserve(TimeSpan.FromTicks(1)));

        Assert.Equal(TimeSpan.FromSeconds(10), budget.ReservedDelay);

    }

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
    public async Task ExecuteAsync_ExhaustsRetriesOnBusy_ThrowsSqliteException()
    {

        int attempts = 0;

        SqliteException thrown = await Assert.ThrowsAsync<SqliteException>(() => SqliteBusyRetry.ExecuteAsync(() =>
        {

            attempts++;

            throw new SqliteException("busy", 5);

        }));

        Assert.True(attempts > 1);

        Assert.Equal(5, thrown.SqliteErrorCode);

    }

    [Fact]
    public async Task ExecuteAsync_ExhaustsRetriesOnWrappedBusy_ThrowsDbUpdateException()
    {

        int attempts = 0;

        DbUpdateException thrown = await Assert.ThrowsAsync<DbUpdateException>(() => SqliteBusyRetry.ExecuteAsync(() =>
        {

            attempts++;

            throw new DbUpdateException("save failed", new SqliteException("busy", 5));

        }));

        Assert.True(attempts > 1);

        Assert.Equal(5, Assert.IsType<SqliteException>(thrown.InnerException).SqliteErrorCode);

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
