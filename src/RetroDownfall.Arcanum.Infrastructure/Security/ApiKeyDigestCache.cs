using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class ApiKeyDigestCache : IApiKeyDigestCache
{

    private byte[]? _cachedDigest;

    private long _cachedExpiresAtTicks;

    public bool TryGetDigest(out byte[]? digest)
    {

        long now = Environment.TickCount64;

        byte[]? cached = Volatile.Read(ref _cachedDigest);

        long expiresAt = Volatile.Read(ref _cachedExpiresAtTicks);

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

        long now = Environment.TickCount64;

        Volatile.Write(ref _cachedExpiresAtTicks, now + (ttlSeconds * 1000L));

        Volatile.Write(ref _cachedDigest, digest);

    }

    public void Invalidate()
    {

        Volatile.Write(ref _cachedDigest, null);

        Volatile.Write(ref _cachedExpiresAtTicks, 0);

    }

}
