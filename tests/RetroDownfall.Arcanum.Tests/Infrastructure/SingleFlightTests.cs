using System.Collections.Concurrent;

using RetroDownfall.Arcanum.Infrastructure.Caching;

namespace RetroDownfall.Arcanum.Tests.Infrastructure;

public sealed class SingleFlightTests
{

    [Fact]
    public async Task CoalesceAsync_concurrent_misses_for_same_key_run_factory_once()
    {

        const int concurrency = 32;

        ConcurrentDictionary<string, Lazy<Task<int>>> inFlight = new();

        int invocations = 0;

        Barrier barrier = new(concurrency);

        Task<int>[] tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {

                barrier.SignalAndWait();

                return await SingleFlight.CoalesceAsync(
                    inFlight,
                    "shared-key",
                    async () =>
                    {

                        Interlocked.Increment(ref invocations);

                        await Task.Delay(50);

                        return 42;

                    });

            }))
            .ToArray();

        int[] results = await Task.WhenAll(tasks);

        Assert.Equal(1, invocations);

        Assert.All(results, r => Assert.Equal(42, r));

        Assert.Empty(inFlight);

    }

    [Fact]
    public async Task CoalesceAsync_distinct_keys_run_independently_and_cleanup()
    {

        ConcurrentDictionary<string, Lazy<Task<int>>> inFlight = new();

        int a = await SingleFlight.CoalesceAsync(inFlight, "a", () => Task.FromResult(1));

        int b = await SingleFlight.CoalesceAsync(inFlight, "b", () => Task.FromResult(2));

        Assert.Equal(1, a);

        Assert.Equal(2, b);

        Assert.Empty(inFlight);

    }

    [Fact]
    public async Task CoalesceAsync_releases_entry_after_completion_so_next_miss_reruns()
    {

        ConcurrentDictionary<string, Lazy<Task<int>>> inFlight = new();

        int invocations = 0;

        int first = await SingleFlight.CoalesceAsync(
            inFlight,
            "k",
            async () =>
            {

                Interlocked.Increment(ref invocations);

                await Task.CompletedTask;

                return 7;

            });

        int second = await SingleFlight.CoalesceAsync(
            inFlight,
            "k",
            async () =>
            {

                Interlocked.Increment(ref invocations);

                await Task.CompletedTask;

                return 7;

            });

        Assert.Equal(7, first);

        Assert.Equal(7, second);

        Assert.Equal(2, invocations);

        Assert.Empty(inFlight);

    }

    [Fact]
    public async Task CoalesceAsync_factory_fault_propagates_and_entry_is_cleaned_up()
    {

        ConcurrentDictionary<string, Lazy<Task<int>>> inFlight = new();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {

            await SingleFlight.CoalesceAsync(
                inFlight,
                "boom",
                async () =>
                {

                    await Task.CompletedTask;

                    throw new InvalidOperationException("boom");

                });

        });

        Assert.Empty(inFlight);

    }

}
