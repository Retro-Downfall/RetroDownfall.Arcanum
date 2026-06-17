namespace RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Primitives;

public interface IPromptRepository
{

    Task<Prompt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Prompt?> GetByNameAndVersionAsync(string name, string version, Guid? campaignId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Prompt>> ListVersionsAsync(string name, Guid? campaignId, CancellationToken cancellationToken = default);

    Task<ListPageResult<Prompt>> ListAsync(
        Guid? campaignId,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default);

    Task<Prompt> AddAsync(Prompt prompt, CancellationToken cancellationToken = default);

    Task<Prompt> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

}
