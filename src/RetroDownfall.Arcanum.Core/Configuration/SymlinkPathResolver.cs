namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Resolves the final target of a symbolic link, if any. Returns null with success when the path is not a symlink.
/// Fails closed (returns false) on resolution errors such as a dangling symlink or permission denial.
/// Shared between Core path policies and Infrastructure MCP helpers so both paths use the same resolution logic.
/// </summary>
internal static class SymlinkPathResolver
{

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

}
