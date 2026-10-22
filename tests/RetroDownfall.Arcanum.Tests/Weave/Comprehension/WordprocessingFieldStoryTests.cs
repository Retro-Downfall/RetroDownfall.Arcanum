namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Complex fields are scoped to the story that holds them. Word keeps the body, each note and each text box apart, and a
/// field's begin, separate and end must sit in one of them, so a field character in one story can neither end nor open a
/// field in another, and field code in one story never makes another story's text read as code or as result.
/// </summary>
public sealed class WordprocessingFieldStoryTests
{
    private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

    private static string FieldCharacter(string type) => $"<w:r><w:fldChar w:fldCharType=\"{type}\"/></w:r>";

    private static string Begin => FieldCharacter("begin");

    private static string Separate => FieldCharacter("separate");

    private static string End => FieldCharacter("end");

    private static string Instruction(string text) => $"<w:r><w:instrText xml:space=\"preserve\"> {text} </w:instrText></w:r>";

    private static string Paragraph(string runs) => $"<w:p>{runs}</w:p>";

    private static string VmlBox(string paragraphs) =>
        "<w:r><w:pict><v:shape style=\"position:absolute;width:100pt\"><v:textbox><w:txbxContent>" + paragraphs + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

    private static string DrawingBox(string paragraphs) =>
        "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"Box\"/>"
        + $"<a:graphic><a:graphicData uri=\"{ShapeUri}\"><wps:wsp><wps:cNvSpPr txBox=\"1\"/><wps:spPr/><wps:txbx><w:txbxContent>"
        + paragraphs + "</w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    private static string Box(string kind, string paragraphs) => kind == "vml" ? VmlBox(paragraphs) : DrawingBox(paragraphs);

    private static string Extract(string body) => WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(body));

    // ---------------------------------------------------------------- a text box cannot reach the field around it

    [Theory]
    [InlineData("vml", "end")]
    [InlineData("vml", "separate")]
    [InlineData("drawing", "end")]
    [InlineData("drawing", "separate")]
    public void Extract_TextBoxInFieldCode_CannotEndOrOpenTheResultOfTheBodysField(string box, string fieldCharacter)
    {
        // The box is anchored in the instruction of a body field. Its own end (or separate) is a field character of the box's
        // story and must not close the body's instruction; the text after the box is still instruction and stays out.
        string paragraph = Paragraph(
            WordprocessingFixtures.R("before ") + Begin + Instruction("QUOTE") + Box(box, Paragraph(FieldCharacter(fieldCharacter)))
            + WordprocessingFixtures.R("FIELDCODELEAK") + Separate + WordprocessingFixtures.R("result") + End + WordprocessingFixtures.R(" after"));

        Assert.Equal("before result after\n", Extract(paragraph));
    }

    [Theory]
    [InlineData("vml")]
    [InlineData("drawing")]
    public void Extract_TextBoxInFieldCode_ProjectsNothingOfItsOwnText(string box)
    {
        string paragraph = Paragraph(
            WordprocessingFixtures.R("before ") + Begin + Instruction("QUOTE") + Box(box, WordprocessingFixtures.P("BOXINFIELDCODE"))
            + Separate + WordprocessingFixtures.R("result") + End);

        Assert.Equal("before result\n", Extract(paragraph));
    }

    [Fact]
    public void Extract_NoteReferenceAfterATextBoxInFieldCode_ReachesNoNote()
    {
        // A box whose end would close the body's field must not let the note reference behind it count as shown.
        string paragraph = Paragraph(
            WordprocessingFixtures.R("before ") + Begin + Instruction("QUOTE") + VmlBox(Paragraph(End))
            + WordprocessingFixtures.FootnoteReference(1) + Separate + WordprocessingFixtures.R("result") + End);

        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(paragraph),
            footnotesXml: WordprocessingFixtures.Footnotes("<w:footnote w:id=\"1\">" + WordprocessingFixtures.P("FIELDCODENOTELEAK") + "</w:footnote>"));

        Assert.Equal("before result\n", WordprocessingFixtures.Extract(package));
    }

    [Fact]
    public void Extract_NoteReferenceInsideATextBoxInFieldCode_ReachesNoNote()
    {
        string paragraph = Paragraph(
            WordprocessingFixtures.R("before ") + Begin + Instruction("QUOTE") + VmlBox(Paragraph(WordprocessingFixtures.FootnoteReference(1)))
            + Separate + WordprocessingFixtures.R("result") + End);

        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(paragraph),
            footnotesXml: WordprocessingFixtures.Footnotes("<w:footnote w:id=\"1\">" + WordprocessingFixtures.P("BOXNOTELEAK") + "</w:footnote>"));

        Assert.Equal("before result\n", WordprocessingFixtures.Extract(package));
    }

    [Theory]
    [InlineData("vml")]
    [InlineData("drawing")]
    public void Extract_TextBoxInTheResultOfAField_IsProjected(string box)
    {
        // Only the instruction hides; a box anchored in the result is shown.
        string paragraph = Paragraph(
            Begin + Instruction("QUOTE") + Separate + WordprocessingFixtures.R("result ") + Box(box, WordprocessingFixtures.P("result box")) + End + WordprocessingFixtures.R("tail"));

        Assert.Equal("result \nresult box\ntail\n", Extract(paragraph));
    }

    // ---------------------------------------------------------------- a field begun in a text box ends with the box

    [Theory]
    [InlineData("vml")]
    [InlineData("drawing")]
    public void Extract_FieldBegunInATextBoxAndNeverEnded_DoesNotHideTheTextAfterTheBox(string box)
    {
        string paragraph = Paragraph(
            WordprocessingFixtures.R("before") + Box(box, Paragraph(WordprocessingFixtures.R("box ") + Begin + Instruction("PAGE"))) + WordprocessingFixtures.R("AFTERBOX"))
            + WordprocessingFixtures.P("NEXTPARA");

        Assert.Equal("before\nbox \nAFTERBOX\nNEXTPARA\n", Extract(paragraph));
    }

    [Fact]
    public void Extract_FieldBegunInANestedTextBox_DoesNotHideTheOuterBox()
    {
        string inner = VmlBox(Paragraph(WordprocessingFixtures.R("inner ") + Begin + Instruction("PAGE")));

        string outer = VmlBox(Paragraph(WordprocessingFixtures.R("outer ") + inner + WordprocessingFixtures.R("OUTERTAIL")));

        Assert.Equal("outer \ninner \nOUTERTAIL\n\n", Extract(Paragraph(outer)));
    }

    [Fact]
    public void Extract_FieldBegunInATextBox_DoesNotCloseABodyFieldWhoseResultFollows()
    {
        // The box's begin must not be ended by the body's end: the body field is open around the box, in its result.
        string paragraph = Paragraph(
            Begin + Instruction("QUOTE") + Separate + WordprocessingFixtures.R("result ") + VmlBox(Paragraph(Begin + Instruction("PAGE"))) + WordprocessingFixtures.R("still result") + End
            + WordprocessingFixtures.R(" after"));

        Assert.Equal("result still result after\n", Extract(paragraph));
    }

    [Theory]
    [InlineData("vml")]
    [InlineData("drawing")]
    public void Extract_CompleteFieldInsideATextBox_ProjectsItsResultOnly(string box)
    {
        string field = Begin + Instruction("PAGE") + Separate + WordprocessingFixtures.R("7") + End;

        string paragraph = Paragraph(WordprocessingFixtures.R("a") + Box(box, Paragraph(WordprocessingFixtures.R("box ") + field + WordprocessingFixtures.R(" end"))) + WordprocessingFixtures.R("tail"));

        Assert.Equal("a\nbox 7 end\ntail\n", Extract(paragraph));
    }

    // ---------------------------------------------------------------- each note is a story of its own

    private static byte[] Package(string body, string footnotes) =>
        WordprocessingFixtures.Package(WordprocessingFixtures.Document(body), footnotesXml: WordprocessingFixtures.Footnotes(footnotes));

    private static string Body(params int[] references) =>
        Paragraph(WordprocessingFixtures.R("body") + string.Concat(references.Select(WordprocessingFixtures.FootnoteReference)));

    [Fact]
    public void Extract_FieldLeftOpenInOneNote_DoesNotHideTheNoteAfterIt()
    {
        string footnotes =
            "<w:footnote w:id=\"1\">" + Paragraph(WordprocessingFixtures.R("one") + Begin + Instruction("PAGE")) + "</w:footnote>"
            + "<w:footnote w:id=\"2\">" + WordprocessingFixtures.P("two") + "</w:footnote>";

        Assert.Equal("body\nonetwo\n", WordprocessingFixtures.Extract(Package(Body(1, 2), footnotes)));
    }

    [Fact]
    public void Extract_FieldLeftOpenInANoteMetBeforeItsTurn_DoesNotHideTheNoteThatIsMetNext()
    {
        // Note 2 comes first in the part but is referenced second, so it is held; its open field must not reach note 1.
        string footnotes =
            "<w:footnote w:id=\"2\">" + Paragraph(WordprocessingFixtures.R("two") + Begin + Instruction("PAGE")) + "</w:footnote>"
            + "<w:footnote w:id=\"1\">" + WordprocessingFixtures.P("one") + "</w:footnote>";

        Assert.Equal("body\none\ntwo", WordprocessingFixtures.Extract(Package(Body(1, 2), footnotes)));
    }

    [Fact]
    public void Extract_TextBoxInANote_HasItsOwnFieldStory()
    {
        string note = Paragraph(
            WordprocessingFixtures.R("note ") + Begin + Instruction("QUOTE") + VmlBox(Paragraph(End))
            + WordprocessingFixtures.R("NOTELEAK") + Separate + WordprocessingFixtures.R("res") + End);

        Assert.Equal("body\nnote res\n", WordprocessingFixtures.Extract(Package(Body(1), "<w:footnote w:id=\"1\">" + note + "</w:footnote>")));
    }

    [Fact]
    public void Extract_FieldBegunInATextBoxInANote_DoesNotHideTheRestOfTheNote()
    {
        string note = Paragraph(WordprocessingFixtures.R("note ") + VmlBox(Paragraph(Begin + Instruction("PAGE"))) + WordprocessingFixtures.R("NOTETAIL"));

        Assert.Equal("body\nnote NOTETAIL\n", WordprocessingFixtures.Extract(Package(Body(1), "<w:footnote w:id=\"1\">" + note + "</w:footnote>")));
    }
}
