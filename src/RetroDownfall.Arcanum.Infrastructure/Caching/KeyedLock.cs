using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Infrastructure.Caching;

/// <summary>
/// Self-evicting per-key <see cref="SemaphoreSlim(1, 1)"/> map. Acquire returns a releaser that
/// releases the semaphore on dispose. Entries are removed only when the per-key waiter/holder
/// refcount reaches zero, using reference-equality <see cref="ConcurrentDictionary{TKey, TValue}.TryRemove"/>
/// so a cancelled waiter cannot evict a semaphore another waiter is using or about to use.
/// </summary>
internal sealed class KeyedLock<TKey> where TKey : notnull
{

    private readonly ConcurrentDictionary<TKey, LockState> _map;

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

        while (true)
        {

            LockState state = _map.GetOrAdd(key, static _ => new LockState());

            lock (state.Sync)
            {

                // A state removed between GetOrAdd and this lock must not be reused.
                if (!_map.TryGetValue(key, out LockState? current) || !ReferenceEquals(current, state))
                {

                    continue;

                }

                state.RefCount++;

            }

            try
            {

                await state.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                return new Releaser(_map, key, state);

            }
            catch (OperationCanceledException)
            {

                ReleaseRef(_map, key, state);

                throw;

            }

        }

    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="key"/> currently has an entry whose semaphore is held
    /// (has a zero current count). Idle entries are auto-evicted, so a <c>false</c> result means the
    /// key is not actively locked. Used by sweepers that must skip in-flight work.
    /// </summary>
    internal bool IsHeld(TKey key)
    {

        return _map.TryGetValue(key, out LockState? state) && state.Semaphore.CurrentCount == 0;

    }

    /// <summary>Current entry count of the underlying map; for assertions in the test suite.</summary>
    internal int CountForTesting => _map.Count;

    private static void ReleaseRef(ConcurrentDictionary<TKey, LockState> map, TKey key, LockState state)
    {

        lock (state.Sync)
        {

            state.RefCount--;

            if (state.RefCount <= 0)
            {

                _ = map.TryRemove(new KeyValuePair<TKey, LockState>(key, state));

            }

        }

    }

    private sealed class LockState
    {

        public object Sync { get; } = new();

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int RefCount;

    }

    private sealed class Releaser : IDisposable
    {

        private readonly ConcurrentDictionary<TKey, LockState> _map;

        private readonly TKey _key;

        private readonly LockState _state;

        private int _disposed;

        public Releaser(ConcurrentDictionary<TKey, LockState> map, TKey key, LockState state)
        {

            _map = map;

            _key = key;

            _state = state;

        }

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {

                return;

            }

            _state.Semaphore.Release();

            ReleaseRef(_map, _key, _state);

        }

    }

}
