using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <inheritdoc />
internal sealed class UnseenServantPacer(
    IEventBus eventBus,
    IOptionsMonitor<ArcanumSettings> optionsMonitor) : IUnseenServantPacer
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

        if (_overrides.TryGetValue(key, out int previous) && previous == clamped)
        {

            return;
        }

        _overrides[key] = clamped;

        UnseenServantJob? configured = (optionsMonitor.CurrentValue.Daemon?.Jobs ?? [])
            .FirstOrDefault(job => string.Equals(job.Name.Trim(), key, StringComparison.Ordinal));

        string targetSpell = configured?.TargetSpell ?? string.Empty;

        eventBus.Publish(new DaemonEvent(
            DateTimeOffset.UtcNow,
            Guid.Empty,
            key,
            targetSpell,
            DaemonEventType.IntervalChanged,
            Message: clamped.ToString()));
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
