namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Grimoire-backed persistence for Unseen Servant scheduler watermarks (last-run timestamp and
/// dynamic interval override, per job). Write-through: callers persist on every state change,
/// no batching or periodic snapshots.
/// </summary>
public interface IUnseenServantWatermarkStore
{

    Task<UnseenServantWatermark?> GetAsync(string jobKey, CancellationToken cancellationToken = default);

    Task SaveAsync(string jobKey, DateTimeOffset lastRunAt, int effectiveIntervalMinutes, CancellationToken cancellationToken = default);

    /// <summary>Updates only the completion timestamp; the interval is used only when inserting a new row.</summary>
    Task SaveLastRunAsync(string jobKey, DateTimeOffset lastRunAt, int initialIntervalMinutes, CancellationToken cancellationToken = default);

    /// <summary>Updates only the interval; the timestamp is used only when inserting a new row.</summary>
    Task SaveIntervalAsync(string jobKey, DateTimeOffset initialLastRunAt, int effectiveIntervalMinutes, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UnseenServantWatermark>> GetAllAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(string jobKey, CancellationToken cancellationToken = default);

}
