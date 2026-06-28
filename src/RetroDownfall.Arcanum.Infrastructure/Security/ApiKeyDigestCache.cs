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

            digest = entry.Digest;

            return true;

        }

        digest = null;

        return false;

    }

    public void StoreDigest(byte[] digest, int ttlSeconds)
    {

        long now = _timeProvider.GetUtcNow().Ticks / TimeSpan.TicksPerMillisecond;

        DigestEntry snapshot = new(digest, now + (ttlSeconds * 1000L));

        Volatile.Write(ref _entry, snapshot);

    }

    public void Invalidate()
    {

        Volatile.Write(ref _entry, null);

    }

}
