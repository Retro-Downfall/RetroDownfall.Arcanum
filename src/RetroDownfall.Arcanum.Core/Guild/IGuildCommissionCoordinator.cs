using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>The sole Commission creation path. It verifies caller intent and commits admission before scheduling existing work.</summary>
public interface IGuildCommissionCoordinator
{
    Task<Result<GuildCommissionDto>> IssueAsync(GuildCallerContext caller, GuildMutationIdentity mutation, GuildIssueRootRequest request, CancellationToken cancellationToken);

    Task<Result<GuildCommissionDto>> IssueAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid parentCommissionId, GuildIssueChildRequest request, CancellationToken cancellationToken);

    Task<Result<GuildCommissionDto>> ContinueAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid commissionId, GuildContinueRequest request, CancellationToken cancellationToken);

    Task<Result<GuildCancellationDto>> RequestCancellationAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid commissionId, GuildCancelCommissionRequest request, CancellationToken cancellationToken);
}
