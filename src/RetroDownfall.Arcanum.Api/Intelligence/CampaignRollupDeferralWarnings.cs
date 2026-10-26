namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Shares content-free warning suppression across scoped foreground and background maintenance.
/// Uses monotonic elapsed time and retains at most 1,024 Campaign ids; the least recently warned
/// Campaign is evicted at capacity, so suppression is best effort beyond that working set.
/// </summary>
public sealed class CampaignRollupDeferralWarnings(TimeProvider timeProvider)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();

    private readonly Dictionary<Guid, (long Timestamp, LinkedListNode<Guid> Order)> _warnings = [];

    private readonly LinkedList<Guid> _order = new();

    public bool TryAcquire(Guid campaignId)
    {
        lock (_gate)
        {
            long now = timeProvider.GetTimestamp();

            if (_warnings.TryGetValue(campaignId, out (long Timestamp, LinkedListNode<Guid> Order) existing))
            {
                if (timeProvider.GetElapsedTime(existing.Timestamp, now) < Interval)
                {
                    return false;
                }

                _order.Remove(existing.Order);
            }
            else if (_warnings.Count == 1024)
            {
                Guid oldest = _order.First!.Value;

                _order.RemoveFirst();

                _warnings.Remove(oldest);
            }

            LinkedListNode<Guid> order = _order.AddLast(campaignId);

            _warnings[campaignId] = (now, order);

            return true;
        }
    }
}
