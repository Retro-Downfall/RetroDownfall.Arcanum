using System.Diagnostics;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Api.Intelligence.Subagents;

internal sealed class SubagentRunner(
    Lazy<ITurnExecutionFacade> turnCoordinator,
    ILongRunningOperationCoordinator operations,
    ISubagentTelemetrySink telemetry) : ISubagentRunner
{
    private static readonly TimeSpan OperationLease = TimeSpan.FromMinutes(15);

    private const string IsolatedSystemInstruction =
        "You are an isolated subagent. Work only from this system message, the child task prompt, "
        + "and explicitly attached file content. Do not assume parent conversation or memory. "
        + "Return a concise result summary for the parent agent.";

    public async Task<SubagentRunResult> RunAsync(
        SubagentRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Guid childRunId = Guid.NewGuid();
        DelegatedManaTracker tracker = new(
            request.MaxTokens,
            request.MaxCostUsd);
        Stopwatch stopwatch = Stopwatch.StartNew();
        SubagentRunOutcome outcome = SubagentRunOutcome.Failed;
        LongRunningOperationLeaseResult? operationLease = null;
        string ownerId = $"subagent:{Environment.ProcessId}:{childRunId:N}";

        try
        {
            operationLease = await operations
                .StartAsync(
                    new LongRunningOperationCreateRequest(
                        LongRunningOperationKinds.Subagent,
                        LongRunningOperationRecoveryPolicy.AbandonSafely,
                        "Isolated subagent run.",
                        DateTimeOffset.UtcNow,
                        RunId: childRunId),
                    ownerId,
                    OperationLease,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!operationLease.Acquired)
            {
                return Failure(
                    childRunId,
                    tracker,
                    SubagentFailureCodes.DurableStartFailed);
            }

            using IDisposable isolation = SubagentExecutionAmbient.EnterChild(tracker);

            PingRequest childRequest = BuildIsolatedRequest(request);

            Result<PromptTurnResult> result = await turnCoordinator.Value
                .ExecuteBufferedAsync(
                    childRequest,
                    hasIdempotencyKey: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (tracker.GetUsage().Exhausted)
            {
                outcome = SubagentRunOutcome.BudgetExhausted;

                await FailOperationAsync(
                        operationLease,
                        ownerId,
                        SubagentFailureCodes.BudgetExhausted)
                    .ConfigureAwait(false);

                return Failure(
                    childRunId,
                    tracker,
                    SubagentFailureCodes.BudgetExhausted);
            }

            if (result.IsFailure)
            {
                await FailOperationAsync(
                        operationLease,
                        ownerId,
                        SubagentFailureCodes.ChildFailed)
                    .ConfigureAwait(false);

                return Failure(
                    childRunId,
                    tracker,
                    SubagentFailureCodes.ChildFailed);
            }

            bool completed = await operations
                .CompleteAsync(
                    operationLease.Operation.Id,
                    ownerId,
                    operationLease.Operation.Revision,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (!completed)
            {
                return Failure(
                    childRunId,
                    tracker,
                    SubagentFailureCodes.ChildFailed);
            }

            outcome = SubagentRunOutcome.Completed;

            return new SubagentRunResult(
                Success: true,
                Summary: result.Value.Text,
                childRunId,
                tracker.GetUsage(),
                FailureCode: null);
        }
        catch (BudgetExhaustedException)
        {
            outcome = SubagentRunOutcome.BudgetExhausted;

            if (operationLease is { Acquired: true })
            {
                await FailOperationAsync(
                        operationLease,
                        ownerId,
                        SubagentFailureCodes.BudgetExhausted)
                    .ConfigureAwait(false);
            }

            return Failure(
                childRunId,
                tracker,
                SubagentFailureCodes.BudgetExhausted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = SubagentRunOutcome.Cancelled;

            if (operationLease is { Acquired: true })
            {
                await FailOperationAsync(
                        operationLease,
                        ownerId,
                        SubagentFailureCodes.Cancelled)
                    .ConfigureAwait(false);
            }

            throw;
        }
        catch
        {
            if (operationLease is { Acquired: true })
            {
                await FailOperationAsync(
                        operationLease,
                        ownerId,
                        SubagentFailureCodes.ChildFailed)
                    .ConfigureAwait(false);
            }

            return Failure(
                childRunId,
                tracker,
                SubagentFailureCodes.ChildFailed);
        }
        finally
        {
            stopwatch.Stop();
            DelegatedManaUsage usage = tracker.GetUsage();

            telemetry.RecordSubagentRun(
                new SubagentTelemetryEvent(
                    usage.Tokens,
                    usage.CostUsd,
                    stopwatch.Elapsed,
                    outcome));
        }
    }

    private static PingRequest BuildIsolatedRequest(SubagentRunRequest request) =>
        new(
            Prompt: request.Prompt,
            Model: request.Model,
            WorkingDirectory: string.Empty,
            ContextSnapshot: null,
            SessionId: null,
            DisableMcpTools: true,
            CliTerminalFormatting: false,
            UnattendedMode: true,
            AttachedFiles: [.. request.Files],
            ChronosyncDelta: null,
            StatelessMessages:
            [
                new CoreChatMessage("system", IsolatedSystemInstruction),
                new CoreChatMessage("user", request.Prompt),
            ],
            SkipSpellRouting: true,
            MaxOutputTokens: request.MaxTokens is { } maxTokens
                ? int.CreateSaturating(maxTokens)
                : null,
            DisableAllTools: true);

    private static SubagentRunResult Failure(
        Guid runId,
        DelegatedManaTracker tracker,
        string failureCode) =>
        new(
            Success: false,
            Summary: string.Empty,
            runId,
            tracker.GetUsage(),
            failureCode);

    private async Task FailOperationAsync(
        LongRunningOperationLeaseResult lease,
        string ownerId,
        string failureCode) =>
        _ = await operations
            .FailAsync(
                lease.Operation.Id,
                ownerId,
                lease.Operation.Revision,
                failureCode,
                CancellationToken.None)
            .ConfigureAwait(false);
}
