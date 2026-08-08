using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <inheritdoc />
internal sealed class UnseenServantPacer(
    IEventBus eventBus,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IServiceScopeFactory scopeFactory,
    ILogger<UnseenServantPacer> logger) : IUnseenServantPacer
{

    private readonly ConcurrentDictionary<string, int> _overrides = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool SetDynamicInterval(string jobName, int intervalMinutes)
    {

        string trimmedName = jobName.Trim();

        if (trimmedName.Length == 0)
        {

            return false;
        }

        UnseenServantJob? configured = (optionsMonitor.CurrentValue.Daemon?.Jobs ?? [])
            .FirstOrDefault(job => string.Equals(job.Name.Trim(), trimmedName, StringComparison.Ordinal));

        if (configured is null)
        {

            // Composite keys require a TargetSpell, which only a configured job has. Setting
            // initiative for a name not present in Arcanum:Daemon:Jobs cannot be applied, and the
            // caller is told so rather than being allowed to report a phantom success.
            return false;
        }

        string composite = UnseenServantJobTracker.JobTrackingKey(configured);

        int clamped = ArcanumSettingClamps.UnseenServantIntervalMinutes(intervalMinutes);

        if (_overrides.TryGetValue(composite, out int previous) && previous == clamped)
        {

            return true;
        }

        _overrides[composite] = clamped;

        eventBus.Publish(new DaemonEvent(
            DateTimeOffset.UtcNow,
            Guid.Empty,
            trimmedName,
            configured.TargetSpell,
            DaemonEventType.IntervalChanged,
            Message: clamped.ToString()));

        _ = PersistIntervalAsync(composite, clamped);

        return true;
    }

    /// <inheritdoc />
    public int GetEffectiveInterval(UnseenServantJob job)
    {

        string composite = UnseenServantJobTracker.JobTrackingKey(job);

        int raw = _overrides.TryGetValue(composite, out int fromComposite)
            ? fromComposite
            : job.IntervalMinutes;

        return ArcanumSettingClamps.UnseenServantIntervalMinutes(raw);
    }

    /// <inheritdoc />
    public Task HydrateAsync(IReadOnlyList<UnseenServantWatermark> watermarks, CancellationToken cancellationToken = default)
    {

        foreach (UnseenServantWatermark watermark in watermarks)
        {

            if (watermark.EffectiveIntervalMinutes > 0)
            {

                _overrides[watermark.JobKey] = watermark.EffectiveIntervalMinutes;
            }
        }

        return Task.CompletedTask;

    }

    private async Task PersistIntervalAsync(string composite, int clamped)
    {

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();

            IUnseenServantWatermarkStore store = scope.ServiceProvider.GetRequiredService<IUnseenServantWatermarkStore>();

            UnseenServantWatermark? existing = await store.GetAsync(composite, CancellationToken.None).ConfigureAwait(false);

            DateTimeOffset lastRunAt = existing?.LastRunAt ?? DateTimeOffset.UtcNow;

            await store.SaveAsync(composite, lastRunAt, clamped, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist Unseen Servant interval override for key {JobKey}.", composite);
        }

    }

}
