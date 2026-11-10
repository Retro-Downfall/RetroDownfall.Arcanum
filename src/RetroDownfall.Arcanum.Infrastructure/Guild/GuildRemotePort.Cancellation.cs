using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildRemotePort
{
    public Task<Result<GuildDispatchObservation>> RequestCancellationAsync(GuildExecutionHandle execution,
        GuildMutationIdentity mutation, Guid questId, Guid dispatchId, CancellationToken ct) =>
        CancelCoreAsync(execution, mutation, questId, dispatchId, null, ct);

    internal Task<Result<GuildDispatchObservation>> RequestCancellationClaimedAsync(GuildExecutionHandle execution,
        GuildMutationIdentity mutation, Guid questId, Guid dispatchId, Guid claim, CancellationToken ct) =>
        CancelCoreAsync(execution, mutation, questId, dispatchId, claim, ct);

    internal async Task<Result<GuildDispatchObservation>> RequestCancellationOwnedAsync(GuildCallerContext owner,
        GuildMutationIdentity mutation, Guid questId, Guid dispatchId, Guid claim, CancellationToken ct)
    {
        var cleanup = await _quests.CreateRemoteCleanupDriverAsync(owner, questId, ct).ConfigureAwait(false);

        return cleanup.IsFailure ? cleanup.Error
            : await CancelCoreAsync(cleanup.Value.Execution, mutation, questId, dispatchId, claim, ct, cleanup.Value).ConfigureAwait(false);
    }

    private async Task<Result<GuildDispatchObservation>> CancelCoreAsync(GuildExecutionHandle execution,
        GuildMutationIdentity mutation, Guid questId, Guid dispatchId, Guid? claimed, CancellationToken ct, GuildQuestDriver? cleanup = null)
    {
        if (execution is null || execution.QuestId != questId) return Invalid;

        GuildQuestDriver driver;

        if (cleanup is not null) driver = cleanup;
        else
        {
            var caller = await callers.CreateRunCallerAsync(authority, execution, dispatchId, ct).ConfigureAwait(false);

            if (caller.IsFailure) return caller.Error;

            driver = new(execution, "observe", 0, caller.Value);
        }

        var valid = await _quests.ValidateLocalCancellationRequestAsync(driver, mutation, ct).ConfigureAwait(false);

        if (valid.IsFailure) return valid.Error;

        var before = await _quests.ReadRemotePortAsync(driver, dispatchId, ct).ConfigureAwait(false);

        if (before.IsFailure) return before.Error;

        if (before.Value.Quest.Quest.ResultReceiptId is not null) return Observation(before.Value, dispatchId);

        Guid claim;

        if (claimed is Guid supplied)
        {
            if (!ownership.IsClaimedBy(before.Value.Operation.Id, supplied)) return Invalid;

            claim = supplied;
        }
        else if (!ownership.TryClaim(before.Value.Operation.Id, out claim)) return Observation(before.Value, dispatchId);

        try
        {
            if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null) return Unavailable;

            await using IGrimoireWorkLease work = acquired;

            // A pending stop remains joined here; its exact physical observation owns the Effect group.
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

            var prepared = await _quests.PrepareRemoteCancellationAsync(driver, lifetime.Token).ConfigureAwait(false);

            if (prepared.IsFailure) return prepared.Error;

            dispatchId = prepared.Value;

            var selected = await _quests.ReadRemotePortAsync(driver, dispatchId, lifetime.Token).ConfigureAwait(false);

            if (selected.IsFailure) return selected.Error;

            before = selected;

            var lease = await operations.TryAcquireLeaseAsync(before.Value.Operation.Id, "guild-remote-" + claim.ToString("N"),
                time.GetUtcNow(), time.GetUtcNow().AddMinutes(2), lifetime.Token).ConfigureAwait(false);

            if (!lease.Acquired) return Observation(before.Value, dispatchId);

            driver = driver with { LeaseOwner = lease.Operation.LeaseOwner!, OperationRevision = lease.Operation.Revision };

            if (before.Value.Input is not { } input) return Invalid;

            using Run run = new(driver, dispatchId, input, lifetime, stopping: true);

            using CancellationTokenSource pulseStop = new();

            Task pulse = PulseAsync(run, pulseStop.Token);

            try
            {
                try
                {
                    var identity = await _quests.ReadRemoteTaskIdentityAsync(driver, lifetime.Token).ConfigureAwait(false);

                    if (identity.IsFailure)
                    {
                        if (before.Value.Dispatch is null || before.Value.Dispatch.Evidence == GuildDispatchEvidence.Prepared)
                        {
                            await run.Serial.WaitAsync(lifetime.Token).ConfigureAwait(false);

                            try
                            {
                                Guid resultId = Guid.NewGuid();

                                string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]));

                                var current = await _quests.ReadRemotePortAsync(run.Driver, dispatchId, lifetime.Token).ConfigureAwait(false);

                                if (current.IsFailure) return current.Error;

                                var committed = await _quests.CommitResultAsync(run.Driver, new(resultId, digest), current.Value.Quest.Quest.Revision,
                                    new(new(questId, resultId, digest, null, QuestState.Canceled, LongRunningOperationRecoveryOutcomes.Cancelled), null,
                                        NoDispatchProven: true, WorkStoppedProven: true), lifetime.Token).ConfigureAwait(false);

                                if (committed.IsFailure) return committed.Error;

                                var settled = await _quests.ReadRemotePortAsync(run.Driver, dispatchId, CancellationToken.None).ConfigureAwait(false);

                                return settled.IsFailure ? settled.Error : Observation(settled.Value, dispatchId);
                            }
                            finally { run.Serial.Release(); }
                        }

                        return await ParkAsync(driver, dispatchId, ErrorCodes.Guild.ReconciliationRequired).ConfigureAwait(false);
                    }

                    var result = await client.ObserveGuildTaskAsync(input.DiscoveryEndpoint, identity.Value.TaskId, input.Transport,
                        new Lifecycle(this, run, identity.Value.TaskId, true, identity.Value.ContextId), true, lifetime.Token, identity.Value.ContextId).ConfigureAwait(false);

                    var after = await _quests.ReadRemotePortAsync(run.Driver, dispatchId, CancellationToken.None).ConfigureAwait(false);

                    if (after.IsSuccess && after.Value.Quest.Quest.ResultReceiptId is not null) return Observation(after.Value, dispatchId);

                    return await ParkAsync(run.Driver, dispatchId, result.IsFailure ? result.Error.Code : ErrorCodes.Guild.ReconciliationRequired).ConfigureAwait(false);
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
        finally { ownership.Release(before.Value.Operation.Id, claim); }
    }
}
