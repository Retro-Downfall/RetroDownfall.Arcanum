using System.Text;
using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>What a master shape contributes to an instance: its text (null when it has none worth retaining) and its HideText value.</summary>
internal readonly record struct VisioMasterShape(bool Hide, string? Text);

/// <summary>
/// The result of looking up a master shape. <see cref="Unretained"/> means the master has something the instance needs
/// that was not retained within the master retention bounds, so the instance cannot be projected faithfully.
/// </summary>
internal readonly record struct VisioMasterLookup(bool Unretained, bool Hide, string? Text);

/// <summary>
/// The masters of one Visio package. <c>masters.xml</c> is read up front (it only maps a master ID to its part), and a
/// master part is read and parsed only when an instance first needs it, then kept. Master parts are never projected on
/// their own: this class only answers what text a master shape lends to the instances that inherit from it.
/// </summary>
/// <remarks>
/// Retention is bounded twice: master text may total at most the configured number of characters (262,144), and at most
/// <see cref="MaximumRetainedShapes"/> master shapes are kept across all masters. A shape that must be dropped is
/// remembered only as a flag on its master, so a lookup that would have needed it reports
/// <see cref="VisioMasterLookup.Unretained"/> instead of guessing. Master shapes that carry neither text nor HideText
/// need no entry. A master shape's own layer membership is not applied: layers belong to the page the instance is on.
/// </remarks>
internal sealed class VisioMasterCatalog
{
    internal const int MaximumRetainedShapes = 8192;

    private readonly BoundedZipPackage _package;

    private readonly CancellationToken _cancellationToken;

    private readonly int _maximumCharacters;

    private readonly Dictionary<int, string?> _entries;

    private readonly Dictionary<int, MasterRecord> _records = [];

    private readonly StringBuilder _scratch = new();

    private int _retainedCharacters;

    private int _retainedShapes;

    private VisioMasterCatalog(
        BoundedZipPackage package,
        CancellationToken cancellationToken,
        int maximumCharacters,
        Dictionary<int, string?> entries)
    {
        _package = package;

        _cancellationToken = cancellationToken;

        _maximumCharacters = maximumCharacters;

        _entries = entries;
    }

    /// <summary>A catalog for a package that has no masters part: every lookup finds nothing.</summary>
    public static VisioMasterCatalog Empty(BoundedZipPackage package, CancellationToken cancellationToken, int maximumCharacters) =>
        new(package, cancellationToken, maximumCharacters, []);

    /// <summary>Reads <c>masters.xml</c> and its relationships. At most <paramref name="maximumMasters"/> Master elements are allowed.</summary>
    public static VisioMasterCatalog Load(
        BoundedZipPackage package,
        string mastersEntry,
        int maximumMasters,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> targets = new(StringComparer.Ordinal);

        foreach (OpcRelationship relationship in OpcPackageRelationships.Read(package, mastersEntry, cancellationToken))
        {
            if (OpcPackageRelationships.IsVisioType(relationship.Type, "master"))
            {
                targets[relationship.Id] = relationship.TargetEntryName;
            }
        }

        Dictionary<int, string?> entries = [];

        using BoundedXmlReader xml = BoundedXmlReader.Create(package.ReadEntry(mastersEntry, cancellationToken), cancellationToken);

        VisioXml.OpenRoot(xml, "Masters");

        XmlReader reader = xml.Reader;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        int masters = 0;

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (!VisioXml.IsElement(reader, "Master"))
            {
                xml.SkipElement();

                continue;
            }

            if (++masters > maximumMasters)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
            }

            int id = VisioXml.TryParseIndex(reader.GetAttribute("ID"), out int parsed) ? parsed : -1;

            string? target = null;

            VisioChildScan children = VisioXml.BeginChildren(xml);

            while (VisioXml.NextChild(xml, ref children))
            {
                if (target is null && VisioXml.IsElement(reader, "Rel"))
                {
                    string? relationshipId = reader.GetAttribute("id", VisioXml.RelationshipsNamespace);

                    if (relationshipId is not null)
                    {
                        targets.TryGetValue(relationshipId, out target);
                    }
                }

                xml.SkipElement();
            }

            if (id >= 0)
            {
                entries.TryAdd(id, target);
            }
        }

        return new VisioMasterCatalog(package, cancellationToken, maximumCharacters, entries);
    }

    /// <summary>
    /// What the shape <paramref name="masterShapeId"/> of master <paramref name="masterId"/> lends to an instance; a negative
    /// shape ID means the master's first top-level shape. A master the package does not define lends nothing.
    /// </summary>
    public VisioMasterLookup Lookup(int masterId, int masterShapeId)
    {
        if (masterId < 0 || !_entries.TryGetValue(masterId, out string? target))
        {
            return default;
        }

        MasterRecord record = GetRecord(masterId, target);

        if (masterShapeId < 0)
        {
            if (record.TopLevel is { } top)
            {
                return new VisioMasterLookup(false, top.Hide, top.Text);
            }

            return record.TopLevelUnretained ? new VisioMasterLookup(true, false, null) : default;
        }

        if (record.ById.TryGetValue(masterShapeId, out VisioMasterShape shape))
        {
            return new VisioMasterLookup(false, shape.Hide, shape.Text);
        }

        return record.Overflowed ? new VisioMasterLookup(true, false, null) : default;
    }

    private MasterRecord GetRecord(int masterId, string? target)
    {
        if (_records.TryGetValue(masterId, out MasterRecord? existing))
        {
            return existing;
        }

        if (target is null)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);
        }

        string entry = OpcPackageRelationships.ResolveEntry(_package, target)
            ?? throw new DocumentPackageRejectedException(DocumentPackageRejection.MissingPart);

        MasterRecord record = new();

        using (BoundedXmlReader xml = BoundedXmlReader.Create(_package.ReadEntry(entry, _cancellationToken), _cancellationToken))
        {
            VisioXml.OpenRoot(xml, "MasterContents");

            XmlReader reader = xml.Reader;

            VisioChildScan scan = VisioXml.BeginChildren(xml);

            while (VisioXml.NextChild(xml, ref scan))
            {
                if (VisioXml.IsElement(reader, "Shapes"))
                {
                    ReadShapes(xml, record, topLevel: true);
                }
                else
                {
                    xml.SkipElement();
                }
            }
        }

        _records[masterId] = record;

        return record;
    }

    private void ReadShapes(BoundedXmlReader xml, MasterRecord record, bool topLevel)
    {
        XmlReader reader = xml.Reader;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (VisioXml.IsElement(reader, "Shape"))
            {
                ReadShape(xml, record, topLevel);
            }
            else
            {
                xml.SkipElement();
            }
        }
    }

    private void ReadShape(BoundedXmlReader xml, MasterRecord record, bool topLevel)
    {
        XmlReader reader = xml.Reader;

        bool firstTopLevel = topLevel && !record.TopLevelSeen;

        if (topLevel)
        {
            record.TopLevelSeen = true;
        }

        if (VisioXml.Hides(VisioXml.ParseBoolean(reader.GetAttribute("Del"))) || reader.GetAttribute("Type") == "Guide")
        {
            xml.SkipElement();

            return;
        }

        int id = VisioXml.TryParseIndex(reader.GetAttribute("ID"), out int parsed) ? parsed : -1;

        bool hide = false;

        bool textSeen = false;

        bool textDropped = false;

        string? text = null;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (VisioXml.IsElement(reader, "Cell"))
            {
                if (reader.GetAttribute("N") == "HideText" && VisioXml.Hides(VisioXml.ReadBooleanCell(reader)))
                {
                    hide = true;
                }

                xml.SkipElement();
            }
            else if (VisioXml.IsElement(reader, "Text") && !textSeen)
            {
                textSeen = true;

                _scratch.Clear();

                if (!VisioXml.ReadMixedText(xml, _scratch, _maximumCharacters - _retainedCharacters))
                {
                    textDropped = true;
                }
                else if (_scratch.Length > 0)
                {
                    // Reserved now, so the sub-shapes that follow are bounded by what is left.
                    text = _scratch.ToString();

                    _retainedCharacters += text.Length;
                }

                _scratch.Clear();
            }
            else if (VisioXml.IsElement(reader, "Shapes"))
            {
                ReadShapes(xml, record, topLevel: false);
            }
            else
            {
                xml.SkipElement();
            }
        }

        if (hide)
        {
            // A hidden shape lends no text: release the reservation and keep only the fact that it is hidden.
            Release(ref text);

            textDropped = false;
        }

        bool lost = false;

        if (hide || text is not null)
        {
            if (_retainedShapes >= MaximumRetainedShapes)
            {
                Release(ref text);

                lost = true;
            }
            else
            {
                _retainedShapes++;

                VisioMasterShape shape = new(hide, text);

                if (id >= 0)
                {
                    record.ById.TryAdd(id, shape);
                }

                if (firstTopLevel)
                {
                    record.TopLevel = shape;
                }
            }
        }
        else if (textDropped)
        {
            lost = true;
        }

        if (lost)
        {
            record.Overflowed = true;

            if (firstTopLevel)
            {
                record.TopLevelUnretained = true;
            }
        }
    }

    private void Release(ref string? text)
    {
        if (text is not null)
        {
            _retainedCharacters -= text.Length;

            text = null;
        }
    }

    private sealed class MasterRecord
    {
        public Dictionary<int, VisioMasterShape> ById { get; } = [];

        public VisioMasterShape? TopLevel { get; set; }

        public bool TopLevelSeen { get; set; }

        public bool TopLevelUnretained { get; set; }

        public bool Overflowed { get; set; }
    }
}
