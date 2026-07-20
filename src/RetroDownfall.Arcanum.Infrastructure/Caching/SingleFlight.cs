using System.Collections.Concurrent;
using CoreSingleFlight = RetroDownfall.Arcanum.Core.Caching.SingleFlight;

namespace RetroDownfall.Arcanum.Infrastructure.Caching;

/// <summary>
/// Thin forwarder to <see cref="CoreSingleFlight"/> so existing Infrastructure call sites keep
/// compiling against this namespace. Prefer <see cref="CoreSingleFlight"/> for new code.
/// </summary>
internal static class SingleFlight
{

    internal static Task<TResult> CoalesceAsync<TKey, TResult>(
        ConcurrentDictionary<TKey, Lazy<Task<TResult>>> inFlight,
        TKey key,
        Func<Task<TResult>> factory) where TKey : notnull =>
        CoreSingleFlight.CoalesceAsync(inFlight, key, factory);

}
