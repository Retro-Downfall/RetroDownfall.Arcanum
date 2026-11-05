using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Physical mutation admission. Public lease selectors cannot authorize writes; a permit remains held through actual effect completion.</summary>
public interface IGuildWorkspaceLeaseService
{
    Task<Result<WorkspaceLeaseDto>> AcquireAsync(GuildCallerContext caller, GuildMutationIdentity mutation, AcquireWorkspaceLeaseRequest request, CancellationToken cancellationToken);

    Task<Result<WorkspaceLeaseDto>> RenewAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid leaseId, RenewWorkspaceLeaseRequest request, CancellationToken cancellationToken);

    Task<Result<WorkspaceLeaseDto>> ReleaseAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid leaseId, ReleaseWorkspaceLeaseRequest request, CancellationToken cancellationToken);

    Task<Result<WorkspaceLeasePageDto>> ListAsync(GuildCallerContext caller, Guid? workspaceId, Guid? commissionId, GuildWorkspaceLeaseState? state, GuildPageQuery query, CancellationToken cancellationToken);

    Task<Result<GuildMutationPermit>> AdmitMutationAsync(GuildVerifiedAuthority authority, GuildMutationPlan plan, CancellationToken cancellationToken);

    Task<Result> ValidateCommitAsync(GuildMutationPermit permit, CancellationToken cancellationToken);

    Task<Result> CompleteMutationAsync(GuildMutationPermit permit, CancellationToken cancellationToken);
}
