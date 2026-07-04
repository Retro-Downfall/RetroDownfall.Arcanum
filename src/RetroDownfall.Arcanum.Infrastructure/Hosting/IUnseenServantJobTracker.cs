using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Process-local runtime state for configured Unseen Servant jobs (last run, next due, last result).
/// </summary>
public interface IUnseenServantJobTracker
{

    void RecordCompletion(UnseenServantJob job, bool success, string? resultSummary);

    DateTimeOffset? GetLastRunAt(UnseenServantJob job);

    DateTimeOffset? GetNextDueAt(UnseenServantJob job, int effectiveIntervalMinutes);

    string? GetLastResult(UnseenServantJob job);

    /// <summary>
    /// Seeds in-memory last-run state from persisted <see cref="UnseenServantWatermark"/> rows on
    /// scheduler startup. Overdue jobs (persisted <c>LastRunAt + EffectiveIntervalMinutes</c> already
    /// in the past) are seeded with the current time instead of the stale value, so they wait one
    /// full interval before firing rather than triggering a restart-storm.
    /// </summary>
    Task HydrateAsync(IReadOnlyList<UnseenServantWatermark> watermarks, CancellationToken cancellationToken = default);

}
