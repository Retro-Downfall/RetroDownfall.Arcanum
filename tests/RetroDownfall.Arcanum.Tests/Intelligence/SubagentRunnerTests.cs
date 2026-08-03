using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SubagentRunnerTests
{
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
            telemetry);
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
            telemetry);

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

    private sealed class CapturingTurnFacade(
        Result<PromptTurnResult> result) : ITurnExecutionFacade
    {
        public PingRequest? Request { get; private set; }

        public Action? OnExecute { get; init; }

        public Task<Result<PromptTurnResult>> ExecuteBufferedAsync(
            PingRequest request,
            bool hasIdempotencyKey,
            CancellationToken executionToken,
            InferenceAuditContext? auditContext = null)
        {
            _ = hasIdempotencyKey;
            _ = auditContext;
            executionToken.ThrowIfCancellationRequested();
            Request = request;
            OnExecute?.Invoke();
            return Task.FromResult(result);
        }

        public IAsyncEnumerable<IntelligenceEvent> ExecuteIntelligenceStreamAsync(
            PingRequest request,
            bool hasIdempotencyKey,
            CancellationToken executionToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<OpenAiChatChunk> ExecuteOpenAiSseAsync(
            PingRequest request,
            bool hasIdempotencyKey,
            string completionId,
            string model,
            CancellationToken executionToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException();
    }

    private sealed class FakeOperationCoordinator : ILongRunningOperationCoordinator
    {
        public LongRunningOperationCreateRequest? StartRequest { get; private set; }

        public int CompleteCalls { get; private set; }

        public int FailCalls { get; private set; }

        public string? FailureCode { get; private set; }

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

        public Task<bool> CompleteAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            _ = operationId;
            _ = ownerId;
            _ = expectedRevision;
            cancellationToken.ThrowIfCancellationRequested();
            CompleteCalls++;
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
            _ = expectedRevision;
            cancellationToken.ThrowIfCancellationRequested();
            FailCalls++;
            FailureCode = errorCode;
            return Task.FromResult(true);
        }

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

    private sealed class CapturingTelemetry : ISubagentTelemetrySink
    {
        public SubagentTelemetryEvent? Event { get; private set; }

        public void RecordSubagentRun(SubagentTelemetryEvent telemetryEvent) =>
            Event = telemetryEvent;
    }
}
