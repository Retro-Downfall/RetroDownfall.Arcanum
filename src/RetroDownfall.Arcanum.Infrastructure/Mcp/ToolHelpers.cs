using System.Diagnostics.CodeAnalysis;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal static class ToolHelpers
{

    /// <summary>
    /// Test-only seam for Windows ordinal-ignore path comparison branches on non-Windows hosts.
    /// Production code should leave this at the default (<see langword="false"/>).
    /// Set via <see cref="SetUseOrdinalIgnoreCasePathComparisonForTests(bool)"/>.
    /// </summary>
    private static bool _useOrdinalIgnoreCasePathComparisonForTests;

    /// <summary>
    /// Test-only seam for relative-path escape validation branches.
    /// Set via <see cref="SetRelativePathResolverForTests"/>.
    /// </summary>
    private static Func<string, string, string>? _getRelativePathForTests;

    /// <summary>
    /// Test-only seam for symlink resolution failure and target branches.
    /// Set via <see cref="SetSymlinkResolverForTests"/>.
    /// </summary>
    private static Func<string, (bool Success, string? Target)>? _tryResolveFinalSymlinkTargetForTests;

    /// <summary>
    /// Enables or disables Windows-style ordinal-ignore-case path comparison for tests.
    /// </summary>
    internal static void SetUseOrdinalIgnoreCasePathComparisonForTests(bool value)
    {
        _useOrdinalIgnoreCasePathComparisonForTests = value;
    }

    /// <summary>
    /// Supplies a test-only relative-path resolver. Pass <see langword="null"/> to restore the default.
    /// </summary>
    internal static void SetRelativePathResolverForTests(Func<string, string, string>? resolver)
    {
        _getRelativePathForTests = resolver;
    }

    /// <summary>
    /// Supplies a test-only symlink resolver. Pass <see langword="null"/> to restore the default.
    /// </summary>
    internal static void SetSymlinkResolverForTests(Func<string, (bool Success, string? Target)>? resolver)
    {
        _tryResolveFinalSymlinkTargetForTests = resolver;
    }

    /// <summary>
    /// Restores all test seams to production defaults. Call from test teardown to avoid cross-test leakage.
    /// </summary>
    internal static void ResetTestSeams()
    {
        _useOrdinalIgnoreCasePathComparisonForTests = false;
        _getRelativePathForTests = null;
        _tryResolveFinalSymlinkTargetForTests = null;
    }

    private static StringComparison PathComparison =>
        _useOrdinalIgnoreCasePathComparisonForTests || OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

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

        StringComparison cmp = PathComparison;

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

    internal static bool TryResolveFinalSymlinkTargetForCoverageTest(string path, out string? resolvedTarget)
        => TryResolveFinalSymlinkTarget(path, out resolvedTarget);

    private static bool TryValidatePathComponentsUnderRoot(
        string workspaceRootFull,
        string candidateFull,
        out string? resolvedFinalPath)
    {
        resolvedFinalPath = null;

        char sep = Path.DirectorySeparatorChar;

        string root = workspaceRootFull.TrimEnd(sep);

        string candidate = Path.GetFullPath(candidateFull);

        StringComparison rootCmp = PathComparison;

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
            relative = _getRelativePathForTests is not null
                ? _getRelativePathForTests(root, candidate)
                : Path.GetRelativePath(root, candidate);
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

            if (finalTarget is not null)
            {
                resolvedFinalPath = finalTarget;
            }
            else
            {
                resolvedFinalPath = candidate;
            }

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

        if (_tryResolveFinalSymlinkTargetForTests is not null)
        {
            (bool success, string? target) = _tryResolveFinalSymlinkTargetForTests(path);

            if (!success)
            {
                return false;
            }

            resolvedTarget = target;

            return true;
        }

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

            if (!Directory.Exists(path))
            {
                return true;
            }

            FileSystemInfo? directoryLinkTarget = Directory.ResolveLinkTarget(path, returnFinalTarget: true);

            if (directoryLinkTarget is null)
            {
                return true;
            }

            resolvedTarget = Path.GetFullPath(directoryLinkTarget.FullName);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

}
