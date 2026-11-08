using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Schedules only committed identities. Durable start intent remains the source of pending work.</summary>
internal interface IGuildLocalQuestScheduler
{
    bool IsAvailable { get; }

    void Schedule(GuildQuestDto quest);
}
