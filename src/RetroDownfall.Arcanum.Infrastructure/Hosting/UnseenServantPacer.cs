using System.Collections.Concurrent;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <inheritdoc />
internal sealed class UnseenServantPacer : IUnseenServantPacer
{

    private readonly ConcurrentDictionary<string, int> _overrides = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void SetDynamicInterval(string jobName, int intervalMinutes)
    {

        string key = jobName.Trim();

        if (key.Length == 0)
        {

            return;
        }

        int clamped = ArcanumSettingClamps.UnseenServantIntervalMinutes(intervalMinutes);

        _overrides[key] = clamped;
    }

    /// <inheritdoc />
    public int GetEffectiveInterval(UnseenServantJob job)
    {

        string composite = $"{job.Name}\0{job.TargetSpell}";

        int raw = _overrides.TryGetValue(composite, out int fromComposite)
            ? fromComposite
            : _overrides.TryGetValue(job.Name.Trim(), out int fromName)
                ? fromName
                : job.IntervalMinutes;

        return ArcanumSettingClamps.UnseenServantIntervalMinutes(raw);
    }

}
