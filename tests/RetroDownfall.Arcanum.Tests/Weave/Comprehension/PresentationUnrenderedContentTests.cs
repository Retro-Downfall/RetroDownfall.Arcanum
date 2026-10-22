namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// PowerPoint text is read only where a viewer gets it: the text body of a shape and the text bodies of a table's cells,
/// the table itself being graphic data of the table kind. Graphic data of any other kind, extension elements, elements
/// from a namespace the extractor does not read, and text bodies or tables placed anywhere else are never rendered, so a
/// paragraph in them is not projected.
/// </summary>
public sealed class PresentationUnrenderedContentTests
{
    private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";

    private static string Paragraph(string text) => PresentationDeck.Paragraph(text);

    private static string Shape(int id, string paragraphs, string properties = "") => PresentationDeck.Shape(id, paragraphs, properties);

    private static string TextBody(string text) => $"<p:txBody><a:bodyPr/><a:lstStyle/>{Paragraph(text)}</p:txBody>";

    private static string Table(string text) =>
        $"<a:tbl><a:tblPr/><a:tblGrid/><a:tr h=\"1\"><a:tc><a:txBody><a:bodyPr/><a:lstStyle/>{Paragraph(text)}</a:txBody><a:tcPr/></a:tc></a:tr></a:tbl>";

    private static string Frame(string graphicDataAttributes, string content) =>
        "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Frame\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm/>"
        + $"<a:graphic><a:graphicData{graphicDataAttributes}>{content}</a:graphicData></a:graphic></p:graphicFrame>";

    private static Task<string> TextOfAsync(string shapes) =>
        PresentationDeck.TextOfAsync(PresentationDeck.Deck(PresentationDeck.Slide(shapes + Shape(9, Paragraph("Visible")))));

    // ---------------------------------------------------------------- graphic data

    [Theory]
    [InlineData(" uri=\"urn:example:unknown-graphic\"")]
    [InlineData(" uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"")]
    [InlineData(" uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"")]
    [InlineData(" uri=\"http://schemas.openxmlformats.org/presentationml/2006/ole\"")]
    [InlineData(" uri=\"\"")]
    [InlineData("")]
    public async Task Extract_GraphicDataOfAKindThatIsNotATable_IsNotWalked(string graphicDataAttributes)
    {
        // A text body and a table placed in graphic data a viewer draws as something else (or not at all).
        string frame = Frame(graphicDataAttributes, TextBody("GRAPHICDATATEXTLEAK") + Table("GRAPHICDATATABLELEAK"));

        Assert.Equal("Slide 1\nVisible\n", await TextOfAsync(frame));
    }

    [Fact]
    public async Task Extract_GraphicDataOfTheTableKind_ProjectsItsTableAndNotATextBodyBesideIt()
    {
        string frame = Frame($" uri=\"{TableUri}\"", TextBody("BESIDETHETABLE") + Table("Cell"));

        Assert.Equal("Slide 1\nCell\nVisible\n", await TextOfAsync(frame));
    }

    [Fact]
    public async Task Extract_StrictTableGraphicData_ProjectsItsTable()
    {
        // The strict dialect names the table kind with its own uri.
        string slide =
            $"{PresentationDeck.Declaration}<p:sld xmlns:a=\"{PresentationDeck.StrictA}\" xmlns:r=\"{PresentationDeck.StrictR}\" xmlns:p=\"{PresentationDeck.StrictP}\">"
            + "<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>"
            + Frame(" uri=\"http://purl.oclc.org/ooxml/drawingml/table\"", Table("Strict cell"))
            + Frame(" uri=\"http://purl.oclc.org/ooxml/drawingml/chart\"", Table("STRICTCHARTLEAK"))
            + "</p:spTree></p:cSld></p:sld>";

        List<(string Name, string Content)> entries =
        [
            ("_rels/.rels", PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.StrictType("officeDocument"), "ppt/presentation.xml"))),
            ("ppt/presentation.xml", PresentationDeck.Presentation(PresentationDeck.StrictP, PresentationDeck.StrictR, "rId1")),
            (
                "ppt/_rels/presentation.xml.rels",
                PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.StrictType("slide"), "slides/slide1.xml"))),
            ("ppt/slides/slide1.xml", slide),
        ];

        Assert.Equal("Slide 1\nStrict cell\n", await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    // ---------------------------------------------------------------- extension elements

    [Fact]
    public async Task Extract_ExtensionElementOutsideAnExtensionList_IsNotWalked()
    {
        string shape =
            "<p:sp><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1\" cy=\"1\">" + Shape(6, Paragraph("DRAWINGEXTLEAK")) + "</a:ext></a:xfrm></p:spPr>"
            + "<p:txBody><a:bodyPr/><a:lstStyle/>" + Paragraph("Shown") + "</p:txBody></p:sp>"
            + "<p:ext uri=\"{DEADBEEF}\">" + Shape(7, Paragraph("PRESENTATIONEXTLEAK")) + "</p:ext>";

        Assert.Equal("Slide 1\nShown\nVisible\n", await TextOfAsync(shape));
    }

    // ---------------------------------------------------------------- elements from a namespace the extractor does not read

    [Fact]
    public async Task Extract_ElementFromAnotherNamespace_IsSkippedWithItsSubtree()
    {
        string foreign =
            "<x:wrapper xmlns:x=\"urn:example:foreign\">" + Shape(5, Paragraph("FOREIGNSHAPELEAK")) + Table("FOREIGNTABLELEAK") + "</x:wrapper>";

        Assert.Equal("Slide 1\nVisible\n", await TextOfAsync(foreign));
    }

    // ---------------------------------------------------------------- text bodies and tables only where they are drawn

    public static TheoryData<string, string> TextBodiesWhereNothingDrawsOne() => new()
    {
        { "shape properties", "<p:sp><p:spPr>" + TextBody("SPPRLEAK") + "</p:spPr></p:sp>" },
        { "shape style", "<p:sp><p:style>" + TextBody("STYLELEAK") + "</p:style></p:sp>" },
        { "non-visual properties", "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"x\"/><p:cNvSpPr/><p:nvPr>" + TextBody("NVPRLEAK") + "</p:nvPr></p:nvSpPr></p:sp>" },
        { "shape name properties", "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"x\">" + TextBody("CNVPRLEAK") + "</p:cNvPr><p:cNvSpPr/><p:nvPr/></p:nvSpPr></p:sp>" },
        { "shape tree", TextBody("SPTREELEAK") },
        { "text body", "<p:sp><p:txBody><a:bodyPr/><a:lstStyle/>" + Paragraph("Shown") + TextBody("INTEXTBODYLEAK") + "</p:txBody></p:sp>" },
        { "paragraph", "<p:sp><p:txBody><a:bodyPr/><a:lstStyle/><a:p>" + TextBody("INPARAGRAPHLEAK") + "</a:p></p:txBody></p:sp>" },
        { "cell properties", "<p:graphicFrame><a:graphic><a:graphicData uri=\"" + TableUri + "\"><a:tbl><a:tr><a:tc><a:tcPr>" + TextBody("TCPRLEAK") + "</a:tcPr></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>" },
    };

    [Theory]
    [MemberData(nameof(TextBodiesWhereNothingDrawsOne))]
    public async Task Extract_TextBodyWhereNothingDrawsIt_IsNotProjected(string placement, string shapes)
    {
        string text = await TextOfAsync(shapes);

        Assert.False(text.Contains("LEAK", StringComparison.Ordinal), $"A text body in {placement} was projected: {text}");

        Assert.EndsWith("Visible\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("shape tree")]
    [InlineData("group")]
    public async Task Extract_TableOutsideTableGraphicData_IsNotProjected(string placement)
    {
        string shapes = placement switch
        {
            "shape" => "<p:sp><p:spPr/>" + Table("STRAYTABLELEAK") + "</p:sp>",
            "shape tree" => Table("STRAYTABLELEAK"),
            _ => "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"3\" name=\"g\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" + Table("STRAYTABLELEAK") + "</p:grpSp>",
        };

        Assert.Equal("Slide 1\nVisible\n", await TextOfAsync(shapes));
    }

    [Fact]
    public async Task Extract_TextBodyInsideAnAlternateContentChoiceOfAShape_IsProjectedOnce()
    {
        // The compatibility wrappers are transparent: the shape, not the wrapper, is the text body's parent.
        string wrapped =
            $"<mc:AlternateContent xmlns:mc=\"{PresentationDeck.Mc}\" xmlns:a14=\"http://schemas.microsoft.com/office/drawing/2010/main\">"
            + "<mc:Choice Requires=\"a14\">" + TextBody("Chosen text") + "</mc:Choice><mc:Fallback>" + TextBody("FALLBACKTEXTLEAK") + "</mc:Fallback></mc:AlternateContent>";

        string shape = "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"x\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/>" + wrapped + "</p:sp>";

        Assert.Equal("Slide 1\nChosen text\nVisible\n", await TextOfAsync(shape));
    }

    [Fact]
    public async Task Extract_ShapesTablesAndGroups_AreStillProjectedWhereTheyAreDrawn()
    {
        string group = "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"3\" name=\"g\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" + Shape(4, Paragraph("In group")) + "</p:grpSp>";

        string table = Frame($" uri=\"{TableUri}\"", Table("In table"));

        Assert.Equal("Slide 1\nIn group\nIn table\nVisible\n", await TextOfAsync(group + table));
    }
}
