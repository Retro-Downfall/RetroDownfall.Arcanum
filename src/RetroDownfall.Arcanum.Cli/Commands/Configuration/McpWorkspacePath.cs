namespace RetroDownfall.Arcanum.Cli.Commands.Configuration;

/// <summary>
/// The canonical spelling and the comparison of the workspace paths the <c>mcp</c> verbs scope by, so a
/// <c>--workspace</c> path and a server's working directory match whenever they name the same location in
/// the same words an operator would use: a relative spelling, a <c>.</c> or <c>..</c> segment, doubled or
/// trailing separators, and (for a Windows path) either separator and any letter case.
/// </summary>
/// <remarks>
/// <para>The canonical form is lexical. Symbolic links are not followed, because the host stores a
/// server's working directory as it was configured and a followed link would no longer match it.</para>
/// <para>A path is read in Windows terms when the host is Windows or when it is spelled with a drive
/// letter or a UNC prefix, so a server-owned <c>C:\srv\proj</c> compares the same way on every platform.
/// Every other path is a POSIX path: separators and case are significant. Like
/// <c>WorkspaceRootPolicy</c>, this does not canonicalise case on macOS, whose default file system is
/// case-insensitive; an operator there types the spelling the server was configured with.</para>
/// </remarks>
internal static class McpWorkspacePath
{
    /// <summary>The canonical spelling of <paramref name="path"/> on this host, against the current directory.</summary>
    public static string Canonicalise(string path) =>
        Canonicalise(path, Environment.CurrentDirectory, OperatingSystem.IsWindows());

    /// <summary>Whether two paths name the same location on this host, against the current directory.</summary>
    public static bool Same(string left, string right) =>
        Same(left, right, Environment.CurrentDirectory, OperatingSystem.IsWindows());

    /// <summary>
    /// The canonical spelling of <paramref name="path"/>: absolute (a relative path is resolved against
    /// <paramref name="currentDirectory"/>), with <c>.</c> and <c>..</c> segments applied, one separator
    /// between segments and none at the end, and the platform's separator on a Windows path.
    /// </summary>
    /// <param name="path">The path as the operator typed it, or as the server stores it.</param>
    /// <param name="currentDirectory">What a relative path is relative to.</param>
    /// <param name="windowsHost">Whether the host is Windows, where every path is read in Windows terms.</param>
    internal static string Canonicalise(string path, string currentDirectory, bool windowsHost)
    {
        ArgumentNullException.ThrowIfNull(path);

        string trimmed = path.Trim();

        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        bool windows = IsWindowsSpelling(trimmed, windowsHost);

        char separator = windows ? '\\' : '/';

        string spelled = windows ? trimmed.Replace('/', '\\') : trimmed;

        string root = RootOf(spelled, windows);

        if (root.Length == 0 && !string.IsNullOrWhiteSpace(currentDirectory))
        {
            string baseDirectory = currentDirectory.Trim();

            string baseRoot = RootOf(windows ? baseDirectory.Replace('/', '\\') : baseDirectory, windows);

            if (baseRoot.Length > 0)
            {
                spelled = (windows ? baseDirectory.Replace('/', '\\') : baseDirectory).TrimEnd(separator) + separator + spelled;

                root = RootOf(spelled, windows);
            }
        }

        // A UNC root already carries its server and share, which a ".." must never climb out of.
        List<string> segments = [];

        foreach (string segment in spelled[root.Length..].Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return root + string.Join(separator, segments);
    }

    /// <summary>
    /// Whether two paths name the same location: equal once canonical, ignoring case when either is read in
    /// Windows terms.
    /// </summary>
    /// <param name="left">One path.</param>
    /// <param name="right">The other path.</param>
    /// <param name="currentDirectory">What a relative path is relative to.</param>
    /// <param name="windowsHost">Whether the host is Windows, where every path is read in Windows terms.</param>
    internal static bool Same(string left, string right, string currentDirectory, bool windowsHost)
    {
        ArgumentNullException.ThrowIfNull(left);

        ArgumentNullException.ThrowIfNull(right);

        StringComparison comparison =
            IsWindowsSpelling(left.Trim(), windowsHost) || IsWindowsSpelling(right.Trim(), windowsHost)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        return string.Equals(
            Canonicalise(left, currentDirectory, windowsHost),
            Canonicalise(right, currentDirectory, windowsHost),
            comparison);
    }

    private static bool IsWindowsSpelling(string path, bool windowsHost) =>
        windowsHost
        || HasDriveLetter(path)
        || path.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool HasDriveLetter(string path) =>
        path.Length >= 2
        && char.IsAsciiLetter(path[0])
        && path[1] == ':';

    /// <summary>
    /// The root of a path whose separators are already the platform's: <c>/</c>, a drive such as
    /// <c>C:\</c>, a UNC share such as <c>\\server\share\</c>, or a bare <c>\</c>; empty for a relative path.
    /// </summary>
    private static string RootOf(string path, bool windows)
    {
        if (!windows)
        {
            return path.StartsWith('/') ? "/" : string.Empty;
        }

        if (HasDriveLetter(path))
        {
            return string.Concat(path.AsSpan(0, 2), "\\");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);

            return parts.Length >= 2
                ? $@"\\{parts[0]}\{parts[1]}\"
                : @"\\" + string.Join('\\', parts) + (parts.Length > 0 ? "\\" : string.Empty);
        }

        return path.StartsWith('\\') ? "\\" : string.Empty;
    }
}
