using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed class ApprenticeService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ChronicleHub chronicleHub,
    ILogger<ApprenticeService> logger) : BackgroundService, IApprenticeRuntime
{

    private const string UnattendedDenySnippet = "Forbidden art denied";

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _executionTokens = new();

    private readonly ConcurrentDictionary<Guid, Task> _activeTasks = new();

    private readonly ConcurrentQueue<Guid> _pendingStarts = new();

    private int _runningCount;

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
        foreach (KeyValuePair<Guid, CancellationTokenSource> pair in _executionTokens)
        {
            try
            {
                await pair.Value.CancelAsync().ConfigureAwait(false);
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
            return Result<string>.Failure(new Error("Apprentice.Disabled", "Apprentice orchestration is disabled."));
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice is null)
        {
            return Result<string>.Failure(new Error("Apprentice.NotFound", "Apprentice was not found."));
        }

        if (!CanStart(apprentice.Status))
        {
            return Result<string>.Failure(new Error("Apprentice.AlreadyRunning", "Apprentice is already running or not in a startable state."));
        }

        int maxConcurrent = ArcanumSettingClamps.MaxConcurrentApprentices(settings.MaxConcurrentApprentices);

        if (Volatile.Read(ref _runningCount) >= maxConcurrent)
        {
            _pendingStarts.Enqueue(apprenticeId);

            return Result<string>.Failure(new Error("Apprentice.MaxReached", "Maximum concurrent Apprentices reached; queued for next slot."));
        }

        SpawnExecution(apprenticeId);

        return Result<string>.Success(apprenticeId.ToString());
    }

    public async Task<Result<string>> PauseAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice is null)
        {
            return Result<string>.Failure(new Error("Apprentice.NotFound", "Apprentice was not found."));
        }

        if (!IsPausable(apprentice.Status))
        {
            return Result<string>.Failure(new Error("Apprentice.Running", "Apprentice is not running or planning."));
        }

        if (_executionTokens.TryGetValue(apprenticeId, out CancellationTokenSource? cts))
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        apprentice.Status = ApprenticeStatus.Paused.ToString();

        await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.ApprenticePaused,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            AtStep = apprentice.CurrentStep,
        });

        return Result<string>.Success(apprenticeId.ToString());
    }

    public async Task<Result<string>> ResumeAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice is null)
        {
            return Result<string>.Failure(new Error("Apprentice.NotFound", "Apprentice was not found."));
        }

        if (!string.Equals(apprentice.Status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal))
        {
            return Result<string>.Failure(new Error("Apprentice.NotPaused", "Apprentice is not paused."));
        }

        ApprenticeSettings settings = GetApprenticeSettings();

        int maxConcurrent = ArcanumSettingClamps.MaxConcurrentApprentices(settings.MaxConcurrentApprentices);

        if (Volatile.Read(ref _runningCount) >= maxConcurrent)
        {
            return Result<string>.Failure(new Error("Apprentice.MaxReached", "Maximum concurrent Apprentices reached."));
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

        SpawnExecution(apprenticeId);

        return Result<string>.Success(apprenticeId.ToString());
    }

    public async Task<Result<string>> CancelAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository repo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await repo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice is null)
        {
            return Result<string>.Failure(new Error("Apprentice.NotFound", "Apprentice was not found."));
        }

        if (!IsCancellable(apprentice.Status))
        {
            return Result<string>.Failure(new Error("Apprentice.NotPaused", "Apprentice is not in a cancellable state."));
        }

        if (_executionTokens.TryRemove(apprenticeId, out CancellationTokenSource? cts))
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
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

            IReadOnlyList<Apprentice> resumable = await repo.GetResumableAsync(stoppingToken).ConfigureAwait(false);

            ApprenticeSettings settings = GetApprenticeSettings();

            int maxConcurrent = ArcanumSettingClamps.MaxConcurrentApprentices(settings.MaxConcurrentApprentices);

            foreach (Apprentice apprentice in resumable)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (Volatile.Read(ref _runningCount) >= maxConcurrent)
                {
                    _pendingStarts.Enqueue(apprentice.Id);

                    continue;
                }

                logger.LogInformation("Resuming Apprentice {ApprenticeId} after host restart.", apprentice.Id);

                SpawnExecution(apprentice.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Apprentice crash recovery failed.");
        }
    }

    private void SpawnExecution(Guid apprenticeId)
    {

        if (!_activeTasks.TryAdd(apprenticeId, Task.CompletedTask))
        {

            return;

        }

        Interlocked.Increment(ref _runningCount);

        Task task = Task.Run(() => RunApprenticeAsync(apprenticeId));

        _activeTasks[apprenticeId] = task;

        _ = task.ContinueWith(
            _ => CleanupExecution(apprenticeId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CleanupExecution(Guid apprenticeId)
    {
        _activeTasks.TryRemove(apprenticeId, out _);

        if (_executionTokens.TryRemove(apprenticeId, out CancellationTokenSource? cts))
        {
            cts.Dispose();
        }

        Interlocked.Decrement(ref _runningCount);

        TryDequeuePendingStart();
    }

    private void TryDequeuePendingStart()
    {
        ApprenticeSettings settings = GetApprenticeSettings();

        int maxConcurrent = ArcanumSettingClamps.MaxConcurrentApprentices(settings.MaxConcurrentApprentices);

        while (Volatile.Read(ref _runningCount) < maxConcurrent && _pendingStarts.TryDequeue(out Guid nextId))
        {
            SpawnExecution(nextId);
        }
    }

    private async Task RunApprenticeAsync(Guid apprenticeId)
    {
        DateTimeOffset runStarted = DateTimeOffset.UtcNow;

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);

        _executionTokens[apprenticeId] = linkedCts;

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

            if (string.Equals(apprentice.Status, ApprenticeStatus.Cancelled.ToString(), StringComparison.Ordinal))
            {
                return;
            }

            apprentice.Status = ApprenticeStatus.Planning.ToString();

            await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

            Publish(apprenticeId, new ApprenticeEvent
            {
                Type = ApprenticeEventType.ApprenticeStarted,
                ApprenticeId = apprenticeId,
                Timestamp = DateTimeOffset.UtcNow,
                Name = apprentice.Name,
                Goal = apprentice.Goal,
            });

            List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

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
                    .ExecutePromptAsync(planRequest, linkedCts.Token)
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

            int stepTimeoutMinutes = ArcanumSettingClamps.StepTimeoutMinutes(settings.StepTimeoutMinutes);

            while (apprentice.CurrentStep < plan.Count)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                Apprentice? fresh = await repo.GetByIdAsync(apprenticeId, linkedCts.Token).ConfigureAwait(false);

                if (fresh is null)
                {
                    return;
                }

                if (string.Equals(fresh.Status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal)
                    || string.Equals(fresh.Status, ApprenticeStatus.Cancelled.ToString(), StringComparison.Ordinal))
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

                PlanStep current = plan[stepIndex] with
                {
                    Status = "in_progress",
                    StartedAt = DateTimeOffset.UtcNow,
                };

                plan[stepIndex] = current;

                apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

                await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

                Publish(apprenticeId, new ApprenticeEvent
                {
                    Type = ApprenticeEventType.StepStarted,
                    ApprenticeId = apprenticeId,
                    Timestamp = DateTimeOffset.UtcNow,
                    StepIndex = current.Index,
                    Description = current.Description,
                });

                string stepPrompt = ApprenticePromptBuilder.BuildStepExecutionPrompt(apprentice, plan, stepIndex);

                using CancellationTokenSource stepTimeout = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);

                stepTimeout.CancelAfter(TimeSpan.FromMinutes(stepTimeoutMinutes));

                DateTimeOffset stepStarted = DateTimeOffset.UtcNow;

                string stepResultText = string.Empty;

                bool stepFailed = false;

                string? stepError = null;

                try
                {
                    PingRequest stepRequest = new(
                        Prompt: stepPrompt,
                        Model: ResolveModel(),
                        WorkingDirectory: apprentice.WorkspacePath,
                        SessionId: apprentice.SessionId,
                        UnattendedMode: true,
                        SkipSpellRouting: true);

                    await foreach (IntelligenceEvent frame in intelligence
                        .StreamPromptAsync(stepRequest, stepTimeout.Token)
                        .ConfigureAwait(false))
                    {
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

                        if (frame.Type == IntelligenceEventType.Result && !string.IsNullOrWhiteSpace(frame.Message))
                        {
                            stepResultText = frame.Message;
                        }

                        if (frame.Type == IntelligenceEventType.Error)
                        {
                            stepFailed = true;

                            stepError = frame.Message;
                        }

                        if (frame.Type == IntelligenceEventType.ToolResult
                            && frame.Message.Contains(UnattendedDenySnippet, StringComparison.OrdinalIgnoreCase))
                        {
                            stepFailed = true;

                            stepError = frame.Message;
                        }

                        if (frame.Type == IntelligenceEventType.WardResolved && frame.WardAllowed == false)
                        {
                            stepFailed = true;

                            stepError = frame.WardReason ?? "Ward denied.";
                        }
                    }
                }
                catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
                {
                    Apprentice? pausedCheck = await repo.GetByIdAsync(apprenticeId, CancellationToken.None).ConfigureAwait(false);

                    if (pausedCheck is not null
                        && (string.Equals(pausedCheck.Status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal)
                            || string.Equals(pausedCheck.Status, ApprenticeStatus.Cancelled.ToString(), StringComparison.Ordinal)))
                    {
                        return;
                    }

                    stepFailed = true;

                    stepError = "Step execution was cancelled.";
                }
                catch (OperationCanceledException)
                {
                    stepFailed = true;

                    stepError = $"Step timed out after {stepTimeoutMinutes} minutes.";
                }

                if (stepFailed)
                {
                    plan[stepIndex] = current with
                    {
                        Status = "failed",
                        CompletedAt = DateTimeOffset.UtcNow,
                        Result = stepError,
                    };

                    apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

                    apprentice.Status = ApprenticeStatus.Failed.ToString();

                    apprentice.ErrorMessage = stepError;

                    await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

                    Publish(apprenticeId, new ApprenticeEvent
                    {
                        Type = ApprenticeEventType.StepFailed,
                        ApprenticeId = apprenticeId,
                        Timestamp = DateTimeOffset.UtcNow,
                        StepIndex = current.Index,
                        Error = stepError,
                    });

                    Publish(apprenticeId, new ApprenticeEvent
                    {
                        Type = ApprenticeEventType.ApprenticeFailed,
                        ApprenticeId = apprenticeId,
                        Timestamp = DateTimeOffset.UtcNow,
                        Error = stepError,
                    });

                    return;
                }

                long durationMs = (long)(DateTimeOffset.UtcNow - stepStarted).TotalMilliseconds;

                plan[stepIndex] = current with
                {
                    Status = "completed",
                    CompletedAt = DateTimeOffset.UtcNow,
                    Result = stepResultText,
                };

                apprentice.Plan = ApprenticeRepository.SerializePlan(plan);

                apprentice.CurrentStep = stepIndex + 1;

                apprentice.CheckpointData = ApprenticeRepository.SerializeCheckpoint(new ApprenticeCheckpoint
                {
                    CurrentStep = apprentice.CurrentStep,
                    Timestamp = DateTimeOffset.UtcNow,
                });

                await repo.UpdateAsync(apprentice, linkedCts.Token).ConfigureAwait(false);

                Publish(apprenticeId, new ApprenticeEvent
                {
                    Type = ApprenticeEventType.StepCompleted,
                    ApprenticeId = apprenticeId,
                    Timestamp = DateTimeOffset.UtcNow,
                    StepIndex = current.Index,
                    Result = stepResultText,
                    DurationMs = durationMs,
                });
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
        apprentice.Status = ApprenticeStatus.Failed.ToString();

        apprentice.ErrorMessage = errorMessage;

        await repo.UpdateAsync(apprentice, cancellationToken).ConfigureAwait(false);

        Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.ApprenticeFailed,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            Error = errorMessage,
        });
    }

    private void Publish(Guid apprenticeId, ApprenticeEvent @event) =>
        chronicleHub.Publish(apprenticeId, @event);

    private ApprenticeSettings GetApprenticeSettings() =>
        optionsMonitor.CurrentValue.Apprentices ?? new ApprenticeSettings();

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
        || string.Equals(status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal);

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
