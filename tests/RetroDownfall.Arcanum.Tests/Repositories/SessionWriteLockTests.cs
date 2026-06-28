using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed class SessionWriteLockTests
{

    [Fact]

    public async Task AcquireAsync_SameSessionId_MutuallyExcludes()
    {

        Guid sessionId = Guid.NewGuid();

        const int concurrency = 32;

        int counter = 0;

        int maxConcurrent = 0;

        int currentConcurrent = 0;

        Task[] tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {

                using IDisposable releaser = await SessionWriteLock.AcquireAsync(sessionId).ConfigureAwait(false);

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

    }

    [Fact]

    public async Task AcquireAsync_DistinctSessions_DoNotBlockEachOther()
    {

        Task a = Task.Run(async () =>
        {

            using IDisposable releaser = await SessionWriteLock.AcquireAsync(Guid.NewGuid()).ConfigureAwait(false);

            await Task.Delay(25).ConfigureAwait(false);

        });

        Task b = Task.Run(async () =>
        {

            using IDisposable releaser = await SessionWriteLock.AcquireAsync(Guid.NewGuid()).ConfigureAwait(false);

            await Task.Delay(25).ConfigureAwait(false);

        });

        await Task.WhenAll(a, b);

        Assert.True(a.IsCompleted);

        Assert.True(b.IsCompleted);

    }

    [Fact]

    public async Task AcquireAsync_ReleaserDoubleDispose_IsIdempotent()
    {

        IDisposable releaser = await SessionWriteLock.AcquireAsync(Guid.NewGuid());

        releaser.Dispose();

        releaser.Dispose();

        using IDisposable next = await SessionWriteLock.AcquireAsync(Guid.NewGuid());

        Assert.True(true);

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
