using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

[Collection("ApprenticeReliability")]
public sealed class ApprenticeServiceReliabilityTests
{
    [Fact]
    public async Task ExecuteStepStream_StructuredToolDenial_FailsRegardlessOfWording()
    {
        Guid apprenticeId = Guid.NewGuid();

        IntelligenceEvent denial = new(
            IntelligenceEventType.ToolResult,
            Message: "execute_command",
            Data: "The operator policy blocked this invocation.",
            ToolCall: new IntelligenceToolCallEvent(
                "denied-call",
                "execute_command",
                "{}"),
            ToolDenied: true);

        ScriptedStreamIntelligence intelligence = new(denial);

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

        Assert.True(outcome.ToolDenied);

        Assert.False(outcome.IsRetryable);

        Assert.Equal(denial.Data, outcome.ErrorMessage);
    }
    [Fact]
    public async Task ExecuteStepStream_LegacyDeniedWardFollowedByResult_CompletesSuccessfully()
    {
        Guid apprenticeId = Guid.NewGuid();

        ScriptedStreamIntelligence intelligence = new(
            new IntelligenceEvent(
                IntelligenceEventType.WardResolved,
                Message: "legacy audit record",
                WardId: "legacy-ward",
                WardToolName: "write_file",
                WardAllowed: false),
            new IntelligenceEvent(
                IntelligenceEventType.Result,
                Message: "completed"));

        ApprenticeService service = CreateService(
            new InMemoryApprenticeRepository(),
            new ArcanumSettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        ApprenticeService.StepExecutionOutcome outcome = await ExecuteStepStreamAsync(
            service,
            intelligence,
            TestApprentice(apprenticeId));

        Assert.False(outcome.StepFailed);

        Assert.False(outcome.ToolDenied);

        Assert.False(outcome.IsRetryable);

        Assert.Equal("completed", outcome.ResultText);

        Assert.Null(outcome.ErrorMessage);
    }
    [Fact]
    public async Task ExecuteStepStream_DenialPhraseInSuccessfulToolData_DoesNotFailTheStep()
    {
        Guid apprenticeId = Guid.NewGuid();

        ScriptedStreamIntelligence intelligence = new(
            new IntelligenceEvent(
                IntelligenceEventType.ToolResult,
                Message: "read_file",
                Data: "Forbidden art denied appears in ordinary successful content.",
                ToolCall: new IntelligenceToolCallEvent(
                    "successful-content-call",
                    "read_file",
                    "{}"),
                ToolDenied: false),
            new IntelligenceEvent(
                IntelligenceEventType.Result,
                Message: "completed"));

        ApprenticeService service = CreateService(
            new InMemoryApprenticeRepository(),
            new ArcanumSettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        ApprenticeService.StepExecutionOutcome outcome = await ExecuteStepStreamAsync(
            service,
            intelligence,
            TestApprentice(apprenticeId));

        Assert.False(outcome.StepFailed);

        Assert.False(outcome.ToolDenied);

        Assert.Equal("completed", outcome.ResultText);
    }
    [Fact]
    public async Task ExecuteStepStream_ToolNameContainingDenialPhrase_DoesNotFailTheStep()
    {
        Guid apprenticeId = Guid.NewGuid();

        ScriptedStreamIntelligence intelligence = new(
            new IntelligenceEvent(
                IntelligenceEventType.ToolResult,
                Message: "arbitrary_forbidden art denied_tool",
                Data: "ok",
                ToolCall: new IntelligenceToolCallEvent(
                    "successful-call",
                    "arbitrary_forbidden art denied_tool",
                    "{}")),
            new IntelligenceEvent(
                IntelligenceEventType.Result,
                Message: "completed"));

        ApprenticeService service = CreateService(
            new InMemoryApprenticeRepository(),
            new ArcanumSettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        ApprenticeService.StepExecutionOutcome outcome = await ExecuteStepStreamAsync(
            service,
            intelligence,
            TestApprentice(apprenticeId));

        Assert.False(outcome.StepFailed);

        Assert.False(outcome.ToolDenied);

        Assert.Equal("completed", outcome.ResultText);
    }
    [Fact]
    public async Task ExecuteStepStream_ReasoningDenialText_IsIgnoredAndNotPublished()
    {
        Guid apprenticeId = Guid.NewGuid();

        ScriptedStreamIntelligence intelligence = new(
            new IntelligenceEvent(
                IntelligenceEventType.Reasoning,
                Message: "Forbidden art denied in reasoning.",
                Data: "Forbidden art denied in hidden reasoning."),
            new IntelligenceEvent(
                IntelligenceEventType.ToolResult,
                Message: "read_file",
                Data: "ok",
                ToolCall: new IntelligenceToolCallEvent(
                    "read-call",
                    "read_file",
                    "{}")),
            new IntelligenceEvent(
                IntelligenceEventType.Result,
                Message: "completed"));

        ArcanumSettings settings = new();

        ChronicleHub hub = new();

        ApprenticeService service = CreateService(
            new InMemoryApprenticeRepository(),
            settings,
            new CapturingLogger<ApprenticeService>(),
            intelligence,
            hub: hub);

        using CancellationTokenSource chronicleCancellation = new();

        await using IAsyncEnumerator<ApprenticeEvent> chronicle =
            hub.SubscribeAsync(
                    apprenticeId,
                    chronicleCancellation.Token)
                .GetAsyncEnumerator();

        Task<bool> firstEvent = chronicle.MoveNextAsync().AsTask();

        ApprenticeService.StepExecutionOutcome outcome = await ExecuteStepStreamAsync(
            service,
            intelligence,
            TestApprentice(apprenticeId));

        Assert.False(outcome.StepFailed);

        Assert.Equal("completed", outcome.ResultText);

        Assert.True(
            await firstEvent.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            IntelligenceEventType.ToolResult,
            chronicle.Current.WizardEvent?.Type);

        Task<bool> unexpectedEvent = chronicle.MoveNextAsync().AsTask();

        await chronicleCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => unexpectedEvent);
    }
    // P8: intervene-and-resume at full capacity must acquire the slot FIRST and
    // return MaxReached with NO state mutation (no plan/checkpoint/publish).

    [Fact]
    public async Task Intervene_ResumeAtCapacity_ReturnsCapacityFailureWithNoStateMutation()
    {
        Guid escalade = Guid.NewGuid();

        string originalPlan = ApprenticeRepository.SerializePlan(
        [
            new PlanStep { Index = 1, Description = "Stuck step", Status = "failed", Attempts = 3 },
        ]);

        Apprentice apprentice = new()
        {
            Id = escalade,
            Name = "Reliability-1",
            Goal = "Survive a capacity-rejected resume.",
            WorkspacePath = "/tmp/arcanum-test",
            Status = ApprenticeStatus.Escalated.ToString(),
            Plan = originalPlan,
            CurrentStep = 0,
            CheckpointData = null,
            ErrorMessage = "Need Divine Intervention.",
        };
        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = CreateCapacitySettings();

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(repo, settings, logger);

        // Pre-fill the concurrency gate so the intervene resume cannot acquire a slot.

        Assert.True(GetConcurrencyGate(service).TryAcquire(1, out _));

        Result<string> result = await service
            .InterveneAsync(escalade, "Resume the quest.", resume: true, CancellationToken.None)
;

        Assert.True(result.IsFailure);

        Assert.Equal("Apprentice.MaxReached", result.Error.Code);

        Apprentice persisted = repo.Get(escalade);

        Assert.Equal(ApprenticeStatus.Escalated.ToString(), persisted.Status);

        Assert.Equal(originalPlan, persisted.Plan);

        Assert.Null(persisted.CheckpointData);

        Assert.Equal("Need Divine Intervention.", persisted.ErrorMessage);
    }
    [Fact]
    public async Task StartAsync_QueuedBeyondFormerCapacity_IsDurableAndEventuallyRuns()
    {
        Apprentice first = TestApprentice(Guid.NewGuid());

        Apprentice second = TestApprentice(Guid.NewGuid());

        StartSignalingApprenticeRepository repo = new(first, second);

        ArcanumSettings settings = CreateCapacitySettings();

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(
            repo,
            settings,
            logger,
            new FailingPlanIntelligence(),
            new NotImplementedGrimoireRepository());

        Guid holderId = Guid.NewGuid();

        Assert.True(TryAcquireExecutionSlot(service, holderId));

        Result<string> firstStart = await service.StartAsync(first.Id, CancellationToken.None);

        Result<string> secondStart = await service.StartAsync(second.Id, CancellationToken.None);

        Assert.True(firstStart.IsSuccess, firstStart.Error.Message);

        Assert.True(secondStart.IsSuccess, secondStart.Error.Message);

        Assert.Equal(ApprenticeStatus.Planning.ToString(), repo.Get(first.Id).Status);

        Assert.Equal(ApprenticeStatus.Planning.ToString(), repo.Get(second.Id).Status);

        ReleaseAcquiredExecutionSlot(service, holderId);

        await repo.WaitForExecutionAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(5));

        await repo.WaitForExecutionAsync(second.Id).WaitAsync(TimeSpan.FromSeconds(5));
    }
    // W2.4 Fix 1 (P2): an apprentice queued in _pendingStarts (status Idle, which
    // IsCancellable excludes) must be cancellable. CancelAsync must drain it from
    // the pending queue and mark it Cancelled (was: not cancellable, stayed queued).

    [Fact]
    public async Task CancelAsync_ApprenticePendingInQueue_DrainsAndMarksCancelled()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Reliability-Fix1",
            Goal = "Be cancellable while pending in the start queue.",
            WorkspacePath = "/tmp/arcanum-test",
            Status = ApprenticeStatus.Idle.ToString(),
            Plan = "[]",
            CurrentStep = 0,
            CheckpointData = null,
        };
        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = CreateCapacitySettings();

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(repo, settings, logger);

        // Pre-fill the gate so StartAsync cannot acquire a slot and enqueues instead.

        Assert.True(GetConcurrencyGate(service).TryAcquire(1, out _));

        _ = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.Contains(apprenticeId, GetPendingStarts(service));

        Result<string> result = await service.CancelAsync(apprenticeId, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.DoesNotContain(apprenticeId, GetPendingStarts(service));

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Cancelled.ToString(), persisted.Status);
    }
    // W2.4 Fix 2 (P2): StartAsync at capacity with queue space must return SUCCESS
    // (the apprentice is queued) rather than Failure("Apprentice.MaxReached"). No
    // execution task is started yet; the id sits in _pendingStarts and not in
    // _activeTasks.

    [Fact]
    public async Task StartAsync_AtCapacity_WithQueueSpace_ReturnsSuccessAndQueues()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Reliability-Fix2",
            Goal = "Survive being queued on capacity.",
            WorkspacePath = "/tmp/arcanum-test",
            Status = ApprenticeStatus.Idle.ToString(),
            Plan = "[]",
            CurrentStep = 0,
            CheckpointData = null,
        };
        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = CreateCapacitySettings();

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(repo, settings, logger);

        Assert.True(GetConcurrencyGate(service).TryAcquire(1, out _));

        Result<string> result = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Contains(apprenticeId, GetPendingStarts(service));

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));
    }
    [Fact]
    public async Task PendingStart_CapacityLossPublicationRemainsInsideLifecycleCriticalSection()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = TestApprentice(apprenticeId);

        apprentice.Status = ApprenticeStatus.Planning.ToString();

        apprentice.Plan = "[]";

        InMemoryApprenticeRepository repo = new(apprentice);

        BlockingPlanIntelligence intelligence = new();

        BlockingCapacityLossExecutionCapacity capacity = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence,
            new NotImplementedGrimoireRepository(),
            executionCapacity: capacity);

        Guid holderId = Guid.NewGuid();

        Assert.True(TryAcquireExecutionSlot(service, holderId));

        Task<ExecutionSlotAttempt>? admission = null;

        try
        {
            Task<bool> atomicPublicationProbe = Task.Factory.StartNew(
                () =>
                {
                    using Lock.Scope pendingPublication =
                        GetPendingStartsLock(service).EnterScope();

                    admission = Task.Factory.StartNew(
                        () => TryAcquireExecutionSlot(
                            service,
                            apprenticeId,
                            queueOnCapacity: true),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);

                    capacity.CapacityLossReached.Task
                        .WaitAsync(TimeSpan.FromSeconds(10))
                        .GetAwaiter()
                        .GetResult();

                    capacity.AllowCapacityLossReturn.TrySetResult();

                    return SpinWait.SpinUntil(
                        () => CanEnterAndReleaseExecutionLifecycleLock(service),
                        TimeSpan.FromSeconds(1));
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            bool publicationEscapedLifecycle = await atomicPublicationProbe;

            Assert.False(
                publicationEscapedLifecycle,
                "The lifecycle lock must remain held until the exact pending identity is published.");

            Assert.NotNull(admission);

            ExecutionSlotAttempt queued = await admission!.WaitAsync(
                TimeSpan.FromSeconds(10));

            Assert.True(queued.Accepted, queued.Failure?.Error.Message);

            Assert.True(queued.Queued);

            Assert.Equal([apprenticeId], GetPendingStarts(service).ToArray());

            ReleaseAcquiredExecutionSlot(service, holderId);

            await WaitUntilAsync(() => intelligence.ExecuteCalls == 1);

            Assert.DoesNotContain(apprenticeId, GetPendingStarts(service));

            Assert.False(GetPendingStartIds(service).ContainsKey(apprenticeId));

            Assert.True(GetActiveTasks(service).ContainsKey(apprenticeId));

            Assert.Equal(1, GetConcurrencyGate(service).RunningCount);
        }
        finally
        {
            capacity.AllowCapacityLossReturn.TrySetResult();

            ReleaseAcquiredExecutionSlot(service, holderId);

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
    [Fact]
    public async Task PendingStart_ConcurrentCapacityWinnerRetainsExactIdentityAndLaterRuns()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = TestApprentice(apprenticeId);

        apprentice.Status = ApprenticeStatus.Idle.ToString();

        apprentice.Plan = "[]";

        InMemoryApprenticeRepository repo = new(apprentice);

        BlockingPlanIntelligence intelligence = new();

        ConcurrentWinnerExecutionCapacity capacity = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence,
            new NotImplementedGrimoireRepository(),
            executionCapacity: capacity);

        Guid holderId = Guid.NewGuid();

        Assert.True(TryAcquireExecutionSlot(service, holderId));

        Result<string> queued = await service.StartAsync(
            apprenticeId,
            CancellationToken.None);

        Assert.True(queued.IsSuccess, queued.Error.Message);

        Assert.Equal([apprenticeId], GetPendingStarts(service).ToArray());

        capacity.ArmConcurrentWinner();

        try
        {
            ReleaseAcquiredExecutionSlot(service, holderId);

            Assert.True(capacity.WinnerHeld);

            Assert.Equal([apprenticeId], GetPendingStarts(service).ToArray());

            Assert.True(GetPendingStartIds(service).ContainsKey(apprenticeId));

            capacity.ReleaseConcurrentWinner();

            InvokeTryDequeuePendingStart(service);

            await WaitUntilAsync(() => intelligence.ExecuteCalls == 1);

            Assert.DoesNotContain(apprenticeId, GetPendingStarts(service));

            Assert.False(GetPendingStartIds(service).ContainsKey(apprenticeId));

            Assert.True(GetActiveTasks(service).ContainsKey(apprenticeId));

            Assert.Equal(1, GetConcurrencyGate(service).RunningCount);
        }
        finally
        {
            capacity.ReleaseConcurrentWinner();

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
    [Fact]
    public async Task PendingStart_DequeueDuringStopRetainsExactIdentityWithoutStarting()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = TestApprentice(apprenticeId);

        apprentice.Status = ApprenticeStatus.Idle.ToString();

        apprentice.Plan = "[]";

        InMemoryApprenticeRepository repo = new(apprentice);

        BlockingPlanIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence,
            new NotImplementedGrimoireRepository());

        Guid holderId = Guid.NewGuid();

        Assert.True(TryAcquireExecutionSlot(service, holderId));

        Result<string> queued = await service.StartAsync(
            apprenticeId,
            CancellationToken.None);

        Assert.True(queued.IsSuccess, queued.Error.Message);

        Task stop = service.StopAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(() => GetStopping(service));

            InvokeTryDequeuePendingStart(service);

            Assert.Equal([apprenticeId], GetPendingStarts(service).ToArray());

            Assert.True(GetPendingStartIds(service).ContainsKey(apprenticeId));

            Assert.Equal(0, intelligence.ExecuteCalls);
        }
        finally
        {
            ReleaseAcquiredExecutionSlot(service, holderId);

            await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }
    [Fact]
    public async Task PendingStart_AlreadyRunningEntryIsDroppedWhileCapacityEntryIsRetained()
    {
        Guid activeId = Guid.NewGuid();

        Guid waitingId = Guid.NewGuid();

        using ApprenticeService service = CreateService(
            new InMemoryApprenticeRepository(),
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>());

        Assert.True(TryAcquireExecutionSlot(service, activeId));

        Assert.True(GetPendingStartIds(service).TryAdd(activeId, 0));

        GetPendingStarts(service).Enqueue(activeId);

        Assert.True(GetPendingStartIds(service).TryAdd(waitingId, 0));

        GetPendingStarts(service).Enqueue(waitingId);

        InvokeTryDequeuePendingStart(service);

        Assert.Equal([waitingId], GetPendingStarts(service).ToArray());

        Assert.False(GetPendingStartIds(service).ContainsKey(activeId));

        Assert.True(GetPendingStartIds(service).ContainsKey(waitingId));

        ReleaseAcquiredExecutionSlot(service, activeId);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(waitingId));

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }
    // W2.4 Fix 3 (P2): a non-cancel exception in ONE Simulacrum branch must NOT
    // fail the whole Task.WhenAll. RunSimulacrumBranchAsync must catch the
    // exception and return a Terminal SingleStepResult (was: exception escaped,
    // faulting the whole group after siblings may have run tools).

    [Fact]
    public async Task RunSimulacrumBranchAsync_NonCancelException_ReturnsTerminalNotThrows()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Reliability-Fix3",
            Goal = "Isolate a single branch fault.",
            WorkspacePath = "/tmp/arcanum-test",
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 0, Description = "Faulty branch" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = CreateCapacitySettings();

        CapturingLogger<ApprenticeService> logger = new();

        ThrowingIntelligenceProvider intelligence = new();

        ApprenticeService service = CreateService(repo, settings, logger, intelligence);

        using SemaphoreSlim gate = new(1, 1);

        using CancellationTokenSource linkedCts = new();

        List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("RunSimulacrumBranchAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task task = (Task)method!.Invoke(service, new object[]
        {
            gate,
            apprentice,
            plan,
            0,
            settings.ResolveApprentices(),
            apprenticeId,
            linkedCts,
        })!;

        // RED: awaiting a faulted task throws InvalidOperationException.
        // GREEN: the branch returns a Terminal SingleStepResult.

        await task.WaitAsync(TimeSpan.FromSeconds(15));

        object? result = task.GetType().GetProperty("Result")!.GetValue(task);

        Assert.NotNull(result);

        object? kind = result!.GetType().GetProperty("Kind")!.GetValue(result);

        Assert.NotNull(kind);

        Assert.Equal("Terminal", kind!.ToString());
    }
    [Fact]
    public async Task SimulacrumStampsChildrenAndShiftsFateBeforeCheckpointAdvance()
    {
        Guid apprenticeId = Guid.NewGuid();

        Guid childId = Guid.NewGuid();

        List<PlanStep> plan =
        [
            new PlanStep { Index = 0, Description = "First branch", IsParallel = true },
            new PlanStep { Index = 1, Description = "Second branch", IsParallel = true },
            new PlanStep { Index = 2, Description = "Original tail" },
        ];

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Reliable Simulacrum",
            Goal = "Commit only after every grouped effect is durable.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(plan),
            CurrentStep = 0,
            CheckpointData = ApprenticeRepository.SerializeCheckpoint(new ApprenticeCheckpoint
            {
                CurrentStep = 0,
                ConversationSummary = "keep me",
                CompletedToolCallIds = ["receipt-1"],
            }),
            SessionId = Guid.NewGuid(),
        };
        SimulacrumOrderingRepository repo = new(apprentice, childId);

        SimulacrumOrderingIntelligence intelligence = new(childId);

        ArcanumSettings settings = CreateCapacitySettings();

        ApprenticeService service = CreateService(
            repo,
            settings,
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        using CancellationTokenSource linkedCts = new();

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("ExecuteSimulacrumGroupAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task<bool> execution = (Task<bool>)method!.Invoke(
            service,
            new object[]
            {
                repo,
                intelligence,
                apprentice,
                plan,
                0,
                2,
                settings.ResolveApprentices(),
                apprenticeId,
                linkedCts,
            })!;

        try
        {
            await repo.ChildStampReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            AssertUnadvanced(repo.Get(apprenticeId));

            repo.AllowChildStamp.TrySetResult();

            await intelligence.ShiftingFateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            AssertUnadvanced(repo.Get(apprenticeId));

            intelligence.AllowShiftingFate.TrySetResult();

            Assert.True(await execution.WaitAsync(TimeSpan.FromSeconds(10)));

            Apprentice completed = repo.Get(apprenticeId);

            Assert.Equal(2, completed.CurrentStep);

            ApprenticeCheckpoint checkpoint = Assert.IsType<ApprenticeCheckpoint>(
                ApprenticeRepository.DeserializeCheckpoint(completed.CheckpointData));

            Assert.Equal(2, checkpoint.CurrentStep);

            Assert.Equal("keep me", checkpoint.ConversationSummary);

            Assert.Equal(["receipt-1"], checkpoint.CompletedToolCallIds);

            List<PlanStep> completedPlan = ApprenticeRepository.DeserializePlan(completed.Plan);

            Assert.Equal("completed", completedPlan[0].Status);

            Assert.Equal("completed", completedPlan[1].Status);

            Assert.Equal("Revised tail", completedPlan[2].Description);
        }
        finally
        {
            repo.AllowChildStamp.TrySetResult();

            intelligence.AllowShiftingFate.TrySetResult();

            _ = await Record.ExceptionAsync(() => execution);
        }
        static void AssertUnadvanced(Apprentice persisted)
        {
            Assert.Equal(0, persisted.CurrentStep);

            ApprenticeCheckpoint checkpoint = Assert.IsType<ApprenticeCheckpoint>(
                ApprenticeRepository.DeserializeCheckpoint(persisted.CheckpointData));

            Assert.Equal(0, checkpoint.CurrentStep);

            Assert.Equal("keep me", checkpoint.ConversationSummary);

            Assert.Equal(["receipt-1"], checkpoint.CompletedToolCallIds);
        }
    }
    [Fact]
    public async Task DeniedRecoveryCreatesNoScope()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        CovenantExclusiveRecoveryOwner owner = new(
            Guid.NewGuid(),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(new byte[32]));

        Assert.True(inner.BeginOrResumeExclusive(owner).IsSuccess);

        RefusingScopeFactory scopes = new();

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(gate);

        await using ServiceProvider provider = services.BuildServiceProvider();

        using ApprenticeService service = ActivatorUtilities.CreateInstance<ApprenticeService>(
            provider,
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            new CapturingLogger<ApprenticeService>());

        using CancellationTokenSource cancellation = new();

        Task recovery = InvokeResumeCrashRecoveryAsync(service, cancellation.Token);

        Assert.Equal(0, scopes.Created);

        Assert.False(recovery.IsCompleted);

        Assert.Equal([GrimoireWorkKind.ApprenticeExecution], gate.RequestedWorkKinds);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
    }
    [Fact]
    public async Task DeniedPlanPreservesExecutionIdentityWithoutProviderOrScope()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Deferred planner",
            Goal = "Keep the same execution while maintenance is closed.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Idle.ToString(),
            Plan = "[]",
            CurrentStep = 0,
        };
        InMemoryApprenticeRepository repo = new(apprentice);

        BlockingPlanIntelligence intelligence = new();

        SingleServiceScopeFactory scopes = new(
            repo,
            intelligence,
            new NotImplementedGrimoireRepository());

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        Assert.True(inner.BeginOrResumeExclusive(Owner()).IsSuccess);

        using ApprenticeService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            new CapturingLogger<ApprenticeService>(),
            gate);

        Result<string> started = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(started.IsSuccess, started.Error.Message);

        try
        {
            await WaitUntilAsync(
                () => scopes.Created > 1 || gate.RequestedWorkKinds.Count > 0);

            Assert.Equal(1, scopes.Created);

            Assert.Equal(0, intelligence.ExecuteCalls);

            Assert.Equal([GrimoireWorkKind.ApprenticeExecution], gate.RequestedWorkKinds);

            Assert.True(GetActiveTasks(service).TryGetValue(apprenticeId, out Task? active));

            Assert.False(active!.IsCompleted);

            Assert.Equal(1, GetExecutionGeneration(service, apprenticeId));

            Assert.Equal(1, GetConcurrencyGate(service).RunningCount);

            Assert.Contains(apprenticeId, GetFreshStartIds(service));

            Apprentice persisted = repo.Get(apprenticeId);

            Assert.Equal(ApprenticeStatus.Planning.ToString(), persisted.Status);

            Assert.Equal(0, persisted.CurrentStep);

            Assert.Empty(ApprenticeRepository.DeserializePlan(persisted.Plan));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(ApprenticeStatus.Planning.ToString(), repo.Get(apprenticeId).Status);

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }
    [Fact]
    public async Task DeniedPlanFrontierRetainsFreshIdentityUntilReopen()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Deferred plan frontier",
            Goal = "Spend the fresh-start identity only after the frontier is won.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Planning.ToString(),
            Plan = "[]",
            CurrentStep = 0,
        };
        FrontierReadRepository repo = new(apprentice);

        SuccessfulPlanIntelligence intelligence = new();

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        using ApprenticeService service = new(
            new SingleServiceScopeFactory(
                repo,
                intelligence,
                new NotImplementedGrimoireRepository()),
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            new CapturingLogger<ApprenticeService>(),
            gate);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        GetFreshStartIds(service).TryAdd(apprenticeId, 0);

        BeginExecutionTask(service, apprenticeId);

        await repo.ReadReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task retainedTask = GetActiveTasks(service)[apprenticeId];

        long retainedGeneration = GetExecutionGeneration(service, apprenticeId);

        await using IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        repo.AllowRead.TrySetResult();

        await WaitUntilAsync(() => gate.EffectGroupAttempts > 0);

        Assert.True((await inner.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None)).IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = (await inner
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        Assert.Equal(0, intelligence.ExecuteCalls);

        Assert.Contains(apprenticeId, GetFreshStartIds(service));

        Assert.Same(retainedTask, GetActiveTasks(service)[apprenticeId]);

        Assert.Equal(retainedGeneration, GetExecutionGeneration(service, apprenticeId));

        Assert.True((await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(1, intelligence.ExecuteCalls);

        Assert.Equal(2, gate.EffectGroupAttempts);

        Assert.DoesNotContain(apprenticeId, GetFreshStartIds(service));

        Assert.Equal(retainedGeneration, GetExecutionGeneration(service, apprenticeId));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }
    [Fact]
    public async Task FreshPlanReopensWithSameIdentityAndOneStartedEvent()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Fresh planner",
            Goal = "Begin exactly once after maintenance.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Idle.ToString(),
            Plan = "[]",
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        InMemoryApprenticeRepository repo = new(apprentice);

        SuccessfulPlanIntelligence intelligence = new();

        SingleServiceScopeFactory scopes = new(
            repo,
            intelligence,
            new NotImplementedGrimoireRepository());

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        await using IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        Assert.True((await inner.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None)).IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = (await inner
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        ChronicleHub hub = new();

        CapturingLogger<ApprenticeService> logger = new();

        using ApprenticeService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            hub,
            logger,
            gate);

        Result<string> started = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(started.IsSuccess, started.Error.Message);

        Task retainedTask = GetActiveTasks(service)[apprenticeId];

        long retainedGeneration = GetExecutionGeneration(service, apprenticeId);

        Assert.Contains(apprenticeId, GetFreshStartIds(service));

        List<ApprenticeEvent> captured = [];

        using CancellationTokenSource chronicleCancellation = new(TimeSpan.FromSeconds(10));

        TaskCompletionSource primed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task collector = CollectChronicleAsync(
            service.SubscribeChronicleAsync(apprenticeId, chronicleCancellation.Token),
            captured,
            primed,
            chronicleCancellation.Token);

        hub.Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.EventsDropped,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            Summary = "prime",
        });

        await primed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (captured)
        {
            captured.Clear();
        }
        Assert.True((await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        await collector.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(retainedTask.IsCompletedSuccessfully);

        Assert.Equal(retainedGeneration, GetExecutionGeneration(service, apprenticeId));

        Assert.Equal(2, intelligence.ExecuteCalls);

        Assert.Equal(1, intelligence.StreamCalls);

        Assert.Equal(2, gate.EffectGroupAttempts);

        Assert.DoesNotContain(apprenticeId, GetFreshStartIds(service));

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), persisted.Status);

        Assert.Equal(1, persisted.CurrentStep);

        Assert.Null(persisted.ErrorMessage);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);

        lock (captured)
        {
            Assert.Single(captured, e => e.Type == ApprenticeEventType.ApprenticeStarted);

            Assert.DoesNotContain(captured, e => e.Type == ApprenticeEventType.ApprenticeResumed);

            Assert.DoesNotContain(captured, e => e.Type == ApprenticeEventType.ApprenticeFailed);

            Assert.Single(captured, e => e.Type == ApprenticeEventType.ApprenticeCompleted);
        }
    }
    [Fact]
    public async Task WinningPlanDrainsInExactFrontierDisposalOrder()
    {
        Guid apprenticeId = Guid.NewGuid();

        ConcurrentQueue<string> phases = new();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Winning planner",
            Goal = "Conclude the durable plan before maintenance proceeds.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Planning.ToString(),
            Plan = "[]",
            CurrentStep = 0,
        };
        BlockingPlanPersistenceRepository repo = new(apprentice, phases);

        BlockingOrderedPlanIntelligence intelligence = new(phases);

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        FrontierDisposalBarriers barriers = new(phases, gate);

        SingleServiceScopeFactory scopes = new(
            repo,
            intelligence,
            new NotImplementedGrimoireRepository(),
            barriers.BeforeScopeDisposalAsync);

        using ApprenticeService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            new CapturingLogger<ApprenticeService>(),
            gate);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        GetFreshStartIds(service).TryAdd(apprenticeId, 0);

        BeginExecutionTask(service, apprenticeId);

        await intelligence.ProviderReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["provider"], phases);

        await using IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        Task<Result> drain = inner
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        await Task.Yield();

        Assert.False(drain.IsCompleted);

        intelligence.AllowProvider.TrySetResult();

        await repo.PlanPersistenceReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["provider", "durable-plan"], phases);

        Assert.False(drain.IsCompleted);

        repo.AllowPlanPersistence.TrySetResult();

        await barriers.EffectGroupDisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            ["provider", "durable-plan", "effect-group-disposal"],
            phases);

        Assert.False(drain.IsCompleted);

        barriers.AllowEffectGroupDisposal.TrySetResult();

        await barriers.ScopeDisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            [
                "provider",
                "durable-plan",
                "effect-group-disposal",
                "scope-disposal",
            ],
            phases);

        Assert.False(drain.IsCompleted);

        barriers.AllowScopeDisposal.TrySetResult();

        await barriers.WorkLeaseDisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            [
                "provider",
                "durable-plan",
                "effect-group-disposal",
                "scope-disposal",
                "work-lease-disposal",
            ],
            phases);

        Assert.False(drain.IsCompleted);

        barriers.AllowWorkLeaseDisposal.TrySetResult();

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = (await inner
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True((await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Running.ToString(), persisted.Status);

        Assert.Single(ApprenticeRepository.DeserializePlan(persisted.Plan));

        Assert.Equal(1, gate.EffectGroupAttempts);

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }
    [Fact]
    public async Task StopAsync_WaitsForRealStartPlanningHandoffAndParksPlanningRow()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = TestApprentice(apprenticeId);

        apprentice.Status = ApprenticeStatus.Idle.ToString();

        ParkedPlanningUpdateRepository repo = new(apprentice);

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            new FailingPlanIntelligence(),
            new NotImplementedGrimoireRepository());

        Task<Result<string>> start = service.StartAsync(
            apprenticeId,
            CancellationToken.None);

        await repo.PlanningUpdateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task stop = service.StopAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(() => GetStopping(service));

            Assert.False(
                stop.IsCompleted,
                "Stop must wait for the real Start persistence-to-worker handoff.");
        }
        finally
        {
            repo.AllowPlanningUpdate.TrySetResult();
        }
        Result<string> started = await start.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(started.IsSuccess, started.Error.Message);

        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ApprenticeStatus.Paused.ToString(), repo.Get(apprenticeId).Status);

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }
    [Fact]
    public async Task StopAsync_WaitsForCapacityBoundStartPlanningHandoffBeforeQueueing()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = TestApprentice(apprenticeId);

        apprentice.Status = ApprenticeStatus.Idle.ToString();

        ParkedPlanningUpdateRepository repo = new(apprentice);

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            new FailingPlanIntelligence(),
            new NotImplementedGrimoireRepository());

        Assert.True(GetConcurrencyGate(service).TryAcquire(1, out IDisposable? holder));

        Assert.NotNull(holder);

        Task<Result<string>> start = service.StartAsync(
            apprenticeId,
            CancellationToken.None);

        await repo.PlanningUpdateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task stop = service.StopAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(() => GetStopping(service));

            Assert.False(
                stop.IsCompleted,
                "Stop must observe a Start handoff even before concurrency capacity is available.");
        }
        finally
        {
            repo.AllowPlanningUpdate.TrySetResult();
        }
        Result<string> result = await start.WaitAsync(TimeSpan.FromSeconds(10));

        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        holder!.Dispose();

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Apprentice.Disabled, result.Error.Code);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), repo.Get(apprenticeId).Status);

        Assert.DoesNotContain(apprenticeId, GetPendingStarts(service));

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }
    [Fact]
    public async Task StopAsync_WaitsForResumePersistenceHandoffAndParksRunningRow()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = TestApprentice(apprenticeId);

        apprentice.Status = ApprenticeStatus.Paused.ToString();

        apprentice.Plan = ApprenticeRepository.SerializePlan(
        [
            new PlanStep { Index = 0, Description = "Resume safely" },
        ]);

        ParkedRunningUpdateRepository repo = new(apprentice);

        CapturingLogger<ApprenticeService> logger = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            logger,
            new SuccessfulStepIntelligence(),
            new NotImplementedGrimoireRepository());

        Task<Result<string>> resume = service.ResumeAsync(
            apprenticeId,
            CancellationToken.None);

        await repo.RunningUpdateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task stop = service.StopAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(() => GetStopping(service));

            Assert.False(
                stop.IsCompleted,
                "Stop must wait until the reserved execution owns a task that can park the durable row.");
        }
        finally
        {
            repo.AllowRunningUpdate.TrySetResult();
        }
        Result<string> resumed = await resume.WaitAsync(TimeSpan.FromSeconds(10));

        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(resumed.IsSuccess, resumed.Error.Message);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), repo.Get(apprenticeId).Status);

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
    }
    [Fact]
    public async Task StopAsync_WaitsForInterventionPersistenceHandoffAndParksRunningRow()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = TestApprentice(apprenticeId);

        apprentice.Status = ApprenticeStatus.Escalated.ToString();

        apprentice.Plan = ApprenticeRepository.SerializePlan(
        [
            new PlanStep
            {
                Index = 0,
                Description = "Accept guidance safely",
                Status = "failed",
                Attempts = 1,
            },
        ]);

        apprentice.ErrorMessage = "Awaiting guidance.";

        ParkedRunningUpdateRepository repo = new(apprentice);

        CapturingLogger<ApprenticeService> logger = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            logger,
            new SuccessfulStepIntelligence(),
            new NotImplementedGrimoireRepository());

        Task<Result<string>> intervention = service.InterveneAsync(
            apprenticeId,
            "Continue with the revised approach.",
            resume: true,
            CancellationToken.None);

        await repo.RunningUpdateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task stop = service.StopAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(() => GetStopping(service));

            Assert.False(
                stop.IsCompleted,
                "Stop must wait until the reserved intervention owns a task that can park the durable row.");
        }
        finally
        {
            repo.AllowRunningUpdate.TrySetResult();
        }
        Result<string> intervened = await intervention.WaitAsync(TimeSpan.FromSeconds(10));

        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(intervened.IsSuccess, intervened.Error.Message);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), repo.Get(apprenticeId).Status);

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
    }
    [Fact]
    public async Task StopAsync_CancelledDrainDoesNotReportSuccessfulShutdown()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = TestApprentice(apprenticeId);

        apprentice.Status = ApprenticeStatus.Paused.ToString();

        apprentice.Plan = ApprenticeRepository.SerializePlan(
        [
            new PlanStep { Index = 0, Description = "Resume safely" },
        ]);

        ParkedRunningUpdateRepository repo = new(apprentice);

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            new SuccessfulStepIntelligence(),
            new NotImplementedGrimoireRepository());

        Task<Result<string>> resume = service.ResumeAsync(
            apprenticeId,
            CancellationToken.None);

        await repo.RunningUpdateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using CancellationTokenSource shutdownCancellation = new();

        Task stop = service.StopAsync(shutdownCancellation.Token);

        await WaitUntilAsync(() => GetStopping(service));

        await shutdownCancellation.CancelAsync();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        }
        finally
        {
            repo.AllowRunningUpdate.TrySetResult();

            _ = await resume.WaitAsync(TimeSpan.FromSeconds(10));

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(ApprenticeStatus.Paused.ToString(), repo.Get(apprenticeId).Status);

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }
    [Fact]
    public async Task ResumeAsync_PersistenceFailureReleasesReservationAndCapacity()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = TestApprentice(apprenticeId);

        apprentice.Status = ApprenticeStatus.Paused.ToString();

        ThrowingRunningUpdateRepository repo = new(apprentice);

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>());

        try
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ResumeAsync(apprenticeId, CancellationToken.None));

            Assert.Equal("Simulated Running persistence failure.", failure.Message);

            Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

            Assert.Equal(0, GetConcurrencyGate(service).RunningCount);

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            ReleaseAcquiredExecutionSlot(service, apprenticeId);
        }
    }
    [Fact]
    public async Task DeniedSerialStepRetainsSameTaskAndResumesOnce()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Deferred step",
            Goal = "Resume one exact attempt after maintenance.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 0, Description = "Run once" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        FrontierReadRepository repo = new(apprentice);

        SuccessfulStepIntelligence intelligence = new();

        SingleServiceScopeFactory scopes = new(
            repo,
            intelligence,
            new NotImplementedGrimoireRepository());

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        using ApprenticeService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            new CapturingLogger<ApprenticeService>(),
            gate);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await repo.ReadReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task retainedTask = GetActiveTasks(service)[apprenticeId];

        long retainedGeneration = GetExecutionGeneration(service, apprenticeId);

        IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        IGrimoireExclusiveClosedLease? closed = null;

        try
        {
            repo.AllowRead.TrySetResult();

            await WaitUntilAsync(
                () => gate.EffectGroupAttempts > 0 || intelligence.StreamCalls > 0);

            Assert.True((await inner.DrainRequestAndWorkAsync(
                closing,
                CancellationToken.None)).IsSuccess);

            closed = (await inner.CloseConnectionAdmissionAsync(
                closing,
                CancellationToken.None)).Value;

            Assert.Equal(0, intelligence.StreamCalls);

            Assert.Equal(0, repo.Get(apprenticeId).CurrentStep);

            Assert.Equal(1, GetConcurrencyGate(service).RunningCount);

            Assert.Equal(retainedGeneration, GetExecutionGeneration(service, apprenticeId));

            Assert.Same(retainedTask, GetActiveTasks(service)[apprenticeId]);

            Assert.False(retainedTask.IsCompleted);

            Assert.True((await closed.CompleteAsync(
                CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                CancellationToken.None)).IsSuccess);

            await closed.DisposeAsync();

            closed = null;

            await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

            Assert.Equal(1, intelligence.StreamCalls);

            Assert.Equal(1, repo.Get(apprenticeId).CurrentStep);

            Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);

            Assert.Equal(2, gate.EffectGroupAttempts);

            Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
        }
        finally
        {
            repo.AllowRead.TrySetResult();

            if (closed is not null)
            {
                _ = await closed.CompleteAsync(
                    CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                    CancellationToken.None);

                await closed.DisposeAsync();
            }
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
    [Fact]
    public async Task KeepClosedWaitIsObservedByStop()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Closed stop",
            Goal = "Stop without entering the closed Grimoire.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 0, Description = "Remain durable" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        FrontierReadRepository repo = new(apprentice);

        SuccessfulStepIntelligence intelligence = new();

        SingleServiceScopeFactory scopes = new(
            repo,
            intelligence,
            new NotImplementedGrimoireRepository());

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        using ApprenticeService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            new CapturingLogger<ApprenticeService>(),
            gate);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await repo.ReadReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        repo.AllowRead.TrySetResult();

        await WaitUntilAsync(() => gate.EffectGroupAttempts > 0);

        Assert.True((await inner.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None)).IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = (await inner
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        int admittedScopes = scopes.Created;

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(admittedScopes, scopes.Created);

        Assert.Equal(0, intelligence.StreamCalls);

        Assert.Equal(ApprenticeStatus.Running.ToString(), repo.Get(apprenticeId).Status);

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);

        Assert.True((await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);
    }
    [Fact]
    public async Task ReclosedTurnstileRetainsExecutionUntilLaterGenerationOpens()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Reclosed step",
            Goal = "Wait through every closed generation.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 0, Description = "Run after the second reopen" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        InMemoryApprenticeRepository repo = new(apprentice);

        SuccessfulStepIntelligence intelligence = new();

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        await using IGrimoireClosingOwner initialClosing = inner
            .BeginOrResumeExclusive(Owner()).Value;

        Assert.True((await inner.DrainRequestAndWorkAsync(
            initialClosing,
            CancellationToken.None)).IsSuccess);

        await using IGrimoireExclusiveClosedLease initialClosed = (await inner
            .CloseConnectionAdmissionAsync(
                initialClosing,
                CancellationToken.None)).Value;

        ReclosingApprenticeAdmissionGate gate = new(inner);

        using ApprenticeService service = new(
            new SingleServiceScopeFactory(
                repo,
                intelligence,
                new NotImplementedGrimoireRepository()),
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            new CapturingLogger<ApprenticeService>(),
            gate);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await gate.FirstWaitReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task retainedTask = GetActiveTasks(service)[apprenticeId];

        long retainedGeneration = GetExecutionGeneration(service, apprenticeId);

        Assert.True((await initialClosed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        await using IGrimoireExclusiveClosedLease reclosed = await gate
            .Reclosed.Task
            .WaitAsync(TimeSpan.FromSeconds(10));

        await WaitUntilAsync(() => gate.WaitCalls >= 2);

        Assert.Equal(0, intelligence.StreamCalls);

        Assert.Same(retainedTask, GetActiveTasks(service)[apprenticeId]);

        Assert.Equal(retainedGeneration, GetExecutionGeneration(service, apprenticeId));

        Assert.Equal(1, GetConcurrencyGate(service).RunningCount);

        Assert.True((await reclosed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(1, intelligence.StreamCalls);

        Assert.Equal(1, repo.Get(apprenticeId).CurrentStep);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);

        Assert.Equal(1, gate.EffectGroupAttempts);

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }
    [Fact]
    public async Task DeniedSimulacrumRetainsSameTaskAndResumesWholeGroupOnce()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Deferred Simulacrum",
            Goal = "Resume one whole parallel frontier after maintenance.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 0, Description = "First branch", IsParallel = true },
                new PlanStep { Index = 1, Description = "Second branch", IsParallel = true },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        FrontierReadRepository repo = new(apprentice);

        SuccessfulStepIntelligence intelligence = new();

        SingleServiceScopeFactory scopes = new(
            repo,
            intelligence,
            new NotImplementedGrimoireRepository());

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        using ApprenticeService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            new CapturingLogger<ApprenticeService>(),
            gate);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await repo.ReadReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task retainedTask = GetActiveTasks(service)[apprenticeId];

        long retainedGeneration = GetExecutionGeneration(service, apprenticeId);

        IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        IGrimoireExclusiveClosedLease? closed = null;

        try
        {
            repo.AllowRead.TrySetResult();

            await WaitUntilAsync(
                () => gate.EffectGroupAttempts > 0 || intelligence.StreamCalls > 0);

            Assert.True((await inner.DrainRequestAndWorkAsync(
                closing,
                CancellationToken.None)).IsSuccess);

            closed = (await inner.CloseConnectionAdmissionAsync(
                closing,
                CancellationToken.None)).Value;

            Assert.Equal(0, intelligence.StreamCalls);

            Assert.Equal(0, repo.Get(apprenticeId).CurrentStep);

            Assert.Equal(1, GetConcurrencyGate(service).RunningCount);

            Assert.Equal(retainedGeneration, GetExecutionGeneration(service, apprenticeId));

            Assert.Same(retainedTask, GetActiveTasks(service)[apprenticeId]);

            Assert.False(retainedTask.IsCompleted);

            Assert.True((await closed.CompleteAsync(
                CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                CancellationToken.None)).IsSuccess);

            await closed.DisposeAsync();

            closed = null;

            await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

            Assert.Equal(2, intelligence.StreamCalls);

            Assert.Equal(2, repo.Get(apprenticeId).CurrentStep);

            Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);

            Assert.Equal(2, gate.EffectGroupAttempts);

            Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
        }
        finally
        {
            repo.AllowRead.TrySetResult();

            if (closed is not null)
            {
                _ = await closed.CompleteAsync(
                    CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                    CancellationToken.None);

                await closed.DisposeAsync();
            }
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
    [Fact]
    public async Task WinningSimulacrumDrainsAfterWholeFrontierInExactDisposalOrder()
    {
        Guid apprenticeId = Guid.NewGuid();

        Guid childId = Guid.NewGuid();

        ConcurrentQueue<string> phases = new();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Winning Simulacrum",
            Goal = "Conclude every parallel effect before maintenance proceeds.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 0, Description = "First branch", IsParallel = true },
                new PlanStep { Index = 1, Description = "Second branch", IsParallel = true },
                new PlanStep { Index = 2, Description = "Original tail" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        Apprentice child = new()
        {
            Id = childId,
            Name = "Stamped child",
            Goal = "Retain the parent lineage.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Idle.ToString(),
            Plan = "[]",
            CurrentStep = 0,
        };
        BlockingSimulacrumFrontierRepository repo = new(
            apprentice,
            child,
            phases);

        BlockingSimulacrumFrontierIntelligence intelligence = new(
            childId,
            phases);

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        FrontierDisposalBarriers barriers = new(phases, gate);

        SingleServiceScopeFactory scopes = new(
            repo,
            intelligence,
            new NotImplementedGrimoireRepository(),
            barriers.BeforeScopeDisposalAsync);

        CapturingLogger<ApprenticeService> logger = new();

        using ApprenticeService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            logger,
            gate);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await intelligence.ProvidersReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["provider"], phases);

        await using IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        Task<Result> drain = inner
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        await Task.Yield();

        Assert.False(drain.IsCompleted);

        intelligence.AllowProviders.TrySetResult();

        await repo.ChildStampReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["provider", "child-stamp"], phases);

        Assert.False(drain.IsCompleted);

        repo.AllowChildStamp.TrySetResult();

        await intelligence.ShiftingFateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["provider", "child-stamp", "shifting-fate"], phases);

        Assert.False(drain.IsCompleted);

        intelligence.AllowShiftingFate.TrySetResult();

        await repo.FinalCheckpointReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            ["provider", "child-stamp", "shifting-fate", "final-checkpoint"],
            phases);

        Assert.False(drain.IsCompleted);

        repo.AllowFinalCheckpoint.TrySetResult();

        await barriers.EffectGroupDisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            [
                "provider",
                "child-stamp",
                "shifting-fate",
                "final-checkpoint",
                "effect-group-disposal",
            ],
            phases);

        Assert.False(drain.IsCompleted);

        barriers.AllowEffectGroupDisposal.TrySetResult();

        await barriers.ScopeDisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            [
                "provider",
                "child-stamp",
                "shifting-fate",
                "final-checkpoint",
                "effect-group-disposal",
                "scope-disposal",
            ],
            phases);

        Assert.False(drain.IsCompleted);

        barriers.AllowScopeDisposal.TrySetResult();

        await barriers.WorkLeaseDisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            [
                "provider",
                "child-stamp",
                "shifting-fate",
                "final-checkpoint",
                "effect-group-disposal",
                "scope-disposal",
                "work-lease-disposal",
            ],
            phases);

        Assert.False(drain.IsCompleted);

        barriers.AllowWorkLeaseDisposal.TrySetResult();

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = (await inner
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True((await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(2, persisted.CurrentStep);

        ApprenticeCheckpoint checkpoint = Assert.IsType<ApprenticeCheckpoint>(
            ApprenticeRepository.DeserializeCheckpoint(persisted.CheckpointData));

        Assert.Equal(2, checkpoint.CurrentStep);

        Assert.Equal(apprenticeId, repo.Get(childId).ParentApprenticeId);

        Assert.Equal(1, gate.EffectGroupAttempts);

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
    }
    [Fact]
    public async Task WinningStepDrainsThroughCheckpointAndScopeDisposal()
    {
        Guid apprenticeId = Guid.NewGuid();

        ConcurrentQueue<string> phases = new();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Winning step",
            Goal = "Finish the effect before maintenance owns the Grimoire.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 0, Description = "Finish once" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        FinalCheckpointRecordingRepository repo = new(
            apprentice,
            targetStep: 1,
            phases);

        BlockingSuccessfulStepIntelligence intelligence = new(phases);

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        TaskCompletionSource groupDisposalReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource allowGroupDisposal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource workLeaseDisposalReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource allowWorkLeaseDisposal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        int groupDisposals = 0;

        gate.BeforeEffectGroupDisposalAsync = async () =>
        {
            if (Interlocked.Increment(ref groupDisposals) != 1)
            {
                return;
            }
            phases.Enqueue("effect-group-disposal");

            groupDisposalReached.TrySetResult();

            await allowGroupDisposal.Task;
        };
        int workLeaseDisposals = 0;

        gate.BeforeWorkLeaseDisposalAsync = async () =>
        {
            if (Interlocked.Increment(ref workLeaseDisposals) != 1)
            {
                return;
            }
            phases.Enqueue("work-lease-disposal");

            workLeaseDisposalReached.TrySetResult();

            await allowWorkLeaseDisposal.Task;
        };
        TaskCompletionSource scopeDisposalReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource allowScopeDisposal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        int scopeDisposals = 0;

        SingleServiceScopeFactory scopes = new(
            repo,
            intelligence,
            new NotImplementedGrimoireRepository(),
            async () =>
            {
                if (Interlocked.Increment(ref scopeDisposals) != 1)
                {
                    return;
                }
                phases.Enqueue("scope-disposal");

                scopeDisposalReached.TrySetResult();

                await allowScopeDisposal.Task;
            });

        using ApprenticeService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            new CapturingLogger<ApprenticeService>(),
            gate);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await intelligence.StreamReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, gate.EffectGroupAttempts);

        Assert.Equal(["provider"], phases);

        await using IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        Task<Result> drain = inner
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        await Task.Yield();

        Assert.False(drain.IsCompleted);

        intelligence.AllowStream.TrySetResult();

        await groupDisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            ["provider", "final-checkpoint", "effect-group-disposal"],
            phases);

        Assert.Equal(1, repo.Get(apprenticeId).CurrentStep);

        Assert.False(drain.IsCompleted);

        allowGroupDisposal.TrySetResult();

        await scopeDisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            [
                "provider",
                "final-checkpoint",
                "effect-group-disposal",
                "scope-disposal",
            ],
            phases);

        Assert.False(drain.IsCompleted);

        allowScopeDisposal.TrySetResult();

        await workLeaseDisposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            [
                "provider",
                "final-checkpoint",
                "effect-group-disposal",
                "scope-disposal",
                "work-lease-disposal",
            ],
            phases);

        Assert.False(drain.IsCompleted);

        allowWorkLeaseDisposal.TrySetResult();

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = (await inner
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        Assert.True((await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);
    }
    [Fact]
    public async Task StopDuringAdmittedSerialFrontierPersistsPaused()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Admitted stop",
            Goal = "Persist the owned cancellation before leaving the frontier.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 0, Description = "Pause here" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        InMemoryApprenticeRepository repo = new(apprentice);

        BlockingSuccessfulStepIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence,
            new NotImplementedGrimoireRepository());

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await intelligence.StreamReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Apprentice paused = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Paused.ToString(), paused.Status);

        Assert.Equal(0, paused.CurrentStep);

        Assert.False(GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(0, GetConcurrencyGate(service).RunningCount);
    }
    [Fact]
    public async Task SerialRetriesUseOneFrontierPerAttemptAndRetainFailureEvidence()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Bounded retries",
            Goal = "Stop when retry evidence makes no progress.",
            WorkspacePath = Path.GetTempPath(),
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 0, Description = "Retry carefully" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        InMemoryApprenticeRepository repo = new(apprentice);

        RepeatedRetryableStepIntelligence intelligence = new();

        SingleServiceScopeFactory scopes = new(
            repo,
            intelligence,
            new NotImplementedGrimoireRepository());

        RecordingGrimoireWorkAdmissionGate gate = new(
            new GrimoireConnectionAdmissionGate(TimeProvider.System));

        using ApprenticeService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(CreateCapacitySettings()),
            new ChronicleHub(),
            new CapturingLogger<ApprenticeService>(),
            gate);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Apprentice escalated = repo.Get(apprenticeId);

        Assert.Equal(2, intelligence.StreamCalls);

        Assert.Equal(2, gate.EffectGroupAttempts);

        Assert.Equal(0, escalated.CurrentStep);

        Assert.Equal(ApprenticeStatus.Escalated.ToString(), escalated.Status);

        Assert.Equal(
            "Step recovery stopped because the same failure evidence repeated.",
            escalated.ErrorMessage);
    }
    // A prior execution generation must never classify a foreign cancellation over a newer resume.
    [Fact]
    public async Task RunApprenticeAsync_StaleGeneration_DoesNotPersistFailureOverNewerResume()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Stale failure",
            Goal = "A stale run must not clobber its replacement.",
            WorkspacePath = "/tmp/arcanum-test",
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 1, Description = "Foreign cancellation" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        OceOnFirstGetApprenticeRepository repo = new(apprentice);

        ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            new FailingPlanIntelligence(),
            new NotImplementedGrimoireRepository());

        SeedExecutionGeneration(service, apprenticeId, 2L);

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("RunApprenticeAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task runTask = (Task)method!.Invoke(service, new object[] { apprenticeId, 1L })!;

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(ApprenticeStatus.Running.ToString(), repo.Get(apprenticeId).Status);
    }
    [Fact]
    public async Task RunApprenticeAsync_ForeignOperationCanceledException_PersistsFailure()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Foreign cancellation",
            Goal = "Do not mistake another operation's cancellation for mine.",
            WorkspacePath = "/tmp/arcanum-test",
            Status = ApprenticeStatus.Running.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 1, Description = "Step interrupted by a foreign token" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
        };
        OceOnFirstGetApprenticeRepository repo = new(apprentice);

        CapturingLogger<ApprenticeService> logger = new();

        ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            logger,
            new FailingPlanIntelligence(),
            new NotImplementedGrimoireRepository());

        SeedExecutionGeneration(service, apprenticeId, 1L);

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("RunApprenticeAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task runTask = (Task)method!.Invoke(service, new object[] { apprenticeId, 1L })!;

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Failed.ToString(), persisted.Status);

        Assert.Equal("Simulated foreign cancellation.", persisted.ErrorMessage);
    }
    // W2.4 Fix 5 (P2): a Planning apprentice resumed after a host restart must
    // emit ApprenticeResumed on the chronicle hub, not a duplicate
    // ApprenticeStarted (was: the recovery path always emitted ApprenticeStarted
    // for any non-Running/Paused status, including Planning).

    [Fact]
    public async Task RunApprenticeAsync_PlanningResumeAfterRestart_EmitsApprenticeResumedNotStarted()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            Name = "Reliability-Fix5",
            Goal = "Emit ApprenticeResumed on restart resume.",
            WorkspacePath = "/tmp/arcanum-test",
            Status = ApprenticeStatus.Planning.ToString(),
            Plan = "[]",
            CurrentStep = 0,
            SessionId = null,
        };
        InMemoryApprenticeRepository repo = new(apprentice);

        ArcanumSettings settings = CreateCapacitySettings();

        CapturingLogger<ApprenticeService> logger = new();

        FailingPlanIntelligence intelligence = new();

        NotImplementedGrimoireRepository grimoire = new();

        ChronicleHub hub = new();

        ApprenticeService service = CreateService(repo, settings, logger, intelligence, grimoire, hub);

        List<ApprenticeEvent> captured = new();

        using CancellationTokenSource subscribeCts = new(TimeSpan.FromSeconds(10));

        TaskCompletionSource primed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Call the collector directly (not Task.Run): an async method runs
        // synchronously up to its first await-that-yields, which here is the
        // channel MoveNextAsync AFTER the subscription is registered. So by the
        // time CollectChronicleAsync returns its Task, the channel is live and a
        // publish will be delivered (no Task.Run scheduling race).

        Task collector = CollectChronicleAsync(
            service.SubscribeChronicleAsync(apprenticeId, subscribeCts.Token),
            captured,
            primed,
            subscribeCts.Token);

        // Prime the subscription with a sentinel so we know the collector is
        // listening before RunApprenticeAsync publishes its first event.

        hub.Publish(apprenticeId, new ApprenticeEvent
        {
            Type = ApprenticeEventType.EventsDropped,
            ApprenticeId = apprenticeId,
            Timestamp = DateTimeOffset.UtcNow,
            Summary = "prime",
        });

        await primed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (captured)
        {
            captured.Clear();
        }
        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("RunApprenticeAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        SeedExecutionGeneration(service, apprenticeId, 1L);

        Task runTask = (Task)method!.Invoke(service, new object[] { apprenticeId, 1L })!;

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        subscribeCts.Cancel();

        try
        {
            await collector.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        List<ApprenticeEvent> events;

        lock (captured)
        {
            events = captured.ToList();
        }
        Assert.DoesNotContain(events, e => e.Type == ApprenticeEventType.ApprenticeStarted);

        Assert.Contains(events, e => e.Type == ApprenticeEventType.ApprenticeResumed);
    }
    private static ArcanumSettings CreateCapacitySettings() => new()
    {
        Features = new FeatureSettings { Apprentices = true },
        Execution = new ExecutionSettings
        {
            MaxConcurrentApprentices = 1,
        },
    };
    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(
            Guid.NewGuid(),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(new byte[32]));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
    private static async Task<ApprenticeService.StepExecutionOutcome> ExecuteStepStreamAsync(
        ApprenticeService service,
        IArcanumIntelligenceProvider intelligence,
        Apprentice apprentice)
    {
        using CancellationTokenSource linkedCancellation = new();

        return await service.ExecuteStepStreamAsync(
                intelligence,
                apprentice,
                "perform the step",
                linkedCancellation,
                apprentice.Id,
                false)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static Apprentice TestApprentice(Guid apprenticeId) =>
        new()
        {
            Id = apprenticeId,
            Name = "test",
            Goal = "test stream behavior",
            SessionId = Guid.NewGuid(),
            WorkspacePath = Path.GetTempPath(),
        };
    private static ApprenticeService CreateService(
        InMemoryApprenticeRepository repo,
        ArcanumSettings settings,
        ILogger<ApprenticeService> logger,
        IArcanumIntelligenceProvider? intelligence = null,
        IGrimoireRepository? grimoire = null,
        ChronicleHub? hub = null,
        IApprenticeExecutionCapacity? executionCapacity = null)
    {
        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        ChronicleHub resolvedHub = hub ?? new();

        SingleServiceScopeFactory scopeFactory = new(repo, intelligence, grimoire);

        return new ApprenticeService(
            scopeFactory,
            options,
            resolvedHub,
            logger,
            new GrimoireConnectionAdmissionGate(TimeProvider.System),
            executionCapacity);
    }
    private static IApprenticeExecutionCapacity GetConcurrencyGate(ApprenticeService service)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_concurrencyGate", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return (IApprenticeExecutionCapacity)field!.GetValue(service)!;
    }
    private static bool TryAcquireExecutionSlot(
        ApprenticeService service,
        Guid apprenticeId) =>
        TryAcquireExecutionSlot(
            service,
            apprenticeId,
            queueOnCapacity: false).Accepted;

    private static ExecutionSlotAttempt TryAcquireExecutionSlot(
        ApprenticeService service,
        Guid apprenticeId,
        bool queueOnCapacity)
    {
        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod(
                "TryAcquireExecutionSlot",
                BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        object?[] arguments = [apprenticeId, queueOnCapacity, false, null];

        bool accepted = Assert.IsType<bool>(method!.Invoke(service, arguments));

        return new ExecutionSlotAttempt(
            accepted,
            Assert.IsType<bool>(arguments[2]),
            arguments[3] as Result<string>);
    }
    private sealed record ExecutionSlotAttempt(
        bool Accepted,
        bool Queued,
        Result<string>? Failure);
    private static void ReleaseAcquiredExecutionSlot(
        ApprenticeService service,
        Guid apprenticeId)
    {
        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod(
                "ReleaseAcquiredExecutionSlot",
                BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        _ = method!.Invoke(service, [apprenticeId]);
    }
    private static void BeginExecutionTask(
        ApprenticeService service,
        Guid apprenticeId)
    {
        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod(
                "BeginExecutionTask",
                BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        _ = method!.Invoke(service, [apprenticeId]);
    }
    private static ConcurrentQueue<Guid> GetPendingStarts(ApprenticeService service)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_pendingStarts", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return (ConcurrentQueue<Guid>)field!.GetValue(service)!;
    }
    private static ConcurrentDictionary<Guid, byte> GetPendingStartIds(
        ApprenticeService service)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_pendingStartIds", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return (ConcurrentDictionary<Guid, byte>)field!.GetValue(service)!;
    }
    private static void InvokeTryDequeuePendingStart(ApprenticeService service)
    {
        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod(
                "TryDequeuePendingStart",
                BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        _ = method!.Invoke(service, null);
    }
    private static ConcurrentDictionary<Guid, Task> GetActiveTasks(ApprenticeService service)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_activeTasks", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return (ConcurrentDictionary<Guid, Task>)field!.GetValue(service)!;
    }
    private static void SeedExecutionGeneration(ApprenticeService service, Guid apprenticeId, long generation)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_executionGenerations", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        ConcurrentDictionary<Guid, long> generations =
            (ConcurrentDictionary<Guid, long>)field!.GetValue(service)!;

        generations[apprenticeId] = generation;
    }
    private static long GetExecutionGeneration(ApprenticeService service, Guid apprenticeId)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_executionGenerations", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        ConcurrentDictionary<Guid, long> generations =
            (ConcurrentDictionary<Guid, long>)field!.GetValue(service)!;

        return generations[apprenticeId];
    }
    private static bool GetStopping(ApprenticeService service)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_stopping", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return Assert.IsType<bool>(field!.GetValue(service));
    }
    private static Lock GetPendingStartsLock(ApprenticeService service)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_pendingStartsLock", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return Assert.IsType<Lock>(field!.GetValue(service));
    }
    private static bool CanEnterAndReleaseExecutionLifecycleLock(ApprenticeService service)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_executionLifecycleLock", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        Lock lifecycle = Assert.IsType<Lock>(field!.GetValue(service));

        if (!lifecycle.TryEnter())
        {
            return false;
        }
        lifecycle.Exit();

        return true;
    }
    private static ConcurrentDictionary<Guid, byte> GetFreshStartIds(ApprenticeService service)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_freshStartIds", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        return (ConcurrentDictionary<Guid, byte>)field!.GetValue(service)!;
    }
    private static async Task InvokeResumeCrashRecoveryAsync(ApprenticeService service, CancellationToken cancellationToken)
    {
        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("ResumeCrashRecoveryAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task? task = (Task?)method!.Invoke(service, new object?[] { cancellationToken });

        Assert.NotNull(task);

        await task!;
    }
    private static async Task CollectChronicleAsync(
        IAsyncEnumerable<ApprenticeEvent> events,
        List<ApprenticeEvent> captured,
        TaskCompletionSource primed,
        CancellationToken cancellationToken)
    {
        await foreach (ApprenticeEvent e in events.ConfigureAwait(false))
        {
            lock (captured)
            {
                captured.Add(e);
            }
            primed.TrySetResult();

            if (e.Type is ApprenticeEventType.ApprenticeFailed
                or ApprenticeEventType.ApprenticeCompleted)
            {
                break;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
    private sealed class ReclosingApprenticeAdmissionGate(
        GrimoireConnectionAdmissionGate inner) : IGrimoireConnectionAdmissionGate
    {
        private readonly RecordingGrimoireWorkAdmissionGate _recording = new(inner);

        private int _waitCalls;

        internal TaskCompletionSource FirstWaitReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<IGrimoireExclusiveClosedLease> Reclosed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int WaitCalls => Volatile.Read(ref _waitCalls);

        internal int EffectGroupAttempts => _recording.EffectGroupAttempts;

        public long CurrentGeneration => _recording.CurrentGeneration;

        public bool TryAcquireRequestLease(
            GrimoireRequestKind kind,
            out IGrimoireRequestLease? lease) =>
            _recording.TryAcquireRequestLease(kind, out lease);

        public bool TryAcquireWorkLease(
            GrimoireWorkKind kind,
            out IGrimoireWorkLease? lease) =>
            _recording.TryAcquireWorkLease(kind, out lease);

        public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(
            System.Data.Common.DbConnection connection) =>
            _recording.AcquireOrdinaryOpen(connection);

        public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
            CovenantExclusiveRecoveryOwner owner,
            IGrimoireRequestLease? initiatingRequest = null,
            System.Data.Common.DbConnection? scopedConnection = null) =>
            _recording.BeginOrResumeExclusive(
                owner,
                initiatingRequest,
                scopedConnection);

        public ValueTask<Result> DrainRequestAndWorkAsync(
            IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) =>
            _recording.DrainRequestAndWorkAsync(
                closingOwner,
                cancellationToken);

        public ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
            IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) =>
            _recording.CloseConnectionAdmissionAsync(
                closingOwner,
                cancellationToken);

        public ValueTask<Result> AbortClosingAsync(
            IGrimoireClosingOwner closingOwner,
            Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync,
            CancellationToken cancellationToken) =>
            _recording.AbortClosingAsync(
                closingOwner,
                proveNoDestructiveEffectAsync,
                cancellationToken);

        public async Task<long> WaitForNextOpenGenerationAsync(
            long observedGeneration,
            CancellationToken cancellationToken)
        {
            int wait = Interlocked.Increment(ref _waitCalls);

            if (wait == 1)
            {
                FirstWaitReached.TrySetResult();
            }
            long generation = await _recording
                .WaitForNextOpenGenerationAsync(
                    observedGeneration,
                    cancellationToken)
                .ConfigureAwait(false);

            if (wait != 1)
            {
                return generation;
            }
            try
            {
                await using IGrimoireClosingOwner closing = inner
                    .BeginOrResumeExclusive(Owner()).Value;

                Result drained = await inner.DrainRequestAndWorkAsync(
                    closing,
                    cancellationToken);

                if (drained.IsFailure)
                {
                    throw new InvalidOperationException(drained.Error.Message);
                }
                Result<IGrimoireExclusiveClosedLease> closed = await inner
                    .CloseConnectionAdmissionAsync(
                        closing,
                        cancellationToken);

                if (closed.IsFailure)
                {
                    throw new InvalidOperationException(closed.Error.Message);
                }
                Reclosed.TrySetResult(closed.Value);
            }
            catch (Exception ex)
            {
                Reclosed.TrySetException(ex);

                throw;
            }
            return generation;
        }
        public ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>>
            AcquireExpiredLeaseAdoptionInterlockAsync(
                CovenantExclusiveRecoveryOwner candidateOwner,
                Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>>
                    revalidateDurableOwnerAsync,
                CancellationToken cancellationToken) =>
            _recording.AcquireExpiredLeaseAdoptionInterlockAsync(
                candidateOwner,
                revalidateDurableOwnerAsync,
                cancellationToken);
    }
    private sealed class ConcurrentWinnerExecutionCapacity : IApprenticeExecutionCapacity
    {
        private readonly Lock _gate = new();

        private int _running;

        private bool _armConcurrentWinner;

        private bool _winnerHeld;

        internal bool WinnerHeld
        {
            get
            {
                lock (_gate)
                {
                    return _winnerHeld;
                }
            }
        }
        public int RunningCount
        {
            get
            {
                lock (_gate)
                {
                    return _running;
                }
            }
        }
        internal void ArmConcurrentWinner()
        {
            lock (_gate)
            {
                _armConcurrentWinner = true;
            }
        }
        internal void ReleaseConcurrentWinner()
        {
            lock (_gate)
            {
                if (!_winnerHeld)
                {
                    return;
                }
                _winnerHeld = false;

                _running--;
            }
        }
        public bool TryAcquire(int maxConcurrent, out IDisposable? lease)
        {
            lock (_gate)
            {
                if (_armConcurrentWinner)
                {
                    _armConcurrentWinner = false;

                    if (_running < maxConcurrent)
                    {
                        _running++;

                        _winnerHeld = true;
                    }
                    lease = null;

                    return false;
                }
                if (_running >= maxConcurrent)
                {
                    lease = null;

                    return false;
                }
                _running++;

                lease = new CapacityLease(this);

                return true;
            }
        }
        private sealed class CapacityLease(
            ConcurrentWinnerExecutionCapacity owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }
                lock (owner._gate)
                {
                    owner._running--;
                }
            }
        }
    }
    private sealed class BlockingCapacityLossExecutionCapacity : IApprenticeExecutionCapacity
    {
        private readonly ApprenticeConcurrencyGate _gate = new();

        internal TaskCompletionSource CapacityLossReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowCapacityLossReturn { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RunningCount => _gate.RunningCount;

        public bool TryAcquire(int maxConcurrent, out IDisposable? lease)
        {
            if (_gate.TryAcquire(maxConcurrent, out lease))
            {
                return true;
            }
            CapacityLossReached.TrySetResult();

            AllowCapacityLossReturn.Task.GetAwaiter().GetResult();

            return false;
        }
    }
    private sealed class FrontierDisposalBarriers
    {
        private readonly ConcurrentQueue<string> _phases;

        private int _effectGroupDisposals;

        private int _scopeDisposals;

        private int _workLeaseDisposals;

        internal FrontierDisposalBarriers(
            ConcurrentQueue<string> phases,
            RecordingGrimoireWorkAdmissionGate gate)
        {
            _phases = phases;

            gate.BeforeEffectGroupDisposalAsync = BeforeEffectGroupDisposalAsync;

            gate.BeforeWorkLeaseDisposalAsync = BeforeWorkLeaseDisposalAsync;
        }
        internal TaskCompletionSource EffectGroupDisposalReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowEffectGroupDisposal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ScopeDisposalReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowScopeDisposal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource WorkLeaseDisposalReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowWorkLeaseDisposal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async ValueTask BeforeScopeDisposalAsync()
        {
            if (Interlocked.Increment(ref _scopeDisposals) != 1)
            {
                return;
            }
            _phases.Enqueue("scope-disposal");

            ScopeDisposalReached.TrySetResult();

            await AllowScopeDisposal.Task;
        }
        private async ValueTask BeforeEffectGroupDisposalAsync()
        {
            if (Interlocked.Increment(ref _effectGroupDisposals) != 1)
            {
                return;
            }
            _phases.Enqueue("effect-group-disposal");

            EffectGroupDisposalReached.TrySetResult();

            await AllowEffectGroupDisposal.Task;
        }
        private async ValueTask BeforeWorkLeaseDisposalAsync()
        {
            if (Interlocked.Increment(ref _workLeaseDisposals) != 1)
            {
                return;
            }
            _phases.Enqueue("work-lease-disposal");

            WorkLeaseDisposalReached.TrySetResult();

            await AllowWorkLeaseDisposal.Task;
        }
    }
    private sealed class RefusingScopeFactory : IServiceScopeFactory
    {
        internal int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;

            throw new InvalidOperationException("Admission must precede scope creation.");
        }
    }
    private sealed class SingleServiceScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _provider;

        private readonly Func<ValueTask> _beforeScopeDisposalAsync;

        private int _created;

        private int _active;

        internal int Created => Volatile.Read(ref _created);

        public SingleServiceScopeFactory(
            IApprenticeRepository repo,
            IArcanumIntelligenceProvider? intelligence = null,
            IGrimoireRepository? grimoire = null,
            Func<ValueTask>? beforeScopeDisposalAsync = null)
        {
            _provider = new SingleServiceProvider(repo, intelligence, grimoire);

            _beforeScopeDisposalAsync = beforeScopeDisposalAsync
                ?? (static () => ValueTask.CompletedTask);
        }
        public IServiceScope CreateScope()
        {
            _ = Interlocked.Increment(ref _created);

            _ = Interlocked.Increment(ref _active);

            return new SingleScope(
                _provider,
                async () =>
                {
                    if (Interlocked.Decrement(ref _active) == 0)
                    {
                        await _beforeScopeDisposalAsync();
                    }
                });
        }
        private sealed class SingleScope(
            IServiceProvider provider,
            Func<ValueTask> beforeDisposalAsync) : IServiceScope, IAsyncDisposable
        {
            private int _disposed;

            public IServiceProvider ServiceProvider { get; } = provider;

            public void Dispose()
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    await beforeDisposalAsync();
                }
            }
        }
        private sealed class SingleServiceProvider : IServiceProvider
        {
            private readonly IApprenticeRepository _repo;

            private readonly IArcanumIntelligenceProvider? _intelligence;

            private readonly IGrimoireRepository? _grimoire;

            public SingleServiceProvider(
                IApprenticeRepository repo,
                IArcanumIntelligenceProvider? intelligence,
                IGrimoireRepository? grimoire)
            {
                _repo = repo;

                _intelligence = intelligence;

                _grimoire = grimoire;
            }
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IApprenticeRepository))
                {
                    return _repo;
                }
                if (serviceType == typeof(IArcanumIntelligenceProvider) && _intelligence is not null)
                {
                    return _intelligence;
                }
                if (serviceType == typeof(IGrimoireRepository) && _grimoire is not null)
                {
                    return _grimoire;
                }
                return null;
            }
        }
    }
    private class InMemoryApprenticeRepository : IApprenticeRepository
    {
        private readonly Dictionary<Guid, Apprentice> _store = new();

        public InMemoryApprenticeRepository(params Apprentice[] apprentices)
        {
            foreach (Apprentice apprentice in apprentices)
            {
                _store[apprentice.Id] = apprentice;
            }
        }
        public Apprentice Get(Guid id)
        {
            return _store[id];
        }
        public virtual Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Apprentice? value = _store.TryGetValue(id, out Apprentice? found) ? found : null;

            return Task.FromResult(value);
        }
        public virtual Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
        {
            _store[apprentice.Id] = apprentice;

            return Task.FromResult(apprentice);
        }
        public Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default)
        {
            string running = ApprenticeStatus.Running.ToString();

            IReadOnlyList<Apprentice> values = _store.Values
                .Where(a => string.Equals(a.Status, running, StringComparison.Ordinal))
                .ToList();

            return Task.FromResult(values);
        }
        public Task<IReadOnlyList<Apprentice>> GetInterruptedPlanningAsync(CancellationToken cancellationToken = default)
        {
            string planning = ApprenticeStatus.Planning.ToString();

            IReadOnlyList<Apprentice> values = _store.Values
                .Where(a => string.Equals(a.Status, planning, StringComparison.Ordinal))
                .ToList();

            return Task.FromResult(values);
        }
        public Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
        {
            _store[apprentice.Id] = apprentice;

            return Task.FromResult(apprentice);
        }
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_store.Remove(id));
        }
        public Task<ListPageResult<Apprentice>> ListAsync(
            Guid? campaignId,
            string? status,
            int? limit = null,
            DateTimeOffset? beforeUpdatedAt = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
    private sealed class StartSignalingApprenticeRepository(
        params Apprentice[] apprentices)
        : InMemoryApprenticeRepository(apprentices)
    {
        private readonly ConcurrentDictionary<Guid, int> _reads = new();

        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _executionReads = new();

        public override Task<Apprentice?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            if (_reads.AddOrUpdate(id, 1, static (_, current) => current + 1) >= 2)
            {
                _executionReads
                    .GetOrAdd(
                        id,
                        static _ => new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult();
            }
            return base.GetByIdAsync(id, cancellationToken);
        }
        public Task WaitForExecutionAsync(Guid id) =>
            _executionReads
                .GetOrAdd(
                    id,
                    static _ => new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously))
                .Task;
    }
    private sealed class FrontierReadRepository(
        Apprentice apprentice) : InMemoryApprenticeRepository(apprentice)
    {
        private int _reads;

        internal TaskCompletionSource ReadReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Apprentice?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                ReadReached.TrySetResult();

                await AllowRead.Task.WaitAsync(cancellationToken);
            }
            return await base.GetByIdAsync(id, cancellationToken);
        }
    }
    private sealed class BlockingPlanPersistenceRepository(
        Apprentice apprentice,
        ConcurrentQueue<string> phases) : InMemoryApprenticeRepository(apprentice)
    {
        private int _recorded;

        internal TaskCompletionSource PlanPersistenceReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowPlanPersistence { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Apprentice> UpdateAsync(
            Apprentice updated,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(updated.Plan, "[]", StringComparison.Ordinal)
                && string.Equals(
                    updated.Status,
                    ApprenticeStatus.Running.ToString(),
                    StringComparison.Ordinal)
                && Interlocked.Exchange(ref _recorded, 1) == 0)
            {
                phases.Enqueue("durable-plan");

                PlanPersistenceReached.TrySetResult();

                await AllowPlanPersistence.Task.WaitAsync(cancellationToken);
            }
            return await base.UpdateAsync(updated, cancellationToken);
        }
    }
    private sealed class FinalCheckpointRecordingRepository(
        Apprentice apprentice,
        int targetStep,
        ConcurrentQueue<string> phases) : InMemoryApprenticeRepository(apprentice)
    {
        private int _recorded;

        public override Task<Apprentice> UpdateAsync(
            Apprentice updated,
            CancellationToken cancellationToken = default)
        {
            ApprenticeCheckpoint? checkpoint = ApprenticeRepository
                .DeserializeCheckpoint(updated.CheckpointData);

            if (updated.CurrentStep == targetStep
                && checkpoint?.CurrentStep == targetStep
                && Interlocked.Exchange(ref _recorded, 1) == 0)
            {
                phases.Enqueue("final-checkpoint");
            }
            return base.UpdateAsync(updated, cancellationToken);
        }
    }
    private sealed class ParkedPlanningUpdateRepository(
        Apprentice apprentice) : InMemoryApprenticeRepository(apprentice)
    {
        private int _parked;

        internal TaskCompletionSource PlanningUpdateReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowPlanningUpdate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Apprentice> UpdateAsync(
            Apprentice updated,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(
                    updated.Status,
                    ApprenticeStatus.Planning.ToString(),
                    StringComparison.Ordinal)
                && Interlocked.Exchange(ref _parked, 1) == 0)
            {
                PlanningUpdateReached.TrySetResult();

                await AllowPlanningUpdate.Task.WaitAsync(cancellationToken);
            }
            return await base.UpdateAsync(updated, cancellationToken);
        }
    }
    private sealed class ParkedRunningUpdateRepository(
        Apprentice apprentice) : InMemoryApprenticeRepository(Clone(apprentice))
    {
        private int _runningUpdates;

        internal TaskCompletionSource RunningUpdateReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowRunningUpdate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Apprentice?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            Apprentice? current = await base.GetByIdAsync(id, cancellationToken);

            return current is null ? null : Clone(current);
        }
        public override async Task<Apprentice> UpdateAsync(
            Apprentice updated,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(
                    updated.Status,
                    ApprenticeStatus.Running.ToString(),
                    StringComparison.Ordinal)
                && Interlocked.Increment(ref _runningUpdates) == 1)
            {
                RunningUpdateReached.TrySetResult();

                await AllowRunningUpdate.Task.WaitAsync(cancellationToken);
            }
            return await base.UpdateAsync(Clone(updated), cancellationToken);
        }
        public new Apprentice Get(Guid id) => Clone(base.Get(id));

        private static Apprentice Clone(Apprentice source) => new()
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
    }
    private sealed class ThrowingRunningUpdateRepository(
        Apprentice apprentice) : InMemoryApprenticeRepository(apprentice)
    {
        public override Task<Apprentice> UpdateAsync(
            Apprentice updated,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(
                updated.Status,
                ApprenticeStatus.Running.ToString(),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated Running persistence failure.");
            }
            return base.UpdateAsync(updated, cancellationToken);
        }
    }
    private sealed class SuccessfulStepIntelligence : IArcanumIntelligenceProvider
    {
        private int _streamCalls;

        internal int StreamCalls => Volatile.Read(ref _streamCalls);

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
            _ = Interlocked.Increment(ref _streamCalls);

            cancellationToken.ThrowIfCancellationRequested();

            yield return new IntelligenceEvent(
                IntelligenceEventType.Result,
                "step complete");

            await Task.Yield();
        }
    }
    private sealed class BlockingSuccessfulStepIntelligence(
        ConcurrentQueue<string>? phases = null) : IArcanumIntelligenceProvider
    {
        internal TaskCompletionSource StreamReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowStream { get; } =
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
            phases?.Enqueue("provider");

            StreamReached.TrySetResult();

            await AllowStream.Task.WaitAsync(cancellationToken);

            yield return new IntelligenceEvent(
                IntelligenceEventType.Result,
                "step complete");
        }
    }
    private sealed class RepeatedRetryableStepIntelligence : IArcanumIntelligenceProvider
    {
        private int _streamCalls;

        internal int StreamCalls => Volatile.Read(ref _streamCalls);

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
            _ = Interlocked.Increment(ref _streamCalls);

            cancellationToken.ThrowIfCancellationRequested();

            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                "same retryable evidence");

            await Task.Yield();
        }
    }
    private sealed class SimulacrumOrderingRepository(
        Apprentice apprentice,
        Guid childId) : InMemoryApprenticeRepository(Clone(apprentice))
    {
        internal TaskCompletionSource ChildStampReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowChildStamp { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Apprentice?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            if (id == childId)
            {
                ChildStampReached.TrySetResult();

                await AllowChildStamp.Task.WaitAsync(cancellationToken);

                return null;
            }
            Apprentice? current = await base.GetByIdAsync(id, cancellationToken);

            return current is null ? null : Clone(current);
        }
        public override Task<Apprentice> UpdateAsync(
            Apprentice updated,
            CancellationToken cancellationToken = default) =>
            base.UpdateAsync(Clone(updated), cancellationToken);

        public new Apprentice Get(Guid id) => Clone(base.Get(id));

        private static Apprentice Clone(Apprentice source) => new()
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
    }
    private sealed class SimulacrumOrderingIntelligence(Guid childId) : IArcanumIntelligenceProvider
    {
        private int _streams;

        internal TaskCompletionSource ShiftingFateReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowShiftingFate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            ShiftingFateReached.TrySetResult();

            await AllowShiftingFate.Task.WaitAsync(cancellationToken);

            return new PromptTurnResult(
                """[{"index":2,"description":"Revised tail"}]""",
                Usage: null);
        }
        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            int stream = Interlocked.Increment(ref _streams);

            if (stream == 1)
            {
                yield return new IntelligenceEvent(
                    IntelligenceEventType.ToolResult,
                    "cast_sending",
                    Data: "{\"childApprenticeId\":\"" + childId + "\"}",
                    ToolCall: new IntelligenceToolCallEvent(
                        "cast-1",
                        "cast_sending",
                        "{}"));
            }
            yield return new IntelligenceEvent(
                IntelligenceEventType.Result,
                "branch complete");

            await Task.Yield();

            cancellationToken.ThrowIfCancellationRequested();
        }
    }
    private sealed class BlockingSimulacrumFrontierRepository(
        Apprentice apprentice,
        Apprentice child,
        ConcurrentQueue<string> phases)
        : InMemoryApprenticeRepository(Clone(apprentice), Clone(child))
    {
        private readonly Guid _apprenticeId = apprentice.Id;

        private readonly Guid _childId = child.Id;

        private int _childStampRecorded;

        private int _finalCheckpointRecorded;

        internal TaskCompletionSource ChildStampReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowChildStamp { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FinalCheckpointReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowFinalCheckpoint { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Apprentice?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            Apprentice? current = await base.GetByIdAsync(id, cancellationToken);

            return current is null ? null : Clone(current);
        }
        public override async Task<Apprentice> UpdateAsync(
            Apprentice updated,
            CancellationToken cancellationToken = default)
        {
            Apprentice persisted = await base.UpdateAsync(
                Clone(updated),
                cancellationToken);

            if (updated.Id == _childId
                && updated.ParentApprenticeId == _apprenticeId
                && Interlocked.Exchange(ref _childStampRecorded, 1) == 0)
            {
                phases.Enqueue("child-stamp");

                ChildStampReached.TrySetResult();

                await AllowChildStamp.Task.WaitAsync(cancellationToken);
            }
            ApprenticeCheckpoint? checkpoint = ApprenticeRepository
                .DeserializeCheckpoint(updated.CheckpointData);

            if (updated.Id == _apprenticeId
                && updated.CurrentStep == 2
                && checkpoint?.CurrentStep == 2
                && Interlocked.Exchange(ref _finalCheckpointRecorded, 1) == 0)
            {
                phases.Enqueue("final-checkpoint");

                FinalCheckpointReached.TrySetResult();

                await AllowFinalCheckpoint.Task.WaitAsync(cancellationToken);
            }
            return Clone(persisted);
        }
        public new Apprentice Get(Guid id) => Clone(base.Get(id));

        private static Apprentice Clone(Apprentice source) => new()
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
    }
    private sealed class BlockingSimulacrumFrontierIntelligence(
        Guid childId,
        ConcurrentQueue<string> phases) : IArcanumIntelligenceProvider
    {
        private int _streams;

        internal TaskCompletionSource ProvidersReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowProviders { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ShiftingFateReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowShiftingFate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            phases.Enqueue("shifting-fate");

            ShiftingFateReached.TrySetResult();

            await AllowShiftingFate.Task.WaitAsync(cancellationToken);

            return new PromptTurnResult(
                """[{"index":2,"description":"Revised tail"}]""",
                Usage: null);
        }
        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            int stream = Interlocked.Increment(ref _streams);

            if (stream == 2)
            {
                phases.Enqueue("provider");

                ProvidersReached.TrySetResult();
            }
            await AllowProviders.Task.WaitAsync(cancellationToken);

            if (stream == 1)
            {
                yield return new IntelligenceEvent(
                    IntelligenceEventType.ToolResult,
                    "cast_sending",
                    Data: "{\"childApprenticeId\":\"" + childId + "\"}",
                    ToolCall: new IntelligenceToolCallEvent(
                        "cast-1",
                        "cast_sending",
                        "{}"));
            }
            yield return new IntelligenceEvent(
                IntelligenceEventType.Result,
                "branch complete");
        }
    }
    private sealed class CapturingLogger<TCategory> : ILogger<TCategory>
    {
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);

            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, message, exception));
            }
        }
        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    // Throws a foreign OperationCanceledException on the first read, then allows
    // failure settlement to load the same durable Apprentice.

    private sealed class OceOnFirstGetApprenticeRepository : InMemoryApprenticeRepository
    {
        private int _calls;

        public OceOnFirstGetApprenticeRepository(params Apprentice[] apprentices) : base(apprentices)
        {
        }
        public override Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new OperationCanceledException("Simulated foreign cancellation.");
            }
            return base.GetByIdAsync(id, cancellationToken);
        }
    }
    // StreamPromptAsync throws a non-cancel exception to inject a Simulacrum
    // branch fault (Fix 3). ExecutePromptAsync is unused by the Fix 3 test.

    private sealed class ScriptedStreamIntelligence(
        params IntelligenceEvent[] frames)
        : IArcanumIntelligenceProvider
    {
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
            foreach (IntelligenceEvent frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return frame;

                await Task.Yield();
            }
        }
    }
    private sealed class ThrowingIntelligenceProvider : IArcanumIntelligenceProvider
    {
        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            throw new InvalidOperationException("Branch fault injection.");
    }
    // ExecutePromptAsync returns Failure so RunApprenticeAsync's plan-generation
    // path fails fast and exits via FailApprenticeAsync (Fix 5). StreamPromptAsync
    // is unused by the Fix 5 test.

    private sealed class FailingPlanIntelligence : IArcanumIntelligenceProvider
    {
        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            return Task.FromResult(
                Result<PromptTurnResult>.Failure(new Error("Plan.Failed", "Plan generation failed.")));
        }
        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotImplementedException();
    }
    private sealed class BlockingPlanIntelligence : IArcanumIntelligenceProvider
    {
        private int _executeCalls;

        internal int ExecuteCalls => Volatile.Read(ref _executeCalls);

        public async Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            _ = Interlocked.Increment(ref _executeCalls);

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return new PromptTurnResult("unreachable", Usage: null);
        }
        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotImplementedException();
    }
    private sealed class SuccessfulPlanIntelligence : IArcanumIntelligenceProvider
    {
        private int _executeCalls;

        private int _streamCalls;

        internal int ExecuteCalls => Volatile.Read(ref _executeCalls);

        internal int StreamCalls => Volatile.Read(ref _streamCalls);

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            int call = Interlocked.Increment(ref _executeCalls);

            if (call > 1)
            {
                return Task.FromResult<Result<PromptTurnResult>>(
                    new PromptTurnResult("NO_CHANGE", Usage: null));
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

            yield return new IntelligenceEvent(
                IntelligenceEventType.Result,
                "step complete");

            await Task.Yield();
        }
    }
    private sealed class BlockingOrderedPlanIntelligence(
        ConcurrentQueue<string> phases) : IArcanumIntelligenceProvider
    {
        internal TaskCompletionSource ProviderReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowProvider { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            phases.Enqueue("provider");

            ProviderReached.TrySetResult();

            await AllowProvider.Task.WaitAsync(cancellationToken);

            return new PromptTurnResult(
                """[{"index":1,"description":"One durable step"}]""",
                Usage: null);
        }
        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotImplementedException();
    }
    // Minimal IGrimoireRepository stub: every method throws NotImplementedException.
    // RunApprenticeAsync resolves the grimoire from the scope before plan
    // generation, but the failing-plan path returns before any grimoire call, so
    // the stub just needs to exist.

    private sealed class NotImplementedGrimoireRepository : IGrimoireRepository
    {
        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId, string prompt, string model, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task FinalizeAssistantEntryAsync(
            Guid assistantEntryId, string fullContent, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DiscardAssistantEntryAsync(
            Guid assistantEntryId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AppendToolInteractionAsync(
            Guid sessionId, string toolName, string arguments, string result, string modelUsed,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveCompletedExchangeAsync(
            string userPrompt, string assistantText, string modelUsed, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Session?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Session?> GetSessionHeaderAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(
            Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(
            Guid sessionId, int takeLast, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(
            Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteEntryAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SetEntryPinnedAsync(Guid sessionId, Guid entryId, bool pinned, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> GetPinnedEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(
            int threshold, DateTime idleCutoff, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(
            Guid sessionId, DateTime watermark, int batchSize, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task IncrementSessionTokensAsync(
            Guid sessionId, long totalTokens, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task IncrementSessionTokensAndCostAsync(
            Guid sessionId, long totalTokens, decimal costUsd, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task UpdateSessionCampaignRollupAsync(
            Guid sessionId, string summary, DateTime lastSummarizedMessageAt,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ListPageResult<LoreDto>> ListLoreAsync(
            int? limit = null, int offset = 0, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(
            string workspacePath, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
