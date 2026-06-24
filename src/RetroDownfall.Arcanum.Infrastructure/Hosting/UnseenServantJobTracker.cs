using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <inheritdoc />
internal sealed class UnseenServantJobTracker : IUnseenServantJobTracker
{

    private readonly ConcurrentDictionary<string, JobRecord> _records = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void RecordCompletion(UnseenServantJob job, bool success, string? resultSummary)
    {

        string key = JobTrackingKey(job);

        _records[key] = new JobRecord(DateTimeOffset.UtcNow, resultSummary ?? (success ? "Success" : "Failed"));

    }

    /// <inheritdoc />
    public DateTimeOffset? GetLastRunAt(UnseenServantJob job) =>
        _records.TryGetValue(JobTrackingKey(job), out JobRecord record) ? record.LastRunUtc : null;

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
        _records.TryGetValue(JobTrackingKey(job), out JobRecord record) ? record.LastResult : null;

    internal static string JobTrackingKey(UnseenServantJob job) =>
        $"{job.Name}\0{job.TargetSpell}";

    private readonly record struct JobRecord(DateTimeOffset LastRunUtc, string LastResult);

}
