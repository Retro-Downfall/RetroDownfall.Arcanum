namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Process-local digest cache for API key validation. Invalidated when the on-disk key rotates.
/// </summary>
public interface IApiKeyDigestCache
{
    /// <summary>
    /// Returns a caller-owned defensive copy of the current authentication digest. The caller is
    /// responsible for zeroing the returned buffer.
    /// </summary>
    bool TryGetDigest(out byte[]? digest);

    /// <summary>
    /// Reads the authentication digest and the cache generation observed with it. A miss still
    /// returns its generation so an asynchronous secret-store read can publish only if no key
    /// rotation, invalidation, or newer fill occurred in the meantime. On success,
    /// <paramref name="digest"/> is a caller-owned defensive copy that the caller must zero.
    /// </summary>
    bool TryGetDigest(out byte[]? digest, out long generation);

    /// <summary>
    /// Returns the process-lifetime digest used only to prove that the process answering the local
    /// port is the installed Arcanum host. Implementations may retain this after the ordinary
    /// authentication TTL; explicit key invalidation must clear both views. On success, the result
    /// is a caller-owned defensive copy that the caller must zero.
    /// </summary>
    bool TryGetPresenceDigest(out byte[]? digest) =>
        TryGetDigest(out digest);

    /// <summary>
    /// Stores a private copy of <paramref name="digest"/>. Ownership of the supplied buffer always
    /// remains with the caller.
    /// </summary>
    void StoreDigest(byte[] digest, int ttlSeconds);

    /// <summary>
    /// Publishes a cache-miss result only when <paramref name="expectedGeneration"/> is still
    /// current. The cache takes a private copy only when publication succeeds; ownership of the
    /// supplied buffer remains with the caller for both outcomes.
    /// </summary>
    bool TryStoreDigest(
        byte[] digest,
        int ttlSeconds,
        long expectedGeneration);

    void Invalidate();
}
