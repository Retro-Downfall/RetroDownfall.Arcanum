using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

internal static class SessionWriteLock
{

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public static async Task<IDisposable> AcquireAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {

        SemaphoreSlim semaphore = _locks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new LockRelease(semaphore);

    }

    private sealed class LockRelease(SemaphoreSlim semaphore) : IDisposable
    {

        public void Dispose()
        {

            semaphore.Release();

        }

    }

}
