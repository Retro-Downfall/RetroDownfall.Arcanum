namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal enum PortablePathFlavour
{

    Unix = 0,

    Windows = 1,

}

/// <summary>
/// A rooted path parsed without reference to the running platform, so a Windows path recorded on
/// the source machine stays comparable while a Unix host is doing the restoring.
/// </summary>
/// <remarks>
/// <see cref="Path"/> resolves against the current process — <c>Path.GetFullPath(@"C:\x")</c> on
/// Unix yields a relative-to-cwd result and <c>Path.GetFullPath("/x")</c> on Windows adopts the
/// current drive. Cross-platform restore mapping needs the recorded path's own semantics, so roots
/// and separators are parsed here explicitly.
///
/// Deliberately not a record struct: synthesized equality would compare <see cref="Segments"/> by
/// reference, which reads as correct and silently is not. Comparison goes only through
/// <see cref="Equals(PortablePath, bool)"/>, which takes the case sensitivity the caller means.
/// </remarks>
internal readonly struct PortablePath
{

    private readonly string? _root;

    private readonly IReadOnlyList<string>? _segments;

    private PortablePath(
        PortablePathFlavour flavour,
        string root,
        IReadOnlyList<string> segments,
        bool hasTraversal)
    {

        Flavour = flavour;

        _root = root;

        _segments = segments;

        HasTraversal = hasTraversal;

    }

    public PortablePathFlavour Flavour { get; }

    /// <summary>The rendered root: <c>/</c>, <c>C:\</c>, or <c>\\server\share\</c>.</summary>
    public string Root => _root ?? string.Empty;

    /// <summary>Backed by a nullable field so <c>default(PortablePath)</c> is inert, not a crash.</summary>
    public IReadOnlyList<string> Segments => _segments ?? [];

    public bool HasTraversal { get; }

    /// <summary>Windows drive and UNC roots compare without regard to case; Unix roots do not.</summary>
    public bool CaseInsensitive => Flavour == PortablePathFlavour.Windows;

    private char Separator => Flavour == PortablePathFlavour.Windows ? '\\' : '/';

    public static bool TryParse(
        string? value,
        out PortablePath parsed)
    {

        parsed = default;

        if (string.IsNullOrWhiteSpace(value))
        {

            return false;

        }

        string trimmed = value.Trim();

        if (trimmed.Contains('\0', StringComparison.Ordinal))
        {

            return false;

        }

        PortablePathFlavour flavour;

        string root;

        string remainder;

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal) && LooksLikeUnc(trimmed))
        {

            string[] uncParts = trimmed[2..]
                .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

            if (uncParts.Length < 2)
            {

                return false;

            }

            flavour = PortablePathFlavour.Windows;

            root = @"\\" + uncParts[0] + @"\" + uncParts[1] + @"\";

            remainder = string.Join('\\', uncParts[2..]);

        }
        else if (trimmed.Length >= 3
                 && char.IsAsciiLetter(trimmed[0])
                 && trimmed[1] == ':'
                 && trimmed[2] is '\\' or '/')
        {

            flavour = PortablePathFlavour.Windows;

            root = char.ToUpperInvariant(trimmed[0]) + @":\";

            remainder = trimmed[3..];

        }
        else if (trimmed[0] == '/')
        {

            flavour = PortablePathFlavour.Unix;

            root = "/";

            remainder = trimmed[1..];

        }
        else
        {

            return false;

        }

        List<string> segments = [];

        bool hasTraversal = false;

        foreach (string segment in remainder.Split(
                     ['\\', '/'],
                     StringSplitOptions.RemoveEmptyEntries))
        {

            if (string.Equals(segment, ".", StringComparison.Ordinal))
            {

                continue;

            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {

                hasTraversal = true;

                continue;

            }

            segments.Add(segment);

        }

        parsed = new PortablePath(flavour, root, segments, hasTraversal);

        return true;

    }

    public bool Equals(PortablePath other, bool caseInsensitive)
    {

        StringComparison comparison = caseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (Flavour != other.Flavour
            || !string.Equals(Root, other.Root, comparison)
            || Segments.Count != other.Segments.Count)
        {

            return false;

        }

        for (int index = 0; index < Segments.Count; index++)
        {

            if (!string.Equals(Segments[index], other.Segments[index], comparison))
            {

                return false;

            }

        }

        return true;

    }

    /// <summary>Reports whether this path is <paramref name="root"/> itself or lies beneath it.</summary>
    public bool IsUnder(PortablePath root, bool caseInsensitive)
    {

        StringComparison comparison = caseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (Flavour != root.Flavour
            || !string.Equals(Root, root.Root, comparison)
            || Segments.Count < root.Segments.Count)
        {

            return false;

        }

        for (int index = 0; index < root.Segments.Count; index++)
        {

            if (!string.Equals(Segments[index], root.Segments[index], comparison))
            {

                return false;

            }

        }

        return true;

    }

    /// <summary>Renders this root followed by <paramref name="tail"/> using this path's separator.</summary>
    public string Append(IReadOnlyList<string> tail)
    {

        IEnumerable<string> combined = Segments.Concat(tail);

        return Root + string.Join(Separator, combined);

    }

    public override string ToString() => Append([]);

    private static bool LooksLikeUnc(string value) =>
        value[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Length >= 2;

}
