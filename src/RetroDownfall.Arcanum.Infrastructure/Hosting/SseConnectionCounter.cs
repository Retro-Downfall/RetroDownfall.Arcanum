using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Thread-safe per-event-type SSE connection counter shared across all SSE routes via
/// <see cref="SseConnectionGate"/>. Backs the per-type connection cap so a single greedy
/// stream family (for example log watchers) cannot starve the shared global SSE pool.
/// </summary>
/// <remarks>
/// Public (rather than internal, per the original design note) because it is a constructor
/// dependency of the public <see cref="SseConnectionGate"/>, which is resolved via DI from the
/// Api assembly; a public constructor cannot accept a less-accessible parameter type (CS0051).
/// </remarks>
public sealed class SseConnectionCounter
{

    private readonly ConcurrentDictionary<string, int> _counts = new();

    public int Increment(string eventType)
    {

        return _counts.AddOrUpdate(eventType, 1, static (_, count) => count + 1);

    }

    public void Decrement(string eventType)
    {

        _counts.AddOrUpdate(eventType, 0, static (_, count) => Math.Max(0, count - 1));

    }

    public int GetCount(string eventType) =>
        _counts.TryGetValue(eventType, out int count) ? count : 0;

}
