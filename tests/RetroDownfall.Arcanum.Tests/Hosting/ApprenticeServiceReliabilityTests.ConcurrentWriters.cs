using System.Reflection;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// The run and the operator write the same Apprentice row. Every write either side makes is conditional on the
/// row it read, so neither can revert what the other committed in between, and an operator request that did not
/// take effect says so instead of reporting success.
/// </summary>
public sealed partial class ApprenticeServiceReliabilityTests
{
    [Fact]
    public async Task PauseAsync_RunEndedBeforeThePauseLanded_ReportsConflictAndKeepsTheEndedStatus()
    {
        Guid apprenticeId = Guid.NewGuid();

        AfterFirstReadRepository repo = new(RunningApprenticeWithOneStep(apprenticeId))
        {
            AfterFirstRead = row => row.Status = ApprenticeStatus.Completed.ToString(),
        };

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>());

        Result<string> paused = await service.PauseAsync(apprenticeId, CancellationToken.None);

        Assert.True(paused.IsFailure);

        Assert.Equal(ErrorCodes.Apprentice.Running, paused.Error.Code);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);
    }

    /// <summary>
    /// The run's own cancellation arm recorded <c>Paused</c> before the operator's Pause wrote it. The Apprentice
    /// is paused, which is what the request asked for.
    /// </summary>
    [Fact]
    public async Task PauseAsync_RunAlreadyRecordedItsOwnPause_ReportsSuccess()
    {
        Guid apprenticeId = Guid.NewGuid();

        AfterFirstReadRepository repo = new(RunningApprenticeWithOneStep(apprenticeId))
        {
            AfterFirstRead = row => row.Status = ApprenticeStatus.Paused.ToString(),
        };

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>());

        Result<string> paused = await service.PauseAsync(apprenticeId, CancellationToken.None);

        Assert.True(paused.IsSuccess, paused.IsFailure ? paused.Error.Message : null);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), repo.Get(apprenticeId).Status);
    }

    [Fact]
    public async Task CancelAsync_RunEndedBeforeTheCancelLanded_ReportsConflictAndKeepsTheEndedStatus()
    {
        Guid apprenticeId = Guid.NewGuid();

        AfterFirstReadRepository repo = new(RunningApprenticeWithOneStep(apprenticeId))
        {
            AfterFirstRead = row => row.Status = ApprenticeStatus.Completed.ToString(),
        };

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>());

        Result<string> cancelled = await service.CancelAsync(apprenticeId, CancellationToken.None);

        Assert.True(cancelled.IsFailure);

        Assert.Equal(ErrorCodes.Apprentice.NotPaused, cancelled.Error.Code);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);
    }

    /// <summary>
    /// The execution commits a step after Cancel read the row. Cancel writes only the status, so the committed
    /// step position and plan survive it.
    /// </summary>
    [Fact]
    public async Task CancelAsync_StepCommittedAfterCancelReadTheRow_KeepsTheCommittedStep()
    {
        Guid apprenticeId = Guid.NewGuid();

        AfterFirstReadRepository repo = new(TwoStepApprentice(apprenticeId, ApprenticeStatus.Running))
        {
            AfterFirstRead = CommitFirstStep,
        };

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>());

        Result<string> cancelled = await service.CancelAsync(apprenticeId, CancellationToken.None);

        Assert.True(cancelled.IsSuccess, cancelled.IsFailure ? cancelled.Error.Message : null);

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Cancelled.ToString(), persisted.Status);

        AssertFirstStepCommitted(persisted);
    }

    /// <summary>
    /// A Paused execution can still be committing its last step. A tail merged at the step Reweave read must not
    /// be written over a step that committed meanwhile; the request fails and the commit stands.
    /// </summary>
    [Fact]
    public async Task ReweaveAsync_StepCommittedWhileThePlanWasRewoven_ReturnsCannotReweaveAndKeepsTheCommit()
    {
        Guid apprenticeId = Guid.NewGuid();

        AfterFirstReadRepository repo = new(TwoStepApprentice(apprenticeId, ApprenticeStatus.Paused))
        {
            AfterFirstRead = CommitFirstStep,
        };

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>());

        Result<ApprenticeDetailDto> rewoven = await service.ReweaveAsync(
            apprenticeId,
            [new PlanStep { Description = "Rewoven step" }],
            CancellationToken.None);

        Assert.True(rewoven.IsFailure);

        Assert.Equal(ErrorCodes.Apprentice.CannotReweave, rewoven.Error.Code);

        Apprentice persisted = repo.Get(apprenticeId);

        AssertFirstStepCommitted(persisted);

        Assert.Equal("Second step", ApprenticeRepository.DeserializePlan(persisted.Plan)[1].Description);
    }

    /// <summary>
    /// The Apprentice left <c>Escalated</c> after Intervene read it. The guidance is not applied, the request fails,
    /// and the execution slot it reserved for the resume is given back.
    /// </summary>
    [Fact]
    public async Task InterveneAsync_ApprenticeLeftEscalatedWhileGuidanceWasApplied_ReturnsNotEscalatedAndReleasesTheSlot()
    {
        Guid apprenticeId = Guid.NewGuid();

        AfterFirstReadRepository repo = new(TwoStepApprentice(apprenticeId, ApprenticeStatus.Escalated))
        {
            AfterFirstRead = row => row.Status = ApprenticeStatus.Cancelled.ToString(),
        };

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>());

        Result<string> intervened = await service.InterveneAsync(
            apprenticeId,
            "Try the other approach.",
            resume: true,
            CancellationToken.None);

        Assert.True(intervened.IsFailure);

        Assert.Equal(ErrorCodes.Apprentice.NotEscalated, intervened.Error.Code);

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Cancelled.ToString(), persisted.Status);

        Assert.Null(ApprenticeRepository.DeserializeCheckpoint(persisted.CheckpointData)?.DmGuidance);

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }

    /// <summary>
    /// Cancel withdraws a queued start from the queue before it touches the row, so the request token must not
    /// be able to abandon it there: a row left <c>Planning</c> with no queue entry would silently run after the
    /// next restart.
    /// </summary>
    [Fact]
    public async Task CancelAsync_QueuedStartWithAnAlreadyCancelledRequest_StillRecordsCancelled()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = TestApprentice(apprenticeId);

        apprentice.Status = ApprenticeStatus.Idle.ToString();

        apprentice.Plan = "[]";

        CancellationSensitiveRepository repo = new(apprentice);

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>());

        Assert.True(GetConcurrencyGate(service).TryAcquire(1, out _));

        Result<string> started = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        Assert.Contains(apprenticeId, GetPendingStarts(service));

        using CancellationTokenSource request = new();

        await request.CancelAsync();

        Result<string> cancelled = await service.CancelAsync(apprenticeId, request.Token);

        Assert.True(cancelled.IsSuccess, cancelled.IsFailure ? cancelled.Error.Message : null);

        Assert.DoesNotContain(apprenticeId, GetPendingStarts(service));

        Assert.Equal(ApprenticeStatus.Cancelled.ToString(), repo.Get(apprenticeId).Status);
    }

    /// <summary>
    /// The run's own pause record, written after its execution was cancelled, must never turn an operator's
    /// <c>Cancelled</c> into <c>Paused</c> — even when the Cancel lands right after the run looked at the row.
    /// </summary>
    [Fact]
    public async Task PersistPausedIfCurrent_OperatorCancelLandingAfterTheRunReadsTheRow_KeepsCancelled()
    {
        Guid apprenticeId = Guid.NewGuid();

        AfterFirstReadRepository repo = new(RunningApprenticeWithOneStep(apprenticeId))
        {
            AfterFirstRead = row => row.Status = ApprenticeStatus.Cancelled.ToString(),
        };

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>());

        SeedExecutionGeneration(service, apprenticeId, 1L);

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("PersistPausedIfCurrentAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        await ((Task)method!.Invoke(service, [repo, apprenticeId, 1L])!).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ApprenticeStatus.Cancelled.ToString(), repo.Get(apprenticeId).Status);
    }

    /// <summary>
    /// An operator pauses and re-weaves the step that is running, between the end of its stream and its commit.
    /// The completion belongs to the step that ran, not to its replacement: the operator's plan stands and the
    /// replacement is not marked completed with the old step's result.
    /// </summary>
    [Fact]
    public async Task Reweave_landing_between_step_end_and_its_commit_is_not_overwritten_by_the_commit()
    {
        Guid apprenticeId = Guid.NewGuid();

        InMemoryApprenticeRepository repo = new(TwoStepApprentice(apprenticeId, ApprenticeStatus.Running));

        StepThenShiftingFateIntelligence intelligence = new(
            afterStreamResult: () => repo.Mutate(apprenticeId, row => PauseAndReweave(row, atStep: 0, "Rewoven step")),
            duringShiftingFate: null);

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), persisted.Status);

        Assert.Equal(0, persisted.CurrentStep);

        PlanStep replacement = Assert.Single(ApprenticeRepository.DeserializePlan(persisted.Plan));

        Assert.Equal("Rewoven step", replacement.Description);

        Assert.NotEqual("completed", replacement.Status);

        Assert.Null(replacement.Result);

        Assert.Equal(0, intelligence.ShiftingFateCalls);
    }

    /// <summary>
    /// An operator pauses and re-weaves the tail while the Shifting Fate evaluation runs. The evaluation's own
    /// revision was computed from the plan before that, so it is dropped rather than written over the
    /// operator's.
    /// </summary>
    [Fact]
    public async Task Shifting_fate_revision_does_not_overwrite_an_operator_reweave_made_during_the_evaluation()
    {
        Guid apprenticeId = Guid.NewGuid();

        InMemoryApprenticeRepository repo = new(TwoStepApprentice(apprenticeId, ApprenticeStatus.Running));

        StepThenShiftingFateIntelligence intelligence = new(
            afterStreamResult: null,
            duringShiftingFate: () => repo.Mutate(apprenticeId, row => PauseAndReweave(row, atStep: 1, "Operator tail")),
            shiftingFateAnswer: """[{"index":2,"description":"Fate tail"}]""");

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(1, intelligence.ShiftingFateCalls);

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), persisted.Status);

        Assert.Equal(1, persisted.CurrentStep);

        List<PlanStep> plan = ApprenticeRepository.DeserializePlan(persisted.Plan);

        Assert.Equal("completed", plan[0].Status);

        Assert.Equal("Operator tail", plan[1].Description);
    }

    /// <summary>
    /// Binding the run's new Session writes the binding alone. A plan an operator re-wove while the Session was
    /// being created is not reverted to the copy this unit read before it.
    /// </summary>
    [Fact]
    public async Task Session_binding_writes_only_the_session_and_keeps_a_plan_rewoven_meanwhile()
    {
        Guid apprenticeId = Guid.NewGuid();

        Guid campaignId = Guid.NewGuid();

        Guid sessionId = Guid.NewGuid();

        InMemoryApprenticeRepository repo = new(SessionlessApprentice(
            apprenticeId,
            campaignId,
            Path.Combine(Path.GetTempPath(), $"apprentice-bind-only-{Guid.NewGuid():N}")));

        RecordingCanonicalCampaignContextResolver resolver = new(CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 3,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null));

        RecordingSessionTurnBeginStore turnStore = new(sessionId)
        {
            AfterCreate = () => repo.Mutate(apprenticeId, row => PauseAndReweave(row, atStep: 0, "Rewoven step")),
        };

        CountingSessionStepIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence,
            campaignResolver: resolver,
            turnBeginStore: turnStore);

        Result<string> started = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(sessionId, persisted.SessionId);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), persisted.Status);

        Assert.Equal("Rewoven step", Assert.Single(ApprenticeRepository.DeserializePlan(persisted.Plan)).Description);

        Assert.Equal(0, intelligence.StreamCalls);
    }

    /// <summary>
    /// A queued start with a known plan moves from <c>Planning</c> to <c>Running</c> before its first step. An
    /// operator's Pause that landed after the run read the row is not reverted by that move, and no step runs.
    /// </summary>
    [Fact]
    public async Task Known_plan_start_does_not_revert_a_pause_that_landed_after_the_run_read_the_row()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = RunningApprenticeWithOneStep(apprenticeId);

        apprentice.Status = ApprenticeStatus.Planning.ToString();

        AfterFirstReadRepository repo = new(apprentice)
        {
            AfterFirstRead = row => row.Status = ApprenticeStatus.Paused.ToString(),
        };

        SuccessfulStepIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(ApprenticeStatus.Paused.ToString(), repo.Get(apprenticeId).Status);

        Assert.Equal(0, intelligence.StreamCalls);
    }

    private static Apprentice TwoStepApprentice(Guid apprenticeId, ApprenticeStatus status)
    {
        Apprentice apprentice = RunningApprenticeWithOneStep(apprenticeId);

        apprentice.Status = status.ToString();

        apprentice.Plan = ApprenticeRepository.SerializePlan(
        [
            new PlanStep { Index = 1, Description = "First step" },
            new PlanStep { Index = 2, Description = "Second step" },
        ]);

        return apprentice;
    }

    /// <summary>What the execution's step commit writes: step 0 completed and the position advanced past it.</summary>
    private static void CommitFirstStep(Apprentice row)
    {
        List<PlanStep> plan = ApprenticeRepository.DeserializePlan(row.Plan);

        plan[0] = plan[0] with { Status = "completed", Result = "first step done" };

        row.Plan = ApprenticeRepository.SerializePlan(plan);

        row.CurrentStep = 1;
    }

    private static void AssertFirstStepCommitted(Apprentice persisted)
    {
        Assert.Equal(1, persisted.CurrentStep);

        PlanStep first = ApprenticeRepository.DeserializePlan(persisted.Plan)[0];

        Assert.Equal("completed", first.Status);

        Assert.Equal("first step done", first.Result);
    }

    /// <summary>What an operator's Pause followed by a Reweave at <paramref name="atStep"/> leaves in the row.</summary>
    private static void PauseAndReweave(Apprentice row, int atStep, string description)
    {
        row.Status = ApprenticeStatus.Paused.ToString();

        row.Plan = ApprenticeRepository.SerializePlan(ApprenticeExecutionPolicy.MergePlanTail(
            ApprenticeRepository.DeserializePlan(row.Plan),
            atStep,
            [new PlanStep { Description = description }]));
    }

    /// <summary>
    /// Changes the stored row once, right after the first read returns its snapshot: the shape of a write from
    /// the other side landing between the code under test's read and its own write.
    /// </summary>
    private sealed class AfterFirstReadRepository(params Apprentice[] apprentices)
        : InMemoryApprenticeRepository(apprentices)
    {
        private int _reads;

        internal Action<Apprentice>? AfterFirstRead { get; init; }

        public override async Task<Apprentice?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            Apprentice? snapshot = await base.GetByIdAsync(id, cancellationToken);

            if (snapshot is not null
                && AfterFirstRead is { } change
                && Interlocked.Increment(ref _reads) == 1)
            {
                Mutate(id, change);
            }

            return snapshot;
        }
    }
}
