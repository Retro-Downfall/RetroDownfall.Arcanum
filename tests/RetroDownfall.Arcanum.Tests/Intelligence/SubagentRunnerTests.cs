using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Api.Intelligence.Subagents;

using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SubagentRunnerTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumed_child_uses_same_operation_identity_and_expiry_cancels_execution_without_completing_root(bool expire)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        Guid id = Guid.NewGuid();

        var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, id, h.Digest));

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var facade = new CapturingTurnFacade(new PromptTurnResult("summary", null))
        {
            Gate = release.Task,
            OnExecute = () => Assert.Same(child, SubagentExecutionAmbient.MemoryDelegation)
        };

        FakeOperationCoordinator operations = new();

        SubagentRunner runner = new(new(() => facade), operations, TimeProvider.System, NullLogger<SubagentRunner>.Instance);

        var request = new SubagentRunRequest("actual task", "model", [], 100, null) { MemoryDelegation = new(id, child) };

        var running = runner.RunAsync(request, default);

        await facade.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(id, operations.StartRequest!.RunId);

        Assert.Same(ArcanumInvocationContext.None, facade.InvocationContext);

        if (expire)
        {
            h.Time.Advance(TimeSpan.FromMinutes(5));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(SubagentFailureCodes.Cancelled, operations.FailureCode);
        }
        else
        {
            release.TrySetResult();

            Assert.Equal(id, (await running.WaitAsync(TimeSpan.FromSeconds(10))).RunId);

            Assert.False(root.Revocation.IsCancellationRequested);

            await using var continuation = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(root));
        }

        Assert.Throws<InvalidOperationException>(() => child.Sources);
    }

    [SkippableFact]
    public async Task Capsule_materialization_owns_sources_until_child_consumer_teardown()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        Guid id = Guid.NewGuid();
        var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, id, h.Digest));
        bool retainedWhileConsumerActive = false;
        var facade = new CapturingTurnFacade(new PromptTurnResult("summary", null))
        {
            OnExecute = () =>
            {
                h.Time.Advance(TimeSpan.FromMinutes(5));
                retainedWhileConsumerActive = !child.Sources.Fragments.IsEmpty;
            }
        };
        var runner = new SubagentRunner(new(() => facade), new FakeOperationCoordinator(), TimeProvider.System, NullLogger<SubagentRunner>.Instance);
        try
        {
            await runner.RunAsync(new("task", "model", [], 100, null) { MemoryDelegation = new(id, child) }, default);
        }
        catch (OperationCanceledException) { }
        Assert.True(retainedWhileConsumerActive);
        Assert.Throws<InvalidOperationException>(() => child.Sources);
    }

    [SkippableTheory]
    [InlineData("success")]
    [InlineData("empty")]
    [InlineData("fault")]
    [InlineData("complete-refused")]
    public async Task Protected_child_preserves_immutable_metadata_on_every_result(string outcome)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync(sensitive: true);
        await using var root = await h.PrepareAsync();
        Guid id = Guid.NewGuid();
        var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, id, h.Digest));
        var facade = new CapturingTurnFacade(new PromptTurnResult(outcome == "empty" ? "" : "summary", null))
        {
            Failure = outcome == "fault" ? new InvalidOperationException("private source echo") : null,
        };
        var runner = new SubagentRunner(new(() => facade), new FakeOperationCoordinator { RefuseComplete = outcome == "complete-refused" },
            TimeProvider.System, NullLogger<SubagentRunner>.Instance);
        var result = await runner.RunAsync(new("task", "model", [], 100, null) { MemoryDelegation = new(id, child) }, default);
        Assert.Equal(RetroDownfall.Arcanum.Core.Covenant.ContentSensitivity.CovenantDerived, result.Sensitivity!.Level);
        Assert.Equal(child.Sensitivity.Provenance, result.Sensitivity!.Provenance);
        Assert.Throws<InvalidOperationException>(() => child.Sources);
        Assert.Equal(root.Sensitivity, child.Sensitivity);
    }

    [SkippableFact]
    public async Task Expiry_before_first_scope_releases_content_but_retains_minimal_result_metadata()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync(sensitive: true);
        await using var root = await h.PrepareAsync();
        Guid id = Guid.NewGuid();
        var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, id, h.Digest));
        var captured = child.Sensitivity;
        h.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Throws<InvalidOperationException>(() => child.Sources);
        Assert.Equal(captured, child.Sensitivity);
        Assert.Equal(RetroDownfall.Arcanum.Core.Covenant.ContentSensitivity.CovenantDerived, captured.Level);
        var operations = new FakeOperationCoordinator();
        var runner = new SubagentRunner(new(() => new CapturingTurnFacade(new PromptTurnResult("never", null))), operations,
            TimeProvider.System, NullLogger<SubagentRunner>.Instance);
        try { await runner.RunAsync(new("task", "model", [], 100, null) { MemoryDelegation = new(id, child) }, default); }
        catch (OperationCanceledException) { }
        Assert.Null(operations.StartRequest);
    }

    [SkippableTheory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "empty")]
    [InlineData(true, "fault")]
    public async Task Selected_child_initial_disclosure_and_result_include_frozen_parent_input_label(bool selectedSensitive, string outcome)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync(sensitive: selectedSensitive);
        await using var root = await h.PrepareAsync();
        Guid parentGeneration = Guid.NewGuid();
        var input = RetroDownfall.Arcanum.Core.Covenant.ProviderCallSensitivities.Create(
            RetroDownfall.Arcanum.Core.Covenant.ContentSensitivity.CovenantDerived,
            RetroDownfall.Arcanum.Core.Covenant.GenerationProvenance.CreateExact([parentGeneration]));
        var binding = new RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation.MemoryDelegationRootBinding(
            new PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator);
        binding.RecordDerived(input);
        RetroDownfall.Arcanum.Core.Covenant.ProviderCallSensitivity? initial = null;
        var facade = new CapturingTurnFacade(new PromptTurnResult(outcome == "empty" ? "" : "summary", null))
        {
            OnExecute = () => initial = SubagentExecutionAmbient.Protection!.Sensitivity,
            Failure = outcome == "fault" ? new InvalidOperationException("private") : null,
        };
        var runner = new SubagentRunner(new(() => facade), new FakeOperationCoordinator(), TimeProvider.System, NullLogger<SubagentRunner>.Instance);
        var tool = new RetroDownfall.Arcanum.Api.Intelligence.Tools.ArcanumDelegateTaskTool(runner, "model", binding);
        var output = await tool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
        {
            ["prompt"] = "explicit derived input", ["max_tokens"] = 100, ["memory_grant"] = "memory",
        }));
        Assert.NotNull(initial);
        Assert.Equal(RetroDownfall.Arcanum.Core.Covenant.ContentSensitivity.CovenantDerived, initial.Level);
        Assert.Contains(parentGeneration, initial.Provenance.ExactGenerationIds);
        foreach (var generation in root.Sensitivity.Provenance.ExactGenerationIds) Assert.Contains(generation, initial.Provenance.ExactGenerationIds);
        var result = Assert.IsType<RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation.MemoryDelegationToolResult>(output);
        Assert.Equal(initial.Digest, result.Sensitivity.Digest);
        Assert.Equal("explicit derived input", facade.Request!.Prompt);
        Assert.Same(ArcanumInvocationContext.None, facade.InvocationContext);
    }

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
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
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
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
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
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
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

        // Advance only an armed delay. Each next timer proves the previous heartbeat completed;
        // advancing time inside a polling predicate races the asynchronous renewal continuation.
        for (int renewal = 0; renewal < 4; renewal++)
        {
            Assert.Equal(TimeSpan.FromMinutes(5), await time.NextTimerAsync());

            time.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(TimeSpan.FromMinutes(5), await time.NextTimerAsync());

        Assert.Equal(4, operations.HeartbeatCalls);

        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(20), time.GetUtcNow());

        childGate.SetResult();

        SubagentRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Success);
        Assert.Equal("child summary", result.Summary);
        Assert.Equal(1, operations.CompleteCalls);
        Assert.Equal(0, operations.FailCalls);

        // The heartbeats bumped the stored revision; Complete has to address the current one.
        Assert.Equal(1L + operations.HeartbeatCalls, operations.LastCompleteRevision);
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
        TestCapturingLogger<SubagentRunner> logger = new();
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
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

        time.Advance(await time.NextTimerAsync());

        await operations.HeartbeatAttempted.WaitAsync(TimeSpan.FromSeconds(10));

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
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
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

        time.Advance(await time.NextTimerAsync());

        await operations.HeartbeatAttempted.WaitAsync(TimeSpan.FromSeconds(10));

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(10)));

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
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
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

    /// <summary>
    /// A child turn that faults outside the provider/engine paths (which log downstream) used to be
    /// collapsed into <c>Subagent.ChildFailed</c> with nothing in the log. The failure is now
    /// logged, as exception type only because the message can echo child prompt or file content.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenChildThrowsUnexpectedly_LogsAndFailsTheOperation()
    {
        const string marker = "child-secret-marker";
        CapturingTurnFacade facade = new(
            Result<PromptTurnResult>.Success(new PromptTurnResult("unused", null)))
        {
            Failure = new InvalidOperationException(marker),
        };
        FakeOperationCoordinator operations = new();
        TestCapturingLogger<SubagentRunner> logger = new();
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            operations,
            TimeProvider.System,
            logger);

        SubagentRunResult result = await runner.RunAsync(
            new SubagentRunRequest("Faulting task.", null, [], MaxTokens: 1_000, MaxCostUsd: null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SubagentFailureCodes.ChildFailed, result.FailureCode);
        Assert.Equal(1, operations.FailCalls);
        Assert.Equal(SubagentFailureCodes.ChildFailed, operations.FailureCode);

        TestLogEntry entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);

        Assert.Contains(result.RunId.ToString(), entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(InvalidOperationException), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    /// <summary>
    /// R-008: the child turn starts inside the parent's <c>delegate_task</c> call, where the parent
    /// turn's accounting is now re-established. The child must not see it: a child that adopted the
    /// parent's handle would settle the parent's run and reservation when it completed.
    /// </summary>
    /// <remarks>
    /// Restoration is not asserted here. <see cref="SubagentRunner.RunAsync"/> is an async method, and
    /// an <c>AsyncLocal</c> it writes never flows back to its caller, so the caller sees the parent's
    /// handle afterwards whether or not the suspension was undone. The restore is pinned where it is
    /// observable, in one flow: <c>TurnAccountingHandleTests.AmbientSuspend_HidesTheHandleAndWriterThenRestoresThem</c>.
    /// </remarks>
    [Fact]
    public async Task RunAsync_HidesTheParentTurnsAccountingFromTheChild()
    {
        TurnAccountingHandle parent = (await TurnAccountingHandle.BeginAsync(
                turnRunWriter: null,
                budgetReservations: null,
                new PricingSettings(),
                "test-model",
                sessionId: null,
                surface: "test",
                purpose: "chat",
                requestId: "parent",
                CancellationToken.None))
            .Value;

        bool childSawAccounting = true;

        CapturingTurnFacade facade = new(
            Result<PromptTurnResult>.Success(
                new PromptTurnResult(
                    "child summary",
                    new ChatCompletionUsage(10, 5, 15))))
        {
            OnExecute = () => childSawAccounting = TurnAccountingAmbient.Current is not null,
        };
        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => facade),
            new FakeOperationCoordinator(),
            TimeProvider.System,
            NullLogger<SubagentRunner>.Instance);

        using (TurnAccountingAmbient.Push(parent, writer: null))
        {
            SubagentRunResult result = await runner.RunAsync(
                new SubagentRunRequest("child task", "test-model", [], MaxTokens: 1_000, MaxCostUsd: null),
                CancellationToken.None);

            Assert.True(result.Success);
        }

        Assert.False(childSawAccounting);
    }

    private sealed class CapturingTurnFacade(
        Result<PromptTurnResult> result) : ITurnExecutionFacade
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PingRequest? Request { get; private set; }

        public ArcanumInvocationContext? InvocationContext { get; private set; }

        public Action? OnExecute { get; init; }

        /// <summary>Thrown from the child turn to model an unexpected fault.</summary>
        public Exception? Failure { get; init; }

        /// <summary>Held open to model a child turn that outlives the durable lease.</summary>
        public Task? Gate { get; init; }

        public Task Entered => _entered.Task;

        public async Task<Result<PromptTurnResult>> ExecuteBufferedAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken executionToken,
            InferenceAuditContext? auditContext = null)
        {
            _ = auditContext;
            executionToken.ThrowIfCancellationRequested();
            Request = request;
            InvocationContext = invocationContext;
            OnExecute?.Invoke();
            _ = _entered.TrySetResult();

            if (Failure is not null)
            {
                throw Failure;
            }

            if (Gate is not null)
            {
                await Gate.WaitAsync(executionToken).ConfigureAwait(false);
            }

            return result;
        }

        public IAsyncEnumerable<IntelligenceEvent> ExecuteIntelligenceStreamAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken executionToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException();
    }

    private sealed class FakeOperationCoordinator : ILongRunningOperationCoordinator
    {
        private int _heartbeatCalls;

        private readonly TaskCompletionSource _heartbeatAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LongRunningOperationCreateRequest? StartRequest { get; private set; }

        public int CompleteCalls { get; private set; }

        public int FailCalls { get; private set; }

        public string? FailureCode { get; private set; }

        public long Revision { get; private set; } = 1;

        public int HeartbeatCalls => Volatile.Read(ref _heartbeatCalls);

        public Task HeartbeatAttempted => _heartbeatAttempted.Task;

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
            _heartbeatAttempted.TrySetResult();

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

        private readonly Channel<TimeSpan> _scheduled = Channel.CreateUnbounded<TimeSpan>();

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

            _scheduled.Writer.TryWrite(dueTime);

            return timer;
        }

        public Task<TimeSpan> NextTimerAsync() =>
            _scheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

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
}
