using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Infrastructure.Caching;

/// <summary>
/// Per-key single-flight coalescing for async miss paths. Concurrent misses for the same key
/// share one in-flight <see cref="Task{T}"/>; the in-flight entry is removed after completion
/// (only if it is still this exact entry) so the map cannot grow unbounded across distinct keys.
/// </summary>
internal static class SingleFlight
{

    /// <summary>
    /// Returns a task that shares the in-flight factory result for <paramref name="key"/> with
    /// other concurrent callers, running <paramref name="factory"/> at most once per miss wave.
    /// After the shared task completes the entry is removed from <paramref name="inFlight"/>
    /// unless another miss wave has already replaced it with a newer entry.
    /// </summary>
    internal static Task<TResult> CoalesceAsync<TKey, TResult>(
        ConcurrentDictionary<TKey, Lazy<Task<TResult>>> inFlight,
        TKey key,
        Func<Task<TResult>> factory) where TKey : notnull
    {

        Lazy<Task<TResult>> lazy = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);

        Lazy<Task<TResult>> entry = inFlight.GetOrAdd(key, lazy);

        return AwaitAndCleanupAsync(entry, inFlight, key);

    }

    private static async Task<TResult> AwaitAndCleanupAsync<TKey, TResult>(
        Lazy<Task<TResult>> entry,
        ConcurrentDictionary<TKey, Lazy<Task<TResult>>> inFlight,
        TKey key) where TKey : notnull
    {

        try
        {

            return await entry.Value.ConfigureAwait(false);

        }
        finally
        {

            // Remove only if the map still holds this exact entry; a newer entry another thread
            // added (after this miss wave completed and the entry was cleared) must not be evicted.
            _ = inFlight.TryRemove(new KeyValuePair<TKey, Lazy<Task<TResult>>>(key, entry));

        }

    }

}
