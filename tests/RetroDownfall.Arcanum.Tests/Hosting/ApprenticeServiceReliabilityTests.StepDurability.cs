using System.Reflection;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
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

    /// <summary>
    /// The turn has already delivered its terminal Result frame — its model and tool effects have run — but the
    /// stream is still finishing up when the Pause lands, so it observes the cancelled token and throws. That is
    /// a completed step, not an interrupted one, and Resume must not run it again.
    /// </summary>
    [Fact]
    public async Task Pause_after_the_result_frame_while_the_stream_finishes_still_records_the_step_completed()
    {
        Guid apprenticeId = Guid.NewGuid();

        CancellationSensitiveRepository repo = new(RunningApprenticeWithOneStep(apprenticeId));

        ApprenticeService? service = null;

        StepThenShiftingFateIntelligence intelligence = new(
            afterStreamResult: () => CancelExecutionLease(service!, apprenticeId),
            duringShiftingFate: null,
            observeTokenAfterResult: true);

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

    /// <summary>
    /// The plan response is paid for the moment it arrives. A Pause landing right after it must keep that plan,
    /// so Resume continues from it instead of buying a second one.
    /// </summary>
    [Fact]
    public async Task Pause_right_after_the_plan_call_keeps_the_paid_plan_and_resume_does_not_buy_another()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = RunningApprenticeWithOneStep(apprenticeId);

        apprentice.Status = ApprenticeStatus.Idle.ToString();

        apprentice.Plan = "[]";

        CancellationSensitiveRepository repo = new(apprentice);

        ApprenticeService? service = null;

        PlanThenPauseIntelligence intelligence = new(afterPlan: () => CancelExecutionLease(service!, apprenticeId));

        using ApprenticeService created = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        service = created;

        Result<string> started = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Apprentice paused = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), paused.Status);

        Assert.Equal("One durable step", Assert.Single(ApprenticeRepository.DeserializePlan(paused.Plan)).Description);

        intelligence.AfterPlan = null;

        Result<string> resumed = await service.ResumeAsync(apprenticeId, CancellationToken.None);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(1, intelligence.PlanCalls);

        Assert.Equal(1, intelligence.StreamCalls);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);
    }

    /// <summary>
    /// The plan call runs on the cancellable execution token, so a Pause the provider honours while the call is
    /// still in flight abandons it: no plan response arrives and there is none to keep. Whether the provider
    /// surfaces the cancellation by throwing or as a failed result, the row ends <c>Paused</c> with no plan, and
    /// Resume generates the plan again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pause_the_provider_honours_during_the_plan_call_leaves_no_plan_and_resume_generates_one(
        bool providerThrows)
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = RunningApprenticeWithOneStep(apprenticeId);

        apprentice.Status = ApprenticeStatus.Idle.ToString();

        apprentice.Plan = "[]";

        CancellationSensitiveRepository repo = new(apprentice);

        ApprenticeService? service = null;

        PlanThenPauseIntelligence intelligence = new(afterPlan: () => CancelExecutionLease(service!, apprenticeId))
        {
            HonoursCancellation = true,
            ReportsCancellationAsFailure = !providerThrows,
        };

        using ApprenticeService created = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        service = created;

        Result<string> started = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Apprentice paused = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), paused.Status);

        Assert.Empty(ApprenticeRepository.DeserializePlan(paused.Plan));

        Assert.Equal(0, intelligence.StreamCalls);

        intelligence.AfterPlan = null;

        Result<string> resumed = await service.ResumeAsync(apprenticeId, CancellationToken.None);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(2, intelligence.PlanCalls);

        Assert.Equal(1, intelligence.StreamCalls);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);
    }

    /// <summary>
    /// One Simulacrum branch finished while its sibling failed and an operator paused the Apprentice. The failure
    /// cannot be recorded over the operator's Paused, but the finished branch's effects have run, so its
    /// completion is committed anyway — and the group's next run executes only the branch that did not finish.
    /// </summary>
    [Fact]
    public async Task Simulacrum_branch_that_finished_beside_a_failed_sibling_is_committed_and_not_run_again()
    {
        Guid apprenticeId = Guid.NewGuid();

        List<PlanStep> plan =
        [
            new PlanStep { Index = 1, Description = "Branch A", IsParallel = true },
            new PlanStep { Index = 2, Description = "Branch B", IsParallel = true },
        ];

        Apprentice apprentice = RunningSimulacrumApprentice(apprenticeId, plan);

        InMemoryApprenticeRepository repo = new(apprentice);

        SiblingFailureIntelligence intelligence = new(
            beforeFailure: () => repo.Mutate(
                apprenticeId,
                row => row.Status = ApprenticeStatus.Paused.ToString()));

        ArcanumSettings settings = CreateCapacitySettings();

        using ApprenticeService service = CreateService(
            repo,
            settings,
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        using CancellationTokenSource firstRun = new();

        Assert.False(await InvokeSimulacrumGroupAsync(
            service,
            repo,
            intelligence,
            apprentice,
            plan,
            settings,
            apprenticeId,
            firstRun));

        Apprentice stopped = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), stopped.Status);

        Assert.Equal(0, stopped.CurrentStep);

        List<PlanStep> stoppedPlan = ApprenticeRepository.DeserializePlan(stopped.Plan);

        Assert.Equal("completed", stoppedPlan[0].Status);

        Assert.Equal("branch A done", stoppedPlan[0].Result);

        Assert.NotEqual("completed", stoppedPlan[1].Status);

        // The operator resumes, and the provider no longer fails the second branch.
        repo.Mutate(apprenticeId, row => row.Status = ApprenticeStatus.Running.ToString());

        intelligence.FailBranchB = false;

        Apprentice resumed = repo.Get(apprenticeId);

        using CancellationTokenSource secondRun = new();

        Assert.True(await InvokeSimulacrumGroupAsync(
            service,
            repo,
            intelligence,
            resumed,
            ApprenticeRepository.DeserializePlan(resumed.Plan),
            settings,
            apprenticeId,
            secondRun));

        Assert.Equal(1, intelligence.BranchAStreams);

        Assert.Equal(2, intelligence.BranchBStreams);

        Apprentice finished = repo.Get(apprenticeId);

        Assert.Equal(2, finished.CurrentStep);

        Assert.All(
            ApprenticeRepository.DeserializePlan(finished.Plan),
            step => Assert.Equal("completed", step.Status));

        Assert.Equal("branch A done", ApprenticeRepository.DeserializePlan(finished.Plan)[0].Result);
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
    /// StartAsync persists <c>Planning</c> before queueing, and plan generation commits its plan before it
    /// moves the row to <c>Running</c>, so a <c>Planning</c> row that already has a plan never reached its
    /// first step: it continues from that plan rather than being escalated as a plan generation cut short.
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

    /// <summary>
    /// A queued (re)start of an Apprentice spawned by an inbound Sending resumes after a restart through the
    /// known-plan path, and its step still runs under the inherited A2A delegation chain — the peer's only cycle
    /// guard — which the completed step's checkpoint carries forward.
    /// </summary>
    [Fact]
    public async Task CrashRecovery_QueuedRestartWithExistingPlan_RunsTheStepUnderTheDelegationChain()
    {
        string[] inboundChain = ["origin-node-a", "this-node-b"];

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = RunningApprenticeWithOneStep(apprenticeId);

        apprentice.Status = ApprenticeStatus.Planning.ToString();

        apprentice.CheckpointData = ApprenticeRepository.SerializeCheckpoint(new ApprenticeCheckpoint
        {
            CurrentStep = 0,
            DelegationChain = inboundChain,
        });

        InMemoryApprenticeRepository repo = new(apprentice);

        DelegationChainCapturingIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        await InvokeResumeCrashRecoveryAsync(service, CancellationToken.None);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(inboundChain, intelligence.ChainDuringStep);

        Apprentice recovered = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), recovered.Status);

        Assert.Equal(
            inboundChain,
            ApprenticeRepository.DeserializeCheckpoint(recovered.CheckpointData)?.DelegationChain);
    }

    /// <summary>
    /// A successful turn always ends with a terminal Result frame. A stream that simply stops — a dropped
    /// transport, a provider that closed early — has not produced the step's result, so it must not be
    /// recorded as a completed step with empty text.
    /// </summary>
    [Fact]
    public async Task ExecuteStepStream_EndsWithoutResultFrame_IsRetryableNotCompleted()
    {
        Guid apprenticeId = Guid.NewGuid();

        ScriptedStreamIntelligence intelligence = new(
            new IntelligenceEvent(
                IntelligenceEventType.ToolResult,
                Message: "read_file",
                Data: "partial work",
                ToolCall: new IntelligenceToolCallEvent(
                    "read-call",
                    "read_file",
                    "{}")));

        ApprenticeService service = CreateService(
            new InMemoryApprenticeRepository(),
            new ArcanumSettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        ApprenticeService.StepExecutionOutcome outcome = await ExecuteStepStreamAsync(
            service,
            intelligence,
            TestApprentice(apprenticeId));

        Assert.True(outcome.StepFailed);

        Assert.True(outcome.IsRetryable);

        Assert.False(outcome.ToolDenied);

        Assert.False(outcome.PauseOrCancel);

        Assert.False(string.IsNullOrWhiteSpace(outcome.ErrorMessage));
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
    /// a real provider call does. With <paramref name="observeTokenAfterResult"/> the stream checks its token
    /// once more after the Result frame, the way a turn that is still finishing up after its answer does.
    /// </summary>
    private sealed class StepThenShiftingFateIntelligence(
        Action? afterStreamResult,
        Action? duringShiftingFate,
        bool observeTokenAfterResult = false,
        string shiftingFateAnswer = "NO_CHANGE") : IArcanumIntelligenceProvider
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
                new PromptTurnResult(shiftingFateAnswer, Usage: null));
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

            if (observeTokenAfterResult)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>
    /// Two Simulacrum branches told apart by the step their prompt marks current: "Branch A" always finishes,
    /// and "Branch B" faults while <see cref="FailBranchB"/> is set, after <c>beforeFailure</c> runs.
    /// </summary>
    private sealed class SiblingFailureIntelligence(Action beforeFailure) : IArcanumIntelligenceProvider
    {
        private int _branchAStreams;

        private int _branchBStreams;

        internal bool FailBranchB { get; set; } = true;

        internal int BranchAStreams => Volatile.Read(ref _branchAStreams);

        internal int BranchBStreams => Volatile.Read(ref _branchBStreams);

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
            await Task.Yield();

            if (request.Prompt.Contains("Branch B  ← CURRENT", StringComparison.Ordinal))
            {
                _ = Interlocked.Increment(ref _branchBStreams);

                if (FailBranchB)
                {
                    beforeFailure();

                    throw new InvalidOperationException("The second branch's provider failed.");
                }

                yield return new IntelligenceEvent(IntelligenceEventType.Result, "branch B done");

                yield break;
            }
            _ = Interlocked.Increment(ref _branchAStreams);

            yield return new IntelligenceEvent(IntelligenceEventType.Result, "branch A done");
        }
    }

    /// <summary>
    /// A plan call whose response has already arrived when <see cref="AfterPlan"/> runs, followed by successful
    /// step streams and Shifting Fate evaluations that change nothing.
    /// </summary>
    private sealed class PlanThenPauseIntelligence(Action? afterPlan) : IArcanumIntelligenceProvider
    {
        private int _planCalls;

        private int _streamCalls;

        internal Action? AfterPlan { get; set; } = afterPlan;

        /// <summary>
        /// When set, a cancellation observed once <see cref="AfterPlan"/> has run aborts the plan call before its
        /// response is returned, the way a real provider honours the token mid-call.
        /// </summary>
        internal bool HonoursCancellation { get; init; }

        /// <summary>With <see cref="HonoursCancellation"/>, the abort is a failed result instead of a throw.</summary>
        internal bool ReportsCancellationAsFailure { get; init; }

        internal int PlanCalls => Volatile.Read(ref _planCalls);

        internal int StreamCalls => Volatile.Read(ref _streamCalls);

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            if (!request.Prompt.Contains("creating a plan", StringComparison.Ordinal))
            {
                return Task.FromResult<Result<PromptTurnResult>>(
                    new PromptTurnResult("NO_CHANGE", Usage: null));
            }
            _ = Interlocked.Increment(ref _planCalls);

            AfterPlan?.Invoke();

            if (HonoursCancellation && cancellationToken.IsCancellationRequested)
            {
                return ReportsCancellationAsFailure
                    ? Task.FromResult(Result<PromptTurnResult>.Failure(
                        new Error("Hub.Error", "The plan request was cancelled.")))
                    : Task.FromCanceled<Result<PromptTurnResult>>(cancellationToken);
            }

            return Task.FromResult<Result<PromptTurnResult>>(
                new PromptTurnResult(
                    """[{"index":1,"description":"One durable step"}]""",
                    Usage: null));
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

            yield return new IntelligenceEvent(IntelligenceEventType.Result, "step complete");
        }
    }

    /// <summary>Records the delegation chain the step ran under, as the internal MCP tools see it.</summary>
    private sealed class DelegationChainCapturingIntelligence : IArcanumIntelligenceProvider
    {
        internal IReadOnlyList<string>? ChainDuringStep { get; private set; }

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
            ChainDuringStep = ApprenticeToolInvocationAmbient.Current?.DelegationChain;

            await Task.Yield();

            yield return new IntelligenceEvent(IntelligenceEventType.Result, "step complete");
        }
    }
}
