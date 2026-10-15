using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Core.Tower;

public interface ICampaignRepository
{
    Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The Campaign registered at <paramref name="path"/>. A path spelled the same way as a registration but for a trailing separator, or
    /// (where the file system folds case) the case of a name, is that registration: the same directory registered twice is a duplicate.
    /// </summary>
    Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// The registered Campaign whose root is <paramref name="path"/> or one of its ancestors, or <see langword="null"/> when no registered
    /// root contains it. The deepest root wins, so a Campaign nested inside another governs its own tree; when several registrations name
    /// the same directory, the one whose Sanctum is strictest governs it. A root matches by whole directory components, never by string
    /// prefix (<c>/work/app</c> does not contain <c>/work/app-legacy</c>), and by every spelling the same directory can have: a trailing
    /// separator, a symbolic link on either side, the case of a name on a platform whose file systems fold case, and the Unicode
    /// normalisation form of an accented name. This is how a directory below a Campaign root (or the root spelled differently) is
    /// recognised as that Campaign's workspace.
    /// </summary>
    /// <remarks>
    /// The default answers the exact match, which is all a store without an ancestor query can know; the production repository overrides it.
    /// </remarks>
    Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default) =>
        GetByPathAsync(path, cancellationToken);

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
