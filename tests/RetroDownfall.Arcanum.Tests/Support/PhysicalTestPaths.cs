namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The physical spelling of a test directory: every symbolic link along the path resolved, the way the operating system reports a working
/// directory (<c>getcwd</c>, <c>Environment.CurrentDirectory</c>). A macOS temporary directory sits behind the <c>/var</c> link, so the
/// path a test builds and the path a process reports for it are two spellings of one directory. It is resolved here, without the workspace
/// path policy, so a test that only needs the physical spelling does not become a policy user of its own.
/// </summary>
internal static class PhysicalTestPaths
{
    private const int MaxLinkExpansions = 40;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    internal static string Resolve(string path) => Resolve(Path.GetFullPath(path), 0);

    private static string Resolve(string full, int expansions)
    {
        if (expansions > MaxLinkExpansions)
        {
            throw new IOException($"Too many levels of symbolic links in '{full}'.");
        }

        string root = Path.GetPathRoot(full) ?? string.Empty;

        string[] parts = full[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        string current = root;

        for (int index = 0; index < parts.Length; index++)
        {
            string next = Path.Combine(current, parts[index]);

            FileSystemInfo? target = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: false);

            if (target is null)
            {
                current = next;

                continue;
            }

            string below = string.Join(Path.DirectorySeparatorChar, parts[(index + 1)..]);

            return Resolve(Path.GetFullPath(Path.Combine(target.FullName, below)), expansions + 1);
        }

        return current;
    }
}
