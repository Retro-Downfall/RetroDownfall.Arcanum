using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <inheritdoc />
internal sealed class UnseenServantJobTracker : IUnseenServantJobTracker
{

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRunUtc = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, string> _lastResult = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void RecordCompletion(UnseenServantJob job, bool success, string? resultSummary)
    {

        string key = JobTrackingKey(job);

        _lastRunUtc[key] = DateTimeOffset.UtcNow;

        _lastResult[key] = resultSummary ?? (success ? "Success" : "Failed");

    }

    /// <inheritdoc />
    public DateTimeOffset? GetLastRunAt(UnseenServantJob job) =>
        _lastRunUtc.TryGetValue(JobTrackingKey(job), out DateTimeOffset last) ? last : null;

    /// <inheritdoc />
    public DateTimeOffset? GetNextDueAt(UnseenServantJob job, int effectiveIntervalMinutes)
    {

        if (!job.Enabled)
        {

            return null;

        }

        DateTimeOffset? last = GetLastRunAt(job);

        if (last is null)
        {

            return null;

        }

        return last.Value.AddMinutes(effectiveIntervalMinutes);

    }

    /// <inheritdoc />
    public string? GetLastResult(UnseenServantJob job) =>
        _lastResult.TryGetValue(JobTrackingKey(job), out string? result) ? result : null;

    internal static string JobTrackingKey(UnseenServantJob job) =>
        $"{job.Name}\0{job.TargetSpell}";

}
