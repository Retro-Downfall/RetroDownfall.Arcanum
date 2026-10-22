using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>One internal Open Packaging Conventions relationship, with its target already resolved to a package entry name.</summary>
internal sealed record OpcRelationship(string Id, string Type, string TargetEntryName);

/// <summary>
/// Reads Open Packaging Conventions relationship parts (<c>_rels/.rels</c> and <c>&lt;folder&gt;/_rels/&lt;part&gt;.rels</c>)
/// and resolves their targets to package entry names. This is how OOXML and Visio packages are walked: parts are
/// reached only through relationships, never by scanning the package.
/// </summary>
/// <remarks>
/// External targets are omitted and never followed. A target is resolved lexically (relative to the source part's
/// folder, or the package root for a leading slash, with <c>.</c> and <c>..</c> segments normalized); one that escapes
/// the root, or that could never be a valid entry name, is refused. Resolution does not check that the target exists:
/// <see cref="ResolveEntry"/> does, case-insensitively and with one percent-unescape.
/// </remarks>
internal static class OpcPackageRelationships
{
    internal const int MaximumRelationships = 8192;

    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    private const string TransitionalPrefix = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    private const string StrictPrefix = "http://purl.oclc.org/ooxml/officeDocument/relationships/";

    private const string VisioPrefix = "http://schemas.microsoft.com/visio/2010/relationships/";

    /// <summary>The relationship part for a source part: <c>""</c> (the package root) is <c>_rels/.rels</c>.</summary>
    public static string RelationshipEntryName(string sourceEntryName)
    {
        if (sourceEntryName.Length == 0)
        {
            return "_rels/.rels";
        }

        int slash = sourceEntryName.LastIndexOf('/');

        return slash < 0
            ? $"_rels/{sourceEntryName}.rels"
            : $"{sourceEntryName[..(slash + 1)]}_rels/{sourceEntryName[(slash + 1)..]}.rels";
    }

    /// <summary>
    /// Reads the internal relationships of <paramref name="sourceEntryName"/> (<c>""</c> for the package root). An absent
    /// relationship part is an empty list.
    /// </summary>
    public static IReadOnlyList<OpcRelationship> Read(BoundedZipPackage package, string sourceEntryName, CancellationToken cancellationToken)
    {
        string relationshipEntry = RelationshipEntryName(sourceEntryName);

        if (!package.Contains(relationshipEntry))
        {
            return [];
        }

        using BoundedXmlReader xml = BoundedXmlReader.Create(package.ReadEntry(relationshipEntry, cancellationToken), cancellationToken);

        List<OpcRelationship> relationships = [];

        HashSet<string> ids = new(StringComparer.Ordinal);

        int seen = 0;

        while (xml.Read())
        {
            XmlReader reader = xml.Reader;

            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName != "Relationship"
                || reader.NamespaceURI != RelationshipsNamespace)
            {
                continue;
            }

            if (++seen > MaximumRelationships)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
            }

            string? id = reader.GetAttribute("Id");

            string? type = reader.GetAttribute("Type");

            string? target = reader.GetAttribute("Target");

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type) || string.IsNullOrEmpty(target))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
            }

            if (!ids.Add(id))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
            }

            if (string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            relationships.Add(new OpcRelationship(id, type, ResolveTarget(sourceEntryName, target)));
        }

        return relationships;
    }

    /// <summary>
    /// The entry that <paramref name="targetEntryName"/> names, or null: the literal name when the package has it,
    /// otherwise the name after one percent-unescape. Lookup is case-insensitive, per OPC.
    /// </summary>
    public static string? ResolveEntry(BoundedZipPackage package, string targetEntryName)
    {
        if (package.Contains(targetEntryName))
        {
            return targetEntryName;
        }

        string unescaped = Uri.UnescapeDataString(targetEntryName);

        return !string.Equals(unescaped, targetEntryName, StringComparison.Ordinal) && package.Contains(unescaped) ? unescaped : null;
    }

    /// <summary>True when <paramref name="type"/> is the transitional or strict officeDocument relationship <paramref name="localName"/>.</summary>
    public static bool IsOfficeDocumentType(string type, string localName) =>
        MatchesPrefixed(type, TransitionalPrefix, localName) || MatchesPrefixed(type, StrictPrefix, localName);

    /// <summary>True when <paramref name="type"/> is the Visio 2010 relationship <paramref name="localName"/>.</summary>
    public static bool IsVisioType(string type, string localName) => MatchesPrefixed(type, VisioPrefix, localName);

    private static bool MatchesPrefixed(string type, string prefix, string localName) =>
        type.Length == prefix.Length + localName.Length
        && type.StartsWith(prefix, StringComparison.Ordinal)
        && type.AsSpan(prefix.Length).SequenceEqual(localName);

    private static string ResolveTarget(string sourceEntryName, string target)
    {
        int fragment = target.IndexOf('#');

        if (fragment >= 0)
        {
            target = target[..fragment];
        }

        if (target.Length == 0)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
        }

        foreach (char character in target)
        {
            if (character < 0x20 || character == 0x7F || character == '\\' || character == ':')
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.UnsafeEntryName);
            }
        }

        List<string> segments = [];

        string relative = target;

        if (target[0] == '/')
        {
            relative = target[1..];
        }
        else
        {
            int lastSlash = sourceEntryName.LastIndexOf('/');

            if (lastSlash > 0)
            {
                segments.AddRange(sourceEntryName[..lastSlash].Split('/'));
            }
        }

        foreach (string segment in relative.Split('/'))
        {
            if (segment.Length == 0)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.UnsafeEntryName);
            }

            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new DocumentPackageRejectedException(DocumentPackageRejection.UnsafeEntryName);
                }

                segments.RemoveAt(segments.Count - 1);

                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.UnsafeEntryName);
        }

        return string.Join('/', segments);
    }
}
