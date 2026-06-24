using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class ApiKeyDigestCache : IApiKeyDigestCache
{

    private byte[]? _cachedDigest;

    private long _cachedExpiresAtMilliseconds;

    private readonly TimeProvider _timeProvider;

    public ApiKeyDigestCache(TimeProvider? timeProvider = null)
    {

        _timeProvider = timeProvider ?? TimeProvider.System;

    }

    public bool TryGetDigest(out byte[]? digest)
    {

        long now = _timeProvider.GetUtcNow().Ticks / TimeSpan.TicksPerMillisecond;

        byte[]? cached = Volatile.Read(ref _cachedDigest);

        long expiresAt = Volatile.Read(ref _cachedExpiresAtMilliseconds);

        if (cached is not null && now < expiresAt)
        {

            digest = cached;

            return true;

        }

        digest = null;

        return false;

    }

    public void StoreDigest(byte[] digest, int ttlSeconds)
    {

        long now = _timeProvider.GetUtcNow().Ticks / TimeSpan.TicksPerMillisecond;

        Volatile.Write(ref _cachedExpiresAtMilliseconds, now + (ttlSeconds * 1000L));

        Volatile.Write(ref _cachedDigest, digest);

    }

    public void Invalidate()
    {

        Volatile.Write(ref _cachedDigest, null);

        Volatile.Write(ref _cachedExpiresAtMilliseconds, 0);

    }

}
