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

    private readonly SemaphoreSlim _changes = new(1, 1);

    /// <inheritdoc />
    public async Task<bool> SetDynamicIntervalAsync(string jobName, int intervalMinutes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        UnseenServantJob snapshot = configured with { };

        string composite = UnseenServantJobTracker.JobTrackingKey(snapshot);

        int clamped = ArcanumSettingClamps.UnseenServantIntervalMinutes(intervalMinutes);

        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_overrides.TryGetValue(composite, out int previous) || previous != clamped)
            {
                _overrides[composite] = clamped;

                eventBus.Publish(new DaemonEvent(
                    DateTimeOffset.UtcNow,
                    Guid.Empty,
                    trimmedName,
                    snapshot.TargetSpell,
                    DaemonEventType.IntervalChanged,
                    Message: clamped.ToString()));
            }

            await PersistIntervalAsync(composite, clamped, cancellationToken).ConfigureAwait(false);

            return true;
        }
        finally
        {
            _changes.Release();
        }
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
            cancellationToken.ThrowIfCancellationRequested();

            if (watermark.EffectiveIntervalMinutes > 0)
            {
                _overrides.TryAdd(watermark.JobKey, watermark.EffectiveIntervalMinutes);
            }
        }

        return Task.CompletedTask;
    }

    private async Task PersistIntervalAsync(string composite, int clamped, CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IUnseenServantWatermarkStore store = scope.ServiceProvider.GetRequiredService<IUnseenServantWatermarkStore>();

            await store.SaveIntervalAsync(composite, DateTimeOffset.UtcNow, clamped, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist Unseen Servant interval override for key {JobKey}.", composite);
        }
    }
}
