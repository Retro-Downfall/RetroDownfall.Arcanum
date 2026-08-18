namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Resolves the final target of a symbolic link, if any. Returns null with success when the path is not a symlink.
/// Fails closed (returns false) on resolution errors such as a dangling symlink or permission denial.
/// Shared between Core path policies and Infrastructure MCP helpers so both paths use the same resolution logic.
/// </summary>
internal static class SymlinkPathResolver
{

    /// <summary>
    /// Bounds symlink-chain recursion so a cycle of directory links cannot spin forever.
    /// </summary>
    private const int MaxResolutionDepth = 40;

    /// <summary>
    /// Test seam for symlink resolution failure and target branches.
    /// </summary>
    internal static Func<string, (bool Success, string? Target)>? TryResolveForTests { get; set; }

    public static bool TryResolveFinalTarget(string path, out string? resolvedTarget)
    {

        resolvedTarget = null;

        if (TryResolveForTests is not null)
        {

            (bool success, string? target) = TryResolveForTests(path);

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

    /// <summary>
    /// Canonicalizes <paramref name="path"/> one component at a time, resolving a symbolic link wherever
    /// it appears rather than only in the final position. <see cref="File.ResolveLinkTarget(string, bool)"/>
    /// and <see cref="Directory.ResolveLinkTarget(string, bool)"/> inspect only the last component, so a
    /// containment check built on them alone treats "&lt;root&gt;/link/child" as contained whenever "child"
    /// is an ordinary directory — however far outside the root "link" points.
    /// </summary>
    /// <remarks>
    /// A path whose leading components exist but whose tail does not is canonicalized as far as it exists
    /// and the remainder is appended lexically, so callers that validate a path before creating it keep
    /// working. Fails closed (returns false) on any resolution error.
    /// </remarks>
    public static bool TryResolveCanonicalPath(string path, out string? canonicalPath) =>
        TryResolveCanonicalPath(path, resolutionDepth: 0, out canonicalPath);

    private static bool TryResolveCanonicalPath(string path, int resolutionDepth, out string? canonicalPath)
    {

        canonicalPath = null;

        if (resolutionDepth > MaxResolutionDepth)
        {

            return false;

        }

        string fullPath;

        try
        {

            fullPath = Path.GetFullPath(path);

        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException or UnauthorizedAccessException)
        {

            return false;

        }

        string? pathRoot = Path.GetPathRoot(fullPath);

        if (string.IsNullOrEmpty(pathRoot))
        {

            return false;

        }

        string[] components = fullPath[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        string current = pathRoot;

        for (int index = 0; index < components.Length; index++)
        {

            string candidate = Path.Combine(current, components[index]);

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {

                string remainder = string.Join(
                    Path.DirectorySeparatorChar,
                    components[(index + 1)..]);

                canonicalPath = Path.TrimEndingDirectorySeparator(
                    remainder.Length == 0 ? candidate : Path.Combine(candidate, remainder));

                return true;

            }

            if (!TryResolveFinalTarget(candidate, out string? target))
            {

                return false;

            }

            if (target is null)
            {

                current = candidate;

                continue;

            }

            // The link's own target can sit behind further symlinked ancestors, so canonicalize it too.
            if (!TryResolveCanonicalPath(target, resolutionDepth + 1, out string? canonicalTarget)
                || canonicalTarget is null)
            {

                return false;

            }

            current = canonicalTarget;

        }

        canonicalPath = Path.TrimEndingDirectorySeparator(current);

        return true;

    }

}
