using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Core.TheForge;

public interface ICampaignRepository
{

    Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default);

    Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<ListPageResult<Campaign>> ListAsync(
        WorkspaceType? typeFilter,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default);

    Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default);

    Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);

}
