using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// What one <c>w:rPr</c> says about hiding: <see cref="Direct"/> is its own <c>vanish</c>/<c>specVanish</c> verdict (null when
/// it declares neither), <see cref="StyleHidden"/> is whether the character style it names hides.
/// </summary>
internal readonly record struct WordprocessingRunFormatting(bool? Direct, bool StyleHidden);

/// <summary>Namespace constants and small matching helpers shared by the Wordprocessing readers.</summary>
internal static class WordprocessingXml
{
    internal const string TransitionalNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    internal const string StrictNamespace = "http://purl.oclc.org/ooxml/wordprocessingml/main";

    internal const string MarkupCompatibilityNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    internal const string TransitionalDrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";

    internal const string StrictDrawingNamespace = "http://purl.oclc.org/ooxml/drawingml/wordprocessingDrawing";

    internal const string VectorMarkupNamespace = "urn:schemas-microsoft-com:vml";

    internal const string WordprocessingShapeNamespace = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

    internal const string WordprocessingGroupNamespace = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";

    internal const string WordprocessingCanvasNamespace = "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas";

    internal const string TransitionalDrawingMlNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";

    internal const string StrictDrawingMlNamespace = "http://purl.oclc.org/ooxml/drawingml/main";

    internal const string MathNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    internal const string StrictMathNamespace = "http://purl.oclc.org/ooxml/officeDocument/math";

    /// <summary>
    /// The namespaces this extractor understands, which is what decides an <c>mc:Choice</c> (a choice is taken only when
    /// every namespace it requires is understood; otherwise the fallback is). Understanding a namespace means that what a
    /// choice written against it holds is either read here (WordprocessingML, the drawing containers, VML, Office Math) or
    /// is non-text markup that is skipped without losing anything the fallback would have shown. The emoji extension
    /// (<c>w16se</c>) is deliberately absent: its choice holds no text, and its fallback holds the character.
    /// </summary>
    private static readonly HashSet<string> NamespacesUnderstood = new(StringComparer.Ordinal)
    {
        TransitionalNamespace,

        StrictNamespace,

        TransitionalDrawingNamespace,

        StrictDrawingNamespace,

        TransitionalDrawingMlNamespace,

        StrictDrawingMlNamespace,

        WordprocessingShapeNamespace,

        WordprocessingGroupNamespace,

        WordprocessingCanvasNamespace,

        VectorMarkupNamespace,

        MathNamespace,

        StrictMathNamespace,

        "urn:schemas-microsoft-com:office:office",

        "urn:schemas-microsoft-com:office:word",

        "http://schemas.microsoft.com/office/word/2010/wordprocessingInk",

        "http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing",

        "http://schemas.microsoft.com/office/word/2010/wordml",

        "http://schemas.microsoft.com/office/word/2012/wordml",

        "http://schemas.microsoft.com/office/word/2006/wordml",

        "http://schemas.microsoft.com/office/word/2016/wordml/cid",

        "http://schemas.microsoft.com/office/word/2018/wordml",

        "http://schemas.microsoft.com/office/word/2018/wordml/cex",

        "http://schemas.microsoft.com/office/drawing/2010/main",

        "http://schemas.microsoft.com/office/drawing/2014/main",

        "http://schemas.microsoft.com/office/drawing/2016/SVG/main",
    };

    /// <summary>True for the transitional or the strict WordprocessingML namespace. Elements are matched by namespace, never by prefix.</summary>
    internal static bool IsWordprocessing(string namespaceUri) =>
        namespaceUri == TransitionalNamespace || namespaceUri == StrictNamespace;

    internal static bool IsWordprocessingDrawing(string namespaceUri) =>
        namespaceUri == TransitionalDrawingNamespace || namespaceUri == StrictDrawingNamespace;

    /// <summary>True for the transitional or the strict Office Math namespace.</summary>
    internal static bool IsMath(string namespaceUri) =>
        namespaceUri == MathNamespace || namespaceUri == StrictMathNamespace;

    /// <summary>
    /// True for the namespaces whose elements can lead to document text: WordprocessingML, the drawing containers that
    /// hold text boxes (DrawingML, <c>wps</c>, <c>wpc</c>) and VML. The <c>wp</c> drawing containers and the <c>wpg</c> groups
    /// (the only <c>wpg</c> elements that lead to text) are handled before this question is asked. An element from any other
    /// namespace is one Word does not understand; a conforming package marks such namespaces <c>mc:Ignorable</c> and Word
    /// drops the element together with its content, so its subtree is never walked.
    /// </summary>
    internal static bool IsContentNamespace(string namespaceUri) =>
        IsWordprocessing(namespaceUri)
        || namespaceUri == TransitionalDrawingMlNamespace
        || namespaceUri == StrictDrawingMlNamespace
        || namespaceUri == WordprocessingShapeNamespace
        || namespaceUri == WordprocessingCanvasNamespace
        || namespaceUri == VectorMarkupNamespace;

    /// <summary>True when this extractor understands the namespace, so an <c>mc:Choice</c> that requires it can be taken.</summary>
    internal static bool IsUnderstood(string namespaceUri) => NamespacesUnderstood.Contains(namespaceUri);

    /// <summary>
    /// True for an attribute that turns a feature on unless it says otherwise: absent is off, an explicit "0" or "false"
    /// is off, and anything else (including "1", "true" and unrecognised values) is on.
    /// </summary>
    internal static bool IsTruthyFlag(string? value) =>
        value is not null && value != "0" && value != "false";

    /// <summary>
    /// The value of an on/off property element: the element alone means on, and only "0", "false" and "off" mean off.
    /// Anything unrecognised is on, so a property is never ignored for being spelled unexpectedly.
    /// </summary>
    internal static bool IsOn(string? value) => value is null || (value != "0" && value != "false" && value != "off");

    /// <summary>
    /// Enters the element the reader is on. Returns false when it was empty (and has been consumed); otherwise leaves the
    /// reader on its first child node and the caller loops with <see cref="WordprocessingChildren.MoveNext"/>.
    /// </summary>
    internal static bool EnterElement(BoundedXmlReader xml)
    {
        bool empty = xml.Reader.IsEmptyElement;

        xml.Read();

        return !empty;
    }

    /// <summary>
    /// Reads the <c>w:rPr</c> the reader is on and consumes it. Only its direct children count (those of a selected
    /// <c>mc:AlternateContent</c> branch included): <c>vanish</c> and <c>specVanish</c> hide (unless switched off),
    /// <c>rStyle</c> names the character style, and everything else, a tracked change's recorded previous properties
    /// included, is skipped unread.
    /// </summary>
    internal static WordprocessingRunFormatting ReadRunProperties(BoundedXmlReader xml, WordprocessingStyleSheet styles)
    {
        XmlReader reader = xml.Reader;

        bool? direct = null;

        bool styled = false;

        bool styleHidden = false;

        if (EnterElement(xml))
        {
            WordprocessingChildren children = new(xml);

            while (children.MoveNext())
            {
                if (IsWordprocessing(reader.NamespaceURI))
                {
                    string localName = reader.LocalName;

                    if (localName is "vanish" or "specVanish")
                    {
                        direct = (direct ?? false) | IsOn(reader.GetAttribute("val", reader.NamespaceURI));
                    }
                    else if (localName == "rStyle")
                    {
                        styled = true;

                        styleHidden |= styles.IsCharacterStyleHidden(reader.GetAttribute("val", reader.NamespaceURI));
                    }
                }

                xml.SkipElement();
            }
        }

        return new WordprocessingRunFormatting(direct, styled ? styleHidden : styles.IsCharacterStyleHidden(null));
    }

    /// <summary>
    /// True for the WordprocessingML range markup elements (bookmarks, comment ranges and the move and custom XML revision
    /// ranges). They mark a position and carry no text.
    /// </summary>
    internal static bool IsRangeMarkup(string namespaceUri, string localName) =>
        IsWordprocessing(namespaceUri)
        && localName is "bookmarkStart" or "bookmarkEnd"
            or "commentRangeStart" or "commentRangeEnd"
            or "moveFromRangeStart" or "moveFromRangeEnd" or "moveToRangeStart" or "moveToRangeEnd"
            or "customXmlInsRangeStart" or "customXmlInsRangeEnd" or "customXmlDelRangeStart" or "customXmlDelRangeEnd"
            or "customXmlMoveFromRangeStart" or "customXmlMoveFromRangeEnd" or "customXmlMoveToRangeStart" or "customXmlMoveToRangeEnd";

    /// <summary>
    /// True for the drawing elements whose first child may be a <c>cNvPr</c> carrying a hidden flag: a shape
    /// (<c>wps:wsp</c>) or a group (<c>wpg:wgp</c>, <c>wpg:grpSp</c>). The outermost shape's flag is on <c>wp:docPr</c>; a
    /// shape inside a group says it here.
    /// </summary>
    internal static bool IsDrawingShapeContainer(string namespaceUri, string localName) =>
        (namespaceUri == WordprocessingShapeNamespace && localName == "wsp")
        || (namespaceUri == WordprocessingGroupNamespace && localName is "wgp" or "grpSp");

    /// <summary>
    /// True when a VML <c>style</c> attribute declares <c>visibility:hidden</c> or <c>display:none</c> (names and values
    /// are matched ignoring case and surrounding white space).
    /// </summary>
    internal static bool DeclaresHiddenShape(string style)
    {
        ReadOnlySpan<char> rest = style;

        while (!rest.IsEmpty)
        {
            int semicolon = rest.IndexOf(';');

            ReadOnlySpan<char> declaration = semicolon < 0 ? rest : rest[..semicolon];

            rest = semicolon < 0 ? default : rest[(semicolon + 1)..];

            int colon = declaration.IndexOf(':');

            if (colon < 0)
            {
                continue;
            }

            ReadOnlySpan<char> name = declaration[..colon].Trim();

            ReadOnlySpan<char> value = declaration[(colon + 1)..].Trim();

            if ((name.Equals("visibility", StringComparison.OrdinalIgnoreCase) && value.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                || (name.Equals("display", StringComparison.OrdinalIgnoreCase) && value.Equals("none", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Advances to the document element and requires it to be <paramref name="rootName"/> in a WordprocessingML
    /// namespace. [Content_Types].xml is never consulted: the declared format is judged by this root element.
    /// </summary>
    internal static void MoveToRoot(BoundedXmlReader xml, string rootName)
    {
        while (xml.Read())
        {
            if (xml.Reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (xml.Reader.LocalName != rootName || !IsWordprocessing(xml.Reader.NamespaceURI))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            return;
        }

        throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
    }
}
