using System.Xml;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Extractor for PowerPoint presentation packages (<c>.pptx</c> and <c>.pptm</c>), projecting the text a viewer sees
/// in the slide show.
/// </summary>
/// <remarks>
/// <para>
/// The package is walked only through relationships and the slide list: the <c>officeDocument</c> relationship names
/// the presentation part, its <c>p:sldIdLst</c> orders the slides, and the presentation part's own relationships
/// (type <c>slide</c> only) name each slide part. Notes, comments, layouts, masters, charts, diagrams, embeddings,
/// macros, document properties and every unreferenced entry are never opened, and external targets are never
/// followed.
/// </para>
/// <para>
/// A slide projects as <c>Slide n</c> followed by its text, where n is the 1-based position in the slide list (a
/// hidden slide still counts). Text is DrawingML <c>a:t</c> inside a run or field of a paragraph of a text body; each
/// paragraph and each <c>a:br</c> ends with a newline. A table row projects as its cells joined by a tab and ends with
/// a newline; the paragraphs and breaks of one cell are joined by single spaces, and a tab or line break typed into a
/// cell becomes a space, so a cell can never forge a cell or row boundary. Hidden slides (<c>show="0"</c>), hidden
/// shapes and groups (<c>hidden</c> on the shape's <c>p:cNvPr</c>), table cells covered by a merge, the non-first
/// branches of <c>mc:AlternateContent</c>, extension lists and elements, and custom data are excluded; alt text is never
/// emitted. A slide part may be named by only one slide list entry: a second mention is a format mismatch.
/// </para>
/// <para>
/// Text is read only where a viewer gets it: a text body that is a direct child of a shape (a transparent
/// <c>mc:AlternateContent</c> wrapper aside) or of a table cell, and a table only as <c>a:graphicData</c> of the table
/// kind. Graphic data of any other kind (a chart, a diagram, an object, an unknown kind, no kind), elements from a
/// namespace the extractor does not read, and a text body or table placed anywhere else (shape properties, non-visual
/// properties, the shape tree, a paragraph) are skipped or ignored, whatever paragraphs they hold.
/// </para>
/// <para>
/// Shape visibility does not depend on the order of a shape's children. The text of a slide is held in a buffer of at
/// most <see cref="MaximumBufferedCharacters"/> characters until the slide has been read to its end, and a shape or
/// group whose non-visual properties carry a hidden <c>p:cNvPr</c> (directly, or inside <c>mc:AlternateContent</c>
/// wrappers) discards the text held for it and for everything inside it, wherever in the shape the marker sits. Text
/// beyond the buffer is cut and reported as truncation. Equations, chart and SmartArt text, and colour or size tricks
/// are not projected or detected.
/// </para>
/// </remarks>
internal sealed class PresentationDocumentExtractor : ISessionAttachmentDocumentExtractor
{
    internal const int MaximumSlides = 4096;

    /// <summary>
    /// The most characters held for one slide. It equals the default sink ceiling: text beyond it could never be
    /// emitted, so it is dropped and the output is marked truncated.
    /// </summary>
    internal const int MaximumBufferedCharacters = 262_144;

    private const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";

    private const string StrictPresentationNamespace = "http://purl.oclc.org/ooxml/presentationml/main";

    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private const string StrictDrawingNamespace = "http://purl.oclc.org/ooxml/drawingml/main";

    private const string TableGraphicUri = "http://schemas.openxmlformats.org/drawingml/2006/table";

    private const string StrictTableGraphicUri = "http://purl.oclc.org/ooxml/drawingml/table";

    private const string MarkupCompatibilityNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string StrictRelationshipsNamespace = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    private readonly int _maximumSlides;

    public PresentationDocumentExtractor()
        : this(MaximumSlides)
    {
    }

    internal PresentationDocumentExtractor(int maximumSlides)
    {
        _maximumSlides = maximumSlides;
    }

    public ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        BoundedZipPackage package = BoundedZipPackage.Open(bytes, cancellationToken);

        string presentationEntry = ResolvePresentationPart(package, cancellationToken);

        List<string> slideEntries = ReadSlideList(package, presentationEntry, cancellationToken);

        for (int index = 0; index < slideEntries.Count; index++)
        {
            // ReadEntry observes cancellation before each part, and the XML reader every 1,024 nodes within one.
            if (!ExtractSlide(package.ReadEntry(slideEntries[index], cancellationToken), index + 1, output, cancellationToken))
            {
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    private static string ResolvePresentationPart(BoundedZipPackage package, CancellationToken cancellationToken)
    {
        OpcRelationship? presentation = null;

        foreach (OpcRelationship relationship in OpcPackageRelationships.Read(package, string.Empty, cancellationToken))
        {
            if (!OpcPackageRelationships.IsOfficeDocumentType(relationship.Type, "officeDocument"))
            {
                continue;
            }

            if (presentation is not null)
            {
                // Two main parts would let another consumer show a different document than the one read here.
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            presentation = relationship;
        }

        if (presentation is null)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
        }

        // A target the package does not have is refused as a missing part when the entry is read.
        return OpcPackageRelationships.ResolveEntry(package, presentation.TargetEntryName) ?? presentation.TargetEntryName;
    }

    /// <summary>
    /// The slide part entry names in <c>p:sldIdLst</c> order. Each entry is resolved as it is read, so an unresolvable
    /// slide fails before any slide text is produced, and the count is bounded before anything is resolved for it.
    /// </summary>
    private List<string> ReadSlideList(BoundedZipPackage package, string presentationEntry, CancellationToken cancellationToken)
    {
        using BoundedXmlReader xml = BoundedXmlReader.Create(package.ReadEntry(presentationEntry, cancellationToken), cancellationToken);

        XmlReader reader = xml.Reader;

        MoveToRootElement(xml, "presentation");

        // The root is checked before the relationships are read, so a package of another format is refused as such.
        Dictionary<string, string> slideTargets = new(StringComparer.Ordinal);

        foreach (OpcRelationship relationship in OpcPackageRelationships.Read(package, presentationEntry, cancellationToken))
        {
            if (OpcPackageRelationships.IsOfficeDocumentType(relationship.Type, "slide"))
            {
                slideTargets[relationship.Id] = relationship.TargetEntryName;
            }
        }

        List<string> slideEntries = [];

        // Part names are case-insensitive in a package, so two spellings of one name are one slide part.
        HashSet<string> distinctEntries = new(StringComparer.OrdinalIgnoreCase);

        if (reader.IsEmptyElement)
        {
            return slideEntries;
        }

        bool listSeen = false;

        xml.Read();

        while (reader.NodeType != XmlNodeType.EndElement || reader.Depth != 0)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                xml.Read();
            }
            else if (!listSeen && reader.LocalName == "sldIdLst" && IsPresentationNamespace(reader.NamespaceURI))
            {
                // Only the first list is the slide order; a repeated one is not read for slides at all.
                listSeen = true;

                ReadSlideIds(xml, package, slideTargets, slideEntries, distinctEntries);
            }
            else
            {
                xml.SkipElement();
            }
        }

        return slideEntries;
    }

    private void ReadSlideIds(
        BoundedXmlReader xml,
        BoundedZipPackage package,
        Dictionary<string, string> slideTargets,
        List<string> slideEntries,
        HashSet<string> distinctEntries)
    {
        XmlReader reader = xml.Reader;

        int listDepth = reader.Depth;

        if (reader.IsEmptyElement)
        {
            xml.Read();

            return;
        }

        xml.Read();

        while (reader.NodeType != XmlNodeType.EndElement || reader.Depth != listDepth)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                xml.Read();

                continue;
            }

            if (reader.LocalName == "sldId" && IsPresentationNamespace(reader.NamespaceURI))
            {
                if (slideEntries.Count >= _maximumSlides)
                {
                    throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
                }

                string? relationshipId = reader.GetAttribute("id", RelationshipsNamespace)
                    ?? reader.GetAttribute("id", StrictRelationshipsNamespace);

                if (relationshipId is null
                    || !slideTargets.TryGetValue(relationshipId, out string? target)
                    || OpcPackageRelationships.ResolveEntry(package, target) is not { } entry)
                {
                    throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
                }

                if (!distinctEntries.Add(entry))
                {
                    // Every mention would inflate and parse the part again: real producers give each position its own
                    // part, and a list naming one part many times is a way to multiply the cost of a small package.
                    throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                }

                slideEntries.Add(entry);
            }

            xml.SkipElement();
        }

        xml.Read();
    }

    /// <summary>Projects one slide. Returns false when the sink refused text, so the caller stops at once.</summary>
    private static bool ExtractSlide(
        ReadOnlyMemory<byte> part,
        int number,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        using BoundedXmlReader xml = BoundedXmlReader.Create(part, cancellationToken);

        MoveToRootElement(xml, "sld");

        if (IsFalse(xml.Reader.GetAttribute("show", string.Empty)))
        {
            return true;
        }

        return output.TryAppend($"Slide {number}\n") && new SlideTextWalker(xml, output).Walk();
    }

    /// <summary>Moves to the root element and requires it to be the named PresentationML element.</summary>
    private static void MoveToRootElement(BoundedXmlReader xml, string rootName)
    {
        while (xml.Read())
        {
            if (xml.Reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (xml.Reader.LocalName != rootName || !IsPresentationNamespace(xml.Reader.NamespaceURI))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            return;
        }

        throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
    }

    private static bool IsTrue(string? value)
    {
        string? trimmed = value?.Trim();

        return trimmed == "1" || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFalse(string? value)
    {
        string? trimmed = value?.Trim();

        return trimmed == "0" || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPresentationNamespace(string namespaceUri) =>
        namespaceUri == PresentationNamespace || namespaceUri == StrictPresentationNamespace;

    private static bool IsDrawingNamespace(string namespaceUri) =>
        namespaceUri == DrawingNamespace || namespaceUri == StrictDrawingNamespace;

    private static bool IsTableGraphicUri(string? uri) => uri is TableGraphicUri or StrictTableGraphicUri;

    private enum ElementKind : byte
    {
        Other,

        NonVisual,

        /// <summary>A shape, group, picture, connector or graphic frame: the unit that can be hidden.</summary>
        Shape,

        AlternateContent,

        /// <summary>The <c>mc:Choice</c> that is projected.</summary>
        Choice,

        TextBody,

        /// <summary>The <c>a:graphicData</c> of the table kind, the only graphic data that holds text a viewer reads.</summary>
        TableData,

        /// <summary>An <c>a:tbl</c> in table graphic data.</summary>
        Table,

        /// <summary>An <c>a:tc</c> met inside a table cell: its text is part of the outer cell's.</summary>
        NestedCell,

        Paragraph,

        Run,

        Text,

        TableRow,

        TableCell,
    }

    /// <summary>
    /// A single forward pass over the children of one slide's root. It remembers what kind of element is open at each
    /// depth, which is all the context the text rules need. Everything it would write is held in a bounded buffer: the
    /// slide is a frame, and so is each shape inside it, and a hidden marker anywhere in a shape discards the buffer back
    /// to where that shape began, so a shape is shown or hidden as a whole whatever order its children come in.
    /// </summary>
    private sealed class SlideTextWalker(BoundedXmlReader xml, ISessionAttachmentDocumentTextSink output)
    {
        private readonly XmlReader _reader = xml.Reader;

        private readonly ElementKind[] _path = new ElementKind[BoundedXmlReader.MaximumDepth + 1];

        private readonly bool[] _choiceTaken = new bool[BoundedXmlReader.MaximumDepth + 1];

        // One frame for the slide and at most one more per open element depth.
        private readonly int[] _frameStart = new int[BoundedXmlReader.MaximumDepth + 2];

        private readonly bool[] _frameStartedOverflowed = new bool[BoundedXmlReader.MaximumDepth + 2];

        private char[] _buffer = [];

        private int _length;

        private bool _overflowed;

        private int _frameCount = 1;

        private int _hiddenFrame = -1;

        private int _textDepth = -1;

        private int _cellDepth = -1;

        private int _cellsInRow;

        private bool _cellHasText;

        private bool _cellBreakPending;

        private bool _advance = true;

        /// <summary>Walks to the end of the slide and hands its visible text to the sink. Returns false when the sink stopped accepting text.</summary>
        public bool Walk()
        {
            while (true)
            {
                if (_advance && !xml.Read())
                {
                    return Complete();
                }

                _advance = true;

                switch (_reader.NodeType)
                {
                    case XmlNodeType.Element:
                        VisitElement();

                        break;

                    case XmlNodeType.EndElement:
                        VisitEndElement();

                        break;

                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        VisitText();

                        break;
                }
            }
        }

        private void VisitElement()
        {
            int depth = _reader.Depth;

            _path[depth] = ElementKind.Other;

            string namespaceUri = _reader.NamespaceURI;

            if (namespaceUri == MarkupCompatibilityNamespace)
            {
                VisitCompatibilityElement(depth);
            }
            else if (IsPresentationNamespace(namespaceUri))
            {
                VisitPresentationElement(depth);
            }
            else if (IsDrawingNamespace(namespaceUri))
            {
                VisitDrawingElement(depth);
            }
            else
            {
                // An element from a namespace the extractor does not read (an extension container, a custom wrapper) is not
                // text a viewer sees, whatever it holds.
                SkipCurrentElement();
            }
        }

        /// <summary>
        /// Only the first <c>mc:Choice</c> of an <c>mc:AlternateContent</c> is projected; <c>mc:Fallback</c> never is,
        /// and neither is a choice that is not a direct child of an <c>mc:AlternateContent</c>.
        /// </summary>
        private void VisitCompatibilityElement(int depth)
        {
            switch (_reader.LocalName)
            {
                case "AlternateContent":
                    _path[depth] = ElementKind.AlternateContent;

                    _choiceTaken[depth] = false;

                    break;

                case "Choice" when _path[depth - 1] == ElementKind.AlternateContent && !_choiceTaken[depth - 1]:
                    _choiceTaken[depth - 1] = true;

                    _path[depth] = ElementKind.Choice;

                    break;

                case "Choice" or "Fallback":
                    SkipCurrentElement();

                    break;
            }
        }

        private void VisitPresentationElement(int depth)
        {
            string name = _reader.LocalName;

            switch (name)
            {
                case "nvSpPr" or "nvGrpSpPr" or "nvPicPr" or "nvGraphicFramePr" or "nvCxnSpPr":
                    _path[depth] = ElementKind.NonVisual;

                    break;

                case "sp" or "grpSp" or "graphicFrame" or "pic" or "cxnSp":
                    if (!_reader.IsEmptyElement)
                    {
                        _path[depth] = ElementKind.Shape;

                        PushFrame();
                    }

                    break;

                case "txBody" when CanHoldTextBody(depth):
                    _path[depth] = ElementKind.TextBody;

                    break;

                case "extLst" or "custDataLst" or "ext":
                    SkipCurrentElement();

                    break;

                case "cNvPr" when IsNonVisualProperty(depth) && IsTrue(_reader.GetAttribute("hidden", string.Empty)):
                    HideCurrentShape();

                    break;
            }
        }

        private void VisitDrawingElement(int depth)
        {
            ElementKind parent = _path[depth - 1];

            switch (_reader.LocalName)
            {
                case "txBody" when CanHoldTextBody(depth):
                    _path[depth] = ElementKind.TextBody;

                    break;

                case "extLst" or "ext":
                    SkipCurrentElement();

                    break;

                // Graphic data is drawn according to its kind: a table is read, and a chart, a diagram, an object or a kind
                // nothing here knows is not text a viewer gets from the slide's markup.
                case "graphicData":
                    if (IsTableGraphicUri(_reader.GetAttribute("uri", string.Empty)))
                    {
                        _path[depth] = ElementKind.TableData;
                    }
                    else
                    {
                        SkipCurrentElement();
                    }

                    break;

                case "tbl" when parent == ElementKind.TableData:
                    _path[depth] = ElementKind.Table;

                    break;

                case "tc" when IsTrue(_reader.GetAttribute("hMerge", string.Empty)) || IsTrue(_reader.GetAttribute("vMerge", string.Empty)):
                    SkipCurrentElement();

                    break;

                // A table nested in a cell is part of the outer cell and adds no boundaries; a row outside a table is no row.
                case "tr" when _cellDepth < 0 && parent == ElementKind.Table:
                    _path[depth] = ElementKind.TableRow;

                    _cellsInRow = 0;

                    break;

                case "tc" when parent == ElementKind.TableRow:
                    if (_cellsInRow++ > 0)
                    {
                        Write('\t');
                    }

                    if (!_reader.IsEmptyElement)
                    {
                        _path[depth] = ElementKind.TableCell;

                        _cellDepth = depth;

                        _cellHasText = false;

                        _cellBreakPending = false;
                    }

                    break;

                case "tc" when _cellDepth >= 0:
                    _path[depth] = ElementKind.NestedCell;

                    break;

                case "p" when parent == ElementKind.TextBody:
                    if (!_reader.IsEmptyElement)
                    {
                        _path[depth] = ElementKind.Paragraph;

                        break;
                    }

                    EndLine();

                    break;

                case "r" or "fld" when parent == ElementKind.Paragraph:
                    _path[depth] = ElementKind.Run;

                    break;

                case "t" when parent == ElementKind.Run && !_reader.IsEmptyElement:
                    _path[depth] = ElementKind.Text;

                    _textDepth = depth;

                    break;

                case "br" when parent == ElementKind.Paragraph:
                    EndLine();

                    break;
            }
        }

        private void VisitEndElement()
        {
            switch (_path[_reader.Depth])
            {
                case ElementKind.Text:
                    _textDepth = -1;

                    break;

                case ElementKind.Paragraph:
                    EndLine();

                    break;

                case ElementKind.TableCell:
                    _cellDepth = -1;

                    break;

                case ElementKind.TableRow:
                    // A row with no visible cell has no line; a covered cell is not a cell of the row.
                    if (_cellsInRow > 0)
                    {
                        Write('\n');
                    }

                    break;

                case ElementKind.Shape:
                    PopFrame();

                    break;
            }
        }

        private void VisitText()
        {
            if (_textDepth >= 0 && _reader.Depth == _textDepth + 1)
            {
                WriteText(_reader.Value);
            }
        }

        /// <summary>
        /// True when the element at <paramref name="depth"/> is a direct child of a shape's non-visual properties.
        /// <c>mc:AlternateContent</c> and its projected <c>mc:Choice</c> are transparent wrappers on the way up.
        /// </summary>
        private bool IsNonVisualProperty(int depth) => ParentThroughCompatibility(depth) == ElementKind.NonVisual;

        /// <summary>
        /// True when the element at <paramref name="depth"/> sits where a text body is drawn: directly in a shape, or in a table
        /// cell. A text body anywhere else (shape properties, non-visual properties, a paragraph, the shape tree itself) is
        /// not drawn.
        /// </summary>
        private bool CanHoldTextBody(int depth) =>
            ParentThroughCompatibility(depth) is ElementKind.Shape or ElementKind.TableCell or ElementKind.NestedCell;

        /// <summary>The kind of the element's parent, looking through the <c>mc:AlternateContent</c> and <c>mc:Choice</c> wrappers between them.</summary>
        private ElementKind ParentThroughCompatibility(int depth)
        {
            int ancestor = depth - 1;

            while (ancestor > 0 && _path[ancestor] is ElementKind.AlternateContent or ElementKind.Choice)
            {
                ancestor--;
            }

            return _path[ancestor];
        }

        private void PushFrame()
        {
            _frameStart[_frameCount] = _length;

            _frameStartedOverflowed[_frameCount] = _overflowed;

            _frameCount++;
        }

        private void PopFrame()
        {
            _frameCount--;

            if (_hiddenFrame == _frameCount)
            {
                _hiddenFrame = -1;
            }
        }

        /// <summary>
        /// Hides the shape whose non-visual properties are being read, which is the innermost open frame however many
        /// compatibility wrappers sit between: everything held since it began is discarded and nothing more is kept for it.
        /// </summary>
        private void HideCurrentShape()
        {
            if (_hiddenFrame >= 0)
            {
                return;
            }

            int frame = _frameCount - 1;

            _hiddenFrame = frame;

            _length = _frameStart[frame];

            // Overflow that began inside this shape went with its text; overflow from before it stays.
            if (_overflowed && !_frameStartedOverflowed[frame])
            {
                _overflowed = false;
            }
        }

        /// <summary>A paragraph or <c>a:br</c> ends a line, except in a table cell where it is a space before the cell's next text.</summary>
        private void EndLine()
        {
            if (_cellDepth >= 0)
            {
                _cellBreakPending = true;
            }
            else
            {
                Write('\n');
            }
        }

        private void WriteText(string value)
        {
            if (_cellDepth < 0)
            {
                Write(value);

                return;
            }

            if (value.Length == 0)
            {
                return;
            }

            if (_cellBreakPending && _cellHasText)
            {
                Write(' ');
            }

            _cellBreakPending = false;

            _cellHasText = true;

            if (value.AsSpan().IndexOfAny('\t', '\r', '\n') < 0)
            {
                Write(value);

                return;
            }

            // A tab or line break typed into a cell would read as a cell or row boundary of the projection.
            foreach (char character in value)
            {
                Write(character is '\t' or '\r' or '\n' ? ' ' : character);
            }
        }

        private void Write(char character) => Write(new ReadOnlySpan<char>(in character));

        private void Write(ReadOnlySpan<char> text)
        {
            if (_hiddenFrame >= 0 || _overflowed || text.Length == 0)
            {
                return;
            }

            int room = MaximumBufferedCharacters - _length;

            if (text.Length > room)
            {
                text = text[..room];

                _overflowed = true;
            }

            if (_buffer.Length - _length < text.Length)
            {
                Array.Resize(ref _buffer, Math.Min(MaximumBufferedCharacters, Math.Max(_length + text.Length, Math.Max(256, _buffer.Length * 2))));
            }

            text.CopyTo(_buffer.AsSpan(_length));

            _length += text.Length;
        }

        /// <summary>
        /// Hands the slide's visible text to the sink once the whole slide is known; false when the sink stopped
        /// accepting text. A hidden root has already emptied the buffer and cleared any overflow, so it writes nothing.
        /// </summary>
        private bool Complete()
        {
            if (_length > 0 && !output.TryAppend(_buffer.AsSpan(0, _length)))
            {
                return false;
            }

            if (!_overflowed)
            {
                return true;
            }

            output.MarkTruncated();

            return false;
        }

        /// <summary>Consumes the current element's subtree. The reader then sits on the next node, which is still to be visited.</summary>
        private void SkipCurrentElement()
        {
            xml.SkipElement();

            _advance = false;
        }
    }
}
