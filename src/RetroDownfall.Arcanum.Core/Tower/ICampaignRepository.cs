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
    /// separator, a symbolic link on either side, and the case of a name and the Unicode normalisation form of an accented name, which
    /// are judged per volume (two spellings that differ only that way are one directory on a volume that folds them and two on one that
    /// does not). This is how a directory below a Campaign root (or the root spelled differently) is recognised as that Campaign's
    /// workspace.
    /// </summary>
    /// <remarks>
    /// This member deliberately has no default body. A store that fell back to <see cref="GetByPathAsync"/> would silently leave every
    /// directory below a Campaign root, or the root spelled another way, without that Campaign's Sanctum, so each implementer decides what
    /// it answers. A test pins that the member stays without a body.
    /// </remarks>
    Task<Campaign?> GetByContainedPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// The registered Campaign whose root the text of <paramref name="path"/> places it under, reading the path as it is spelled: no link in
    /// the path is followed, so a link that stands inside a root is a name inside that root whatever it points at, and a path spelled through
    /// a link to a root is not spelled inside it. The deepest root wins, and when several rows name the same directory the one whose Sanctum
    /// is strictest governs it, as for <see cref="GetByContainedPathAsync"/>; a root matches by whole directory components, by every
    /// spelling of the root itself (its own links resolved, a trailing separator, and the case and Unicode normalisation form of a name,
    /// which are judged per volume: a case sibling on a case-sensitive volume is a different directory and does not match, and the text
    /// decides only where the file system cannot be asked), and never by string prefix.
    /// </summary>
    /// <remarks>
    /// <see cref="GetByContainedPathAsync"/> answers the nearest root over every form the path can be read in, which for a link that leads
    /// into another root is the root of its target. This answers the other question: whose workspace a path is written in. A surface that
    /// judges a read of a file by the Sanctum of the Campaign it is spelled inside asks both. Like <see cref="GetByContainedPathAsync"/> it
    /// has no default body, for the same reason: a store that answered the exact match would leave a file spelled inside a Campaign root
    /// without that Campaign's Sanctum. A test pins that both members stay without a body.
    /// </remarks>
    Task<Campaign?> GetBySpelledPathAsync(string path, CancellationToken cancellationToken = default);

    Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<ListPageResult<Campaign>> ListAsync(
        WorkspaceType? typeFilter,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default);

    Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default);

    /// <summary>Writes only name, type, description, settings and the supplied UpdatedAt; returns the stored row or a domain failure.</summary>
    Task<Result<Campaign>> UpdateHeaderAsync(Campaign campaign, CancellationToken cancellationToken = default);

    /// <summary>Writes only settings and the supplied UpdatedAt; returns the stored row or a domain failure.</summary>
    Task<Result<Campaign>> UpdateSettingsAsync(Campaign campaign, CancellationToken cancellationToken = default);

    /// <summary>Writes only Sanctum configuration and the supplied UpdatedAt; returns the stored row or a domain failure.</summary>
    Task<Result<Campaign>> UpdateSanctumAsync(Campaign campaign, CancellationToken cancellationToken = default);

    /// <summary>Writes only name and type and the supplied UpdatedAt; returns the stored row or a domain failure.</summary>
    Task<Result<Campaign>> UpdateWorkspaceAsync(Campaign campaign, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
