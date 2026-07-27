using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed class ExpiringRequestBindingStore<T>
{
    private readonly ConcurrentDictionary<string, Binding> _byRequest =
        new(StringComparer.Ordinal);

    private readonly Func<long> _getTimestamp;
    private readonly long _ttl;
    private readonly long _sweepInterval;
    private long _nextSweepTimestamp;

    internal ExpiringRequestBindingStore(
        long ttl,
        long sweepInterval,
        Func<long> getTimestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sweepInterval);
        ArgumentNullException.ThrowIfNull(getTimestamp);

        _ttl = ttl;
        _sweepInterval = sweepInterval;
        _getTimestamp = getTimestamp;
        _nextSweepTimestamp = getTimestamp() + sweepInterval;
    }

    internal int CountForTests => _byRequest.Count;

    internal void Bind(
        string connectionKey,
        string requestId,
        T value)
    {
        SweepExpiredIfDue();
        _byRequest[ComposeKey(connectionKey, requestId)] =
            new Binding(value, _getTimestamp());
    }

    internal bool TryResolve(
        string connectionKey,
        string requestId,
        out T value)
    {
        SweepExpiredIfDue();

        string key = ComposeKey(connectionKey, requestId);
        if (!_byRequest.TryGetValue(key, out Binding binding))
        {
            value = default!;
            return false;
        }

        if (IsExpired(binding, _getTimestamp()))
        {
            _byRequest.TryRemove(key, out _);
            value = default!;
            return false;
        }

        value = binding.Value;
        return true;
    }

    internal void Unbind(
        string connectionKey,
        string requestId) =>
        _byRequest.TryRemove(
            ComposeKey(connectionKey, requestId),
            out _);

    private void SweepExpiredIfDue()
    {
        long now = _getTimestamp();
        long next = Volatile.Read(ref _nextSweepTimestamp);

        if (now < next
            || Interlocked.CompareExchange(
                ref _nextSweepTimestamp,
                now + _sweepInterval,
                next) != next)
        {
            return;
        }

        foreach (KeyValuePair<string, Binding> pair in _byRequest)
        {
            if (IsExpired(pair.Value, now))
            {
                _byRequest.TryRemove(pair.Key, out _);
            }
        }
    }

    private bool IsExpired(Binding binding, long now) =>
        now - binding.CreatedTimestamp > _ttl;

    private static string ComposeKey(
        string connectionKey,
        string requestId) =>
        connectionKey + "\u001f" + requestId;

    private readonly record struct Binding(
        T Value,
        long CreatedTimestamp);
}
