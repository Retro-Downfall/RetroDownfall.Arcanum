using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Atomic domain/evidence operations over the existing LRO transaction lane; no independently writable execution state.</summary>
public interface ICommissionStore
{
    Task<Result<GuildAdmissionReceipt>> AdmitAsync(GuildAdmissionContext admission, GuildAdmissionRequest request, CancellationToken cancellationToken);

    Task<Result<GuildResultReceipt>> CommitResultAsync(GuildVerifiedAuthority authority, GuildCommitResultRequest request, CancellationToken cancellationToken);

    Task<Result<GuildResultApplicationReceipt>> ApplyResultAsync(GuildVerifiedAuthority authority, GuildResultApplicationRequest request, CancellationToken cancellationToken);

    Task<Result<GuildCommissionDto>> GetAsync(GuildCallerContext caller, Guid commissionId, CancellationToken cancellationToken);

    Task<Result<GuildCommissionPageDto>> ListAsync(GuildCallerContext caller, GuildListCommissionsQuery query, CancellationToken cancellationToken);

    Task<Result<GuildCommissionPageDto>> GetTreeAsync(GuildCallerContext caller, Guid commissionId, GuildPageQuery query, CancellationToken cancellationToken);
}
