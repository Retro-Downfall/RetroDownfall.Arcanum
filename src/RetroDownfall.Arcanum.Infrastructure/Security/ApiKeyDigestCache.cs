using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class ApiKeyDigestCache : IApiKeyDigestCache
{

    // W3.3 Fix 5: the (digest, expiry) pair is published as a single immutable
    // snapshot via one Volatile.Write of this reference. The previous design used
    // two separate Volatile.Writes (expiry then digest), which let a concurrent
    // TryGetDigest observe the NEW expiry paired with the OLD digest and serve a
    // stale digest as valid. A record reference write is atomic on aligned
    // references, so readers see either the full old snapshot or the full new one.
    private sealed record DigestEntry(byte[] Digest, long ExpiresAtMilliseconds);

    private DigestEntry? _entry;

    private readonly TimeProvider _timeProvider;

    public ApiKeyDigestCache(TimeProvider? timeProvider = null)
    {

        _timeProvider = timeProvider ?? TimeProvider.System;

    }

    public bool TryGetDigest(out byte[]? digest)
    {

        long now = _timeProvider.GetUtcNow().Ticks / TimeSpan.TicksPerMillisecond;

        DigestEntry? entry = Volatile.Read(ref _entry);

        if (entry is not null && now < entry.ExpiresAtMilliseconds)
        {

            // W7-10: a defensive copy. entry.Digest is the live array every FixedTimeEquals
            // compares the presented API key against for the rest of the TTL window, and a
            // caller that zeroes or otherwise writes into what it gets back — the natural
            // instinct in this codebase, where every other secret buffer is zeroed in a finally —
            // must never be able to reach that array.
            digest = entry.Digest.ToArray();

            return true;

        }

        digest = null;

        return false;

    }

    public void StoreDigest(byte[] digest, int ttlSeconds)
    {

        long now = _timeProvider.GetUtcNow().Ticks / TimeSpan.TicksPerMillisecond;

        // W7-10: owns a copy rather than the caller's array. ApiKeyAuthenticator hands this the
        // same array it then returns to its own caller on the cache-miss path, so aliasing the
        // caller's array here would let a write into that returned value corrupt the cached
        // digest too.
        DigestEntry snapshot = new(digest.ToArray(), now + (ttlSeconds * 1000L));

        Volatile.Write(ref _entry, snapshot);

    }

    public void Invalidate()
    {

        Volatile.Write(ref _entry, null);

    }

}
