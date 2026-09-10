using System.Reflection;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class ApiKeyDigestCacheTests
{
    [Fact]
    public void Authentication_ttl_ignores_a_forward_wall_clock_jump()
    {
        DualClockTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] digest = [1, 2, 3, 4];

        cache.StoreDigest(digest, ttlSeconds: 60);

        timeProvider.JumpUtc(TimeSpan.FromDays(365));

        Assert.True(cache.TryGetDigest(out byte[]? cached));
        Assert.Equal(digest, cached);
    }

    [Fact]
    public void Authentication_ttl_expires_from_elapsed_time_despite_a_backward_wall_clock_jump()
    {
        DualClockTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        cache.StoreDigest([1, 2, 3, 4], ttlSeconds: 60);

        timeProvider.JumpUtc(TimeSpan.FromDays(-365));
        timeProvider.AdvanceMonotonic(TimeSpan.FromSeconds(61));

        Assert.False(cache.TryGetDigest(out byte[]? cached));
        Assert.Null(cached);
    }

    [Fact]
    public void Presence_digest_survives_authentication_ttl_but_not_invalidation()
    {
        FakeTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] digest = [1, 2, 3, 4];

        cache.StoreDigest(digest, ttlSeconds: 1);

        timeProvider.Advance(TimeSpan.FromSeconds(2));

        Assert.False(cache.TryGetDigest(out _));

        Assert.True(cache.TryGetPresenceDigest(out byte[]? presence));
        Assert.Equal(digest, presence);
        Assert.NotSame(digest, presence);

        Array.Clear(presence!);

        Assert.True(cache.TryGetPresenceDigest(out byte[]? unchanged));
        Assert.Equal(digest, unchanged);

        cache.Invalidate();

        Assert.False(cache.TryGetPresenceDigest(out byte[]? invalidated));
        Assert.Null(invalidated);
    }

    [Fact]
    public void Constructor_WithoutTimeProvider_UsesSystemClock()
    {
        ApiKeyDigestCache cache = new();
        byte[] digest = [1, 2, 3, 4];

        cache.StoreDigest(digest, ttlSeconds: 60);

        bool found = cache.TryGetDigest(out byte[]? result);

        Assert.True(found);

        // TryGetDigest hands out a defensive copy, never the live cached array — a caller
        // that zeroes or otherwise writes into what it gets back must not corrupt the digest every
        // subsequent authentication compares against.
        Assert.NotSame(digest, result);

        Assert.Equal(digest, result);
    }

    [Fact]
    public void TryGetDigest_StoredAndNotExpired_ReturnsDigest()
    {
        FakeTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] digest = [1, 2, 3, 4];

        cache.StoreDigest(digest, ttlSeconds: 60);

        bool found = cache.TryGetDigest(out byte[]? result);

        Assert.True(found);

        Assert.NotSame(digest, result);

        Assert.Equal(digest, result);
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

        Assert.NotSame(second, result);

        Assert.Equal(second, result);
    }

    [Fact]
    public void StoreDigest_NewDigest_ZeroesTheRetiredCacheOwnedBuffer()
    {
        FakeTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] first = [1, 2, 3, 4];

        byte[] second = [5, 6, 7, 8];

        cache.StoreDigest(first, ttlSeconds: 60);

        byte[] retiredBuffer = GetCacheOwnedDigestBuffer(cache);

        Assert.Equal(first, retiredBuffer);
        Assert.NotSame(first, retiredBuffer);

        cache.StoreDigest(second, ttlSeconds: 60);

        Assert.All(retiredBuffer, value => Assert.Equal((byte)0, value));

        Assert.True(cache.TryGetDigest(out byte[]? current));
        Assert.Equal(second, current);
    }

    [Fact]
    public void Invalidate_ZeroesTheRetiredCacheOwnedBuffer()
    {
        FakeTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] digest = [11, 12, 13, 14];

        cache.StoreDigest(digest, ttlSeconds: 60);

        byte[] retiredBuffer = GetCacheOwnedDigestBuffer(cache);

        cache.Invalidate();

        Assert.All(retiredBuffer, value => Assert.Equal((byte)0, value));
        Assert.False(cache.TryGetDigest(out _));
        Assert.False(cache.TryGetPresenceDigest(out _));
    }

    [Fact]
    public async Task StoreDigest_WhileReaderOwnsTheOldSnapshot_DefersZeroingUntilTheCopyFinishes()
    {
        using BlockingTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] first = [21, 22, 23, 24];

        byte[] second = [31, 32, 33, 34];

        cache.StoreDigest(first, ttlSeconds: 60);

        byte[] retiredBuffer = GetCacheOwnedDigestBuffer(cache);

        timeProvider.BlockNextRead();

        Task<(bool Found, byte[]? Digest)> reader = Task.Factory.StartNew(
            () =>
            {
                bool found = cache.TryGetDigest(out byte[]? digest);

                return (found, digest);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.True(timeProvider.WaitUntilReadIsBlocked(TimeSpan.FromSeconds(10)));

            cache.StoreDigest(second, ttlSeconds: 60);

            Assert.Equal(first, retiredBuffer);
            Assert.False(reader.IsCompleted);
        }
        finally
        {
            timeProvider.ReleaseRead();
        }

        (bool found, byte[]? copiedDigest) = await reader;

        Assert.True(found);
        Assert.Equal(first, copiedDigest);
        Assert.All(retiredBuffer, value => Assert.Equal((byte)0, value));

        Assert.True(cache.TryGetDigest(out byte[]? current));
        Assert.Equal(second, current);
    }

    [Fact]
    public async Task Invalidate_WhileReaderOwnsTheSnapshot_DefersZeroingUntilTheCopyFinishes()
    {
        using BlockingTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] digest = [41, 42, 43, 44];

        cache.StoreDigest(digest, ttlSeconds: 60);

        byte[] retiredBuffer = GetCacheOwnedDigestBuffer(cache);

        timeProvider.BlockNextRead();

        Task<(bool Found, byte[]? Digest)> reader = Task.Factory.StartNew(
            () =>
            {
                bool found = cache.TryGetDigest(out byte[]? copiedDigest);

                return (found, copiedDigest);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.True(timeProvider.WaitUntilReadIsBlocked(TimeSpan.FromSeconds(10)));

            cache.Invalidate();

            Assert.Equal(digest, retiredBuffer);
            Assert.False(reader.IsCompleted);
        }
        finally
        {
            timeProvider.ReleaseRead();
        }

        (bool found, byte[]? copiedDigest) = await reader;

        Assert.True(found);
        Assert.Equal(digest, copiedDigest);
        Assert.All(retiredBuffer, value => Assert.Equal((byte)0, value));
        Assert.False(cache.TryGetDigest(out _));
        Assert.False(cache.TryGetPresenceDigest(out _));
    }

    [Fact]
    public async Task TryStoreDigest_RechecksGenerationAfterPreparingTheSnapshot()
    {
        using BlockingTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        Assert.False(cache.TryGetDigest(out _, out long observedGeneration));

        byte[] staleDigest = [51, 52, 53, 54];

        byte[] rotatedDigest = [61, 62, 63, 64];

        timeProvider.BlockNextRead();

        Task<bool> stalePublication = Task.Factory.StartNew(
            () => cache.TryStoreDigest(
                staleDigest,
                ttlSeconds: 60,
                expectedGeneration: observedGeneration),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.True(timeProvider.WaitUntilReadIsBlocked(TimeSpan.FromSeconds(10)));

            cache.StoreDigest(rotatedDigest, ttlSeconds: 60);
        }
        finally
        {
            timeProvider.ReleaseRead();
        }

        Assert.False(await stalePublication);

        Assert.True(cache.TryGetDigest(out byte[]? current));
        Assert.Equal(rotatedDigest, current);

        Assert.True(cache.TryGetPresenceDigest(out byte[]? presence));
        Assert.Equal(rotatedDigest, presence);
    }

    // TryGetDigest hands out the live shared byte[] holding the expected digest, so any
    // consumer that writes into what it gets back — the natural instinct in this codebase, where
    // every other secret buffer is zeroed in a finally — silently rewrites the digest every
    // subsequent authentication compares against. StoreDigest aliases the caller's array the same
    // way, so a caller that keeps its own reference after storing can corrupt the cache too. Both
    // directions need their own copy.

    [Fact]
    public void TryGetDigest_MutatingTheReturnedArray_DoesNotChangeTheCachedDigest()
    {
        FakeTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] original = [10, 20, 30, 40];

        byte[] stored = (byte[])original.Clone();

        cache.StoreDigest(stored, ttlSeconds: 60);

        bool foundFirst = cache.TryGetDigest(out byte[]? first);

        Assert.True(foundFirst);

        Assert.NotNull(first);

        Array.Clear(first);

        bool foundSecond = cache.TryGetDigest(out byte[]? second);

        Assert.True(foundSecond);

        Assert.Equal(original, second);
    }

    [Fact]
    public void StoreDigest_MutatingTheCallerArrayAfterStoring_DoesNotChangeTheCachedDigest()
    {
        FakeTimeProvider timeProvider = new();

        ApiKeyDigestCache cache = new(timeProvider);

        byte[] original = [50, 60, 70, 80];

        byte[] callerOwned = (byte[])original.Clone();

        cache.StoreDigest(callerOwned, ttlSeconds: 60);

        Array.Clear(callerOwned);

        bool found = cache.TryGetDigest(out byte[]? result);

        Assert.True(found);

        Assert.Equal(original, result);
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

    private static byte[] GetCacheOwnedDigestBuffer(ApiKeyDigestCache cache)
    {
        FieldInfo? entryField = typeof(ApiKeyDigestCache).GetField(
            "_entry",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(entryField);

        object? entry = entryField.GetValue(cache);

        Assert.NotNull(entry);

        FieldInfo? digestField = entry.GetType().GetField(
            "_digest",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(digestField);

        byte[]? digest = digestField.GetValue(entry) as byte[];

        Assert.NotNull(digest);

        return digest;
    }

    private sealed class BlockingTimeProvider : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _readEntered = new(initialState: false);

        private readonly ManualResetEventSlim _readRelease = new(initialState: false);

        private readonly ManualResetEventSlim _readExited = new(initialState: false);

        private int _blockNextRead;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            if (Interlocked.Exchange(ref _blockNextRead, 0) != 0)
            {
                _readEntered.Set();

                try
                {
                    _readRelease.Wait();
                }
                finally
                {
                    _readExited.Set();
                }
            }

            return 0L;
        }

        public void BlockNextRead()
        {
            _readEntered.Reset();
            _readRelease.Reset();
            _readExited.Reset();

            Volatile.Write(ref _blockNextRead, 1);
        }

        public bool WaitUntilReadIsBlocked(TimeSpan timeout) =>
            _readEntered.Wait(timeout);

        public void ReleaseRead() =>
            _readRelease.Set();

        public void Dispose()
        {
            _readRelease.Set();

            bool readWasArmedOrExited = Interlocked.Exchange(ref _blockNextRead, 0) != 0 ||
                _readExited.Wait(TimeSpan.FromSeconds(10));

            if (!readWasArmedOrExited)
            {
                return;
            }

            _readEntered.Dispose();
            _readRelease.Dispose();
            _readExited.Dispose();
        }
    }

    private sealed class DualClockTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private long _timestamp = 987_654_321L;

        public override long TimestampFrequency => 1_000L;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void JumpUtc(TimeSpan delta) =>
            _utcNow = _utcNow.Add(delta);

        public void AdvanceMonotonic(TimeSpan delta) =>
            _timestamp = checked(
                _timestamp +
                (delta.Ticks * TimestampFrequency / TimeSpan.TicksPerSecond));
    }
}
