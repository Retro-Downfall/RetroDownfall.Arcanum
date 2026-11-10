using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildRemotePort
{
    private async Task<Result<A2ADispatchResult>> StopRejectedRemoteAsync(Run run, CancellationToken ct)
    {
        await run.Serial.WaitAsync(ct).ConfigureAwait(false);

        GuildRemoteTaskIdentity identity;

        try
        {
            var current = await _quests.ReadRemotePortAsync(run.Driver, run.DispatchId, ct).ConfigureAwait(false);

            if (current.IsFailure) return current.Error;

            if (current.Value.StopReasonCode != ErrorCodes.Sending.TaskRejected) return Conflict;

            var caller = await callers.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

            if (caller.IsFailure) return caller.Error;

            if (!current.Value.Quest.Quest.CancellationRequested)
            {
                byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes($"Guild.Remote.Protocol.Stop:{run.Driver.Execution.QuestId:D}:{run.DispatchId:D}"));

                GuildCancelQuestRequest request = new(new Guid(key.AsSpan(0, 16)), current.Value.Quest.Quest.Revision,
                    GuildCancellationReason.OperatorRequested);

                var intent = GuildQuestIntent.Compute(run.Driver.Execution.QuestId, request);

                if (intent.IsFailure) return intent.Error;

                var cancelled = await _quests.RequestCancellationAsync(caller.Value, new(request.IdempotencyKey, intent.Value),
                    run.Driver.Execution.QuestId, request, ct).ConfigureAwait(false);

                if (cancelled.IsFailure) return cancelled.Error;
            }

            run.Stopping = true;

            var refreshed = await _quests.RefreshRemoteDriverAsync(run.Driver, false, ct).ConfigureAwait(false);

            if (refreshed.IsFailure) return refreshed.Error;

            run.Driver = refreshed.Value;

            var known = await _quests.ReadRemoteTaskIdentityAsync(run.Driver, ct).ConfigureAwait(false);

            if (known.IsFailure) return known.Error;

            identity = known.Value;
        }
        finally { run.Serial.Release(); }

        return await client.ObserveGuildTaskAsync(run.Input.DiscoveryEndpoint, identity.TaskId, run.Input.Transport,
            new Lifecycle(this, run, identity.TaskId, true, identity.ContextId), true, ct, identity.ContextId).ConfigureAwait(false);
    }
}
