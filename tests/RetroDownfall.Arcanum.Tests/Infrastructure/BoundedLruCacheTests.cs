using RetroDownfall.Arcanum.Infrastructure.Caching;

namespace RetroDownfall.Arcanum.Tests.Infrastructure;

public class BoundedLruCacheTests
{
    [Fact]
    public void Constructor_ZeroCapacity_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedLruCache<string, int>(0));
    }

    [Fact]
    public void Constructor_NegativeCapacity_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedLruCache<string, int>(-1));
    }

    [Fact]
    public void TryGetValue_MissingKey_ReturnsFalse()
    {
        BoundedLruCache<string, int> cache = new(2);

        Assert.False(cache.TryGetValue("missing", out int value));
        Assert.Equal(default, value);
    }

    [Fact]
    public void SetAndGet_ReturnsValue()
    {
        BoundedLruCache<string, int> cache = new(2);

        cache.Set("a", 1);

        Assert.True(cache.TryGetValue("a", out int value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void Set_ExistingKey_UpdatesValueAndPromotes()
    {
        BoundedLruCache<string, int> cache = new(2);

        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("a", 10);
        cache.Set("c", 3);

        Assert.True(cache.TryGetValue("a", out int value));
        Assert.Equal(10, value);
        Assert.False(cache.TryGetValue("b", out _));
        Assert.True(cache.TryGetValue("c", out value));
        Assert.Equal(3, value);
    }

    [Fact]
    public void Capacity_Exceeded_EvictsLeastRecentlyUsed()
    {
        BoundedLruCache<string, int> cache = new(2);

        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("c", 3);

        Assert.False(cache.TryGetValue("a", out _));
        Assert.True(cache.TryGetValue("b", out int value));
        Assert.Equal(2, value);
        Assert.True(cache.TryGetValue("c", out value));
        Assert.Equal(3, value);
    }

    [Fact]
    public void Get_PromotesKeyToMostRecentlyUsed()
    {
        BoundedLruCache<string, int> cache = new(2);

        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGetValue("a", out _));
        cache.Set("c", 3);

        Assert.True(cache.TryGetValue("a", out int value));
        Assert.Equal(1, value);
        Assert.False(cache.TryGetValue("b", out _));
        Assert.True(cache.TryGetValue("c", out value));
        Assert.Equal(3, value);
    }

    [Fact]
    public void Concurrency_Stress_DoesNotThrowAndMaintainsCapacity()
    {
        BoundedLruCache<int, int> cache = new(10);

        Parallel.For(0, 1000, i =>
        {
            cache.Set(i % 50, i);

            if (i % 7 == 0)
            {
                cache.TryGetValue(i % 50, out _);
            }
        });

        int found = 0;

        for (int i = 0; i < 50; i++)
        {
            if (cache.TryGetValue(i, out _))
            {
                found++;
            }
        }

        Assert.Equal(10, found);
    }

    [Fact]
    public void GetOrAdd_MissingKey_CreatesAndReturnsValue()
    {
        BoundedLruCache<string, int> cache = new(2);

        int value = cache.GetOrAdd("a", _ => 1);

        Assert.Equal(1, value);

        Assert.True(cache.TryGetValue("a", out int stored));

        Assert.Equal(1, stored);
    }

    [Fact]
    public void GetOrAdd_ExistingKey_ReturnsExistingValueAndPromotes()
    {
        BoundedLruCache<string, int> cache = new(2);

        cache.Set("a", 1);
        cache.Set("b", 2);

        int value = cache.GetOrAdd("a", _ => 999);

        Assert.Equal(1, value);

        cache.Set("c", 3);

        Assert.True(cache.TryGetValue("a", out int a));

        Assert.Equal(1, a);

        Assert.False(cache.TryGetValue("b", out _));
    }

    [Fact]
    public void GetOrAdd_OverCapacity_EvictsLeastRecentlyUsed()
    {
        BoundedLruCache<string, int> cache = new(2);

        cache.GetOrAdd("a", _ => 1);
        cache.GetOrAdd("b", _ => 2);
        cache.GetOrAdd("c", _ => 3);

        Assert.False(cache.TryGetValue("a", out _));

        Assert.True(cache.TryGetValue("b", out int b));

        Assert.Equal(2, b);

        Assert.True(cache.TryGetValue("c", out int c));

        Assert.Equal(3, c);
    }

    [Fact]
    public void GetOrAdd_FactoryInvokedOnceForMissingKey()
    {
        BoundedLruCache<string, int> cache = new(4);

        int invocations = 0;

        int value = cache.GetOrAdd("k", _ =>
        {
            invocations++;

            return 42;
        });

        Assert.Equal(42, value);

        Assert.Equal(1, invocations);

        int second = cache.GetOrAdd("k", _ =>
        {
            invocations++;

            return 999;
        });

        Assert.Equal(42, second);

        Assert.Equal(1, invocations);
    }
}
