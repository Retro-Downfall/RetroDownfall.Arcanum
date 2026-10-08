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

        string root = RootOf(spelled, windows, out int rootSpelledLength);

        if (root.Length == 0 && !string.IsNullOrWhiteSpace(currentDirectory))
        {
            string baseDirectory = currentDirectory.Trim();

            string baseRoot = RootOf(windows ? baseDirectory.Replace('/', '\\') : baseDirectory, windows, out _);

            if (baseRoot.Length > 0)
            {
                spelled = (windows ? baseDirectory.Replace('/', '\\') : baseDirectory).TrimEnd(separator) + separator + spelled;

                root = RootOf(spelled, windows, out rootSpelledLength);
            }
        }

        // A UNC root already carries its server and share, which a ".." must never climb out of.
        List<string> segments = [];

        // The rest starts where the spelled root ends, which is not the canonical root's length: "C:" and
        // "C:proj" spell no separator after the drive, and a UNC prefix can be spelled with doubled or no
        // trailing separators.
        foreach (string segment in spelled[rootSpelledLength..].Split(separator, StringSplitOptions.RemoveEmptyEntries))
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
    /// <param name="path">The path.</param>
    /// <param name="windows">Whether the path is read in Windows terms.</param>
    /// <param name="spelledLength">
    /// How many characters of <paramref name="path"/> the root takes up as spelled, which is where the rest of
    /// the path starts. It differs from the canonical root's length for a drive with no separator after it
    /// (<c>C:</c>, <c>C:proj</c>) and for a UNC prefix spelled with doubled or no trailing separators.
    /// </param>
    private static string RootOf(string path, bool windows, out int spelledLength)
    {
        if (!windows)
        {
            spelledLength = path.StartsWith('/') ? 1 : 0;

            return path.StartsWith('/') ? "/" : string.Empty;
        }

        if (HasDriveLetter(path))
        {
            spelledLength = path.Length > 2 && path[2] == '\\' ? 3 : 2;

            return string.Concat(path.AsSpan(0, 2), "\\");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // The server and the share are the first two non-empty segments; the root ends where the share's
            // name does, and the separators after it belong to the rest.
            List<string> parts = [];
            int position = 2;
            while (parts.Count < 2 && position < path.Length)
            {
                while (position < path.Length && path[position] == '\\')
                {
                    position++;
                }

                int start = position;
                while (position < path.Length && path[position] != '\\')
                {
                    position++;
                }

                if (position > start)
                {
                    parts.Add(path[start..position]);
                }
            }

            spelledLength = position;

            return parts.Count >= 2
                ? $@"\\{parts[0]}\{parts[1]}\"
                : @"\\" + string.Join('\\', parts) + (parts.Count > 0 ? "\\" : string.Empty);
        }

        spelledLength = path.StartsWith('\\') ? 1 : 0;

        return path.StartsWith('\\') ? "\\" : string.Empty;
    }
}
