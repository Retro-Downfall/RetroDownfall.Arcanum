namespace RetroDownfall.TheForge.Ux.Services.Git;

/// <summary>One row from <c>git status --porcelain=v1</c>.</summary>
public sealed record GitPorcelainEntry(
    char IndexStatus,
    char WorkTreeStatus,
    string Path,
    string? OriginalPath = null)
{

    public bool HasStagedChange =>
        IndexStatus is not ' ' and not '?' and not '!';

    public bool HasUnstagedChange =>
        WorkTreeStatus is not ' ' and not '!'
        || IndexStatus == '?'
        || WorkTreeStatus == '?';

    public bool IsUntracked => IndexStatus == '?' && WorkTreeStatus == '?';

    public string StatusCode => $"{IndexStatus}{WorkTreeStatus}";

    public string DisplayPath =>
        OriginalPath is { Length: > 0 } orig
            ? $"{orig} → {Path}"
            : Path;

}
