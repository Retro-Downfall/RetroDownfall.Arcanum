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
/// paragraph and each <c>a:br</c> ends with a newline. Hidden slides (<c>show="0"</c>), hidden shapes and groups
/// (<c>hidden</c> on the shape's <c>p:cNvPr</c>), table cells covered by a merge, the non-first branches of
/// <c>mc:AlternateContent</c>, extension lists and custom data are excluded; alt text is never emitted.
/// </para>
/// <para>
/// Shape visibility is decided from the shape's non-visual properties, which the PresentationML schema places before
/// its text body, so a hidden shape is skipped as a whole in a single forward pass without buffering. Equations,
/// chart and SmartArt text, and colour or size tricks are not projected or detected.
/// </para>
/// </remarks>
internal sealed class PresentationDocumentExtractor : ISessionAttachmentDocumentExtractor
{
    internal const int MaximumSlides = 4096;

    private const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";

    private const string StrictPresentationNamespace = "http://purl.oclc.org/ooxml/presentationml/main";

    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private const string StrictDrawingNamespace = "http://purl.oclc.org/ooxml/drawingml/main";

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

                ReadSlideIds(xml, package, slideTargets, slideEntries);
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
        List<string> slideEntries)
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

    private enum ElementKind : byte
    {
        Other,

        NonVisual,

        AlternateContent,

        TextBody,

        Paragraph,

        Run,

        Text,
    }

    /// <summary>
    /// A single forward pass over the children of one slide's root. It remembers what kind of element is open at each
    /// depth, which is all the context the text rules need, so nothing is buffered or retained in proportion to the
    /// document.
    /// </summary>
    private sealed class SlideTextWalker(BoundedXmlReader xml, ISessionAttachmentDocumentTextSink output)
    {
        private readonly XmlReader _reader = xml.Reader;

        private readonly ElementKind[] _path = new ElementKind[BoundedXmlReader.MaximumDepth + 1];

        private readonly bool[] _choiceTaken = new bool[BoundedXmlReader.MaximumDepth + 1];

        private int _suppressedDepth = -1;

        private int _textDepth = -1;

        private bool _advance = true;

        /// <summary>Walks to the end of the slide. Returns false as soon as the sink refuses text.</summary>
        public bool Walk()
        {
            while (true)
            {
                if (_advance && !xml.Read())
                {
                    return true;
                }

                _advance = true;

                if (_suppressedDepth >= 0)
                {
                    // Inside a hidden shape: only its end tag matters, and it is found by depth.
                    if (_reader.NodeType == XmlNodeType.EndElement && _reader.Depth == _suppressedDepth)
                    {
                        _suppressedDepth = -1;
                    }

                    continue;
                }

                bool accepted = _reader.NodeType switch
                {
                    XmlNodeType.Element => VisitElement(),
                    XmlNodeType.EndElement => VisitEndElement(),
                    XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace => VisitText(),
                    _ => true,
                };

                if (!accepted)
                {
                    return false;
                }
            }
        }

        private bool VisitElement()
        {
            int depth = _reader.Depth;

            _path[depth] = ElementKind.Other;

            string namespaceUri = _reader.NamespaceURI;

            if (namespaceUri == MarkupCompatibilityNamespace)
            {
                VisitCompatibilityElement(depth);

                return true;
            }

            if (IsPresentationNamespace(namespaceUri))
            {
                VisitPresentationElement(depth);

                return true;
            }

            return !IsDrawingNamespace(namespaceUri) || VisitDrawingElement(depth);
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

                case "txBody":
                    _path[depth] = ElementKind.TextBody;

                    break;

                case "extLst" or "custDataLst":
                    SkipCurrentElement();

                    break;

                case "cNvPr" when _path[depth - 1] == ElementKind.NonVisual && IsTrue(_reader.GetAttribute("hidden", string.Empty)):
                    // The shape is the element holding the non-visual container: two levels up from here.
                    _suppressedDepth = depth - 2;

                    if (_textDepth >= _suppressedDepth)
                    {
                        _textDepth = -1;
                    }

                    break;
            }
        }

        private bool VisitDrawingElement(int depth)
        {
            ElementKind parent = _path[depth - 1];

            switch (_reader.LocalName)
            {
                case "txBody":
                    _path[depth] = ElementKind.TextBody;

                    break;

                case "extLst":
                    SkipCurrentElement();

                    break;

                case "tc" when IsTrue(_reader.GetAttribute("hMerge", string.Empty)) || IsTrue(_reader.GetAttribute("vMerge", string.Empty)):
                    SkipCurrentElement();

                    break;

                case "p" when parent == ElementKind.TextBody:
                    if (!_reader.IsEmptyElement)
                    {
                        _path[depth] = ElementKind.Paragraph;

                        break;
                    }

                    return output.TryAppend("\n");

                case "r" or "fld" when parent == ElementKind.Paragraph:
                    _path[depth] = ElementKind.Run;

                    break;

                case "t" when parent == ElementKind.Run && !_reader.IsEmptyElement:
                    _path[depth] = ElementKind.Text;

                    _textDepth = depth;

                    break;

                case "br" when parent == ElementKind.Paragraph:
                    return output.TryAppend("\n");
            }

            return true;
        }

        private bool VisitEndElement()
        {
            switch (_path[_reader.Depth])
            {
                case ElementKind.Text:
                    _textDepth = -1;

                    return true;

                case ElementKind.Paragraph:
                    return output.TryAppend("\n");

                default:
                    return true;
            }
        }

        private bool VisitText() =>
            _textDepth < 0 || _reader.Depth != _textDepth + 1 || output.TryAppend(_reader.Value);

        /// <summary>Consumes the current element's subtree. The reader then sits on the next node, which is still to be visited.</summary>
        private void SkipCurrentElement()
        {
            xml.SkipElement();

            _advance = false;
        }
    }
}
