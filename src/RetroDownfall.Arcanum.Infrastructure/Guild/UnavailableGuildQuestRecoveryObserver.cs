using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Explicit production refusal while no qualified local or transport recovery adapter is installed.</summary>
internal sealed class UnavailableGuildQuestRecoveryObserver : IGuildQuestRecoveryObserver
{
    public bool CanObserve(GuildQuestRecoveryTarget target) => false;

    public Task<Result> ObserveAsync(GuildQuestRecoveryTarget target, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(new(ErrorCodes.Guild.Unavailable,
            "No qualified Guild recovery observer is installed for this effect.")));
}
