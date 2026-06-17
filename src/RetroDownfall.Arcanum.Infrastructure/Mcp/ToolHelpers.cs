using System.Diagnostics.CodeAnalysis;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal static class ToolHelpers
{

    internal static bool TryNormalizeWorkspace(
        string workingDirectory,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? configurationErrorMessage)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            normalized = null;

            configurationErrorMessage = "No workspace directory was provided for this request. The operator should run `arcanum ask` from their project folder so file paths and commands are scoped to that workspace.";

            return false;
        }

        try
        {
            normalized = Path.GetFullPath(workingDirectory.Trim());

            configurationErrorMessage = null;

            return true;
        }
        catch (Exception)
        {
            normalized = null;

            configurationErrorMessage = "The workspace directory on this request could not be resolved. Please ask the operator to use a valid path and try again.";

            return false;
        }
    }

    /// <summary>
    /// Lexical prefix-only check (used for ASCII-fast pre-filter and for paths that do not yet exist on disk).
    /// </summary>
    internal static bool IsPathUnderWorkspace(string workspaceRootFull, string candidateFull)
    {
        char sep = Path.DirectorySeparatorChar;

        string root = workspaceRootFull.TrimEnd(sep);

        string prefix = root + sep;

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return candidateFull.Equals(root, cmp) || candidateFull.StartsWith(prefix, cmp);
    }

    /// <summary>
    /// Lexical prefix check plus symlink target resolution for every path component from the workspace root
    /// to the candidate. Rejects when any existing ancestor is a symlink whose final target leaves the root,
    /// including when the leaf path does not yet exist (write/create through a symlinked parent).
    /// </summary>
    internal static bool IsPathUnderWorkspaceWithSymlinkCheck(
        string workspaceRootFull,
        string candidateFull,
        out string? resolvedFinalPath)
    {
        resolvedFinalPath = null;

        if (!IsPathUnderWorkspace(workspaceRootFull, candidateFull))
        {
            return false;
        }

        if (!TryValidatePathComponentsUnderRoot(workspaceRootFull, candidateFull, out string? validatedPath))
        {
            return false;
        }

        resolvedFinalPath = validatedPath;

        return true;
    }

    /// <summary>
    /// Re-validates containment immediately before I/O to mitigate TOCTOU between resolution and use.
    /// </summary>
    internal static bool RevalidatePathBeforeIo(string workspaceRootFull, string absolutePath)
    {
        return IsPathUnderWorkspaceWithSymlinkCheck(workspaceRootFull, absolutePath, out _);
    }

    private static bool TryValidatePathComponentsUnderRoot(
        string workspaceRootFull,
        string candidateFull,
        out string? resolvedFinalPath)
    {
        resolvedFinalPath = null;

        char sep = Path.DirectorySeparatorChar;

        string root = workspaceRootFull.TrimEnd(sep);

        string candidate = Path.GetFullPath(candidateFull);

        StringComparison rootCmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!IsPathUnderWorkspace(root, candidate))
        {
            return false;
        }

        if (string.Equals(root, candidate, rootCmp))
        {
            resolvedFinalPath = candidate;

            return true;
        }

        string relative;

        try
        {
            relative = Path.GetRelativePath(root, candidate);
        }
        catch (Exception)
        {
            return false;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return false;
        }

        string current = root;

        string[] parts = relative.Split(sep, StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            current = Path.GetFullPath(Path.Combine(current, part));

            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            if (!TryResolveFinalSymlinkTarget(current, out string? linkTarget))
            {
                return false;
            }

            if (linkTarget is not null)
            {
                if (!IsPathUnderWorkspace(root, linkTarget))
                {
                    return false;
                }

                current = linkTarget;
            }
        }

        if (File.Exists(candidate) || Directory.Exists(candidate))
        {
            if (!TryResolveFinalSymlinkTarget(candidate, out string? finalTarget))
            {
                return false;
            }

            resolvedFinalPath = finalTarget ?? candidate;

            return IsPathUnderWorkspace(root, resolvedFinalPath);
        }

        resolvedFinalPath = null;

        return true;
    }

    /// <summary>
    /// Resolves a symlink target when present. Returns false when resolution fails (fail-closed).
    /// When the path is not a symlink, <paramref name="resolvedTarget"/> is null and the method returns true.
    /// </summary>
    private static bool TryResolveFinalSymlinkTarget(string path, out string? resolvedTarget)
    {
        resolvedTarget = null;

        try
        {
            if (File.Exists(path))
            {
                FileSystemInfo? linkTarget = File.ResolveLinkTarget(path, returnFinalTarget: true);

                if (linkTarget is null)
                {
                    return true;
                }

                resolvedTarget = Path.GetFullPath(linkTarget.FullName);

                return true;
            }

            if (Directory.Exists(path))
            {
                FileSystemInfo? linkTarget = Directory.ResolveLinkTarget(path, returnFinalTarget: true);

                if (linkTarget is null)
                {
                    return true;
                }

                resolvedTarget = Path.GetFullPath(linkTarget.FullName);

                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        return true;
    }

}
