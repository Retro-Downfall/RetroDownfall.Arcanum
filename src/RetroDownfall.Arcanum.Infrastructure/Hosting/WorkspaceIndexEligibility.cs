namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// The single rule that decides whether a workspace path may be indexed, shared by the full walk
/// (<c>EnumerateCandidateFiles</c>), the incremental drain (<c>ProcessIncrementalChangesAsync</c>) and
/// the watcher intake (<c>QueueWatcherChange</c>), so a path the full walk would never send to the
/// embedding provider can never reach it through a watcher event either.
/// </summary>
/// <remarks>
/// A path is eligible when no segment of its relative path is dot-prefixed, hidden, a system entry or
/// an ignored directory name, and its extension is configured. The dot rule is lexical and applies on
/// every platform: on Unix <c>File.GetAttributes</c> reports only the leaf's own hidden bit, so a file
/// inside <c>.secrets/</c> carries no attribute at all, and Windows has no dot rule of its own, so the
/// attribute check stays for entries that are hidden without a leading dot.
/// </remarks>
internal static class WorkspaceIndexEligibility
{
    private const FileAttributes HiddenOrSystem = FileAttributes.Hidden | FileAttributes.System;

    private static readonly char[] DirectorySeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly HashSet<string> IgnoredDirectorySegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".git",
        "node_modules",
        ".vs",
        ".nuget",
        "packages",
        "dist",
        "build",
    };

    /// <summary>
    /// The eligibility of one entry whose own attributes are already known.
    /// </summary>
    /// <param name="relativePath">
    /// The path relative to the workspace root. The walk passes only the entry's name because every
    /// ancestor was vetted before the walk descended into it.
    /// </param>
    /// <param name="attributes">The attributes of the entry itself.</param>
    /// <param name="extensions">
    /// The configured extensions, or <see langword="null"/> to skip the extension rule (a directory,
    /// or a watcher event whose target kind is not known yet).
    /// </param>
    internal static bool IsEligible(string relativePath, FileAttributes attributes, IReadOnlySet<string>? extensions)
    {
        if ((attributes & HiddenOrSystem) != 0 || !HasEligibleSegments(relativePath))
        {
            return false;
        }

        return extensions is null || extensions.Contains(Path.GetExtension(relativePath));
    }

    /// <summary>
    /// The eligibility of a path whose ancestors have not been vetted by a walk: the lexical rules,
    /// then the Hidden/System attribute of every ancestor directory and of the leaf.
    /// </summary>
    /// <param name="relativePath">The path relative to the workspace root.</param>
    /// <param name="getAttributes">
    /// Reads the attributes of a prefix of <paramref name="relativePath"/> (its first segment, its
    /// first two segments, and so on up to the whole path). It may throw the filesystem's exceptions;
    /// the caller decides what an unreadable entry means.
    /// </param>
    /// <param name="extensions">The configured extensions, or <see langword="null"/> to skip the rule.</param>
    internal static bool IsEligible(
        string relativePath,
        Func<string, FileAttributes> getAttributes,
        IReadOnlySet<string>? extensions)
    {
        if (!IsEligible(relativePath, default(FileAttributes), extensions))
        {
            return false;
        }

        int start = 0;

        while (start < relativePath.Length)
        {
            int separator = relativePath.IndexOfAny(DirectorySeparators, start);

            int end = separator < 0 ? relativePath.Length : separator;

            if (end > start && (getAttributes(relativePath[..end]) & HiddenOrSystem) != 0)
            {
                return false;
            }

            start = end + 1;
        }

        return true;
    }

    /// <summary>
    /// The lexical part of the rule, free of filesystem access: no segment is dot-prefixed or an
    /// ignored directory name. Used where a path is judged before anything is known about the entry,
    /// such as when a watcher event arrives. The workspace root itself (<c>"."</c>) passes.
    /// </summary>
    internal static bool HasEligibleSegments(string relativePath)
    {
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ignored =
            IgnoredDirectorySegments.GetAlternateLookup<ReadOnlySpan<char>>();

        int start = 0;

        while (start < relativePath.Length)
        {
            int separator = relativePath.IndexOfAny(DirectorySeparators, start);

            int end = separator < 0 ? relativePath.Length : separator;

            if (end > start)
            {
                ReadOnlySpan<char> segment = relativePath.AsSpan(start, end - start);

                // A lone "." is the workspace root itself (a directory event for it still has to reach
                // the incremental drain, which answers it with a reconciliation); every other
                // dot-prefixed segment, ".." included, is hidden or outside the workspace.
                bool isRoot = segment.Length == 1 && segment[0] == '.';

                if ((segment[0] == '.' && !isRoot) || ignored.Contains(segment))
                {
                    return false;
                }
            }

            start = end + 1;
        }

        return true;
    }
}
