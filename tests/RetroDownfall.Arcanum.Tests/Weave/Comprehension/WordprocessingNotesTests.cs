using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Footnotes and endnotes: a note is projected only when a visible reference in the body reaches it, in first-reference
/// order (footnotes, then endnotes) after the body; separators are skipped and parts are reached only by relationship.
/// </summary>
public sealed class WordprocessingNotesTests
{
    private const string Vanish = "<w:rPr><w:vanish/></w:rPr>";

    private const string Revision = "w:id=\"9\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"";

    private static string Footnote(int id, string text, string? type = null) =>
        $"<w:footnote w:id=\"{id}\"{(type is null ? string.Empty : $" w:type=\"{type}\"")}>{WordprocessingFixtures.P(text)}</w:footnote>";

    private static string Endnote(int id, string text, string? type = null) =>
        $"<w:endnote w:id=\"{id}\"{(type is null ? string.Empty : $" w:type=\"{type}\"")}>{WordprocessingFixtures.P(text)}</w:endnote>";

    private static string FootRef(int id) => WordprocessingFixtures.FootnoteReference(id);

    private static string EndRef(int id) => WordprocessingFixtures.EndnoteReference(id);

    /// <summary>A body paragraph of <paramref name="text"/> followed by the given reference runs.</summary>
    private static string Body(string text, params string[] references) =>
        "<w:p>" + WordprocessingFixtures.R(text) + string.Concat(references) + "</w:p>";

    private static byte[] Package(string body, string? footnotes = null, string? endnotes = null, string? styles = null) =>
        WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(body),
            stylesXml: styles is null ? null : WordprocessingFixtures.Styles(styles),
            footnotesXml: footnotes is null ? null : WordprocessingFixtures.Footnotes(footnotes),
            endnotesXml: endnotes is null ? null : WordprocessingFixtures.Endnotes(endnotes));

    // ---------------------------------------------------------------- projection

    [Fact]
    public void Extract_FootnotesThenEndnotes_FollowTheBody()
    {
        string text = WordprocessingFixtures.Extract(Package(
            Body("body one", FootRef(1), EndRef(1)) + Body("body two", FootRef(2), EndRef(2)),
            footnotes: Footnote(1, "foot one") + Footnote(2, "foot two"),
            endnotes: Endnote(1, "end one") + Endnote(2, "end two")));

        Assert.Equal("body one\nbody two\nfoot one\nfoot two\nend one\nend two\n", text);
    }

    [Fact]
    public void Extract_NotesAreProjectedInFirstReferenceOrderNotPartOrder()
    {
        // Sensitivity: projecting in the order of the notes part. The second note in the part is referenced first.
        string body = "<w:p>" + WordprocessingFixtures.R("a") + FootRef(2) + WordprocessingFixtures.R("b") + FootRef(1) + "</w:p>";

        string text = WordprocessingFixtures.Extract(Package(body, footnotes: Footnote(1, "first in part") + Footnote(2, "second in part")));

        Assert.Equal("ab\nsecond in part\nfirst in part\n", text);
    }

    [Fact]
    public void Extract_EndnotesAreProjectedInTheirOwnFirstReferenceOrder_AfterAllFootnotes()
    {
        string body = Body("x", EndRef(2), FootRef(3), EndRef(1), FootRef(1));

        string text = WordprocessingFixtures.Extract(Package(
            body,
            footnotes: Footnote(1, "foot 1") + Footnote(3, "foot 3"),
            endnotes: Endnote(1, "end 1") + Endnote(2, "end 2")));

        Assert.Equal("x\nfoot 3\nfoot 1\nend 2\nend 1\n", text);
    }

    [Fact]
    public void Extract_ANoteReferencedSeveralTimes_IsProjectedOnceAtItsFirstReference()
    {
        string text = WordprocessingFixtures.Extract(Package(
            Body("x", FootRef(2), FootRef(1), FootRef(2)),
            footnotes: Footnote(1, "one") + Footnote(2, "two")));

        Assert.Equal("x\ntwo\none\n", text);
    }

    [Fact]
    public void Extract_FootnoteAndEndnoteIdsAreSeparateSpaces()
    {
        // A footnote reference reaches the footnotes part only, an endnote reference the endnotes part only.
        string text = WordprocessingFixtures.Extract(Package(
            Body("x", FootRef(1)),
            footnotes: Footnote(1, "the footnote"),
            endnotes: Endnote(1, "NOT_REFERENCED_ENDNOTE")));

        Assert.Equal("x\nthe footnote\n", text);
    }

    [Fact]
    public void Extract_FootnotesOnly_AndEndnotesOnly_AreProjected()
    {
        Assert.Equal("body\nfoot\n", WordprocessingFixtures.Extract(Package(Body("body", FootRef(1)), footnotes: Footnote(1, "foot"))));

        Assert.Equal("body\nend\n", WordprocessingFixtures.Extract(Package(Body("body", EndRef(1)), endnotes: Endnote(1, "end"))));
    }

    [Fact]
    public void Extract_NoteReferenceMarksAndNoteNumberRuns_EmitNothing()
    {
        string note = "<w:footnote w:id=\"1\"><w:p><w:r><w:rPr><w:rStyle w:val=\"FootnoteReference\"/></w:rPr><w:footnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> </w:t></w:r><w:r><w:t>the note</w:t></w:r></w:p></w:footnote>";

        string body = "<w:p>" + WordprocessingFixtures.R("text") + "<w:r><w:rPr><w:rStyle w:val=\"FootnoteReference\"/></w:rPr><w:footnoteReference w:id=\"1\"/></w:r></w:p>";

        Assert.Equal("text\n the note\n", WordprocessingFixtures.Extract(Package(body, footnotes: note)));
    }

    // ---------------------------------------------------------------- notes nothing visible references

    [Fact]
    public void Extract_UnreferencedNotes_AreNotProjected()
    {
        string footnotes = Footnote(-1, "SEPARATOR", "separator") + Footnote(1, "UNREFERENCED_NOTE_INJECTION");

        string endnotes = Endnote(1, "UNREFERENCED_ENDNOTE_INJECTION");

        Assert.Equal("body\n", WordprocessingFixtures.Extract(Package(WordprocessingFixtures.P("body"), footnotes, endnotes)));
    }

    [Fact]
    public void Extract_NoteReferencedOnlyFromADeletedRun_IsNotProjected()
    {
        string body = $"<w:p>{WordprocessingFixtures.R("body")}<w:del {Revision}>{FootRef(1)}</w:del><w:moveFrom {Revision}>{FootRef(2)}</w:moveFrom></w:p>";

        string text = WordprocessingFixtures.Extract(Package(body, footnotes: Footnote(1, "DELETED_REFERENCE_NOTE") + Footnote(2, "MOVED_AWAY_REFERENCE_NOTE")));

        Assert.Equal("body\n", text);
    }

    [Fact]
    public void Extract_NoteReferencedByAnInsertedRun_IsProjected()
    {
        string body = $"<w:p>{WordprocessingFixtures.R("body")}<w:ins {Revision}>{FootRef(1)}</w:ins></w:p>";

        Assert.Equal("body\nnote\n", WordprocessingFixtures.Extract(Package(body, footnotes: Footnote(1, "note"))));
    }

    [Fact]
    public void Extract_NoteReferencedOnlyFromAVanishedRun_IsNotProjected()
    {
        string vanished = "<w:r>" + Vanish + "<w:footnoteReference w:id=\"1\"/></w:r>";

        string text = WordprocessingFixtures.Extract(Package("<w:p>" + WordprocessingFixtures.R("body") + vanished + "</w:p>", footnotes: Footnote(1, "VANISHED_REFERENCE_NOTE")));

        Assert.Equal("body\n", text);
    }

    [Fact]
    public void Extract_NoteReferencedOnlyFromARunHiddenByAStyle_IsNotProjected()
    {
        string styles =
            "<w:style w:type=\"character\" w:styleId=\"HiddenRun\">" + Vanish + "</w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"HiddenPara\">" + Vanish + "</w:style>";

        string byCharacterStyle = "<w:p>" + WordprocessingFixtures.R("body") + "<w:r><w:rPr><w:rStyle w:val=\"HiddenRun\"/></w:rPr><w:footnoteReference w:id=\"1\"/></w:r></w:p>";

        string byParagraphStyle = "<w:p><w:pPr><w:pStyle w:val=\"HiddenPara\"/></w:pPr>" + FootRef(2) + "</w:p>";

        string text = WordprocessingFixtures.Extract(Package(
            byCharacterStyle + byParagraphStyle,
            footnotes: Footnote(1, "HIDDEN_BY_CHARACTER_STYLE_NOTE") + Footnote(2, "HIDDEN_BY_PARAGRAPH_STYLE_NOTE"),
            styles: styles));

        Assert.Equal("body\n\n", text);
    }

    [Fact]
    public void Extract_NoteReferencedOnlyFromFieldCode_IsNotProjected()
    {
        string begin = "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>";

        string separate = "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>";

        string end = "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

        string body = "<w:p>" + WordprocessingFixtures.R("body") + begin + FootRef(1) + separate + WordprocessingFixtures.R("result") + FootRef(2) + end + "</w:p>";

        string text = WordprocessingFixtures.Extract(Package(body, footnotes: Footnote(1, "FIELD_CODE_REFERENCE_NOTE") + Footnote(2, "field result reference note")));

        Assert.Equal("bodyresult\nfield result reference note\n", text);
    }

    [Fact]
    public void Extract_NoteReferencedOnlyFromAHiddenTable_IsNotProjected()
    {
        string styles = "<w:style w:type=\"table\" w:styleId=\"HiddenTable\">" + Vanish + "</w:style>";

        string body = "<w:tbl><w:tblPr><w:tblStyle w:val=\"HiddenTable\"/></w:tblPr><w:tr><w:tc><w:p>" + FootRef(1) + "</w:p></w:tc></w:tr></w:tbl>";

        string text = WordprocessingFixtures.Extract(Package(body + WordprocessingFixtures.P("shown"), footnotes: Footnote(1, "HIDDEN_TABLE_REFERENCE_NOTE"), styles: styles));

        Assert.Equal("\nshown\n", text);
    }

    [Fact]
    public void Extract_NoteReferencedOnlyFromAHiddenDrawing_IsNotProjectedWhetherTheFlagComesFirstOrLast()
    {
        // A drawing's text is decided when the drawing ends, so a reference inside it counts only if the drawing is shown.
        string Drawing(string flagFirst, string flagLast, int id) =>
            "<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/>" + flagFirst + "<a:graphic><a:graphicData><wps:wsp><wps:txbx><w:txbxContent>"
            + "<w:p>" + FootRef(id) + "</w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic>" + flagLast + "</wp:inline></w:drawing></w:r></w:p>";

        string hiddenFirst = Drawing("<wp:docPr id=\"1\" name=\"b\" hidden=\"1\"/>", string.Empty, 1);

        string hiddenLast = Drawing(string.Empty, "<wp:docPr id=\"2\" name=\"b\" hidden=\"1\"/>", 2);

        string shown = Drawing("<wp:docPr id=\"3\" name=\"b\"/>", string.Empty, 3);

        string text = WordprocessingFixtures.Extract(Package(
            hiddenFirst + hiddenLast + shown,
            footnotes: Footnote(1, "HIDDEN_FIRST_NOTE") + Footnote(2, "HIDDEN_LAST_NOTE") + Footnote(3, "shown note")));

        Assert.Equal("\n\n\n\nshown note\n", text);
    }

    [Fact]
    public void Extract_NoteReferencedFromATextBoxOrACell_IsProjected()
    {
        string box = "<w:r><w:pict><v:shape><v:textbox><w:txbxContent><w:p>" + FootRef(1) + "</w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

        string cell = "<w:tbl><w:tr><w:tc><w:p>" + FootRef(2) + "</w:p></w:tc></w:tr></w:tbl>";

        string text = WordprocessingFixtures.Extract(Package("<w:p>" + box + "</w:p>" + cell, footnotes: Footnote(1, "box note") + Footnote(2, "cell note")));

        Assert.Equal("\n\n\nbox note\ncell note\n", text);
    }

    [Fact]
    public void Extract_ReferenceToASeparatorOrAMissingNote_ProjectsNothing()
    {
        string footnotes = Footnote(-1, "SEPARATOR_FOOTNOTE", "separator") + Footnote(0, "CONTINUATION", "continuationSeparator") + Footnote(1, "real");

        Assert.Equal("body\nreal\n", WordprocessingFixtures.Extract(Package(Body("body", FootRef(-1), FootRef(0), FootRef(7), FootRef(1)), footnotes)));
    }

    [Fact]
    public void Extract_ReferenceWithoutANumericId_ReachesNoNote()
    {
        string body = "<w:p>" + WordprocessingFixtures.R("body") + "<w:r><w:footnoteReference w:id=\"one\"/></w:r><w:r><w:footnoteReference/></w:r></w:p>";

        Assert.Equal("body\n", WordprocessingFixtures.Extract(Package(body, footnotes: Footnote(1, "NOT_REACHED"))));
    }

    [Fact]
    public void Extract_ReferenceMarkOutsideARun_ReachesNoNote()
    {
        string body = "<w:p>" + WordprocessingFixtures.R("body") + "<w:footnoteReference w:id=\"1\"/></w:p>";

        Assert.Equal("body\n", WordprocessingFixtures.Extract(Package(body, footnotes: Footnote(1, "NOT_REACHED"))));
    }

    // ---------------------------------------------------------------- holding notes that are met out of order

    [Fact]
    public void Extract_NoteMetBeforeItsTurn_IsHeldUntilTheEarlierReferencedNotesAreProjected()
    {
        string text = WordprocessingFixtures.Extract(Package(
            Body("x", FootRef(3), FootRef(1), FootRef(2)),
            footnotes: Footnote(1, "one") + Footnote(2, "two") + Footnote(3, "three")));

        Assert.Equal("x\nthree\none\ntwo\n", text);
    }

    [Fact]
    public void Extract_NoteReferencedBeforeANoteThatTheNotesPartLacks_StillProjectsTheOthersInOrder()
    {
        string text = WordprocessingFixtures.Extract(Package(
            Body("x", FootRef(9), FootRef(2), FootRef(1)),
            footnotes: Footnote(1, "one") + Footnote(2, "two")));

        Assert.Equal("x\ntwo\none\n", text);
    }

    [Fact]
    public void Extract_ReferencedNoteWhoseIdAppearsTwiceInTheNotesPart_IsFormatMismatch()
    {
        // Which definition a reader would see is not specified, so the package is refused rather than guessed at.
        byte[] package = Package(Body("x", FootRef(1)), footnotes: Footnote(1, "first") + Footnote(1, "second"));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_HeldNotesBeyondTheBufferBound_TruncateInsteadOfGrowingWithoutBound()
    {
        // The note referenced second is met first in the part, so it is held; it is larger than the bound.
        string huge = new('x', WordprocessingOutput.MaximumBufferedCharacters + 1);

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(
            Package(Body("x", FootRef(1), FootRef(2)), footnotes: Footnote(2, huge) + Footnote(1, "one")),
            maximumCharacters: 1_000_000);

        Assert.True(result.WasTruncated);

        Assert.Equal("x\n", result.Text);
    }

    [Fact]
    public void Extract_MoreVisibleNoteReferencesThanTheBound_IsStructureBounds()
    {
        string references = string.Concat(Enumerable.Range(0, WordprocessingPartWalker.MaximumNoteReferences + 1).Select(FootRef));

        Assert.Equal(
            DocumentPackageRejection.StructureBounds,
            WordprocessingFixtures.Rejection(Package("<w:p>" + references + "</w:p>", footnotes: Footnote(1, "note"))));
    }

    [Fact]
    public void Extract_ExactlyTheBoundOfNoteReferences_IsAccepted()
    {
        string references = string.Concat(Enumerable.Range(0, WordprocessingPartWalker.MaximumNoteReferences).Select(FootRef));

        Assert.Equal("\nnote\n", WordprocessingFixtures.Extract(Package("<w:p>" + references + "</w:p>", footnotes: Footnote(0, "note"))));
    }

    [Theory]
    [InlineData("separator")]
    [InlineData("continuationSeparator")]
    [InlineData("continuationNotice")]
    public void Extract_SeparatorNotes_AreSkipped(string type)
    {
        // Sensitivity: skipping the three separator note types. Each carries text a real note would never hold, and is
        // referenced here so only the type keeps it out.
        string text = WordprocessingFixtures.Extract(Package(
            Body("body", FootRef(-1), FootRef(1), EndRef(-1), EndRef(1)),
            footnotes: Footnote(-1, "SEPARATOR_FOOTNOTE", type) + Footnote(1, "real footnote"),
            endnotes: Endnote(-1, "SEPARATOR_ENDNOTE", type) + Endnote(1, "real endnote")));

        Assert.Equal("body\nreal footnote\nreal endnote\n", text);
    }

    [Fact]
    public void Extract_NormalNotesAndNotesOfAnUnknownType_AreProjected()
    {
        string text = WordprocessingFixtures.Extract(Package(
            Body("body", FootRef(1), FootRef(2)),
            footnotes: Footnote(1, "normal", "normal") + Footnote(2, "other", "something-else")));

        Assert.Equal("body\nnormal\nother\n", text);
    }

    [Fact]
    public void Extract_ContentThatIsNotANote_InsideANotesPart_IsSkipped()
    {
        string footnotes = WordprocessingFixtures.P("STRAY_PARAGRAPH") + Footnote(1, "note") + Endnote(1, "WRONG_KIND_OF_NOTE");

        string endnotes = Footnote(1, "WRONG_KIND_OF_NOTE_IN_ENDNOTES") + Endnote(1, "end");

        Assert.Equal("body\nnote\nend\n", WordprocessingFixtures.Extract(Package(Body("body", FootRef(1), EndRef(1)), footnotes, endnotes)));
    }

    [Fact]
    public void Extract_NoteWithTablesHyperlinksAndInsertions_IsProjected()
    {
        string note =
            "<w:footnote w:id=\"1\"><w:tbl><w:tr><w:tc>" + WordprocessingFixtures.P("cell") + "</w:tc></w:tr></w:tbl>"
            + "<w:p><w:hyperlink w:anchor=\"a\">" + WordprocessingFixtures.R("link") + "</w:hyperlink><w:ins w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\">" + WordprocessingFixtures.R(" added") + "</w:ins>"
            + "<w:del w:id=\"2\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\">" + WordprocessingFixtures.R(" REMOVED") + "</w:del></w:p></w:footnote>";

        Assert.Equal("body\ncell\nlink added\n", WordprocessingFixtures.Extract(Package(Body("body", FootRef(1)), note)));
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

        string text = WordprocessingFixtures.Extract(Package(Body("body", FootRef(1)), footnotes, styles: styles));

        Assert.Equal("body\n\nvisible\n", text);
    }

    [Fact]
    public void Extract_FieldCodeStateDoesNotCarryFromTheBodyIntoTheNotes()
    {
        // The body ends inside an unterminated field's code; the notes part starts afresh.
        string body = "<w:p>" + WordprocessingFixtures.R("body") + FootRef(1) + EndRef(1) + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r></w:p>";

        string text = WordprocessingFixtures.Extract(Package(body, footnotes: Footnote(1, "note"), endnotes: Endnote(1, "end")));

        Assert.Equal("bodynote\nend\n", text);
    }

    [Fact]
    public void Extract_FieldCodeStateDoesNotCarryFromFootnotesIntoEndnotes()
    {
        string footnotes = "<w:footnote w:id=\"1\"><w:p>" + WordprocessingFixtures.R("note") + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r></w:p></w:footnote>";

        string text = WordprocessingFixtures.Extract(Package(Body("body", FootRef(1), EndRef(1)), footnotes, endnotes: Endnote(1, "end")));

        Assert.Equal("body\nnoteend\n", text);
    }

    [Fact]
    public void Extract_AlternateContentInANote_ProjectsTheFirstChoiceOnly()
    {
        string note =
            "<w:footnote w:id=\"1\"><w:p><mc:AlternateContent><mc:Choice Requires=\"wps\">" + WordprocessingFixtures.R("choice") + "</mc:Choice><mc:Fallback>" + WordprocessingFixtures.R("FALLBACK") + "</mc:Fallback></mc:AlternateContent></w:p></w:footnote>";

        Assert.Equal("body\nchoice\n", WordprocessingFixtures.Extract(Package(Body("body", FootRef(1)), note)));
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
    public void Extract_MalformedUnreferencedNote_IsStillMalformedXml()
    {
        // Notes nothing references are skipped, not unread: a part that is not well-formed is refused whether or not the
        // malformed note is one a reference reaches.
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(Body("body", FootRef(1))),
            footnotesXml: $"<w:footnotes {WordprocessingFixtures.Namespaces()}>" + Footnote(1, "note") + "<w:footnote w:id=\"2\"><w:p>");

        Assert.Equal(DocumentPackageRejection.MalformedXml, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_NotesPartsAreFoundByRelationshipNotByName()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/document.xml"))),
            ("word/document.xml", WordprocessingFixtures.Document(Body("body", FootRef(1)))),
            ("word/_rels/document.xml.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rIdN", WordprocessingFixtures.FootnotesType, "notes/feet.xml"))),
            ("word/notes/feet.xml", WordprocessingFixtures.Footnotes(Footnote(1, "from the related part"))),
            ("word/footnotes.xml", WordprocessingFixtures.Footnotes(Footnote(1, "DECOY_BY_NAME"))));

        Assert.Equal("body\nfrom the related part\n", WordprocessingFixtures.Extract(package));
    }

    [Fact]
    public void Extract_NotesPartSmuggledWithoutARelationship_IsNeverRead()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(Body("body", FootRef(1))),
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
            Package(Body("body", FootRef(1)), footnotes: Footnote(1, "footnote text")), maximumCharacters: 9);

        Assert.Equal("body\nfoot", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_OutputCeilingReachedInTheFootnotes_NeverReadsTheEndnotes()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(Body("body", FootRef(1), EndRef(1))),
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
            WordprocessingFixtures.Document(Body("a long body paragraph", FootRef(1), EndRef(1))),
            footnotesXml: WordprocessingFixtures.Footnotes(Footnote(1, "note")),
            endnotesXml: "<<< malformed");

        Assert.Equal("a long", WordprocessingFixtures.ExtractResult(package, maximumCharacters: 6).Text);
    }
}
