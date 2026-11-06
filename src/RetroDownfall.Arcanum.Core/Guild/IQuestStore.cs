using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Atomic domain/evidence operations over the existing LRO transaction lane; no independently writable execution state.</summary>
public interface IQuestStore
{
    Task<Result<GuildAdmissionReceipt>> AdmitAsync(GuildAdmissionContext admission, GuildAdmissionRequest request, CancellationToken cancellationToken);

    Task<Result<GuildResultReceipt>> CommitResultAsync(GuildVerifiedAuthority authority, GuildCommitResultRequest request, CancellationToken cancellationToken);

    Task<Result<GuildResultApplicationReceipt>> ApplyResultAsync(GuildVerifiedAuthority authority, GuildResultApplicationRequest request, CancellationToken cancellationToken);

    Task<Result<GuildQuestDto>> GetAsync(GuildCallerContext caller, Guid questId, CancellationToken cancellationToken);

    Task<Result<GuildQuestPageDto>> ListAsync(GuildCallerContext caller, GuildListQuestsQuery query, CancellationToken cancellationToken);

    Task<Result<GuildQuestPageDto>> GetTreeAsync(GuildCallerContext caller, Guid questId, GuildPageQuery query, CancellationToken cancellationToken);
}
