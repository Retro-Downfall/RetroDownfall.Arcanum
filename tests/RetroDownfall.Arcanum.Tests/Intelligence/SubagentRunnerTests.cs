using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SubagentRunnerTests
{
    [Fact]
    public void SubagentRunRequest_CarriesOnlyFieldsTheRunnerConsumes()
    {
        // The parent attachment allowlist is enforced at parse time by ArcanumDelegateTaskTool
        // (FileReadResult.ParentPolicyDenied); the runner has nothing left to filter. The request
        // contract must not advertise an allowlist enforcement point that does not exist, so this
        // deconstruction pins its exact arity.
        SubagentRunRequest request = new(
            "prompt",
            "test-model",
            [new AttachedFileDto("src/A.cs", "sealed class A {}")],
            MaxTokens: 1_000,
            MaxCostUsd: 2m);

        (string prompt,
            string? model,
            IReadOnlyList<AttachedFileDto> files,
            long? maxTokens,
            decimal? maxCostUsd) = request;

        Assert.Equal("prompt", prompt);
        Assert.Equal("test-model", model);
        Assert.Single(files);
        Assert.Equal(1_000, maxTokens);
        Assert.Equal(2m, maxCostUsd);
    }

    [Fact]
    public async Task RunAsync_UsesSterileContext_CompletesDurableOperation_AndReturnsOnlySummary()
    {
        CapturingTurnFacade facade = new(
            Result<PromptTurnResult>.Success(
                new PromptTurnResult(
                    "child summary",
                    new ChatCompletionUsage(10, 5, 15))));
        FakeOperationCoordinator operations = new();
        CapturingTelemetry telemetry = new();
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
            telemetry,
            TimeProvider.System,
            NullLogger<SubagentRunner>.Instance);
        AttachedFileDto explicitFile = new("src/A.cs", "sealed class A {}");

        SubagentRunResult result = await runner.RunAsync(
            new SubagentRunRequest(
                "Review the explicit file.",
                "test-model",
                [explicitFile],
                MaxTokens: 1_000,
                MaxCostUsd: null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("child summary", result.Summary);
        Assert.NotEqual(Guid.Empty, result.RunId);

        // A subagent inherits no Covenant content and exposes no mutation tool, and the invocation
        // context is where that is now decided rather than being a consequence of an empty request.
        Assert.Same(ArcanumInvocationContext.None, facade.InvocationContext);
        Assert.False(facade.InvocationContext!.CanReadCovenant);
        Assert.False(facade.InvocationContext.CanStageCovenantMutation);
        Assert.Equal(ArcanumExecutionSurface.InternalBackground, facade.InvocationContext.Surface);

        PingRequest request = Assert.IsType<PingRequest>(facade.Request);
        Assert.Null(request.SessionId);
        Assert.Empty(request.WorkingDirectory);
        Assert.Null(request.ContextSnapshot);
        Assert.Null(request.ChronosyncDelta);
        Assert.Null(request.DataStreams);
        Assert.Null(request.CampaignId);
        Assert.True(request.DisableMcpTools);
        Assert.True(request.DisableAllTools);
        Assert.True(request.SkipSpellRouting);
        Assert.True(request.UnattendedMode);
        Assert.Equal([explicitFile], request.AttachedFiles);

        List<CoreChatMessage> messages = Assert.IsType<List<CoreChatMessage>>(
            request.StatelessMessages);
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Contains("isolated subagent", messages[0].Content);
        Assert.DoesNotContain("parent", messages[1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Review the explicit file.", messages[1].Content);

        Assert.Equal(LongRunningOperationKinds.Subagent, operations.StartRequest?.Kind);
        Assert.Equal(
            LongRunningOperationRecoveryPolicy.AbandonSafely,
            operations.StartRequest?.RecoveryPolicy);
        Assert.Equal(result.RunId, operations.StartRequest?.RunId);
        Assert.Equal(1, operations.CompleteCalls);
        Assert.Equal(0, operations.FailCalls);
        Assert.Equal(SubagentRunOutcome.Completed, telemetry.Event?.Outcome);
    }

    [Fact]
    public async Task RunAsync_WhenProviderUsageExceedsCeiling_FailsDurablyAndBillsUsage()
    {
        CapturingTurnFacade facade = new(
            Result<PromptTurnResult>.Success(
                new PromptTurnResult("must not escape", null)))
        {
            OnExecute = static () =>
            {
                DelegatedManaTracker tracker =
                    Assert.IsType<DelegatedManaTracker>(
                        SubagentExecutionAmbient.Tracker);
                tracker.BeginModelCall();
                tracker.RecordUsageDeferred(
                    new Microsoft.Extensions.AI.UsageDetails
                    {
                        InputTokenCount = 900,
                        OutputTokenCount = 101,
                        TotalTokenCount = 1_001,
                    },
                    costUsd: 0.02m);
            },
        };
        FakeOperationCoordinator operations = new();
        CapturingTelemetry telemetry = new();
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
            telemetry,
            TimeProvider.System,
            NullLogger<SubagentRunner>.Instance);

        SubagentRunResult result = await runner.RunAsync(
            new SubagentRunRequest(
                "Bounded task.",
                null,
                [],
                MaxTokens: 1_000,
                MaxCostUsd: null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SubagentFailureCodes.BudgetExhausted, result.FailureCode);
        Assert.Equal(1_001, result.Usage.Tokens);
        Assert.Equal(0.02m, result.Usage.CostUsd);
        Assert.Equal(0, operations.CompleteCalls);
        Assert.Equal(1, operations.FailCalls);
        Assert.Equal(SubagentFailureCodes.BudgetExhausted, operations.FailureCode);
        Assert.Equal(SubagentRunOutcome.BudgetExhausted, telemetry.Event?.Outcome);
        Assert.Equal(1_001, telemetry.Event?.Tokens);
    }

    /// <summary>
    /// The subagent lease is taken at the coordinator's 15-minute maximum, and a delegated child on
    /// local inference can easily run longer. Without renewal the reconciler claims the expired lease
    /// and abandons the operation under a new owner, so the child's finished summary is discarded and
    /// the parent model is told the subagent failed — after the delegated tokens were already billed.
    /// </summary>
    [Fact]
    public async Task RunAsync_ChildOutlivesTheLease_RenewsItAndStillCompletesOnTheCurrentRevision()
    {
        ManualTimeProvider time = new();
        TaskCompletionSource childGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingTurnFacade facade = new(
            Result<PromptTurnResult>.Success(
                new PromptTurnResult("child summary", null)))
        {
            Gate = childGate.Task,
        };
        FakeOperationCoordinator operations = new();
        CapturingTelemetry telemetry = new();
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
            telemetry,
            time,
            NullLogger<SubagentRunner>.Instance);

        Task<SubagentRunResult> run = runner.RunAsync(
            new SubagentRunRequest(
                "Long child task.",
                null,
                [],
                MaxTokens: 1_000,
                MaxCostUsd: null),
            CancellationToken.None);

        await facade.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        // Push the child well past the 15-minute lease ceiling one simulated minute at a time,
        // giving each renewal a chance to arm its next delay.
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    time.Advance(TimeSpan.FromMinutes(1));

                    return operations.HeartbeatCalls >= 3;
                },
                TimeSpan.FromSeconds(10)),
            $"expected at least three lease renewals, saw {operations.HeartbeatCalls}");

        childGate.SetResult();

        SubagentRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Success);
        Assert.Equal("child summary", result.Summary);
        Assert.Equal(1, operations.CompleteCalls);
        Assert.Equal(0, operations.FailCalls);

        // The heartbeats bumped the stored revision; Complete has to address the current one.
        Assert.Equal(1L + operations.HeartbeatCalls, operations.LastCompleteRevision);
        Assert.Equal(SubagentRunOutcome.Completed, telemetry.Event?.Outcome);
    }

    /// <summary>
    /// The renewal loop is a lease keeper, not a result carrier. The child turn and its renewal share
    /// one DI scope — and therefore one <c>DbContext</c> connection — so a heartbeat can escape with
    /// something other than <see cref="OperationCanceledException"/>. That Task is awaited from the
    /// inner <c>finally</c>, after the child has already produced its answer, so a faulted renewal
    /// would replace a completed run with ChildFailed once every delegated token had been billed and
    /// with nothing left to retry.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenALeaseHeartbeatThrows_KeepsTheChildAnswerAndCompletesTheOperation()
    {
        ManualTimeProvider time = new();
        TaskCompletionSource childGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingTurnFacade facade = new(
            Result<PromptTurnResult>.Success(
                new PromptTurnResult("child summary", null)))
        {
            Gate = childGate.Task,
        };
        FakeOperationCoordinator operations = new()
        {
            HeartbeatFailure = new InvalidOperationException(
                "A second operation was started on this context instance before a previous operation completed."),
        };
        CapturingTelemetry telemetry = new();
        TestCapturingLogger<SubagentRunner> logger = new();
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
            telemetry,
            time,
            logger);

        Task<SubagentRunResult> run = runner.RunAsync(
            new SubagentRunRequest(
                "Long child task.",
                null,
                [],
                MaxTokens: 1_000,
                MaxCostUsd: null),
            CancellationToken.None);

        await facade.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(
            await WaitForAsync(
                () =>
                {
                    time.Advance(TimeSpan.FromMinutes(1));

                    return operations.HeartbeatAttempts >= 1;
                },
                TimeSpan.FromSeconds(10)),
            "expected the renewal loop to attempt at least one heartbeat");

        childGate.SetResult();

        SubagentRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Success);
        Assert.Equal("child summary", result.Summary);
        Assert.Equal(0, operations.HeartbeatCalls);
        Assert.Equal(1, operations.CompleteCalls);
        Assert.Equal(0, operations.FailCalls);

        // A heartbeat that threw committed nothing, so the row is still on the revision the last
        // accepted transition left it at and the terminal transition must address that one.
        Assert.Equal(1L, operations.LastCompleteRevision);
        Assert.Equal(SubagentRunOutcome.Completed, telemetry.Event?.Outcome);

        // Renewal stopping early is a diagnosable event, not a silent one.
        Assert.Contains(
            logger.Entries,
            static entry => entry.Level == LogLevel.Warning
                && entry.Exception is InvalidOperationException);
    }

    /// <summary>
    /// A faulted renewal awaited in the inner <c>finally</c> also replaces the caller's
    /// <see cref="OperationCanceledException"/>, which bypasses the cancellation filter and reports a
    /// cancelled run as ChildFailed — losing the 130 exit code the CLI contract depends on.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenCancelledWhileALeaseHeartbeatIsFaulting_StillReportsCancellation()
    {
        ManualTimeProvider time = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource childGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingTurnFacade facade = new(
            Result<PromptTurnResult>.Success(
                new PromptTurnResult("never returned", null)))
        {
            Gate = childGate.Task,
        };
        FakeOperationCoordinator operations = new()
        {
            HeartbeatFailure = new InvalidOperationException("connection is already open"),
        };
        CapturingTelemetry telemetry = new();
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
            telemetry,
            time,
            NullLogger<SubagentRunner>.Instance);

        Task<SubagentRunResult> run = runner.RunAsync(
            new SubagentRunRequest(
                "Long child task.",
                null,
                [],
                MaxTokens: 1_000,
                MaxCostUsd: null),
            cancellation.Token);

        await facade.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(
            await WaitForAsync(
                () =>
                {
                    time.Advance(TimeSpan.FromMinutes(1));

                    return operations.HeartbeatAttempts >= 1;
                },
                TimeSpan.FromSeconds(10)),
            "expected the renewal loop to attempt at least one heartbeat");

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(SubagentRunOutcome.Cancelled, telemetry.Event?.Outcome);
        Assert.Equal(SubagentFailureCodes.Cancelled, operations.FailureCode);
    }

    /// <summary>
    /// A refused <c>CompleteAsync</c> means another owner already moved the row. The runner still owes
    /// the ledger a terminal transition attempt rather than walking away silently.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenCompleteIsRefused_FailsTheOperationBeforeReportingFailure()
    {
        CapturingTurnFacade facade = new(
            Result<PromptTurnResult>.Success(
                new PromptTurnResult("child summary", null)));
        FakeOperationCoordinator operations = new() { RefuseComplete = true };
        CapturingTelemetry telemetry = new();
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
            telemetry,
            TimeProvider.System,
            NullLogger<SubagentRunner>.Instance);

        SubagentRunResult result = await runner.RunAsync(
            new SubagentRunRequest(
                "Contended task.",
                null,
                [],
                MaxTokens: 1_000,
                MaxCostUsd: null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SubagentFailureCodes.ChildFailed, result.FailureCode);
        Assert.Equal(1, operations.CompleteCalls);
        Assert.Equal(1, operations.FailCalls);
        Assert.Equal(SubagentFailureCodes.ChildFailed, operations.FailureCode);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class CapturingTurnFacade(
        Result<PromptTurnResult> result) : ITurnExecutionFacade
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PingRequest? Request { get; private set; }

        public ArcanumInvocationContext? InvocationContext { get; private set; }

        public Action? OnExecute { get; init; }

        /// <summary>Held open to model a child turn that outlives the durable lease.</summary>
        public Task? Gate { get; init; }

        public Task Entered => _entered.Task;

        public async Task<Result<PromptTurnResult>> ExecuteBufferedAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            bool hasIdempotencyKey,
            CancellationToken executionToken,
            InferenceAuditContext? auditContext = null)
        {
            _ = hasIdempotencyKey;
            _ = auditContext;
            executionToken.ThrowIfCancellationRequested();
            Request = request;
            InvocationContext = invocationContext;
            OnExecute?.Invoke();
            _ = _entered.TrySetResult();

            if (Gate is not null)
            {
                await Gate.WaitAsync(executionToken).ConfigureAwait(false);
            }

            return result;
        }

        public IAsyncEnumerable<IntelligenceEvent> ExecuteIntelligenceStreamAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            bool hasIdempotencyKey,
            CancellationToken executionToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<OpenAiChatChunk> ExecuteOpenAiSseAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            bool hasIdempotencyKey,
            string completionId,
            string model,
            CancellationToken executionToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException();
    }

    private sealed class FakeOperationCoordinator : ILongRunningOperationCoordinator
    {
        private int _heartbeatCalls;

        private int _heartbeatAttempts;

        public LongRunningOperationCreateRequest? StartRequest { get; private set; }

        public int CompleteCalls { get; private set; }

        public int FailCalls { get; private set; }

        public string? FailureCode { get; private set; }

        public long Revision { get; private set; } = 1;

        public int HeartbeatCalls => Volatile.Read(ref _heartbeatCalls);

        public int HeartbeatAttempts => Volatile.Read(ref _heartbeatAttempts);

        public long? LastCompleteRevision { get; private set; }

        public long? LastFailRevision { get; private set; }

        /// <summary>Models a row another owner already claimed and moved.</summary>
        public bool RefuseComplete { get; init; }

        /// <summary>
        /// Models a heartbeat that faults rather than being refused — the renewal loop shares the
        /// child turn's DI scope and DbContext connection, so a concurrent-use
        /// <see cref="InvalidOperationException"/> or a non-busy Sqlite error can escape the store.
        /// </summary>
        public Exception? HeartbeatFailure { get; init; }

        public Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LongRunningOperationLeaseResult> StartAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            _ = ownerId;
            _ = leaseDuration;
            cancellationToken.ThrowIfCancellationRequested();
            StartRequest = request;
            LongRunningOperation operation = new(
                Guid.NewGuid(),
                request.Kind,
                LongRunningOperationState.Running,
                request.RecoveryPolicy,
                request.RootOperationId,
                request.ParentOperationId,
                request.SessionId,
                request.RunId,
                request.InferenceRunId,
                request.BudgetReservationId,
                request.IdempotencyClaimId,
                request.CreatedAt,
                request.CreatedAt,
                request.CreatedAt,
                CompletedAt: null,
                LeaseOwner: ownerId,
                LeaseExpiresAt: request.CreatedAt.Add(leaseDuration),
                AttemptCount: 1,
                CheckpointVersion: 0,
                CheckpointPayload: null,
                CheckpointReference: null,
                request.PublicSummary,
                TerminalErrorCode: null,
                Revision: 1);

            return Task.FromResult(
                new LongRunningOperationLeaseResult(true, operation));
        }

        /// <summary>
        /// Mirrors <c>LongRunningOperationStore</c>: every accepted transition — heartbeat included —
        /// bumps the revision, and a transition addressed at a stale revision is refused.
        /// </summary>
        public Task<bool> CompleteAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            _ = operationId;
            _ = ownerId;
            cancellationToken.ThrowIfCancellationRequested();
            CompleteCalls++;
            LastCompleteRevision = expectedRevision;

            if (RefuseComplete || expectedRevision != Revision)
            {
                return Task.FromResult(false);
            }

            Revision++;
            return Task.FromResult(true);
        }

        public Task<bool> FailAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            string errorCode,
            CancellationToken cancellationToken = default)
        {
            _ = operationId;
            _ = ownerId;
            cancellationToken.ThrowIfCancellationRequested();
            FailCalls++;
            FailureCode = errorCode;
            LastFailRevision = expectedRevision;

            if (expectedRevision != Revision)
            {
                return Task.FromResult(false);
            }

            Revision++;
            return Task.FromResult(true);
        }

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            _ = operationId;
            _ = ownerId;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _heartbeatAttempts);

            if (HeartbeatFailure is not null)
            {
                throw HeartbeatFailure;
            }

            if (leaseDuration < TimeSpan.FromSeconds(5) || leaseDuration > TimeSpan.FromMinutes(15))
            {
                throw new ArgumentOutOfRangeException(nameof(leaseDuration));
            }

            Interlocked.Increment(ref _heartbeatCalls);
            Revision++;
            return Task.FromResult(true);
        }

        public Task<bool> CheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A clock whose timers fire only when the test advances it, so a five-minute renewal interval
    /// can be exercised without waiting five minutes.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();

        private readonly List<ManualTimer> _timers = [];

        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state, dueTime, period);

            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            ManualTimer[] due;

            lock (_gate)
            {
                _now = _now.Add(delta);
                due = [.. _timers];
            }

            foreach (ManualTimer timer in due)
            {
                timer.Advance(delta);
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _ = _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private readonly Lock _gate = new();

            private TimeSpan _remaining = dueTime;

            private TimeSpan _period = period;

            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    _remaining = dueTime;
                    _period = period;
                }

                return true;
            }

            public void Advance(TimeSpan delta)
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (_disposed || _remaining == Timeout.InfiniteTimeSpan)
                        {
                            return;
                        }

                        _remaining -= delta;

                        if (_remaining > TimeSpan.Zero)
                        {
                            return;
                        }

                        _remaining = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero
                            ? Timeout.InfiniteTimeSpan
                            : _remaining + _period;
                    }

                    callback(state);

                    delta = TimeSpan.Zero;
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                }

                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CapturingTelemetry : ISubagentTelemetrySink
    {
        public SubagentTelemetryEvent? Event { get; private set; }

        public void RecordSubagentRun(SubagentTelemetryEvent telemetryEvent) =>
            Event = telemetryEvent;
    }
}
