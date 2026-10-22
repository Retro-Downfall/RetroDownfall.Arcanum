using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Drawings, text boxes and the document's non-body children: the anchor's own hidden flag, the document background,
/// the line breaks around a text box, and the way a drawing's text is held until the drawing ends.
/// </summary>
public sealed class WordprocessingDrawingTests
{
    private static string Extract(string body) => WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(body));

    private static string TextBoxGraphic(string boxText) =>
        "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvSpPr txBox=\"1\"/><wps:spPr/>"
        + $"<wps:txbx><w:txbxContent><w:p><w:r><w:t>{boxText}</w:t></w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic>";

    private static string Anchor(string anchorAttributes, string docPrAttributes, string boxText) =>
        "<w:r><w:drawing><wp:anchor" + anchorAttributes + " distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"1\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">"
        + "<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>"
        + $"<wp:extent cx=\"1\" cy=\"1\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/><wp:docPr id=\"1\" name=\"Text Box 1\"{docPrAttributes}/><wp:cNvGraphicFramePr/>"
        + TextBoxGraphic(boxText) + "</wp:anchor></w:drawing></w:r>";

    private static string Inline(string inlineAttributes, string docPrAttributes, string boxText) =>
        "<w:r><w:drawing><wp:inline" + inlineAttributes + "><wp:extent cx=\"1\" cy=\"1\"/>"
        + $"<wp:docPr id=\"1\" name=\"Box\"{docPrAttributes}/>" + TextBoxGraphic(boxText) + "</wp:inline></w:drawing></w:r>";

    private static string VmlBox(string boxText, string style = "position:absolute;width:100pt") =>
        $"<w:r><w:pict><v:shape style=\"{style}\"><v:textbox><w:txbxContent>" + WordprocessingFixtures.P(boxText) + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

    // ---------------------------------------------------------------- the anchor's own hidden flag

    [Theory]
    [InlineData(" hidden=\"1\"")]
    [InlineData(" hidden=\"true\"")]
    public void Extract_AnchorWhoseHiddenAttributeIsOn_HidesTheFloatingTextBox(string anchorAttributes)
    {
        // Sensitivity: reading hidden from the anchor itself; wp:docPr here says nothing.
        string body = "<w:p>" + WordprocessingFixtures.R("body") + Anchor(anchorAttributes, string.Empty, "ANCHOR_HIDDEN_LEAK") + "</w:p>";

        Assert.Equal("body\n", Extract(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" hidden=\"0\"")]
    [InlineData(" hidden=\"false\"")]
    public void Extract_AnchorWhoseHiddenAttributeIsOffOrAbsent_ProjectsTheFloatingTextBox(string anchorAttributes)
    {
        Assert.Equal("shown box\n\n", Extract("<w:p>" + Anchor(anchorAttributes, string.Empty, "shown box") + "</w:p>"));
    }

    [Fact]
    public void Extract_EitherTheAnchorOrItsDocPrHiding_HidesTheDrawing()
    {
        string anchorOnly = Anchor(" hidden=\"1\"", " hidden=\"0\"", "HIDDEN_BY_ANCHOR");

        string docPrOnly = Anchor(" hidden=\"0\"", " hidden=\"1\"", "HIDDEN_BY_DOCPR");

        Assert.Equal("body\n", Extract("<w:p>" + anchorOnly + docPrOnly + WordprocessingFixtures.R("body") + "</w:p>"));
    }

    [Fact]
    public void Extract_InlineDrawing_HasNoHiddenAttributeOfItsOwn()
    {
        // CT_Inline carries no hidden attribute; only the anchor and docPr do.
        Assert.Equal("inline box\n\n", Extract("<w:p>" + Inline(" hidden=\"1\"", string.Empty, "inline box") + "</w:p>"));
    }

    [Fact]
    public void Extract_HiddenAnchorInTheStrictDrawingNamespace_IsExcluded()
    {
        string anchor =
            "<w:r><w:drawing><sp:anchor xmlns:sp=\"http://purl.oclc.org/ooxml/drawingml/wordprocessingDrawing\" hidden=\"1\"><sp:extent cx=\"1\" cy=\"1\"/><sp:docPr id=\"1\" name=\"Box\"/>"
            + TextBoxGraphic("STRICT_ANCHOR_HIDDEN") + "</sp:anchor></w:drawing></w:r>";

        Assert.Equal("body\n", Extract("<w:p>" + anchor + WordprocessingFixtures.R("body") + "</w:p>"));
    }

    // ---------------------------------------------------------------- the document's non-body children

    private static string RawDocument(string inner) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {WordprocessingFixtures.Namespaces()}>{inner}</w:document>";

    [Fact]
    public void Extract_DocumentBackgroundDrawingText_IsNotProjected()
    {
        // Sensitivity: walking only w:body. A background is a page fill, never flowing text, and the text box in it is
        // not one a reader sees.
        string background =
            "<w:background w:color=\"FFFFFF\"><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"bg\"/>"
            + TextBoxGraphic("BACKGROUND_INJECTION") + "</wp:inline></w:drawing></w:background>";

        string document = RawDocument(background + "<w:body>" + WordprocessingFixtures.P("body") + "</w:body>");

        Assert.Equal("body\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Package(document)));
    }

    [Fact]
    public void Extract_DocumentBackgroundWithVectorMarkupAndParagraphs_IsNotProjected()
    {
        string background =
            "<w:background w:color=\"FFFFFF\"><v:background id=\"_x0000_s1025\"><v:fill type=\"frame\"/></v:background>"
            + "<w:pict><v:shape><v:textbox><w:txbxContent>" + WordprocessingFixtures.P("BACKGROUND_VML_INJECTION") + "</w:txbxContent></v:textbox></v:shape></w:pict>"
            + WordprocessingFixtures.P("BACKGROUND_PARAGRAPH") + "</w:background>";

        string document = RawDocument(background + "<w:body>" + WordprocessingFixtures.P("body") + "</w:body>");

        Assert.Equal("body\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Package(document)));
    }

    [Fact]
    public void Extract_ContentBesideTheBody_IsNotProjected()
    {
        string document = RawDocument(WordprocessingFixtures.P("BEFORE_BODY") + "<w:body>" + WordprocessingFixtures.P("body") + "</w:body>" + WordprocessingFixtures.P("AFTER_BODY"));

        Assert.Equal("body\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Package(document)));
    }

    [Fact]
    public void Extract_BodyInsideAnAlternateContentChoice_IsStillTheBody()
    {
        string document = RawDocument("<mc:AlternateContent><mc:Choice Requires=\"w14\"><w:body>" + WordprocessingFixtures.P("chosen body") + "</w:body></mc:Choice></mc:AlternateContent>");

        Assert.Equal("chosen body\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Package(document)));
    }

    // ---------------------------------------------------------------- a text box starts and ends on its own line

    [Fact]
    public void Extract_VmlTextBoxInTheMiddleOfASentence_IsSeparatedFromTheTextOnBothSides()
    {
        // Sensitivity: the break before the box ("showsQuarterly") and the break after it.
        string paragraph = "<w:p>" + WordprocessingFixtures.R("The figure shows") + VmlBox("Quarterly Sales") + WordprocessingFixtures.R("growth.") + "</w:p>";

        Assert.Equal("The figure shows\nQuarterly Sales\ngrowth.\n", Extract(paragraph));
    }

    [Fact]
    public void Extract_DrawingMlTextBoxInTheMiddleOfASentence_IsSeparatedFromTheTextOnBothSides()
    {
        string paragraph = "<w:p>" + WordprocessingFixtures.R("Anchor paragraph.") + Anchor(string.Empty, string.Empty, "Text box body") + WordprocessingFixtures.R("More.") + "</w:p>";

        Assert.Equal("Anchor paragraph.\nText box body\nMore.\n", Extract(paragraph));
    }

    [Fact]
    public void Extract_TextBoxAtTheStartOfAParagraph_AddsNoLeadingBreak()
    {
        Assert.Equal("one\n\ntwo\n", Extract("<w:p>" + VmlBox("one") + "</w:p>" + WordprocessingFixtures.P("two")));
    }

    [Fact]
    public void Extract_AdjacentTextBoxes_AreEachOnTheirOwnLine()
    {
        Assert.Equal("first\nsecond\n\n", Extract("<w:p>" + VmlBox("first") + VmlBox("second") + "</w:p>"));
    }

    [Fact]
    public void Extract_TextBoxThatProjectsNothing_AddsNoBreak()
    {
        string empty = "<w:r><w:pict><v:shape><v:textbox><w:txbxContent/></v:textbox></v:shape></w:pict></w:r>";

        string hiddenInside = "<w:r><w:pict><v:shape><v:textbox><w:txbxContent><w:p><w:r><w:rPr><w:vanish/></w:rPr><w:t>HIDDEN</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

        Assert.Equal("before after\n", Extract("<w:p>" + WordprocessingFixtures.R("before ") + empty + WordprocessingFixtures.R("after") + "</w:p>"));

        Assert.Equal("before \nafter\n", Extract("<w:p>" + WordprocessingFixtures.R("before ") + hiddenInside + WordprocessingFixtures.R("after") + "</w:p>"));
    }

    [Fact]
    public void Extract_HiddenTextBoxInTheMiddleOfASentence_AddsNoBreak()
    {
        string paragraph = "<w:p>" + WordprocessingFixtures.R("before ") + VmlBox("HIDDEN", "visibility:hidden") + WordprocessingFixtures.R("after") + "</w:p>";

        Assert.Equal("before after\n", Extract(paragraph));
    }

    [Fact]
    public void Extract_TextBoxInsideATextBox_EachStartsOnItsOwnLine()
    {
        string inner = VmlBox("inner");

        string outer = "<w:r><w:pict><v:shape><v:textbox><w:txbxContent><w:p>" + WordprocessingFixtures.R("outer text") + inner + WordprocessingFixtures.R("outer tail") + "</w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

        Assert.Equal("lead\nouter text\ninner\nouter tail\n\n", Extract("<w:p>" + WordprocessingFixtures.R("lead") + outer + "</w:p>"));
    }

    // ---------------------------------------------------------------- a drawing's text is held until the drawing ends

    [Fact]
    public void Extract_HiddenFlagAfterTheGraphic_DiscardsTheBreakAndTheFieldStateTheDrawingTouched()
    {
        // A field that begins inside a drawing that turns out hidden must not leave later text in field code.
        string box = "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:t>HIDDEN</w:t></w:r></w:p>";

        string drawing =
            "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><a:graphic><a:graphicData><wps:wsp><wps:txbx><w:txbxContent>" + box
            + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic><wp:docPr id=\"1\" name=\"Box\" hidden=\"1\"/></wp:inline></w:drawing></w:r>";

        Assert.Equal("before after\n", Extract("<w:p>" + WordprocessingFixtures.R("before ") + drawing + WordprocessingFixtures.R("after") + "</w:p>"));
    }

    [Fact]
    public void Extract_DroppedDrawing_LeavesTheLineStateAsItFoundIt()
    {
        // The dropped drawing wrote a line of its own; the text box after it must still start on a new line.
        string dropped =
            "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><a:graphic><a:graphicData><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("DROPPED") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic><wp:docPr id=\"1\" name=\"Box\" hidden=\"1\"/></wp:inline></w:drawing></w:r>";

        string paragraph = "<w:p>" + WordprocessingFixtures.R("before ") + dropped + VmlBox("box") + "</w:p>";

        Assert.Equal("before \nbox\n\n", Extract(paragraph));
    }

    [Fact]
    public void Extract_DrawingNestedInAHiddenDrawing_IsDroppedWithIt_AndOneInAVisibleDrawingIsDecidedAlone()
    {
        string hiddenInner = Inline(string.Empty, " hidden=\"1\"", "HIDDEN_INNER");

        string visibleOuter =
            "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"9\" name=\"Outer\"/><a:graphic><a:graphicData><wps:wsp><wps:txbx><w:txbxContent><w:p>"
            + WordprocessingFixtures.R("outer ") + hiddenInner + WordprocessingFixtures.R("text") + "</w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

        Assert.Equal("outer text\n\n", Extract("<w:p>" + visibleOuter + "</w:p>"));
    }

    [Fact]
    public void Extract_DrawingTextBeyondTheHoldingBound_TruncatesAndProjectsNothingOfIt()
    {
        // Visibility is not known until the drawing ends, and what is held is bounded; text that cannot be held is
        // withheld and reported as truncation, never emitted.
        string huge = new('x', WordprocessingOutput.MaximumBufferedCharacters + 1);

        byte[] package = WordprocessingFixtures.Docx(
            WordprocessingFixtures.P("before") + "<w:p>" + Inline(string.Empty, string.Empty, huge) + "</w:p>" + WordprocessingFixtures.P("after"));

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(package, maximumCharacters: 1_000_000);

        Assert.Equal("before\n", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_DrawingTextUpToTheHoldingBound_IsProjected()
    {
        string large = new('x', WordprocessingOutput.MaximumBufferedCharacters - 1);

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(
            WordprocessingFixtures.Docx("<w:p>" + Inline(string.Empty, string.Empty, large) + "</w:p>"), maximumCharacters: 1_000_000);

        Assert.Equal(large + "\n\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void Extract_SinkRefusingTheHeldTextOfADrawing_StopsTheWalk()
    {
        // The ceiling is reached when the drawing's held text is flushed; nothing after it is read.
        string drawing = "<w:p>" + Inline(string.Empty, string.Empty, "aaaaaaaa") + "</w:p>";

        string main = $"<w:document {WordprocessingFixtures.Namespaces()}><w:body>{drawing}<<";

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(WordprocessingFixtures.Package(main), maximumCharacters: 4);

        Assert.Equal("aaaa", result.Text);

        Assert.True(result.WasTruncated);
    }
}
