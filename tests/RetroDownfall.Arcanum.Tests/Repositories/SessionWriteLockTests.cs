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

    /// <summary>
    /// The second session must acquire while the first still holds its lock. This used to await
    /// <c>Task.WhenAll</c> over two 25 ms bodies and then assert <c>IsCompleted</c> on both, which is
    /// true by definition of having awaited them — a <c>KeyedLock</c> collapsed to one global gate
    /// would simply run the two bodies back to back and the test would still pass.
    /// </summary>
    [Fact]

    public async Task AcquireAsync_DistinctSessions_DoNotBlockEachOther()
    {

        Guid held = Guid.NewGuid();

        Guid other = Guid.NewGuid();

        using IDisposable heldReleaser = await SessionWriteLock.AcquireAsync(held);

        Assert.True(SessionWriteLock.IsHeldForTesting(held));

        using IDisposable otherReleaser = await SessionWriteLock
            .AcquireAsync(other)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(SessionWriteLock.IsHeldForTesting(other));

        Assert.True(SessionWriteLock.IsHeldForTesting(held));

    }

    /// <summary>
    /// A second dispose must not return a second permit. This used to dispose twice and then acquire a
    /// <em>fresh</em> <see cref="Guid"/>, ending in a literal <c>Assert.True(true)</c>, so it never
    /// observed the session whose releaser it had double-disposed. An over-released semaphore lets two
    /// writers hold one session's write lock at once, which is what the lock exists to prevent.
    /// </summary>
    [Fact]

    public async Task AcquireAsync_ReleaserDoubleDispose_IsIdempotent()
    {

        Guid sessionId = Guid.NewGuid();

        IDisposable releaser = await SessionWriteLock.AcquireAsync(sessionId);

        Assert.True(SessionWriteLock.IsHeldForTesting(sessionId));

        releaser.Dispose();

        Assert.False(SessionWriteLock.IsHeldForTesting(sessionId));

        releaser.Dispose();

        IDisposable reacquired = await SessionWriteLock
            .AcquireAsync(sessionId)
            .WaitAsync(TimeSpan.FromSeconds(10));

        try
        {

            Assert.True(SessionWriteLock.IsHeldForTesting(sessionId));

            Task<IDisposable> contender = SessionWriteLock.AcquireAsync(sessionId);

            Task winner = await Task.WhenAny(contender, Task.Delay(TimeSpan.FromMilliseconds(250)));

            Assert.False(
                ReferenceEquals(winner, contender),
                "The second dispose released a spare permit, so two writers hold the same session's write lock.");

            reacquired.Dispose();

            using IDisposable settled = await contender.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(SessionWriteLock.IsHeldForTesting(sessionId));

        }
        catch
        {

            reacquired.Dispose();

            throw;

        }

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
