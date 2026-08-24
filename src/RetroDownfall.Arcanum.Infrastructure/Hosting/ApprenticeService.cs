using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: IHostedService + IApprenticeRuntime long-running orchestration
internal sealed class ApprenticeService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ChronicleHub chronicleHub,
    ILogger<ApprenticeService> logger) : BackgroundService, IApprenticeRuntime
{

    private readonly ConcurrentDictionary<Guid, ExecutionLease> _executionTokens = new();

    private readonly ConcurrentDictionary<Guid, long> _executionGenerations = new();

    private readonly ConcurrentDictionary<Guid, Task> _activeTasks = new();

    private readonly ConcurrentQueue<Guid> _pendingStarts = new();

    private readonly ConcurrentDictionary<Guid, byte> _pendingStartIds = new();

    private readonly ConcurrentDictionary<Guid, byte> _freshStartIds = new();

    private readonly Lock _pendingStartsLock = new();

    private readonly ApprenticeConcurrencyGate _concurrencyGate = new();

    private readonly ConcurrentDictionary<Guid, IDisposable> _executionLeases = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        await ResumeCrashRecoveryAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<Guid, ExecutionLease> pair in _executionTokens)
        {
            try
            {
                await pair.Value.Cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        Task[] tasks = [.. _activeTasks.Values];

        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Apprentice shutdown drain timed out or failed.");
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<string>> StartAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
    {
        ApprenticeSettings settings = GetApprenticeSettings();

        if (!settings.Enabled)
        {
            return Result<string>.Failure(new Error(ErrorCodes.Apprentice.Disabled, "Apprentice orchestration is disabled."));
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice is null)
        {
            return Result<string>.Failure(new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found."));
        }

        if (!CanStart(apprentice.Status))
        {
            return Result<string>.Failure(new Error(ErrorCodes.Apprentice.AlreadyRunning, "Apprentice is already running or not in a startable state."));
        }

        apprentice.Status = ApprenticeStatus.Planning.ToString();

        await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

        _freshStartIds.TryAdd(apprenticeId, 0);

        if (!TryAcquireExecutionSlot(apprenticeId, queueOnCapacity: true, out bool queued, out Result<string>? capacityFailure))
        {

            _freshStartIds.TryRemove(apprenticeId, out _);

            return capacityFailure!;

        }

        if (queued)
        {

            // Fix 2: the apprentice was successfully queued for the next slot; no
            // execution task has started yet (it starts when a slot frees up).

            return Result<string>.Success(apprenticeId.ToString());

        }

        BeginExecutionTask(apprenticeId);

        return Result<string>.Success(apprenticeId.ToString());
    }

    public async Task<Result<string>> PauseAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice is null)
        {
            return Result<string>.Failure(new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found."));
        }

        if (!IsPausable(apprentice.Status))
        {
            return Result<string>.Failure(new Error(ErrorCodes.Apprentice.Running, "Apprentice is not running or planning."));
        }

        long? pauseGeneration = null;

        if (_executionTokens.TryGetValue(apprenticeId, out ExecutionLease? lease))
        {
            pauseGeneration = lease.Generation;

            try
            {
                await lease.Cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // Only persist Paused if this pause still owns the execution generation. A newer Resume
        // increments the generation; a late Pause must not clobber that Running status.
        if (pauseGeneration is null || OwnsExecutionGeneration(apprenticeId, pauseGeneration.Value))
        {

            apprentice.Status = ApprenticeStatus.Paused.ToString();

            await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

            Publish(apprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.ApprenticePaused,
                ApprenticeId = apprenticeId,
                Timestamp = DateTimeOffset.UtcNow,
                AtStep = apprentice.CurrentStep,
            });

        }

        return Result<string>.Success(apprenticeId.ToString());
    }

    public async Task<Result<string>> ResumeAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice is null)
        {
            return Result<string>.Failure(new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found."));
        }

        if (!string.Equals(apprentice.Status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal))
        {
            return Result<string>.Failure(new Error(ErrorCodes.Apprentice.NotPaused, "Apprentice is not paused."));
        }

        if (!TryAcquireExecutionSlot(apprenticeId, queueOnCapacity: false, out bool _, out Result<string>? capacityFailure))
        {

            return capacityFailure!;

        }

        apprentice.Status = ApprenticeStatus.Running.ToString();

        await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.ApprenticeResumed,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            FromStep = apprentice.CurrentStep,
        });

        BeginExecutionTask(apprenticeId);

        return Result<string>.Success(apprenticeId.ToString());
    }

    public async Task<Result<string>> CancelAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
    {

        // Fix 1: a queued (Idle) apprentice is not IsCancellable, but it can be
        // cancelled directly by draining it from the pending start queue. Check
        // pending FIRST, before the IsCancellable guard, so a queued apprentice
        // is cancelled (not left to start later).

        if (RemovePendingStart(apprenticeId))
        {

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

            Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

            if (apprentice is null)
            {

                return Result<string>.Failure(new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found."));

            }

            apprentice.Status = ApprenticeStatus.Cancelled.ToString();

            await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

            Publish(apprenticeId, new ApprenticeEvent
            {

                Type = ApprenticeEventType.ApprenticeCancelled,

                ApprenticeId = apprenticeId,

                Timestamp = DateTimeOffset.UtcNow,

            });

            return Result<string>.Success(apprenticeId.ToString());

        }

        await using AsyncServiceScope outerScope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository outerRepo = outerScope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? loaded = await outerRepo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (loaded is null)
        {

            return Result<string>.Failure(new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found."));

        }

        if (!IsCancellable(loaded.Status))
        {

            return Result<string>.Failure(new Error(ErrorCodes.Apprentice.NotPaused, "Apprentice is not in a cancellable state."));

        }

        long? cancelGeneration = null;

        if (_executionTokens.TryGetValue(apprenticeId, out ExecutionLease? lease))
        {

            cancelGeneration = lease.Generation;

            try
            {

                await lease.Cts.CancelAsync().ConfigureAwait(false);

            }
            catch (ObjectDisposedException)
            {

            }

        }

        // Only persist Cancelled if this cancel still owns the execution generation. A newer Resume
        // increments the generation; a late Cancel must not clobber that Running status.
        if (cancelGeneration is null || OwnsExecutionGeneration(apprenticeId, cancelGeneration.Value))
        {

            loaded.Status = ApprenticeStatus.Cancelled.ToString();

            await outerRepo.UpdateAsync(loaded, cancellationToken).ConfigureAwait(false);

            Publish(apprenticeId, new ApprenticeEvent
            {

                Type = ApprenticeEventType.ApprenticeCancelled,

                ApprenticeId = apprenticeId,

                Timestamp = DateTimeOffset.UtcNow,

            });

        }

        return Result<string>.Success(apprenticeId.ToString());

    }

    public async Task<Result<ApprenticeDetailDto>> ReweaveAsync(
        Guid apprenticeId,
        IReadOnlyList<PlanStep> steps,
        CancellationToken cancellationToken = default)
    {
        Result<List<PlanStep>> validated = ApprenticeExecutionPolicy.ValidateReweaveSteps(steps);

        if (validated.IsFailure)
        {

            return Result<ApprenticeDetailDto>.Failure(validated.Error);

        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice is null)
        {

            return Result<ApprenticeDetailDto>.Failure(
                new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found."));

        }

        if (!ApprenticeExecutionPolicy.IsReweavableStatus(apprentice.Status))
        {

            return Result<ApprenticeDetailDto>.Failure(
                new Error(ErrorCodes.Apprentice.CannotReweave, "Apprentice is not in a state that allows re-weaving the plan."));

        }

        List<PlanStep> currentPlan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

        List<PlanStep> merged = ApprenticeExecutionPolicy.MergePlanTail(
            currentPlan,
            apprentice.CurrentStep,
            validated.Value!);

        apprentice.Plan = ApprenticeRepository.SerializePlan(merged);

        await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.PlanRevised,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            Plan = merged,
            AtStep = apprentice.CurrentStep,
        });

        return Result<ApprenticeDetailDto>.Success(ToDetailDto(apprentice));

    }

    public async Task<Result<string>> InterveneAsync(
        Guid apprenticeId,
        string guidance,
        bool resume,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(guidance))
        {

            return Result<string>.Failure(
                new Error(ErrorCodes.Apprentice.InvalidGuidance, "Dungeon Master guidance is required."));

        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice is null)
        {

            return Result<string>.Failure(
                new Error(ErrorCodes.Apprentice.NotFound, "Apprentice was not found."));

        }

        if (!ApprenticeExecutionPolicy.IsEscalatedStatus(apprentice.Status))
        {

            return Result<string>.Failure(
                new Error(ErrorCodes.Apprentice.NotEscalated, "Apprentice is not awaiting Divine Intervention."));

        }

        // Resume needs a slot first: MaxReached must not mutate plan/checkpoint/events.
        if (resume
            && !TryAcquireExecutionSlot(apprenticeId, queueOnCapacity: false, out bool _, out Result<string>? capacityFailure))
        {

            return capacityFailure!;

        }

        try
        {

            ApplyDivineInterventionGuidance(apprentice, guidance.Trim());

            if (resume)
            {

                apprentice.Status = ApprenticeStatus.Running.ToString();

            }

            await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

            Publish(apprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.ApprenticeIntervened,
                ApprenticeId = apprenticeId,
                Timestamp = DateTimeOffset.UtcNow,
                AtStep = apprentice.CurrentStep,
                Summary = guidance.Trim(),
            });

            if (resume)
            {

                BeginExecutionTask(apprenticeId);

            }

            return Result<string>.Success(apprenticeId.ToString());

        }
        catch
        {

            if (resume)
            {

                ReleaseAcquiredExecutionSlot(apprenticeId);

            }

            throw;

        }

    }

    private static void ApplyDivineInterventionGuidance(Apprentice apprentice, string guidance)
    {

        List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

        int stepIndex = apprentice.CurrentStep;

        if (stepIndex < plan.Count)
        {

            PlanStep current = plan[stepIndex];

            plan[stepIndex] = current with
            {
                Status = "pending",
                Attempts = 0,
                StartedAt = null,
                CompletedAt = null,
                Result = null,
            };

            apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

        }

        ApprenticeCheckpoint? existing = ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData);

        apprentice.CheckpointData = ApprenticeRepository.SerializeCheckpoint(RebaseCheckpoint(existing) with
        {
            CurrentStep = apprentice.CurrentStep,
            Timestamp = DateTimeOffset.UtcNow,
            DmGuidance = guidance,
        });

        apprentice.ErrorMessage = null;

    }

    /// <summary>
    /// The base every checkpoint rewrite must build on. Rewrites go through <c>with</c> on this value rather
    /// than a fresh initializer so that a member the rewriting path does not name is carried forward instead
    /// of silently reset — <see cref="ApprenticeCheckpoint.DelegationChain"/> is the only durable carrier of
    /// the A2A delegation lineage, and <c>ConclaveDelegationChain.ContainsSelf</c> at the receiving peer is
    /// the sole cycle guard (issue #55 forbids a hop ceiling), so losing it on a step boundary makes every
    /// later dispatch loop-blind.
    /// </summary>
    private static ApprenticeCheckpoint RebaseCheckpoint(ApprenticeCheckpoint? existing) =>
        existing ?? new ApprenticeCheckpoint();

    public IAsyncEnumerable<ApprenticeEvent> SubscribeChronicleAsync(
        Guid apprenticeId,
        CancellationToken cancellationToken = default) =>
        chronicleHub.SubscribeAsync(apprenticeId, cancellationToken);

    private async Task ResumeCrashRecoveryAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

            IReadOnlyList<Apprentice> interruptedPlanning = await repo
                .GetInterruptedPlanningAsync(stoppingToken)
                .ConfigureAwait(false);

            foreach (Apprentice apprentice in interruptedPlanning)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                const string reason = "Interrupted during planning after host restart.";

                apprentice.Status = ApprenticeStatus.Escalated.ToString();

                apprentice.ErrorMessage = reason;

                ApprenticeCheckpoint? existing = ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData);

                apprentice.CheckpointData = ApprenticeRepository.SerializeCheckpoint(RebaseCheckpoint(existing) with
                {
                    CurrentStep = apprentice.CurrentStep,
                    Timestamp = DateTimeOffset.UtcNow,
                    EscalationReason = reason,
                });

                await repo.UpdateAsync(apprentice, stoppingToken).ConfigureAwait(false);

                logger.LogWarning(
                    "Apprentice {ApprenticeId} was interrupted during planning; escalated for Divine Intervention.",
                    apprentice.Id);

                Publish(apprentice.Id, new ApprenticeEvent
                {
                    Type = ApprenticeEventType.ApprenticeEscalated,
                    ApprenticeId = apprentice.Id,
                    Timestamp = DateTimeOffset.UtcNow,
                    Error = reason,
                });
            }

            IReadOnlyList<Apprentice> resumable = await repo.GetResumableAsync(stoppingToken).ConfigureAwait(false);

            foreach (Apprentice apprentice in resumable)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (!TryAcquireExecutionSlot(apprentice.Id, queueOnCapacity: true, out bool recoveryQueued, out Result<string>? recoveryFailure))
                {

                    logger.LogWarning(
                        "Apprentice {ApprenticeId} could not be resumed after host restart ({Code}): {Message}. It remains in the DB and will retry on the next restart that has capacity.",
                        apprentice.Id,
                        recoveryFailure?.Error.Code,
                        recoveryFailure?.Error.Message);

                    continue;

                }

                if (recoveryQueued)
                {

                    // Fix 2: successfully queued for the next slot; no execution
                    // task yet. The pending-start drainer will start it.

                    logger.LogInformation(
                        "Apprentice {ApprenticeId} queued for next slot after host restart.", apprentice.Id);

                    continue;

                }

                logger.LogInformation("Resuming Apprentice {ApprenticeId} after host restart.", apprentice.Id);

                BeginExecutionTask(apprentice.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Apprentice crash recovery failed.");
        }
    }

    private bool TryAcquireExecutionSlot(Guid apprenticeId, bool queueOnCapacity, out bool queued, out Result<string>? failure)
    {

        failure = null;

        queued = false;

        if (!_activeTasks.TryAdd(apprenticeId, Task.CompletedTask))
        {

            failure = Result<string>.Failure(
                new Error(ErrorCodes.Apprentice.AlreadyRunning, "Apprentice is already running or not in a startable state."));

            return false;

        }

        ApprenticeSettings settings = GetApprenticeSettings();

        int maxConcurrent = ArcanumSettingClamps.MaxConcurrentApprentices(settings.MaxConcurrentApprentices);

        if (_concurrencyGate.TryAcquire(maxConcurrent, out IDisposable? lease))
        {

            // Increment generation on acquire so a prior run's CleanupExecution cannot dispose this
            // lease or clobber status after a Resume wins the slot but before BeginExecutionTask.
            _executionGenerations.AddOrUpdate(apprenticeId, 1L, static (_, current) => current + 1);

            _executionLeases[apprenticeId] = lease!;

            return true;

        }

        _activeTasks.TryRemove(apprenticeId, out _);

        if (queueOnCapacity)
        {
            lock (_pendingStartsLock)
            {

                // Dedup: an idempotent re-start of an already-pending apprentice is a
                // successful queue (no duplicate enqueue).

                if (_pendingStartIds.TryAdd(apprenticeId, 0))
                {
                    _pendingStarts.Enqueue(apprenticeId);

                }

            }

            // Successfully queued (or already queued): not a failure. The caller
            // learns via queued=true that no execution task has started yet.

            queued = true;

            return true;

        }

        failure = Result<string>.Failure(
            new Error(ErrorCodes.Apprentice.MaxReached, "Maximum concurrent Apprentices reached."));

        return false;

    }

    /// <summary>
    /// Releases a slot acquired via <see cref="TryAcquireExecutionSlot"/> before
    /// <see cref="BeginExecutionTask"/> runs (e.g. InterveneAsync persistence failure).
    /// </summary>
    private void ReleaseAcquiredExecutionSlot(Guid apprenticeId)
    {

        _activeTasks.TryRemove(apprenticeId, out _);

        if (_executionLeases.TryRemove(apprenticeId, out IDisposable? lease))
        {

            lease.Dispose();

            TryDequeuePendingStart();

        }

    }

    private void BeginExecutionTask(Guid apprenticeId)
    {

        // Generation was incremented when the execution slot was acquired (Start/Resume/Intervene/
        // crash-recovery). Re-read it here so the run + cleanup share the same lease token.
        if (!_executionGenerations.TryGetValue(apprenticeId, out long generation))
        {

            generation = _executionGenerations.AddOrUpdate(apprenticeId, 1L, static (_, current) => current + 1);

        }

        Task task = Task.Run(() => RunApprenticeAsync(apprenticeId, generation));

        _activeTasks[apprenticeId] = task;

        _ = task.ContinueWith(
            antecedent =>
            {

                if (antecedent.IsFaulted && antecedent.Exception is not null)
                {

                    logger.LogError(antecedent.Exception, "Apprentice run task faulted for {ApprenticeId}.", apprenticeId);

                }

                CleanupExecution(apprenticeId, generation, antecedent);

            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    }

    private void CleanupExecution(Guid apprenticeId, long generation, Task completedTask)
    {

        if (_activeTasks.TryGetValue(apprenticeId, out Task? active)
            && ReferenceEquals(active, completedTask))
        {

            _activeTasks.TryRemove(apprenticeId, out _);

        }

        if (_executionTokens.TryGetValue(apprenticeId, out ExecutionLease? token)
            && token.Generation == generation
            && _executionTokens.TryRemove(KeyValuePair.Create(apprenticeId, token)))
        {

            token.Cts.Dispose();

        }

        if (OwnsExecutionGeneration(apprenticeId, generation)
            && _executionLeases.TryRemove(apprenticeId, out IDisposable? lease))
        {

            lease.Dispose();

            TryDequeuePendingStart();

        }

    }

    private bool OwnsExecutionGeneration(Guid apprenticeId, long generation) =>
        _executionGenerations.TryGetValue(apprenticeId, out long current) && current == generation;

    private sealed record ExecutionLease(CancellationTokenSource Cts, long Generation);

    private void TryDequeuePendingStart()
    {
        ApprenticeSettings settings = GetApprenticeSettings();

        int maxConcurrent = ArcanumSettingClamps.MaxConcurrentApprentices(settings.MaxConcurrentApprentices);

        while (true)
        {

            Guid nextId;

            lock (_pendingStartsLock)
            {

                if (!_pendingStarts.TryDequeue(out nextId))
                {

                    return;

                }

                // Committing to start (or drop) this id: it is no longer pending.

                _pendingStartIds.TryRemove(nextId, out _);

            }

            if (_concurrencyGate.RunningCount >= maxConcurrent)
            {

                // Gate full again: re-enqueue and re-register as pending.

                lock (_pendingStartsLock)
                {

                    _pendingStartIds.TryAdd(nextId, 0);

                    _pendingStarts.Enqueue(nextId);

                }

                return;

            }

            if (!TryAcquireExecutionSlot(nextId, queueOnCapacity: false, out bool _, out Result<string>? _))
            {

                continue;

            }

            BeginExecutionTask(nextId);

            return;

        }

    }

    /// <summary>
    /// Removes a specific apprentice id from the pending start queue and its
    /// dedup set. Returns true if the id was pending (and is now removed),
    /// false otherwise. Used by CancelAsync to drain a queued (Idle) apprentice
    /// without widening IsCancellable's meaning.
    /// </summary>

    private bool RemovePendingStart(Guid apprenticeId)
    {

        lock (_pendingStartsLock)
        {

            if (!_pendingStartIds.TryRemove(apprenticeId, out _))
            {

                return false;

            }

            _freshStartIds.TryRemove(apprenticeId, out _);

            // ConcurrentQueue has no O(1) removal of a specific element: drain
            // and re-enqueue all ids except the target. The lock keeps the queue
            // and dedup set in sync atomically.

            if (_pendingStarts.IsEmpty)
            {

                return true;

            }

            Guid[] snapshot = _pendingStarts.ToArray();

            _pendingStarts.Clear();

            foreach (Guid id in snapshot)
            {

                if (id != apprenticeId)
                {

                    _pendingStarts.Enqueue(id);

                }

            }

            return true;

        }

    }

    private async Task RunApprenticeAsync(Guid apprenticeId, long generation)
    {
        DateTimeOffset runStarted = DateTimeOffset.UtcNow;

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);

        _executionTokens[apprenticeId] = new ExecutionLease(linkedCts, generation);

        try
        {
            ApprenticeSettings settings = GetApprenticeSettings();

            if (!settings.Enabled)
            {
                return;
            }

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

            IArcanumIntelligenceProvider intelligence =
                scope.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>();

            IGrimoireRepository grimoire = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, linkedCts.Token).ConfigureAwait(false);

            if (apprentice is null)
            {
                return;
            }

            if (ApprenticeExecutionPolicy.IsEscalatedStatus(apprentice.Status))
            {

                return;

            }

            if (string.Equals(apprentice.Status, ApprenticeStatus.Cancelled.ToString(), StringComparison.Ordinal))
            {

                return;

            }

            List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

            bool isContinuation = string.Equals(apprentice.Status, ApprenticeStatus.Running.ToString(), StringComparison.Ordinal)
                || string.Equals(apprentice.Status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal);

            if (!isContinuation)
            {

                // Fix 5: a Planning apprentice reaching here was resumed after a host
                // restart (GetResumableAsync returns Planning apprentices with empty
                // plans). It must emit ApprenticeResumed, not a duplicate
                // ApprenticeStarted. A fresh start (Idle/Failed/Completed/Cancelled)
                // still emits ApprenticeStarted.

                bool isFreshStart = _freshStartIds.TryRemove(apprenticeId, out _);

                bool isResumeAfterRestart = !isFreshStart
                    && string.Equals(
                        apprentice.Status,
                        ApprenticeStatus.Planning.ToString(),
                        StringComparison.Ordinal);

                apprentice.Status = ApprenticeStatus.Planning.ToString();

                await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

                if (isResumeAfterRestart)
                {

                    Publish(apprenticeId, new ApprenticeEvent
                    {

                        Type = ApprenticeEventType.ApprenticeResumed,

                        ApprenticeId = apprenticeId,

                        Timestamp = DateTimeOffset.UtcNow,

                        FromStep = apprentice.CurrentStep,

                    });

                }
                else
                {

                    Publish(apprenticeId, new ApprenticeEvent
                    {

                        Type = ApprenticeEventType.ApprenticeStarted,

                        ApprenticeId = apprenticeId,

                        Timestamp = DateTimeOffset.UtcNow,

                        Name = apprentice.Name,

                        Goal = apprentice.Goal,

                    });

                }

            }

            if (plan.Count == 0)
            {
                string planPrompt = ApprenticePromptBuilder.BuildPlanGenerationPrompt(apprentice);

                string? model = ResolveModel();

                PingRequest planRequest = new(
                    Prompt: planPrompt,
                    Model: model,
                    WorkingDirectory: apprentice.WorkspacePath,
                    UnattendedMode: true,
                    SkipSpellRouting: true);

                Result<PromptTurnResult> planResult = await intelligence
                    .ExecutePromptAsync(planRequest, ArcanumInvocationContext.None, linkedCts.Token)
                    .ConfigureAwait(false);

                if (planResult.IsFailure)
                {
                    await FailApprenticeAsync(repo, apprentice, planResult.Error.Message, apprenticeId, linkedCts.Token)
                        .ConfigureAwait(false);

                    return;
                }

                plan = ApprenticePlanParser.ParsePlan(planResult.Value.Text);

                apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

                apprentice.Status = ApprenticeStatus.Running.ToString();

                await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

                Publish(apprenticeId, new ApprenticeEvent
                {
                    Type = ApprenticeEventType.PlanGenerated,
                    ApprenticeId = apprenticeId,
                    Timestamp = DateTimeOffset.UtcNow,
                    Plan = plan,
                });
            }
            else if (!string.Equals(apprentice.Status, ApprenticeStatus.Running.ToString(), StringComparison.Ordinal))
            {
                apprentice.Status = ApprenticeStatus.Running.ToString();

                await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);
            }

            if (apprentice.SessionId is null)
            {
                string? model = ResolveModel();

                (Guid sessionId, _) = await grimoire
                    .BeginAssistantReplyAsync(
                        null,
                        $"Apprentice {apprentice.Name} begins their quest.",
                        model ?? "apprentice",
                        linkedCts.Token)
                    .ConfigureAwait(false);

                apprentice.SessionId = sessionId;

                await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);
            }

            while (apprentice.CurrentStep < plan.Count)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                Apprentice? fresh = await repo.GetByIdAsync(apprenticeId, linkedCts.Token).ConfigureAwait(false);

                if (fresh is null)
                {

                    return;

                }

                if (string.Equals(fresh.Status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal)
                    || string.Equals(fresh.Status, ApprenticeStatus.Cancelled.ToString(), StringComparison.Ordinal)
                    || ApprenticeExecutionPolicy.IsEscalatedStatus(fresh.Status))
                {

                    return;

                }

                apprentice = fresh;

                plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

                if (apprentice.CurrentStep >= plan.Count)
                {

                    break;

                }

                int stepIndex = apprentice.CurrentStep;

                int simulacrumGroupEnd = ComputeParallelGroupEnd(plan, stepIndex);

                if (simulacrumGroupEnd - stepIndex > 1)
                {

                    bool advanced = await ExecuteSimulacrumGroupAsync(
                        repo,
                        intelligence,
                        apprentice,
                        plan,
                        stepIndex,
                        simulacrumGroupEnd,
                        settings,
                        apprenticeId,
                        linkedCts).ConfigureAwait(false);

                    if (!advanced)
                    {

                        return;

                    }

                    continue;

                }

                StepExecutionOutcome? outcome = null;
                HashSet<StepRecoveryState> observedRecoveryStates = [];
                int attempt = 1;

                while (true)
                {

                    linkedCts.Token.ThrowIfCancellationRequested();

                    plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

                    if (stepIndex >= plan.Count)
                    {

                        break;

                    }

                    PlanStep current = plan[stepIndex] with
                    {
                        Status = "in_progress",
                        StartedAt = DateTimeOffset.UtcNow,
                        Attempts = attempt - 1,
                    };

                    plan[stepIndex] = current;

                    apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

                    await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

                    if (attempt == 1)
                    {

                        Publish(apprenticeId, new ApprenticeEvent
                        {
                            Type = ApprenticeEventType.StepStarted,
                            ApprenticeId = apprenticeId,
                            Timestamp = DateTimeOffset.UtcNow,
                            StepIndex = current.Index,
                            Description = current.Description,
                        });

                    }

                    ApprenticeCheckpoint? checkpoint = ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData);

                    string stepPrompt = ApprenticePromptBuilder.BuildStepExecutionPrompt(
                        apprentice,
                        plan,
                        stepIndex,
                        checkpoint);

                    outcome = await ExecuteStepStreamAsync(
                        intelligence,
                        apprentice,
                        stepPrompt,
                        linkedCts,
                        apprenticeId).ConfigureAwait(false);

                    StepFailureKind failureKind = ApprenticeExecutionPolicy.ClassifyStepFailure(
                        outcome.StepFailed,
                        outcome.EscalationRequested,
                        outcome.WardDenied,
                        outcome.ForbiddenArtDenied,
                        outcome.PauseOrCancel,
                        outcome.IsRetryable);

                    if (failureKind == StepFailureKind.PausedOrCancelled)
                    {

                        return;

                    }

                    if (failureKind == StepFailureKind.None)
                    {

                        break;

                    }

                    if (failureKind == StepFailureKind.EscalationRequested)
                    {

                        await EscalateAsync(
                            repo,
                            apprentice,
                            apprenticeId,
                            stepIndex,
                            outcome.ErrorMessage ?? "The Apprentice petitioned the Dungeon Master.",
                            outcome.AlreadyAlerted,
                            linkedCts.Token).ConfigureAwait(false);

                        return;

                    }

                    if (failureKind == StepFailureKind.Terminal)
                    {

                        await FailStepAsync(
                            repo,
                            apprentice,
                            plan,
                            stepIndex,
                            current,
                            outcome.ErrorMessage ?? "Step execution failed.",
                            apprenticeId,
                            linkedCts.Token).ConfigureAwait(false);

                        return;

                    }

                    StepRecoveryState recoveryState = BuildStepRecoveryState(outcome);
                    if (!observedRecoveryStates.Add(recoveryState))
                    {
                        const string noProgressMessage =
                            "Step recovery stopped because the same failure evidence repeated.";
                        if (settings.EnableDivineIntervention)
                        {
                            await EscalateAsync(
                                repo,
                                apprentice,
                                apprenticeId,
                                stepIndex,
                                noProgressMessage,
                                alreadyAlerted: false,
                                linkedCts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await FailStepAsync(
                                repo,
                                apprentice,
                                plan,
                                stepIndex,
                                current,
                                noProgressMessage,
                                apprenticeId,
                                linkedCts.Token).ConfigureAwait(false);
                        }

                        return;
                    }

                    string retryMessage = ApprenticeExecutionPolicy.SanitizeOperatorMessage(outcome.ErrorMessage);

                    plan[stepIndex] = current with { Attempts = attempt };

                    apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

                    await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

                    Publish(apprenticeId, new ApprenticeEvent
                    {
                        Type = ApprenticeEventType.StepRetrying,
                        ApprenticeId = apprenticeId,
                        Timestamp = DateTimeOffset.UtcNow,
                        StepIndex = current.Index,
                        Attempt = attempt,
                        BackoffMs = 0,
                        Error = retryMessage,
                    });

                    attempt++;

                }

                if (outcome is null || outcome.StepFailed)
                {

                    return;

                }

                if (outcome.SpawnedChildIds.Count > 0)
                {

                    await StampCastSendingsAsync(repo, apprenticeId, outcome.SpawnedChildIds, linkedCts.Token)
                        .ConfigureAwait(false);

                }

                DateTimeOffset stepStarted = outcome.StepStarted;

                long durationMs = (long)(DateTimeOffset.UtcNow - stepStarted).TotalMilliseconds;

                if (settings.EnableShiftingFate)
                {

                    plan = await AttemptShiftingFateAsync(
                        repo,
                        intelligence,
                        apprentice,
                        plan,
                        stepIndex,
                        apprenticeId,
                        linkedCts.Token).ConfigureAwait(false);

                }

                apprentice = await CompleteStepAsync(
                    repo,
                    apprentice,
                    plan,
                    stepIndex,
                    outcome.ResultText ?? string.Empty,
                    durationMs,
                    apprenticeId,
                    linkedCts.Token).ConfigureAwait(false);

                plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

            }

            apprentice.Status = ApprenticeStatus.Completed.ToString();

            apprentice.ErrorMessage = null;

            await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

            long totalDurationMs = (long)(DateTimeOffset.UtcNow - runStarted).TotalMilliseconds;

            Publish(apprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.ApprenticeCompleted,
                ApprenticeId = apprenticeId,
                Timestamp = DateTimeOffset.UtcNow,
                Summary = $"Completed {plan.Count} steps.",
                TotalDurationMs = totalDurationMs,
            });
        }
        catch (OperationCanceledException)
        {

            // Fix 4: persist an intermediate status so a later resume continues from
            // the checkpoint rather than re-running the in-progress step (which would
            // duplicate non-idempotent tool side effects). If the cancel came from
            // CancelAsync, the status is already Cancelled — do not overwrite it. If
            // the cancel came from host shutdown (StopAsync), the linked CTS is
            // cancelled but no status was set, so persist Paused. Use a fresh scope
            // and a non-cancellable token (the linked CTS is cancelled).
            //
            // Generation guard: only mutate status if this run still owns the execution
            // generation. A newer Resume increments the generation; a late cancel handler
            // must not clobber Running back to Paused.

            if (!OwnsExecutionGeneration(apprenticeId, generation))
            {

                return;

            }

            try
            {

                await using AsyncServiceScope cancelScope = scopeFactory.CreateAsyncScope();

                IApprenticeRepository cancelRepo = cancelScope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

                Apprentice? cancelApprentice = await cancelRepo
                    .GetByIdAsync(apprenticeId, CancellationToken.None)
                    .ConfigureAwait(false);

                if (cancelApprentice is not null && OwnsExecutionGeneration(apprenticeId, generation))
                {

                    string current = cancelApprentice.Status;

                    bool isRunning = string.Equals(current, ApprenticeStatus.Running.ToString(), StringComparison.Ordinal)
                        || string.Equals(current, ApprenticeStatus.Planning.ToString(), StringComparison.Ordinal);

                    // Only persist if still Running/Planning; leave Cancelled/Failed/
                    // Escalated/Completed (set by CancelAsync/FailApprenticeAsync/etc.) alone.

                    if (isRunning)
                    {

                        cancelApprentice.Status = ApprenticeStatus.Paused.ToString();

                        await cancelRepo.UpdateAsync(cancelApprentice, CancellationToken.None).ConfigureAwait(false);

                        Publish(apprenticeId, new ApprenticeEvent
                        {

                            Type = ApprenticeEventType.ApprenticePaused,

                            ApprenticeId = apprenticeId,

                            Timestamp = DateTimeOffset.UtcNow,

                            AtStep = cancelApprentice.CurrentStep,

                        });

                    }

                }

            }
            catch (Exception inner)
            {

                logger.LogError(inner, "Failed to persist Paused status after Apprentice cancellation.");

            }

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Apprentice {ApprenticeId} failed with an unhandled exception.", apprenticeId);

            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

                Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, CancellationToken.None).ConfigureAwait(false);

                if (apprentice is not null)
                {
                    await FailApprenticeAsync(repo, apprentice, ex.Message, apprenticeId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception inner)
            {
                logger.LogError(inner, "Failed to persist Apprentice failure state.");
            }
        }
    }

    private async Task FailApprenticeAsync(
        IApprenticeRepository repo,
        Apprentice apprentice,
        string errorMessage,
        Guid apprenticeId,
        CancellationToken cancellationToken)
    {
        string sanitized = ApprenticeExecutionPolicy.SanitizeOperatorMessage(errorMessage);

        apprentice.Status = ApprenticeStatus.Failed.ToString();

        apprentice.ErrorMessage = sanitized;

        await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.ApprenticeFailed,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            Error = sanitized,
        });
    }

    private async Task FailStepAsync(
        IApprenticeRepository repo,
        Apprentice apprentice,
        List<PlanStep> plan,
        int stepIndex,
        PlanStep current,
        string errorMessage,
        Guid apprenticeId,
        CancellationToken cancellationToken)
    {
        string sanitized = ApprenticeExecutionPolicy.SanitizeOperatorMessage(errorMessage);

        plan[stepIndex] = current with
        {
            Status = "failed",
            CompletedAt = DateTimeOffset.UtcNow,
            Result = sanitized,
        };

        apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

        apprentice.Status = ApprenticeStatus.Failed.ToString();

        apprentice.ErrorMessage = sanitized;

        await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.StepFailed,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            StepIndex = current.Index,
            Error = sanitized,
        });

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.ApprenticeFailed,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            Error = sanitized,
        });
    }

    private async Task EscalateAsync(
        IApprenticeRepository repo,
        Apprentice apprentice,
        Guid apprenticeId,
        int stepIndex,
        string reason,
        bool alreadyAlerted,
        CancellationToken cancellationToken)
    {
        string sanitized = ApprenticeExecutionPolicy.SanitizeOperatorMessage(reason);

        apprentice.Status = ApprenticeStatus.Escalated.ToString();

        apprentice.ErrorMessage = sanitized;

        ApprenticeCheckpoint? existing = ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData);

        apprentice.CheckpointData = ApprenticeRepository.SerializeCheckpoint(RebaseCheckpoint(existing) with
        {
            CurrentStep = apprentice.CurrentStep,
            Timestamp = DateTimeOffset.UtcNow,
            EscalationReason = sanitized,
        });

        await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.ApprenticeEscalated,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            StepIndex = stepIndex < ApprenticeRepository.DeserializePlan(apprentice.Plan).Count
                ? ApprenticeRepository.DeserializePlan(apprentice.Plan)[stepIndex].Index
                : stepIndex + 1,
            Error = sanitized,
        });

        if (!alreadyAlerted)
        {

            await DispatchEscalationAlertAsync(apprentice, sanitized, cancellationToken).ConfigureAwait(false);

        }

    }

    private async Task DispatchEscalationAlertAsync(
        Apprentice apprentice,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            ICommLinkDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<ICommLinkDispatcher>();

            CommLinkMessage message = new(
                $"Apprentice '{apprentice.Name}' requires Divine Intervention",
                reason,
                CommLinkSeverity.Critical,
                "apprentice-escalation");

            Result<CommLinkDeliveryResult> dispatch = await dispatcher
                .DispatchAsync(message, cancellationToken)
                .ConfigureAwait(false);

            if (dispatch.IsFailure)
            {

                logger.LogWarning(
                    "Comm Link dispatch failed for Apprentice {ApprenticeId}: {Message}",
                    apprentice.Id,
                    dispatch.Error.Message);

            }

        }
        catch (Exception ex)
        {

            logger.LogError(ex, "Comm Link dispatch threw for Apprentice {ApprenticeId}.", apprentice.Id);

        }

    }

    private async Task<List<PlanStep>> AttemptShiftingFateAsync(
        IApprenticeRepository repo,
        IArcanumIntelligenceProvider intelligence,
        Apprentice apprentice,
        List<PlanStep> plan,
        int completedStepIndex,
        Guid apprenticeId,
        CancellationToken cancellationToken)
    {
        try
        {

            string weavePrompt = ApprenticePromptBuilder.BuildWeaveEvaluationPrompt(
                apprentice,
                plan,
                completedStepIndex);

            PingRequest weaveRequest = new(
                Prompt: weavePrompt,
                Model: ResolveModel(),
                WorkingDirectory: apprentice.WorkspacePath,
                UnattendedMode: true,
                SkipSpellRouting: true);

            Result<PromptTurnResult> weaveResult = await intelligence
                .ExecutePromptAsync(weaveRequest, ArcanumInvocationContext.None, cancellationToken)
                .ConfigureAwait(false);

            if (weaveResult.IsFailure)
            {

                logger.LogWarning(
                    "Shifting Fate evaluation failed for Apprentice {ApprenticeId}: {Message}",
                    apprenticeId,
                    weaveResult.Error.Message);

                return plan;

            }

            if (!ApprenticePlanParser.TryParseRevisedPlan(
                    weaveResult.Value.Text,
                    out List<PlanStep>? revisedTail)
                || revisedTail is null)
            {
                return plan;

            }

            List<PlanStep> merged = ApprenticeExecutionPolicy.MergePlanTail(
                plan,
                completedStepIndex + 1,
                revisedTail);

            if (merged.SequenceEqual(plan))
            {
                return plan;
            }

            apprentice.Plan = ApprenticeRepository.SerializePlan(merged);

            await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

            Publish(apprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.PlanRevised,
                ApprenticeId = apprenticeId,
                Timestamp = DateTimeOffset.UtcNow,
                Plan = merged,
                AtStep = completedStepIndex + 1,
            });

            return merged;

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Shifting Fate evaluation threw for Apprentice {ApprenticeId}.", apprenticeId);

            return plan;

        }

    }

    private async Task<Apprentice> CompleteStepAsync(
        IApprenticeRepository repo,
        Apprentice apprentice,
        List<PlanStep> plan,
        int stepIndex,
        string stepResultText,
        long durationMs,
        Guid apprenticeId,
        CancellationToken cancellationToken)
    {
        Apprentice? fresh = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (fresh is null)
        {

            return apprentice;

        }

        apprentice = fresh;

        plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

        if (stepIndex >= plan.Count)
        {

            return apprentice;

        }

        PlanStep current = plan[stepIndex];

        plan[stepIndex] = current with
        {
            Status = "completed",
            CompletedAt = DateTimeOffset.UtcNow,
            Result = stepResultText,
        };

        apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

        apprentice.CurrentStep = stepIndex + 1;

        ApprenticeCheckpoint? existing = ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData);

        apprentice.CheckpointData = ApprenticeRepository.SerializeCheckpoint(RebaseCheckpoint(existing) with
        {
            CurrentStep = apprentice.CurrentStep,
            Timestamp = DateTimeOffset.UtcNow,
            DmGuidance = null,
        });

        await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.StepCompleted,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            StepIndex = current.Index,
            Result = stepResultText,
            DurationMs = durationMs,
        });

        return apprentice;

    }

    private static int ComputeParallelGroupEnd(IReadOnlyList<PlanStep> plan, int start)
    {
        if (start >= plan.Count || !plan[start].IsParallel)
        {

            return start + 1;

        }

        int end = start;

        while (end < plan.Count && plan[end].IsParallel)
        {

            end++;

        }

        return end;

    }

    private async Task<bool> ExecuteSimulacrumGroupAsync(
        IApprenticeRepository repo,
        IArcanumIntelligenceProvider intelligence,
        Apprentice apprentice,
        List<PlanStep> plan,
        int groupStart,
        int groupEnd,
        ApprenticeSettings settings,
        Guid apprenticeId,
        CancellationTokenSource linkedCts)
    {
        DateTimeOffset groupStarted = DateTimeOffset.UtcNow;

        for (int i = groupStart; i < groupEnd; i++)
        {

            plan[i] = plan[i] with
            {
                Status = "in_progress",
                StartedAt = groupStarted,
                Attempts = 0,
            };

        }

        apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

        await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.SimulacrumStarted,
            ApprenticeId = apprenticeId,
            Timestamp = groupStarted,
            StepIndex = plan[groupStart].Index,
            Summary = $"Simulacrum: {groupEnd - groupStart} parallel steps.",
        });

        for (int i = groupStart; i < groupEnd; i++)
        {

            Publish(apprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.StepStarted,
                ApprenticeId = apprenticeId,
                Timestamp = groupStarted,
                StepIndex = plan[i].Index,
                Description = plan[i].Description,
            });

        }

        int maxConcurrentBranches = ArcanumSettingClamps.MaxConcurrentApprenticeBranches(
            optionsMonitor.CurrentValue.Execution.MaxConcurrentApprenticeBranches);
        using SemaphoreSlim gate = new(maxConcurrentBranches, maxConcurrentBranches);

        Apprentice snapshot = apprentice;

        List<PlanStep> planSnapshot = plan;

        List<Task<SingleStepResult>> branchTasks = new(groupEnd - groupStart);

        for (int i = groupStart; i < groupEnd; i++)
        {

            int branchIndex = i;

            branchTasks.Add(RunSimulacrumBranchAsync(
                gate,
                snapshot,
                planSnapshot,
                branchIndex,
                settings,
                apprenticeId,
                linkedCts));

        }

        SingleStepResult[] results = await Task.WhenAll(branchTasks).ConfigureAwait(false);

        bool anyPaused = false;

        SingleStepResult? terminal = null;

        SingleStepResult? escalated = null;

        List<Guid> spawned = [];

        foreach (SingleStepResult branch in results)
        {

            foreach (Guid childId in branch.SpawnedChildIds)
            {

                spawned.Add(childId);

            }

            if (branch.Kind == StepResultKind.PausedOrCancelled)
            {

                anyPaused = true;

            }
            else if (branch.Kind == StepResultKind.Terminal)
            {

                terminal ??= branch;

            }
            else if (branch.Kind == StepResultKind.Escalated)
            {

                escalated ??= branch;

            }

        }

        if (anyPaused)
        {

            return false;

        }

        Apprentice? fresh = await repo.GetByIdAsync(apprenticeId, linkedCts.Token).ConfigureAwait(false);

        if (fresh is null)
        {

            return false;

        }

        if (string.Equals(fresh.Status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal)
            || string.Equals(fresh.Status, ApprenticeStatus.Cancelled.ToString(), StringComparison.Ordinal)
            || ApprenticeExecutionPolicy.IsEscalatedStatus(fresh.Status))
        {

            return false;

        }

        apprentice = fresh;

        plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

        if (terminal is not null)
        {

            if (terminal.StepIndex < plan.Count)
            {

                await FailStepAsync(
                    repo,
                    apprentice,
                    plan,
                    terminal.StepIndex,
                    plan[terminal.StepIndex],
                    terminal.ErrorMessage ?? "Step execution failed.",
                    apprenticeId,
                    linkedCts.Token).ConfigureAwait(false);

            }
            else
            {

                await FailApprenticeAsync(
                    repo,
                    apprentice,
                    terminal.ErrorMessage ?? "Step execution failed.",
                    apprenticeId,
                    linkedCts.Token).ConfigureAwait(false);

            }

            return false;

        }

        if (escalated is not null)
        {

            await EscalateAsync(
                repo,
                apprentice,
                apprenticeId,
                escalated.StepIndex,
                escalated.ErrorMessage ?? "A Simulacrum step requires Divine Intervention.",
                escalated.AlreadyAlerted,
                linkedCts.Token).ConfigureAwait(false);

            return false;

        }

        long groupDurationMs = (long)(DateTimeOffset.UtcNow - groupStarted).TotalMilliseconds;

        foreach (SingleStepResult branch in results)
        {

            if (branch.StepIndex >= plan.Count)
            {

                continue;

            }

            PlanStep done = plan[branch.StepIndex];

            plan[branch.StepIndex] = done with
            {
                Status = "completed",
                CompletedAt = DateTimeOffset.UtcNow,
                Result = branch.ResultText ?? string.Empty,
            };

            Publish(apprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.StepCompleted,
                ApprenticeId = apprenticeId,
                Timestamp = DateTimeOffset.UtcNow,
                StepIndex = done.Index,
                Result = branch.ResultText ?? string.Empty,
                DurationMs = groupDurationMs,
            });

        }

        apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

        apprentice.CurrentStep = groupEnd;

        ApprenticeCheckpoint? existing = ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData);

        apprentice.CheckpointData = ApprenticeRepository.SerializeCheckpoint(RebaseCheckpoint(existing) with
        {
            CurrentStep = groupEnd,
            Timestamp = DateTimeOffset.UtcNow,
            DmGuidance = null,
        });

        await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.SimulacrumCompleted,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            AtStep = groupEnd,
            Summary = $"Simulacrum complete: {groupEnd - groupStart} parallel steps.",
            TotalDurationMs = groupDurationMs,
        });

        if (spawned.Count > 0)
        {

            await StampCastSendingsAsync(repo, apprenticeId, spawned, linkedCts.Token).ConfigureAwait(false);

        }

        if (settings.EnableShiftingFate)
        {

            _ = await AttemptShiftingFateAsync(
                repo,
                intelligence,
                apprentice,
                plan,
                groupEnd - 1,
                apprenticeId,
                linkedCts.Token).ConfigureAwait(false);

        }

        return true;

    }

    private async Task<SingleStepResult> RunSimulacrumBranchAsync(
        SemaphoreSlim gate,
        Apprentice snapshot,
        IReadOnlyList<PlanStep> planSnapshot,
        int stepIndex,
        ApprenticeSettings settings,
        Guid apprenticeId,
        CancellationTokenSource linkedCts)
    {
        try
        {

            await gate.WaitAsync(linkedCts.Token).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return new SingleStepResult(stepIndex, StepResultKind.PausedOrCancelled, null, null, false, 0, []);

        }

        try
        {

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IArcanumIntelligenceProvider branchIntelligence =
                scope.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>();

            return await RunStepAttemptsAsync(
                branchIntelligence,
                snapshot,
                planSnapshot,
                stepIndex,
                stateless: true,
                settings,
                apprenticeId,
                linkedCts).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {

            return new SingleStepResult(stepIndex, StepResultKind.PausedOrCancelled, null, null, false, 0, []);

        }
        catch (Exception ex)
        {

            // Fix 3: isolate a single branch fault. A non-cancel exception in one
            // Simulacrum branch must NOT fail the whole Task.WhenAll (which would
            // fault the apprentice after sibling branches may have run tools).
            // Surface the fault as a Terminal SingleStepResult so the post-WhenAll
            // reconciliation handles it alongside paused/advanced branches.

            return new SingleStepResult(
                stepIndex,
                StepResultKind.Terminal,
                null,
                ApprenticeExecutionPolicy.SanitizeOperatorMessage(ex.Message),
                false,
                0,
                []);

        }
        finally
        {

            gate.Release();

        }

    }

    private async Task<SingleStepResult> RunStepAttemptsAsync(
        IArcanumIntelligenceProvider intelligence,
        Apprentice snapshot,
        IReadOnlyList<PlanStep> planSnapshot,
        int stepIndex,
        bool stateless,
        ApprenticeSettings settings,
        Guid apprenticeId,
        CancellationTokenSource linkedCts)
    {
        ApprenticeCheckpoint? checkpoint = ApprenticeRepository.DeserializeCheckpoint(snapshot.CheckpointData);

        StepExecutionOutcome? outcome = null;
        HashSet<StepRecoveryState> observedRecoveryStates = [];
        int attempt = 1;

        while (true)
        {

            linkedCts.Token.ThrowIfCancellationRequested();

            string stepPrompt = ApprenticePromptBuilder.BuildStepExecutionPrompt(
                snapshot,
                planSnapshot,
                stepIndex,
                checkpoint);

            outcome = await ExecuteStepStreamAsync(
                intelligence,
                snapshot,
                stepPrompt,
                linkedCts,
                apprenticeId,
                stateless).ConfigureAwait(false);

            StepFailureKind failureKind = ApprenticeExecutionPolicy.ClassifyStepFailure(
                outcome.StepFailed,
                outcome.EscalationRequested,
                outcome.WardDenied,
                outcome.ForbiddenArtDenied,
                outcome.PauseOrCancel,
                outcome.IsRetryable);

            if (failureKind == StepFailureKind.PausedOrCancelled)
            {

                return new SingleStepResult(stepIndex, StepResultKind.PausedOrCancelled, null, null, false, attempt - 1, outcome.SpawnedChildIds);

            }

            if (failureKind == StepFailureKind.None)
            {

                return new SingleStepResult(stepIndex, StepResultKind.Completed, outcome.ResultText, null, false, attempt - 1, outcome.SpawnedChildIds);

            }

            if (failureKind == StepFailureKind.EscalationRequested)
            {

                return new SingleStepResult(
                    stepIndex,
                    StepResultKind.Escalated,
                    null,
                    outcome.ErrorMessage ?? "The Apprentice petitioned the Dungeon Master.",
                    outcome.AlreadyAlerted,
                    attempt - 1,
                    outcome.SpawnedChildIds);

            }

            if (failureKind == StepFailureKind.Terminal)
            {

                return new SingleStepResult(
                    stepIndex,
                    StepResultKind.Terminal,
                    null,
                    outcome.ErrorMessage ?? "Step execution failed.",
                    false,
                    attempt - 1,
                    outcome.SpawnedChildIds);

            }

            StepRecoveryState recoveryState = BuildStepRecoveryState(outcome);
            if (!observedRecoveryStates.Add(recoveryState))
            {
                const string noProgressMessage =
                    "Step recovery stopped because the same failure evidence repeated.";
                return settings.EnableDivineIntervention
                    ? new SingleStepResult(
                        stepIndex,
                        StepResultKind.Escalated,
                        null,
                        noProgressMessage,
                        false,
                        attempt - 1,
                        outcome.SpawnedChildIds)
                    : new SingleStepResult(
                        stepIndex,
                        StepResultKind.Terminal,
                        null,
                        noProgressMessage,
                        false,
                        attempt - 1,
                        outcome.SpawnedChildIds);

            }

            Publish(apprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.StepRetrying,
                ApprenticeId = apprenticeId,
                Timestamp = DateTimeOffset.UtcNow,
                StepIndex = planSnapshot[stepIndex].Index,
                Attempt = attempt,
                BackoffMs = 0,
                Error = ApprenticeExecutionPolicy.SanitizeOperatorMessage(outcome.ErrorMessage),
            });

            attempt++;

        }

    }

    private static StepRecoveryState BuildStepRecoveryState(StepExecutionOutcome outcome) =>
        new(
            outcome.ErrorMessage ?? string.Empty,
            outcome.ResultText ?? string.Empty,
            string.Join(
                ",",
                outcome.SpawnedChildIds
                    .Order()
                    .Select(static id => id.ToString("N"))));

    private readonly record struct StepRecoveryState(
        string ErrorMessage,
        string ResultText,
        string SpawnedChildIds);

    private enum StepResultKind
    {

        Completed,

        Terminal,

        Escalated,

        PausedOrCancelled,

    }

    private sealed record SingleStepResult(
        int StepIndex,
        StepResultKind Kind,
        string? ResultText,
        string? ErrorMessage,
        bool AlreadyAlerted,
        int Attempts,
        IReadOnlyList<Guid> SpawnedChildIds);

    private async Task StampCastSendingsAsync(
        IApprenticeRepository repo,
        Guid parentApprenticeId,
        IReadOnlyList<Guid> childIds,
        CancellationToken cancellationToken)
    {
        // cast_sending builds its ConclaveCastRequest without a delegation chain, so this stamping pass is
        // where a locally cast child inherits the caller's A2A lineage — exactly as it inherits the caller's
        // id. Without it a grandchild of an inbound Sending dispatches with an empty chain and the peer that
        // originated the work cannot recognise itself in it.
        IReadOnlyList<string>? inheritedChain = await ReadDelegationChainAsync(
            repo,
            parentApprenticeId,
            cancellationToken).ConfigureAwait(false);

        foreach (Guid childId in childIds)
        {
            try
            {
                Apprentice? child = await repo.GetByIdAsync(childId, cancellationToken).ConfigureAwait(false);

                if (child is null)
                {

                    continue;

                }

                ApprenticeCheckpoint? existing = ApprenticeRepository.DeserializeCheckpoint(child.CheckpointData);

                child.ParentApprenticeId = parentApprenticeId;

                child.CheckpointData = ApprenticeRepository.SerializeCheckpoint(RebaseCheckpoint(existing) with
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    ParentApprenticeId = parentApprenticeId,
                    DelegationChain = existing?.DelegationChain is { Count: > 0 } own ? own : inheritedChain,
                });

                await repo.UpdateAsync(child, cancellationToken).ConfigureAwait(false);

                Publish(parentApprenticeId, new ApprenticeEvent
                {
                    Type = ApprenticeEventType.CastSent,
                    ApprenticeId = parentApprenticeId,
                    Timestamp = DateTimeOffset.UtcNow,
                    Summary = child.Id.ToString(),
                    Name = child.Name,
                    Goal = child.Goal,
                });

                Result<string> start = await StartAsync(childId, cancellationToken).ConfigureAwait(false);

                if (start.IsFailure)
                {

                    logger.LogInformation(
                        "Cast Sending child {ChildId} was created but not started: {Message}",
                        childId,
                        start.Error.Message);

                }

            }
            catch (OperationCanceledException)
            {

                throw;

            }
            catch (Exception ex)
            {

                logger.LogWarning(ex, "Failed to stamp Cast Sending lineage for child {ChildId}.", childId);

            }

        }

    }

    /// <summary>
    /// Reads an Apprentice's persisted A2A delegation chain, normalising "absent" and "empty" to
    /// <see langword="null"/> so purely local work never has a chain invented for it. Best-effort: a read
    /// failure yields no chain rather than failing the step that is stamping lineage.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ReadDelegationChainAsync(
        IApprenticeRepository repo,
        Guid apprenticeId,
        CancellationToken cancellationToken)
    {

        try
        {

            Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

            ApprenticeCheckpoint? checkpoint = ApprenticeRepository.DeserializeCheckpoint(apprentice?.CheckpointData);

            return checkpoint?.DelegationChain is { Count: > 0 } chain ? chain : null;

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Failed to read the delegation chain for Apprentice {ApprenticeId}.", apprenticeId);

            return null;

        }

    }

    /// <summary>
    /// Parses a <c>dispatch_sending</c> tool result and publishes <c>sendingDispatched</c> followed by
    /// <c>sendingCompleted</c>/<c>sendingFailed</c> on <paramref name="apprenticeId"/>'s Chronicle. Malformed
    /// payloads are ignored (best-effort observability; never fails the step).
    /// </summary>
    private void PublishDispatchSendingEvents(Guid apprenticeId, string resultText)
    {

        foreach (ApprenticeEvent @event in SendingChronicleFrames.Build(apprenticeId, resultText, DateTimeOffset.UtcNow))
        {

            Publish(apprenticeId, @event);

        }

    }

    private static bool TryParseCastSendingChildId(string resultText, out Guid childId)
    {
        childId = Guid.Empty;

        try
        {
            CastSendingResultWire? payload = System.Text.Json.JsonSerializer.Deserialize(
                resultText.Trim(),
                McpJsonSerializerContext.Default.CastSendingResultWire);

            if (payload is not null && payload.ChildApprenticeId != Guid.Empty)
            {

                childId = payload.ChildApprenticeId;

                return true;

            }

        }
        catch (System.Text.Json.JsonException)
        {

        }

        return false;

    }

    /// <summary>
    /// Fail-closed parse of <c>petition_dungeon_master</c> structured result.
    /// Only an explicit <c>notificationStatus: "delivered"</c> counts as already alerted.
    /// </summary>
    private static bool IsPetitionNotificationDelivered(string? resultText)
    {

        if (string.IsNullOrWhiteSpace(resultText))
        {

            return false;

        }

        try
        {

            PetitionDungeonMasterResultWire? payload = System.Text.Json.JsonSerializer.Deserialize(
                resultText.Trim(),
                McpJsonSerializerContext.Default.PetitionDungeonMasterResultWire);

            return payload is not null
                && string.Equals(payload.NotificationStatus, "delivered", StringComparison.Ordinal);

        }
        catch (System.Text.Json.JsonException)
        {

            return false;

        }

    }

    private async Task<StepExecutionOutcome> ExecuteStepStreamAsync(
        IArcanumIntelligenceProvider intelligence,
        Apprentice apprentice,
        string stepPrompt,
        CancellationTokenSource linkedCts,
        Guid apprenticeId,
        bool stateless = false)
    {
        DateTimeOffset stepStarted = DateTimeOffset.UtcNow;

        string stepResultText = string.Empty;

        bool stepFailed = false;

        bool escalationRequested = false;

        bool wardDenied = false;

        bool forbiddenArtDenied = false;

        bool pauseOrCancel = false;

        bool alreadyAlerted = false;

        string? stepError = null;

        string? escalationReason = null;

        // Pending petition ToolCalls awaiting ToolResult/ToolError, keyed by CallId.
        HashSet<string> pendingPetitionCallIds = new(StringComparer.Ordinal);

        List<Guid> spawnedChildIds = [];

        // Names the Apprentice behind every internal MCP tool call this step makes. dispatch_sending
        // needs it to extend the inherited A2A delegation chain rather than restarting it (issue #59)
        // and to publish live sendingProgress frames onto this Apprentice's Chronicle (issue #61).
        using IDisposable apprenticeToolScope = ApprenticeToolInvocationAmbient.Begin(
            new ApprenticeToolInvocationContext(
                apprenticeId,
                ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData)?.DelegationChain ?? []));

        try
        {

            PingRequest stepRequest = stateless
                ? new PingRequest(
                    Prompt: stepPrompt,
                    Model: ResolveModel(),
                    WorkingDirectory: apprentice.WorkspacePath,
                    UnattendedMode: true,
                    SkipSpellRouting: true,
                    StatelessMessages: [new CoreChatMessage("user", stepPrompt)])
                : new PingRequest(
                    Prompt: stepPrompt,
                    Model: ResolveModel(),
                    WorkingDirectory: apprentice.WorkspacePath,
                    SessionId: apprentice.SessionId,
                    UnattendedMode: true,
                    SkipSpellRouting: true);

            await foreach (IntelligenceEvent frame in intelligence
                .StreamPromptAsync(stepRequest, ArcanumInvocationContext.None, linkedCts.Token)
                .ConfigureAwait(false))
            {

                ApprenticeStreamFrameDisposition disposition =
                    ApprenticeStreamFramePolicy.Classify(frame.Type);
                if (disposition == ApprenticeStreamFrameDisposition.Ignore)
                {
                    // Reasoning and unknown future frames remain ephemeral and never enter the
                    // Apprentice result, Chronicle, checkpoint, or subsequent Master context.
                    continue;
                }

                if (IsPassThrough(frame.Type))
                {

                    Publish(apprenticeId, new ApprenticeEvent
                    {
                        Type = MapPassThrough(frame.Type),
                        ApprenticeId = apprenticeId,
                        Timestamp = frame.Timestamp ?? DateTimeOffset.UtcNow,
                        WizardEvent = frame,
                    });

                }

                if (frame.Type == IntelligenceEventType.ToolCall
                    && string.Equals(
                        frame.ToolCall?.Name,
                        ApprenticeExecutionPolicy.PetitionDungeonMasterToolName,
                        StringComparison.Ordinal))
                {

                    escalationRequested = true;

                    // Do not assume delivery on ToolCall — wait for ToolResult by CallId.
                    if (!string.IsNullOrWhiteSpace(frame.ToolCall?.CallId))
                    {

                        pendingPetitionCallIds.Add(frame.ToolCall.CallId);

                    }

                    escalationReason = TryExtractPetitionReason(frame.ToolCall?.ArgumentsJson);

                }

                if (frame.Type == IntelligenceEventType.ToolResult
                    && frame.ToolCall is { CallId: { Length: > 0 } resultCallId }
                    && pendingPetitionCallIds.Remove(resultCallId))
                {

                    // Fail closed: only an explicit "delivered" status counts as already alerted.
                    if (IsPetitionNotificationDelivered(frame.Data))
                    {

                        alreadyAlerted = true;

                    }

                }

                if (frame.Type == IntelligenceEventType.ToolError
                    && frame.ToolCall is { CallId: { Length: > 0 } errorCallId }
                    && pendingPetitionCallIds.Remove(errorCallId))
                {

                    // ToolError for a pending petition → not alerted (alreadyAlerted unchanged).

                }

                if (frame.Type == IntelligenceEventType.Result && !string.IsNullOrWhiteSpace(frame.Message))
                {

                    stepResultText = frame.Message;

                }

                if (frame.Type == IntelligenceEventType.Error)
                {

                    stepFailed = true;

                    stepError = frame.Message;

                    // Stream Error: any still-pending petitions are not alerted.
                    pendingPetitionCallIds.Clear();

                }

                if (frame.Type == IntelligenceEventType.ToolResult
                    && frame.ToolDenied)
                {

                    stepFailed = true;

                    forbiddenArtDenied = true;

                    stepError = frame.Data;

                }

                if (frame.Type == IntelligenceEventType.ToolResult
                    && string.Equals(frame.ToolCall?.Name, "cast_sending", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(frame.Data)
                    && TryParseCastSendingChildId(frame.Data, out Guid spawnedChildId))
                {

                    spawnedChildIds.Add(spawnedChildId);

                }

                // dispatch_sending (Archmage Client) is blocking: by the time this ToolResult frame
                // arrives, the exchange with the remote A2A agent has already fully completed or
                // failed. Unlike cast_sending there is no child Apprentice to stamp/start afterward,
                // so the Chronicle events are published immediately here — apprenticeId (the currently
                // running Apprentice) is already in scope, which is exactly the piece of context the
                // dispatch_sending MCP tool itself cannot see (it is workspace-scoped, not
                // Apprentice-scoped; see docs/Arcanum.DESIGN.md §5.7.1.
                if (frame.Type == IntelligenceEventType.ToolResult
                    && string.Equals(frame.ToolCall?.Name, "dispatch_sending", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(frame.Data))
                {

                    PublishDispatchSendingEvents(apprenticeId, frame.Data);

                }

                if (frame.Type == IntelligenceEventType.WardResolved && frame.WardAllowed == false)
                {

                    stepFailed = true;

                    wardDenied = true;

                    stepError = frame.WardReason ?? "Ward denied.";

                }

            }

        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {

            pauseOrCancel = true;

        }

        if (escalationRequested)
        {

            stepFailed = true;

            stepError = escalationReason ?? "The Apprentice petitioned the Dungeon Master for guidance.";

        }

        return new StepExecutionOutcome(
            StepFailed: stepFailed,
            EscalationRequested: escalationRequested,
            WardDenied: wardDenied,
            ForbiddenArtDenied: forbiddenArtDenied,
            PauseOrCancel: pauseOrCancel,
            IsRetryable: stepFailed && !wardDenied && !forbiddenArtDenied && !escalationRequested,
            ResultText: stepResultText,
            ErrorMessage: stepError,
            AlreadyAlerted: alreadyAlerted,
            StepStarted: stepStarted,
            SpawnedChildIds: spawnedChildIds);

    }

    private static string? TryExtractPetitionReason(string? argumentsJson)
    {

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {

            return null;

        }

        try
        {

            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(argumentsJson);

            if (doc.RootElement.TryGetProperty("reason", out System.Text.Json.JsonElement reason)
                && reason.ValueKind == System.Text.Json.JsonValueKind.String)
            {

                return reason.GetString();

            }

        }
        catch (System.Text.Json.JsonException)
        {

        }

        return null;

    }

    private static ApprenticeDetailDto ToDetailDto(Apprentice apprentice)
    {
        List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

        ApprenticeCheckpoint? checkpoint = ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData);

        return new ApprenticeDetailDto(
            apprentice.Id,
            apprentice.CampaignId,
            apprentice.ParentApprenticeId ?? checkpoint?.ParentApprenticeId,
            apprentice.Name,
            apprentice.Goal,
            plan,
            apprentice.CurrentStep,
            apprentice.Status,
            apprentice.SessionId,
            apprentice.WorkspacePath,
            checkpoint,
            apprentice.ErrorMessage,
            apprentice.CreatedAt,
            apprentice.UpdatedAt);
    }

    private sealed record StepExecutionOutcome(
        bool StepFailed,
        bool EscalationRequested,
        bool WardDenied,
        bool ForbiddenArtDenied,
        bool PauseOrCancel,
        bool IsRetryable,
        string? ResultText,
        string? ErrorMessage,
        bool AlreadyAlerted,
        DateTimeOffset StepStarted,
        IReadOnlyList<Guid> SpawnedChildIds);

    private void Publish(Guid apprenticeId, ApprenticeEvent @event) =>
        chronicleHub.Publish(apprenticeId, @event);

    private ApprenticeSettings GetApprenticeSettings() =>
        optionsMonitor.CurrentValue.ResolveApprentices();

    private string? ResolveModel()
    {
        ArcanumSettings arc = optionsMonitor.CurrentValue;

        if (!string.IsNullOrWhiteSpace(arc.DefaultModel))
        {
            return arc.DefaultModel.Trim();
        }

        return null;
    }

    private static bool CanStart(string status) =>
        string.Equals(status, ApprenticeStatus.Idle.ToString(), StringComparison.Ordinal)
        || string.Equals(status, ApprenticeStatus.Failed.ToString(), StringComparison.Ordinal)
        || string.Equals(status, ApprenticeStatus.Completed.ToString(), StringComparison.Ordinal)
        || string.Equals(status, ApprenticeStatus.Cancelled.ToString(), StringComparison.Ordinal);

    private static bool IsPausable(string status) =>
        string.Equals(status, ApprenticeStatus.Running.ToString(), StringComparison.Ordinal)
        || string.Equals(status, ApprenticeStatus.Planning.ToString(), StringComparison.Ordinal);

    private static bool IsCancellable(string status) =>
        IsPausable(status)
        || string.Equals(status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal)
        || ApprenticeExecutionPolicy.IsEscalatedStatus(status);

    private static bool IsPassThrough(IntelligenceEventType type) =>
        type is IntelligenceEventType.ToolCall
            or IntelligenceEventType.ToolResult
            or IntelligenceEventType.Warded
            or IntelligenceEventType.WardResolved;

    private static ApprenticeEventType MapPassThrough(IntelligenceEventType type) => type switch
    {
        IntelligenceEventType.ToolCall => ApprenticeEventType.ToolCall,
        IntelligenceEventType.ToolResult => ApprenticeEventType.ToolResult,
        IntelligenceEventType.Warded => ApprenticeEventType.Warded,
        IntelligenceEventType.WardResolved => ApprenticeEventType.WardResolved,
        _ => ApprenticeEventType.ToolCall,
    };

}
