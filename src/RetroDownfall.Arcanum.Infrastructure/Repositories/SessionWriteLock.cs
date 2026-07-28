using RetroDownfall.Arcanum.Infrastructure.Caching;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

internal static class SessionWriteLock
{

    private static readonly KeyedLock<Guid> _locks = new();

    internal static Action<Guid>? BeforeAcquireForTesting { get; set; }

    public static Task<IDisposable> AcquireAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {

        BeforeAcquireForTesting?.Invoke(sessionId);

        return _locks.AcquireAsync(sessionId, cancellationToken);

    }

    internal static bool IsHeldForTesting(Guid sessionId) =>
        _locks.IsHeld(sessionId);

}
