using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The declared contents of an OpenDocument package, read from <c>META-INF/manifest.xml</c>. The manifest, not the
/// <c>mimetype</c> entry, decides the format: its root entry must carry the expected media type, <c>content.xml</c>
/// must be declared and present, and any encryption data refuses the package.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is read with <c>DtdProcessing.Ignore</c> rather than <c>Prohibit</c> (plan decision D6): macOS
/// <c>textutil</c> writes <c>&lt;!DOCTYPE manifest:manifest PUBLIC "-//OpenOffice.org//DTD Manifest 1.0//EN"
/// "Manifest.dtd"&gt;</c> into current ODF packages, so prohibiting it would reject every ODT that macOS produces.
/// <c>Ignore</c> processes no declaration, so no entity can be declared, expanded or resolved; the manifest's values
/// are only compared against closed path and media-type sets.
/// </para>
/// <para>
/// Content parts (<c>content.xml</c>, <c>styles.xml</c>) are never read here and keep <c>Prohibit</c>.
/// </para>
/// </remarks>
internal sealed class OpenDocumentManifest
{
    internal const string EntryName = "META-INF/manifest.xml";

    internal const int MaximumEntries = 8192;

    private const string ManifestNamespace = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    private const string ContentEntryName = "content.xml";

    private readonly HashSet<string> _paths;

    private OpenDocumentManifest(HashSet<string> paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// Reads and validates the manifest of <paramref name="package"/> against <paramref name="expectedMediaType"/>
    /// (the registry media type of the declared format).
    /// </summary>
    public static OpenDocumentManifest Read(BoundedZipPackage package, string expectedMediaType, CancellationToken cancellationToken)
    {
        if (!package.Contains(EntryName))
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
        }

        using BoundedXmlReader xml = BoundedXmlReader.Create(
            package.ReadEntry(EntryName, cancellationToken),
            cancellationToken,
            ignoreDocumentType: true);

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

        bool sawRoot = false;

        bool rootsMatch = true;

        int entries = 0;

        while (xml.Read())
        {
            XmlReader reader = xml.Reader;

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "encryption-data" || string.Equals(reader.LocalName, "keyinfo", StringComparison.OrdinalIgnoreCase))
            {
                // Matched by local name in any namespace: an encrypted package must never be mistaken for a readable one.
                throw new DocumentPackageRejectedException(DocumentPackageRejection.Encrypted);
            }

            if (reader.LocalName != "file-entry" || reader.NamespaceURI != ManifestNamespace)
            {
                continue;
            }

            if (++entries > MaximumEntries)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
            }

            string? fullPath = reader.GetAttribute("full-path", ManifestNamespace);

            if (fullPath is null)
            {
                continue;
            }

            paths.Add(fullPath);

            if (fullPath == "/")
            {
                // Every root entry must agree: two contradictory roots are ambiguous, so they are refused.
                sawRoot = true;

                rootsMatch &= MediaTypeMatches(reader.GetAttribute("media-type", ManifestNamespace), expectedMediaType);
            }
        }

        if (!sawRoot || !rootsMatch)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
        }

        if (!paths.Contains(ContentEntryName) || !package.Contains(ContentEntryName))
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
        }

        return new OpenDocumentManifest(paths);
    }

    /// <summary>True when the manifest declares a file entry at <paramref name="path"/> (case-insensitive).</summary>
    public bool Declares(string path) => _paths.Contains(path);

    private static bool MediaTypeMatches(string? actual, string expected)
    {
        if (actual is null)
        {
            return false;
        }

        ReadOnlySpan<char> mediaType = actual;

        int parameters = mediaType.IndexOf(';');

        if (parameters >= 0)
        {
            mediaType = mediaType[..parameters];
        }

        return mediaType.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
