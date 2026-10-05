using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

/// <summary>
/// Workspace paths that model-driven and API-driven file writes must never create, modify, rename
/// or delete. Git metadata (<c>.git</c>: hooks, <c>core.fsmonitor</c>, <c>core.hooksPath</c>,
/// aliases) executes with the operator's full identity outside any tool jail on the operator's next
/// <c>git</c> command, and the <c>.arcanum</c> marker directory is host-owned state. The host's own
/// <c>.arcanum</c> writes (campaign registration, <c>campaign.json</c>) do not go through the file
/// tools and are not subject to this check.
/// </summary>
/// <remarks>
/// Matching is segment-based and follows the same Unicode and trailing-dot alias model as
/// <see cref="WorkspaceRelativePath"/>, so a path that the host filesystem would resolve to a
/// protected name is protected too. <c>.git</c> is protected at any depth (nested checkouts and
/// submodule working trees carry hooks as well, and a <c>.git</c> file is a worktree pointer);
/// <c>.arcanum</c> is the workspace marker directory and is protected only as the first segment.
/// </remarks>
internal static class WorkspaceProtectedPaths
{
    private const string GitSegment = ".git";

    private const string ArcanumSegment = ".arcanum";

    // The 8.3 short name NTFS may assign to .git; git itself treats it as an alias of .git.
    private const string GitWindowsShortNameSegment = "GIT~1";

    internal static bool IsProtectedRelativePath(string relativePath) =>
        IsProtectedRelativePath(relativePath, WorkspaceRelativePath.CurrentPlatform);

    internal static bool IsProtectedRelativePath(
        string relativePath,
        WorkspacePathAliasPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        string gitAlias = WorkspaceRelativePath.GetCanonicalAlias(GitSegment, platform);

        string arcanumAlias = WorkspaceRelativePath.GetCanonicalAlias(ArcanumSegment, platform);

        bool isFirstSegment = true;

        foreach (string segment in relativePath.Split(
                     ['/', '\\'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            string alias = WorkspaceRelativePath.GetCanonicalAlias(segment, platform);

            if (string.Equals(alias, gitAlias, StringComparison.Ordinal)
                || (platform == WorkspacePathAliasPlatform.Windows
                    && string.Equals(alias, GitWindowsShortNameSegment, StringComparison.Ordinal)))
            {
                return true;
            }

            if (isFirstSegment
                && string.Equals(alias, arcanumAlias, StringComparison.Ordinal))
            {
                return true;
            }

            isFirstSegment = false;
        }

        return false;
    }

    /// <summary>
    /// Whether a write to <paramref name="absolutePath"/> is refused: the lexical spelling is protected,
    /// or the canonical location (every existing component's symbolic links resolved) is, which stops an
    /// in-workspace link that points into <c>.git</c> or <c>.arcanum</c> from being used as the way in.
    /// </summary>
    internal static bool IsProtectedPath(
        string workspaceRoot,
        string absolutePath)
    {
        if (IsProtectedAbsolutePath(workspaceRoot, absolutePath))
        {
            return true;
        }

        try
        {
            return WorkspacePathPolicy.TryCanonicalize(
                       Path.GetFullPath(workspaceRoot),
                       out string? canonicalRoot,
                       out _)
                   && WorkspacePathPolicy.TryCanonicalize(
                       Path.GetFullPath(absolutePath),
                       out string? canonicalPath,
                       out _)
                   && WorkspacePathPolicy.IsPathUnderWorkspace(canonicalRoot, canonicalPath)
                   && IsProtectedAbsolutePath(canonicalRoot, canonicalPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or IOException)
        {
            // Containment validation rejects an unresolvable path on its own; nothing is protected
            // here that the lexical check above did not already decide.
            return false;
        }
    }

    internal static bool IsProtectedAbsolutePath(
        string workspaceRoot,
        string absolutePath) =>
        IsProtectedAbsolutePath(
            workspaceRoot,
            absolutePath,
            WorkspaceRelativePath.CurrentPlatform);

    internal static bool IsProtectedAbsolutePath(
        string workspaceRoot,
        string absolutePath,
        WorkspacePathAliasPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        string relative = Path.GetRelativePath(
            Path.GetFullPath(workspaceRoot),
            Path.GetFullPath(absolutePath));

        return IsProtectedRelativePath(relative, platform);
    }
}
