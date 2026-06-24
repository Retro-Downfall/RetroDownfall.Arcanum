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

}
