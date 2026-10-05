using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

/// <summary>
/// Workspace paths that model-driven and API-driven file writes must never create, modify, rename
/// or delete. Git metadata (<c>.git</c>: hooks, <c>core.fsmonitor</c>, <c>core.hooksPath</c>,
/// aliases) executes with the operator's full identity outside any tool jail on the operator's next
/// <c>git</c> command, and the <c>.arcanum</c> marker directory is host-owned state. The host's own
/// <c>.arcanum</c> writes (campaign registration, <c>campaign.json</c>) do not go through the file
/// tools and are not subject to this check.
/// </summary>
/// <remarks>
/// Matching is segment-based and follows the same Unicode and trailing-dot alias model as
/// <see cref="WorkspaceRelativePath"/>, so a path that the host filesystem would resolve to a
/// protected name is protected too. <c>.git</c> is protected at any depth (nested checkouts and
/// submodule working trees carry hooks as well, and a <c>.git</c> file is a worktree pointer);
/// <c>.arcanum</c> is the workspace marker directory and is protected only as the first segment.
/// On Windows the numbered 8.3 short names (<c>GIT~n</c>, <c>ARCANU~n</c>) and an NTFS stream suffix
/// (<c>.git::$INDEX_ALLOCATION</c>) name the same entries; on macOS the code points HFS+ ignores do not
/// change which entry a spelling names. Both widen the protection and never narrow it.
/// </remarks>
internal static class WorkspaceProtectedPaths
{
    private const string GitSegment = ".git";

    private const string ArcanumSegment = ".arcanum";

    // The 8.3 short names NTFS may assign: a leading period is dropped and the base is cut to six
    // characters before "~n", so .git becomes GIT~1 (git itself treats it as an alias of .git) and
    // .arcanum becomes ARCANU~1. The numeric tail grows when another entry already owns the name.
    private const string GitWindowsShortNameBase = "GIT";

    private const string ArcanumWindowsShortNameBase = "ARCANU";

    internal static bool IsProtectedRelativePath(string relativePath) =>
        IsProtectedRelativePath(relativePath, WorkspaceRelativePath.CurrentPlatform);

    internal static bool IsProtectedRelativePath(
        string relativePath,
        WorkspacePathAliasPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        string gitAlias = WorkspaceRelativePath.GetCanonicalAlias(GitSegment, platform);

        string arcanumAlias = WorkspaceRelativePath.GetCanonicalAlias(ArcanumSegment, platform);

        bool isFirstSegment = true;

        foreach (string segment in relativePath.Split(
                     ['/', '\\'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            string alias = GetProtectionAlias(segment, platform);

            if (string.Equals(alias, gitAlias, StringComparison.Ordinal)
                || (platform == WorkspacePathAliasPlatform.Windows
                    && IsWindowsShortName(alias, GitWindowsShortNameBase)))
            {
                return true;
            }

            if (isFirstSegment
                && (string.Equals(alias, arcanumAlias, StringComparison.Ordinal)
                    || (platform == WorkspacePathAliasPlatform.Windows
                        && IsWindowsShortName(alias, ArcanumWindowsShortNameBase))))
            {
                return true;
            }

            isFirstSegment = false;
        }

        return false;
    }

    /// <summary>
    /// The comparison form of one path segment: what the host filesystem would resolve the spelling to,
    /// beyond the case, Unicode and trailing-dot folding <see cref="WorkspaceRelativePath"/> already
    /// applies. On Windows an NTFS stream suffix (<c>.git::$INDEX_ALLOCATION</c>, <c>.git:stream</c>) names
    /// the entry itself, so everything from the first colon is dropped. On macOS the HFS+ ignorable code
    /// points (U+200C-U+200F, U+202A-U+202E, U+206A-U+206F, U+FEFF) are dropped, because that filesystem
    /// does not distinguish <c>.g&#x200C;it</c> from <c>.git</c>; APFS keeps them, so this only widens the
    /// protection. A colon or an ignorable code point is an ordinary character elsewhere.
    /// </summary>
    private static string GetProtectionAlias(
        string segment,
        WorkspacePathAliasPlatform platform)
    {
        string spelling = segment;

        if (platform == WorkspacePathAliasPlatform.Windows)
        {
            int streamSeparator = spelling.IndexOf(':', StringComparison.Ordinal);

            if (streamSeparator >= 0)
            {
                spelling = spelling[..streamSeparator];
            }
        }
        else if (platform == WorkspacePathAliasPlatform.MacOS)
        {
            spelling = RemoveHfsIgnorableCodePoints(spelling);
        }

        return WorkspaceRelativePath.GetCanonicalAlias(spelling, platform);
    }

    private static string RemoveHfsIgnorableCodePoints(string segment)
    {
        if (!segment.Any(IsHfsIgnorableCodePoint))
        {
            return segment;
        }

        return string.Concat(segment.Where(static character => !IsHfsIgnorableCodePoint(character)));
    }

    private static bool IsHfsIgnorableCodePoint(char character) =>
        character is (>= '‌' and <= '‏')
            or (>= '‪' and <= '‮')
            or (>= '⁪' and <= '⁯')
            or '﻿';

    /// <summary>
    /// Whether <paramref name="alias"/> (already upper-cased with trailing dots and spaces trimmed) is
    /// <c>BASE~n</c> for a positive decimal <c>n</c>. Every tail is accepted, not only <c>~1</c>, because
    /// the digit depends on which other short names already exist in the directory; a literal file named
    /// <c>GIT~2</c> is refused on Windows as the price of failing closed.
    /// </summary>
    private static bool IsWindowsShortName(
        string alias,
        string shortNameBase)
    {
        if (!alias.StartsWith(shortNameBase + "~", StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> tail = alias.AsSpan(shortNameBase.Length + 1);

        return tail.Length > 0
            && tail.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>
    /// Whether a write to <paramref name="absolutePath"/> is refused: the lexical spelling is protected,
    /// or the canonical location (every existing component's symbolic links resolved) is, which stops an
    /// in-workspace link that points into <c>.git</c> or <c>.arcanum</c> from being used as the way in.
    /// </summary>
    internal static bool IsProtectedPath(
        string workspaceRoot,
        string absolutePath)
    {
        if (IsProtectedAbsolutePath(workspaceRoot, absolutePath))
        {
            return true;
        }

        try
        {
            return WorkspacePathPolicy.TryCanonicalize(
                       Path.GetFullPath(workspaceRoot),
                       out string? canonicalRoot,
                       out _)
                   && WorkspacePathPolicy.TryCanonicalize(
                       Path.GetFullPath(absolutePath),
                       out string? canonicalPath,
                       out _)
                   && WorkspacePathPolicy.IsPathUnderWorkspace(canonicalRoot, canonicalPath)
                   && IsProtectedAbsolutePath(canonicalRoot, canonicalPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or IOException)
        {
            // Containment validation rejects an unresolvable path on its own; nothing is protected
            // here that the lexical check above did not already decide.
            return false;
        }
    }

    internal static bool IsProtectedAbsolutePath(
        string workspaceRoot,
        string absolutePath) =>
        IsProtectedAbsolutePath(
            workspaceRoot,
            absolutePath,
            WorkspaceRelativePath.CurrentPlatform);

    internal static bool IsProtectedAbsolutePath(
        string workspaceRoot,
        string absolutePath,
        WorkspacePathAliasPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        string relative = Path.GetRelativePath(
            Path.GetFullPath(workspaceRoot),
            Path.GetFullPath(absolutePath));

        return IsProtectedRelativePath(relative, platform);
    }
}
