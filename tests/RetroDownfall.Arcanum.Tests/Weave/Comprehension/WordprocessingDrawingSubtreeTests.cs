namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Text in a drawing or picture is read only where Word renders it: the <c>w:txbxContent</c> of a text box, reached through
/// <c>wps:txbx</c> or <c>v:textbox</c> of a shape the drawing renderer draws. Extension lists, graphic data of a kind Word
/// does not render as text, shape templates and every other subtree are never walked, even when they contain a paragraph,
/// and a footnote or endnote reference in them reaches no note.
/// </summary>
public sealed class WordprocessingDrawingSubtreeTests
{
    private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

    private const string GroupUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";

    private const string CanvasUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas";

    private static string Leak(string marker) => WordprocessingFixtures.P(marker);

    private static string Extension(string content) => $"<a:ext uri=\"{{00000000-0000-0000-0000-000000000001}}\">{content}</a:ext>";

    private static string ExtensionList(string marker) => "<a:extLst>" + Extension(Leak(marker)) + "</a:extLst>";

    private static string Box(string text) =>
        "<wps:txbx><w:txbxContent>" + WordprocessingFixtures.P(text) + "</w:txbxContent></wps:txbx>";

    private static string Shape(string beforeTextBox = "", string afterTextBox = "", string text = "visible box") =>
        "<wps:wsp><wps:cNvSpPr txBox=\"1\"/>" + beforeTextBox + Box(text) + afterTextBox + "<wps:bodyPr/></wps:wsp>";

    private static string Drawing(
        string graphicDataAttributes,
        string graphicDataContent,
        string inDrawing = "",
        string inContainer = "",
        string inGraphic = "",
        string container = "inline") =>
        "<w:r><w:drawing>" + inDrawing + $"<wp:{container}><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"Box\"/>" + inContainer
        + "<a:graphic>" + inGraphic + $"<a:graphicData{graphicDataAttributes}>" + graphicDataContent + $"</a:graphicData></a:graphic></wp:{container}></w:drawing></w:r>";

    private static string ShapeDrawing(string beforeTextBox = "", string afterTextBox = "", string inDrawing = "", string inContainer = "", string inGraphic = "", string inGraphicData = "") =>
        Drawing($" uri=\"{ShapeUri}\"", inGraphicData + Shape(beforeTextBox, afterTextBox), inDrawing, inContainer, inGraphic);

    private static string GroupDrawing(string inGroup = "", string inNestedGroup = "", string afterGroup = "") =>
        Drawing(
            $" uri=\"{GroupUri}\"",
            "<wpg:wgp><wpg:cNvGrpSpPr/><wpg:grpSpPr/>" + inGroup
            + "<wpg:grpSp><wpg:cNvPr id=\"3\" name=\"Inner\"/><wpg:cNvGrpSpPr/><wpg:grpSpPr/>" + inNestedGroup + Shape(text: "nested box") + "</wpg:grpSp>"
            + Shape() + afterGroup + "</wpg:wgp>");

    private static string CanvasDrawing(string inCanvas = "") =>
        Drawing($" uri=\"{CanvasUri}\"", "<wpc:wpc><wpc:bg/><wpc:whole/>" + inCanvas + Shape() + "</wpc:wpc>");

    private static string Paragraph(string runs) => "<w:p>" + runs + "</w:p>";

    private static string Extract(string body) => WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(body));

    private static string ExtractWithFootnote(string body, string noteText) =>
        WordprocessingFixtures.Extract(
            WordprocessingFixtures.Package(
                WordprocessingFixtures.Document(body),
                footnotesXml: WordprocessingFixtures.Footnotes("<w:footnote w:id=\"1\">" + WordprocessingFixtures.P(noteText) + "</w:footnote>")));

    // ---------------------------------------------------------------- extension lists

    [Fact]
    public void Extract_ExtensionListInAShapesProperties_IsNotWalked()
    {
        string drawing = ShapeDrawing(beforeTextBox: "<wps:spPr>" + ExtensionList("SPPREXTLSTLEAK") + "</wps:spPr>");

        Assert.Equal("visible box\n\n", Extract(Paragraph(drawing)));
    }

    [Fact]
    public void Extract_ExtensionListOfAShape_IsNotWalked()
    {
        string drawing = ShapeDrawing(afterTextBox: "<wps:extLst>" + Extension(Leak("WSPEXTLSTLEAK")) + "</wps:extLst>");

        Assert.Equal("visible box\n\n", Extract(Paragraph(drawing)));
    }

    [Fact]
    public void Extract_ExtensionListOfACanvas_IsNotWalked()
    {
        string drawing = CanvasDrawing("<wpc:extLst>" + Extension(Leak("WPCEXTLSTLEAK")) + "</wpc:extLst>");

        Assert.Equal("visible box\n\n", Extract(Paragraph(drawing)));
    }

    [Fact]
    public void Extract_ExtensionListOfADrawingGroup_IsNotWalked()
    {
        string drawing = GroupDrawing(afterGroup: "<wpg:extLst>" + Extension(Leak("WPGEXTLSTLEAK")) + "</wpg:extLst>");

        Assert.Equal("nested box\nvisible box\n\n", Extract(Paragraph(drawing)));
    }

    [Fact]
    public void Extract_ExtensionWhoseContentIsAWholeTextBox_IsNotWalked()
    {
        string extension = "<wps:spPr><a:extLst>" + Extension(Box("EXTENSIONTEXTBOXLEAK")) + "</a:extLst></wps:spPr>";

        Assert.Equal("visible box\n\n", Extract(Paragraph(ShapeDrawing(beforeTextBox: extension))));
    }

    [Fact]
    public void Extract_NoteReferenceInAnExtension_ReachesNoNote()
    {
        string extension = "<wps:spPr><a:extLst>" + Extension(Paragraph(WordprocessingFixtures.FootnoteReference(1))) + "</a:extLst></wps:spPr>";

        Assert.Equal("visible box\n\n", ExtractWithFootnote(Paragraph(ShapeDrawing(beforeTextBox: extension)), "EXTNOTELEAK"));
    }

    // ---------------------------------------------------------------- graphic data

    [Theory]
    [InlineData(" uri=\"urn:example:unknown-graphic\"")]
    [InlineData(" uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"")]
    [InlineData(" uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"")]
    [InlineData(" uri=\"\"")]
    [InlineData("")]
    public void Extract_GraphicDataOfAKindWordDoesNotRenderAsText_IsNotWalked(string graphicDataAttributes)
    {
        // A paragraph placed in the graphic data, and a text box shape that only a loader for the right kind could show.
        string drawing = Drawing(graphicDataAttributes, Leak("GRAPHICDATALEAK") + Shape(text: "UNKNOWNKINDBOX"));

        Assert.Equal("body\n", Extract(Paragraph(WordprocessingFixtures.R("body") + drawing)));
    }

    [Fact]
    public void Extract_NoteReferenceInGraphicDataOfAnUnknownKind_ReachesNoNote()
    {
        string drawing = Drawing(" uri=\"urn:example:unknown-graphic\"", Paragraph(WordprocessingFixtures.FootnoteReference(1)));

        Assert.Equal("body\n", ExtractWithFootnote(Paragraph(WordprocessingFixtures.R("body") + drawing), "GRAPHICDATANOTELEAK"));
    }

    [Fact]
    public void Extract_GraphicDataOfATextBearingKind_ProjectsItsShapesAndNotAParagraphBesideThem()
    {
        string drawing = Drawing($" uri=\"{ShapeUri}\"", Leak("BESIDETHESHAPE") + Shape());

        Assert.Equal("visible box\n\n", Extract(Paragraph(drawing)));
    }

    [Fact]
    public void Extract_GraphicDataOfEachTextBearingKind_ProjectsItsTextBoxes()
    {
        string text = Extract(Paragraph(ShapeDrawing()) + Paragraph(GroupDrawing()) + Paragraph(CanvasDrawing()));

        Assert.Equal("visible box\n\nnested box\nvisible box\n\nvisible box\n\n", text);
    }

    // ---------------------------------------------------------------- a paragraph in a drawing container that is not a text box

    public static TheoryData<string, string> DrawingContainersHoldingAStrayParagraph()
    {
        string stray = Leak("STRAYPARAGRAPHLEAK");

        return new TheoryData<string, string>
        {
            { "w:drawing", ShapeDrawing(inDrawing: stray) },
            { "wp:inline", ShapeDrawing(inContainer: stray) },
            { "wp:anchor", Drawing($" uri=\"{ShapeUri}\"", Shape(), inContainer: stray, container: "anchor") },
            { "a:graphic", ShapeDrawing(inGraphic: stray) },
            { "wps:wsp", ShapeDrawing(beforeTextBox: stray) },
            { "wps:bodyPr", ShapeDrawing(afterTextBox: "<wps:bodyPr>" + stray + "</wps:bodyPr>") },
            { "wps:spPr", ShapeDrawing(beforeTextBox: "<wps:spPr>" + stray + "</wps:spPr>") },
            { "wps:txbx", Drawing($" uri=\"{ShapeUri}\"", "<wps:wsp><wps:cNvSpPr/><wps:txbx>" + stray + "<w:txbxContent>" + WordprocessingFixtures.P("visible box") + "</w:txbxContent></wps:txbx></wps:wsp>") },
            { "a:extLst", ShapeDrawing(inContainer: ExtensionList("INSIDEEXTLSTLEAK")) },
            { "wpg:wgp", GroupDrawing(inGroup: stray) },
            { "wpg:grpSp", GroupDrawing(inNestedGroup: stray) },
            { "wpc:wpc", CanvasDrawing(stray) },
        };
    }

    public static TheoryData<string, string> ShapeContainersWhereWordDrawsNone()
    {
        string leak = Shape(text: "MISPLACEDSHAPELEAK");

        return new TheoryData<string, string>
        {
            { "shape in a shape", ShapeDrawing(beforeTextBox: leak) },
            { "group in a group", GroupDrawing(inGroup: "<wpg:wgp><wpg:cNvGrpSpPr/><wpg:grpSpPr/>" + leak + "</wpg:wgp>") },
            { "canvas in a group", GroupDrawing(inGroup: "<wpc:wpc>" + leak + "</wpc:wpc>") },
            { "canvas in a canvas", CanvasDrawing("<wpc:wpc>" + leak + "</wpc:wpc>") },
            { "nested group in graphic data", Drawing($" uri=\"{GroupUri}\"", "<wpg:grpSp><wpg:cNvGrpSpPr/><wpg:grpSpPr/>" + leak + "</wpg:grpSp>" + Shape()) },
            { "nested group in a canvas", CanvasDrawing("<wpg:grpSp><wpg:cNvGrpSpPr/><wpg:grpSpPr/>" + leak + "</wpg:grpSp>") },
        };
    }

    [Theory]
    [MemberData(nameof(ShapeContainersWhereWordDrawsNone))]
    public void Extract_ShapeContainerWhereWordDoesNotDrawIt_IsNotWalked(string placement, string drawing)
    {
        string text = Extract(Paragraph(drawing));

        Assert.False(text.Contains("MISPLACEDSHAPELEAK", StringComparison.Ordinal), $"A {placement} was projected: {text}");

        Assert.Contains("visible box\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DrawingContainersHoldingAStrayParagraph))]
    public void Extract_ParagraphInADrawingContainerThatIsNotATextBox_IsNotProjected(string container, string drawing)
    {
        string text = Extract(Paragraph(WordprocessingFixtures.R("body ") + drawing));

        Assert.False(text.Contains("LEAK", StringComparison.Ordinal), $"A paragraph in {container} was projected: {text}");

        Assert.Contains("visible box\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_DrawingElementsOutsideADrawing_AreNotWalked()
    {
        // The shape and the drawing object are reached only through w:drawing, and VML only through w:pict or w:object.
        string stray =
            "<wps:wsp><wps:cNvSpPr/>" + Box("STRAYSHAPELEAK") + "</wps:wsp>"
            + "<v:shape><v:textbox><w:txbxContent>" + WordprocessingFixtures.P("STRAYVMLLEAK") + "</w:txbxContent></v:textbox></v:shape>"
            + "<wp:inline><wp:extent cx=\"1\" cy=\"1\"/><a:graphic><a:graphicData uri=\"" + ShapeUri + "\">" + Shape(text: "STRAYINLINELEAK") + "</a:graphicData></a:graphic></wp:inline>"
            + "<a:graphic><a:graphicData uri=\"" + ShapeUri + "\">" + Shape(text: "STRAYGRAPHICLEAK") + "</a:graphicData></a:graphic>";

        Assert.Equal("body\n", Extract(Paragraph(WordprocessingFixtures.R("body") + stray + "<w:r>" + stray + "</w:r>")));
    }

    // ---------------------------------------------------------------- vector markup

    private static string Picture(string content) => "<w:r><w:pict>" + content + "</w:pict></w:r>";

    private static string VmlShape(string inShape = "", string text = "shape own text", string attributes = "") =>
        $"<v:shape{attributes}>{inShape}<v:textbox><w:txbxContent>" + WordprocessingFixtures.P(text) + "</w:txbxContent></v:textbox></v:shape>";

    [Fact]
    public void Extract_VmlShapeType_IsATemplateAndProjectsNoText()
    {
        string shapeType =
            "<v:shapetype id=\"_x0000_t202\" coordsize=\"21600,21600\" path=\"m,l,21600r21600,l21600,xe\"><v:textbox><w:txbxContent>"
            + WordprocessingFixtures.P("SHAPETYPELEAK") + "</w:txbxContent></v:textbox></v:shapetype>";

        Assert.Equal("body\n", Extract(Paragraph(WordprocessingFixtures.R("body") + Picture(shapeType))));
    }

    [Fact]
    public void Extract_VmlShapeTypeTemplate_LendsNoTextToTheShapeThatUsesIt()
    {
        string template = "<v:shapetype id=\"t1\"><v:textbox><w:txbxContent>" + WordprocessingFixtures.P("TEMPLATETEXT") + "</w:txbxContent></v:textbox></v:shapetype>";

        string text = Extract(Paragraph(WordprocessingFixtures.R("body") + Picture(template + VmlShape(attributes: " type=\"#t1\""))));

        Assert.Equal("body\nshape own text\n\n", text);
    }

    [Fact]
    public void Extract_NoteReferenceInAVmlShapeType_ReachesNoNote()
    {
        string template = "<v:shapetype id=\"t1\"><v:textbox><w:txbxContent>" + Paragraph(WordprocessingFixtures.FootnoteReference(1)) + "</w:txbxContent></v:textbox></v:shapetype>";

        Assert.Equal("body\n", ExtractWithFootnote(Paragraph(WordprocessingFixtures.R("body") + Picture(template)), "SHAPETYPENOTELEAK"));
    }

    [Theory]
    [InlineData("v:fill")]
    [InlineData("v:stroke")]
    [InlineData("v:path")]
    [InlineData("v:imagedata")]
    [InlineData("v:shadow")]
    public void Extract_TextBoxContentInAVmlElementThatIsNotATextBox_IsNotProjected(string element)
    {
        string inShape = $"<{element}><w:txbxContent>" + WordprocessingFixtures.P("NOTATEXTBOXLEAK") + $"</w:txbxContent></{element}>";

        Assert.Equal("body\nshape own text\n\n", Extract(Paragraph(WordprocessingFixtures.R("body") + Picture(VmlShape(inShape)))));
    }

    [Theory]
    [InlineData("pict")]
    [InlineData("shape")]
    [InlineData("textbox")]
    [InlineData("group")]
    [InlineData("object")]
    public void Extract_ParagraphInAVectorContainerThatIsNotATextBoxContent_IsNotProjected(string container)
    {
        string stray = Leak("STRAYVMLPARAGRAPHLEAK");

        string vml = container switch
        {
            "pict" => Picture(stray + VmlShape()),
            "shape" => Picture(VmlShape(stray)),
            "textbox" => Picture("<v:shape><v:textbox>" + stray + "<w:txbxContent>" + WordprocessingFixtures.P("shape own text") + "</w:txbxContent></v:textbox></v:shape>"),
            "group" => Picture("<v:group>" + stray + VmlShape() + "</v:group>"),
            _ => "<w:r><w:object>" + stray + VmlShape() + "</w:object></w:r>",
        };

        Assert.Equal("body\nshape own text\n\n", Extract(Paragraph(WordprocessingFixtures.R("body") + vml)));
    }

    [Theory]
    [InlineData("group")]
    [InlineData("pict")]
    public void Extract_VmlTextBoxWhereNoShapeHoldsIt_IsNotProjected(string parent)
    {
        // A text box belongs to a shape that is drawn; a group or a picture is only a container.
        string loose = "<v:textbox><w:txbxContent>" + WordprocessingFixtures.P("LOOSETEXTBOXLEAK") + "</w:txbxContent></v:textbox>";

        string vml = parent == "group" ? Picture("<v:group>" + loose + VmlShape() + "</v:group>") : Picture(loose + VmlShape());

        Assert.Equal("body\nshape own text\n\n", Extract(Paragraph(WordprocessingFixtures.R("body") + vml)));
    }

    private static string DrawingWithoutItsRun(string text) =>
        ShapeDrawing().Replace("visible box", text, StringComparison.Ordinal)["<w:r>".Length..^"</w:r>".Length];

    [Fact]
    public void Extract_DrawingInAnObject_IsProjected()
    {
        // An object may hold a drawing (as Word writes an embedded object's fallback).
        Assert.Equal("object drawing\n\n", Extract(Paragraph("<w:r><w:object>" + DrawingWithoutItsRun("object drawing") + "</w:object></w:r>")));
    }

    [Fact]
    public void Extract_DrawingInAPicture_IsNotProjected()
    {
        // A picture holds only vector shapes.
        Assert.Equal("body\n", Extract(Paragraph(WordprocessingFixtures.R("body") + "<w:r><w:pict>" + DrawingWithoutItsRun("DRAWINGINPICTURELEAK") + "</w:pict></w:r>")));
    }

    [Theory]
    [InlineData("display:none")]
    [InlineData("visibility:hidden")]
    public void Extract_HiddenVmlTextBox_IsExcludedWhileItsShapeIsDrawn(string style)
    {
        string shape = $"<v:shape><v:textbox style=\"{style}\"><w:txbxContent>" + WordprocessingFixtures.P("HIDDENTEXTBOXLEAK") + "</w:txbxContent></v:textbox></v:shape>";

        Assert.Equal("body\n", Extract(Paragraph(WordprocessingFixtures.R("body") + Picture(shape))));
    }

    [Fact]
    public void Extract_TextBoxesInVmlGroupsAndObjects_AreProjected()
    {
        string group = Picture("<v:group>" + VmlShape(text: "in group") + "<v:group>" + VmlShape(text: "in nested group") + "</v:group></v:group>");

        string shapes = Picture(VmlShape(text: "rect box").Replace("<v:shape", "<v:rect", StringComparison.Ordinal).Replace("</v:shape>", "</v:rect>", StringComparison.Ordinal));

        string objectBox = "<w:r><w:object>" + VmlShape(text: "in object") + "</w:object></w:r>";

        Assert.Equal("in group\nin nested group\n\nrect box\n\nin object\n\n", Extract(Paragraph(group) + Paragraph(shapes) + Paragraph(objectBox)));
    }
}
