using System.Text;
using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// What a master shape contributes to an instance: whether its HideText hides, whether it is a group whose DisplayMode
/// hides the group shape and text, its text (null when it has none worth retaining), and its layer membership (null when it
/// names none; <see cref="VisioXml.UnknownLayerMembership"/> when it could not be read or retained).
/// </summary>
internal readonly record struct VisioMasterShape(bool Hide, bool HideGroup, string? Text, string? LayerMembers);

/// <summary>
/// The result of looking up a master shape. <see cref="Unretained"/> means the master has something the instance needs
/// that was not retained within the master retention bounds, so the instance cannot be projected faithfully.
/// </summary>
internal readonly record struct VisioMasterLookup(bool Unretained, bool Hide, bool HideGroup, string? Text, string? LayerMembers)
{
    public static VisioMasterLookup NotRetained => new(true, false, false, null, null);

    public static VisioMasterLookup Of(VisioMasterShape shape) => new(false, shape.Hide, shape.HideGroup, shape.Text, shape.LayerMembers);
}

/// <summary>
/// The masters of one Visio package. <c>masters.xml</c> is read up front (it only maps a master ID to its part), and a
/// master part is read and parsed only when an instance first needs it, then kept. Master parts are never projected on
/// their own: this class only answers what text a master shape lends to the instances that inherit from it.
/// </summary>
/// <remarks>
/// <para>
/// Retention is bounded three times: master text may total at most the configured number of characters (262,144), at most
/// <see cref="MaximumRetainedShapes"/> master shapes are kept across all masters, and layer memberships may total at most
/// <see cref="MaximumRetainedLayerCharacters"/> characters (<see cref="MaximumLayerMembershipCharacters"/> for one shape);
/// a membership beyond its bound is kept as <see cref="VisioXml.UnknownLayerMembership"/>, which hides on any page that has an
/// invisible layer. A shape that must be dropped is remembered only as a flag on its master, so a lookup that would have
/// needed it reports <see cref="VisioMasterLookup.Unretained"/> instead of guessing. Master shapes that carry no text,
/// no HideText, no group DisplayMode that hides and no layer membership need no entry.
/// </para>
/// <para>
/// An instance takes what it does not say itself from its master shape, layer membership included: a master shape on a
/// layer that is invisible on the page hides the instance (and, as ever, everything under it) unless the instance has a
/// LayerMember cell of its own. A top-level instance (a <c>Master</c> and no <c>MasterShape</c>) stands for the master's
/// top-level shape only when the master has exactly one that is neither deleted nor a guide; with several there is no one
/// shape it could stand for and it inherits nothing for itself, while its sub-shapes still inherit through
/// <c>MasterShape</c>.
/// </para>
/// <para>
/// A part is read for one master only: two Master elements that name the same part are refused as a format mismatch when
/// <c>masters.xml</c> is read, because a part shared by master IDs would be inflated and parsed once per ID.
/// </para>
/// </remarks>
internal sealed class VisioMasterCatalog
{
    internal const int MaximumRetainedShapes = 8192;

    /// <summary>The most characters of layer membership kept for one master shape; a longer one is kept as unknown.</summary>
    internal const int MaximumLayerMembershipCharacters = 512;

    /// <summary>The most characters of layer membership kept across all master shapes; beyond it a membership is kept as unknown.</summary>
    internal const int MaximumRetainedLayerCharacters = 65_536;

    private readonly BoundedZipPackage _package;

    private readonly CancellationToken _cancellationToken;

    private readonly int _maximumCharacters;

    private readonly Dictionary<int, string?> _entries;

    private readonly Dictionary<int, MasterRecord> _records = [];

    private readonly StringBuilder _scratch = new();

    private int _retainedCharacters;

    private int _retainedShapes;

    private int _retainedLayerCharacters;

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

        HashSet<string> parts = new(StringComparer.OrdinalIgnoreCase);

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

            // Real producers give every master its own part. A part named by two Master elements would be inflated and parsed
            // once per master ID, so the second reference is refused here rather than read again. Case and one percent-unescape
            // are folded as the lookup does, so a part cannot be shared under two spellings.
            if (target is not null && OpcPackageRelationships.ResolveEntry(package, target) is { } part && !parts.Add(part))
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
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
    /// shape ID means the master's top-level shape, which it has only when exactly one top-level shape is neither deleted nor
    /// a guide (with several, an instance with no <c>MasterShape</c> inherits nothing). A master the package does not define
    /// lends nothing.
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
            if (record.TopLevelCount != 1)
            {
                return default;
            }

            if (record.TopLevel is { } top)
            {
                return VisioMasterLookup.Of(top);
            }

            return record.TopLevelUnretained ? VisioMasterLookup.NotRetained : default;
        }

        if (record.ById.TryGetValue(masterShapeId, out VisioMasterShape shape))
        {
            return VisioMasterLookup.Of(shape);
        }

        return record.Overflowed ? VisioMasterLookup.NotRetained : default;
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

        // A deleted shape and a guide are not drawn: they lend nothing and are not a top-level shape an instance could stand for.
        if (VisioXml.Hides(VisioXml.ParseBoolean(reader.GetAttribute("Del"))) || reader.GetAttribute("Type") == "Guide")
        {
            xml.SkipElement();

            return;
        }

        bool firstTopLevel = false;

        if (topLevel)
        {
            firstTopLevel = record.TopLevelCount == 0;

            record.TopLevelCount++;
        }

        int id = VisioXml.TryParseIndex(reader.GetAttribute("ID"), out int parsed) ? parsed : -1;

        bool isGroup = reader.GetAttribute("Type") == "Group";

        bool hide = false;

        bool groupHidden = false;

        string? layerMembers = null;

        bool textSeen = false;

        bool textDropped = false;

        string? text = null;

        VisioChildScan scan = VisioXml.BeginChildren(xml);

        while (VisioXml.NextChild(xml, ref scan))
        {
            if (VisioXml.IsElement(reader, "Cell"))
            {
                ReadCell(reader, isGroup, ref hide, ref groupHidden, ref layerMembers);

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

        if (hide || groupHidden)
        {
            // A hidden shape lends no text: release the reservation and keep only the fact that it is hidden.
            Release(ref text);

            textDropped = false;
        }

        if (string.IsNullOrWhiteSpace(layerMembers))
        {
            layerMembers = null;
        }

        bool lost = false;

        if (hide || groupHidden || text is not null || layerMembers is not null)
        {
            if (_retainedShapes >= MaximumRetainedShapes)
            {
                Release(ref text);

                lost = true;
            }
            else
            {
                _retainedShapes++;

                VisioMasterShape shape = new(hide, groupHidden, text, RetainLayerMembers(layerMembers));

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

    private static void ReadCell(XmlReader reader, bool isGroup, ref bool hide, ref bool groupHidden, ref string? layerMembers)
    {
        string? name = reader.GetAttribute("N");

        if (name == "HideText")
        {
            if (VisioXml.Hides(VisioXml.ReadBooleanCell(reader)))
            {
                hide = true;
            }
        }
        else if (name == "DisplayMode")
        {
            // A Group Properties cell: it only means something on a group.
            if (isGroup && VisioXml.HidesGroup(VisioXml.ReadBooleanCell(reader)))
            {
                groupHidden = true;
            }
        }
        else if (name == "LayerMember" && VisioXml.ReadLayerMember(reader) is { } members)
        {
            // Duplicate cells all count, as they do on a page: a page hides the shape when any of them lists an invisible layer.
            // Once the combined list outgrows what is kept for one shape it is unknown, and stays so without growing further.
            if (layerMembers is null)
            {
                layerMembers = members;
            }
            else if (layerMembers != VisioXml.UnknownLayerMembership)
            {
                layerMembers = (long)layerMembers.Length + members.Length + 1 > MaximumLayerMembershipCharacters
                    ? VisioXml.UnknownLayerMembership
                    : $"{layerMembers};{members}";
            }
        }
    }

    /// <summary>
    /// Charges a layer membership to the retention budget. One that is too long, or that no longer fits the budget, is kept as
    /// unknown, which hides conservatively on a page that has an invisible layer.
    /// </summary>
    private string? RetainLayerMembers(string? members)
    {
        if (members is null || members == VisioXml.UnknownLayerMembership)
        {
            return members;
        }

        if (members.Length > MaximumLayerMembershipCharacters || (long)_retainedLayerCharacters + members.Length > MaximumRetainedLayerCharacters)
        {
            return VisioXml.UnknownLayerMembership;
        }

        _retainedLayerCharacters += members.Length;

        return members;
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

        /// <summary>How many top-level shapes are neither deleted nor guides; an instance with no MasterShape needs exactly one.</summary>
        public int TopLevelCount { get; set; }

        public bool TopLevelUnretained { get; set; }

        public bool Overflowed { get; set; }
    }
}
