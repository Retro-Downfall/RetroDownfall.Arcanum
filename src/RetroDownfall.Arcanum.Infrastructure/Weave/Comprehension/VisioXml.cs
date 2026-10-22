using System.Globalization;
using System.Text;
using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>What a boolean ShapeSheet cell (or attribute) says: nothing, false, true, or something that cannot be read.</summary>
internal enum VisioCellValue
{
    Unspecified,

    False,

    True,

    Unknown,
}

/// <summary>The cursor for walking the direct child elements of one element with <see cref="VisioXml.NextChild"/>.</summary>
internal struct VisioChildScan
{
    internal int Depth;

    internal bool Finished;
}

/// <summary>
/// Shared reading helpers for the MS-VSDX parts. Every helper advances the document only through
/// <see cref="BoundedXmlReader.Read"/> and <see cref="BoundedXmlReader.SkipElement"/>, and every element is matched by
/// namespace and local name, never by prefix.
/// </summary>
internal static class VisioXml
{
    internal const string MainNamespace = "http://schemas.microsoft.com/office/visio/2012/main";

    internal const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>True when the reader is on a start tag of the Visio element <paramref name="localName"/>.</summary>
    public static bool IsElement(XmlReader reader, string localName) =>
        reader.NodeType == XmlNodeType.Element
        && reader.LocalName == localName
        && reader.NamespaceURI == MainNamespace;

    /// <summary>
    /// Parses a boolean (<c>1</c>/<c>0</c>, <c>TRUE</c>/<c>FALSE</c>, any number). Null or blank says nothing; anything else
    /// that is not a boolean or a number cannot be read.
    /// </summary>
    public static VisioCellValue ParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return VisioCellValue.Unspecified;
        }

        if (value.Length > 64)
        {
            return VisioCellValue.Unknown;
        }

        ReadOnlySpan<char> trimmed = value.AsSpan().Trim();

        if (bool.TryParse(trimmed, out bool flag))
        {
            return flag ? VisioCellValue.True : VisioCellValue.False;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return number != 0 ? VisioCellValue.True : VisioCellValue.False;
        }

        return VisioCellValue.Unknown;
    }

    /// <summary>
    /// Reads the value of the boolean Cell the reader is on: the cached <c>V</c>. A cell with no value but a local formula
    /// (anything other than <c>Inh</c>, the inherited-formula marker) has a value that is not known without evaluating it.
    /// </summary>
    public static VisioCellValue ReadBooleanCell(XmlReader reader)
    {
        VisioCellValue value = ParseBoolean(reader.GetAttribute("V"));

        if (value != VisioCellValue.Unspecified)
        {
            return value;
        }

        string? formula = reader.GetAttribute("F");

        return string.IsNullOrEmpty(formula) || formula == "Inh" ? VisioCellValue.Unspecified : VisioCellValue.Unknown;
    }

    /// <summary>True when a "hide" cell (HideText, UIVisibility) or attribute (Del) hides, conservatively including an unreadable value.</summary>
    public static bool Hides(VisioCellValue value) => value is VisioCellValue.True or VisioCellValue.Unknown;

    /// <summary>True when a layer's Visible cell makes the layer invisible, conservatively including an unreadable value.</summary>
    public static bool IsInvisible(VisioCellValue value) => value is VisioCellValue.False or VisioCellValue.Unknown;

    public static bool TryParseIndex(string? value, out int index) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out index);

    /// <summary>
    /// True when a LayerMember list (semicolon-separated layer indices) names a layer that is invisible. An entry that is
    /// not an index cannot be matched to a layer, so on a page that has invisible layers it hides conservatively.
    /// </summary>
    public static bool ListsInvisibleLayer(string? list, VisioLayerSet? layers)
    {
        if (layers is null || string.IsNullOrEmpty(list))
        {
            return false;
        }

        ReadOnlySpan<char> remaining = list;

        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOf(';');

            ReadOnlySpan<char> token = (separator < 0 ? remaining : remaining[..separator]).Trim();

            remaining = separator < 0 ? default : remaining[(separator + 1)..];

            if (token.IsEmpty)
            {
                continue;
            }

            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || layers.IsInvisible(index))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the LayerMember cell the reader is on puts its shape on an invisible layer: its cached <c>V</c> lists one,
    /// or it has no cached value but a local formula (anything other than <c>Inh</c>) that could resolve to one. Without any
    /// invisible layer on the page (<paramref name="layers"/> null) nothing can hide.
    /// </summary>
    public static bool LayerMemberHides(XmlReader reader, VisioLayerSet? layers)
    {
        if (layers is null)
        {
            return false;
        }

        string? members = reader.GetAttribute("V");

        if (!string.IsNullOrEmpty(members))
        {
            return ListsInvisibleLayer(members, layers);
        }

        string? formula = reader.GetAttribute("F");

        return !string.IsNullOrEmpty(formula) && formula != "Inh";
    }

    /// <summary>
    /// Advances to the root element and requires it to be the Visio element <paramref name="localName"/>. The reader is
    /// left on the root start tag.
    /// </summary>
    public static void OpenRoot(BoundedXmlReader xml, string localName)
    {
        while (xml.Read())
        {
            if (xml.Reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!IsElement(xml.Reader, localName))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            return;
        }

        throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
    }

    /// <summary>
    /// Starts walking the children of the element the reader is on, consuming its start tag. Drive it with
    /// <see cref="NextChild"/>; each child element it reports must be consumed (read through or skipped) before the next call.
    /// </summary>
    public static VisioChildScan BeginChildren(BoundedXmlReader xml)
    {
        VisioChildScan scan = new() { Depth = xml.Reader.Depth };

        bool empty = xml.Reader.IsEmptyElement;

        bool more = xml.Read();

        if (empty)
        {
            scan.Finished = true;

            return scan;
        }

        if (!more)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
        }

        return scan;
    }

    /// <summary>
    /// Moves to the next child element and returns true with the reader on its start tag, or returns false after
    /// consuming the parent's end tag.
    /// </summary>
    public static bool NextChild(BoundedXmlReader xml, ref VisioChildScan scan)
    {
        XmlReader reader = xml.Reader;

        while (!scan.Finished)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                return true;
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == scan.Depth)
            {
                scan.Finished = true;

                xml.Read();

                return false;
            }

            if (!xml.Read())
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
            }
        }

        return false;
    }

    /// <summary>
    /// Consumes the mixed content of the Text element (or, with <paramref name="fields"/> false, of a field) the reader
    /// is on, appending its text nodes and field values to <paramref name="target"/>. Text markers and every other
    /// element are skipped with their content. The element is always consumed whole; once appending would pass
    /// <paramref name="limit"/> total characters nothing more is appended and false is returned.
    /// </summary>
    public static bool ReadMixedText(BoundedXmlReader xml, StringBuilder target, int limit, bool fields = true)
    {
        bool overflowed = false;

        ReadMixedText(xml, target, limit, fields, ref overflowed);

        return !overflowed;
    }

    private static void ReadMixedText(BoundedXmlReader xml, StringBuilder target, int limit, bool fields, ref bool overflowed)
    {
        XmlReader reader = xml.Reader;

        if (reader.IsEmptyElement)
        {
            xml.Read();

            return;
        }

        int depth = reader.Depth;

        xml.Read();

        while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    string value = reader.Value;

                    if (!overflowed)
                    {
                        if ((long)target.Length + value.Length > limit)
                        {
                            overflowed = true;
                        }
                        else
                        {
                            target.Append(value);
                        }
                    }

                    if (!xml.Read())
                    {
                        throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
                    }

                    break;

                case XmlNodeType.Element:
                    if (fields && IsElement(reader, "fld"))
                    {
                        ReadMixedText(xml, target, limit, fields: false, ref overflowed);
                    }
                    else
                    {
                        xml.SkipElement();
                    }

                    break;

                default:
                    if (!xml.Read())
                    {
                        throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
                    }

                    break;
            }
        }

        xml.Read();
    }
}
