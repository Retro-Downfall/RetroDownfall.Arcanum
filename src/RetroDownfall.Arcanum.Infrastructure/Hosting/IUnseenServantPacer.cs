using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Process-local overrides for Unseen Servant job polling intervals (Phase 2: Adaptive Initiative),
/// write-through persisted to the Grimoire so overrides survive a host restart.
/// </summary>
public interface IUnseenServantPacer
{

    /// <summary>
    /// Applies a polling-interval override for a job configured under <c>Arcanum:Daemon:Jobs</c>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a configured job matched and the override was applied;
    /// <see langword="false"/> when no such job is configured, in which case nothing was changed and
    /// the caller must report the failure rather than claiming success.
    /// </returns>
    bool SetDynamicInterval(string jobName, int intervalMinutes);

    int GetEffectiveInterval(UnseenServantJob job);

    /// <summary>
    /// Seeds in-memory interval overrides from persisted <see cref="UnseenServantWatermark"/> rows
    /// on scheduler startup. Watermarks with <c>EffectiveIntervalMinutes == 0</c> (no override) are skipped.
    /// </summary>
    Task HydrateAsync(IReadOnlyList<UnseenServantWatermark> watermarks, CancellationToken cancellationToken = default);

}
