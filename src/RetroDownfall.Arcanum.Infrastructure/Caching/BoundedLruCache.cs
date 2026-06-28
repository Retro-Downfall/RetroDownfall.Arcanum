using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Infrastructure.Caching;

/// <summary>
/// Fixed-capacity least-recently-used cache with O(1) lookup and exact eviction order.
/// </summary>
internal sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{

    private readonly int _capacity;

    private readonly ConcurrentDictionary<TKey, LinkedListNode<CacheEntry>> _nodes = new();

    private readonly LinkedList<CacheEntry> _order = new();

    private readonly Lock _lock = new();

    public BoundedLruCache(int capacity)
    {

        if (capacity <= 0)
        {

            throw new ArgumentOutOfRangeException(nameof(capacity));

        }

        _capacity = capacity;

    }

    public bool TryGetValue(TKey key, out TValue value)
    {

        if (!_nodes.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
        {

            value = default!;

            return false;

        }

        lock (_lock)
        {

            if (!_nodes.TryGetValue(key, out LinkedListNode<CacheEntry>? currentNode))
            {

                value = default!;

                return false;

            }

            _order.Remove(currentNode);

            _order.AddFirst(currentNode);

            value = currentNode.Value.Value;

            return true;

        }

    }

    public void Set(TKey key, TValue value)
    {

        lock (_lock)
        {

            if (_nodes.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
            {

                existing.Value = new CacheEntry(key, value);

                _order.Remove(existing);

                _order.AddFirst(existing);

                return;

            }

            LinkedListNode<CacheEntry> node = new(new CacheEntry(key, value));

            _order.AddFirst(node);

            _nodes[key] = node;

            if (_order.Count <= _capacity)
            {

                return;

            }

            LinkedListNode<CacheEntry>? tail = _order.Last;

            if (tail is null)
            {

                return;

            }

            _ = _nodes.TryRemove(tail.Value.Key, out _);

            _order.RemoveLast();

        }

    }

    /// <summary>
    /// Returns the existing value for <paramref name="key"/>, or invokes <paramref name="valueFactory"/>
    /// and inserts the result when the key is absent. The factory is invoked under the instance lock, so
    /// it must not reenter this cache. On a hit the entry is promoted to most-recently-used; on a miss
    /// that exceeds <see cref="_capacity"/> the least-recently-used entry is evicted.
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {

        lock (_lock)
        {

            if (_nodes.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
            {

                _order.Remove(existing);

                _order.AddFirst(existing);

                return existing.Value.Value;

            }

            TValue value = valueFactory(key);

            LinkedListNode<CacheEntry> node = new(new CacheEntry(key, value));

            _order.AddFirst(node);

            _nodes[key] = node;

            if (_order.Count <= _capacity)
            {

                return value;

            }

            LinkedListNode<CacheEntry>? tail = _order.Last;

            if (tail is null)
            {

                return value;

            }

            _ = _nodes.TryRemove(tail.Value.Key, out _);

            _order.RemoveLast();

            return value;

        }

    }

    private sealed class CacheEntry(TKey key, TValue value)
    {

        public TKey Key { get; } = key;

        public TValue Value { get; set; } = value;

    }

}
