namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Process-local digest cache for API key validation. Invalidated when the on-disk key rotates.
/// </summary>
public interface IApiKeyDigestCache
{

    bool TryGetDigest(out byte[]? digest);

    void StoreDigest(byte[] digest, int ttlSeconds);

    void Invalidate();

}
