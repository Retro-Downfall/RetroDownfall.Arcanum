namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal static class WorkspaceRootPath
{
    internal static bool IsWithinOrEqual(
        string candidate,
        string root)
    {
        string relative = Path.GetRelativePath(root, candidate);

        return string.Equals(relative, ".", StringComparison.Ordinal)
            || (!Path.IsPathRooted(relative)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal));
    }
}
