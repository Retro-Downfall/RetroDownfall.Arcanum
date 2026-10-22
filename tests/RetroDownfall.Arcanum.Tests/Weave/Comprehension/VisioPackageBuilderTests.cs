using System.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>Markup helpers and the extraction harness shared by the Visio test classes.</summary>
internal static class VisioFixture
{
    public const string Main = "http://schemas.microsoft.com/office/visio/2012/main";

    public const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    public const string VisioRelationshipPrefix = "http://schemas.microsoft.com/visio/2010/relationships/";

    public const string VbaProjectRelationship = "http://schemas.microsoft.com/office/2006/relationships/vbaProject";

    /// <summary>A Shape element. <paramref name="text"/> is the raw inner markup of a Text element (null: no Text element).</summary>
    public static string Shape(
        int id,
        string? text = null,
        string type = "Shape",
        string attributes = "",
        string cells = "",
        string children = "") =>
        $"<Shape ID='{id}' Type='{type}' {attributes}>{cells}"
        + (text is null ? string.Empty : $"<Text>{text}</Text>")
        + (children.Length == 0 ? string.Empty : $"<Shapes>{children}</Shapes>")
        + "</Shape>";

    public static string Cell(string name, string value) => $"<Cell N='{name}' V='{value}'/>";

    /// <summary>A PageSheet Layer section row. <paramref name="visible"/> null leaves the Visible cell out.</summary>
    public static string LayerRow(int index, string name, string? visible) =>
        $"<Row IX='{index}'>{Cell("Name", name)}{(visible is null ? string.Empty : Cell("Visible", visible))}{Cell("Print", "1")}</Row>";

    public static string LayerSection(params string[] rows) => $"<Section N='Layer'>{string.Concat(rows)}</Section>";

    public static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    public static string Xml(string rootName, string inner, string rootAttributes = "") =>
        $"<?xml version='1.0' encoding='utf-8' ?><{rootName} xmlns='{Main}' xmlns:r='{Relationships}' xml:space='preserve' {rootAttributes}>{inner}</{rootName}>";

    public static SessionAttachmentExtractionResult Extract(byte[] package, VisioDrawingExtractor? extractor = null, int sinkCharacters = 262_144)
    {
        BoundedDocumentTextSink sink = new(sinkCharacters, CancellationToken.None);

        ValueTask task = (extractor ?? new VisioDrawingExtractor()).ExtractAsync(package, sink, CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully);

        return sink.Complete();
    }

    public static DocumentPackageRejection Reject(byte[] package, VisioDrawingExtractor? extractor = null)
    {
        BoundedDocumentTextSink sink = new(262_144, CancellationToken.None);

        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(
            () => (extractor ?? new VisioDrawingExtractor()).ExtractAsync(package, sink, CancellationToken.None));

        return exception.Rejection;
    }
}

/// <summary>
/// Builds a hand-made MS-VSDX package: <c>[Content_Types].xml</c>, package and part relationships,
/// <c>visio/document.xml</c>, <c>pages.xml</c> with page parts, optional <c>masters.xml</c> with master parts,
/// plus <c>windows.xml</c>, <c>comments.xml</c> and <c>docProps/core.xml</c> carrying markers that must never be
/// projected. Any entry can be replaced, removed or added after the default graph is generated.
/// </summary>
internal sealed class VisioPackageBuilder
{
    private readonly List<PageSpec> _pages = [];

    private readonly List<MasterSpec> _masters = [];

    private readonly List<(string Name, string? Content)> _overrides = [];

    private readonly List<RawZipEntry> _raw = [];

    private int[]? _order;

    private bool _macroEnabled;

    public VisioPackageBuilder AddPage(
        string name,
        string shapes,
        string pageSheet = "",
        string pageAttributes = "",
        string? nameU = null,
        bool rawName = false,
        string pageContentsExtra = "")
    {
        _pages.Add(new PageSpec(name, nameU, rawName, shapes, pageSheet, pageAttributes, pageContentsExtra));

        return this;
    }

    public VisioPackageBuilder AddMaster(int id, string shapes, string masterSheet = "", string masterAttributes = "")
    {
        _masters.Add(new MasterSpec(id, shapes, masterSheet, masterAttributes));

        return this;
    }

    /// <summary>Lists the pages in pages.xml in this order (indices into the added pages); part names keep their add order.</summary>
    public VisioPackageBuilder Reorder(params int[] order)
    {
        _order = order;

        return this;
    }

    public VisioPackageBuilder MacroEnabled()
    {
        _macroEnabled = true;

        return this;
    }

    public VisioPackageBuilder Replace(string entryName, string content)
    {
        _overrides.Add((entryName, content));

        return this;
    }

    public VisioPackageBuilder Remove(string entryName)
    {
        _overrides.Add((entryName, null));

        return this;
    }

    public VisioPackageBuilder AddRaw(RawZipEntry entry)
    {
        _raw.Add(entry);

        return this;
    }

    public byte[] Build() => RawZipPackage.Of([.. Entries().Select(entry => RawZipEntry.Deflated(entry.Name, entry.Content)), .. _raw]).ToBytes();

    public List<(string Name, string Content)> Entries()
    {
        List<(string Name, string Content)> entries = [];

        entries.Add(("[Content_Types].xml", ContentTypes()));

        entries.Add(("_rels/.rels", Rels(
            ("rId1", VisioFixture.VisioRelationshipPrefix + "document", "visio/document.xml", false),
            ("rId2", VisioFixture.PackageRelationships + "/metadata/core-properties", "docProps/core.xml", false))));

        entries.Add(("docProps/core.xml", "<?xml version='1.0' encoding='UTF-8' standalone='yes'?><cp:coreProperties xmlns:cp='http://schemas.openxmlformats.org/package/2006/metadata/core-properties' xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>CORE-MARKER</dc:title></cp:coreProperties>"));

        entries.Add(("visio/document.xml", DocumentXml()));

        List<(string Id, string Type, string Target, bool External)> documentRels =
        [
            ("rId1", VisioFixture.VisioRelationshipPrefix + "pages", "pages/pages.xml", false),
        ];

        if (_masters.Count > 0)
        {
            documentRels.Add(("rId2", VisioFixture.VisioRelationshipPrefix + "masters", "masters/masters.xml", false));
        }

        documentRels.Add(("rId3", VisioFixture.VisioRelationshipPrefix + "windows", "windows.xml", false));

        documentRels.Add(("rId4", VisioFixture.VisioRelationshipPrefix + "comments", "comments.xml", false));

        if (_macroEnabled)
        {
            documentRels.Add(("rId5", VisioFixture.VbaProjectRelationship, "vbaProject.bin", false));
        }

        entries.Add(("visio/_rels/document.xml.rels", Rels([.. documentRels])));

        entries.Add(("visio/windows.xml", VisioFixture.Xml("Windows", "<Window ID='0' WindowType='Drawing' ViewScale='-1'><StencilGroup>WINDOW-MARKER</StencilGroup></Window>", "ClientWidth='1024' ClientHeight='768'")));

        entries.Add(("visio/comments.xml", VisioFixture.Xml("Comments", "<CommentList><CommentEntry AuthorID='0' PageID='0' ShapeID='1' CommentID='1'>COMMENT-MARKER</CommentEntry></CommentList>")));

        entries.Add(("visio/pages/pages.xml", PagesXml()));

        entries.Add(("visio/pages/_rels/pages.xml.rels", Rels([.. _pages.Select((_, index) => ($"rId{index + 1}", VisioFixture.VisioRelationshipPrefix + "page", $"page{index + 1}.xml", false))])));

        for (int index = 0; index < _pages.Count; index++)
        {
            PageSpec page = _pages[index];

            entries.Add(($"visio/pages/page{index + 1}.xml", VisioFixture.Xml("PageContents", $"<Shapes>{page.Shapes}</Shapes>{page.ContentsExtra}")));
        }

        if (_masters.Count > 0)
        {
            entries.Add(("visio/masters/masters.xml", MastersXml()));

            entries.Add(("visio/masters/_rels/masters.xml.rels", Rels([.. _masters.Select((_, index) => ($"rId{index + 1}", VisioFixture.VisioRelationshipPrefix + "master", $"master{index + 1}.xml", false))])));

            for (int index = 0; index < _masters.Count; index++)
            {
                entries.Add(($"visio/masters/master{index + 1}.xml", VisioFixture.Xml("MasterContents", $"<Shapes>{_masters[index].Shapes}</Shapes>")));
            }
        }

        foreach ((string name, string? content) in _overrides)
        {
            int existing = entries.FindIndex(entry => entry.Name == name);

            if (content is null)
            {
                if (existing >= 0)
                {
                    entries.RemoveAt(existing);
                }
            }
            else if (existing >= 0)
            {
                entries[existing] = (name, content);
            }
            else
            {
                entries.Add((name, content));
            }
        }

        return entries;
    }

    /// <summary>An entry that a macro-enabled package references: garbage where deflate data should be, and a bomb-sized declared length.</summary>
    public static RawZipEntry UninflatableEntry(string name) =>
        new(ComprehensionPackageBuilder.Utf8Bytes(name), new byte[64], 8)
        {
            DataAreaBytes = [.. Enumerable.Repeat((byte)0xFF, 64)],
            UncompressedSize = int.MaxValue,
        };

    public static string Rels(params (string Id, string Type, string Target, bool External)[] relationships) =>
        $"<?xml version='1.0' encoding='UTF-8' standalone='yes'?><Relationships xmlns='{VisioFixture.PackageRelationships}'>"
        + string.Concat(relationships.Select(relationship =>
            $"<Relationship Id='{relationship.Id}' Type='{relationship.Type}' Target='{VisioFixture.Escape(relationship.Target)}'{(relationship.External ? " TargetMode='External'" : string.Empty)}/>"))
        + "</Relationships>";

    private string ContentTypes() =>
        "<?xml version='1.0' encoding='UTF-8' standalone='yes'?><Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'>"
        + "<Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/>"
        + "<Default Extension='xml' ContentType='application/xml'/>"
        + (_macroEnabled ? "<Default Extension='bin' ContentType='application/vnd.ms-office.vbaProject'/>" : string.Empty)
        + $"<Override PartName='/visio/document.xml' ContentType='application/vnd.ms-visio.drawing{(_macroEnabled ? ".macroEnabled" : string.Empty)}.main+xml'/>"
        + "<Override PartName='/visio/pages/pages.xml' ContentType='application/vnd.ms-visio.pages+xml'/>"
        + "<Override PartName='/visio/windows.xml' ContentType='application/vnd.ms-visio.windows+xml'/>"
        + "</Types>";

    private static string DocumentXml() => VisioFixture.Xml(
        "VisioDocument",
        "<DocumentSettings TopPage='0' DefaultTextStyle='3' DefaultLineStyle='3' DefaultFillStyle='3' DefaultGuideStyle='4'><GlueSettings>9</GlueSettings><SnapSettings>65847</SnapSettings><SnapExtensions>34</SnapExtensions><DynamicGridEnabled>1</DynamicGridEnabled></DocumentSettings>"
        + "<Colors><ColorEntry IX='24' RGB='#E8EEF7'/></Colors>"
        + "<FaceNames><FaceName ID='1' Name='Calibri' UnicodeRanges='-536870145 -1073732485 9 0' CharSets='1073741823 0' Panose='2 15 5 2 2 2 4 3 2 4' Flags='325'/></FaceNames>"
        + "<StyleSheets><StyleSheet ID='0' NameU='No Style' IsCustomNameU='1' Name='No Style'><Cell N='EnableLineProps' V='1'/><Cell N='LineWeight' V='0.01041666666666667'/></StyleSheet></StyleSheets>"
        + "<DocumentSheet ID='0' NameU='TheDoc'><Section N='User'><Row N='msvNoAutoConnect'><Cell N='Value' V='1'/></Row></Section></DocumentSheet>");

    private string PagesXml()
    {
        IEnumerable<int> order = _order ?? [.. Enumerable.Range(0, _pages.Count)];

        string pages = string.Concat(order.Select(index =>
        {
            PageSpec page = _pages[index];

            string name = page.Name.Length == 0 ? string.Empty : $" Name='{(page.RawName ? page.Name : VisioFixture.Escape(page.Name))}'";

            string? universal = page.NameU ?? (page.RawName || page.Name.Length == 0 ? null : page.Name);

            string nameU = universal is null ? string.Empty : $" NameU='{VisioFixture.Escape(universal)}'";

            return $"<Page ID='{index}'{name}{nameU} ViewScale='-1' ViewCenterX='4.25' ViewCenterY='5.5' {page.Attributes}>"
                + $"<PageSheet LineStyle='0' FillStyle='0' TextStyle='0'><Cell N='PageWidth' V='8.5'/><Cell N='PageHeight' V='11'/>{page.PageSheet}</PageSheet>"
                + $"<Rel r:id='rId{index + 1}'/></Page>";
        }));

        return VisioFixture.Xml("Pages", pages);
    }

    private string MastersXml() => VisioFixture.Xml(
        "Masters",
        string.Concat(_masters.Select((master, index) =>
            $"<Master ID='{master.Id}' NameU='Stencil {master.Id}' IsCustomNameU='1' Name='Stencil {master.Id}' Prompt='' IconSize='1' AlignName='2' MatchByName='0' IconUpdate='1' UniqueID='{{00000000-0000-0000-0000-{master.Id:D12}}}' BaseID='{{00000000-0000-0000-0000-{master.Id:D12}}}' PatternFlags='0' Hidden='0' MasterType='2' {master.Attributes}>"
            + $"<PageSheet LineStyle='0' FillStyle='0' TextStyle='0'><Cell N='PageWidth' V='1'/>{master.PageSheet}</PageSheet>"
            + $"<Rel r:id='rId{index + 1}'/></Master>")));

    private sealed record PageSpec(string Name, string? NameU, bool RawName, string Shapes, string PageSheet, string Attributes, string ContentsExtra);

    private sealed record MasterSpec(int Id, string Shapes, string PageSheet, string Attributes);
}

/// <summary>Checks that the hand-built packages are the graph the Visio extractor is specified to walk.</summary>
public sealed class VisioPackageBuilderTests
{
    [Fact]
    public void Build_ProducesAnOpcGraphFromTheRootRelationshipToPagesAndMasters()
    {
        byte[] bytes = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "A"))
            .AddMaster(2, VisioFixture.Shape(1, "M"))
            .Build();

        BoundedZipPackage package = BoundedZipPackage.Open(bytes, CancellationToken.None);

        OpcRelationship document = Assert.Single(
            OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None),
            relationship => OpcPackageRelationships.IsVisioType(relationship.Type, "document"));

        Assert.Equal("visio/document.xml", document.TargetEntryName);

        IReadOnlyList<OpcRelationship> parts = OpcPackageRelationships.Read(package, document.TargetEntryName, CancellationToken.None);

        Assert.Equal(
            ["visio/pages/pages.xml", "visio/masters/masters.xml", "visio/windows.xml", "visio/comments.xml"],
            parts.Select(relationship => relationship.TargetEntryName));

        Assert.Equal("visio/pages/page1.xml", Assert.Single(OpcPackageRelationships.Read(package, "visio/pages/pages.xml", CancellationToken.None)).TargetEntryName);

        Assert.Equal("visio/masters/master1.xml", Assert.Single(OpcPackageRelationships.Read(package, "visio/masters/masters.xml", CancellationToken.None)).TargetEntryName);
    }

    [Fact]
    public void Build_MacroEnabled_ReferencesAVbaProjectThatCannotBeInflated()
    {
        byte[] bytes = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "A"))
            .MacroEnabled()
            .AddRaw(VisioPackageBuilder.UninflatableEntry("visio/vbaProject.bin"))
            .Build();

        BoundedZipPackage package = BoundedZipPackage.Open(bytes, CancellationToken.None);

        Assert.Contains(
            OpcPackageRelationships.Read(package, "visio/document.xml", CancellationToken.None),
            relationship => relationship.Type == VisioFixture.VbaProjectRelationship && relationship.TargetEntryName == "visio/vbaProject.bin");

        // Reading it would fail: the test proves that the extractor, which must succeed, never reads it.
        Assert.Throws<DocumentPackageRejectedException>(() => package.ReadEntry("visio/vbaProject.bin", CancellationToken.None));
    }
}
