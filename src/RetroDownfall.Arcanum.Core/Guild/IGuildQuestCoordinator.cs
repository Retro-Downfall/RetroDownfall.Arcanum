using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>The sole Quest creation path. It verifies caller intent and commits admission before scheduling existing work.</summary>
public interface IGuildQuestCoordinator
{
    Task<Result<GuildQuestDto>> IssueAsync(GuildCallerContext caller, GuildMutationIdentity mutation, GuildIssueRootRequest request, CancellationToken cancellationToken);

    Task<Result<GuildQuestDto>> IssueAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid parentQuestId, GuildIssueChildRequest request, CancellationToken cancellationToken);

    Task<Result<GuildQuestDto>> ContinueAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid questId, GuildContinueRequest request, CancellationToken cancellationToken);

    Task<Result<GuildCancellationDto>> RequestCancellationAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid questId, GuildCancelQuestRequest request, CancellationToken cancellationToken);
}
