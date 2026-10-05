namespace RetroDownfall.Arcanum.Core.Tower;

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

    /// <summary>
    /// Replaces every prompt of one Campaign with <paramref name="prompts"/> in a single transaction, so a
    /// failure at any step leaves the Campaign's previous prompts untouched. Each incoming prompt is
    /// stamped with <paramref name="campaignId"/>, and an incoming prompt may reuse the name and version of
    /// one being replaced. Returns the number of prompts written.
    /// </summary>
    /// <remarks>
    /// The token governs the work up to and including the first delete. After that the adds and the commit
    /// run to completion regardless of it, so a disconnect cannot interrupt the swap midway.
    /// </remarks>
    Task<Result<int>> ReplaceCampaignPromptsAsync(
        Guid campaignId,
        IReadOnlyList<Prompt> prompts,
        CancellationToken cancellationToken = default);
}
