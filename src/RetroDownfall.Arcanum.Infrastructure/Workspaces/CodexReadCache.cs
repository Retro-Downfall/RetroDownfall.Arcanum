using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

/// <summary>
/// A cached body is only valid for the exact read that produced it: the same path <b>and</b> the same
/// size limit, so lowering the limit can never be answered from a body that the lower limit rejects.
/// </summary>
internal readonly record struct CodexCacheKey(string Path, long MaxSizeBytes);

/// <summary>
/// What the file looked like when it was read. Identity and length join the modification time, so a
/// replacement that restores the old mtime (or a different object swapped in under the same name)
/// still misses.
/// </summary>
internal readonly record struct CodexFileStamp(FileHandleIdentity Identity, long Length, long MtimeUtcTicks);

/// <summary>
/// The bounded, stamp-validated, least-recently-used cache behind <see cref="CodexReader"/>. It holds
/// at most <c>capacity</c> bodies; a lookup that finds a different stamp drops the entry, and a store
/// beyond capacity evicts the entry that was used longest ago. It is a separate type so its eviction
/// order and validation can be pinned with a small capacity and synthetic stamps instead of through the
/// process-wide instance the readers share.
/// </summary>
internal sealed class CodexReadCache
{
    private readonly int _capacity;

    private readonly Lock _gate = new();

    private readonly Dictionary<CodexCacheKey, LinkedListNode<(CodexCacheKey Key, Entry Entry)>> _index = [];

    // Most recently used first.
    private readonly LinkedList<(CodexCacheKey Key, Entry Entry)> _order = new();

    internal CodexReadCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _capacity = capacity;
    }

    private sealed record Entry(CodexFileStamp Stamp, string Content);

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>Whether <paramref name="key"/> is cached, without counting as a use.</summary>
    internal bool Contains(CodexCacheKey key)
    {
        lock (_gate)
        {
            return _index.ContainsKey(key);
        }
    }

    /// <summary>
    /// The cached body when its stamp is still <paramref name="stamp"/>, which also makes it the most
    /// recently used; <see langword="null"/> otherwise, after dropping an entry whose stamp moved.
    /// </summary>
    internal string? TryGet(CodexCacheKey key, CodexFileStamp stamp)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(key, out LinkedListNode<(CodexCacheKey Key, Entry Entry)>? node))
            {
                return null;
            }

            if (node.Value.Entry.Stamp != stamp)
            {
                _order.Remove(node);

                _index.Remove(key);

                return null;
            }

            _order.Remove(node);

            _order.AddFirst(node);

            return node.Value.Entry.Content;
        }
    }

    internal void Store(CodexCacheKey key, CodexFileStamp stamp, string content)
    {
        lock (_gate)
        {
            if (_index.Remove(key, out LinkedListNode<(CodexCacheKey Key, Entry Entry)>? existing))
            {
                _order.Remove(existing);
            }

            _index[key] = _order.AddFirst((key, new Entry(stamp, content)));

            while (_index.Count > _capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();

                _index.Remove(oldest.Value.Key);
            }
        }
    }
}
