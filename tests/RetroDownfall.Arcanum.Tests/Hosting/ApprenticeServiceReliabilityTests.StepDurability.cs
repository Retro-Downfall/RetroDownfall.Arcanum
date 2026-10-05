using System.Reflection;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// Durability of an Apprentice step once its model and tool effects have run: the completion is the
/// point of no return, so Pause, Cancel, and host shutdown landing after it must neither lose it nor
/// make Resume pay for the same step again.
/// </summary>
public sealed partial class ApprenticeServiceReliabilityTests
{
    [Fact]
    public async Task Pause_during_shifting_fate_does_not_rerun_the_completed_step()
    {
        Guid apprenticeId = Guid.NewGuid();

        CancellationSensitiveRepository repo = new(RunningApprenticeWithOneStep(apprenticeId));

        ApprenticeService? service = null;

        StepThenShiftingFateIntelligence intelligence = new(
            afterStreamResult: null,
            duringShiftingFate: () => CancelExecutionLease(service!, apprenticeId));

        using ApprenticeService created = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        service = created;

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(1, intelligence.ShiftingFateCalls);

        AssertStepCommittedThenPaused(repo.Get(apprenticeId));

        await AssertResumeDoesNotRerunTheStepAsync(service, repo, intelligence, apprenticeId);
    }

    [Fact]
    public async Task Pause_between_step_stream_end_and_completion_commit_still_records_the_step_completed()
    {
        Guid apprenticeId = Guid.NewGuid();

        CancellationSensitiveRepository repo = new(RunningApprenticeWithOneStep(apprenticeId));

        ApprenticeService? service = null;

        StepThenShiftingFateIntelligence intelligence = new(
            afterStreamResult: () => CancelExecutionLease(service!, apprenticeId),
            duringShiftingFate: null);

        using ApprenticeService created = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        service = created;

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        AssertStepCommittedThenPaused(repo.Get(apprenticeId));

        await AssertResumeDoesNotRerunTheStepAsync(service, repo, intelligence, apprenticeId);
    }

    [Fact]
    public async Task Pause_after_session_creation_still_binds_the_session()
    {
        Guid apprenticeId = Guid.NewGuid();

        Guid campaignId = Guid.NewGuid();

        Guid sessionId = Guid.NewGuid();

        CancellationSensitiveRepository repo = new(SessionlessApprentice(
            apprenticeId,
            campaignId,
            Path.Combine(Path.GetTempPath(), $"apprentice-bind-{Guid.NewGuid():N}")));

        RecordingCanonicalCampaignContextResolver resolver = new(CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 3,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null));

        RecordingSessionTurnBeginStore turnStore = new(sessionId);

        CountingSessionStepIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence,
            campaignResolver: resolver,
            turnBeginStore: turnStore);

        // The Session row exists from here on; a pause landing now must not leave it unbound.
        turnStore.AfterCreate = () => CancelExecutionLease(service, apprenticeId);

        Result<string> started = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Apprentice paused = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), paused.Status);

        Assert.Equal(sessionId, paused.SessionId);

        turnStore.AfterCreate = null;

        Result<string> resumed = await service.ResumeAsync(apprenticeId, CancellationToken.None);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        _ = Assert.Single(turnStore.Creations);

        Assert.Equal(1, intelligence.StreamCalls);

        Assert.Equal(sessionId, repo.Get(apprenticeId).SessionId);
    }

    [Fact]
    public async Task RunApprenticeAsync_NonCancellationFaultAfterOperatorCancel_KeepsCancelledStatus()
    {
        Guid apprenticeId = Guid.NewGuid();

        InMemoryApprenticeRepository repo = new(RunningApprenticeWithOneStep(apprenticeId));

        FaultOnAbortIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await intelligence.StreamReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Result<string> cancelled = await service.CancelAsync(apprenticeId, CancellationToken.None);

        Assert.True(cancelled.IsSuccess, cancelled.IsFailure ? cancelled.Error.Message : null);

        // The abort surfaces as an ordinary fault only after the operator's Cancelled is durable.
        intelligence.AllowFault.TrySetResult();

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Cancelled.ToString(), persisted.Status);

        Assert.Null(persisted.ErrorMessage);
    }

    [Fact]
    public async Task PauseAsync_ConcurrentStepCompletion_DoesNotRevertCurrentStep()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = RunningApprenticeWithOneStep(apprenticeId);

        apprentice.Plan = ApprenticeRepository.SerializePlan(
        [
            new PlanStep { Index = 0, Description = "Completes while Pause is in flight" },
            new PlanStep { Index = 1, Description = "Pause lands here" },
        ]);

        PauseReadGateRepository repo = new(apprentice);

        TwoStepPauseRaceIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        using CancellationTokenSource pauseRequest = new();

        repo.PauseToken = pauseRequest.Token;

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await intelligence.FirstStreamReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Pause reads the row while step 0 is still running, then waits there.
        Task<Result<string>> pause = service.PauseAsync(apprenticeId, pauseRequest.Token);

        await repo.PauseReadReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        intelligence.AllowFirstStream.TrySetResult();

        await intelligence.SecondStreamReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, repo.Get(apprenticeId).CurrentStep);

        repo.AllowPauseRead.TrySetResult();

        Result<string> paused = await pause.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(paused.IsSuccess, paused.IsFailure ? paused.Error.Message : null);

        intelligence.AllowSecondStreamEnd.TrySetResult();

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), persisted.Status);

        Assert.Equal(1, persisted.CurrentStep);

        Assert.Equal("completed", ApprenticeRepository.DeserializePlan(persisted.Plan)[0].Status);
    }

    /// <summary>
    /// StartAsync persists <c>Planning</c> before queueing, and plan generation writes the plan together
    /// with <c>Running</c>, so a <c>Planning</c> row that already has a plan is a queued (re)start that
    /// never reached its first step — not a plan generation cut short.
    /// </summary>
    [Fact]
    public async Task CrashRecovery_QueuedRestartWithExistingPlan_ResumesInsteadOfEscalating()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = RunningApprenticeWithOneStep(apprenticeId);

        apprentice.Status = ApprenticeStatus.Planning.ToString();

        InMemoryApprenticeRepository repo = new(apprentice);

        SuccessfulStepIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        await InvokeResumeCrashRecoveryAsync(service, CancellationToken.None);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Apprentice recovered = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), recovered.Status);

        Assert.Null(recovered.ErrorMessage);

        Assert.Equal(1, recovered.CurrentStep);

        Assert.Equal(1, intelligence.StreamCalls);
    }

    private static void AssertStepCommittedThenPaused(Apprentice persisted)
    {
        Assert.Equal(ApprenticeStatus.Paused.ToString(), persisted.Status);

        Assert.Equal(1, persisted.CurrentStep);

        Assert.Equal("completed", ApprenticeRepository.DeserializePlan(persisted.Plan)[0].Status);

        Assert.Equal("step complete", ApprenticeRepository.DeserializePlan(persisted.Plan)[0].Result);

        Assert.Equal(1, ApprenticeRepository.DeserializeCheckpoint(persisted.CheckpointData)?.CurrentStep);
    }

    private static async Task AssertResumeDoesNotRerunTheStepAsync(
        ApprenticeService service,
        CancellationSensitiveRepository repo,
        StepThenShiftingFateIntelligence intelligence,
        Guid apprenticeId)
    {
        Result<string> resumed = await service.ResumeAsync(apprenticeId, CancellationToken.None);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(1, intelligence.StreamCalls);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);
    }

    /// <summary>
    /// Cancels the exact execution lease Pause, Cancel, and host shutdown cancel, from inside a provider
    /// call, so the cancellation lands at a precise point of the step rather than racing it.
    /// </summary>
    private static void CancelExecutionLease(ApprenticeService service, Guid apprenticeId)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_executionTokens", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        System.Collections.IDictionary leases =
            Assert.IsAssignableFrom<System.Collections.IDictionary>(field!.GetValue(service));

        object lease = Assert.IsAssignableFrom<object>(leases[apprenticeId]);

        PropertyInfo? cts = lease.GetType().GetProperty("Cts");

        Assert.NotNull(cts);

        Assert.IsType<CancellationTokenSource>(cts!.GetValue(lease)).Cancel();
    }

    private static Apprentice CloneApprentice(Apprentice source) => new()
    {
        Id = source.Id,
        CampaignId = source.CampaignId,
        Name = source.Name,
        Goal = source.Goal,
        Plan = source.Plan,
        CurrentStep = source.CurrentStep,
        Status = source.Status,
        SessionId = source.SessionId,
        WorkspacePath = source.WorkspacePath,
        CheckpointData = source.CheckpointData,
        ErrorMessage = source.ErrorMessage,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        ParentApprenticeId = source.ParentApprenticeId,
    };

    /// <summary>
    /// Holds the read made with <see cref="PauseToken"/> — Pause's own snapshot — so a step completion can
    /// commit between that read and Pause's write.
    /// </summary>
    private sealed class PauseReadGateRepository(params Apprentice[] apprentices)
        : InMemoryApprenticeRepository(apprentices)
    {
        internal CancellationToken PauseToken { get; set; }

        internal TaskCompletionSource PauseReadReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowPauseRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Apprentice?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            Apprentice? snapshot = await base.GetByIdAsync(id, cancellationToken);

            if (cancellationToken.CanBeCanceled && cancellationToken == PauseToken)
            {
                PauseReadReached.TrySetResult();

                await AllowPauseRead.Task;
            }

            return snapshot;
        }
    }

    /// <summary>
    /// Step 0 completes when the test allows it; step 1 holds until the test lets it observe the Pause.
    /// </summary>
    private sealed class TwoStepPauseRaceIntelligence : IArcanumIntelligenceProvider
    {
        private int _streams;

        internal TaskCompletionSource FirstStreamReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowFirstStream { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondStreamReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowSecondStreamEnd { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            Task.FromResult<Result<PromptTurnResult>>(
                new PromptTurnResult("NO_CHANGE", Usage: null));

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            if (Interlocked.Increment(ref _streams) == 1)
            {
                FirstStreamReached.TrySetResult();

                await AllowFirstStream.Task;

                yield return new IntelligenceEvent(IntelligenceEventType.Result, "step 0 complete");

                yield break;
            }
            SecondStreamReached.TrySetResult();

            await AllowSecondStreamEnd.Task;

            cancellationToken.ThrowIfCancellationRequested();

            yield return new IntelligenceEvent(IntelligenceEventType.Result, "step 1 complete");
        }
    }

    /// <summary>
    /// A step stream whose transport, once the execution is aborted, fails with an ordinary exception
    /// rather than an <see cref="OperationCanceledException"/>.
    /// </summary>
    private sealed class FaultOnAbortIntelligence : IArcanumIntelligenceProvider
    {
        internal TaskCompletionSource StreamReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowFault { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotImplementedException();

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            StreamReached.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await AllowFault.Task;

                throw new InvalidOperationException("The provider transport was torn down by the abort.");
            }

            yield break;
        }
    }

    /// <summary>
    /// One successful step stream followed by a Shifting Fate evaluation that honours its token the way
    /// a real provider call does.
    /// </summary>
    private sealed class StepThenShiftingFateIntelligence(
        Action? afterStreamResult,
        Action? duringShiftingFate) : IArcanumIntelligenceProvider
    {
        private int _streamCalls;

        private int _shiftingFateCalls;

        internal int StreamCalls => Volatile.Read(ref _streamCalls);

        internal int ShiftingFateCalls => Volatile.Read(ref _shiftingFateCalls);

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            _ = Interlocked.Increment(ref _shiftingFateCalls);

            duringShiftingFate?.Invoke();

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<Result<PromptTurnResult>>(
                new PromptTurnResult("NO_CHANGE", Usage: null));
        }

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            _ = Interlocked.Increment(ref _streamCalls);

            cancellationToken.ThrowIfCancellationRequested();

            await Task.Yield();

            yield return new IntelligenceEvent(
                IntelligenceEventType.Result,
                "step complete");

            afterStreamResult?.Invoke();
        }
    }
}
