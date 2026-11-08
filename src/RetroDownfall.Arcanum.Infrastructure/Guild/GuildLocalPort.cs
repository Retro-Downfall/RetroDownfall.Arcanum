using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Executes committed local work; admission and identity creation belong to ConclaveArchmage.</summary>
internal sealed partial class GuildLocalPort(GuildQuestStore quests, GuildLocalHostPolicy callers, SignetAuthority authority,
    ILongRunningOperationStore operations, IGuildLocalExecutionEngine engine, IGuildLocalInferenceRuntime inference,
    IGuildLocalQuestScheduler host, LongRunningOperationOwnership ownership, TimeProvider time) : IGuildPort
{
    public Task<Result<GuildDispatchObservation>> DispatchAsync(GuildExecutionHandle execution, GuildDispatchRequest request,
        CancellationToken cancellationToken) => DispatchCoreAsync(execution, request, null, cancellationToken);

    internal Task<Result<GuildDispatchObservation>> DispatchClaimedAsync(GuildExecutionHandle execution,
        GuildDispatchRequest request, Guid claim, CancellationToken cancellationToken) =>
        DispatchCoreAsync(execution, request, claim, cancellationToken);

    private async Task<Result<GuildDispatchObservation>> DispatchCoreAsync(GuildExecutionHandle execution, GuildDispatchRequest request,
        Guid? claimed, CancellationToken cancellationToken)
    {
        if (execution is null || request is null || request.QuestId != execution.QuestId || request.RunId != execution.RunId
            || request.DispatchId == Guid.Empty || request.ExpectedOperationRevision < 0)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The dispatch does not identify committed local work.");

        var caller = await callers.CreateRunCallerAsync(authority, execution, request.DispatchId, cancellationToken).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        GuildQuestDriver driver = new(execution, "observe", request.ExpectedOperationRevision, caller.Value);

        var state = await quests.ReadLocalPortAsync(driver, request.DispatchId, cancellationToken).ConfigureAwait(false);

        if (state.IsFailure) return state.Error;

        GuildLocalPortState before = state.Value;

        bool continuation = request.DispatchId != request.QuestId;

        if (request.OperationId != before.Quest.Quest.OperationId)
            return new Error(ErrorCodes.Guild.IdempotencyConflict, "The dispatch differs from its committed operation.");

        if (continuation)
        {
            var accepted = await quests.ReadLocalContinuationAsync(driver, request.DispatchId, cancellationToken, false).ConfigureAwait(false);

            if (accepted.IsFailure) return accepted.Error;

            if (accepted.Value is not { } answer || answer.Mutation != request.Mutation
                || answer.Continuation.ContentRef.Digest != request.InvocationDigest)
                return new Error(ErrorCodes.Guild.IdempotencyConflict, "The dispatch differs from its accepted continuation.");
        }
        else if (request.Mutation?.IdempotencyKey != before.Quest.MutationId
            || request.Mutation.CanonicalRequestDigest != before.Quest.IntentDigest
            || request.InvocationDigest != before.Binding.Input.Digest)
            return new Error(ErrorCodes.Guild.IdempotencyConflict, "The dispatch differs from its committed input.");

        if (before.Quest.Quest.ResultReceiptId is not null) return Observation(before, request.DispatchId);

        if (!host.IsAvailable || !callers.IsExecutionEnabled)
            return new Error(ErrorCodes.Guild.Unavailable, "Qualified local execution is unavailable.");

        if (before.Quest.Quest.ReconciliationRequired || before.Quest.Quest.CancellationRequested
            || before.Dispatch is { Evidence: not GuildDispatchEvidence.Prepared })
            return Observation(before, request.DispatchId);

        if (before.Operation.Revision != request.ExpectedOperationRevision)
            return new Error(ErrorCodes.Guild.RevisionConflict, "The operation revision changed before local dispatch.");

        Guid claim;

        if (claimed is Guid existing)
        {
            if (!ownership.IsClaimedBy(request.OperationId, existing))
                return new Error(ErrorCodes.Guild.AuthorityMismatch, "The scheduler does not own this operation.");

            claim = existing;
        }
        else if (!ownership.TryClaim(request.OperationId, out claim)) return Observation(before, request.DispatchId);

        string leaseOwner = "guild-" + claim.ToString("N");

        LocalRun? running = null;

        try
        {
            DateTimeOffset now = time.GetUtcNow();

            var lease = await operations.TryAcquireLeaseAsync(request.OperationId, leaseOwner, now, now.AddMinutes(2), cancellationToken).ConfigureAwait(false);

            if (!lease.Acquired) return new Error(ErrorCodes.Guild.RevisionConflict, "The committed operation has another live owner.");

            driver = new(execution, leaseOwner, lease.Operation.Revision, caller.Value);

            var input = await quests.ReadLocalInputAsync(driver, cancellationToken).ConfigureAwait(false);

            if (input.IsFailure) return input.Error;

            var dispatch = await quests.PrepareDispatchAsync(driver, request.DispatchId, request.InvocationDigest,
                execution.RunId, continuation, cancellationToken).ConfigureAwait(false);

            if (dispatch.IsFailure) return dispatch.Error;

            GuildLocalInferenceExecution invocation = new(quests, driver, dispatch.Value);

            using CancellationTokenSource runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            using CancellationTokenSource pulseCancellation = new();

            running = new(invocation, runCancellation, continuation);

            Task pulse = PulseAsync(running, pulseCancellation.Token);

            try
            {
                try
                {
                    Result<PromptTurnResult> result = await engine.ExecuteGuildAsync(before.Binding, input.Value, invocation, inference,
                        new EngineWait(this, running), runCancellation.Token).ConfigureAwait(false);

                    return await CompleteAsync(running, before, request, result, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await pulseCancellation.CancelAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await pulse.ConfigureAwait(false);
            }
        }
        catch (Exception) when (running is not null)
        {
            return await ParkAsync(running.Execution, request.DispatchId).ConfigureAwait(false);
        }
        finally
        {
            running?.Serial.Dispose();

            ownership.Release(request.OperationId, claim);
        }
    }

    public async Task<Result<GuildDispatchObservation>> ObserveAsync(GuildExecutionHandle execution, Guid questId, Guid dispatchId,
        CancellationToken cancellationToken)
    {
        if (execution is null || execution.QuestId != questId || dispatchId == Guid.Empty)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The observation does not identify the bound Quest.");

        var caller = await callers.CreateRunCallerAsync(authority, execution, dispatchId, cancellationToken).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        var state = await quests.ReadLocalPortAsync(new(execution, "observe", 0, caller.Value), dispatchId, cancellationToken).ConfigureAwait(false);

        return state.IsFailure ? state.Error : Observation(state.Value, dispatchId);
    }

    private static GuildDispatchObservation Observation(GuildLocalPortState state, Guid dispatchId) =>
        new(state.Quest.Quest.Id, dispatchId, state.Dispatch?.Evidence ?? GuildDispatchEvidence.Prepared,
            state.Quest.Quest.State, state.Quest.Quest.ReconciliationRequired, state.Operation.TerminalErrorCode,
            state.Binding.ApprenticeId, null, null);

    private async Task<Result<GuildDispatchObservation>> ParkAsync(GuildLocalInferenceExecution execution, Guid observedDispatch)
    {
        var current = await quests.ReadLocalPortAsync(execution.Driver, execution.Dispatch.DispatchId, CancellationToken.None).ConfigureAwait(false);

        if (current.IsSuccess && current.Value.Quest.Quest.CancellationRequested)
        {
            // This path is reached only after the awaited engine and all its in-process work
            // have stopped. The store separately proves the durable effect and child frontier.
            var fresh = await quests.RefreshLocalExecutionAsync(execution, time.GetUtcNow(), false, CancellationToken.None).ConfigureAwait(false);

            if (fresh.IsSuccess)
            {
                var resolved = await quests.ResolveLocalDispatchAsync(execution.Driver, execution.Dispatch.DispatchId, CancellationToken.None).ConfigureAwait(false);

                if (resolved.IsSuccess)
                {
                    current = await quests.ReadLocalPortAsync(execution.Driver, execution.Dispatch.DispatchId, CancellationToken.None).ConfigureAwait(false);

                    if (current.IsSuccess)
                    {
                        Guid resultId = Guid.NewGuid();

                        string digest = Convert.ToHexString(SHA256.HashData([]));

                        GuildQuestResult canceled = new(new(execution.Driver.Execution.QuestId, resultId, digest, null,
                            QuestState.Canceled, LongRunningOperationRecoveryOutcomes.Cancelled), null,
                            NoDispatchProven: !current.Value.HadPhysicalDispatch, WorkStoppedProven: true);

                        var committed = await quests.CommitResultAsync(execution.Driver, new(resultId, digest),
                            current.Value.Quest.Quest.Revision, canceled, CancellationToken.None).ConfigureAwait(false);

                        if (committed.IsSuccess)
                            return await ObserveAsync(execution.Driver.Execution, execution.Driver.Execution.QuestId, observedDispatch, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }

        var parked = await quests.ParkLocalExecutionAsync(execution.Driver, CancellationToken.None).ConfigureAwait(false);

        if (parked.IsFailure) return parked.Error;

        return await ObserveAsync(execution.Driver.Execution, execution.Driver.Execution.QuestId, observedDispatch, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PulseAsync(LocalRun run, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), time, ct).ConfigureAwait(false);

                await run.Serial.WaitAsync(ct).ConfigureAwait(false);

                try
                {
                    var pulse = await quests.RefreshLocalExecutionAsync(run.Execution, time.GetUtcNow(), true, ct).ConfigureAwait(false);

                    if (pulse.IsFailure || pulse.Value)
                    {
                        await run.Cancellation.CancelAsync().ConfigureAwait(false);

                        return;
                    }
                }
                finally
                {
                    run.Serial.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            await run.Cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<Result<GuildLocalEngineContinuation>> WaitAsync(LocalRun run, GuildLocalInferenceExecution execution,
        GuildLocalWaitCheckpointWrite checkpoint, CancellationToken ct)
    {
        Guid continuationId;

        await run.Serial.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (!ReferenceEquals(execution, run.Execution))
                return new Error(ErrorCodes.Guild.AuthorityMismatch, "The question does not belong to the active local dispatch.");

            if (run.Restoring)
            {
                run.Restoring = false;

                continuationId = execution.Dispatch.DispatchId;
            }
            else
            {
                var fresh = await quests.RefreshLocalExecutionAsync(execution, time.GetUtcNow(), false, ct).ConfigureAwait(false);

                if (fresh.IsFailure) return fresh.Error;

                if (fresh.Value) return new Error(ErrorCodes.Guild.AuthorityRevoked, "The Quest is stopping.");

                var resolved = await quests.ResolveLocalDispatchAsync(execution.Driver, execution.Dispatch.DispatchId, ct).ConfigureAwait(false);

                if (resolved.IsFailure) return resolved.Error;

                continuationId = Guid.NewGuid();

                var waiting = await quests.WaitForInputAsync(execution.Driver, Guid.NewGuid(), continuationId, checkpoint, ct).ConfigureAwait(false);

                if (waiting.IsFailure) return waiting.Error;

                var refreshed = await quests.RefreshLocalExecutionAsync(execution, time.GetUtcNow(), false, ct).ConfigureAwait(false);

                if (refreshed.IsFailure) return refreshed.Error;
            }
        }
        finally
        {
            run.Serial.Release();
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var accepted = await quests.ReadLocalContinuationAsync(run.Execution.Driver, continuationId, ct).ConfigureAwait(false);

            if (accepted.IsFailure) return accepted.Error;

            if (accepted.Value is { Body: not null } answer)
            {
                await run.Serial.WaitAsync(ct).ConfigureAwait(false);

                try
                {
                    var refreshed = await quests.RefreshLocalExecutionAsync(run.Execution, time.GetUtcNow(), false, ct).ConfigureAwait(false);

                    if (refreshed.IsFailure) return refreshed.Error;

                    if (refreshed.Value) return new Error(ErrorCodes.Guild.AuthorityRevoked, "The Quest is stopping.");

                    var prepared = await quests.PrepareDispatchAsync(run.Execution.Driver, continuationId,
                        answer.Continuation.ContentRef.Digest, execution.Driver.Execution.RunId, true, ct).ConfigureAwait(false);

                    if (prepared.IsFailure) return prepared.Error;

                    if (prepared.Value.Evidence != GuildDispatchEvidence.Prepared)
                        return new Error(ErrorCodes.Guild.ReconciliationRequired, "The accepted continuation already has execution evidence.");

                    run.Execution = new(quests, run.Execution.Driver, prepared.Value);

                    return new GuildLocalEngineContinuation(run.Execution, answer.Continuation, answer.Body);
                }
                finally
                {
                    run.Serial.Release();
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct).ConfigureAwait(false);
        }
    }

    private sealed class EngineWait(GuildLocalPort port, LocalRun run) : IGuildLocalEngineWait
    {
        public Task<Result<GuildLocalEngineContinuation>> WaitAsync(GuildLocalInferenceExecution execution,
            GuildLocalWaitCheckpointWrite checkpoint, CancellationToken cancellationToken) =>
            port.WaitAsync(run, execution, checkpoint, cancellationToken);
    }

    private sealed class LocalRun(GuildLocalInferenceExecution execution, CancellationTokenSource cancellation, bool restoring)
    {
        internal GuildLocalInferenceExecution Execution = execution;

        internal readonly CancellationTokenSource Cancellation = cancellation;

        internal readonly SemaphoreSlim Serial = new(1, 1);

        internal bool Restoring = restoring;
    }

    private Task<Result<GuildDispatchObservation>> Unavailable()
    {
        _ = (quests, callers, authority, operations, engine, inference, host, ownership, time);

        return Task.FromResult(Result<GuildDispatchObservation>.Failure(new(ErrorCodes.Guild.Unavailable, "Qualified local execution is unavailable.")));
    }
}
