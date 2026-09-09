using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
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
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: IHostedService + IApprenticeRuntime long-running orchestration
internal sealed class ApprenticeService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ChronicleHub chronicleHub,
    ILogger<ApprenticeService> logger,
    IGrimoireConnectionAdmissionGate admissionGate,
    IApprenticeExecutionCapacity? executionCapacity = null) : BackgroundService, IApprenticeRuntime
{
    private readonly ConcurrentDictionary<Guid, ExecutionLease> _executionTokens = new();

    private readonly ConcurrentDictionary<Guid, long> _executionGenerations = new();

    private readonly ConcurrentDictionary<Guid, Task> _activeTasks = new();

    private readonly ConcurrentQueue<Guid> _pendingStarts = new();

    private readonly ConcurrentDictionary<Guid, byte> _pendingStartIds = new();

    private readonly ConcurrentDictionary<Guid, byte> _freshStartIds = new();

    private readonly Lock _pendingStartsLock = new();

    private readonly IApprenticeExecutionCapacity _concurrencyGate =
        executionCapacity ?? new DefaultApprenticeExecutionCapacity();

    private readonly ConcurrentDictionary<Guid, IDisposable> _executionLeases = new();

    private readonly Dictionary<Guid, ExecutionReservation> _executionReservations = [];

    private readonly Dictionary<Guid, StartHandoffReservation> _startHandoffReservations = [];

    // Linearizes the reservation-to-worker handoff with StopAsync so shutdown observes either
    // the exact running task or a reservation whose drain completes only after handoff cleanup.
    private readonly Lock _executionLifecycleLock = new();

    private bool _stopping;

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
        ExecutionLease[] executions;

        Task[] tasks;

        lock (_executionLifecycleLock)
        {
            _stopping = true;

            foreach (ExecutionReservation reservation in _executionReservations.Values)
            {
                reservation.StopRequested = true;
            }
            executions = [.. _executionTokens.Values];

            tasks =
            [
                .. _startHandoffReservations.Values
                    .Select(static reservation => reservation.Drained.Task),
                .. _executionReservations.Values
                    .Select(static reservation => reservation.Drained.Task),
                .. _activeTasks
                    .Where(pair => !_executionReservations.ContainsKey(pair.Key)
                        && !pair.Value.IsCompleted)
                    .Select(static pair => pair.Value),
            ];
        }

        foreach (ExecutionLease execution in executions)
        {
            try
            {
                await execution.Cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Apprentice shutdown drain failed.");
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

        if (!TryReserveStartHandoff(apprenticeId, out Result<string>? handoffFailure))
        {
            return handoffFailure!;
        }

        bool acquiredExecutionSlot = false;

        bool queued = false;

        try
        {
            if (TryAcquireExecutionSlot(
                    apprenticeId,
                    queueOnCapacity: false,
                    out bool _,
                    out Result<string>? firstCapacityFailure))
            {
                acquiredExecutionSlot = true;
            }
            else if (!string.Equals(
                firstCapacityFailure?.Error.Code,
                ErrorCodes.Apprentice.MaxReached,
                StringComparison.Ordinal))
            {
                return firstCapacityFailure!;
            }
            apprentice.Status = ApprenticeStatus.Planning.ToString();

            await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

            _freshStartIds.TryAdd(apprenticeId, 0);

            if (!acquiredExecutionSlot
                && !TryAcquireExecutionSlot(
                    apprenticeId,
                    queueOnCapacity: true,
                    out queued,
                    out Result<string>? capacityFailure))
            {
                _freshStartIds.TryRemove(apprenticeId, out _);

                if (string.Equals(
                    capacityFailure?.Error.Code,
                    ErrorCodes.Apprentice.Disabled,
                    StringComparison.Ordinal))
                {
                    apprentice.Status = ApprenticeStatus.Paused.ToString();

                    await repo.UpdateAsync(
                        apprentice,
                        CancellationToken.None).ConfigureAwait(false);
                }

                return capacityFailure!;
            }
            acquiredExecutionSlot = !queued;

            CompleteStartHandoff(apprenticeId);

            if (queued)
            {
                // The exact pending identity is durably Planning before publication. Capacity
                // release and Stop cannot pass the lifecycle critical section before it appears.

                return Result<string>.Success(apprenticeId.ToString());
            }
            BeginExecutionTask(apprenticeId);

            return Result<string>.Success(apprenticeId.ToString());
        }
        catch
        {
            _freshStartIds.TryRemove(apprenticeId, out _);

            if (queued)
            {
                _ = RemovePendingStart(apprenticeId);
            }

            if (acquiredExecutionSlot)
            {
                ReleaseAcquiredExecutionSlot(apprenticeId);
            }

            throw;
        }
        finally
        {
            CompleteStartHandoff(apprenticeId);
        }
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

        try
        {
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
        catch
        {
            ReleaseAcquiredExecutionSlot(apprenticeId);

            throw;
        }
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
        IGrimoireWorkLease? admitted;

        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();

            long observedGeneration = admissionGate.CurrentGeneration;

            if (admissionGate.TryAcquireWorkLease(
                    GrimoireWorkKind.ApprenticeExecution,
                    out admitted))
            {
                break;
            }

            long waitAfterGeneration = observedGeneration > 0
                ? observedGeneration - 1
                : 0;

            _ = await admissionGate.WaitForNextOpenGenerationAsync(
                waitAfterGeneration,
                stoppingToken).ConfigureAwait(false);
        }

        await using IGrimoireWorkLease lease = admitted!;

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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Apprentice crash recovery failed.");
        }
    }

    private bool TryReserveStartHandoff(
        Guid apprenticeId,
        out Result<string>? failure)
    {
        failure = null;

        lock (_executionLifecycleLock)
        {
            if (_stopping)
            {
                failure = Result<string>.Failure(
                    new Error(
                        ErrorCodes.Apprentice.Disabled,
                        "Apprentice orchestration is stopping."));

                return false;
            }

            lock (_pendingStartsLock)
            {
                if (_startHandoffReservations.ContainsKey(apprenticeId)
                    || _activeTasks.ContainsKey(apprenticeId)
                    || _pendingStartIds.ContainsKey(apprenticeId))
                {
                    failure = Result<string>.Failure(
                        new Error(
                            ErrorCodes.Apprentice.AlreadyRunning,
                            "Apprentice is already running or not in a startable state."));

                    return false;
                }
                _startHandoffReservations.Add(
                    apprenticeId,
                    new StartHandoffReservation());
            }

            return true;
        }
    }

    private void CompleteStartHandoff(Guid apprenticeId)
    {
        StartHandoffReservation? reservation = null;

        lock (_executionLifecycleLock)
        {
            if (_startHandoffReservations.Remove(
                apprenticeId,
                out StartHandoffReservation? removed))
            {
                reservation = removed;
            }
        }
        reservation?.Drained.TrySetResult();
    }

    private bool TryAcquireExecutionSlot(
        Guid apprenticeId,
        bool queueOnCapacity,
        out bool queued,
        out Result<string>? failure)
    {
        failure = null;

        queued = false;

        ApprenticeSettings settings = GetApprenticeSettings();

        int maxConcurrent = ArcanumSettingClamps.MaxConcurrentApprentices(
            settings.MaxConcurrentApprentices);

        lock (_executionLifecycleLock)
        {
            if (_stopping)
            {
                failure = Result<string>.Failure(
                    new Error(
                        ErrorCodes.Apprentice.Disabled,
                        "Apprentice orchestration is stopping."));

                return false;
            }

            if (!_activeTasks.TryAdd(apprenticeId, Task.CompletedTask))
            {
                failure = Result<string>.Failure(
                    new Error(
                        ErrorCodes.Apprentice.AlreadyRunning,
                        "Apprentice is already running or not in a startable state."));

                return false;
            }

            if (_concurrencyGate.TryAcquire(maxConcurrent, out IDisposable? lease))
            {
                long generation = _executionGenerations.AddOrUpdate(
                    apprenticeId,
                    1L,
                    static (_, current) => current + 1);

                _executionLeases[apprenticeId] = lease!;

                _executionReservations[apprenticeId] = new ExecutionReservation(generation);

                return true;
            }
            _activeTasks.TryRemove(apprenticeId, out _);

            if (queueOnCapacity)
            {
                lock (_pendingStartsLock)
                {
                    if (_pendingStartIds.TryAdd(apprenticeId, 0))
                    {
                        _pendingStarts.Enqueue(apprenticeId);
                    }
                }
                queued = true;

                return true;
            }
            failure = Result<string>.Failure(
                new Error(
                    ErrorCodes.Apprentice.MaxReached,
                    "Maximum concurrent Apprentices reached."));

            return false;
        }
    }

    /// <summary>
    /// Releases a slot acquired via <see cref="TryAcquireExecutionSlot"/> before
    /// <see cref="BeginExecutionTask"/> runs (e.g. InterveneAsync persistence failure).
    /// </summary>
    private void ReleaseAcquiredExecutionSlot(Guid apprenticeId)
    {
        IDisposable? concurrencyLease = null;

        ExecutionLease? execution = null;

        ExecutionReservation? reservation = null;

        bool dequeueNext;

        lock (_executionLifecycleLock)
        {
            _activeTasks.TryRemove(apprenticeId, out _);

            _executionTokens.TryRemove(apprenticeId, out execution);

            _executionLeases.TryRemove(apprenticeId, out concurrencyLease);

            if (_executionReservations.Remove(apprenticeId, out ExecutionReservation? removed))
            {
                reservation = removed;
            }
            dequeueNext = !_stopping;
        }
        execution?.Cts.Dispose();

        concurrencyLease?.Dispose();

        reservation?.Drained.TrySetResult();

        if (dequeueNext && concurrencyLease is not null)
        {
            TryDequeuePendingStart();
        }
    }

    private void BeginExecutionTask(Guid apprenticeId)
    {
        Task? task = null;

        long generation = 0;

        lock (_executionLifecycleLock)
        {
            if (!_activeTasks.TryGetValue(apprenticeId, out Task? reservation)
                || !ReferenceEquals(reservation, Task.CompletedTask)
                || !_executionReservations.TryGetValue(
                    apprenticeId,
                    out ExecutionReservation? executionReservation))
            {
                return;
            }
            generation = executionReservation.Generation;

            CancellationTokenSource cancellation = new();

            if (_stopping || executionReservation.StopRequested)
            {
                cancellation.Cancel();
            }
            _executionTokens[apprenticeId] = new ExecutionLease(
                cancellation,
                generation);

            task = Task.Run(async () =>
            {
                // Task.Run may begin before its returned Task is published below. Taking the same
                // lifecycle lock makes publication the first observable event, so even a
                // synchronously finishing run cleans up the exact task stored in _activeTasks.
                lock (_executionLifecycleLock)
                {
                }

                try
                {
                    await RunApprenticeAsync(apprenticeId, generation).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // This wrapper is the task's sole owner. Observe and log the terminal failure
                    // here so no detached continuation or unobserved fault outlives the execution.
                    logger.LogError(
                        exception,
                        "Apprentice run task faulted for {ApprenticeId}.",
                        apprenticeId);
                }
                finally
                {
                    CleanupExecution(apprenticeId, generation, task!);
                }
            });

            _activeTasks[apprenticeId] = task;
        }
    }

    private void CleanupExecution(
        Guid apprenticeId,
        long generation,
        Task completedTask)
    {
        ExecutionLease? execution = null;

        IDisposable? concurrencyLease = null;

        ExecutionReservation? reservation = null;

        bool dequeueNext;

        lock (_executionLifecycleLock)
        {
            if (_activeTasks.TryGetValue(apprenticeId, out Task? active)
                && ReferenceEquals(active, completedTask))
            {
                _activeTasks.TryRemove(
                    KeyValuePair.Create(apprenticeId, active));
            }

            if (_executionTokens.TryGetValue(apprenticeId, out ExecutionLease? current)
                && current.Generation == generation)
            {
                _executionTokens.TryRemove(
                    KeyValuePair.Create(apprenticeId, current));

                execution = current;
            }

            if (OwnsExecutionGeneration(apprenticeId, generation))
            {
                _executionLeases.TryRemove(
                    apprenticeId,
                    out concurrencyLease);
            }

            if (_executionReservations.TryGetValue(
                    apprenticeId,
                    out ExecutionReservation? currentReservation)
                && currentReservation.Generation == generation)
            {
                _executionReservations.Remove(apprenticeId);

                reservation = currentReservation;
            }
            dequeueNext = !_stopping;
        }
        execution?.Cts.Dispose();

        concurrencyLease?.Dispose();

        reservation?.Drained.TrySetResult();

        if (dequeueNext && concurrencyLease is not null)
        {
            TryDequeuePendingStart();
        }
    }

    private bool OwnsExecutionGeneration(Guid apprenticeId, long generation) =>
        _executionGenerations.TryGetValue(apprenticeId, out long current) && current == generation;

    private sealed record ExecutionLease(CancellationTokenSource Cts, long Generation);

    private sealed class ExecutionReservation(long generation)
    {
        internal long Generation { get; } = generation;

        internal TaskCompletionSource Drained { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool StopRequested { get; set; }
    }

    private sealed class StartHandoffReservation
    {
        internal TaskCompletionSource Drained { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void TryDequeuePendingStart()
    {
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

            if (!TryAcquireExecutionSlot(
                    nextId,
                    queueOnCapacity: false,
                    out bool _,
                    out Result<string>? failure))
            {
                if (string.Equals(
                    failure?.Error.Code,
                    ErrorCodes.Apprentice.MaxReached,
                    StringComparison.Ordinal))
                {
                    RequeuePendingStart(nextId);

                    return;
                }

                if (string.Equals(
                    failure?.Error.Code,
                    ErrorCodes.Apprentice.Disabled,
                    StringComparison.Ordinal))
                {
                    RequeuePendingStart(nextId);

                    return;
                }
                continue;
            }
            BeginExecutionTask(nextId);

            return;
        }
    }

    private void RequeuePendingStart(Guid apprenticeId)
    {
        lock (_pendingStartsLock)
        {
            if (_pendingStartIds.TryAdd(apprenticeId, 0))
            {
                _pendingStarts.Enqueue(apprenticeId);
            }
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

        bool ownsExecutionToken = false;

        ExecutionLease execution;

        if (!_executionTokens.TryGetValue(
                apprenticeId,
                out ExecutionLease? registered)
            || registered is null
            || registered.Generation != generation)
        {
            execution = new ExecutionLease(new CancellationTokenSource(), generation);

            _executionTokens[apprenticeId] = execution;

            ownsExecutionToken = true;
        }
        else
        {
            execution = registered;
        }
        CancellationTokenSource linkedCts = execution.Cts;

        ApprenticeExecutionMemory memory = new();

        try
        {
            ApprenticeSettings settings = GetApprenticeSettings();

            if (!settings.Enabled)
            {
                return;
            }

            while (true)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                long observedGeneration = admissionGate.CurrentGeneration;

                if (!admissionGate.TryAcquireWorkLease(
                        GrimoireWorkKind.ApprenticeExecution,
                        out IGrimoireWorkLease? admitted))
                {
                    await WaitForApprenticeAdmissionAsync(
                        observedGeneration,
                        linkedCts.Token).ConfigureAwait(false);

                    continue;
                }
                ApprenticeUnitDisposition disposition;

                // Declaration order is the drain contract: the bounded scope returns every pooled
                // Grimoire resource before the outer work lease tells maintenance this unit is gone.
                await using (IGrimoireWorkLease lease = admitted!)
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                    IApprenticeRepository repo = scope.ServiceProvider
                        .GetRequiredService<IApprenticeRepository>();

                    try
                    {
                        disposition = await ExecuteNextApprenticeUnitAsync(
                            scope.ServiceProvider,
                            repo,
                            lease,
                            apprenticeId,
                            generation,
                            settings,
                            memory,
                            runStarted,
                            linkedCts).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
                    {
                        await PersistPausedIfCurrentAsync(
                            repo,
                            apprenticeId,
                            generation).ConfigureAwait(false);

                        return;
                    }
                    catch (Exception ex)
                    {
                        await PersistFailureIfCurrentAsync(
                            repo,
                            apprenticeId,
                            generation,
                            ex).ConfigureAwait(false);

                        return;
                    }
                }

                if (disposition == ApprenticeUnitDisposition.DeferredForMaintenance)
                {
                    // Refusal keeps this same task, generation, retry memory, and concurrency slot.
                    // Waiting begins only after both scope and work authority have been released.
                    await WaitForApprenticeAdmissionAsync(
                        observedGeneration,
                        linkedCts.Token).ConfigureAwait(false);

                    continue;
                }

                if (disposition == ApprenticeUnitDisposition.Stop)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            await TryPersistPausedAfterCancellationAsync(
                apprenticeId,
                generation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Apprentice {ApprenticeId} failed with an unhandled exception.",
                apprenticeId);

            await TryPersistFailureAfterExceptionAsync(
                apprenticeId,
                generation,
                ex).ConfigureAwait(false);
        }
        finally
        {
            if (ownsExecutionToken
                && _executionTokens.TryRemove(
                    KeyValuePair.Create(apprenticeId, execution)))
            {
                execution.Cts.Dispose();
            }
        }
    }

    private async Task<ApprenticeUnitDisposition> ExecuteNextApprenticeUnitAsync(
        IServiceProvider services,
        IApprenticeRepository repo,
        IGrimoireWorkLease lease,
        Guid apprenticeId,
        long generation,
        ApprenticeSettings settings,
        ApprenticeExecutionMemory memory,
        DateTimeOffset runStarted,
        CancellationTokenSource linkedCts)
    {
        Apprentice? apprentice = await repo
            .GetByIdAsync(apprenticeId, linkedCts.Token)
            .ConfigureAwait(false);

        if (apprentice is null
            || string.Equals(
                apprentice.Status,
                ApprenticeStatus.Cancelled.ToString(),
                StringComparison.Ordinal)
            || string.Equals(
                apprentice.Status,
                ApprenticeStatus.Paused.ToString(),
                StringComparison.Ordinal)
            || ApprenticeExecutionPolicy.IsEscalatedStatus(apprentice.Status))
        {
            return ApprenticeUnitDisposition.Stop;
        }

        List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

        if (plan.Count == 0)
        {
            return await ExecutePlanGenerationUnitAsync(
                services,
                repo,
                lease,
                apprentice,
                apprenticeId,
                generation,
                linkedCts).ConfigureAwait(false);
        }

        if (!string.Equals(
                apprentice.Status,
                ApprenticeStatus.Running.ToString(),
                StringComparison.Ordinal))
        {
            await InitializeKnownPlanAsync(
                repo,
                apprentice,
                apprenticeId,
                linkedCts.Token).ConfigureAwait(false);

            return ApprenticeUnitDisposition.Continue;
        }

        if (apprentice.SessionId is null)
        {
            ICanonicalCampaignContextResolver campaignResolver = services
                .GetRequiredService<ICanonicalCampaignContextResolver>();

            Result<CanonicalCampaignContext> campaign = await campaignResolver
                .ResolveAsync(
                    new CanonicalCampaignResolutionRequest(
                        SessionId: null,
                        ExplicitCampaignId: apprentice.CampaignId,
                        WorkingDirectory: apprentice.WorkspacePath),
                    linkedCts.Token)
                .ConfigureAwait(false);

            if (campaign.IsFailure)
            {
                await FailApprenticeAsync(
                    repo,
                    apprentice,
                    campaign.Error.Message,
                    apprenticeId,
                    linkedCts.Token).ConfigureAwait(false);

                return ApprenticeUnitDisposition.Stop;
            }
            ISessionTurnBeginStore turnBeginStore = services
                .GetRequiredService<ISessionTurnBeginStore>();

            Result<Guid> session = await turnBeginStore
                .CreateBoundSessionAsync(
                    campaign.Value,
                    $"Apprentice {apprentice.Name} begins their quest.",
                    linkedCts.Token)
                .ConfigureAwait(false);

            if (session.IsFailure)
            {
                await FailApprenticeAsync(
                    repo,
                    apprentice,
                    session.Error.Message,
                    apprenticeId,
                    linkedCts.Token).ConfigureAwait(false);

                return ApprenticeUnitDisposition.Stop;
            }
            apprentice.SessionId = session.Value;

            await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

            return ApprenticeUnitDisposition.Continue;
        }

        if (apprentice.CurrentStep >= plan.Count)
        {
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

            return ApprenticeUnitDisposition.Stop;
        }

        int stepIndex = apprentice.CurrentStep;

        int simulacrumGroupEnd = ComputeParallelGroupEnd(plan, stepIndex);

        if (simulacrumGroupEnd - stepIndex > 1)
        {
            memory.SerialAttempt = null;

            return await ExecuteSimulacrumUnitAsync(
                services,
                repo,
                lease,
                apprentice,
                plan,
                stepIndex,
                simulacrumGroupEnd,
                settings,
                apprenticeId,
                generation,
                linkedCts).ConfigureAwait(false);
        }

        if (memory.SerialAttempt is null || memory.SerialAttempt.StepIndex != stepIndex)
        {
            int nextAttempt = Math.Max(1, plan[stepIndex].Attempts + 1);

            memory.SerialAttempt = new SerialAttemptMemory(stepIndex, nextAttempt);
        }

        return await ExecuteSerialAttemptUnitAsync(
            services,
            repo,
            lease,
            apprentice,
            plan,
            settings,
            memory.SerialAttempt,
            apprenticeId,
            generation,
            linkedCts).ConfigureAwait(false);
    }

    private async Task<ApprenticeUnitDisposition> ExecutePlanGenerationUnitAsync(
        IServiceProvider services,
        IApprenticeRepository repo,
        IGrimoireWorkLease lease,
        Apprentice apprentice,
        Guid apprenticeId,
        long generation,
        CancellationTokenSource linkedCts)
    {
        if (!lease.TryBeginExternalEffectGroup(
                out IGrimoireExternalEffectGroup? admittedGroup))
        {
            return ApprenticeUnitDisposition.DeferredForMaintenance;
        }

        await using IGrimoireExternalEffectGroup effectGroup = admittedGroup!;

        // The group begins before any start event/provider call and remains through durable plan
        // and status publication, so maintenance sees either none or the concluded plan unit.
        try
        {
            bool isContinuation =
                string.Equals(
                    apprentice.Status,
                    ApprenticeStatus.Running.ToString(),
                    StringComparison.Ordinal)
                || string.Equals(
                    apprentice.Status,
                    ApprenticeStatus.Paused.ToString(),
                    StringComparison.Ordinal);

            if (!isContinuation)
            {
                bool isFreshStart = _freshStartIds.TryRemove(apprenticeId, out _);

                bool isResumeAfterRestart = !isFreshStart
                    && string.Equals(
                        apprentice.Status,
                        ApprenticeStatus.Planning.ToString(),
                        StringComparison.Ordinal);

                apprentice.Status = ApprenticeStatus.Planning.ToString();

                await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

                Publish(apprenticeId, new ApprenticeEvent
                {
                    Type = isResumeAfterRestart
                        ? ApprenticeEventType.ApprenticeResumed
                        : ApprenticeEventType.ApprenticeStarted,
                    ApprenticeId = apprenticeId,
                    Timestamp = DateTimeOffset.UtcNow,
                    FromStep = isResumeAfterRestart ? apprentice.CurrentStep : null,
                    Name = isResumeAfterRestart ? null : apprentice.Name,
                    Goal = isResumeAfterRestart ? null : apprentice.Goal,
                });
            }
            IArcanumIntelligenceProvider intelligence = services
                .GetRequiredService<IArcanumIntelligenceProvider>();

            string planPrompt = ApprenticePromptBuilder.BuildPlanGenerationPrompt(apprentice);

            PingRequest planRequest = new(
                Prompt: planPrompt,
                Model: ResolveModel(),
                WorkingDirectory: apprentice.WorkspacePath,
                UnattendedMode: true,
                SkipSpellRouting: true);

            Result<PromptTurnResult> planResult = await intelligence
                .ExecutePromptAsync(
                    planRequest,
                    ArcanumInvocationContext.None,
                    linkedCts.Token)
                .ConfigureAwait(false);

            if (planResult.IsFailure)
            {
                await FailApprenticeAsync(
                    repo,
                    apprentice,
                    planResult.Error.Message,
                    apprenticeId,
                    linkedCts.Token).ConfigureAwait(false);

                return ApprenticeUnitDisposition.Stop;
            }

            List<PlanStep> plan = ApprenticePlanParser.ParsePlan(planResult.Value.Text);

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

            return ApprenticeUnitDisposition.Continue;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            await PersistPausedIfCurrentAsync(
                repo,
                apprenticeId,
                generation).ConfigureAwait(false);

            return ApprenticeUnitDisposition.Stop;
        }
        catch (Exception ex)
        {
            await PersistFailureIfCurrentAsync(
                repo,
                apprenticeId,
                generation,
                ex).ConfigureAwait(false);

            return ApprenticeUnitDisposition.Stop;
        }
    }

    private async Task InitializeKnownPlanAsync(
        IApprenticeRepository repo,
        Apprentice apprentice,
        Guid apprenticeId,
        CancellationToken cancellationToken)
    {
        bool isFreshStart = _freshStartIds.TryRemove(apprenticeId, out _);

        bool isResumeAfterRestart = !isFreshStart
            && string.Equals(
                apprentice.Status,
                ApprenticeStatus.Planning.ToString(),
                StringComparison.Ordinal);

        apprentice.Status = ApprenticeStatus.Running.ToString();

        await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = isResumeAfterRestart
                ? ApprenticeEventType.ApprenticeResumed
                : ApprenticeEventType.ApprenticeStarted,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            FromStep = isResumeAfterRestart ? apprentice.CurrentStep : null,
            Name = isResumeAfterRestart ? null : apprentice.Name,
            Goal = isResumeAfterRestart ? null : apprentice.Goal,
        });
    }

    private async Task<ApprenticeUnitDisposition> ExecuteSerialAttemptUnitAsync(
        IServiceProvider services,
        IApprenticeRepository repo,
        IGrimoireWorkLease lease,
        Apprentice apprentice,
        List<PlanStep> plan,
        ApprenticeSettings settings,
        SerialAttemptMemory attemptMemory,
        Guid apprenticeId,
        long generation,
        CancellationTokenSource linkedCts)
    {
        if (!lease.TryBeginExternalEffectGroup(
                out IGrimoireExternalEffectGroup? admittedGroup))
        {
            return ApprenticeUnitDisposition.DeferredForMaintenance;
        }

        await using IGrimoireExternalEffectGroup effectGroup = admittedGroup!;

        // One attempt is the atomic effect unit, including retry/failure evidence, child stamping,
        // Shifting Fate, and the final checkpoint. Retry memory lives on the retained task above it.
        try
        {
            int stepIndex = attemptMemory.StepIndex;

            PlanStep current = plan[stepIndex] with
            {
                Status = "in_progress",
                StartedAt = DateTimeOffset.UtcNow,
                Attempts = attemptMemory.Attempt - 1,
            };
            plan[stepIndex] = current;

            apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

            await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

            if (attemptMemory.Attempt == 1)
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
            ApprenticeCheckpoint? checkpoint = ApprenticeRepository
                .DeserializeCheckpoint(apprentice.CheckpointData);

            string stepPrompt = ApprenticePromptBuilder.BuildStepExecutionPrompt(
                apprentice,
                plan,
                stepIndex,
                checkpoint);

            IArcanumIntelligenceProvider intelligence = services
                .GetRequiredService<IArcanumIntelligenceProvider>();

            StepExecutionOutcome outcome = await ExecuteStepStreamAsync(
                intelligence,
                apprentice,
                stepPrompt,
                linkedCts,
                apprenticeId,
                castSendings: attemptMemory.CastSendings).ConfigureAwait(false);

            await SettleCastSendingsAsync(
                repo,
                apprenticeId,
                attemptMemory.CastSendings).ConfigureAwait(false);

            StepFailureKind failureKind = ApprenticeExecutionPolicy.ClassifyStepFailure(
                outcome.StepFailed,
                outcome.EscalationRequested,
                outcome.ToolDenied,
                outcome.PauseOrCancel,
                outcome.IsRetryable);

            if (failureKind == StepFailureKind.PausedOrCancelled)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                return ApprenticeUnitDisposition.Stop;
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

                return ApprenticeUnitDisposition.Stop;
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

                return ApprenticeUnitDisposition.Stop;
            }

            if (failureKind == StepFailureKind.Retryable)
            {
                StepRecoveryState recoveryState = BuildStepRecoveryState(outcome);

                if (!attemptMemory.ObservedRecoveryStates.Add(recoveryState))
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

                    return ApprenticeUnitDisposition.Stop;
                }

                string retryMessage = ApprenticeExecutionPolicy
                    .SanitizeOperatorMessage(outcome.ErrorMessage);

                plan[stepIndex] = current with { Attempts = attemptMemory.Attempt };

                apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

                await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

                Publish(apprenticeId, new ApprenticeEvent
                {
                    Type = ApprenticeEventType.StepRetrying,
                    ApprenticeId = apprenticeId,
                    Timestamp = DateTimeOffset.UtcNow,
                    StepIndex = current.Index,
                    Attempt = attemptMemory.Attempt,
                    BackoffMs = 0,
                    Error = retryMessage,
                });

                attemptMemory.Attempt++;

                return ApprenticeUnitDisposition.Continue;
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
            _ = await CompleteStepAsync(
                repo,
                apprentice,
                plan,
                stepIndex,
                outcome.ResultText ?? string.Empty,
                durationMs,
                apprenticeId,
                linkedCts.Token).ConfigureAwait(false);

            return ApprenticeUnitDisposition.Continue;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            try
            {
                await SettleCastSendingsAsync(
                    repo,
                    apprenticeId,
                    attemptMemory.CastSendings).ConfigureAwait(false);
            }
            catch (Exception settlementFailure)
            {
                await PersistFailureIfCurrentAsync(
                    repo,
                    apprenticeId,
                    generation,
                    settlementFailure).ConfigureAwait(false);

                return ApprenticeUnitDisposition.Stop;
            }

            await PersistPausedIfCurrentAsync(
                repo,
                apprenticeId,
                generation).ConfigureAwait(false);

            return ApprenticeUnitDisposition.Stop;
        }
        catch (Exception ex)
        {
            Exception failure = ex;

            try
            {
                await SettleCastSendingsAsync(
                    repo,
                    apprenticeId,
                    attemptMemory.CastSendings).ConfigureAwait(false);
            }
            catch (Exception settlementFailure)
            {
                failure = new AggregateException(
                    "Apprentice execution and Cast Sending settlement both failed.",
                    ex,
                    settlementFailure);
            }

            await PersistFailureIfCurrentAsync(
                repo,
                apprenticeId,
                generation,
                failure).ConfigureAwait(false);

            return ApprenticeUnitDisposition.Stop;
        }
    }

    private async Task<ApprenticeUnitDisposition> ExecuteSimulacrumUnitAsync(
        IServiceProvider services,
        IApprenticeRepository repo,
        IGrimoireWorkLease lease,
        Apprentice apprentice,
        List<PlanStep> plan,
        int groupStart,
        int groupEnd,
        ApprenticeSettings settings,
        Guid apprenticeId,
        long generation,
        CancellationTokenSource linkedCts)
    {
        if (!lease.TryBeginExternalEffectGroup(
                out IGrimoireExternalEffectGroup? admittedGroup))
        {
            return ApprenticeUnitDisposition.DeferredForMaintenance;
        }

        await using IGrimoireExternalEffectGroup effectGroup = admittedGroup!;

        // Simulacrum branches share this one frontier. Their fresh nested scopes are bounded by the
        // existing branch semaphore and never claim independent effect groups.
        try
        {
            IArcanumIntelligenceProvider intelligence = services
                .GetRequiredService<IArcanumIntelligenceProvider>();

            bool advanced = await ExecuteSimulacrumGroupAsync(
                repo,
                intelligence,
                apprentice,
                plan,
                groupStart,
                groupEnd,
                settings,
                apprenticeId,
                linkedCts).ConfigureAwait(false);

            if (!advanced)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                return ApprenticeUnitDisposition.Stop;
            }

            return ApprenticeUnitDisposition.Continue;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            await PersistPausedIfCurrentAsync(
                repo,
                apprenticeId,
                generation).ConfigureAwait(false);

            return ApprenticeUnitDisposition.Stop;
        }
        catch (Exception ex)
        {
            await PersistFailureIfCurrentAsync(
                repo,
                apprenticeId,
                generation,
                ex).ConfigureAwait(false);

            return ApprenticeUnitDisposition.Stop;
        }
    }

    private async Task WaitForApprenticeAdmissionAsync(
        long observedGeneration,
        CancellationToken cancellationToken)
    {
        long waitAfterGeneration = observedGeneration > 0
            ? observedGeneration - 1
            : 0;

        _ = await admissionGate.WaitForNextOpenGenerationAsync(
            waitAfterGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TryPersistPausedAfterCancellationAsync(
        Guid apprenticeId,
        long generation)
    {
        if (!OwnsExecutionGeneration(apprenticeId, generation)
            || !admissionGate.TryAcquireWorkLease(
                GrimoireWorkKind.ApprenticeExecution,
                out IGrimoireWorkLease? admitted))
        {
            return;
        }

        await using IGrimoireWorkLease lease = admitted!;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider
            .GetRequiredService<IApprenticeRepository>();

        await PersistPausedIfCurrentAsync(
            repo,
            apprenticeId,
            generation).ConfigureAwait(false);
    }

    private async Task TryPersistFailureAfterExceptionAsync(
        Guid apprenticeId,
        long generation,
        Exception exception)
    {
        if (!OwnsExecutionGeneration(apprenticeId, generation)
            || !admissionGate.TryAcquireWorkLease(
                GrimoireWorkKind.ApprenticeExecution,
                out IGrimoireWorkLease? admitted))
        {
            return;
        }

        await using IGrimoireWorkLease lease = admitted!;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider
            .GetRequiredService<IApprenticeRepository>();

        await PersistFailureIfCurrentAsync(
            repo,
            apprenticeId,
            generation,
            exception).ConfigureAwait(false);
    }

    private async Task PersistPausedIfCurrentAsync(
        IApprenticeRepository repo,
        Guid apprenticeId,
        long generation)
    {
        if (!OwnsExecutionGeneration(apprenticeId, generation))
        {
            return;
        }

        try
        {
            Apprentice? apprentice = await repo
                .GetByIdAsync(apprenticeId, CancellationToken.None)
                .ConfigureAwait(false);

            if (apprentice is null || !OwnsExecutionGeneration(apprenticeId, generation))
            {
                return;
            }

            bool isRunning =
                string.Equals(
                    apprentice.Status,
                    ApprenticeStatus.Running.ToString(),
                    StringComparison.Ordinal)
                || string.Equals(
                    apprentice.Status,
                    ApprenticeStatus.Planning.ToString(),
                    StringComparison.Ordinal);

            if (!isRunning)
            {
                return;
            }
            apprentice.Status = ApprenticeStatus.Paused.ToString();

            await repo.UpdateAsync(apprentice, CancellationToken.None).ConfigureAwait(false);

            Publish(apprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.ApprenticePaused,
                ApprenticeId = apprenticeId,
                Timestamp = DateTimeOffset.UtcNow,
                AtStep = apprentice.CurrentStep,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to persist Paused status after Apprentice cancellation.");
        }
    }

    private async Task PersistFailureIfCurrentAsync(
        IApprenticeRepository repo,
        Guid apprenticeId,
        long generation,
        Exception exception)
    {
        logger.LogError(
            exception,
            "Apprentice {ApprenticeId} failed with an unhandled exception.",
            apprenticeId);

        if (!OwnsExecutionGeneration(apprenticeId, generation))
        {
            return;
        }

        try
        {
            Apprentice? apprentice = await repo
                .GetByIdAsync(apprenticeId, CancellationToken.None)
                .ConfigureAwait(false);

            if (apprentice is null || !OwnsExecutionGeneration(apprenticeId, generation))
            {
                return;
            }

            await FailApprenticeAsync(
                repo,
                apprentice,
                exception.Message,
                apprenticeId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception inner)
        {
            logger.LogError(
                inner,
                "Failed to persist Apprentice failure state.");
        }
    }

    private enum ApprenticeUnitDisposition
    {
        Continue,
        DeferredForMaintenance,
        Stop,
    }

    private sealed class ApprenticeExecutionMemory
    {
        internal SerialAttemptMemory? SerialAttempt { get; set; }
    }

    private sealed class SerialAttemptMemory(
        int stepIndex,
        int attempt)
    {
        internal int StepIndex { get; } = stepIndex;

        internal int Attempt { get; set; } = attempt;

        internal HashSet<StepRecoveryState> ObservedRecoveryStates { get; } = [];

        internal CastSendingSettlement CastSendings { get; } = new();
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

        CastSendingSettlement castSendings = new();

        SingleStepResult[] results = await StartJoinAndConcludeSimulacrumBranchesAsync(
            groupStart,
            groupEnd,
            branchIndex => RunSimulacrumBranchWithSettlementAsync(
                gate,
                snapshot,
                planSnapshot,
                branchIndex,
                settings,
                apprenticeId,
                linkedCts,
                castSendings),
            () => new ValueTask(
                SettleCastSendingsAsync(
                    repo,
                    apprenticeId,
                    castSendings))).ConfigureAwait(false);

        bool anyPaused = false;

        SingleStepResult? terminal = null;

        SingleStepResult? escalated = null;

        foreach (SingleStepResult branch in results)
        {
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
        }

        if (settings.EnableShiftingFate)
        {
            plan = await AttemptShiftingFateAsync(
                repo,
                intelligence,
                apprentice,
                plan,
                groupEnd - 1,
                apprenticeId,
                linkedCts.Token).ConfigureAwait(false);
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

        foreach (SingleStepResult branch in results)
        {
            if (branch.StepIndex >= plan.Count)
            {
                continue;
            }
            Publish(apprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.StepCompleted,
                ApprenticeId = apprenticeId,
                Timestamp = DateTimeOffset.UtcNow,
                StepIndex = plan[branch.StepIndex].Index,
                Result = branch.ResultText ?? string.Empty,
                DurationMs = groupDurationMs,
            });
        }
        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.SimulacrumCompleted,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            AtStep = groupEnd,
            Summary = $"Simulacrum complete: {groupEnd - groupStart} parallel steps.",
            TotalDurationMs = groupDurationMs,
        });

        return true;
    }

    internal static async Task<T[]> StartAndJoinBranchesAsync<T>(
        int groupStart,
        int groupEnd,
        Func<int, Task<T>> startBranch)
    {
        List<Task<T>> started = new(groupEnd - groupStart);
        Exception? startupFailure = null;

        try
        {
            for (int branchIndex = groupStart; branchIndex < groupEnd; branchIndex++)
            {
                Task<T> task = startBranch(branchIndex)
                    ?? throw new InvalidOperationException("A Simulacrum branch starter returned no task.");

                started.Add(task);
            }
        }
        catch (Exception ex)
        {
            startupFailure = ex;
        }

        Task<T[]> join = Task.WhenAll(started);

        if (startupFailure is null)
        {
            return await join.ConfigureAwait(false);
        }

        try
        {
            _ = await join.ConfigureAwait(false);
        }
        catch (Exception branchFailure)
        {
            List<Exception> failures = [startupFailure];

            if (join.Exception is { } aggregate)
            {
                failures.AddRange(aggregate.Flatten().InnerExceptions);
            }
            else
            {
                failures.Add(branchFailure);
            }

            throw new AggregateException(
                "Simulacrum branch startup and an already-started branch both failed.",
                failures);
        }

        ExceptionDispatchInfo.Capture(startupFailure).Throw();

        return [];
    }

    internal static async Task<T[]> StartJoinAndConcludeSimulacrumBranchesAsync<T>(
        int groupStart,
        int groupEnd,
        Func<int, Task<T>> startBranch,
        Func<ValueTask> concludeAsync)
    {
        T[]? results = null;

        Exception? branchFailure = null;

        try
        {
            results = await StartAndJoinBranchesAsync(
                groupStart,
                groupEnd,
                startBranch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            branchFailure = ex;
        }

        try
        {
            await concludeAsync().ConfigureAwait(false);
        }
        catch (Exception conclusionFailure) when (branchFailure is not null)
        {
            IReadOnlyList<Exception> branchFailures = branchFailure is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions
                : [branchFailure];

            throw new AggregateException(
                "Simulacrum execution and Cast Sending conclusion both failed.",
                [.. branchFailures, conclusionFailure]);
        }

        if (branchFailure is not null)
        {
            ExceptionDispatchInfo.Capture(branchFailure).Throw();
        }

        return results!;
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
        return await RunSimulacrumBranchWithSettlementAsync(
            gate,
            snapshot,
            planSnapshot,
            stepIndex,
            settings,
            apprenticeId,
            linkedCts,
            new CastSendingSettlement()).ConfigureAwait(false);
    }

    private async Task<SingleStepResult> RunSimulacrumBranchWithSettlementAsync(
        SemaphoreSlim gate,
        Apprentice snapshot,
        IReadOnlyList<PlanStep> planSnapshot,
        int stepIndex,
        ApprenticeSettings settings,
        Guid apprenticeId,
        CancellationTokenSource linkedCts,
        CastSendingSettlement castSendings)
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
                linkedCts,
                castSendings).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            return new SingleStepResult(
                stepIndex,
                StepResultKind.PausedOrCancelled,
                null,
                null,
                false,
                0,
                castSendings.SnapshotUnsettled());
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
                castSendings.SnapshotUnsettled());
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
        CancellationTokenSource linkedCts,
        CastSendingSettlement castSendings)
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
                stateless,
                castSendings).ConfigureAwait(false);

            StepFailureKind failureKind = ApprenticeExecutionPolicy.ClassifyStepFailure(
                outcome.StepFailed,
                outcome.EscalationRequested,
                outcome.ToolDenied,
                outcome.PauseOrCancel,
                outcome.IsRetryable);

            if (failureKind == StepFailureKind.PausedOrCancelled)
            {
                return new SingleStepResult(stepIndex, StepResultKind.PausedOrCancelled, null, null, false, attempt - 1, castSendings.SnapshotUnsettled());
            }

            if (failureKind == StepFailureKind.None)
            {
                return new SingleStepResult(stepIndex, StepResultKind.Completed, outcome.ResultText, null, false, attempt - 1, castSendings.SnapshotUnsettled());
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
                    castSendings.SnapshotUnsettled());
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
                    castSendings.SnapshotUnsettled());
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
                        castSendings.SnapshotUnsettled())
                    : new SingleStepResult(
                        stepIndex,
                        StepResultKind.Terminal,
                        null,
                        noProgressMessage,
                        false,
                        attempt - 1,
                        castSendings.SnapshotUnsettled());
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

    private async Task SettleCastSendingsAsync(
        IApprenticeRepository repo,
        Guid parentApprenticeId,
        CastSendingSettlement settlement)
    {
        IReadOnlyList<Guid> unsettled = settlement.SnapshotUnsettled();

        if (unsettled.Count == 0)
        {
            return;
        }
        Apprentice parent = await repo
            .GetByIdAsync(parentApprenticeId, CancellationToken.None)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Cast Sending parent '{parentApprenticeId}' was not found during lineage settlement.");

        IReadOnlyList<string>? expectedChain = ApprenticeRepository
            .DeserializeCheckpoint(parent.CheckpointData)?
            .DelegationChain;

        using CancellationTokenSource startBudget = new(
            TimeSpan.FromSeconds(ArcanumRuntimeDefaults.DaemonShutdownDrainTimeoutSeconds));

        foreach (Guid childId in unsettled)
        {
            Apprentice child = await repo
                .GetByIdAsync(childId, CancellationToken.None)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Cast Sending child '{childId}' was not found during lineage settlement.");

            ApprenticeCheckpoint? checkpoint = ApprenticeRepository.DeserializeCheckpoint(child.CheckpointData);

            bool parentMatches = child.ParentApprenticeId == parentApprenticeId
                && checkpoint?.ParentApprenticeId == parentApprenticeId;

            bool campaignMatches = child.CampaignId == parent.CampaignId;

            bool chainMatches = DelegationChainsMatch(checkpoint?.DelegationChain, expectedChain);

            bool launchMatches = checkpoint?.LaunchRequested is true;

            if (!parentMatches || !campaignMatches || !chainMatches || !launchMatches)
            {
                throw new InvalidOperationException(
                    $"Cast Sending child '{childId}' does not match its creation-time parent, campaign, delegation, and launch authority.");
            }

            Publish(parentApprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.CastSent,
                ApprenticeId = parentApprenticeId,
                Timestamp = DateTimeOffset.UtcNow,
                Summary = child.Id.ToString(),
                Name = child.Name,
                Goal = child.Goal,
            });

            try
            {
                Result<string> start = await StartAsync(childId, startBudget.Token)
                    .WaitAsync(startBudget.Token)
                    .ConfigureAwait(false);

                if (start.IsFailure)
                {
                    logger.LogInformation(
                        "Cast Sending child {ChildId} was created but not started: {Message}",
                        childId,
                        start.Error.Message);
                }
            }
            catch (OperationCanceledException) when (startBudget.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Cast Sending child {ChildId} start exceeded the bounded start budget.",
                    childId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Cast Sending child {ChildId} was linked but could not be started.",
                    childId);
            }
            settlement.MarkSettled(childId);
        }
    }

    private static bool DelegationChainsMatch(
        IReadOnlyList<string>? actual,
        IReadOnlyList<string>? expected)
    {
        bool actualIsEmpty = actual is not { Count: > 0 };

        bool expectedIsEmpty = expected is not { Count: > 0 };

        return actualIsEmpty && expectedIsEmpty
            || !actualIsEmpty
            && !expectedIsEmpty
            && actual!.SequenceEqual(expected!, StringComparer.Ordinal);
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

    internal async Task<StepExecutionOutcome> ExecuteStepStreamAsync(
        IArcanumIntelligenceProvider intelligence,
        Apprentice apprentice,
        string stepPrompt,
        CancellationTokenSource linkedCts,
        Guid apprenticeId,
        bool stateless = false,
        CastSendingSettlement? castSendings = null)
    {
        DateTimeOffset stepStarted = DateTimeOffset.UtcNow;

        string stepResultText = string.Empty;

        bool stepFailed = false;

        bool escalationRequested = false;

        bool toolDenied = false;

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
                ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData)?.DelegationChain ?? [],
                CampaignId: apprentice.CampaignId));

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

                if (ApprenticeStreamFramePolicy.IsTerminalToolDenial(frame))
                {
                    stepFailed = true;

                    toolDenied = true;

                    stepError = frame.Data;
                }

                if (frame.Type == IntelligenceEventType.ToolResult
                    && string.Equals(frame.ToolCall?.Name, "cast_sending", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(frame.Data)
                    && TryParseCastSendingChildId(frame.Data, out Guid spawnedChildId))
                {
                    spawnedChildIds.Add(spawnedChildId);

                    castSendings?.Record(spawnedChildId);
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
            ToolDenied: toolDenied,
            PauseOrCancel: pauseOrCancel,
            IsRetryable: stepFailed && !toolDenied && !escalationRequested,
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

    internal sealed record StepExecutionOutcome(
        bool StepFailed,
        bool EscalationRequested,
        bool ToolDenied,
        bool PauseOrCancel,
        bool IsRetryable,
        string? ResultText,
        string? ErrorMessage,
        bool AlreadyAlerted,
        DateTimeOffset StepStarted,
        IReadOnlyList<Guid> SpawnedChildIds);

    internal sealed class CastSendingSettlement
    {
        private readonly Lock _gate = new();

        private readonly List<Guid> _detectionOrder = [];

        private readonly HashSet<Guid> _detected = [];

        private readonly HashSet<Guid> _settled = [];

        internal void Record(Guid childId)
        {
            if (childId == Guid.Empty)
            {
                return;
            }

            lock (_gate)
            {
                if (_detected.Add(childId))
                {
                    _detectionOrder.Add(childId);
                }
            }
        }

        internal IReadOnlyList<Guid> SnapshotUnsettled()
        {
            lock (_gate)
            {
                return _detectionOrder
                    .Where(childId => !_settled.Contains(childId))
                    .ToArray();
            }
        }

        internal void MarkSettled(Guid childId)
        {
            lock (_gate)
            {
                _settled.Add(childId);
            }
        }
    }

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
internal interface IApprenticeExecutionCapacity
{
    int RunningCount { get; }

    bool TryAcquire(int maxConcurrent, out IDisposable? lease);
}
internal sealed class DefaultApprenticeExecutionCapacity : IApprenticeExecutionCapacity
{
    private readonly ApprenticeConcurrencyGate _gate = new();

    public int RunningCount => _gate.RunningCount;

    public bool TryAcquire(int maxConcurrent, out IDisposable? lease) =>
        _gate.TryAcquire(maxConcurrent, out lease);
}
