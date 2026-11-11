using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildLocalPort
{
    public async Task<Result<GuildDispatchObservation>> ContinueAsync(GuildExecutionHandle execution,
        GuildDispatchRequest dispatch, GuildContinueRequest request, CancellationToken cancellationToken)
    {
        if (execution is null || dispatch is null || request is null || execution.QuestId != dispatch.QuestId
            || execution.RunId != dispatch.RunId || dispatch.DispatchId == dispatch.QuestId || dispatch.ExpectedOperationRevision < 0)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The continuation does not identify committed local work.");

        var caller = await callers.CreateRunCallerAsync(authority, execution, dispatch.DispatchId, cancellationToken).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        GuildQuestDriver driver = new(execution, "observe", 0, caller.Value);

        Result accepted = await quests.ValidateLocalContinuationRequestAsync(driver, dispatch, request, cancellationToken).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        var state = await quests.ReadLocalPortAsync(driver, dispatch.DispatchId, cancellationToken).ConfigureAwait(false);

        if (state.IsFailure) return state.Error;

        if (state.Value.Operation.Id != dispatch.OperationId)
            return new Error(ErrorCodes.Guild.IdempotencyConflict, "The continuation differs from its committed operation.");

        if (ownership.IsClaimed(dispatch.OperationId)) return Observation(state.Value, dispatch.DispatchId);

        return await DispatchAsync(execution, dispatch, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<GuildDispatchObservation>> RequestCancellationAsync(GuildExecutionHandle execution,
        GuildMutationIdentity mutation, Guid questId, Guid dispatchId, CancellationToken cancellationToken) =>
        RequestCancellationCoreAsync(execution, mutation, questId, dispatchId, null, cancellationToken);

    internal Task<Result<GuildDispatchObservation>> RequestCancellationClaimedAsync(GuildExecutionHandle execution,
        GuildMutationIdentity mutation, Guid questId, Guid dispatchId, Guid claim, CancellationToken cancellationToken) =>
        RequestCancellationCoreAsync(execution, mutation, questId, dispatchId, claim, cancellationToken);

    internal async Task<Result<GuildDispatchObservation>> RequestCancellationOwnedAsync(GuildCallerContext owner,
        GuildMutationIdentity mutation, Guid questId, Guid dispatchId, Guid claim, CancellationToken cancellationToken)
    {
        var cleanup = await quests.CreateLocalCleanupDriverAsync(owner, questId, cancellationToken).ConfigureAwait(false);

        if (cleanup.IsFailure) return cleanup.Error;

        var identity = await quests.ReadLocalCleanupStopIdentityAsync(cleanup.Value, cancellationToken).ConfigureAwait(false);

        return identity.IsFailure ? identity.Error
            : await RequestCancellationCoreAsync(cleanup.Value.Execution, identity.Value, questId, dispatchId, claim,
                cancellationToken, cleanup.Value).ConfigureAwait(false);
    }

    private async Task<Result<GuildDispatchObservation>> RequestCancellationCoreAsync(GuildExecutionHandle execution,
        GuildMutationIdentity mutation, Guid questId, Guid dispatchId, Guid? claimed, CancellationToken cancellationToken,
        GuildQuestDriver? cleanup = null)
    {
        if (execution is null || execution.QuestId != questId || dispatchId == Guid.Empty)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The cancellation does not identify committed local work.");

        GuildQuestDriver driver;

        if (cleanup is not null) driver = cleanup;
        else
        {
            var caller = await callers.CreateRunCallerAsync(authority, execution, dispatchId, cancellationToken).ConfigureAwait(false);

            if (caller.IsFailure) return caller.Error;

            driver = new(execution, "observe", 0, caller.Value);
        }

        Result accepted;

        if (cleanup is not null)
        {
            var identity = await quests.ReadLocalCleanupStopIdentityAsync(cleanup, cancellationToken).ConfigureAwait(false);

            accepted = identity.IsFailure ? Result.Failure(identity.Error)
                : identity.Value == mutation ? Result.Success() : Result.Failure(new(ErrorCodes.Guild.IdempotencyConflict,
                    "The native cleanup stop identity does not bind this Quest."));
        }
        else accepted = await quests.ValidateLocalCancellationRequestAsync(driver, mutation, cancellationToken).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        var state = await quests.ReadLocalPortAsync(driver, dispatchId, cancellationToken).ConfigureAwait(false);

        if (state.IsFailure) return state.Error;

        GuildLocalPortState before = state.Value;

        bool stoppedWaitRequired = before.HadPhysicalDispatch
            || before.Operation.State == LongRunningOperationState.ReconciliationRequired
                && before.Operation.TerminalErrorCode == ErrorCodes.Guild.AwaitingInput;

        if (before.Dispatch is null && dispatchId != questId)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The cancellation dispatch is not committed.");

        if (before.Quest.Quest.ResultReceiptId is not null)
            return Observation(before, dispatchId);

        var frontier = await quests.LocalChildrenStoppedAsync(driver, cancellationToken).ConfigureAwait(false);

        if (frontier.IsFailure) return frontier.Error;

        if (!frontier.Value) return Observation(before, dispatchId);

        Guid claim;

        if (claimed is Guid existing)
        {
            if (!ownership.IsClaimedBy(before.Operation.Id, existing))
                return new Error(ErrorCodes.Guild.AuthorityMismatch, "The scheduler does not own the cancellation drain.");

            claim = existing;
        }
        else if (!ownership.TryClaim(before.Operation.Id, out claim)) return Observation(before, dispatchId);

        try
        {
            // A live owner drains through its existing engine. An unstarted local Quest can be
            // settled without entering the engine or requiring new execution admission.
            DateTimeOffset now = time.GetUtcNow();

            if (before.Operation.LeaseOwner is not null && before.Operation.LeaseExpiresAt > now)
                return Observation(before, dispatchId);

            if (stoppedWaitRequired)
            {
                var stoppedWait = await quests.PrepareStoppedLocalWaitCancellationAsync(driver, false, cancellationToken).ConfigureAwait(false);

                if (stoppedWait.IsFailure) return stoppedWait.Error;

                if (!stoppedWait.Value) return Observation(before, dispatchId);
            }

            string leaseOwner = "guild-cancel-" + claim.ToString("N");

            var lease = await operations.TryAcquireLeaseAsync(before.Operation.Id, leaseOwner, now,
                now.AddMinutes(2), cancellationToken).ConfigureAwait(false);

            if (!lease.Acquired) return Observation(before, dispatchId);

            driver = driver with { LeaseOwner = leaseOwner, OperationRevision = lease.Operation.Revision };

            if (before.Dispatch is not null)
            {
                var resolved = await quests.ResolveLocalDispatchAsync(driver, dispatchId, cancellationToken).ConfigureAwait(false);

                if (resolved.IsFailure) return resolved.Error;
            }

            state = await quests.ReadLocalPortAsync(driver, dispatchId, cancellationToken).ConfigureAwait(false);

            if (state.IsFailure) return state.Error;

            if (state.Value.HadPhysicalDispatch || stoppedWaitRequired)
            {
                var stoppedWait = await quests.PrepareStoppedLocalWaitCancellationAsync(driver, true, cancellationToken).ConfigureAwait(false);

                if (stoppedWait.IsFailure) return stoppedWait.Error;

                if (!stoppedWait.Value) return Observation(state.Value, dispatchId);
            }

            Guid resultId = Guid.NewGuid();

            string digest = Convert.ToHexString(SHA256.HashData([]));

            GuildQuestResult result = new(new(questId, resultId, digest, null, QuestState.Canceled,
                LongRunningOperationRecoveryOutcomes.Cancelled), null,
                NoDispatchProven: !state.Value.HadPhysicalDispatch, WorkStoppedProven: true);

            var committed = await quests.CommitResultAsync(driver, new(resultId, digest), state.Value.Quest.Quest.Revision,
                result, cancellationToken).ConfigureAwait(false);

            if (committed.IsFailure) return committed.Error;

            if (cleanup is null) return await ObserveAsync(execution, questId, dispatchId, cancellationToken).ConfigureAwait(false);

            state = await quests.ReadLocalPortAsync(driver, dispatchId, cancellationToken).ConfigureAwait(false);

            return state.IsFailure ? state.Error : Observation(state.Value, dispatchId);
        }
        finally
        {
            ownership.Release(before.Operation.Id, claim);
        }
    }
}
