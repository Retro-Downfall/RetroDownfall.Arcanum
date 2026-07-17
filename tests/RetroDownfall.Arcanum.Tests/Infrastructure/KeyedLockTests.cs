using RetroDownfall.Arcanum.Infrastructure.Caching;

namespace RetroDownfall.Arcanum.Tests.Infrastructure;

public sealed class KeyedLockTests
{

    [Fact]

    public async Task AcquireAndRelease_SameKeyRepeatedly_MapCountStaysBounded()
    {

        KeyedLock<string> keyedLock = new();

        for (int i = 0; i < 10_000; i++)
        {

            using IDisposable releaser = await keyedLock.AcquireAsync("hot-key");

            Assert.True(keyedLock.CountForTesting <= 1);

        }

        Assert.Equal(0, keyedLock.CountForTesting);

    }

    [Fact]

    public async Task AcquireAndRelease_ManyDistinctKeys_MapCountReturnsToZero()
    {

        KeyedLock<int> keyedLock = new();

        List<IDisposable> releasers = new(500);

        for (int i = 0; i < 500; i++)
        {

            releasers.Add(await keyedLock.AcquireAsync(i));

        }

        Assert.Equal(500, keyedLock.CountForTesting);

        foreach (IDisposable releaser in releasers)
        {

            releaser.Dispose();

        }

        Assert.Equal(0, keyedLock.CountForTesting);

    }

    [Fact]

    public async Task Contention_SameKey_MutualExclusionHoldsAndMapEvictsAfter()
    {

        KeyedLock<string> keyedLock = new();

        const int concurrency = 64;

        int counter = 0;

        int maxConcurrent = 0;

        int currentConcurrent = 0;

        Task[] tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {

                using IDisposable releaser = await keyedLock.AcquireAsync("shared").ConfigureAwait(false);

                int observed = Interlocked.Increment(ref currentConcurrent);

                InterlockedMax(ref maxConcurrent, observed);

                Interlocked.Increment(ref counter);

                await Task.Yield();

                Interlocked.Decrement(ref currentConcurrent);

            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(concurrency, counter);

        Assert.Equal(1, maxConcurrent);

        Assert.Equal(0, keyedLock.CountForTesting);

    }

    [Fact]

    public async Task NoEvict_WhileWaiterBlocked_EntrySurvivesUntilLastRelease()
    {

        KeyedLock<string> keyedLock = new();

        IDisposable first = await keyedLock.AcquireAsync("k");

        TaskCompletionSource<bool> secondGotLock = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> secondMayRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task secondAcquire = Task.Run(async () =>
        {

            using IDisposable releaser = await keyedLock.AcquireAsync("k").ConfigureAwait(false);

            secondGotLock.SetResult(true);

            await secondMayRelease.Task.ConfigureAwait(false);

        });

        await Task.Delay(50);

        Assert.False(secondGotLock.Task.IsCompleted);

        Assert.Equal(1, keyedLock.CountForTesting);

        first.Dispose();

        await secondGotLock.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(secondGotLock.Task.IsCompleted);

        Assert.Equal(1, keyedLock.CountForTesting);

        secondMayRelease.SetResult(true);

        await secondAcquire;

        Assert.Equal(0, keyedLock.CountForTesting);

    }

    [Fact]

    public async Task Releaser_DoubleDispose_IsIdempotentAndDoesNotOverRelease()
    {

        KeyedLock<string> keyedLock = new();

        IDisposable releaser = await keyedLock.AcquireAsync("k");

        releaser.Dispose();

        releaser.Dispose();

        Assert.Equal(0, keyedLock.CountForTesting);

        using IDisposable second = await keyedLock.AcquireAsync("k");

        Assert.Equal(1, keyedLock.CountForTesting);

    }

    [Fact]

    public async Task Cancellation_WhileWaiting_DoesNotAcquireAndBestEffortEvicts()
    {

        KeyedLock<string> keyedLock = new();

        IDisposable holder = await keyedLock.AcquireAsync("k");

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {

            await keyedLock.AcquireAsync("k", cts.Token);

        });

        holder.Dispose();

        Assert.Equal(0, keyedLock.CountForTesting);

    }

    [Fact]

    public async Task CancelStorm_WithSuccessfulAcquires_PreservesMutualExclusionAndClearsMap()
    {

        KeyedLock<string> keyedLock = new();

        const int concurrency = 64;

        int counter = 0;

        int maxConcurrent = 0;

        int currentConcurrent = 0;

        int cancelCount = 0;

        Task[] tasks = Enumerable.Range(0, concurrency)
            .Select(i => Task.Run(async () =>
            {

                using CancellationTokenSource cts = new();

                if (i % 3 == 0)
                {

                    cts.CancelAfter(TimeSpan.FromMilliseconds(1));

                }

                try
                {

                    using IDisposable releaser = await keyedLock.AcquireAsync("shared", cts.Token).ConfigureAwait(false);

                    int observed = Interlocked.Increment(ref currentConcurrent);

                    InterlockedMax(ref maxConcurrent, observed);

                    Interlocked.Increment(ref counter);

                    await Task.Delay(5).ConfigureAwait(false);

                    Interlocked.Decrement(ref currentConcurrent);

                }
                catch (OperationCanceledException)
                {

                    Interlocked.Increment(ref cancelCount);

                }

            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.True(counter > 0);

        Assert.True(cancelCount > 0);

        Assert.Equal(1, maxConcurrent);

        Assert.Equal(0, keyedLock.CountForTesting);

    }

    [Fact]

    public void Constructor_NullComparer_UsesDefaultComparer()
    {

        KeyedLock<string> keyedLock = new(comparer: null);

        Assert.Equal(0, keyedLock.CountForTesting);

    }

    private static void InterlockedMax(ref int target, int value)
    {

        int initial;

        do
        {

            initial = target;

            if (value <= initial)
            {

                return;

            }

        } while (Interlocked.CompareExchange(ref target, value, initial) != initial);

    }

}
