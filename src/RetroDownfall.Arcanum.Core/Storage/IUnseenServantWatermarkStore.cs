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

    Task<IReadOnlyList<UnseenServantWatermark>> GetAllAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(string jobKey, CancellationToken cancellationToken = default);

}
