using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>Receives the text of one spreadsheet text element. False means stop reading: the destination is full.</summary>
internal interface ISpreadsheetTextTarget
{
    bool Append(string text);
}

/// <summary>
/// SpreadsheetML reading helpers over <see cref="BoundedXmlReader"/>. Every read advances the reader only through
/// <see cref="BoundedXmlReader.Read"/> and <see cref="BoundedXmlReader.SkipElement"/>; elements are matched by local
/// name plus namespace URI (transitional or strict), never by prefix.
/// </summary>
internal static class SpreadsheetXml
{
    internal const string TransitionalMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    internal const string StrictMain = "http://purl.oclc.org/ooxml/spreadsheetml/main";

    internal const string TransitionalRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    internal const string StrictRelationships = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    private const long SaturatedNumber = 1L << 40;

    /// <summary>True when the reader's current element is <paramref name="localName"/> in the SpreadsheetML main namespace.</summary>
    public static bool IsMain(XmlReader reader, string localName) =>
        reader.LocalName == localName
        && (reader.NamespaceURI == TransitionalMain || reader.NamespaceURI == StrictMain);

    /// <summary>An attribute that belongs to no namespace, or null.</summary>
    public static string? Attribute(XmlReader reader, string localName) => reader.GetAttribute(localName, string.Empty);

    /// <summary>The <c>r:id</c> attribute of the current element (transitional or strict relationships namespace), or null.</summary>
    public static string? RelationshipId(XmlReader reader) =>
        reader.GetAttribute("id", TransitionalRelationships) ?? reader.GetAttribute("id", StrictRelationships);

    /// <summary>
    /// Advances to the document element and requires it to be <paramref name="rootName"/> in the main namespace; any other
    /// root means the part is not the declared kind.
    /// </summary>
    public static void EnterRoot(BoundedXmlReader xml, string rootName)
    {
        while (xml.Read())
        {
            if (xml.Reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!IsMain(xml.Reader, rootName))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            return;
        }

        throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
    }

    /// <summary>
    /// With the reader on an element start, steps past its start tag. Returns true when the element has content to
    /// iterate with <see cref="NextChildElement"/>; false for an empty element, which is consumed.
    /// </summary>
    public static bool EnterChildren(BoundedXmlReader xml, out int depth)
    {
        depth = xml.Reader.Depth;

        bool hasChildren = !xml.Reader.IsEmptyElement;

        xml.Read();

        return hasChildren;
    }

    /// <summary>
    /// Positions the reader on the next child element of the element entered at <paramref name="parentDepth"/>,
    /// passing over text, whitespace and any grandchildren the caller left behind. Returns false, with the parent's
    /// end tag consumed, when there are no more children.
    /// </summary>
    public static bool NextChildElement(BoundedXmlReader xml, int parentDepth)
    {
        XmlReader reader = xml.Reader;

        while (true)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == parentDepth + 1)
            {
                return true;
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == parentDepth)
            {
                xml.Read();

                return false;
            }

            if (!xml.Read())
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Reads the text content of the text element the reader is on (<c>t</c> or <c>v</c>), handing each text node to
    /// <paramref name="target"/> and skipping any element nested inside. The text is decoded as an <c>ST_Xstring</c>
    /// (<see cref="SpreadsheetXstringDecoder"/>) unless <paramref name="decodeEscapes"/> is false, which a value that is
    /// parsed rather than shown needs. Returns false when the target stopped the read, in which case the reader must be
    /// abandoned.
    /// </summary>
    public static bool ReadText(BoundedXmlReader xml, ISpreadsheetTextTarget target, bool decodeEscapes = true)
    {
        if (!EnterChildren(xml, out int depth))
        {
            return true;
        }

        XmlReader reader = xml.Reader;

        SpreadsheetXstringDecoder? decoder = null;

        while (true)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    string text = reader.Value;

                    bool accepted;

                    if (decodeEscapes && (decoder is not null || text.Contains('_')))
                    {
                        decoder ??= new SpreadsheetXstringDecoder();

                        accepted = decoder.Write(text, target);
                    }
                    else
                    {
                        accepted = target.Append(text);
                    }

                    if (!accepted)
                    {
                        return false;
                    }

                    if (!xml.Read())
                    {
                        return decoder is null || decoder.Finish(target);
                    }

                    break;
                case XmlNodeType.Element:
                    xml.SkipElement();

                    break;
                case XmlNodeType.EndElement when reader.Depth == depth:
                    xml.Read();

                    return decoder is null || decoder.Finish(target);
                default:
                    if (!xml.Read())
                    {
                        return decoder is null || decoder.Finish(target);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Reads a short text element (a cell's <c>v</c> that must be parsed) into at most <paramref name="capacity"/> characters.
    /// Returns null when the text is longer, after reading the whole element so the reader stays usable.
    /// </summary>
    public static string? ReadBoundedText(BoundedXmlReader xml, int capacity)
    {
        SpreadsheetTextBuffer buffer = new(capacity, stopWhenFull: false);

        // A buffer that is not stopWhenFull never stops the read, so the element is always consumed to its end.
        _ = ReadText(xml, buffer, decodeEscapes: false);

        return buffer.Overflowed ? null : buffer.ToString();
    }

    /// <summary>
    /// Parses a non-negative decimal number made only of ASCII digits. A value beyond 2^40 saturates there instead of
    /// overflowing, so an absurdly long digit string is still "a very large number" rather than an error.
    /// </summary>
    public static bool TryParseDigits(string? text, out long value)
    {
        value = 0;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        long accumulated = 0;

        foreach (char digit in text)
        {
            if (digit is < '0' or > '9')
            {
                return false;
            }

            accumulated = Math.Min((accumulated * 10) + (digit - '0'), SaturatedNumber);
        }

        value = accumulated;

        return true;
    }

    /// <summary>Parses a non-negative decimal integer made only of ASCII digits; anything else, or a value above int.MaxValue, is false.</summary>
    public static bool TryParseIndex(string? text, out int value)
    {
        value = 0;

        if (!TryParseDigits(text, out long number) || number > int.MaxValue)
        {
            return false;
        }

        value = (int)number;

        return true;
    }

    /// <summary>True for the XML Schema boolean spellings of true that a hidden flag can take: <c>1</c> and <c>true</c>.</summary>
    public static bool IsTrue(string? flag) => flag == "1" || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a rich text item (<c>si</c> or <c>is</c>): the concatenation of its direct <c>t</c> and of each run's
    /// <c>r/t</c>. Phonetic runs (<c>rPh</c>), <c>phoneticPr</c>, run properties and everything else are skipped.
    /// Returns false when the target stopped the read.
    /// </summary>
    public static bool ReadRichText(BoundedXmlReader xml, ISpreadsheetTextTarget target)
    {
        if (!EnterChildren(xml, out int depth))
        {
            return true;
        }

        while (NextChildElement(xml, depth))
        {
            if (IsMain(xml.Reader, "t"))
            {
                if (!ReadText(xml, target))
                {
                    return false;
                }
            }
            else if (IsMain(xml.Reader, "r"))
            {
                if (!ReadRun(xml, target))
                {
                    return false;
                }
            }
            else
            {
                xml.SkipElement();
            }
        }

        return true;
    }

    private static bool ReadRun(BoundedXmlReader xml, ISpreadsheetTextTarget target)
    {
        if (!EnterChildren(xml, out int depth))
        {
            return true;
        }

        while (NextChildElement(xml, depth))
        {
            if (IsMain(xml.Reader, "t"))
            {
                if (!ReadText(xml, target))
                {
                    return false;
                }
            }
            else
            {
                xml.SkipElement();
            }
        }

        return true;
    }
}
