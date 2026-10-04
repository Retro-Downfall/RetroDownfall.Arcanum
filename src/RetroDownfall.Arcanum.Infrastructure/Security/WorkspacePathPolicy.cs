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
    /// Observation-only test hook, invoked once per <see cref="IsPathUnderWorkspaceWithSymlinkCheck"/> call
    /// that passes the lexical check, with the normalized candidate. It cannot change the outcome.
    /// </summary>
    internal static Action<string>? ContainmentCheckObserverForTests { get; set; }

    /// <summary>
    /// Enables or disables Windows-style ordinal-ignore-case path comparison for tests.
    /// </summary>
    internal static void SetUseOrdinalIgnoreCasePathComparisonForTests(bool value)
    {
        _useOrdinalIgnoreCasePathComparisonForTests = value;
    }

    /// <summary>
    /// Restores all test seams to production defaults. Call from test teardown to avoid cross-test leakage.
    /// </summary>
    internal static void ResetTestSeams()
    {
        _useOrdinalIgnoreCasePathComparisonForTests = false;
        ContainmentCheckObserverForTests = null;
    }

    private static StringComparison PathComparison =>
        _useOrdinalIgnoreCasePathComparisonForTests || OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

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

        if (!TryResolveComponents(
                canonicalRoot,
                SplitComponents(relative),
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
    /// Post-open containment: asks the kernel where <paramref name="handle"/> lives and checks that path
    /// against the kernel's own path for the canonical workspace root. Neither side uses the string the
    /// handle was opened with, so a link swapped in after validation, or an object moved out of the
    /// workspace while open, is caught. Fails closed when either kernel path is unavailable.
    /// </summary>
    internal static bool IsOpenedHandleUnderWorkspace(string workspaceRootFull, SafeFileHandle handle)
    {
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
            if (!FileHandleIdentityInterop.TryGetHandleKernelPath(rootHandle, out string? kernelRoot))
            {
                return false;
            }

            return IsPathUnderWorkspace(kernelRoot, handlePath);
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

        string? pathRoot = Path.GetPathRoot(absolutePath);

        if (string.IsNullOrEmpty(pathRoot) || !Path.IsPathFullyQualified(absolutePath))
        {
            return false;
        }

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
        resolved = null;

        exists = false;

        Stack<string> pending = new();

        PushReversed(pending, components);

        string current = start;

        bool currentIsDirectory = true;

        int linksFollowed = 0;

        while (pending.TryPop(out string? part))
        {
            if (part.Length == 0 || part == ".")
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
                else if (Path.IsPathRooted(linkTarget))
                {
                    // A Windows drive- or root-relative target depends on process state; refuse it.
                    return false;
                }
                else
                {
                    PushReversed(pending, SplitComponents(linkTarget!));
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
    /// Classifies one path without following a symbolic link in its last component. Returns
    /// <see langword="false"/> (fail closed) when the entry exists but cannot be classified.
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
                linkTarget = isDirectory
                    ? new DirectoryInfo(path).LinkTarget
                    : new FileInfo(path).LinkTarget;
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
                if (linkTarget.Length == 0)
                {
                    return false;
                }

                kind = PathEntryKind.SymbolicLink;

                return true;
            }

            // On Unix every reparse point is a symbolic link, so a missing target means the entry changed
            // under us. On Windows it is some other reparse point (a cloud placeholder, deduplicated data)
            // that names no other location.
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        kind = isDirectory ? PathEntryKind.Directory : PathEntryKind.NonDirectory;

        return true;
    }

    private enum PathEntryKind
    {
        Missing,

        Directory,

        NonDirectory,

        SymbolicLink,
    }
}
