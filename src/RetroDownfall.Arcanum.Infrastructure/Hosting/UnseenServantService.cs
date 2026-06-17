using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Daemons;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Proactive minute-based scheduler that runs configured Unseen Servant jobs as headless <see cref="PingRequest"/> calls.
/// </summary>
internal sealed class UnseenServantService(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IUnseenServantPacer pacer,
    IDaemonRunner daemonRunner,
    ILogger<UnseenServantService> logger) : BackgroundService
{
    /// <summary>
    /// Phase 1: last completion timestamps are process-local only. After a host restart, every enabled job
    /// has no entry here and is treated as due once startup jitter elapses (no persisted watermark).
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRunUtc = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstDispatchAfterUtc = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _runningJobs = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Guid, Task> _activeJobTasks = new();

    private readonly DateTimeOffset _startupUtc = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                DispatchDueJobs(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unseen Servant scheduler tick failed; continuing.");
            }
        }
    }

    private void DispatchDueJobs(CancellationToken stoppingToken)
    {
        List<UnseenServantJob>? jobList = optionsMonitor.CurrentValue.Daemon?.Jobs;

        IReadOnlyList<UnseenServantJob> jobs = jobList ?? [];

        int maxConcurrent = ArcanumSettingClamps.DaemonMaxConcurrentJobs(
            optionsMonitor.CurrentValue.Daemon?.MaxConcurrentJobs ?? new DaemonSettings().MaxConcurrentJobs);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (UnseenServantJob job in jobs)
        {
            if (!job.Enabled)
            {
                continue;
            }

            if (_runningJobs.Count >= maxConcurrent)
            {
                logger.LogDebug(
                    "Unseen Servant deferring job {JobName}: at MaxConcurrentJobs={MaxConcurrent}.",
                    job.Name,
                    maxConcurrent);

                continue;
            }

            string key = JobTrackingKey(job);

            if (!_runningJobs.TryAdd(key, 0))
            {
                continue;
            }

            if (!_lastRunUtc.ContainsKey(key))
            {
                DateTimeOffset firstAfter = _firstDispatchAfterUtc.GetOrAdd(
                    key,
                    _ => _startupUtc.AddSeconds(Random.Shared.Next(0, 60)));

                if (now < firstAfter)
                {
                    logger.LogDebug(
                        "Unseen Servant deferring job {JobName}: startup jitter until {FirstAfter:o}.",
                        job.Name,
                        firstAfter);

                    _ = _runningJobs.TryRemove(key, out _);

                    continue;
                }
            }

            int intervalMinutes = ArcanumSettingClamps.UnseenServantIntervalMinutes(pacer.GetEffectiveInterval(job));

            TimeSpan interval = TimeSpan.FromMinutes(intervalMinutes);

            if (_lastRunUtc.TryGetValue(key, out DateTimeOffset last)
                && now - last < interval)
            {
                _ = _runningJobs.TryRemove(key, out _);

                continue;
            }

            Guid taskId = Guid.NewGuid();

            _activeJobTasks[taskId] = Task.CompletedTask;

            Task jobTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await RunJobAsync(job, key, stoppingToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _ = _activeJobTasks.TryRemove(taskId, out _);
                    }
                },
                stoppingToken);

            _activeJobTasks[taskId] = jobTask;

            if (jobTask.IsCanceled)
            {

                _ = _activeJobTasks.TryRemove(taskId, out _);

                _ = _runningJobs.TryRemove(key, out _);

                continue;

            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        int drainSeconds = ArcanumSettingClamps.DaemonShutdownDrainTimeoutSeconds(
            optionsMonitor.CurrentValue.Daemon?.ShutdownDrainTimeoutSeconds ?? new DaemonSettings().ShutdownDrainTimeoutSeconds);

        if (drainSeconds <= 0)
        {
            return;
        }

        Task[] snapshot = _activeJobTasks.Values.ToArray();

        if (snapshot.Length == 0)
        {
            return;
        }

        using CancellationTokenSource drainCts = new(TimeSpan.FromSeconds(drainSeconds));

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            drainCts.Token);

        try
        {
            Task drained = Task.WhenAll(snapshot);

            await drained.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "Unseen Servant shutdown drain elapsed before {Count} job(s) completed.",
                snapshot.Length);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unseen Servant shutdown drain observed an unhandled exception.");
        }
    }

    private static string JobTrackingKey(UnseenServantJob job) =>
        $"{job.Name}\0{job.TargetSpell}";

    private async Task RunJobAsync(UnseenServantJob job, string key, CancellationToken stoppingToken)
    {
        try
        {
            string daemonId = UnseenServantDaemonIds.ForJobName(job.Name);

            Result<DaemonExecutionSummary> result = await daemonRunner
                .RunScheduledAsync(daemonId, stoppingToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                _lastRunUtc[key] = DateTimeOffset.UtcNow;
            }
            else if (result.Error.Code != "Daemon.Cancelled")
            {
                _lastRunUtc[key] = DateTimeOffset.UtcNow;

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
        finally
        {
            _ = _runningJobs.TryRemove(key, out _);
        }
    }

}

/*
 * --- Sample: UnseenMarketWatcher / SPELL.md (copy to ~/.config/arcanum/spells/UnseenMarketWatcher/SPELL.md) ---
 * ---
 * name: UnseenMarketWatcher
 * description: Example headless daemon spell — query a target (e.g. Kalshi spreads) and persist a moving average for the next cycle via scribe_lore.
 * ---
 *
 * ## Daemon job pairing
 *
 * Set `Arcanum:Daemon:Jobs` with `name` matching the lore suffix you persist under. The host injects prior state from
 * `daemon_state_{job.Name}` (e.g. job `name` = `MarketWatcher` → key `daemon_state_MarketWatcher`). Use that exact key
 * in `scribe_lore` each run.
 *
 * ## Behavior
 *
 * 1. Use available tools (e.g. Kalshi MCP or HTTP) to read the current bid/ask or spread for one or more target markets.
 * 2. Parse **Previous State** from the kickoff (JSON or plain text you chose in the prior cycle) for last run's spread and running average.
 * 3. Update a simple moving average (or EMA) of the spread across cycles; include timestamp and raw observations.
 * 4. Call `scribe_lore` with key `daemon_state_<YourJobName>` and a compact value string so the next waking cycle can continue the trend.
 * 5. Summarize briefly in natural language if the model output is shown in logs.
 *
 * --- end sample ---
 */
