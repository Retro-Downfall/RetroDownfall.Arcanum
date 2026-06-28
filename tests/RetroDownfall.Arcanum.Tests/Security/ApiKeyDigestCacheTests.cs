using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class ApiKeyDigestCacheTests
{

    [Fact]
    public void TryGetDigest_StoredAndNotExpired_ReturnsDigest()
    {

        FakeTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] digest = [1, 2, 3, 4];

        cache.StoreDigest(digest, ttlSeconds: 60);

        bool found = cache.TryGetDigest(out byte[]? result);

        Assert.True(found);

        Assert.Same(digest, result);

    }

    [Fact]
    public void TryGetDigest_Expired_ReturnsFalse()
    {

        FakeTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] digest = [1, 2, 3, 4];

        cache.StoreDigest(digest, ttlSeconds: 1);

        timeProvider.Advance(TimeSpan.FromSeconds(2));

        bool found = cache.TryGetDigest(out byte[]? result);

        Assert.False(found);

        Assert.Null(result);

    }

    [Fact]
    public void Invalidate_AfterStore_ReturnsFalse()
    {

        FakeTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        cache.StoreDigest([1, 2, 3], ttlSeconds: 60);

        cache.Invalidate();

        bool found = cache.TryGetDigest(out byte[]? result);

        Assert.False(found);

        Assert.Null(result);

    }

    [Fact]
    public void StoreDigest_NewDigest_ReplacesOldDigest()
    {

        FakeTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] first = [1, 2, 3];

        byte[] second = [4, 5, 6];

        cache.StoreDigest(first, ttlSeconds: 60);

        cache.StoreDigest(second, ttlSeconds: 60);

        bool found = cache.TryGetDigest(out byte[]? result);

        Assert.True(found);

        Assert.Same(second, result);

    }

    // W3.3 Fix 5: StoreDigest must publish an immutable (digest, expiry) snapshot
    // atomically. The old code did two separate Volatile.Writes (expiry first at
    // :50, then digest at :52); a concurrent reader could see the NEW expiry with
    // the OLD digest and serve a stale digest as valid. With ttl=0 the "old"
    // snapshot is instantly expired, so any reader that returns oldDigest after
    // the new store began must have observed a torn snapshot. The atomic single-
    // reference publish never exhibits this.
    [Fact]
    public async Task StoreDigest_ConcurrentReader_NeverObservesTornSnapshot()
    {

        FakeTimeProvider timeProvider = new();

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] oldDigest = [1, 2, 3];

        byte[] newDigest = [4, 5, 6];

        cache.StoreDigest(oldDigest, ttlSeconds: 0);

        StrongBox<int> sawStaleOld = new(0);

        using CancellationTokenSource cts = new();

        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {

            while (!cts.Token.IsCancellationRequested)
            {

                if (cache.TryGetDigest(out byte[]? d) && d is not null && d.SequenceEqual(oldDigest))
                {

                    Volatile.Write(ref sawStaleOld.Value, 1);

                }

            }

        })).ToArray();

        for (int i = 0; i < 200_000; i++)
        {

            cache.StoreDigest(oldDigest, ttlSeconds: 0);

            cache.StoreDigest(newDigest, ttlSeconds: 60);

        }

        cts.Cancel();

        await Task.WhenAll(readers);

        Assert.Equal(0, Volatile.Read(ref sawStaleOld.Value));

    }

}
