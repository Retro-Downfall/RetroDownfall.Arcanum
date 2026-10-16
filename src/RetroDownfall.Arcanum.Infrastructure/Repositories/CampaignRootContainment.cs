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
/// does not apply, so the answer is the file system's whenever it can give one:
/// <list type="bullet">
/// <item>the physical identity (volume and file identifier) of the directory the registered root reaches, against the identity of every
/// existing directory above the working directory. It does not depend on any spelling, and it is exact on every volume: two names that
/// differ only by case are one directory on a volume that folds case and two on one that does not, and the identities say which;</item>
/// <item>when either side cannot be asked (the root is not on disk, or the working directory is too long for any file system), the text of
/// both, with trailing separators dropped, in Unicode normalisation form C, compared by whole directory components and without regard to
/// case where the platform's file systems fold case, over the spelling as given and over the symbolic-link-resolved spelling of each side.
/// That comparison errs toward containment (a case sibling on a case-sensitive volume is taken for the root), so it is used only where the
/// file system has no answer and never once it has one;</item>
/// <item>for a path read as it is spelled, with no link in it followed, the identity of each directory the spelling names (its last
/// component read without following it) against the identity of the directory a root reaches. A case or normalisation fold between the
/// text of the path and the text of a root is judged by it: the fold is dropped when the two are shown to be different directories, as a
/// case sibling is on a case-sensitive volume, and a root whose directory is none of those the spelling names is settled by its own text
/// without asking about its resolved spelling. The text decides alone only where one side cannot be asked: a root that is not on disk, or
/// a prefix of the path that does not exist or is itself a link.</item>
/// </list>
/// A directory's physical identity is read when it is asked for, never stored: a stored copy would go stale the moment a link is repointed
/// or a directory is replaced, and a stale answer here is a Sanctum that silently does not apply. There is no prefix predicate (a root
/// <c>/work/app</c> does not contain <c>/work/app-legacy</c>), and no limit on how many components deep a directory may be: nothing here
/// turns a long path into "no Campaign". A path longer than any file system can hold names no directory that exists, so for it only the
/// text is compared.
///
/// <para>Asking about one registered root costs one query of the file system (the root is followed to the directory it reaches), because
/// every turn asks about every registered root and most of them are unrelated to the directory it stands in. A path read as it is spelled
/// costs one more query for each directory its own spelling names, once, and then one query per root as well; the walk of a root's own
/// components (to find the spelling with its links resolved) is made only for a root whose directory is one of those the spelling names, or
/// that cannot be queried because it is not on disk.</para>
/// </remarks>
internal sealed class CampaignRootContainment
{
    /// <summary>The longest path any supported file system can hold (Windows long paths). Longer spellings are compared as text only.</summary>
    private const int MaxExistingPathCharacters = 32_767;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private readonly string _exactKey;

    private readonly string? _exactCanonicalKey;

    private readonly string _logicalKey;

    private readonly string? _canonicalKey;

    private readonly Dictionary<FileHandleIdentity, int>? _identities;

    private readonly SpelledAncestor[]? _spelled;

    private CampaignRootContainment(
        string exactKey,
        string? exactCanonicalKey,
        string logicalKey,
        string? canonicalKey,
        Dictionary<FileHandleIdentity, int>? identities,
        SpelledAncestor[]? spelled)
    {
        _exactKey = exactKey;

        _exactCanonicalKey = exactCanonicalKey;

        _logicalKey = logicalKey;

        _canonicalKey = canonicalKey;

        _identities = identities;

        _spelled = spelled;
    }

    /// <summary>
    /// A directory the spelling of a path names, <c>Text</c> being that prefix as written. <c>Identity</c> is the identity of what the last
    /// component of the prefix is on disk, read without following it, and is <see langword="null"/> unless that is a directory: a prefix
    /// that does not exist, a file and a link have none, so the file system has nothing to say against a text match with them.
    /// </summary>
    private readonly record struct SpelledAncestor(string Text, FileHandleIdentity? Identity);

    /// <summary>Whether this platform's file systems usually fold case, so a root and a differently cased spelling are probably one directory.</summary>
    private static bool FoldsCase => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private static StringComparison Comparison => FoldsCase
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// The working directory as a path the operating system can resolve, or <see langword="null"/> when it cannot be spelled as one.
    /// <paramref name="followLinks"/> false reads the path as it is spelled instead: no link in it is followed, so its own text is what is
    /// compared, with the roots' spellings (their links resolved) but not with the path's. A link inside a root then counts as inside it
    /// whatever it points at, and a link to a root does not. A case or normalisation fold between that text and a root is judged per
    /// volume, because a fold the file system does not make would name a case sibling as the root, and a case sibling that outranks the
    /// real Campaign judges a file it does not own: the fold stands only while the file system cannot show the two to be different
    /// directories (the root is not on disk, or the prefix does not exist or is a link).
    /// </summary>
    internal static CampaignRootContainment? TryCreate(string path, bool followLinks = true)
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

        string exactKey = ExactKey(full);

        string logicalKey = TextKey(full);

        if (!followLinks
            || full.Length > MaxExistingPathCharacters
            || !WorkspacePathPolicy.TryCanonicalize(full, out string? canonical, out _))
        {
            return new CampaignRootContainment(
                exactKey,
                exactCanonicalKey: null,
                logicalKey,
                canonicalKey: null,
                identities: null,
                spelled: followLinks ? null : ReadSpelledAncestors(full));
        }

        string exactCanonicalKey = ExactKey(canonical);

        string canonicalKey = TextKey(canonical);

        return new CampaignRootContainment(
            exactKey,
            string.Equals(exactCanonicalKey, exactKey, StringComparison.Ordinal) ? null : exactCanonicalKey,
            logicalKey,
            string.Equals(canonicalKey, logicalKey, StringComparison.Ordinal) ? null : canonicalKey,
            ReadAncestorIdentities(canonical),
            spelled: null);
    }

    /// <summary>A path as written, with trailing separators dropped: two paths with the same exact key are the same text.</summary>
    internal static string ExactKey(string path) => path.Trim().TrimEnd(Separators);

    /// <summary>
    /// A path as a key for comparing it with another path: trailing separators dropped and the Unicode normalisation form fixed. Case is
    /// left to the comparison, which knows the platform.
    /// </summary>
    internal static string TextKey(string path)
    {
        string trimmed = ExactKey(path);

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

    /// <summary>
    /// Whether two paths are the same registration. The same text is; spellings that differ only by case or Unicode normalisation are one
    /// directory on a volume that folds them and two on one that does not, so they are the same registration unless the file system shows
    /// them to be two directories.
    /// </summary>
    internal static bool SameText(string left, string right)
    {
        if (string.Equals(ExactKey(left), ExactKey(right), StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(TextKey(left), TextKey(right), Comparison)
            && !AreDifferentDirectories(left, right);
    }

    /// <summary>
    /// How many components below <paramref name="registeredRoot"/> the working directory stands (0 for the root itself), or
    /// <see langword="null"/> when it does not stand below it.
    /// </summary>
    internal int? DepthBelow(string registeredRoot)
    {
        string rootExact = ExactKey(registeredRoot);

        // The same text is the same directory, whatever the file system folds.
        int? depth = Min(
            TextDepth(_exactKey, rootExact, StringComparison.Ordinal),
            TextDepth(_exactCanonicalKey, rootExact, StringComparison.Ordinal));

        if (_identities is not null && TryReadDirectoryIdentity(registeredRoot, out FileHandleIdentity rootIdentity))
        {
            // Both sides are on disk, so the file system has the last word. A case sibling on a case-sensitive volume is a different
            // directory with a different identity, and no text comparison gets to say otherwise.
            return _identities.TryGetValue(rootIdentity, out int identityDepth)
                ? Min(depth, identityDepth)
                : depth;
        }

        return Min(depth, FoldedTextDepth(registeredRoot));
    }

    /// <summary>
    /// The registered root's own spelling of a path that reaches the same directory under another one, or <see langword="null"/> when the
    /// path does not stand below the directory the root reaches.
    /// </summary>
    /// <remarks>
    /// The path is canonicalised (every symbolic link along it resolved) and its existing ancestors are asked for their physical identity,
    /// so the answer is the file system's: the root reached through a link, by another case or normalisation on a volume that folds them,
    /// or physically, is found; a case sibling on a volume that does not fold case, a name-prefix sibling, and anything an in-root link
    /// leads out to are not. The caller still judges the returned path with the ordinary containment rule, so this widens which
    /// spelling reaches the root and never what the root contains.
    /// </remarks>
    internal static string? RebaseOntoRoot(string registeredRoot, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(registeredRoot);

        ArgumentNullException.ThrowIfNull(absolutePath);

        string rootFull;

        string candidateFull;

        try
        {
            rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(registeredRoot.Trim()));

            candidateFull = Path.GetFullPath(absolutePath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (candidateFull.Length > MaxExistingPathCharacters
            || !TryReadDirectoryIdentity(rootFull, out FileHandleIdentity rootIdentity)
            || !WorkspacePathPolicy.TryCanonicalize(candidateFull, out string? canonical, out _))
        {
            return null;
        }

        List<string> below = [];

        string? cursor = canonical;

        while (cursor is not null)
        {
            if (FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(cursor, out FileHandleMetadata metadata)
                && metadata.Kind == FileSystemObjectKind.Directory
                && metadata.Identity == rootIdentity)
            {
                below.Reverse();

                return below.Count == 0
                    ? rootFull
                    : Path.Join(rootFull, string.Join(Path.DirectorySeparatorChar, below));
            }

            string name = Path.GetFileName(cursor);

            string? parent = Path.GetDirectoryName(cursor);

            if (string.IsNullOrEmpty(parent) || string.Equals(parent, cursor, StringComparison.Ordinal) || name.Length == 0)
            {
                return null;
            }

            below.Add(name);

            cursor = parent;
        }

        return null;
    }

    /// <summary>
    /// The comparison of last resort, used while the file system cannot say whether two spellings are one directory. For a path read as it
    /// is spelled it still asks the file system what it can: a fold-only match is dropped when the prefix and the root are shown to be two
    /// directories, and a root whose directory is none of the spelling's own is not walked.
    /// </summary>
    private int? FoldedTextDepth(string registeredRoot)
    {
        string rootKey = TextKey(registeredRoot);

        // One following query for the root. A spelled path is the only one with directories to compare it with.
        FileHandleIdentity? rootIdentity = _spelled is not null && TryReadDirectoryIdentity(registeredRoot, out FileHandleIdentity reached)
            ? reached
            : null;

        int? depth = Judged(
            Min(TextDepth(_logicalKey, rootKey, Comparison), TextDepth(_canonicalKey, rootKey, Comparison)),
            ExactKey(registeredRoot),
            rootIdentity);

        // The spelling of the root with its links resolved can place the path below it only if the root's directory is one the spelling
        // names: a spelled ancestor that has the root's resolved spelling has only real directories along it, so reading it without
        // following the last component reaches the root's own directory. Any other root is settled by its own text, without the walk of
        // each component of it that canonicalising the root costs.
        if (rootIdentity is { } identity && !Names(identity))
        {
            return depth;
        }

        if (registeredRoot.Length > MaxExistingPathCharacters
            || !Path.IsPathFullyQualified(registeredRoot)
            || !WorkspacePathPolicy.TryCanonicalize(registeredRoot.Trim(), out string? rootCanonical, out _))
        {
            return depth;
        }

        string rootCanonicalKey = TextKey(rootCanonical);

        if (string.Equals(rootCanonicalKey, rootKey, StringComparison.Ordinal))
        {
            return depth;
        }

        return Min(
            depth,
            Judged(
                Min(
                    TextDepth(_logicalKey, rootCanonicalKey, Comparison),
                    TextDepth(_canonicalKey, rootCanonicalKey, Comparison)),
                ExactKey(rootCanonical),
                rootIdentity));
    }

    /// <summary>
    /// <paramref name="depth"/> unless the file system shows it to be a fold the volume does not make. The text of the spelled path
    /// <paramref name="depth"/> components up equal to <paramref name="rootText"/> is the same text and stands; text that equals it only
    /// by case or normalisation names the same directory on a volume that folds it and a different one on a volume that does not, and the
    /// answer is the file system's whenever both sides can be asked: the prefix read without following its last component is a directory
    /// of another identity than the one the root reaches. A prefix that does not exist or is a link, a root that is not on disk, and a
    /// path that is not read as it is spelled have nothing to compare, so the text decides.
    /// </summary>
    private int? Judged(int? depth, string rootText, FileHandleIdentity? rootIdentity)
    {
        if (depth is not { } components || rootIdentity is not { } root || _spelled is not { } spelled || components >= spelled.Length)
        {
            return depth;
        }

        SpelledAncestor prefix = spelled[components];

        return string.Equals(prefix.Text, rootText, StringComparison.Ordinal) || prefix.Identity is not { } identity || identity == root
            ? depth
            : null;
    }

    /// <summary>Whether <paramref name="identity"/> is that of a directory the spelling of the path names (see <see cref="SpelledAncestor"/>).</summary>
    private bool Names(FileHandleIdentity identity)
    {
        foreach (SpelledAncestor ancestor in _spelled!)
        {
            if (ancestor.Identity == identity)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The identity of the directory <paramref name="path"/> reaches, with every link along it followed, in one query of the file system;
    /// <see langword="false"/> when it does not reach a directory.
    /// </summary>
    private static bool TryReadDirectoryIdentity(string path, out FileHandleIdentity identity)
    {
        identity = default;

        if (path.Length > MaxExistingPathCharacters
            || !Path.IsPathFullyQualified(path)
            || !FileHandleIdentityInterop.TryGetPathMetadataIgnoringTestSeam(path.Trim(), out FileHandleMetadata metadata)
            || metadata.Kind != FileSystemObjectKind.Directory)
        {
            return false;
        }

        identity = metadata.Identity;

        return true;
    }

    /// <summary>Whether the file system shows two paths to be two directories. Two paths it cannot ask about are not shown to differ.</summary>
    private static bool AreDifferentDirectories(string left, string right) =>
        TryReadDirectoryIdentity(left, out FileHandleIdentity leftIdentity)
        && TryReadDirectoryIdentity(right, out FileHandleIdentity rightIdentity)
        && leftIdentity != rightIdentity;

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

    /// <summary>
    /// The path and each of its ancestors as it is spelled, nearest first (so an entry's index is how many components it is above the
    /// path), each with the identity of what its last component is on disk when that is a directory. The query reads that component
    /// without following it and so never leaves the spelling; a longer path than any file system can hold names nothing on disk, so it
    /// has none.
    /// </summary>
    private static SpelledAncestor[]? ReadSpelledAncestors(string full)
    {
        if (full.Length > MaxExistingPathCharacters)
        {
            return null;
        }

        List<SpelledAncestor> ancestors = [];

        string? cursor = Path.TrimEndingDirectorySeparator(full);

        while (cursor is not null)
        {
            FileHandleIdentity? identity = FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(cursor, out FileHandleMetadata metadata)
                && metadata.Kind == FileSystemObjectKind.Directory
                    ? metadata.Identity
                    : null;

            ancestors.Add(new SpelledAncestor(ExactKey(cursor), identity));

            string? parent = Path.GetDirectoryName(cursor);

            cursor = string.IsNullOrEmpty(parent) || string.Equals(parent, cursor, StringComparison.Ordinal)
                ? null
                : parent;
        }

        return [.. ancestors];
    }

    /// <summary>How many whole components <paramref name="subject"/> has below <paramref name="root"/>, or null when it is not below it.</summary>
    private static int? TextDepth(string? subject, string root, StringComparison comparison)
    {
        if (subject is null
            || subject.Length < root.Length
            || !subject.StartsWith(root, comparison))
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
