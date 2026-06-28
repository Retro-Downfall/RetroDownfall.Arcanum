using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Caching;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class SanctumBreachStore
{

    private const int MaxBreachesPerCampaign = 1000;

    /// <summary>
    /// Upper bound on the number of distinct campaign keys retained in memory. Each buffer is ring-capped
    /// by <see cref="MaxBreachesPerCampaign"/>; this caps the key set so the store cannot grow unbounded
    /// over long uptime. The store is in-memory and lost on host restart (see DESIGN), so LRU-evicting the
    /// least-recently-touched campaign under cap pressure is safe and matches the audit recommendation.
    /// </summary>
    internal const int MaxTrackedCampaigns = 256;

    private readonly BoundedLruCache<string, SanctumBreachRingBuffer> _buffers = new(MaxTrackedCampaigns);

    public void Record(SanctumBreach breach)
    {

        SanctumBreachRingBuffer buffer = _buffers.GetOrAdd(
            breach.CampaignId,
            static _ => new SanctumBreachRingBuffer(MaxBreachesPerCampaign));

        buffer.Add(breach);

    }

    public IReadOnlyList<SanctumBreach> GetSnapshot(string campaignId, int limit)
    {

        if (!_buffers.TryGetValue(campaignId, out SanctumBreachRingBuffer? buffer))
        {

            return Array.Empty<SanctumBreach>();

        }

        return buffer.GetSnapshot(limit);

    }

    private sealed class SanctumBreachRingBuffer
    {

        private readonly Lock _lock = new();

        private readonly SanctumBreach[] _entries;

        private int _count;

        private int _head;

        public SanctumBreachRingBuffer(int capacity)
        {
            _entries = new SanctumBreach[capacity];
        }

        public void Add(SanctumBreach breach)
        {
            lock (_lock)
            {
                int capacity = _entries.Length;

                if (_count < capacity)
                {
                    int index = (_head + _count) % capacity;

                    _entries[index] = breach;

                    _count++;
                }
                else
                {
                    _entries[_head] = breach;

                    _head = (_head + 1) % capacity;
                }
            }
        }

        public IReadOnlyList<SanctumBreach> GetSnapshot(int limit)
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    return Array.Empty<SanctumBreach>();
                }

                SanctumBreach[] chronological = new SanctumBreach[_count];

                for (int i = 0; i < _count; i++)
                {
                    int index = (_head + i) % _entries.Length;

                    chronological[i] = _entries[index];
                }

                int take = Math.Min(limit, _count);

                if (take == _count)
                {
                    return chronological;
                }

                SanctumBreach[] recent = new SanctumBreach[take];

                Array.Copy(chronological, _count - take, recent, 0, take);

                return recent;
            }
        }

    }

}
