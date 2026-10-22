using System.Text;
using System.Xml;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>One extraction run over one Visio package. Every read method returns false when extraction must stop.</summary>
internal sealed class VisioDrawingExtraction(
    ISessionAttachmentDocumentTextSink output,
    CancellationToken cancellationToken,
    int maximumPages,
    int maximumMasters,
    int maximumMasterCharacters)
{
    /// <summary>The most text one top-level shape (with its sub-shapes) may buffer before it is flushed; four times the output ceiling.</summary>
    internal const int MaximumPendingCharacters = 1_048_576;

    private const char LineSeparator = (char)0x2028;

    private const char ParagraphSeparator = (char)0x2029;

    private readonly StringBuilder _pending = new();

    private BoundedZipPackage _package = null!;

    private VisioLayerSet? _layers;

    private VisioMasterCatalog _masters = null!;

    public void Run(ReadOnlyMemory<byte> bytes)
    {
        _package = BoundedZipPackage.Open(bytes, cancellationToken);

        IReadOnlyList<OpcRelationship> packageRelationships = OpcPackageRelationships.Read(_package, string.Empty, cancellationToken);

        string documentEntry = ResolvePart(Find(packageRelationships, "document"));

        using (BoundedXmlReader document = BoundedXmlReader.Create(_package.ReadEntry(documentEntry, cancellationToken), cancellationToken))
        {
            VisioXml.OpenRoot(document, "VisioDocument");
        }

        IReadOnlyList<OpcRelationship> documentRelationships = OpcPackageRelationships.Read(_package, documentEntry, cancellationToken);

        string pagesEntry = ResolvePart(Find(documentRelationships, "pages"));

        OpcRelationship? mastersRelationship = Find(documentRelationships, "masters");

        _masters = mastersRelationship is null
            ? VisioMasterCatalog.Empty(_package, cancellationToken, maximumMasterCharacters)
            : VisioMasterCatalog.Load(_package, ResolvePart(mastersRelationship), maximumMasters, maximumMasterCharacters, cancellationToken);

        ReadPages(pagesEntry);
    }

    private static OpcRelationship? Find(IReadOnlyList<OpcRelationship> relationships, string visioType)
    {
        foreach (OpcRelationship relationship in relationships)
        {
            if (OpcPackageRelationships.IsVisioType(relationship.Type, visioType))
            {
                return relationship;
            }
        }

        return null;
    }

    private string ResolvePart(OpcRelationship? relationship)
    {
        if (relationship is null)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
        }

        return OpcPackageRelationships.ResolveEntry(_package, relationship.TargetEntryName)
            ?? throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
    }

    private void ReadPages(string pagesEntry)
    {
        Dictionary<string, string> pageEntries = new(StringComparer.Ordinal);

        foreach (OpcRelationship relationship in OpcPackageRelationships.Read(_package, pagesEntry, cancellationToken))
        {
            if (OpcPackageRelationships.IsVisioType(relationship.Type, "page"))
            {
                pageEntries[relationship.Id] = relationship.TargetEntryName;
            }
        }

        using BoundedXmlReader xml = BoundedXmlReader.Create(_package.ReadEntry(pagesEntry, cancellationToken), cancellationToken);

        VisioXml.OpenRoot(xml, "Pages");

        XmlReader reader = xml.Reader;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        int pages = 0;

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (!VisioXml.IsElement(reader, "Page"))
            {
                xml.SkipElement();

                continue;
            }

            if (++pages > maximumPages)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
            }

            if (!ReadPage(xml, pageEntries))
            {
                return;
            }
        }
    }

    private bool ReadPage(BoundedXmlReader xml, Dictionary<string, string> pageEntries)
    {
        XmlReader reader = xml.Reader;

        string? name = reader.GetAttribute("Name");

        string? universalName = reader.GetAttribute("NameU");

        string? relationshipId = null;

        bool hiddenFromInterface = false;

        VisioLayerSet? layers = null;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (VisioXml.IsElement(reader, "PageSheet"))
            {
                ReadPageSheet(xml, ref hiddenFromInterface, ref layers);
            }
            else if (VisioXml.IsElement(reader, "Rel"))
            {
                relationshipId ??= reader.GetAttribute("id", VisioXml.RelationshipsNamespace);

                xml.SkipElement();
            }
            else
            {
                xml.SkipElement();
            }
        }

        if (hiddenFromInterface)
        {
            return true;
        }

        if (relationshipId is null || !pageEntries.TryGetValue(relationshipId, out string? target))
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
        }

        string entry = OpcPackageRelationships.ResolveEntry(_package, target)
            ?? throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);

        cancellationToken.ThrowIfCancellationRequested();

        if (!AppendPageName(string.IsNullOrEmpty(name) ? universalName ?? string.Empty : name))
        {
            return false;
        }

        _layers = layers;

        return ReadPageContents(entry);
    }

    private static void ReadPageSheet(BoundedXmlReader xml, ref bool hiddenFromInterface, ref VisioLayerSet? layers)
    {
        XmlReader reader = xml.Reader;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (VisioXml.IsElement(reader, "Cell") && reader.GetAttribute("N") == "UIVisibility")
            {
                // A page hidden from the Visio user interface (this is also how markup overlay pages are stored).
                if (VisioXml.Hides(VisioXml.ReadBooleanCell(reader)))
                {
                    hiddenFromInterface = true;
                }

                xml.SkipElement();
            }
            else if (VisioXml.IsElement(reader, "Section") && reader.GetAttribute("N") == "Layer")
            {
                ReadLayerSection(xml, ref layers);
            }
            else
            {
                xml.SkipElement();
            }
        }
    }

    private static void ReadLayerSection(BoundedXmlReader xml, ref VisioLayerSet? layers)
    {
        XmlReader reader = xml.Reader;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (!VisioXml.IsElement(reader, "Row"))
            {
                xml.SkipElement();

                continue;
            }

            // A layer is referenced by its row index; a row whose index cannot be read could not be applied to any shape.
            if (!VisioXml.TryParseIndex(reader.GetAttribute("IX"), out int index))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
            }

            bool invisible = false;

            VisioChildScan row = VisioXml.BeginChildren(xml);

            while (VisioXml.NextChild(xml, ref row))
            {
                if (VisioXml.IsElement(reader, "Cell")
                    && reader.GetAttribute("N") == "Visible"
                    && VisioXml.IsInvisible(VisioXml.ReadBooleanCell(reader)))
                {
                    invisible = true;
                }

                xml.SkipElement();
            }

            if (invisible)
            {
                layers ??= new VisioLayerSet();

                layers.MarkInvisible(index);
            }
        }
    }

    /// <summary>Appends <c>Page: &lt;name&gt;</c> and a line break, flattening control characters so a name stays on one line.</summary>
    private bool AppendPageName(string name)
    {
        if (!output.TryAppend("Page: "))
        {
            return false;
        }

        Span<char> buffer = stackalloc char[512];

        for (int offset = 0; offset < name.Length; offset += buffer.Length)
        {
            int count = Math.Min(buffer.Length, name.Length - offset);

            for (int index = 0; index < count; index++)
            {
                char current = name[offset + index];

                buffer[index] = char.IsControl(current) || current is LineSeparator or ParagraphSeparator ? ' ' : current;
            }

            if (!output.TryAppend(buffer[..count]))
            {
                return false;
            }
        }

        return output.TryAppend("\n");
    }

    private bool ReadPageContents(string entry)
    {
        using BoundedXmlReader xml = BoundedXmlReader.Create(_package.ReadEntry(entry, cancellationToken), cancellationToken);

        VisioXml.OpenRoot(xml, "PageContents");

        XmlReader reader = xml.Reader;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (VisioXml.IsElement(reader, "Shapes"))
            {
                if (!ReadShapes(xml, topLevel: true, contextMaster: -1))
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

    private bool ReadShapes(BoundedXmlReader xml, bool topLevel, int contextMaster)
    {
        XmlReader reader = xml.Reader;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (VisioXml.IsElement(reader, "Shape"))
            {
                if (!ReadShape(xml, topLevel, contextMaster))
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

    private bool ReadShape(BoundedXmlReader xml, bool topLevel, int contextMaster)
    {
        XmlReader reader = xml.Reader;

        // A locally deleted shape and a guide are not drawn text; both are skipped with their sub-shapes.
        if (VisioXml.Hides(VisioXml.ParseBoolean(reader.GetAttribute("Del"))) || reader.GetAttribute("Type") == "Guide")
        {
            xml.SkipElement();

            return true;
        }

        // A shape that is an instance of a master takes what it does not say itself from the master's shape: the top-level
        // shape of its Master, or, for a sub-shape, the MasterShape within the master of the group it is inside. A shape
        // with neither attribute (one added locally inside an instantiated group) has no counterpart and inherits nothing.
        bool hasOwnMaster = VisioXml.TryParseIndex(reader.GetAttribute("Master"), out int ownMaster);

        bool hasMasterShape = VisioXml.TryParseIndex(reader.GetAttribute("MasterShape"), out int parsedMasterShape);

        int master = hasOwnMaster ? ownMaster : contextMaster;

        int masterShape = hasMasterShape ? parsedMasterShape : -1;

        bool inheritsFromMaster = master >= 0 && (hasOwnMaster || hasMasterShape);

        int shapeStart = _pending.Length;

        int ownStart = 0;

        int ownEnd = 0;

        bool? hideCell = null;

        bool layerHidden = false;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        bool textSeen = false;

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (VisioXml.IsElement(reader, "Cell"))
            {
                ReadShapeCell(xml, ref hideCell, ref layerHidden);
            }
            else if (VisioXml.IsElement(reader, "Text") && !textSeen)
            {
                textSeen = true;

                if (layerHidden)
                {
                    xml.SkipElement();

                    continue;
                }

                ownStart = _pending.Length;

                if (!VisioXml.ReadMixedText(xml, _pending, MaximumPendingCharacters - 1))
                {
                    output.MarkTruncated();

                    return false;
                }

                if (_pending.Length > ownStart)
                {
                    _pending.Append('\n');
                }

                ownEnd = _pending.Length;
            }
            else if (VisioXml.IsElement(reader, "Shapes") && !layerHidden)
            {
                if (!ReadShapes(xml, topLevel: false, master))
                {
                    return false;
                }
            }
            else
            {
                xml.SkipElement();
            }
        }

        if (layerHidden)
        {
            // The shape and everything under it was on an invisible layer: drop whatever was buffered for it.
            _pending.Length = shapeStart;
        }
        else
        {
            VisioMasterLookup inherited = default;

            if (inheritsFromMaster && (!textSeen || hideCell is null))
            {
                inherited = _masters.Lookup(master, masterShape);

                if (inherited.Unretained)
                {
                    output.MarkTruncated();

                    return false;
                }
            }

            // HideText is inherited like any other cell; the shape's own cell, even a false one, overrides the master's.
            bool hideText = hideCell ?? inherited.Hide;

            if (textSeen)
            {
                if (hideText && ownEnd > ownStart)
                {
                    _pending.Remove(ownStart, ownEnd - ownStart);
                }
            }
            else if (!hideText && inherited.Text is { } inheritedText)
            {
                if ((long)_pending.Length + inheritedText.Length + 1 > MaximumPendingCharacters)
                {
                    output.MarkTruncated();

                    return false;
                }

                _pending.Insert(shapeStart, inheritedText).Insert(shapeStart + inheritedText.Length, '\n');
            }
        }

        return !topLevel || Flush();
    }

    private void ReadShapeCell(BoundedXmlReader xml, ref bool? hideCell, ref bool layerHidden)
    {
        XmlReader reader = xml.Reader;

        string? name = reader.GetAttribute("N");

        if (name == "HideText")
        {
            VisioCellValue value = VisioXml.ReadBooleanCell(reader);

            if (value != VisioCellValue.Unspecified)
            {
                // Duplicates cannot un-hide: any cell that hides wins.
                hideCell = (hideCell ?? false) || VisioXml.Hides(value);
            }
        }
        else if (name == "LayerMember" && VisioXml.LayerMemberHides(reader, _layers))
        {
            layerHidden = true;
        }

        xml.SkipElement();
    }

    private bool Flush()
    {
        try
        {
            foreach (ReadOnlyMemory<char> chunk in _pending.GetChunks())
            {
                if (!output.TryAppend(chunk.Span))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            _pending.Clear();
        }
    }
}
