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
    ISubagentTelemetrySink telemetry,
    TimeProvider timeProvider) : ISubagentRunner
{
    private static readonly TimeSpan OperationLease = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often the lease is renewed while the child turn runs. Comfortably inside
    /// <see cref="OperationLease"/> — which is the coordinator's hard maximum — so a slow local
    /// model cannot outlive the lease and have the reconciler abandon a run that is still going.
    /// </summary>
    private static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromMinutes(5);

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
        long operationRevision = 0;
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

            operationRevision = operationLease.Operation.Revision;

            using IDisposable isolation = SubagentExecutionAmbient.EnterChild(tracker);

            PingRequest childRequest = BuildIsolatedRequest(request);

            // The lease is taken at the coordinator's 15-minute maximum, and a delegated child on
            // local inference can outrun it. Renew for as long as the child runs, or the reconciler
            // claims the row, abandons it under a new owner, and the finished answer is discarded.
            using CancellationTokenSource renewalStop = new();

            Task<long> renewal = RenewLeaseWhileRunningAsync(
                operationLease.Operation.Id,
                ownerId,
                operationRevision,
                renewalStop.Token);

            Result<PromptTurnResult> result;

            try
            {
                result = await turnCoordinator.Value
                    .ExecuteBufferedAsync(
                        childRequest,
                        hasIdempotencyKey: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await renewalStop.CancelAsync().ConfigureAwait(false);

                operationRevision = await renewal.ConfigureAwait(false);
            }

            if (tracker.GetUsage().Exhausted)
            {
                outcome = SubagentRunOutcome.BudgetExhausted;

                await FailOperationAsync(
                        operationLease,
                        ownerId,
                        operationRevision,
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
                        operationRevision,
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
                    operationRevision,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (!completed)
            {
                // Somebody else moved the row. Still owe the ledger a terminal transition attempt
                // from this owner rather than leaving the operation dangling.
                await FailOperationAsync(
                        operationLease,
                        ownerId,
                        operationRevision,
                        SubagentFailureCodes.ChildFailed)
                    .ConfigureAwait(false);

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
                        operationRevision,
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
                        operationRevision,
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
                        operationRevision,
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
        long expectedRevision,
        string failureCode) =>
        _ = await operations
            .FailAsync(
                lease.Operation.Id,
                ownerId,
                expectedRevision,
                failureCode,
                CancellationToken.None)
            .ConfigureAwait(false);

    /// <summary>
    /// Holds the durable lease for as long as the child turn runs, and reports the revision the row
    /// reached. Every accepted heartbeat bumps the stored revision by exactly one and requires this
    /// owner still holds an unexpired lease, so counting successful renewals locally keeps the
    /// subsequent <c>CompleteAsync</c>/<c>FailAsync</c> addressed at the current row. A refused
    /// renewal means the lease is gone — there is nothing left to renew, so the loop stops and the
    /// terminal transition is left to fail its compare-and-set.
    /// </summary>
    private async Task<long> RenewLeaseWhileRunningAsync(
        Guid operationId,
        string ownerId,
        long startingRevision,
        CancellationToken stopToken)
    {
        long revision = startingRevision;

        try
        {
            while (true)
            {
                await Task.Delay(LeaseRenewalInterval, timeProvider, stopToken).ConfigureAwait(false);

                if (!await operations
                        .HeartbeatAsync(operationId, ownerId, OperationLease, stopToken)
                        .ConfigureAwait(false))
                {
                    break;
                }

                revision++;
            }
        }
        catch (OperationCanceledException)
        {
            // The child finished, failed, or was cancelled. Outliving it was the loop's only job.
        }

        return revision;
    }
}
