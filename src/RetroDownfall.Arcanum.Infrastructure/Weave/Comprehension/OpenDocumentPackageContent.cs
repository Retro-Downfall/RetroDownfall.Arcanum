using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The parts of an OpenDocument package an extractor reads: <c>content.xml</c> (read once and kept, so the
/// aggregate inflation budget is spent once) and, when the manifest declares it, <c>styles.xml</c>. Nothing else
/// in the package is ever opened.
/// </summary>
/// <remarks>
/// Styles are collected before the body is walked, in a pass that skips the body, so a style that a consumer would
/// honour is honoured however the producer ordered the parts of <c>content.xml</c>.
/// </remarks>
internal sealed class OpenDocumentPackageContent
{
    private const string ContentEntryName = "content.xml";

    private const string StylesEntryName = "styles.xml";

    private readonly ReadOnlyMemory<byte> _content;

    private OpenDocumentPackageContent(ReadOnlyMemory<byte> content, OpenDocumentStyleTable styles)
    {
        _content = content;

        Styles = styles;
    }

    /// <summary>The hiding-relevant styles of <c>styles.xml</c> and <c>content.xml</c>.</summary>
    public OpenDocumentStyleTable Styles { get; }

    /// <summary>
    /// Opens <paramref name="bytes"/> as an OpenDocument package of <paramref name="mimeType"/>: the ZIP container is
    /// validated, the manifest must declare the media type and <c>content.xml</c>, and the style and content parts
    /// are inflated and their styles collected. The content must be an <c>office:document-content</c> whose single
    /// <c>office:body</c> holds exactly one <c>office:&lt;bodyElement&gt;</c>; anything else is a format mismatch.
    /// </summary>
    public static OpenDocumentPackageContent Load(
        ReadOnlyMemory<byte> bytes,
        string mimeType,
        string bodyElement,
        CancellationToken cancellationToken)
    {
        BoundedZipPackage package = BoundedZipPackage.Open(bytes, cancellationToken);

        OpenDocumentManifest manifest = OpenDocumentManifest.Read(package, mimeType, cancellationToken);

        OpenDocumentStyleTable styles = new();

        if (manifest.Declares(StylesEntryName))
        {
            ReadStylesPart(styles, package.ReadEntry(StylesEntryName, cancellationToken), cancellationToken);
        }

        ReadOnlyMemory<byte> content = package.ReadEntry(ContentEntryName, cancellationToken);

        ReadContentStyles(styles, content, bodyElement, cancellationToken);

        return new OpenDocumentPackageContent(content, styles);
    }

    /// <summary>
    /// Opens a reader on <c>content.xml</c> positioned on the <c>office:&lt;bodyElement&gt;</c> element inside
    /// <c>office:body</c>, which <see cref="Load"/> has already validated. The caller disposes the reader.
    /// </summary>
    public BoundedXmlReader OpenBody(string bodyElement, CancellationToken cancellationToken)
    {
        BoundedXmlReader xml = BoundedXmlReader.Create(_content, cancellationToken);

        try
        {
            if (EnterRoot(xml, "document-content"))
            {
                XmlReader reader = xml.Reader;

                while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        xml.Read();

                        continue;
                    }

                    if (IsOffice(reader, "body"))
                    {
                        return EnterBodyElement(xml, bodyElement);
                    }

                    xml.SkipElement();
                }
            }

            throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
        }
        catch
        {
            xml.Dispose();

            throw;
        }
    }

    private static BoundedXmlReader EnterBodyElement(BoundedXmlReader xml, string bodyElement)
    {
        XmlReader reader = xml.Reader;

        if (!reader.IsEmptyElement && xml.Read())
        {
            while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    return IsOffice(reader, bodyElement)
                        ? xml
                        : throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                }

                xml.Read();
            }
        }

        throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
    }

    private static void ReadStylesPart(OpenDocumentStyleTable styles, ReadOnlyMemory<byte> part, CancellationToken cancellationToken)
    {
        using BoundedXmlReader xml = BoundedXmlReader.Create(part, cancellationToken);

        if (!EnterRoot(xml, "document-styles"))
        {
            return;
        }

        XmlReader reader = xml.Reader;

        while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                xml.Read();

                continue;
            }

            if (IsOffice(reader, "styles"))
            {
                styles.ReadContainer(xml, automatic: false);
            }
            else
            {
                // Master pages, the part's own automatic styles and settings are page furniture, never projected.
                xml.SkipElement();
            }
        }
    }

    private static void ReadContentStyles(
        OpenDocumentStyleTable styles,
        ReadOnlyMemory<byte> content,
        string bodyElement,
        CancellationToken cancellationToken)
    {
        using BoundedXmlReader xml = BoundedXmlReader.Create(content, cancellationToken);

        int bodies = 0;

        if (EnterRoot(xml, "document-content"))
        {
            XmlReader reader = xml.Reader;

            while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    xml.Read();

                    continue;
                }

                if (IsOffice(reader, "automatic-styles"))
                {
                    styles.ReadContainer(xml, automatic: true);
                }
                else if (IsOffice(reader, "styles"))
                {
                    styles.ReadContainer(xml, automatic: false);
                }
                else if (IsOffice(reader, "body"))
                {
                    if (++bodies > 1)
                    {
                        throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                    }

                    ValidateBody(xml, bodyElement);
                }
                else
                {
                    xml.SkipElement();
                }
            }
        }

        if (bodies != 1)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
        }
    }

    /// <summary>The body must hold exactly one element and it must be the expected document kind.</summary>
    private static void ValidateBody(BoundedXmlReader xml, string bodyElement)
    {
        XmlReader reader = xml.Reader;

        int children = 0;

        bool matches = false;

        if (reader.IsEmptyElement)
        {
            xml.Read();
        }
        else
        {
            xml.Read();

            while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    xml.Read();

                    continue;
                }

                if (++children == 1)
                {
                    matches = IsOffice(reader, bodyElement);
                }

                xml.SkipElement();
            }

            xml.Read();
        }

        if (children != 1 || !matches)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
        }
    }

    /// <summary>
    /// Checks that the root is the expected <c>office:&lt;rootName&gt;</c> element and moves to its first child node.
    /// False when the root is empty.
    /// </summary>
    private static bool EnterRoot(BoundedXmlReader xml, string rootName)
    {
        while (xml.Read())
        {
            if (xml.Reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!IsOffice(xml.Reader, rootName))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            return !xml.Reader.IsEmptyElement && xml.Read();
        }

        return false;
    }

    private static bool IsOffice(XmlReader reader, string localName) =>
        reader.LocalName == localName && reader.NamespaceURI == OpenDocumentNames.Office;
}
