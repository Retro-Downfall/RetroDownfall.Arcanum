using System.Security.Cryptography;
using System.Text;
using A2A;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Drives only committed remote work. The Quest ledger owns every dispatch, wait and outcome.</summary>
internal sealed partial class GuildRemotePort(GuildQuestStore quests, GuildLocalHostPolicy callers, SignetAuthority authority,
    ILongRunningOperationStore operations, A2AClientService client, IGrimoireConnectionAdmissionGate gate,
    LongRunningOperationOwnership ownership, TimeProvider time) : IGuildPort
{
    private readonly GuildQuestStore _quests = quests;

    public Task<Result<GuildDispatchObservation>> DispatchAsync(GuildExecutionHandle execution, GuildDispatchRequest request,
        CancellationToken ct) => DispatchCoreAsync(execution, request, null, ct);

    internal Task<Result<GuildDispatchObservation>> DispatchClaimedAsync(GuildExecutionHandle execution,
        GuildDispatchRequest request, Guid claim, CancellationToken ct) => DispatchCoreAsync(execution, request, claim, ct);

    private async Task<Result<GuildDispatchObservation>> DispatchCoreAsync(GuildExecutionHandle execution,
        GuildDispatchRequest request, Guid? claimed, CancellationToken ct)
    {
        if (execution is null || request is null || request.QuestId != execution.QuestId || request.RunId != execution.RunId
            || request.DispatchId == Guid.Empty) return Invalid;

        var caller = await callers.CreateRunCallerAsync(authority, execution, request.DispatchId, ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        GuildQuestDriver driver = new(execution, "observe", 0, caller.Value);

        var state = await _quests.ReadRemotePortAsync(driver, request.DispatchId, ct).ConfigureAwait(false);

        if (state.IsFailure) return state.Error;

        if (state.Value.Quest.Quest.ResultReceiptId is not null) return Observation(state.Value, request.DispatchId);

        if (state.Value.Input is not { } input || state.Value.Operation.Id != request.OperationId) return Invalid;

        bool continuation = request.DispatchId != execution.QuestId;

        if (!continuation && (request.Mutation?.IdempotencyKey != state.Value.Quest.MutationId
            || request.Mutation.CanonicalRequestDigest != state.Value.Quest.IntentDigest
            || request.InvocationDigest != state.Value.Quest.Quest.GoalRef.Digest)) return Conflict;

        if (continuation)
        {
            var accepted = await _quests.ReadLocalContinuationAsync(driver, request.DispatchId, ct, false).ConfigureAwait(false);

            if (accepted.IsFailure) return accepted.Error;

            if (accepted.Value is not { } answer || answer.Mutation != request.Mutation
                || answer.Continuation.ContentRef.Digest != request.InvocationDigest) return Conflict;
        }

        if (state.Value.Dispatch is { Evidence: GuildDispatchEvidence.PossiblyDispatched, RemoteTaskId: null })
            return await ParkAsync(driver, request.DispatchId, ErrorCodes.Guild.ReconciliationRequired).ConfigureAwait(false);

        var invocation = await _quests.ReadRemoteInvocationAsync(driver, request.DispatchId, ct).ConfigureAwait(false);

        if (invocation.IsFailure) return invocation.Error;

        input = invocation.Value;

        Guid claim;

        if (claimed is Guid supplied)
        {
            if (!ownership.IsClaimedBy(request.OperationId, supplied)) return Invalid;

            claim = supplied;
        }
        else if (!ownership.TryClaim(request.OperationId, out claim)) return Observation(state.Value, request.DispatchId);

        try
        {
            if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null)
                return Unavailable;

            await using IGrimoireWorkLease work = acquired;

            // Coordination retains Work; every physical transport handoff owns its own Effect group.
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

            string leaseOwner = "guild-remote-" + claim.ToString("N");

            var lease = await operations.TryAcquireLeaseAsync(request.OperationId, leaseOwner, time.GetUtcNow(),
                time.GetUtcNow().AddMinutes(2), lifetime.Token).ConfigureAwait(false);

            if (!lease.Acquired) return Observation(state.Value, request.DispatchId);

            driver = new(execution, leaseOwner, lease.Operation.Revision, caller.Value);

            var prepared = await _quests.PrepareDispatchAsync(driver, request.DispatchId, request.InvocationDigest,
                execution.RunId, continuation, lifetime.Token).ConfigureAwait(false);

            if (prepared.IsFailure) return prepared.Error;

            using Run run = new(driver, request.DispatchId, input, lifetime);

            using CancellationTokenSource pulseStop = new();

            Task pulse = PulseAsync(run, pulseStop.Token);

            try
            {
                try
                {
                    var bound = await _quests.ReadRemotePortAsync(run.Driver, request.DispatchId, lifetime.Token).ConfigureAwait(false);

                    if (bound.IsFailure) return bound.Error;

                    bool observe = prepared.Value.Evidence != GuildDispatchEvidence.Prepared;

                    string? taskId = prepared.Value.RemoteTaskId;
                    string? contextId = prepared.Value.RemoteContextId;

                    if (continuation || observe)
                    {
                        var remote = await _quests.ReadRemoteTaskIdentityAsync(run.Driver, lifetime.Token).ConfigureAwait(false);

                        if (remote.IsFailure) return remote.Error;

                        taskId = remote.Value.TaskId;
                        contextId = remote.Value.ContextId;
                    }

                    Lifecycle lifecycle = new(this, run, taskId, observe, contextId);

                    Result<A2ADispatchResult> transported;

                    if (observe)
                    {
                        transported = await client.ObserveGuildTaskAsync(input.DiscoveryEndpoint, taskId!, input.Transport,
                            lifecycle, bound.Value.Quest.Quest.CancellationRequested, lifetime.Token, contextId).ConfigureAwait(false);
                    }
                    else
                    {
                        var body = await _quests.ReadRemoteBodyAsync(run.Driver, request.DispatchId, lifetime.Token).ConfigureAwait(false);

                        if (body.IsFailure) return body.Error;

                        transported = await client.SendGuildAsync(new(input.DiscoveryEndpoint, body.Value, request.DispatchId.ToString("N"),
                            bound.Value.Quest.Quest.ResolvedGrant.Budget, continuation ? taskId : null, continuation ? contextId : null,
                            input.Mode, new(input.AcceptedOutputModes, input.SkillId), input.DelegationChain, input.Transport), lifecycle,
                            lifetime.Token).ConfigureAwait(false);
                    }

                    if (transported.IsFailure && transported.Error.Code == ErrorCodes.Sending.TaskRejected)
                    {
                        var failed = await _quests.ReadRemotePortAsync(run.Driver, request.DispatchId, CancellationToken.None).ConfigureAwait(false);

                        if (failed.IsSuccess && failed.Value.Quest.Quest.ResultReceiptId is null
                            && failed.Value.StopReasonCode == ErrorCodes.Sending.TaskRejected)
                            transported = await StopRejectedRemoteAsync(run, lifetime.Token).ConfigureAwait(false);
                    }

                    var after = await _quests.ReadRemotePortAsync(run.Driver, request.DispatchId, CancellationToken.None).ConfigureAwait(false);

                    if (after.IsSuccess && (after.Value.Quest.Quest.ResultReceiptId is not null
                        || after.Value.Quest.Quest.State == QuestState.InputRequired)) return Observation(after.Value, request.DispatchId);

                    return await ParkAsync(run.Driver, request.DispatchId, transported.IsFailure
                        ? transported.Error.Code : ErrorCodes.Guild.ReconciliationRequired).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return await ParkAsync(run.Driver, request.DispatchId, ErrorCodes.Guild.ReconciliationRequired).ConfigureAwait(false);
                }
                finally
                {
                    await pulseStop.CancelAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await pulse.ConfigureAwait(false);
            }
        }
        finally
        {
            ownership.Release(request.OperationId, claim);
        }
    }

    public async Task<Result<GuildDispatchObservation>> ContinueAsync(GuildExecutionHandle execution, GuildDispatchRequest dispatch,
        GuildContinueRequest request, CancellationToken ct)
    {
        var caller = await callers.CreateRunCallerAsync(authority, execution, dispatch.DispatchId, ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        var valid = await _quests.ValidateLocalContinuationRequestAsync(new(execution, "observe", 0, caller.Value), dispatch, request, ct).ConfigureAwait(false);

        return valid.IsFailure ? valid.Error : await DispatchAsync(execution, dispatch, ct).ConfigureAwait(false);
    }

    public async Task<Result<GuildDispatchObservation>> ObserveAsync(GuildExecutionHandle execution, Guid questId, Guid dispatchId, CancellationToken ct)
    {
        if (execution is null || execution.QuestId != questId || dispatchId == Guid.Empty) return Invalid;

        var caller = await callers.CreateRunCallerAsync(authority, execution, dispatchId, ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        var state = await _quests.ReadRemotePortAsync(new(execution, "observe", 0, caller.Value), dispatchId, ct).ConfigureAwait(false);

        return state.IsFailure ? state.Error : Observation(state.Value, dispatchId);
    }

    private async Task<Result<GuildDispatchObservation>> ParkAsync(GuildQuestDriver driver, Guid dispatchId, string reason)
    {
        var parked = await _quests.ParkRemoteAsync(driver, reason, CancellationToken.None).ConfigureAwait(false);

        if (parked.IsFailure) return parked.Error;

        var state = await _quests.ReadRemotePortAsync(driver, dispatchId, CancellationToken.None).ConfigureAwait(false);

        return state.IsFailure ? state.Error : Observation(state.Value, dispatchId);
    }

    private static GuildDispatchObservation Observation(GuildRemotePortState state, Guid dispatchId) =>
        new(state.Quest.Quest.Id, dispatchId, state.Dispatch?.Evidence ?? GuildDispatchEvidence.Prepared, state.Quest.Quest.State,
            state.Quest.Quest.ReconciliationRequired, state.Operation.TerminalErrorCode, null,
            state.Dispatch?.RemoteTaskId, state.Dispatch?.RemoteContextId);

    private async Task PulseAsync(Run run, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), time, ct).ConfigureAwait(false);

                await run.Serial.WaitAsync(ct).ConfigureAwait(false);

                try
                {
                    var fresh = await _quests.RefreshRemoteDriverAsync(run.Driver, true, ct).ConfigureAwait(false);

                    if (fresh.IsFailure)
                    {
                        await run.Lifetime.CancelAsync().ConfigureAwait(false);

                        return;
                    }

                    run.Driver = fresh.Value;

                    var current = await _quests.ReadRemotePortAsync(run.Driver, run.DispatchId, ct).ConfigureAwait(false);

                    if (current.IsFailure || !run.Stopping && (current.Value.Quest.Quest.CancellationRequested || !callers.IsExecutionEnabled))
                    {
                        await run.Lifetime.CancelAsync().ConfigureAwait(false);

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
            await run.Lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<Result> CompleteAsync(Run run, A2AGuildTaskObservation observation)
    {
        GuildRemoteStateProjection projection = GuildRemoteStateProjection.Observe(observation.State);

        if (projection.ReconciliationRequired) return new Error(ErrorCodes.Guild.UnsupportedTransportState, "The peer reported an unsupported state.");

        if (projection.State is QuestState.Submitted or QuestState.Working) return Result.Success();

        if (projection.State == QuestState.InputRequired)
        {
            if (run.Driver.Caller?.Kind == GuildCallerKind.HostReceipt)
                return new Error(ErrorCodes.Guild.ReconciliationRequired, "The peer has not confirmed stopped work.");

            if (run.Input.Mode == A2ADispatchMode.Continuable)
                return await _quests.WaitForRemoteInputAsync(run.Driver, run.DispatchId, observation, CancellationToken.None).ConfigureAwait(false);

            var stopped = await _quests.RecordRemoteProtocolStopAsync(run.Driver, run.DispatchId, observation, CancellationToken.None).ConfigureAwait(false);

            return stopped.IsFailure ? stopped.Error : new Error(ErrorCodes.Sending.TaskRejected,
                "The peer requires input that the accepted Sending mode cannot supply.");
        }

        var state = await _quests.ReadRemotePortAsync(run.Driver, run.DispatchId, CancellationToken.None).ConfigureAwait(false);

        if (state.IsFailure) return state.Error;

        QuestState outcome = projection.State;

        if (outcome == QuestState.Rejected || outcome == QuestState.Canceled && !state.Value.Quest.Quest.CancellationRequested)
            outcome = QuestState.Failed;

        bool cleanup = run.Driver.Caller?.Kind == GuildCallerKind.HostReceipt;

        if (state.Value.StopReasonCode == ErrorCodes.Sending.TaskRejected) outcome = QuestState.Failed;
        else if (cleanup) outcome = QuestState.Canceled;

        byte[] body = cleanup ? [] : Encoding.UTF8.GetBytes(observation.ResponseText);

        byte[] digest = SHA256.HashData(body);

        byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes($"Guild.Remote.Result:{run.Driver.Execution.QuestId:D}:{outcome}:{Convert.ToHexString(digest)}"));

        Guid resultId = new(identity.AsSpan(0, 16));

        Guid contentId = new(SHA256.HashData(identity).AsSpan(0, 16));

        GuildDataRef content = new(contentId, GuildContentKind.QuestResult, 1, Convert.ToHexString(digest), "text/plain", body.Length, false);

        string? reason = outcome == QuestState.Canceled ? LongRunningOperationRecoveryOutcomes.Cancelled
            : outcome == QuestState.Failed ? ErrorCodes.Sending.TaskRejected : null;

        var result = await _quests.CommitResultAsync(run.Driver, new(resultId, content.Digest), state.Value.Quest.Quest.Revision,
            new(new(run.Driver.Execution.QuestId, resultId, content.Digest, cleanup ? null : content, outcome, reason), cleanup ? null : body,
                WorkStoppedProven: true, RemoteEvidence: new(run.DispatchId, observation)), CancellationToken.None).ConfigureAwait(false);

        return result.IsFailure ? result.Error : Result.Success();
    }

    private sealed class Run(GuildQuestDriver driver, Guid dispatchId, GuildRemoteExecutionInput input,
        CancellationTokenSource lifetime, bool stopping = false) : IDisposable
    {
        internal GuildQuestDriver Driver = driver;

        internal bool Stopping = stopping;

        internal Guid DispatchId { get; } = dispatchId;

        internal GuildRemoteExecutionInput Input { get; } = input;

        internal CancellationTokenSource Lifetime { get; } = lifetime;

        internal SemaphoreSlim Serial { get; } = new(1, 1);

        public void Dispose() => Serial.Dispose();
    }

    private sealed class Lifecycle(GuildRemotePort owner, Run run, string? expectedTask, bool observationOnly, string? expectedContext = null) : IA2AGuildSendingLifecycle
    {
        public async Task<Result> PreflightAsync(CancellationToken ct)
        {
            await run.Serial.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var admitted = await owner._quests.RemotePreflightAsync(run.Driver, !observationOnly, ct).ConfigureAwait(false);

                return admitted.IsFailure ? admitted.Error : Result.Success();
            }
            finally { run.Serial.Release(); }
        }

        public async Task<Result<SendMessageResponse>> AdmitAndSendAsync(Func<CancellationToken, Task<SendMessageResponse>> send, CancellationToken ct)
        {
            await run.Serial.WaitAsync(ct).ConfigureAwait(false);

            bool handedOff = false;

            try
            {
                return await owner._quests.SendRemoteEffectAsync(run.Driver, run.DispatchId, token =>
                {
                    handedOff = true;

                    run.Serial.Release();

                    return send(token);
                }, ct).ConfigureAwait(false);
            }
            finally { if (!handedOff) run.Serial.Release(); }
        }

        public async Task<Result<T>> AdmitAuxiliaryAsync<T>(Func<CancellationToken, Task<T>> action, bool stopping, CancellationToken ct)
        {
            await run.Serial.WaitAsync(ct).ConfigureAwait(false);

            bool handedOff = false;

            try
            {
                return await owner._quests.AdmitRemoteObservationAsync(run.Driver, token =>
                {
                    handedOff = true;

                    run.Serial.Release();

                    return action(token);
                }, stopping, ct).ConfigureAwait(false);
            }
            finally { if (!handedOff) run.Serial.Release(); }
        }

        public async Task<Result> AcceptedAsync(string? taskId, string? contextId, CancellationToken ct)
        {
            await run.Serial.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                if (expectedTask is not null && expectedTask != taskId || expectedContext is not null && expectedContext != contextId) return Conflict;

                if (taskId is null) return Result.Success();

                var state = await owner._quests.ReadRemotePortAsync(run.Driver, run.DispatchId, ct).ConfigureAwait(false);

                if (state.IsFailure || state.Value.Dispatch is not { } dispatch) return state.IsFailure ? state.Error : Invalid;

                var recorded = await owner._quests.RecordDispatchEvidenceAsync(run.Driver,
                    new(run.DispatchId, dispatch.Revision, GuildDispatchEvidence.Confirmed, RemoteTaskId: taskId, RemoteContextId: contextId), ct).ConfigureAwait(false);

                return recorded.IsFailure ? recorded.Error : Result.Success();
            }
            finally { run.Serial.Release(); }
        }

        public async Task<Result> CallbackAsync(string configId, string tokenHash, CancellationToken ct)
        {
            await run.Serial.WaitAsync(ct).ConfigureAwait(false);

            try { return await owner._quests.RecordRemoteCallbackAsync(run.Driver, configId, tokenHash, ct, run.DispatchId).ConfigureAwait(false); }
            finally { run.Serial.Release(); }
        }

        public async Task<Result> ObservedAsync(A2AGuildTaskObservation observation, CancellationToken ct)
        {
            await run.Serial.WaitAsync(ct).ConfigureAwait(false);

            try { return await owner.CompleteAsync(run, observation).ConfigureAwait(false); }
            finally { run.Serial.Release(); }
        }
    }

    private static readonly Error Invalid = new(ErrorCodes.Guild.InvalidRequest, "The request does not bind committed remote work.");

    private static readonly Error Conflict = new(ErrorCodes.Guild.IdempotencyConflict, "The remote request differs from its accepted identity.");

    private static readonly Error Unavailable = new(ErrorCodes.Guild.Unavailable, "Qualified remote Guild execution is unavailable.");
}
