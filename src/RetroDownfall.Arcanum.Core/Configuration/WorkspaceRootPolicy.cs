using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Shared path containment checks for workspace-scoped API routes.
/// </summary>
/// <remarks>
/// Path comparison is ordinal on Linux and case-insensitive on Windows. On macOS the default
/// file system is case-insensitive (APFS/HFS+) but this helper does not canonicalize casing,
/// so a configured root and a request that differ only in case may be rejected on macOS even
/// though they resolve to the same directory. Operators should configure roots using the
/// canonical casing they intend callers to use.
/// </remarks>
public static class WorkspaceRootPolicy
{

    /// <summary>
    /// When <paramref name="allowedRoots"/> is empty, access is denied (secure-by-default).
    /// When non-empty, the normalized path must fall under one of the configured roots.
    /// </summary>
    public static Result<string> EnforceAllowedRoots(
        string normalizedPath,
        string[] allowedRoots,
        string pathNotAllowedCode,
        string pathNotAllowedMessage)
    {

        if (allowedRoots.Length == 0)
        {

            return Result<string>.Failure(new Error(pathNotAllowedCode, pathNotAllowedMessage));

        }

        if (!IsUnderAnyAllowedRoot(normalizedPath, allowedRoots))
        {
            return Result<string>.Failure(new Error(pathNotAllowedCode, pathNotAllowedMessage));
        }

        return Result<string>.Success(normalizedPath);
    }

    public static bool IsUnderAnyAllowedRoot(string candidateFullPath, string[] allowedRoots)
    {
        char sep = Path.DirectorySeparatorChar;

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (string root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string normalizedRoot;

            try
            {
                normalizedRoot = Path.GetFullPath(root.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue;
            }

            if (IsPathUnderRoot(normalizedRoot, candidateFullPath, sep, cmp))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsStrictChildPath(string parentFullPath, string candidateFullPath)
    {
        char sep = Path.DirectorySeparatorChar;

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (IsSamePath(parentFullPath, candidateFullPath, sep, cmp))
        {
            return false;
        }

        return IsPathUnderRoot(parentFullPath, candidateFullPath, sep, cmp);
    }

    public static bool IsSamePath(string leftFullPath, string rightFullPath)
    {
        char sep = Path.DirectorySeparatorChar;

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return IsSamePath(leftFullPath, rightFullPath, sep, cmp);
    }

    private static bool IsPathUnderRoot(string rootFullPath, string candidateFullPath, char sep, StringComparison cmp)
    {
        string normalizedRoot = rootFullPath.TrimEnd(sep);

        string prefix = normalizedRoot + sep;

        if (!candidateFullPath.Equals(normalizedRoot, cmp)
            && !candidateFullPath.StartsWith(prefix, cmp))
        {
            return false;
        }

        if (!SymlinkPathResolver.TryResolveFinalTarget(candidateFullPath, out string? resolvedTarget))
        {
            return false;
        }

        if (resolvedTarget is null)
        {
            return true;
        }

        string resolvedPrefix = normalizedRoot + sep;

        return resolvedTarget.Equals(normalizedRoot, cmp)
            || resolvedTarget.StartsWith(resolvedPrefix, cmp);
    }

    private static bool IsSamePath(string leftFullPath, string rightFullPath, char sep, StringComparison cmp)
    {
        string left = leftFullPath.TrimEnd(sep);

        string right = rightFullPath.TrimEnd(sep);

        return left.Equals(right, cmp);
    }

}
