using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The D8 hidden-content posture for Word: revisions, non-rendered markup, markup compatibility, ruby text, hidden
/// drawings and field code. (Formatting-hidden text is covered by the vanish tests.) Several
/// fixtures put a <c>w:t</c> where the schema would not, because the rule under test is "skip the whole subtree".
/// </summary>
public sealed class WordprocessingHiddenContentTests
{
    private static string Wrap(string runContent) => $"<w:p>{runContent}</w:p>";

    // ---------------------------------------------------------------- revisions

    [Fact]
    public void Extract_DeletedRuns_AreExcluded()
    {
        // Sensitivity: skipping w:del. The second run uses w:t, which only the subtree skip keeps out.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                WordprocessingFixtures.R("kept ")
                + "<w:del w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:r><w:delText>DELETED_TEXT</w:delText></w:r><w:r><w:t>DELETED_PLAIN</w:t></w:r></w:del>"
                + WordprocessingFixtures.R("end"))));

        Assert.Equal("kept end\n", text);
    }

    [Fact]
    public void Extract_MovedFromRuns_AreExcludedWhileMovedToRunsAreIncluded()
    {
        // Sensitivity: skipping w:moveFrom (Word keeps w:t inside it) while still including w:moveTo.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<w:moveFrom w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:r><w:t>MOVED_AWAY</w:t></w:r></w:moveFrom>")
            + Wrap("<w:moveTo w:id=\"2\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:r><w:t>moved here</w:t></w:r></w:moveTo>")));

        Assert.Equal("\nmoved here\n", text);
    }

    [Fact]
    public void Extract_DeletionInsideAnInsertion_IsExcludedAndTheRestOfTheInsertionKept()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<w:ins w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:del w:id=\"2\" w:author=\"b\" w:date=\"2026-10-22T10:00:00Z\"><w:r><w:t>INSERTED_THEN_DELETED</w:t></w:r></w:del><w:r><w:t>kept</w:t></w:r></w:ins>")));

        Assert.Equal("kept\n", text);
    }

    [Fact]
    public void Extract_DeletedTextElementOutsideADeletion_IsExcluded()
    {
        // Sensitivity: skipping w:delText as a subtree (the nested run is contrived; only a subtree skip keeps it out).
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<w:r><w:delText>STRAY_DELETED</w:delText><w:delText><w:r><w:t>NESTED_RUN_IN_DELTEXT</w:t></w:r></w:delText><w:t>shown</w:t></w:r>")));

        Assert.Equal("shown\n", text);
    }

    // ---------------------------------------------------------------- field instructions and field code

    [Fact]
    public void Extract_ComplexField_ProjectsTheResultAndNotTheInstruction()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                WordprocessingFixtures.R("Page ")
                + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>"
                + "<w:r><w:instrText xml:space=\"preserve\"> PAGE \\* MERGEFORMAT </w:instrText></w:r>"
                + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>"
                + WordprocessingFixtures.R("7")
                + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>")));

        Assert.Equal("Page 7\n", text);
    }

    [Fact]
    public void Extract_InstructionTextElements_AreSkippedAsSubtrees()
    {
        // Sensitivity: skipping w:instrText and w:delInstrText (the nested run is contrived; only a subtree skip keeps it out).
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<w:r><w:instrText><w:r><w:t>INSTR_NESTED</w:t></w:r></w:instrText><w:delInstrText><w:r><w:t>DELINSTR_NESTED</w:t></w:r></w:delInstrText><w:t>shown</w:t></w:r>")));

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_SimpleFieldResult_IsProjectedAndItsInstructionAttributeIsNot()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<w:fldSimple w:instr=\" DATE \\@ &quot;yyyy&quot; INSTRUCTION_ATTRIBUTE \"><w:r><w:t>2026</w:t></w:r></w:fldSimple>")));

        Assert.Equal("2026\n", text);
    }

    [Fact]
    public void Extract_TextBetweenFieldBeginAndSeparate_IsFieldCodeAndExcluded()
    {
        // Sensitivity: tracking the field-code region. A w:t where Word keeps the instruction is still instruction.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                WordprocessingFixtures.R("before ")
                + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>"
                + WordprocessingFixtures.R("HIDDEN_FIELD_CODE")
                + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>"
                + WordprocessingFixtures.R("result")
                + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>"
                + WordprocessingFixtures.R(" after"))));

        Assert.Equal("before result after\n", text);
    }

    [Fact]
    public void Extract_FieldWithoutASeparator_IsEntirelyCode()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                WordprocessingFixtures.R("a")
                + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>"
                + WordprocessingFixtures.R("NO_RESULT_CODE")
                + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>"
                + WordprocessingFixtures.R("b"))));

        Assert.Equal("ab\n", text);
    }

    [Fact]
    public void Extract_NestedFields_ProjectOnlyTheResultsAndResumeAfterTheInnerFieldEnds()
    {
        string begin = "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>";

        string separate = "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>";

        string end = "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

        // outer code ( inner code | inner result ) more outer code | outer result
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                begin + WordprocessingFixtures.R("OUTER_CODE_1")
                + begin + WordprocessingFixtures.R("INNER_CODE") + separate + WordprocessingFixtures.R("INNER_RESULT_IN_OUTER_CODE") + end
                + WordprocessingFixtures.R("OUTER_CODE_2") + separate + WordprocessingFixtures.R("outer result ") + end
                + WordprocessingFixtures.R("tail"))));

        Assert.Equal("outer result tail\n", text);
    }

    [Fact]
    public void Extract_NestedFieldInsideAResult_ProjectsTheInnerResult()
    {
        string begin = "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>";

        string separate = "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>";

        string end = "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                begin + WordprocessingFixtures.R("OUTER_CODE") + separate + WordprocessingFixtures.R("result: ")
                + begin + WordprocessingFixtures.R("INNER_CODE") + separate + WordprocessingFixtures.R("inner result") + end
                + end)));

        Assert.Equal("result: inner result\n", text);
    }

    [Fact]
    public void Extract_FieldCodeSpanningParagraphs_EmitsNothingForThem()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(WordprocessingFixtures.R("a") + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>")
            + Wrap(WordprocessingFixtures.R("CODE_IN_SECOND_PARAGRAPH"))
            + Wrap("<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" + WordprocessingFixtures.R("result") + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>")));

        // The paragraph marks inside the code region are code too, so "a" runs straight into the result.
        Assert.Equal("aresult\n", text);
    }

    [Fact]
    public void Extract_UnterminatedFieldCode_SuppressesTheRestOfThePart()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(WordprocessingFixtures.R("visible") + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>")
            + Wrap(WordprocessingFixtures.R("UNREACHABLE_TAIL"))));

        Assert.Equal("visible", text);
    }

    [Fact]
    public void Extract_FieldEndWithoutABegin_DoesNotAffectLaterText()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" + WordprocessingFixtures.R("ok"))));

        Assert.Equal("ok\n", text);
    }

    [Fact]
    public void Extract_FieldNestingBeyondTheBound_IsStructureBounds()
    {
        string begins = string.Concat(Enumerable.Repeat("<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>", 65));

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx(Wrap(begins))));
    }

    [Fact]
    public void Extract_FieldNestingAtTheBound_IsAccepted()
    {
        string begins = string.Concat(Enumerable.Repeat("<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>", 64));

        string ends = string.Concat(Enumerable.Repeat("<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>", 64));

        Assert.Equal("tail\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(begins + ends + WordprocessingFixtures.R("tail")))));
    }

    // ---------------------------------------------------------------- markup compatibility

    [Fact]
    public void Extract_AlternateContent_ProjectsTheFirstChoiceOnceAndNeverTheFallback()
    {
        // Sensitivity: skipping mc:Fallback. Choice and Fallback carry the same text; it must appear exactly once.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<w:r><mc:AlternateContent>"
                + "<mc:Choice Requires=\"wps\"><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"b\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
                + WordprocessingFixtures.P("shared box text") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></mc:Choice>"
                + "<mc:Fallback><w:pict><v:shape><v:textbox><w:txbxContent>" + WordprocessingFixtures.P("shared box text") + "</w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback>"
                + "</mc:AlternateContent></w:r>")));

        Assert.Equal("shared box text\n\n", text);
    }

    [Fact]
    public void Extract_AlternateContentWithSeveralChoices_ProjectsOnlyTheFirst()
    {
        // Sensitivity: skipping every mc:Choice after the first.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<w:r><mc:AlternateContent>"
                + "<mc:Choice Requires=\"w14\">" + WordprocessingFixtures.R("first choice") + "</mc:Choice>"
                + "<mc:Choice Requires=\"w15\">" + WordprocessingFixtures.R("SECOND_CHOICE") + "</mc:Choice>"
                + "<mc:Choice Requires=\"w16\">" + WordprocessingFixtures.R("THIRD_CHOICE") + "</mc:Choice>"
                + "<mc:Fallback>" + WordprocessingFixtures.R("FALLBACK") + "</mc:Fallback>"
                + "</mc:AlternateContent></w:r>")));

        Assert.Equal("first choice\n", text);
    }

    [Fact]
    public void Extract_AlternateContentWithOnlyAFallback_ProjectsTheFallback()
    {
        // Sensitivity: never neither. With no choice to take, the fallback is what a reader sees.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<mc:AlternateContent><mc:Fallback>" + WordprocessingFixtures.R("only fallback ") + "</mc:Fallback></mc:AlternateContent>" + WordprocessingFixtures.R("body"))));

        Assert.Equal("only fallback body\n", text);
    }

    [Fact]
    public void Extract_AlternateContentAtBlockLevel_ProjectsTheFirstChoiceOnly()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<mc:AlternateContent><mc:Choice Requires=\"w14\">" + WordprocessingFixtures.P("choice") + "</mc:Choice><mc:Fallback>" + WordprocessingFixtures.P("FALLBACK") + "</mc:Fallback></mc:AlternateContent>"));

        Assert.Equal("choice\n", text);
    }

    [Fact]
    public void Extract_FallbackAndChoiceOutsideAnAlternateContent_AreNotProjected()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<mc:Fallback>" + WordprocessingFixtures.P("STRAY_FALLBACK") + "</mc:Fallback><mc:Choice Requires=\"x\">" + WordprocessingFixtures.P("STRAY_CHOICE") + "</mc:Choice>" + WordprocessingFixtures.P("body")));

        Assert.Equal("body\n", text);
    }

    [Fact]
    public void Extract_AlternateContentWhoseOnlyChoiceRequiresAnUnknownPrefix_ProjectsTheFallbackAndNotTheChoice()
    {
        // Sensitivity: evaluating mc:Choice/@Requires. Word cannot take this choice, so it shows the fallback; projecting
        // the choice would surface text the reader never sees.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<mc:AlternateContent><mc:Choice Requires=\"bogus\">" + WordprocessingFixtures.R("HIDDEN_CHOICE") + "</mc:Choice>"
                + "<mc:Fallback>" + WordprocessingFixtures.R("fallback ") + "</mc:Fallback></mc:AlternateContent>" + WordprocessingFixtures.R("body"))));

        Assert.Equal("fallback body\n", text);
    }

    [Theory]
    [InlineData("<mc:Choice Requires=\"foo\" xmlns:foo=\"urn:not:a:word:namespace\">")]
    [InlineData("<mc:Choice Requires=\"wps\" xmlns:wps=\"urn:rebound:to:something:else\">")]
    [InlineData("<mc:Choice Requires=\"wps foo\" xmlns:foo=\"urn:not:a:word:namespace\">")]
    [InlineData("<mc:Choice Requires=\"w14 undeclared\">")]
    [InlineData("<mc:Choice Requires=\"\">")]
    [InlineData("<mc:Choice Requires=\"   \">")]
    [InlineData("<mc:Choice>")]
    [InlineData("<mc:Choice Requires=\"w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14\">")]
    public void Extract_ChoiceThatCouldNotBeTaken_IsSkippedAndTheNextChoiceThatCanBeIsProjected(string choiceStart)
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<mc:AlternateContent>" + choiceStart + WordprocessingFixtures.R("HIDDEN_CHOICE") + "</mc:Choice>"
                + "<mc:Choice Requires=\"w14\">" + WordprocessingFixtures.R("second choice ") + "</mc:Choice>"
                + "<mc:Fallback>" + WordprocessingFixtures.R("FALLBACK") + "</mc:Fallback></mc:AlternateContent>" + WordprocessingFixtures.R("body"))));

        Assert.Equal("second choice body\n", text);
    }

    [Theory]
    [InlineData("<mc:Choice Requires=\"foo\" xmlns:foo=\"urn:not:a:word:namespace\">")]
    [InlineData("<mc:Choice Requires=\"wps\" xmlns:wps=\"urn:rebound:to:something:else\">")]
    [InlineData("<mc:Choice Requires=\"wps foo\" xmlns:foo=\"urn:not:a:word:namespace\">")]
    [InlineData("<mc:Choice Requires=\"w14 undeclared\">")]
    [InlineData("<mc:Choice Requires=\"\">")]
    [InlineData("<mc:Choice>")]
    [InlineData("<mc:Choice Requires=\"w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14 w14\">")]
    public void Extract_NoChoiceThatCanBeTaken_ProjectsTheFallbackAndNeverBoth(string choiceStart)
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<mc:AlternateContent>" + choiceStart + WordprocessingFixtures.R("HIDDEN_CHOICE") + "</mc:Choice>"
                + "<mc:Fallback>" + WordprocessingFixtures.R("fallback ") + "</mc:Fallback></mc:AlternateContent>" + WordprocessingFixtures.R("body"))));

        Assert.Equal("fallback body\n", text);
    }

    [Theory]
    [InlineData("<mc:Choice Requires=\"w14\">")]
    [InlineData("<mc:Choice Requires=\"wps\">")]
    [InlineData("<mc:Choice Requires=\"wps w14 wp14\">")]
    [InlineData("<mc:Choice Requires=\"  wpg\tw15\n wpc  \">")]
    [InlineData("<mc:Choice Requires=\"anything\" xmlns:anything=\"http://schemas.microsoft.com/office/word/2010/wordml\">")]
    [InlineData("<mc:Choice Requires=\"v\">")]
    [InlineData("<mc:Choice Requires=\"a14\" xmlns:a14=\"http://schemas.microsoft.com/office/drawing/2010/main\">")]
    public void Extract_ChoiceRequiringOnlyNamespacesWordUnderstands_IsProjected(string choiceStart)
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<mc:AlternateContent>" + choiceStart + WordprocessingFixtures.R("chosen") + "</mc:Choice>"
                + "<mc:Fallback>" + WordprocessingFixtures.R("FALLBACK") + "</mc:Fallback></mc:AlternateContent>")));

        Assert.Equal("chosen\n", text);
    }

    [Fact]
    public void Extract_ElementInAnUnknownNamespace_IsSkippedWithItsSubtree()
    {
        // Sensitivity: only known content namespaces are descended into. Word drops an element from a namespace that
        // mc:Ignorable names and it does not understand, together with everything inside it.
        string main =
            $"<w:document {WordprocessingFixtures.Namespaces()} xmlns:foo=\"urn:unknown:ignorable\" mc:Ignorable=\"foo\"><w:body>"
            + "<foo:wrapper>" + WordprocessingFixtures.P("HIDDEN_BLOCK") + "</foo:wrapper>"
            + "<w:p><foo:inline><w:r><w:t>HIDDEN_INLINE</w:t></w:r></foo:inline>" + WordprocessingFixtures.R("shown")
            + "<w:r><foo:inRun><w:t>HIDDEN_IN_RUN</w:t></foo:inRun><w:t> end</w:t></w:r></w:p></w:body></w:document>";

        Assert.Equal("shown end\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Package(main)));
    }

    [Fact]
    public void Extract_DrawingGroupsAndCanvases_ProjectTheirTextBoxes()
    {
        string group =
            "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"Group\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\"><wpg:wgp><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("in group") + "</w:txbxContent></wps:txbx></wps:wsp></wpg:wgp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

        string canvas =
            "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"2\" name=\"Canvas\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas\"><wpc:wpc><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("in canvas") + "</w:txbxContent></wps:txbx></wps:wsp></wpc:wpc></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

        Assert.Equal("in group\n\nin canvas\n\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(group) + Wrap(canvas))));
    }

    public static TheoryData<string> NamespacesWordUnderstands() =>
    [
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main",
        "http://purl.oclc.org/ooxml/wordprocessingml/main",
        "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing",
        "http://purl.oclc.org/ooxml/drawingml/wordprocessingDrawing",
        "http://schemas.openxmlformats.org/drawingml/2006/main",
        "http://purl.oclc.org/ooxml/drawingml/main",
        "http://schemas.microsoft.com/office/word/2010/wordprocessingShape",
        "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup",
        "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas",
        "http://schemas.microsoft.com/office/word/2010/wordprocessingInk",
        "http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing",
        "http://schemas.microsoft.com/office/word/2010/wordml",
        "http://schemas.microsoft.com/office/word/2012/wordml",
        "http://schemas.microsoft.com/office/word/2006/wordml",
        "http://schemas.microsoft.com/office/word/2016/wordml/cid",
        "http://schemas.microsoft.com/office/word/2018/wordml",
        "http://schemas.microsoft.com/office/word/2018/wordml/cex",
        "http://schemas.microsoft.com/office/drawing/2010/main",
        "http://schemas.microsoft.com/office/drawing/2014/main",
        "http://schemas.microsoft.com/office/drawing/2016/SVG/main",
        "http://schemas.openxmlformats.org/officeDocument/2006/math",
        "http://purl.oclc.org/ooxml/officeDocument/math",
        "urn:schemas-microsoft-com:vml",
        "urn:schemas-microsoft-com:office:office",
        "urn:schemas-microsoft-com:office:word",
    ];

    public static TheoryData<string> NamespacesNotUnderstood() =>
    [
        // The emoji extension: its choice holds no text, and the fallback holds the character.
        "http://schemas.microsoft.com/office/word/2015/wordml/symex",
        "http://schemas.microsoft.com/office/word/2023/wordml/word16du",
        "http://schemas.microsoft.com/office/word/2016/wordml/chartex",
        "urn:schemas-microsoft-com:office:excel",
        "http://schemas.openxmlformats.org/wordprocessingml/2006/mainX",
    ];

    [Theory]
    [MemberData(nameof(NamespacesNotUnderstood))]
    public void Extract_ChoiceRequiringANamespaceTheExtractorDoesNotUnderstand_ProjectsTheFallback(string namespaceUri)
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                $"<mc:AlternateContent><mc:Choice Requires=\"req\" xmlns:req=\"{namespaceUri}\">" + WordprocessingFixtures.R("CHOICE") + "</mc:Choice>"
                + "<mc:Fallback>" + WordprocessingFixtures.R("fallback") + "</mc:Fallback></mc:AlternateContent>")));

        Assert.Equal("fallback\n", text);
    }

    [Theory]
    [MemberData(nameof(NamespacesWordUnderstands))]
    public void Extract_ChoiceRequiringAnyOneOfTheNamespacesWordReads_IsProjected(string namespaceUri)
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                $"<mc:AlternateContent><mc:Choice Requires=\"req\" xmlns:req=\"{namespaceUri}\">" + WordprocessingFixtures.R("chosen") + "</mc:Choice>"
                + "<mc:Fallback>" + WordprocessingFixtures.R("FALLBACK") + "</mc:Fallback></mc:AlternateContent>")));

        Assert.Equal("chosen\n", text);
    }

    [Theory]
    [InlineData("simplePos")]
    [InlineData("positionH")]
    [InlineData("positionV")]
    [InlineData("extent")]
    [InlineData("effectExtent")]
    [InlineData("wrapNone")]
    [InlineData("wrapSquare")]
    [InlineData("wrapTight")]
    [InlineData("wrapThrough")]
    [InlineData("wrapTopAndBottom")]
    public void Extract_DrawingLayoutElementBeforeItsDocPr_IsAccepted(string layoutElement)
    {
        string drawing =
            $"<w:r><w:drawing><wp:anchor><wp:{layoutElement}/><wp:docPr id=\"1\" name=\"Box\" hidden=\"1\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("HIDDEN_BOX") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>";

        Assert.Equal("body\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(drawing + WordprocessingFixtures.R("body")))));
    }

    [Fact]
    public void Extract_StrictDrawingMlGraphic_ProjectsItsTextBox()
    {
        string drawing =
            "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"Box\"/><s:graphic xmlns:s=\"http://purl.oclc.org/ooxml/drawingml/main\"><s:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("strict box") + "</w:txbxContent></wps:txbx></wps:wsp></s:graphicData></s:graphic></wp:inline></w:drawing></w:r>";

        Assert.Equal("strict box\n\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(drawing))));
    }

    [Fact]
    public void Extract_ChoiceRequiringExactlyTheMaximumNumberOfPrefixes_IsProjected()
    {
        string requires = string.Join(' ', Enumerable.Repeat("w14", 16));

        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap($"<mc:AlternateContent><mc:Choice Requires=\"{requires}\">" + WordprocessingFixtures.R("chosen") + "</mc:Choice></mc:AlternateContent>")));

        Assert.Equal("chosen\n", text);
    }

    [Fact]
    public void Extract_ElementNamedChoiceInAnotherNamespace_IsNotTheChoice()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<mc:AlternateContent><w:Choice>" + WordprocessingFixtures.R("NOT_A_CHOICE") + "</w:Choice>"
                + "<mc:Choice Requires=\"w14\">" + WordprocessingFixtures.R("chosen") + "</mc:Choice></mc:AlternateContent>")));

        Assert.Equal("chosen\n", text);
    }

    [Fact]
    public void Extract_ParagraphPropertiesWhereNoParagraphIsTheParent_AreSkipped()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:tbl><w:tr><w:tc><w:pPr><w:r><w:t>STRAY_PPR</w:t></w:r></w:pPr>" + WordprocessingFixtures.P("shown") + "</w:tc></w:tr></w:tbl>"));

        Assert.Equal("shown\n", text);
    }

    // ---------------------------------------------------------------- ruby

    [Fact]
    public void Extract_Ruby_ProjectsTheBaseTextAndNotThePhoneticGuide()
    {
        // Sensitivity: skipping w:rt.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<w:r><w:ruby><w:rubyPr><w:rubyAlign w:val=\"center\"/></w:rubyPr>"
                + "<w:rt><w:r><w:t>PHONETIC_GUIDE</w:t></w:r></w:rt>"
                + "<w:rubyBase><w:r><w:t>base</w:t></w:r></w:rubyBase></w:ruby></w:r>")));

        Assert.Equal("base\n", text);
    }

    // ---------------------------------------------------------------- hidden drawings

    private static string Drawing(string container, string docPrAttributes, string boxText) =>
        $"<w:r><w:drawing><wp:{container}><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"Box\"{docPrAttributes}/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
        + WordprocessingFixtures.P(boxText)
        + $"</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:{container}></w:drawing></w:r>";

    [Theory]
    [InlineData("inline", " hidden=\"1\"")]
    [InlineData("inline", " hidden=\"true\"")]
    [InlineData("anchor", " hidden=\"1\"")]
    [InlineData("anchor", " hidden=\"true\"")]
    public void Extract_HiddenDrawing_IsExcludedWithItsTextBox(string container, string attributes)
    {
        // Sensitivity: honouring wp:docPr hidden for both wp:inline and wp:anchor.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(WordprocessingFixtures.R("before ") + Drawing(container, attributes, "HIDDEN_BOX") + WordprocessingFixtures.R("after"))));

        Assert.Equal("before after\n", text);
    }

    [Theory]
    [InlineData("inline", "")]
    [InlineData("inline", " hidden=\"0\"")]
    [InlineData("inline", " hidden=\"false\"")]
    [InlineData("anchor", "")]
    [InlineData("anchor", " hidden=\"0\"")]
    [InlineData("anchor", " hidden=\"false\"")]
    public void Extract_VisibleDrawing_ProjectsItsTextBox(string container, string attributes)
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(Drawing(container, attributes, "visible box"))));

        Assert.Equal("visible box\n\n", text);
    }

    [Fact]
    public void Extract_HiddenAnchorWithLayoutElementsBeforeItsDocPr_IsExcluded()
    {
        string anchor =
            "<w:r><w:drawing><wp:anchor><wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH>"
            + "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV><wp:extent cx=\"1\" cy=\"1\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapSquare wrapText=\"bothSides\"/>"
            + "<wp:docPr id=\"2\" name=\"Hidden\" hidden=\"1\"/><wp:cNvGraphicFramePr/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("HIDDEN_ANCHORED") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>";

        Assert.Equal("shown\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(anchor + WordprocessingFixtures.R("shown")))));
    }

    [Fact]
    public void Extract_HiddenDrawingInsideAnAlternateContentChoice_IsExcluded()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<mc:AlternateContent><mc:Choice Requires=\"wps\">" + Drawing("inline", " hidden=\"1\"", "HIDDEN_IN_CHOICE") + "</mc:Choice></mc:AlternateContent>" + WordprocessingFixtures.R("body"))));

        Assert.Equal("body\n", text);
    }

    [Fact]
    public void Extract_DrawingWhoseHiddenDocPrFollowsItsGraphic_IsExcluded()
    {
        // Sensitivity: a drawing's text is held until the drawing ends, so a hidden flag that arrives after the graphic
        // still hides it. Nothing is emitted before the flag is known, and the order is not a reason to refuse the file.
        string reordered =
            "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("REORDERED") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic><wp:docPr id=\"1\" name=\"Box\" hidden=\"1\"/></wp:inline></w:drawing></w:r>";

        Assert.Equal("body\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(reordered + WordprocessingFixtures.R("body")))));
    }

    [Fact]
    public void Extract_DrawingWhoseVisibleDocPrFollowsItsGraphic_ProjectsTheTextBox()
    {
        string reordered =
            "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("late flag") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic><wp:docPr id=\"1\" name=\"Box\"/></wp:inline></w:drawing></w:r>";

        Assert.Equal("late flag\n\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(reordered))));
    }

    [Fact]
    public void Extract_DrawingWithTwoDocPrElementsAheadOfItsGraphic_IsExcludedWhicheverOneHides()
    {
        // Sensitivity: the flags combine; a later visible docPr does not undo an earlier hidden one.
        string Drawing(string first, string second) =>
            $"<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"A\"{first}/><wp:docPr id=\"2\" name=\"B\"{second}/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("AMBIGUOUS") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

        string hiddenFirst = Drawing(" hidden=\"1\"", string.Empty);

        string hiddenSecond = Drawing(string.Empty, " hidden=\"1\"");

        Assert.Equal("body\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(hiddenFirst + hiddenSecond + WordprocessingFixtures.R("body")))));
    }

    [Theory]
    [InlineData(" hidden=\"1\"", "")]
    [InlineData("", " hidden=\"1\"")]
    public void Extract_DrawingWithTwoDocPrElementsWhereEitherHides_IsExcluded(string first, string second)
    {
        string drawing =
            $"<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"A\"{first}/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("AMBIGUOUS") + $"</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic><wp:docPr id=\"2\" name=\"B\"{second}/></wp:inline></w:drawing></w:r>";

        Assert.Equal("body\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(drawing + WordprocessingFixtures.R("body")))));
    }

    [Fact]
    public void Extract_ForeignElementBeforeADrawingsDocPr_IsSkippedAndTheDrawingProjected()
    {
        // wp:extent is a layout element; an element of the same name in another namespace is an extension Word ignores.
        string drawing =
            "<w:r><w:drawing><wp:inline><f:extent xmlns:f=\"urn:foreign\" cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"Box\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("foreign neighbour") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

        Assert.Equal("foreign neighbour\n\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(drawing))));
    }

    [Fact]
    public void Extract_DocPrInAnotherNamespace_IsNotTheDrawingsDocPr()
    {
        // A look-alike hidden flag cannot hide a drawing.
        string drawing =
            "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><f:docPr xmlns:f=\"urn:foreign\" id=\"1\" name=\"Box\" hidden=\"1\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("look-alike flag") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

        Assert.Equal("look-alike flag\n\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(drawing))));
    }

    // ---------------------------------------------------------------- hidden vector shapes

    [Theory]
    [InlineData("position:absolute;visibility:hidden;width:10pt")]
    [InlineData("VISIBILITY: Hidden")]
    [InlineData("visibility :hidden")]
    [InlineData("position:absolute;display:none")]
    [InlineData("Display : None ; width:1pt")]
    public void Extract_HiddenVmlShape_IsExcludedWithItsTextBox(string style)
    {
        // Sensitivity: honouring the VML style properties visibility:hidden and display:none.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<w:r><w:pict>"
                + $"<v:shape id=\"hidden\" style=\"{style}\"><v:textbox><w:txbxContent>" + WordprocessingFixtures.P("HIDDEN_VML") + "</w:txbxContent></v:textbox></v:shape>"
                + "</w:pict></w:r>" + WordprocessingFixtures.R("body"))));

        Assert.Equal("body\n", text);
    }

    [Theory]
    [InlineData("position:absolute;visibility:visible")]
    [InlineData("visibility:inherit")]
    [InlineData("display:block")]
    [InlineData("width:10pt;height:10pt")]
    [InlineData("")]
    public void Extract_VisibleVmlShape_ProjectsItsTextBox(string style)
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<w:r><w:pict>" + $"<v:shape style=\"{style}\"><v:textbox><w:txbxContent>" + WordprocessingFixtures.P("vml text") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>")));

        Assert.Equal("vml text\n\n", text);
    }

    [Fact]
    public void Extract_HiddenVmlGroup_ExcludesEverythingInsideIt()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<w:r><w:pict><v:group style=\"visibility:hidden\"><v:shape><v:textbox><w:txbxContent>" + WordprocessingFixtures.P("HIDDEN_GROUP_CHILD")
                + "</w:txbxContent></v:textbox></v:shape></v:group></w:pict></w:r>" + WordprocessingFixtures.R("body"))));

        Assert.Equal("body\n", text);
    }

    // ---------------------------------------------------------------- hidden shapes inside a drawing group

    // A shape inside a group (wpg) carries its own hidden flag on its non-visual properties; only the outermost
    // shape's flag is on wp:docPr.
    private static string GroupedBox(string nonVisualProperties, string text) =>
        $"<wps:wsp>{nonVisualProperties}<wps:cNvSpPr txBox=\"1\"/><wps:spPr/><wps:txbx><w:txbxContent>"
        + WordprocessingFixtures.P(text)
        + "</w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp>";

    private static string GroupDrawing(string groupContent, string groupProperties = "") =>
        "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"Group\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\">"
        + $"<wpg:wgp>{groupProperties}<wpg:cNvGrpSpPr/><wpg:grpSpPr/>{groupContent}</wpg:wgp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    private static string ShapeProperties(string attributes = "") => $"<wps:cNvPr id=\"2\" name=\"Shape\"{attributes}/>";

    [Theory]
    [InlineData(" hidden=\"1\"")]
    [InlineData(" hidden=\"true\"")]
    public void Extract_HiddenShapeInsideAGroup_IsExcludedWhileItsVisibleSiblingIsProjected(string hidden)
    {
        // Sensitivity: honouring hidden on the non-visual properties of a shape inside a group.
        string group = GroupDrawing(
            GroupedBox(ShapeProperties(hidden), "HIDDEN_CHILD")
            + GroupedBox(ShapeProperties(), "visible child"));

        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(WordprocessingFixtures.R("before ") + group + WordprocessingFixtures.R("after"))));

        Assert.Equal("before \nvisible child\nafter\n", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" hidden=\"0\"")]
    [InlineData(" hidden=\"false\"")]
    public void Extract_VisibleShapesInsideAGroup_AreProjected(string hidden)
    {
        string group = GroupDrawing(GroupedBox(ShapeProperties(hidden), "first child") + GroupedBox(ShapeProperties(hidden), "second child"));

        Assert.Equal("first child\nsecond child\n\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(group))));
    }

    [Fact]
    public void Extract_ShapeWithoutNonVisualProperties_InsideAGroup_IsProjected()
    {
        // The non-visual properties element is optional for a shape inside a group.
        string group = GroupDrawing(GroupedBox(string.Empty, "bare child"));

        Assert.Equal("bare child\n\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(group))));
    }

    [Fact]
    public void Extract_HiddenNestedGroup_ExcludesEveryShapeInsideIt()
    {
        // Sensitivity: honouring hidden on a nested group (wpg:grpSp); a hidden group hides children that say nothing.
        string hiddenInner =
            "<wpg:grpSp><wpg:cNvPr id=\"4\" name=\"Inner\" hidden=\"1\"/><wpg:cNvGrpSpPr/><wpg:grpSpPr/>"
            + GroupedBox(ShapeProperties(), "HIDDEN_IN_HIDDEN_INNER_GROUP")
            + "</wpg:grpSp>";

        string visibleInner =
            "<wpg:grpSp><wpg:cNvPr id=\"5\" name=\"Inner\"/><wpg:cNvGrpSpPr/><wpg:grpSpPr/>"
            + GroupedBox(ShapeProperties(), "visible nested")
            + "</wpg:grpSp>";

        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(GroupDrawing(hiddenInner + visibleInner))));

        Assert.Equal("visible nested\n\n", text);
    }

    [Fact]
    public void Extract_HiddenOutermostGroup_ExcludesEveryShapeInsideIt()
    {
        // Sensitivity: honouring hidden on the non-visual properties of a group (wpg:wgp).
        string hiddenGroup = GroupDrawing(
            GroupedBox(ShapeProperties(), "HIDDEN_IN_HIDDEN_GROUP"),
            "<wpg:cNvPr id=\"1\" name=\"Outer\" hidden=\"true\"/>");

        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(hiddenGroup + WordprocessingFixtures.R("shown"))));

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_HiddenShapeInsideACanvas_IsExcluded()
    {
        string canvas =
            "<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"Canvas\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas\"><wpc:wpc>"
            + GroupedBox(ShapeProperties(" hidden=\"1\""), "HIDDEN_ON_CANVAS")
            + GroupedBox(ShapeProperties(), "visible on canvas")
            + "</wpc:wpc></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

        Assert.Equal("visible on canvas\n\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(canvas))));
    }

    [Fact]
    public void Extract_HiddenShapeInsideAnAlternateContentChoice_IsExcluded()
    {
        string group = GroupDrawing(GroupedBox(ShapeProperties(" hidden=\"1\""), "HIDDEN_IN_CHOICE"));

        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<mc:AlternateContent><mc:Choice Requires=\"wpg\">" + group + "</mc:Choice></mc:AlternateContent>" + WordprocessingFixtures.R("body"))));

        Assert.Equal("body\n", text);
    }

    [Fact]
    public void Extract_NonVisualPropertiesInAnotherNamespace_DoNotHideAShape()
    {
        string foreign = "<f:cNvPr xmlns:f=\"urn:foreign\" id=\"2\" name=\"Shape\" hidden=\"1\"/>";

        string group = GroupDrawing(GroupedBox(foreign, "visible despite a look-alike flag"));

        Assert.Equal("visible despite a look-alike flag\n\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(Wrap(group))));
    }

    [Theory]
    [InlineData("<wps:wsp><wps:txbx><w:txbxContent>{0}</w:txbxContent></wps:txbx><wps:cNvPr id=\"2\" name=\"Late\" hidden=\"1\"/></wps:wsp>")]
    [InlineData("<wps:wsp><wps:cNvSpPr/><wps:cNvPr id=\"2\" name=\"Late\" hidden=\"1\"/><wps:txbx><w:txbxContent>{0}</w:txbxContent></wps:txbx></wps:wsp>")]
    [InlineData("<wps:wsp><wps:cNvPr id=\"2\" name=\"First\"/><wps:cNvPr id=\"3\" name=\"Second\" hidden=\"1\"/><wps:txbx><w:txbxContent>{0}</w:txbxContent></wps:txbx></wps:wsp>")]
    public void Extract_ShapeWhoseNonVisualPropertiesAreNotItsFirstChild_IsFormatMismatch(string shapeTemplate)
    {
        // The hidden flag must be known before the text box is read; a package that places it later is refused.
        string shape = shapeTemplate.Replace("{0}", WordprocessingFixtures.P("REORDERED"), StringComparison.Ordinal);

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx(Wrap(GroupDrawing(shape)))));
    }

    // ---------------------------------------------------------------- property subtrees

    [Fact]
    public void Extract_ParagraphPropertiesSubtree_IsSkipped()
    {
        // Sensitivity: skipping w:pPr (the contrived run inside it would otherwise be emitted).
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:pPr><w:pStyle w:val=\"Normal\"/><w:r><w:t>IN_PPR</w:t></w:r></w:pPr>" + WordprocessingFixtures.R("shown") + "</w:p>"));

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_SectionPropertiesSubtree_IsSkipped()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            WordprocessingFixtures.P("shown") + "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:p><w:r><w:t>IN_SECTPR</w:t></w:r></w:p></w:sectPr>"));

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_RunPropertiesSubtree_IsSkipped()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap("<w:r><w:rPr><w:b/><w:r><w:t>IN_RPR</w:t></w:r></w:rPr><w:t>shown</w:t></w:r>")));

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_RunPropertiesOutsideARun_AreSkipped()
    {
        // w:sdtEndPr (and others) hold a bare w:rPr outside any run.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:sdt><w:sdtPr/><w:sdtEndPr><w:rPr><w:r><w:t>IN_SDTENDPR_RPR</w:t></w:r></w:rPr></w:sdtEndPr><w:sdtContent>" + WordprocessingFixtures.P("shown") + "</w:sdtContent></w:sdt>"));

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_CommentAnchorsAndReferences_ProjectNoText()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            Wrap(
                "<w:commentRangeStart w:id=\"0\"/>" + WordprocessingFixtures.R("commented")
                + "<w:commentRangeEnd w:id=\"0\"/><w:r><w:rPr><w:rStyle w:val=\"CommentReference\"/></w:rPr><w:commentReference w:id=\"0\"/></w:r>")));

        Assert.Equal("commented\n", text);
    }
}
