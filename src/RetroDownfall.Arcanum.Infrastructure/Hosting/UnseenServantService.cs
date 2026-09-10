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
using RetroDownfall.Arcanum.Infrastructure.Data;

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
    IServiceScopeFactory scopeFactory,
    IGrimoireConnectionAdmissionGate workAdmission,
    TimeProvider timeProvider) : BackgroundService
{
    /// <summary>
    /// Startup jitter watermark, intentionally NOT persisted — regenerated fresh every process start
    /// to spread first-tick load. Last-run watermarks are persisted to the Grimoire via
    /// <see cref="IUnseenServantJobTracker.HydrateAsync"/>/<see cref="IUnseenServantWatermarkStore"/>,
    /// so a hydrated job's <see cref="IUnseenServantJobTracker.GetLastRunAt"/>
    /// is non-null and this jitter path is skipped for it.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstDispatchAfterUtc = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _runningJobs = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Guid, Task> _activeJobTasks = new();

    private readonly object _dispatchGate = new();

    private bool _dispatchClosed;

    private readonly DateTimeOffset _startupUtc = timeProvider.GetUtcNow();

    /// <summary>
    /// Cleanup cadence for expired <c>IdempotencyKeys</c> rows. Piggybacks on this service's
    /// existing 1-minute scheduler tick instead of standing up a dedicated <see cref="BackgroundService"/>
    /// — see <c>docs/Arcanum.DESIGN.md</c> §11.17.
    /// </summary>
    private static readonly TimeSpan IdempotencyCleanupInterval = TimeSpan.FromHours(1);

    private DateTimeOffset _lastIdempotencyCleanupUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        HydrationOutcome hydration = await HydrateWatermarksAsync(stoppingToken).ConfigureAwait(false);

        await CleanupExpiredIdempotencyKeysAsync(stoppingToken).ConfigureAwait(false);

        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1), timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                if (hydration == HydrationOutcome.DeferredForMaintenance)
                {
                    hydration = await HydrateWatermarksAsync(stoppingToken).ConfigureAwait(false);

                    if (hydration == HydrationOutcome.DeferredForMaintenance)
                    {
                        continue;
                    }
                }

                DispatchDueJobs(stoppingToken);

                if (timeProvider.GetUtcNow() - _lastIdempotencyCleanupUtc >= IdempotencyCleanupInterval)
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
        stoppingToken.ThrowIfCancellationRequested();

        if (!workAdmission.TryAcquireWorkLease(GrimoireWorkKind.UnseenServant, out IGrimoireWorkLease? admitted))
        {
            return;
        }

        try
        {
            await using IGrimoireWorkLease lease = admitted!;

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IIdempotencyStore store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();

            IIdempotencyClaimStore claimStore = scope.ServiceProvider.GetRequiredService<IIdempotencyClaimStore>();

            int ttlHours = ArcanumSettingClamps.SecurityIdempotencyTtlHours(
                ArcanumRuntimeDefaults.SecurityIdempotencyTtlHours);

            DateTimeOffset olderThan = timeProvider.GetUtcNow().AddHours(-ttlHours);

            int removed = await store.DeleteExpiredAsync(olderThan, stoppingToken).ConfigureAwait(false);

            int claimsRemoved = await claimStore.DeleteExpiredAsync(olderThan, stoppingToken).ConfigureAwait(false);

            if (removed > 0 || claimsRemoved > 0)
            {
                logger.LogDebug(
                    "Idempotency cache sweep removed {Removed} legacy row(s) and {ClaimsRemoved} claim(s).",
                    removed,
                    claimsRemoved);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Idempotency cache sweep failed; will retry on the next scheduled cleanup.");

            return;
        }

        _lastIdempotencyCleanupUtc = timeProvider.GetUtcNow();
    }

    private async Task<HydrationOutcome> HydrateWatermarksAsync(CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();

        if (!workAdmission.TryAcquireWorkLease(GrimoireWorkKind.UnseenServant, out IGrimoireWorkLease? admitted))
        {
            return HydrationOutcome.DeferredForMaintenance;
        }

        await using IGrimoireWorkLease lease = admitted!;

        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IUnseenServantWatermarkStore store = scope.ServiceProvider.GetRequiredService<IUnseenServantWatermarkStore>();

            IReadOnlyList<UnseenServantWatermark> watermarks = await store.GetAllAsync(stoppingToken).ConfigureAwait(false);

            await jobTracker.HydrateAsync(watermarks, stoppingToken).ConfigureAwait(false);

            await pacer.HydrateAsync(watermarks, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to hydrate Unseen Servant watermarks from Grimoire; falling back to in-memory mode.");

            return HydrationOutcome.Fallback;
        }

        return HydrationOutcome.Hydrated;
    }

    private void DispatchDueJobs(CancellationToken stoppingToken)
    {
        lock (_dispatchGate)
        {
            if (_dispatchClosed || stoppingToken.IsCancellationRequested)
            {
                return;
            }

            DispatchDueJobsCore(stoppingToken);
        }
    }

    private void DispatchDueJobsCore(CancellationToken stoppingToken)
    {
        IReadOnlyList<UnseenServantJob> jobs = optionsMonitor.CurrentValue.Daemon?.Jobs ?? [];

        int maxConcurrent = ArcanumSettingClamps.DaemonMaxConcurrentJobs(
            optionsMonitor.CurrentValue.Daemon?.MaxConcurrentJobs ?? new DaemonSettings().MaxConcurrentJobs);

        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (UnseenServantJob configuredJob in jobs)
        {
            UnseenServantJob job = configuredJob with { };

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

            string daemonId = UnseenServantDaemonIds.ForJobName(job.Name);

            _ = TrackJobTask(
                _activeJobTasks,
                taskId,
                () => Task.Run(
                    async () =>
                    {
                        try
                        {
                            await RunJobAsync(job, key, daemonId, stoppingToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Unseen Servant job {JobName} failed.", job.Name);
                        }
                        finally
                        {
                            _ = _activeJobTasks.TryRemove(taskId, out _);
                        }
                    }));
        }
    }

    /// <summary>
    /// Registers a dispatched job under <paramref name="taskId"/> so a shutdown drain snapshot always
    /// observes the real in-flight job, and so a body that completes (removing its own entry) before the
    /// dispatcher regains control cannot be resurrected by a later write.
    /// </summary>
    internal static Task TrackJobTask(
        ConcurrentDictionary<Guid, Task> activeJobTasks,
        Guid taskId,
        Func<Task> startJob)
    {
        // Unwrap must bind to the real job before dispatch releases _dispatchGate. StopAsync takes
        // that same gate before it snapshots this proxy, so the TCS has no arbitrary concurrent
        // consumer to protect and an asynchronously queued bind can only delay a valid shutdown.
        TaskCompletionSource<Task> handle = new();

        Task publishedTask = handle.Task.Unwrap();

        activeJobTasks[taskId] = publishedTask;

        try
        {
            Task jobTask = startJob();

            handle.SetResult(jobTask);

            return jobTask;
        }
        catch
        {
            handle.SetResult(Task.CompletedTask);

            _ = activeJobTasks.TryRemove(new KeyValuePair<Guid, Task>(taskId, publishedTask));

            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_dispatchGate)
        {
            _dispatchClosed = true;
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        int drainSeconds = ArcanumSettingClamps.DaemonShutdownDrainTimeoutSeconds(
            ArcanumRuntimeDefaults.DaemonShutdownDrainTimeoutSeconds);

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

    private async Task RunJobAsync(UnseenServantJob job, string key, string daemonId, CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();

                long observedGeneration = workAdmission.CurrentGeneration;

                if (await TryRunJobAsync(job, key, daemonId, stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                await workAdmission.WaitForNextOpenGenerationAsync(
                    Math.Max(0, observedGeneration - 1), stoppingToken).ConfigureAwait(false);
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

    private async Task<bool> TryRunJobAsync(UnseenServantJob job, string key, string daemonId, CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();

        if (!workAdmission.TryAcquireWorkLease(GrimoireWorkKind.UnseenServant, out IGrimoireWorkLease? admitted))
        {
            return false;
        }

        await using IGrimoireWorkLease lease = admitted!;

        if (!lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup))
        {
            return false;
        }

        await using IGrimoireExternalEffectGroup group = effectGroup!;

        Result<DaemonExecutionSummary> result = await daemonRunner
            .RunScheduledAsync(daemonId, stoppingToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            DaemonExecutionSummary summary = result.Value;

            bool completed = summary.Status == DaemonJobStatus.Completed;

            jobTracker.RecordCompletion(job, completed,
                completed ? "Success" : $"{summary.Status}: {summary.ErrorMessage}");

            await PersistWatermarkAsync(job, key, stoppingToken).ConfigureAwait(false);
        }
        else if (result.Error.Code != "Daemon.Cancelled")
        {
            jobTracker.RecordCompletion(job, success: false,
                resultSummary: $"[{result.Error.Code}] {result.Error.Message}");

            logger.LogWarning("Unseen Servant job {JobName} failed: {Code} {Message}",
                job.Name, result.Error.Code, result.Error.Message);
        }

        return true;
    }

    private async Task PersistWatermarkAsync(UnseenServantJob job, string key, CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IUnseenServantWatermarkStore store = scope.ServiceProvider.GetRequiredService<IUnseenServantWatermarkStore>();

            await store.SaveLastRunAsync(key, timeProvider.GetUtcNow(), pacer.GetEffectiveInterval(job), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist Unseen Servant watermark for job {JobName}.", job.Name);
        }
    }

    private enum HydrationOutcome
    {
        Hydrated,
        Fallback,
        DeferredForMaintenance
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
