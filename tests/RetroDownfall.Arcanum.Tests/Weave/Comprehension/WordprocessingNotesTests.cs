using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>Footnotes and endnotes: projected after the body, separators skipped, parts reached only by relationship.</summary>
public sealed class WordprocessingNotesTests
{
    private const string Vanish = "<w:rPr><w:vanish/></w:rPr>";

    private static string Footnote(int id, string text, string? type = null) =>
        $"<w:footnote w:id=\"{id}\"{(type is null ? string.Empty : $" w:type=\"{type}\"")}>{WordprocessingFixtures.P(text)}</w:footnote>";

    private static string Endnote(int id, string text, string? type = null) =>
        $"<w:endnote w:id=\"{id}\"{(type is null ? string.Empty : $" w:type=\"{type}\"")}>{WordprocessingFixtures.P(text)}</w:endnote>";

    private static byte[] Package(string body, string? footnotes = null, string? endnotes = null, string? styles = null) =>
        WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(body),
            stylesXml: styles is null ? null : WordprocessingFixtures.Styles(styles),
            footnotesXml: footnotes is null ? null : WordprocessingFixtures.Footnotes(footnotes),
            endnotesXml: endnotes is null ? null : WordprocessingFixtures.Endnotes(endnotes));

    // ---------------------------------------------------------------- projection

    [Fact]
    public void Extract_FootnotesThenEndnotes_FollowTheBodyInPartOrder()
    {
        string text = WordprocessingFixtures.Extract(Package(
            WordprocessingFixtures.P("body one") + WordprocessingFixtures.P("body two"),
            footnotes: Footnote(1, "foot one") + Footnote(2, "foot two"),
            endnotes: Endnote(1, "end one") + Endnote(2, "end two")));

        Assert.Equal("body one\nbody two\nfoot one\nfoot two\nend one\nend two\n", text);
    }

    [Fact]
    public void Extract_NotesAreProjectedInPartOrderNotReferenceOrder()
    {
        string body = "<w:p>" + WordprocessingFixtures.R("a") + "<w:r><w:footnoteReference w:id=\"2\"/></w:r>" + WordprocessingFixtures.R("b") + "<w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p>";

        string text = WordprocessingFixtures.Extract(Package(body, footnotes: Footnote(1, "first in part") + Footnote(2, "second in part")));

        Assert.Equal("ab\nfirst in part\nsecond in part\n", text);
    }

    [Fact]
    public void Extract_FootnotesOnly_AndEndnotesOnly_AreProjected()
    {
        Assert.Equal("body\nfoot\n", WordprocessingFixtures.Extract(Package(WordprocessingFixtures.P("body"), footnotes: Footnote(1, "foot"))));

        Assert.Equal("body\nend\n", WordprocessingFixtures.Extract(Package(WordprocessingFixtures.P("body"), endnotes: Endnote(1, "end"))));
    }

    [Fact]
    public void Extract_NoteReferenceMarksAndNoteNumberRuns_EmitNothing()
    {
        string note = "<w:footnote w:id=\"1\"><w:p><w:r><w:rPr><w:rStyle w:val=\"FootnoteReference\"/></w:rPr><w:footnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> </w:t></w:r><w:r><w:t>the note</w:t></w:r></w:p></w:footnote>";

        string body = "<w:p>" + WordprocessingFixtures.R("text") + "<w:r><w:rPr><w:rStyle w:val=\"FootnoteReference\"/></w:rPr><w:footnoteReference w:id=\"1\"/></w:r></w:p>";

        Assert.Equal("text\n the note\n", WordprocessingFixtures.Extract(Package(body, footnotes: note)));
    }

    [Theory]
    [InlineData("separator")]
    [InlineData("continuationSeparator")]
    [InlineData("continuationNotice")]
    public void Extract_SeparatorNotes_AreSkipped(string type)
    {
        // Sensitivity: skipping the three separator note types. Each carries text a real note would never hold.
        string text = WordprocessingFixtures.Extract(Package(
            WordprocessingFixtures.P("body"),
            footnotes: Footnote(-1, "SEPARATOR_FOOTNOTE", type) + Footnote(1, "real footnote"),
            endnotes: Endnote(-1, "SEPARATOR_ENDNOTE", type) + Endnote(1, "real endnote")));

        Assert.Equal("body\nreal footnote\nreal endnote\n", text);
    }

    [Fact]
    public void Extract_NormalNotesAndNotesOfAnUnknownType_AreProjected()
    {
        string text = WordprocessingFixtures.Extract(Package(
            WordprocessingFixtures.P("body"),
            footnotes: Footnote(1, "normal", "normal") + Footnote(2, "other", "something-else")));

        Assert.Equal("body\nnormal\nother\n", text);
    }

    [Fact]
    public void Extract_ContentThatIsNotANote_InsideANotesPart_IsSkipped()
    {
        string footnotes = WordprocessingFixtures.P("STRAY_PARAGRAPH") + Footnote(1, "note") + Endnote(1, "WRONG_KIND_OF_NOTE");

        string endnotes = Footnote(1, "WRONG_KIND_OF_NOTE_IN_ENDNOTES") + Endnote(1, "end");

        Assert.Equal("body\nnote\nend\n", WordprocessingFixtures.Extract(Package(WordprocessingFixtures.P("body"), footnotes, endnotes)));
    }

    [Fact]
    public void Extract_NoteWithTablesHyperlinksAndInsertions_IsProjected()
    {
        string note =
            "<w:footnote w:id=\"1\"><w:tbl><w:tr><w:tc>" + WordprocessingFixtures.P("cell") + "</w:tc></w:tr></w:tbl>"
            + "<w:p><w:hyperlink w:anchor=\"a\">" + WordprocessingFixtures.R("link") + "</w:hyperlink><w:ins w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\">" + WordprocessingFixtures.R(" added") + "</w:ins>"
            + "<w:del w:id=\"2\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\">" + WordprocessingFixtures.R(" REMOVED") + "</w:del></w:p></w:footnote>";

        Assert.Equal("body\ncell\nlink added\n", WordprocessingFixtures.Extract(Package(WordprocessingFixtures.P("body"), note)));
    }

    // ---------------------------------------------------------------- hidden content in notes

    [Fact]
    public void Extract_NotesHonourTheSameStylesAndDirectFormatting()
    {
        string styles =
            "<w:style w:type=\"paragraph\" w:styleId=\"HiddenNote\">" + Vanish + "</w:style>"
            + "<w:style w:type=\"character\" w:styleId=\"HiddenRun\">" + Vanish + "</w:style>";

        string footnotes =
            "<w:footnote w:id=\"1\"><w:p><w:pPr><w:pStyle w:val=\"HiddenNote\"/></w:pPr>" + WordprocessingFixtures.R("HIDDEN_BY_PARAGRAPH_STYLE") + "</w:p>"
            + "<w:p>" + WordprocessingFixtures.R("HIDDEN_DIRECT", "<w:vanish/>") + WordprocessingFixtures.R("HIDDEN_BY_CHARACTER_STYLE", "<w:rStyle w:val=\"HiddenRun\"/>") + WordprocessingFixtures.R("visible") + "</w:p></w:footnote>";

        string text = WordprocessingFixtures.Extract(Package(WordprocessingFixtures.P("body"), footnotes, styles: styles));

        Assert.Equal("body\n\nvisible\n", text);
    }

    [Fact]
    public void Extract_FieldCodeStateDoesNotCarryFromTheBodyIntoTheNotes()
    {
        // The body ends inside an unterminated field's code; the notes part starts afresh.
        string body = "<w:p>" + WordprocessingFixtures.R("body") + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r></w:p>";

        string text = WordprocessingFixtures.Extract(Package(body, footnotes: Footnote(1, "note"), endnotes: Endnote(1, "end")));

        Assert.Equal("bodynote\nend\n", text);
    }

    [Fact]
    public void Extract_FieldCodeStateDoesNotCarryFromFootnotesIntoEndnotes()
    {
        string footnotes = "<w:footnote w:id=\"1\"><w:p>" + WordprocessingFixtures.R("note") + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r></w:p></w:footnote>";

        string text = WordprocessingFixtures.Extract(Package(WordprocessingFixtures.P("body"), footnotes, endnotes: Endnote(1, "end")));

        Assert.Equal("body\nnoteend\n", text);
    }

    [Fact]
    public void Extract_AlternateContentInANote_ProjectsTheFirstChoiceOnly()
    {
        string note =
            "<w:footnote w:id=\"1\"><w:p><mc:AlternateContent><mc:Choice Requires=\"wps\">" + WordprocessingFixtures.R("choice") + "</mc:Choice><mc:Fallback>" + WordprocessingFixtures.R("FALLBACK") + "</mc:Fallback></mc:AlternateContent></w:p></w:footnote>";

        Assert.Equal("body\nchoice\n", WordprocessingFixtures.Extract(Package(WordprocessingFixtures.P("body"), note)));
    }

    // ---------------------------------------------------------------- the notes parts

    [Fact]
    public void Extract_NotesRelationshipWhosePartIsAbsent_IsMissingPart()
    {
        byte[] footnotes = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("x")),
            documentRelationships: [WordprocessingFixtures.Relationship("rIdN", WordprocessingFixtures.FootnotesType, "footnotes.xml")]);

        byte[] endnotes = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("x")),
            documentRelationships: [WordprocessingFixtures.Relationship("rIdN", WordprocessingFixtures.EndnotesType, "endnotes.xml")]);

        Assert.Equal(DocumentPackageRejection.MissingPart, WordprocessingFixtures.Rejection(footnotes));

        Assert.Equal(DocumentPackageRejection.MissingPart, WordprocessingFixtures.Rejection(endnotes));
    }

    [Fact]
    public void Extract_NotesPartWithAnotherRootElement_IsFormatMismatch()
    {
        // The endnotes relationship points at a real footnotes part, and the footnotes relationship at a document.
        byte[] endnotesAsFootnotes = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("x")),
            endnotesXml: WordprocessingFixtures.Footnotes(Footnote(1, "wrong root")));

        byte[] footnotesAsDocument = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("x")),
            footnotesXml: WordprocessingFixtures.Document(WordprocessingFixtures.P("wrong root")));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(endnotesAsFootnotes));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(footnotesAsDocument));
    }

    [Fact]
    public void Extract_MalformedNotesPart_IsMalformedXmlAndDiscardsTheBody()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("body")),
            footnotesXml: "<w:footnotes><w:footnote");

        Assert.Equal(DocumentPackageRejection.MalformedXml, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_NotesPartsAreFoundByRelationshipNotByName()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/document.xml"))),
            ("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("body"))),
            ("word/_rels/document.xml.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rIdN", WordprocessingFixtures.FootnotesType, "notes/feet.xml"))),
            ("word/notes/feet.xml", WordprocessingFixtures.Footnotes(Footnote(1, "from the related part"))),
            ("word/footnotes.xml", WordprocessingFixtures.Footnotes(Footnote(1, "DECOY_BY_NAME"))));

        Assert.Equal("body\nfrom the related part\n", WordprocessingFixtures.Extract(package));
    }

    [Fact]
    public void Extract_NotesPartSmuggledWithoutARelationship_IsNeverRead()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("body")),
            extraEntries:
            [
                ("word/footnotes.xml", WordprocessingFixtures.Footnotes(Footnote(1, "SMUGGLED_FOOTNOTE"))),
                ("word/endnotes.xml", "<<< malformed and unreferenced"),
            ]);

        Assert.Equal("body\n", WordprocessingFixtures.Extract(package));
    }

    // ---------------------------------------------------------------- output ceiling

    [Fact]
    public void Extract_OutputCeilingReachedInANote_TruncatesThere()
    {
        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(
            Package(WordprocessingFixtures.P("body"), footnotes: Footnote(1, "footnote text")), maximumCharacters: 9);

        Assert.Equal("body\nfoot", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_OutputCeilingReachedInTheFootnotes_NeverReadsTheEndnotes()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("body")),
            footnotesXml: WordprocessingFixtures.Footnotes(Footnote(1, "a long footnote")),
            endnotesXml: "<<< malformed");

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(package, maximumCharacters: 8);

        Assert.Equal("body\na l", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_OutputCeilingReachedInTheBody_NeverReadsTheNotes()
    {
        // The endnotes part is malformed; reading it after the body filled the sink would reject the document.
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("a long body paragraph")),
            footnotesXml: WordprocessingFixtures.Footnotes(Footnote(1, "note")),
            endnotesXml: "<<< malformed");

        Assert.Equal("a long", WordprocessingFixtures.ExtractResult(package, maximumCharacters: 6).Text);
    }
}
