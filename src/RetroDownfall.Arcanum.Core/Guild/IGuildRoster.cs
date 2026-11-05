using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Resolves exact visible Card revisions; discovery observations never confer grants.</summary>
public interface IGuildRoster
{
    Task<Result<GuildAgentCard>> ResolveAsync(GuildCallerContext caller, Guid agentId, Guid? cardId, long? revision, CancellationToken cancellationToken);

    Task<Result<GuildAgentCardPageDto>> ListAsync(GuildCallerContext caller, GuildListAgentsQuery query, CancellationToken cancellationToken);

    Task<Result<GuildRefreshReceipt>> RefreshAsync(GuildCallerContext caller, GuildMutationIdentity mutation, GuildRefreshAgentsRequest request, CancellationToken cancellationToken);
}
