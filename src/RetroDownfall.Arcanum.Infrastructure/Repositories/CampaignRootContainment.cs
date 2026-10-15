using System.Text;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// Answers, for one working directory, how many components below a registered Campaign root it stands, or that it does not stand below
/// that root at all.
/// </summary>
/// <remarks>
/// A registered root is text the operator typed, and a working directory is text the operating system reported (<c>getcwd</c>), so the two
/// can name one directory and still differ: through a symbolic link (<c>/tmp</c> is <c>/private/tmp</c>, <c>/var</c> is
/// <c>/private/var</c>), by case on a case-insensitive volume (including the case of non-ASCII letters), by the Unicode normalisation form of
/// an accented name, and by a trailing separator. Comparing the two as text misses all of them, and a miss here is a Campaign whose Sanctum
/// does not apply, so the comparison is made on every form a directory can be spelled in:
/// <list type="bullet">
/// <item>the text of both, with trailing separators dropped, in Unicode normalisation form C, compared by whole directory components and
/// without regard to case where the platform's file systems fold case;</item>
/// <item>the same comparison over the symbolic-link-resolved spelling of each side;</item>
/// <item>the physical identity (volume and file identifier) of the root against the identity of every existing directory above the working
/// directory, which does not depend on any spelling at all.</item>
/// </list>
/// A directory's physical identity is read when it is asked for, never stored: a stored copy would go stale the moment a link is repointed
/// or a directory is replaced, and a stale answer here is a Sanctum that silently does not apply. There is no prefix predicate (a root
/// <c>/work/app</c> does not contain <c>/work/app-legacy</c>), and no limit on how many components deep a directory may be: nothing here
/// turns a long path into "no Campaign". A path longer than any file system can hold names no directory that exists, so for it only the
/// text is compared.
/// </remarks>
internal sealed class CampaignRootContainment
{
    /// <summary>The longest path any supported file system can hold (Windows long paths). Longer spellings are compared as text only.</summary>
    private const int MaxExistingPathCharacters = 32_767;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private readonly string _logicalKey;

    private readonly string? _canonicalKey;

    private readonly Dictionary<FileHandleIdentity, int>? _identities;

    private CampaignRootContainment(
        string logicalKey,
        string? canonicalKey,
        Dictionary<FileHandleIdentity, int>? identities)
    {
        _logicalKey = logicalKey;

        _canonicalKey = canonicalKey;

        _identities = identities;
    }

    /// <summary>Whether this platform's file systems fold case, so a root and a differently cased spelling are one directory.</summary>
    private static bool FoldsCase => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private static StringComparison Comparison => FoldsCase
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>The working directory as a path the operating system can resolve, or <see langword="null"/> when it cannot be spelled as one.</summary>
    internal static CampaignRootContainment? TryCreate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string full;

        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string logicalKey = TextKey(full);

        if (full.Length > MaxExistingPathCharacters
            || !WorkspacePathPolicy.TryCanonicalize(full, out string? canonical, out _))
        {
            return new CampaignRootContainment(logicalKey, canonicalKey: null, identities: null);
        }

        string canonicalKey = TextKey(canonical);

        return new CampaignRootContainment(
            logicalKey,
            string.Equals(canonicalKey, logicalKey, StringComparison.Ordinal) ? null : canonicalKey,
            ReadAncestorIdentities(canonical));
    }

    /// <summary>
    /// A path as a key for comparing it with another path: trailing separators dropped and the Unicode normalisation form fixed. Case is
    /// left to the comparison, which knows the platform.
    /// </summary>
    internal static string TextKey(string path)
    {
        string trimmed = path.Trim().TrimEnd(Separators);

        if (trimmed.AsSpan().IndexOfAnyExceptInRange('\0', '\u007F') < 0)
        {
            return trimmed;
        }

        try
        {
            return trimmed.Normalize(NormalizationForm.FormC);
        }
        catch (Exception exception) when (exception is ArgumentException or PlatformNotSupportedException)
        {
            return trimmed;
        }
    }

    /// <summary>Whether two paths are the same registration by their text: the same up to trailing separators, normalisation and platform case.</summary>
    internal static bool SameText(string left, string right) =>
        string.Equals(TextKey(left), TextKey(right), Comparison);

    /// <summary>
    /// How many components below <paramref name="registeredRoot"/> the working directory stands (0 for the root itself), or
    /// <see langword="null"/> when it does not stand below it.
    /// </summary>
    internal int? DepthBelow(string registeredRoot)
    {
        string rootKey = TextKey(registeredRoot);

        int? depth = Min(TextDepth(_logicalKey, rootKey), TextDepth(_canonicalKey, rootKey));

        if (registeredRoot.Length > MaxExistingPathCharacters
            || !Path.IsPathFullyQualified(registeredRoot)
            || !WorkspacePathPolicy.TryCanonicalize(registeredRoot.Trim(), out string? rootCanonical, out bool rootExists))
        {
            return depth;
        }

        string rootCanonicalKey = TextKey(rootCanonical);

        if (!string.Equals(rootCanonicalKey, rootKey, StringComparison.Ordinal))
        {
            depth = Min(depth, Min(TextDepth(_logicalKey, rootCanonicalKey), TextDepth(_canonicalKey, rootCanonicalKey)));
        }

        if (rootExists
            && _identities is not null
            && FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(rootCanonical, out FileHandleMetadata metadata)
            && metadata.Kind == FileSystemObjectKind.Directory
            && _identities.TryGetValue(metadata.Identity, out int identityDepth))
        {
            depth = Min(depth, identityDepth);
        }

        return depth;
    }

    /// <summary>
    /// The identity of each existing directory from <paramref name="canonical"/> up to the file system root, with how far above the
    /// working directory it is. The path has its links resolved, so every step is a real directory and a no-follow query is the right one.
    /// </summary>
    private static Dictionary<FileHandleIdentity, int> ReadAncestorIdentities(string canonical)
    {
        Dictionary<FileHandleIdentity, int> identities = [];

        string? cursor = canonical;

        int depth = 0;

        while (cursor is not null)
        {
            if (FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(cursor, out FileHandleMetadata metadata)
                && metadata.Kind == FileSystemObjectKind.Directory)
            {
                _ = identities.TryAdd(metadata.Identity, depth);
            }

            string? parent = Path.GetDirectoryName(cursor);

            cursor = string.IsNullOrEmpty(parent) || string.Equals(parent, cursor, StringComparison.Ordinal)
                ? null
                : parent;

            depth++;
        }

        return identities;
    }

    /// <summary>How many whole components <paramref name="subject"/> has below <paramref name="root"/>, or null when it is not below it.</summary>
    private static int? TextDepth(string? subject, string root)
    {
        if (subject is null
            || subject.Length < root.Length
            || !subject.StartsWith(root, Comparison))
        {
            return null;
        }

        if (subject.Length == root.Length)
        {
            return 0;
        }

        // The root must end on a component boundary: /work/app does not contain /work/app-legacy.
        if (Array.IndexOf(Separators, subject[root.Length]) < 0)
        {
            return null;
        }

        int depth = 0;

        bool inComponent = false;

        for (int index = root.Length; index < subject.Length; index++)
        {
            if (Array.IndexOf(Separators, subject[index]) >= 0)
            {
                inComponent = false;
            }
            else if (!inComponent)
            {
                depth++;

                inComponent = true;
            }
        }

        return depth;
    }

    private static int? Min(int? left, int? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return Math.Min(left.Value, right.Value);
    }
}
