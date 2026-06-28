using RetroDownfall.Arcanum.Infrastructure.Caching;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

internal static class SessionWriteLock
{

    private static readonly KeyedLock<Guid> _locks = new();

    public static Task<IDisposable> AcquireAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {

        return _locks.AcquireAsync(sessionId, cancellationToken);

    }

}
