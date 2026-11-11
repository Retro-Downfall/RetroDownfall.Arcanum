using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Executes committed local work; admission and identity creation belong to ConclaveArchmage.</summary>
internal sealed partial class GuildLocalPort(GuildQuestStore quests, GuildLocalHostPolicy callers, SignetAuthority authority,
    ILongRunningOperationStore operations, IGuildLocalExecutionEngine engine, IGuildLocalInferenceRuntime inference,
    IGuildLocalQuestScheduler host, LongRunningOperationOwnership ownership, TimeProvider time) : IGuildPort
{
    public Task<Result<GuildDispatchObservation>> DispatchAsync(GuildExecutionHandle execution, GuildDispatchRequest request,
        CancellationToken cancellationToken) => DispatchDirectCoreAsync(execution, request, null, cancellationToken);

    internal Task<Result<GuildDispatchObservation>> DispatchClaimedAsync(IGrimoireWorkLease work, GuildLocalQuestScheduler scheduler,
        GuildExecutionHandle execution, GuildDispatchRequest request, Guid claim, CancellationToken cancellationToken) =>
        DispatchScheduledCoreAsync(execution, request, claim, work, scheduler, cancellationToken);

    private sealed record LocalDispatchPreflight(GuildLocalPortState Before, GuildCallerContext Caller,
        bool Continuation, GuildDispatchObservation? Immediate);

    private sealed record LocalDispatchPrepared(GuildLocalExecutionInput Input, GuildLocalInferenceExecution Invocation);

    private async Task<Result<LocalDispatchPreflight>> ReadDispatchPreflightAsync(GuildExecutionHandle execution,
        GuildDispatchRequest request, CancellationToken cancellationToken)
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

        if (before.Quest.Quest.ResultReceiptId is not null) return new LocalDispatchPreflight(before, caller.Value, continuation, Observation(before, request.DispatchId));

        if (!host.IsAvailable || !callers.IsExecutionEnabled)
            return new Error(ErrorCodes.Guild.Unavailable, "Qualified local execution is unavailable.");

        if (before.Quest.Quest.ReconciliationRequired || before.Quest.Quest.CancellationRequested
            || before.Dispatch is { Evidence: not GuildDispatchEvidence.Prepared })
            return new LocalDispatchPreflight(before, caller.Value, continuation, Observation(before, request.DispatchId));

        if (before.Operation.Revision != request.ExpectedOperationRevision)
            return new Error(ErrorCodes.Guild.RevisionConflict, "The operation revision changed before local dispatch.");

        return new LocalDispatchPreflight(before, caller.Value, continuation, null);
    }

    private async Task<Result<LocalDispatchPrepared>> PrepareOwnedDispatchAsync(GuildExecutionHandle execution,
        GuildDispatchRequest request, GuildCallerContext caller, string leaseOwner, bool continuation, CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();

        var lease = await operations.TryAcquireLeaseAsync(request.OperationId, leaseOwner, now, now.AddMinutes(2), cancellationToken).ConfigureAwait(false);

        if (!lease.Acquired) return new Error(ErrorCodes.Guild.RevisionConflict, "The committed operation has another live owner.");

        GuildQuestDriver driver = new(execution, leaseOwner, lease.Operation.Revision, caller);

        var input = await quests.ReadLocalInputAsync(driver, cancellationToken).ConfigureAwait(false);

        if (input.IsFailure) return input.Error;

        var dispatch = await quests.PrepareDispatchAsync(driver, request.DispatchId, request.InvocationDigest,
            execution.RunId, continuation, cancellationToken).ConfigureAwait(false);

        if (dispatch.IsFailure) return dispatch.Error;

        return new LocalDispatchPrepared(input.Value, new GuildLocalInferenceExecution(quests, driver, dispatch.Value));
    }

    private async Task<Result<GuildDispatchObservation>> DispatchDirectCoreAsync(GuildExecutionHandle execution, GuildDispatchRequest request,
        Guid? claimed, CancellationToken cancellationToken)
    {
        var preflight = await ReadDispatchPreflightAsync(execution, request, cancellationToken).ConfigureAwait(false);

        if (preflight.IsFailure) return preflight.Error;

        if (preflight.Value.Immediate is { } immediate) return immediate;

        GuildLocalPortState before = preflight.Value.Before;

        GuildCallerContext caller = preflight.Value.Caller;

        bool continuation = preflight.Value.Continuation;

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
            var prepared = await PrepareOwnedDispatchAsync(execution, request, caller, leaseOwner, continuation, cancellationToken).ConfigureAwait(false);

            if (prepared.IsFailure) return prepared.Error;

            GuildLocalInferenceExecution invocation = prepared.Value.Invocation;

            using CancellationTokenSource runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            using CancellationTokenSource pulseCancellation = new();

            running = new(invocation, runCancellation, continuation);

            Task pulse = PulseAsync(running, pulseCancellation.Token);

            try
            {
                try
                {
                    Result<PromptTurnResult> result = await engine.ExecuteGuildAsync(before.Binding, prepared.Value.Input, invocation, inference,
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
            return await ParkAsync(running.Execution, request.DispatchId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            running?.Serial.Dispose();

            ownership.Release(request.OperationId, claim);
        }
    }

    private async Task<Result<GuildDispatchObservation>> DispatchScheduledCoreAsync(GuildExecutionHandle execution, GuildDispatchRequest request,
        Guid? claimed, IGrimoireWorkLease work, GuildLocalQuestScheduler scheduler, CancellationToken cancellationToken)
    {
        var preflight = await ReadDispatchPreflightAsync(execution, request, cancellationToken).ConfigureAwait(false);

        if (preflight.IsFailure) return preflight.Error;

        if (preflight.Value.Immediate is { } immediate) return immediate;

        GuildLocalPortState before = preflight.Value.Before;

        GuildCallerContext caller = preflight.Value.Caller;

        bool continuation = preflight.Value.Continuation;

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
            var prepared = await PrepareOwnedDispatchAsync(execution, request, caller, leaseOwner, continuation, cancellationToken).ConfigureAwait(false);

            if (prepared.IsFailure) return prepared.Error;

            GuildLocalInferenceExecution invocation = prepared.Value.Invocation;

            using CancellationTokenSource runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            using CancellationTokenSource pulseCancellation = new();

            running = new(invocation, runCancellation, continuation);

            Task pulse = PulseAsync(running, pulseCancellation.Token);

            try
            {
                try
                {
                    Result<PromptTurnResult> result = await engine.ExecuteGuildAsync(before.Binding, prepared.Value.Input, invocation, inference,
                        new EngineWait(this, running), runCancellation.Token).ConfigureAwait(false);

                    return await scheduler.RunLocalCompletionAdmittedAsync(work, this, running, before, request, result, cancellationToken).ConfigureAwait(false);
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
            return await ParkAsync(running.Execution, request.DispatchId, cancellationToken).ConfigureAwait(false);
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

    private async Task<Result<GuildDispatchObservation>> ParkAsync(GuildLocalInferenceExecution execution,
        Guid observedDispatch, CancellationToken drainCancellation, string? reasonCode = null)
    {
        // The engine has returned and its owned in-process work has joined. Keep that known
        // stop in this live scope while descendants drain; interruption never fabricates proof.
        DateTimeOffset renewAt = time.GetUtcNow();

        try
        {
            while (true)
            {
                var current = await quests.ReadLocalPortAsync(execution.Driver, execution.Dispatch.DispatchId, CancellationToken.None).ConfigureAwait(false);

                if (current.IsFailure)
                {
                    var cleanup = await AdoptStoppedLocalCleanupAsync(execution).ConfigureAwait(false);

                    if (cleanup.IsFailure) break;

                    execution = cleanup.Value;

                    continue;
                }

                if (!current.Value.Quest.Quest.CancellationRequested)
                {
                    if (drainCancellation.IsCancellationRequested || execution.Driver.Caller is not { } caller) break;

                    var frontier = await quests.ReadLocalCancellationFrontierAsync(caller, execution.Driver.Execution.QuestId,
                        drainCancellation).ConfigureAwait(false);

                    if (frontier.IsFailure || !frontier.Value.AncestorCanceled) break;

                    GuildCancelQuestRequest request = new(Guid.NewGuid(), frontier.Value.Quest.Quest.Revision,
                        GuildCancellationReason.OperatorRequested);

                    var intent = GuildQuestIntent.Compute(execution.Driver.Execution.QuestId, request);

                    if (intent.IsFailure) break;

                    var accepted = await quests.RequestCancellationAsync(caller, new(request.IdempotencyKey, intent.Value),
                        execution.Driver.Execution.QuestId, request, drainCancellation).ConfigureAwait(false);

                    if (accepted.IsFailure && accepted.Error.Code != ErrorCodes.Guild.RevisionConflict) break;

                    continue;
                }

                if (execution.Driver.Caller?.Kind != GuildCallerKind.HostReceipt)
                {
                    var cleanup = await AdoptStoppedLocalCleanupAsync(execution).ConfigureAwait(false);

                    if (cleanup.IsFailure) break;

                    execution = cleanup.Value;

                    continue;
                }

                DateTimeOffset now = time.GetUtcNow();

                bool renew = now >= renewAt && !drainCancellation.IsCancellationRequested;

                var fresh = await quests.RefreshLocalExecutionAsync(execution, now, renew, CancellationToken.None).ConfigureAwait(false);

                if (fresh.IsFailure) break;

                if (renew) renewAt = now.AddSeconds(30);

                var resolved = await quests.ResolveLocalDispatchAsync(execution.Driver, execution.Dispatch.DispatchId, CancellationToken.None).ConfigureAwait(false);

                if (resolved.IsFailure) break;

                var children = await quests.LocalChildrenStoppedAsync(execution.Driver, CancellationToken.None).ConfigureAwait(false);

                if (children.IsFailure) break;

                if (children.Value)
                {
                    current = await quests.ReadLocalPortAsync(execution.Driver, execution.Dispatch.DispatchId, CancellationToken.None).ConfigureAwait(false);

                    if (current.IsFailure) break;

                    Guid resultId = Guid.NewGuid();

                    string digest = Convert.ToHexString(SHA256.HashData([]));

                    GuildQuestResult canceled = new(new(execution.Driver.Execution.QuestId, resultId, digest, null,
                        QuestState.Canceled, LongRunningOperationRecoveryOutcomes.Cancelled), null,
                        NoDispatchProven: !current.Value.HadPhysicalDispatch, WorkStoppedProven: true);

                    var committed = await quests.CommitResultAsync(execution.Driver, new(resultId, digest),
                        current.Value.Quest.Quest.Revision, canceled, CancellationToken.None).ConfigureAwait(false);

                    if (committed.IsSuccess)
                        return await ObserveStoppedLocalAsync(execution.Driver, observedDispatch).ConfigureAwait(false);

                    if (committed.Error.Code != ErrorCodes.Guild.RevisionConflict) break;
                }

                if (drainCancellation.IsCancellationRequested) break;

                await Task.Delay(TimeSpan.FromMilliseconds(250), time, drainCancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested)
        {
        }

        var parked = await quests.ParkLocalExecutionAsync(execution.Driver, CancellationToken.None, reasonCode).ConfigureAwait(false);

        if (parked.IsFailure) return parked.Error;

        return await ObserveStoppedLocalAsync(execution.Driver, observedDispatch).ConfigureAwait(false);
    }

    private async Task<Result<GuildLocalInferenceExecution>> AdoptStoppedLocalCleanupAsync(GuildLocalInferenceExecution execution)
    {
        if (execution.Driver.Caller?.Kind == GuildCallerKind.HostReceipt)
            return new Error(ErrorCodes.Guild.Forbidden, "The stopped cleanup lease cannot acquire work authority.");

        var owner = await callers.CreateNativeOperatorAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);

        if (owner.IsFailure) return owner.Error;

        var cleanup = await quests.CreateLocalCleanupDriverAsync(owner.Value, execution.Driver.Execution.QuestId,
            CancellationToken.None, execution.Driver).ConfigureAwait(false);

        return cleanup.IsFailure ? cleanup.Error : new GuildLocalInferenceExecution(quests, cleanup.Value, execution.Dispatch);
    }

    private async Task<Result<GuildDispatchObservation>> ObserveStoppedLocalAsync(GuildQuestDriver driver, Guid dispatchId)
    {
        if (driver.Caller?.Kind != GuildCallerKind.HostReceipt)
            return await ObserveAsync(driver.Execution, driver.Execution.QuestId, dispatchId, CancellationToken.None).ConfigureAwait(false);

        var state = await quests.ReadLocalPortAsync(driver, dispatchId, CancellationToken.None).ConfigureAwait(false);

        return state.IsFailure ? state.Error : Observation(state.Value, dispatchId);
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

    internal sealed class LocalRun(GuildLocalInferenceExecution execution, CancellationTokenSource cancellation, bool restoring)
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
