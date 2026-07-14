using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Daemons;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Proactive minute-based scheduler that runs configured Unseen Servant jobs as headless <see cref="PingRequest"/> calls.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService daemon scheduler
internal sealed class UnseenServantService(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IUnseenServantPacer pacer,
    IUnseenServantJobTracker jobTracker,
    IDaemonRunner daemonRunner,
    ILogger<UnseenServantService> logger,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    /// <summary>
    /// Startup jitter watermark, intentionally NOT persisted — regenerated fresh every process start
    /// to spread first-tick load. Last-run watermarks are persisted to the Grimoire via
    /// <see cref="IUnseenServantJobTracker.HydrateAsync"/>/<see cref="IUnseenServantWatermarkStore"/>
    /// (see docs/persistence.md), so a hydrated job's <see cref="IUnseenServantJobTracker.GetLastRunAt"/>
    /// is non-null and this jitter path is skipped for it.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstDispatchAfterUtc = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _runningJobs = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Guid, Task> _activeJobTasks = new();

    private readonly DateTimeOffset _startupUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// Cleanup cadence for expired <c>IdempotencyKeys</c> rows. Piggybacks on this service's
    /// existing 1-minute scheduler tick instead of standing up a dedicated <see cref="BackgroundService"/>
    /// — see DESIGN.md §11.17.
    /// </summary>
    private static readonly TimeSpan IdempotencyCleanupInterval = TimeSpan.FromHours(1);

    private DateTimeOffset _lastIdempotencyCleanupUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        await HydrateWatermarksAsync(stoppingToken).ConfigureAwait(false);

        await CleanupExpiredIdempotencyKeysAsync(stoppingToken).ConfigureAwait(false);

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

                if (DateTimeOffset.UtcNow - _lastIdempotencyCleanupUtc >= IdempotencyCleanupInterval)
                {
                    await CleanupExpiredIdempotencyKeysAsync(stoppingToken).ConfigureAwait(false);
                }
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

    private async Task CleanupExpiredIdempotencyKeysAsync(CancellationToken stoppingToken)
    {

        _lastIdempotencyCleanupUtc = DateTimeOffset.UtcNow;

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();

            IIdempotencyStore store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();

            int ttlHours = ArcanumSettingClamps.SecurityIdempotencyTtlHours(
                optionsMonitor.CurrentValue.Security?.IdempotencyTtlHours ?? new SecuritySettings().IdempotencyTtlHours);

            int removed = await store.DeleteExpiredAsync(
                DateTimeOffset.UtcNow.AddHours(-ttlHours),
                stoppingToken).ConfigureAwait(false);

            if (removed > 0)
            {
                logger.LogDebug("Idempotency cache sweep removed {Removed} expired row(s).", removed);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Idempotency cache sweep failed; will retry on the next scheduled cleanup.");
        }

    }

    private async Task HydrateWatermarksAsync(CancellationToken stoppingToken)
    {

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();

            IUnseenServantWatermarkStore store = scope.ServiceProvider.GetRequiredService<IUnseenServantWatermarkStore>();

            IReadOnlyList<UnseenServantWatermark> watermarks = await store.GetAllAsync(stoppingToken).ConfigureAwait(false);

            await jobTracker.HydrateAsync(watermarks, stoppingToken).ConfigureAwait(false);

            await pacer.HydrateAsync(watermarks, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to hydrate Unseen Servant watermarks from Grimoire; falling back to in-memory mode.");
        }

    }

    private void DispatchDueJobs(CancellationToken stoppingToken)
    {
        IReadOnlyList<UnseenServantJob> jobs = optionsMonitor.CurrentValue.Daemon?.Jobs ?? [];

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

            string key = UnseenServantJobTracker.JobTrackingKey(job);

            if (!_runningJobs.TryAdd(key, 0))
            {
                continue;
            }

            if (jobTracker.GetLastRunAt(job) is null)
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

            if (jobTracker.GetLastRunAt(job) is DateTimeOffset last
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
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {

                        logger.LogError(ex, "Unseen Servant job {JobName} failed.", job.Name);

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
                jobTracker.RecordCompletion(job, success: true, resultSummary: "Success");

                await PersistWatermarkAsync(job, key, stoppingToken).ConfigureAwait(false);
            }
            else if (result.Error.Code != "Daemon.Cancelled")
            {
                jobTracker.RecordCompletion(
                    job,
                    success: false,
                    resultSummary: $"[{result.Error.Code}] {result.Error.Message}");

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

    private async Task PersistWatermarkAsync(UnseenServantJob job, string key, CancellationToken stoppingToken)
    {

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();

            IUnseenServantWatermarkStore store = scope.ServiceProvider.GetRequiredService<IUnseenServantWatermarkStore>();

            await store.SaveAsync(key, DateTimeOffset.UtcNow, pacer.GetEffectiveInterval(job), stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist Unseen Servant watermark for job {JobName}.", job.Name);
        }

    }

}

/*
 * --- Sample: UnseenMarketWatcher / SPELL.md (copy to ~/.config/arcanum/spells/UnseenMarketWatcher/SPELL.md) ---
 * ---
 * name: UnseenMarketWatcher
 * description: Example headless daemon spell — query a target (e.g. Kalshi spreads) and persist a moving average for the next cycle via scribe_lexicon.
 * ---
 *
 * ## Daemon job pairing
 *
 * Set `Arcanum:Daemon:Jobs` with `name` matching the daemon job. The host injects prior state from the Lexicon entity
 * `daemon_state:{job.Name}:{shortHash(targetSpell)}` (e.g. job `name` = `MarketWatcher` → entity `daemon_state:MarketWatcher:<hash>`).
 * Use that exact name (type `DaemonState`) in `scribe_lexicon` each run.
 *
 * ## Behavior
 *
 * 1. Use available tools (e.g. Kalshi MCP or HTTP) to read the current bid/ask or spread for one or more target markets.
 * 2. Parse **Previous State** from the kickoff (facts you chose in the prior cycle) for last run's spread and running average.
 * 3. Update a simple moving average (or EMA) of the spread across cycles; include timestamp and raw observations.
 * 4. Call `scribe_lexicon` with name `daemon_state:<YourJobName>:<hash>` and type `DaemonState`, appending concise facts so the next waking cycle can continue the trend.
 * 5. Summarize briefly in natural language if the model output is shown in logs.
 *
 * --- end sample ---
 */
