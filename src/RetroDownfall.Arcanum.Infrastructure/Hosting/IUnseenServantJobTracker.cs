using RetroDownfall.Arcanum.Core.Configuration;

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

}
