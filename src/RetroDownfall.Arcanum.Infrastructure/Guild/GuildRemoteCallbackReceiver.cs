using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Authenticates a recorded callback and wakes its sole durable Quest for observation.</summary>
internal sealed class GuildRemoteCallbackReceiver(GuildQuestStore quests, GuildLocalHostPolicy policy,
    IGuildLocalQuestScheduler scheduler)
{
    internal async Task<Result<bool>> AcceptAsync(string configId, string? token, CancellationToken ct)
    {
        if (!A2ACallbackConfigId.IsWellFormed(configId)) return false;

        var binding = await quests.FindRemoteCallbackAsync(configId, token, ct).ConfigureAwait(false);

        if (binding.IsFailure) return binding.Error;

        if (binding.Value is null) return false;

        var caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        var receipt = await quests.AcceptRemoteCallbackAsync(caller.Value, configId, token, binding.Value, ct).ConfigureAwait(false);

        if (receipt.IsFailure) return receipt.Error;

        if (receipt.Value.State is not (QuestState.Completed or QuestState.Failed or QuestState.Canceled or QuestState.Rejected)
            && scheduler.IsAvailable) scheduler.Schedule(receipt.Value);

        return true;
    }
}
