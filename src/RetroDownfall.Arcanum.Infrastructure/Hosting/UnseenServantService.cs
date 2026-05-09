using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Proactive minute-based scheduler that runs configured Unseen Servant jobs as headless <see cref="PingRequest"/> calls.
/// </summary>
internal sealed class UnseenServantService(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IServiceScopeFactory scopeFactory,
    IUnseenServantPacer pacer,
    ILogger<UnseenServantService> logger) : BackgroundService
{

    /// <summary>
    /// Phase 1: last completion timestamps are process-local only. After a host restart, every enabled job
    /// has no entry here and is treated as due on the first <see cref="PeriodicTimer"/> tick (no persisted watermark).
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRunUtc = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _runningJobs = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                List<UnseenServantJob>? jobList = optionsMonitor.CurrentValue.Daemon?.Jobs;

                IReadOnlyList<UnseenServantJob> jobs = jobList ?? [];

                DateTimeOffset now = DateTimeOffset.UtcNow;

                foreach (UnseenServantJob job in jobs)
                {
                    if (!job.Enabled)
                    {
                        continue;
                    }

                    string key = JobTrackingKey(job);

                    if (!_runningJobs.TryAdd(key, 0))
                    {
                        continue;
                    }

                    int intervalMinutes = ArcanumSettingClamps.UnseenServantIntervalMinutes(pacer.GetEffectiveInterval(job));

                    TimeSpan interval = TimeSpan.FromMinutes(intervalMinutes);

                    if (_lastRunUtc.TryGetValue(key, out DateTimeOffset last)
                        && now - last < interval)
                    {
                        _ = _runningJobs.TryRemove(key, out _);

                        continue;
                    }

                    _ = Task.Run(
                        () => RunJobAsync(job, key, stoppingToken),
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static string JobTrackingKey(UnseenServantJob job) =>
        $"{job.Name}\0{job.TargetSpell}";

    private async Task RunJobAsync(UnseenServantJob job, string key, CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IArcanumIntelligenceProvider intelligence =
                scope.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>();

            int clampedInterval = ArcanumSettingClamps.UnseenServantIntervalMinutes(pacer.GetEffectiveInterval(job));

            string kickoff =
                $"Execute Unseen Servant background protocol. Current polling interval is {clampedInterval} minutes.";

            PingRequest ping = new(
                Prompt: kickoff,
                WorkingDirectory: string.Empty,
                UnattendedMode: true,
                OverrideSpellName: string.IsNullOrWhiteSpace(job.TargetSpell) ? null : job.TargetSpell.Trim());

            Result<string> result = await intelligence
                .ExecutePromptAsync(ping, stoppingToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                logger.LogInformation(
                    "Unseen Servant job {JobName} completed (spell {Spell}).",
                    job.Name,
                    job.TargetSpell);
            }
            else
            {
                logger.LogWarning(
                    "Unseen Servant job {JobName} failed: {Code} {Message}",
                    job.Name,
                    result.Error.Code,
                    result.Error.Message);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Unseen Servant job {JobName} cancelled during shutdown.", job.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unseen Servant job {JobName} threw an unhandled exception.", job.Name);
        }
        finally
        {
            _lastRunUtc[key] = DateTimeOffset.UtcNow;

            _ = _runningJobs.TryRemove(key, out _);
        }
    }

}
