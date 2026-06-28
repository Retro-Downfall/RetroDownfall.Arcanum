using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Infrastructure.Caching;

/// <summary>
/// Self-evicting per-key <see cref="SemaphoreSlim(1, 1)"/> map. Acquire returns a releaser that
/// releases the semaphore on dispose and, when the semaphore is idle (no waiters) and the map still
/// holds this exact semaphore, removes the entry so the map stays bounded to keys with active or
/// in-flight waiters. The reference-equality <see cref="ConcurrentDictionary{TKey, TValue}.TryRemove"/>
/// guard (mirroring <see cref="SingleFlight"/>) prevents evicting a newer semaphore a concurrent
/// acquirer added after this release.
/// </summary>
internal sealed class KeyedLock<TKey> where TKey : notnull
{

    private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _map;

    public KeyedLock(IEqualityComparer<TKey>? comparer = null)
    {

        _map = new(comparer ?? EqualityComparer<TKey>.Default);

    }

    /// <summary>
    /// Acquires a per-key <see cref="SemaphoreSlim(1, 1)"/>, awaiting until the lock is held, and
    /// returns a releaser that releases and best-effort evicts the entry on dispose.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(TKey key, CancellationToken cancellationToken = default)
    {

        SemaphoreSlim semaphore = _map.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new Releaser(_map, key, semaphore);

    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="key"/> currently has an entry whose semaphore is held
    /// (has a zero current count). Idle entries are auto-evicted, so a <c>false</c> result means the
    /// key is not actively locked. Used by sweepers that must skip in-flight work.
    /// </summary>
    internal bool IsHeld(TKey key)
    {

        return _map.TryGetValue(key, out SemaphoreSlim? semaphore) && semaphore.CurrentCount == 0;

    }

    /// <summary>Current entry count of the underlying map; for assertions in the test suite.</summary>
    internal int CountForTesting => _map.Count;

    private sealed class Releaser : IDisposable
    {

        private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _map;

        private readonly TKey _key;

        private readonly SemaphoreSlim _semaphore;

        private int _disposed;

        public Releaser(ConcurrentDictionary<TKey, SemaphoreSlim> map, TKey key, SemaphoreSlim semaphore)
        {

            _map = map;

            _key = key;

            _semaphore = semaphore;

        }

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {

                return;

            }

            _semaphore.Release();

            if (_semaphore.CurrentCount == 1)
            {

                _ = _map.TryRemove(new KeyValuePair<TKey, SemaphoreSlim>(_key, _semaphore));

            }

        }

    }

}
