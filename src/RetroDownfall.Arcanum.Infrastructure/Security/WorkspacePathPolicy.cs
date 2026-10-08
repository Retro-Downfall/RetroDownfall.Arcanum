using System.Diagnostics.CodeAnalysis;
using System.Security;

using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Cross-cutting workspace path containment policy shared by MCP sandbox I/O, spell scanning,
/// sanctum guards, and API path resolution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read tier 1 — canonical-path containment:</b>
/// <see cref="IsPathUnderWorkspace"/> is the lexical prefix check. <see cref="IsPathUnderWorkspaceWithSymlinkCheck"/>
/// and <see cref="RevalidatePathBeforeIo"/> add the canonical-path rule: after the lexical check, the workspace
/// root and the candidate are both canonicalised by walking one component at a time without following links
/// (<c>lstat</c> semantics); every symbolic link met is replaced by its own target text, and that target is
/// walked again component by component, so a link spelled through another link is resolved through that
/// link too (up to <see cref="MaxSymbolicLinkResolutions"/> links). The walk stops at the first component
/// that does not exist, keeping the rest lexically. Containment is then one prefix check of the canonical
/// candidate against the canonical root. A resolved link target is never spliced into the walk as a
/// string, which is what let <c>ws/d -> b/sub</c> through when <c>ws/b -> ../outside</c> (R-007).
/// This tier is sufficient for read-only scans and pre-I/O checks (for example <c>SpellScanner</c> tree walks).
/// </para>
/// <para>
/// <b>Read tier 2 — handle-identity I/O:</b>
/// Mutating or trust-sensitive reads must additionally capture expected
/// <see cref="FileHandleIdentity"/> from the resolved path and re-validate the opened handle after
/// <c>FileStream</c> creation (<c>SandboxedFileIo</c> in the MCP layer). The post-open check asks the kernel
/// where the handle lives (<see cref="IsOpenedHandleUnderWorkspace"/>) and compares that path with the
/// root's own kernel path, never the string the stream was opened with. Handle identity closes TOCTOU gaps
/// between path validation and actual file use.
/// </para>
/// </remarks>
internal static class WorkspacePathPolicy
{
    /// <summary>
    /// Most symbolic links one canonicalisation follows before failing closed. Linux's own limit; macOS
    /// stops at 32, so the kernel refuses such a chain before this does.
    /// </summary>
    internal const int MaxSymbolicLinkResolutions = 40;

    /// <summary>
    /// Test-only seam for Windows ordinal-ignore path comparison branches on non-Windows hosts.
    /// Production code should leave this at the default (<see langword="false"/>).
    /// Set via <see cref="SetUseOrdinalIgnoreCasePathComparisonForTests(bool)"/>.
    /// </summary>
    private static bool _useOrdinalIgnoreCasePathComparisonForTests;

    /// <summary>
    /// Test-only seam for the Windows classification of a reparse point that names no other location, on
    /// non-Windows hosts. Production code should leave this at the default (<see langword="false"/>).
    /// Set via <see cref="SetUseWindowsReparsePointClassificationForTests(bool)"/>.
    /// </summary>
    private static bool _useWindowsReparsePointClassificationForTests;

    /// <summary>
    /// Observation-only test hook, invoked once per <see cref="IsPathUnderWorkspaceWithSymlinkCheck"/> call
    /// that passes the lexical check, with the normalized candidate. It cannot change the outcome.
    /// </summary>
    internal static Action<string>? ContainmentCheckObserverForTests { get; set; }

    /// <summary>
    /// Test-only seam that replaces reading a reparse point's link target, given the path being classified.
    /// It stands in for what the filesystem cannot be made to answer on demand: a read that fails, or a
    /// target that disappears, between the <c>lstat</c> and the <c>readlink</c>, and target text no POSIX
    /// link can hold. Production code leaves it <see langword="null"/>.
    /// </summary>
    internal static Func<string, string?>? LinkTargetReaderForTests { get; set; }

    /// <summary>
    /// Enables or disables Windows-style ordinal-ignore-case path comparison for tests.
    /// </summary>
    internal static void SetUseOrdinalIgnoreCasePathComparisonForTests(bool value)
    {
        _useOrdinalIgnoreCasePathComparisonForTests = value;
    }

    /// <summary>
    /// Enables or disables the Windows classification of a reparse point that names no other location
    /// (an ordinary entry rather than a link that changed under the walk) for tests.
    /// </summary>
    internal static void SetUseWindowsReparsePointClassificationForTests(bool value)
    {
        _useWindowsReparsePointClassificationForTests = value;
    }

    /// <summary>
    /// Restores all test seams to production defaults. Call from test teardown to avoid cross-test leakage.
    /// </summary>
    internal static void ResetTestSeams()
    {
        _useOrdinalIgnoreCasePathComparisonForTests = false;
        _useWindowsReparsePointClassificationForTests = false;
        ContainmentCheckObserverForTests = null;
        LinkTargetReaderForTests = null;
    }

    private static StringComparison PathComparison =>
        _useOrdinalIgnoreCasePathComparisonForTests || OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Whether a reparse point that names no other location is an ordinary entry. On Windows it is some
    /// other reparse point (a cloud placeholder, deduplicated data); on Unix every reparse point is a
    /// symbolic link, so a missing target means the entry changed under the walk.
    /// </summary>
    private static bool ReparsePointWithoutTargetIsOrdinaryEntry =>
        _useWindowsReparsePointClassificationForTests || OperatingSystem.IsWindows();

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
    /// Lexical prefix check plus the canonical-path rule: the canonical candidate (every existing component's
    /// symbolic links resolved and re-walked) must sit under the canonical workspace root. Covers paths whose
    /// leaf does not exist yet (write/create through a linked parent).
    /// </summary>
    /// <param name="workspaceRootFull">The workspace root, as the caller spells it.</param>
    /// <param name="candidateFull">The absolute candidate path.</param>
    /// <param name="resolvedFinalPath">
    /// <see langword="null"/> when the candidate does not exist. Otherwise the candidate itself, or, when the
    /// candidate's own last component is a symbolic link, the canonical location of what it points at,
    /// re-expressed under <paramref name="workspaceRootFull"/>'s spelling.
    /// </param>
    internal static bool IsPathUnderWorkspaceWithSymlinkCheck(
        string workspaceRootFull,
        string candidateFull,
        out string? resolvedFinalPath) =>
        IsPathUnderWorkspaceUnderLinkSemantics(
            workspaceRootFull,
            candidateFull,
            OperatingSystem.IsWindows(),
            out resolvedFinalPath);

    /// <summary>
    /// <see cref="IsPathUnderWorkspaceWithSymlinkCheck"/> with the link
    /// interpretation rule chosen explicitly, so the Windows dual-interpretation rule runs against real links
    /// on any host. Production always passes <see cref="OperatingSystem.IsWindows"/>.
    /// </summary>
    internal static bool IsPathUnderWorkspaceUnderLinkSemantics(
        string workspaceRootFull,
        string candidateFull,
        bool windowsLinkSemantics,
        out string? resolvedFinalPath)
    {
        resolvedFinalPath = null;

        string root;

        string candidate;

        try
        {
            root = Path.GetFullPath(workspaceRootFull);

            candidate = Path.GetFullPath(candidateFull);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException)
        {
            return false;
        }

        if (!IsPathUnderWorkspace(root, candidate))
        {
            return false;
        }

        ContainmentCheckObserverForTests?.Invoke(candidate);

        string relative = SuffixUnder(root, candidate);

        if (!TryCanonicalize(root, out string? canonicalRoot, out _))
        {
            return false;
        }

        int linksFollowed = 0;

        if (!TryResolveComponents(
                canonicalRoot,
                SplitComponents(relative),
                canonicalRoot,
                windowsLinkSemantics,
                ref linksFollowed,
                out string? canonicalCandidate,
                out bool exists))
        {
            return false;
        }

        if (!IsPathUnderWorkspace(canonicalRoot, canonicalCandidate))
        {
            return false;
        }

        if (!exists)
        {
            return true;
        }

        if (relative.Length == 0)
        {
            resolvedFinalPath = candidate;

            return true;
        }

        if (!TryClassifyNoFollow(candidate, out PathEntryKind leafKind, out _))
        {
            return false;
        }

        resolvedFinalPath = leafKind == PathEntryKind.SymbolicLink
            ? RebaseOntoRoot(root, canonicalRoot, canonicalCandidate)
            : candidate;

        return true;
    }

    /// <summary>
    /// Re-validates containment immediately before I/O to mitigate TOCTOU between resolution and use.
    /// </summary>
    internal static bool RevalidatePathBeforeIo(string workspaceRootFull, string absolutePath)
    {
        return IsPathUnderWorkspaceWithSymlinkCheck(workspaceRootFull, absolutePath, out _);
    }

    /// <summary>
    /// Reports whether an existing entry at or above <paramref name="directoryPath"/>, below the workspace root,
    /// stops that directory from being created: an entry that is not a directory, or a symbolic link that does
    /// not lead to one (for example an in-workspace link whose target does not exist, which <c>mkdir</c> never
    /// creates through). The canonical walk keeps the first missing component and everything after it
    /// lexically, so such a path passes containment and only the directory creation fails; writers use this to
    /// name that cause instead of a generic I/O error. It only observes and never decides containment.
    /// </summary>
    internal static bool HasEntryBlockingDirectoryCreation(string workspaceRootFull, string directoryPath)
    {
        string root;

        string candidate;

        try
        {
            root = Path.GetFullPath(workspaceRootFull);

            candidate = Path.GetFullPath(directoryPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException)
        {
            return false;
        }

        if (!IsPathUnderWorkspace(root, candidate))
        {
            return false;
        }

        string current = Path.TrimEndingDirectorySeparator(root);

        foreach (string part in SplitComponents(SuffixUnder(root, candidate)))
        {
            current = Path.Join(current, part);

            if (!TryClassifyNoFollow(current, out PathEntryKind kind, out _)
                || kind == PathEntryKind.Missing)
            {
                return false;
            }

            if (kind == PathEntryKind.NonDirectory
                || (kind == PathEntryKind.SymbolicLink && !Directory.Exists(current)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Post-open containment: asks the kernel where <paramref name="handle"/> lives and checks that path
    /// against the kernel's own path for the canonical workspace root. Neither side uses the string the
    /// handle was opened with, so a link swapped in after validation, or an object moved out of the
    /// workspace while open, is caught. Fails closed when either kernel path is unavailable.
    /// </summary>
    internal static bool IsOpenedHandleUnderWorkspace(string workspaceRootFull, SafeFileHandle handle) =>
        TryGetOpenedHandleRelativePath(workspaceRootFull, handle, out _);

    /// <summary>
    /// <see cref="IsOpenedHandleUnderWorkspace"/> that also says where under the workspace the kernel
    /// places the opened file: the path relative to the kernel's own spelling of the canonical root, which
    /// is the file's real location whatever name it was opened by. A caller that judges a path by its
    /// segments (the workspace indexer's eligibility rule) judges this one, so a link or an alias cannot
    /// present an excluded file under an acceptable name.
    /// </summary>
    /// <param name="workspaceRootFull">The workspace root, as the caller spells it.</param>
    /// <param name="handle">The opened file.</param>
    /// <param name="relativePath">
    /// The real location relative to the workspace root, or <see langword="null"/> when the handle is not
    /// under it or either kernel path is unavailable.
    /// </param>
    internal static bool TryGetOpenedHandleRelativePath(
        string workspaceRootFull,
        SafeFileHandle handle,
        [NotNullWhen(true)] out string? relativePath)
    {
        relativePath = null;

        if (!FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? handlePath))
        {
            return false;
        }

        string root;

        try
        {
            root = Path.GetFullPath(workspaceRootFull);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException)
        {
            return false;
        }

        if (!TryCanonicalize(root, out string? canonicalRoot, out bool rootExists)
            || !rootExists
            || !FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                canonicalRoot,
                out SafeFileHandle rootHandle,
                out _))
        {
            return false;
        }

        using (rootHandle)
        {
            if (!FileHandleIdentityInterop.TryGetHandleKernelPath(rootHandle, out string? kernelRoot)
                || !IsPathUnderWorkspace(kernelRoot, handlePath))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(kernelRoot, handlePath);

            return true;
        }
    }

    /// <summary>
    /// Canonicalises an absolute path: every existing component's symbolic links resolved and re-walked,
    /// the first missing component and everything after it kept lexically.
    /// </summary>
    internal static bool TryCanonicalize(
        string absolutePath,
        [NotNullWhen(true)] out string? canonicalPath,
        out bool exists)
    {
        canonicalPath = null;

        exists = false;

        // A fully qualified path always has a non-empty root, so this one check refuses both a relative path
        // and a Windows drive- or root-relative one.
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            return false;
        }

        string pathRoot = Path.GetPathRoot(absolutePath)!;

        return TryResolveComponents(
            pathRoot,
            SplitComponents(absolutePath[pathRoot.Length..]),
            out canonicalPath,
            out exists);
    }

    /// <summary>
    /// The part of <paramref name="candidate"/> below <paramref name="root"/>, without a leading separator;
    /// empty when they are the same path. Callers have already proven the lexical prefix.
    /// </summary>
    private static string SuffixUnder(string root, string candidate)
    {
        char sep = Path.DirectorySeparatorChar;

        string trimmedRoot = root.TrimEnd(sep);

        return candidate.Length <= trimmedRoot.Length
            ? string.Empty
            : candidate[trimmedRoot.Length..].TrimStart(sep);
    }

    /// <summary>
    /// Re-expresses a canonical path under the root as the caller spelled it, so callers that compute
    /// workspace-relative names from the result never see the canonical root's spelling (for example
    /// <c>/private/var</c> for a root given as <c>/var</c>).
    /// </summary>
    private static string RebaseOntoRoot(string root, string canonicalRoot, string canonicalPath)
    {
        string suffix = SuffixUnder(canonicalRoot, canonicalPath);

        string trimmedRoot = Path.TrimEndingDirectorySeparator(root);

        return suffix.Length == 0
            ? trimmedRoot
            : Path.Join(trimmedRoot, suffix);
    }

    private static string[] SplitComponents(string relative) =>
        relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Walks <paramref name="components"/> from <paramref name="start"/> (which must already be canonical)
    /// the way the kernel resolves a path: a symbolic link's target text replaces the link and is walked
    /// again component by component, from the filesystem root when the target is absolute and from the
    /// link's directory otherwise; <c>..</c> steps to the canonical parent. Fails closed after
    /// <see cref="MaxSymbolicLinkResolutions"/> links (loops included) or on any entry it cannot classify.
    /// </summary>
    private static bool TryResolveComponents(
        string start,
        IReadOnlyList<string> components,
        [NotNullWhen(true)] out string? resolved,
        out bool exists)
    {
        int linksFollowed = 0;

        return TryResolveComponents(
            start,
            components,
            containmentRoot: null,
            windowsLinkSemantics: false,
            ref linksFollowed,
            out resolved,
            out exists);
    }

    /// <param name="containmentRoot">
    /// The canonical root the result must stay under, or <see langword="null"/> while canonicalising the root
    /// itself (whose own links are the operator's choice). Only consulted by the Windows arm.
    /// </param>
    /// <param name="windowsLinkSemantics">
    /// <see langword="true"/> on Windows. The Windows I/O manager may join a relative reparse target to the
    /// link's directory and collapse <c>..</c> as text, where POSIX steps to the physical parent of whatever
    /// the walk has reached. For a relative target containing a <c>..</c> segment, the rest of the path is
    /// therefore also resolved under the textual interpretation, and both results must lie under
    /// <paramref name="containmentRoot"/>.
    /// </param>
    private static bool TryResolveComponents(
        string start,
        IReadOnlyList<string> components,
        string? containmentRoot,
        bool windowsLinkSemantics,
        ref int linksFollowed,
        [NotNullWhen(true)] out string? resolved,
        out bool exists)
    {
        resolved = null;

        exists = false;

        Stack<string> pending = new();

        PushReversed(pending, components);

        string current = start;

        bool currentIsDirectory = true;

        // Every component reaches the stack through SplitComponents, which drops empty entries.
        while (pending.TryPop(out string? part))
        {
            if (part == ".")
            {
                continue;
            }

            if (!currentIsDirectory)
            {
                // Nothing exists below a non-directory; the kernel answers ENOTDIR from here on.
                pending.Push(part);

                break;
            }

            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;

                continue;
            }

            string next = Path.Join(current, part);

            if (!TryClassifyNoFollow(next, out PathEntryKind kind, out string? linkTarget))
            {
                return false;
            }

            if (kind == PathEntryKind.Missing)
            {
                pending.Push(part);

                break;
            }

            if (kind == PathEntryKind.SymbolicLink)
            {
                if (++linksFollowed > MaxSymbolicLinkResolutions)
                {
                    return false;
                }

                if (Path.IsPathFullyQualified(linkTarget!))
                {
                    string targetRoot = Path.GetPathRoot(linkTarget)!;

                    current = targetRoot;

                    PushReversed(pending, SplitComponents(linkTarget![targetRoot.Length..]));
                }
                else
                {
                    // Relative: TryClassifyNoFollow refuses a Windows drive- or root-relative target.
                    string[] targetComponents = SplitComponents(linkTarget!);

                    if (windowsLinkSemantics
                        && containmentRoot is not null
                        && Array.IndexOf(targetComponents, "..") >= 0
                        && !IsTextualLinkInterpretationContained(
                            current,
                            linkTarget!,
                            pending,
                            containmentRoot,
                            ref linksFollowed))
                    {
                        return false;
                    }

                    PushReversed(pending, targetComponents);
                }

                currentIsDirectory = true;

                continue;
            }

            current = next;

            currentIsDirectory = kind == PathEntryKind.Directory;
        }

        if (pending.Count == 0)
        {
            resolved = current;

            exists = true;

            return true;
        }

        // The first missing component and everything after it stay lexical. Any ".." left in that tail can
        // only climb into current's own (already canonical) ancestors.
        try
        {
            resolved = Path.GetFullPath(
                Path.Join(current, string.Join(Path.DirectorySeparatorChar, pending)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException)
        {
            return false;
        }

        return true;
    }

    private static void PushReversed(Stack<string> pending, IReadOnlyList<string> components)
    {
        for (int index = components.Count - 1; index >= 0; index--)
        {
            pending.Push(components[index]);
        }
    }

    /// <summary>
    /// The textual interpretation of a relative link target: joined to the link's directory and collapsed
    /// as text. Pure, so the Windows rule can be pinned on any host.
    /// </summary>
    internal static string TextualLinkTargetInterpretation(string linkDirectory, string relativeLinkTarget) =>
        Path.GetFullPath(Path.Join(linkDirectory, relativeLinkTarget));

    /// <summary>
    /// Resolves the textual interpretation of a relative link target followed by the components still
    /// pending (in walk order), sharing the caller's link budget, and reports whether that path stays under
    /// <paramref name="containmentRoot"/>.
    /// </summary>
    private static bool IsTextualLinkInterpretationContained(
        string linkDirectory,
        string relativeLinkTarget,
        Stack<string> pending,
        string containmentRoot,
        ref int linksFollowed)
    {
        string textual;

        try
        {
            textual = TextualLinkTargetInterpretation(linkDirectory, relativeLinkTarget);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException)
        {
            return false;
        }

        // GetFullPath always answers a fully qualified path, so its root is never empty.
        string textualRoot = Path.GetPathRoot(textual)!;

        // Stack<T> enumerates top first, which is walk order.
        List<string> remaining = [.. SplitComponents(textual[textualRoot.Length..]), .. pending];

        return TryResolveComponents(
                textualRoot,
                remaining,
                containmentRoot,
                windowsLinkSemantics: true,
                ref linksFollowed,
                out string? resolvedTextual,
                out _)
            && IsPathUnderWorkspace(containmentRoot, resolvedTextual);
    }

    /// <summary>
    /// Classifies one path without following a symbolic link in its last component. Returns
    /// <see langword="false"/> (fail closed) when the entry exists but cannot be classified, including a
    /// symbolic link whose target text no walk can follow (see <see cref="IsFollowableLinkTarget"/>).
    /// </summary>
    private static bool TryClassifyNoFollow(
        string path,
        out PathEntryKind kind,
        out string? linkTarget)
    {
        kind = PathEntryKind.Missing;

        linkTarget = null;

        FileAttributes attributes;

        try
        {
            // lstat semantics: a link reports ReparsePoint whether or not its target exists.
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or SecurityException)
        {
            return false;
        }

        bool isDirectory = (attributes & FileAttributes.Directory) != 0;

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            try
            {
                linkTarget = ReadLinkTarget(path, isDirectory);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException
                    or SecurityException)
            {
                return false;
            }

            if (linkTarget is not null)
            {
                if (!IsFollowableLinkTarget(linkTarget))
                {
                    return false;
                }

                kind = PathEntryKind.SymbolicLink;

                return true;
            }

            if (!ReparsePointWithoutTargetIsOrdinaryEntry)
            {
                return false;
            }
        }

        kind = isDirectory ? PathEntryKind.Directory : PathEntryKind.NonDirectory;

        return true;
    }

    private static string? ReadLinkTarget(string path, bool isDirectory) =>
        LinkTargetReaderForTests is { } reader
            ? reader(path)
            : isDirectory
                ? new DirectoryInfo(path).LinkTarget
                : new FileInfo(path).LinkTarget;

    /// <summary>
    /// Whether the walk can follow a link with this target text: it is not empty, and it is either fully
    /// qualified or purely relative. A Windows drive- or root-relative target (<c>C:x</c>, <c>\x</c>)
    /// depends on process state, so it is refused; on Unix a rooted path is always fully qualified.
    /// </summary>
    private static bool IsFollowableLinkTarget(string linkTarget) =>
        linkTarget.Length != 0
        && (Path.IsPathFullyQualified(linkTarget) || !Path.IsPathRooted(linkTarget));

    private enum PathEntryKind
    {
        Missing,

        Directory,

        NonDirectory,

        SymbolicLink,
    }
}
