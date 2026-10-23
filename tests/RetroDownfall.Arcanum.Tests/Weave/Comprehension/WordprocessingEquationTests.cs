using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Office Math (<c>m:oMath</c>, <c>m:oMathPara</c>): the <c>m:t</c> text of an equation is projected as linear text, a
/// display equation is its own paragraph, and the ways an equation can hide text (run properties, tracked deletions, a
/// phantom, a hidden radical degree or limit) are honoured like every other hidden-content rule.
/// </summary>
public sealed class WordprocessingEquationTests
{
    private const string Revision = "w:id=\"5\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"";

    private static string MathRun(string text, string? wordProperties = null) =>
        "<m:r>" + (wordProperties is null ? string.Empty : $"<w:rPr>{wordProperties}</w:rPr>") + $"<m:t>{text}</m:t></m:r>";

    private static string Math(string content) => $"<m:oMath>{content}</m:oMath>";

    private static string Display(params string[] equations) => "<m:oMathPara><m:oMathParaPr><m:jc m:val=\"center\"/></m:oMathParaPr>" + string.Concat(equations) + "</m:oMathPara>";

    private static string SquaredTerm(string @base) =>
        $"<m:sSup><m:e>{MathRun(@base)}</m:e><m:sup>{MathRun("2")}</m:sup></m:sSup>";

    private static string Extract(string body, string? styles = null) =>
        WordprocessingFixtures.Extract(WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(body),
            stylesXml: styles is null ? null : WordprocessingFixtures.Styles(styles)));

    // ---------------------------------------------------------------- projection

    [Fact]
    public void Extract_InlineEquation_ProjectsItsTextInPlace()
    {
        string body = "<w:p>" + WordprocessingFixtures.R("Energy: ") + Math(MathRun("E=mc2")) + WordprocessingFixtures.R(" holds.") + "</w:p>";

        Assert.Equal("Energy: E=mc2 holds.\n", Extract(body));
    }

    [Fact]
    public void Extract_EquationWithPandocShapedRunsAndASuperscript_ProjectsLinearText()
    {
        string equation = Math(
            MathRun("E") + "<m:r><m:rPr><m:sty m:val=\"p\"/></m:rPr><m:t>=</m:t></m:r>" + MathRun("m")
            + "<m:sSup><m:e>" + MathRun("c") + "</m:e><m:sup>" + MathRun("2") + "</m:sup></m:sSup>");

        string body = "<w:p>" + WordprocessingFixtures.R("Energy equation ") + equation + WordprocessingFixtures.R(" holds.") + "</w:p>";

        Assert.Equal("Energy equation E=mc2 holds.\n", Extract(body));
    }

    [Fact]
    public void Extract_DisplayEquation_IsItsOwnParagraph()
    {
        string equation = Math(SquaredTerm("a") + MathRun("+") + SquaredTerm("b") + MathRun("=") + SquaredTerm("c"));

        string body = WordprocessingFixtures.P("Display math:") + "<w:p>" + Display(equation) + "</w:p>" + WordprocessingFixtures.P("After math.");

        Assert.Equal("Display math:\na2+b2=c2\nAfter math.\n", Extract(body));
    }

    [Fact]
    public void Extract_DisplayEquationInTheMiddleOfAParagraph_IsSetApartFromTheTextAroundIt()
    {
        string body = "<w:p>" + WordprocessingFixtures.R("before ") + Display(Math(MathRun("x"))) + WordprocessingFixtures.R(" after") + "</w:p>";

        Assert.Equal("before \nx\n after\n", Extract(body));
    }

    [Fact]
    public void Extract_SeveralEquationsInOneDisplay_EachStartOnTheirOwnLine()
    {
        string body = "<w:p>" + Display(Math(MathRun("a=b")), Math(MathRun("c=d"))) + "</w:p>";

        Assert.Equal("a=b\nc=d\n", Extract(body));
    }

    [Fact]
    public void Extract_TwoInlineEquationsSideBySide_AreNotSeparatedByTheExtractor()
    {
        string body = "<w:p>" + Math(MathRun("a")) + WordprocessingFixtures.R(" and ") + Math(MathRun("b")) + "</w:p>";

        Assert.Equal("a and b\n", Extract(body));
    }

    [Fact]
    public void Extract_EquationAtBlockLevel_IsProjected()
    {
        Assert.Equal("x\nz\n", Extract(Display(Math(MathRun("x"))) + WordprocessingFixtures.P("z")));

        Assert.Equal("y", Extract(Math(MathRun("y"))));
    }

    [Fact]
    public void Extract_EquationStructures_AreFlattenedToTheirText()
    {
        string fraction = "<m:f><m:fPr><m:type m:val=\"bar\"/></m:fPr><m:num>" + MathRun("a") + "</m:num><m:den>" + MathRun("b") + "</m:den></m:f>";

        string delimiter = "<m:d><m:dPr><m:begChr m:val=\"PROPERTY_VALUE\"/><m:endChr m:val=\"PROPERTY_VALUE\"/></m:dPr><m:e>" + MathRun("c") + "</m:e></m:d>";

        string radical = "<m:rad><m:radPr><m:degHide m:val=\"0\"/></m:radPr><m:deg>" + MathRun("3") + "</m:deg><m:e>" + MathRun("d") + "</m:e></m:rad>";

        string matrix = "<m:m><m:mPr><m:mcs><m:mc><m:mcPr><m:count m:val=\"2\"/></m:mcPr></m:mc></m:mcs></m:mPr><m:mr><m:e>" + MathRun("e") + "</m:e><m:e>" + MathRun("f") + "</m:e></m:mr></m:m>";

        string function = "<m:func><m:funcPr/><m:fName>" + MathRun("sin") + "</m:fName><m:e>" + MathRun("g") + "</m:e></m:func>";

        Assert.Equal("abc3defsing\n", Extract("<w:p>" + Math(fraction + delimiter + radical + matrix + function) + "</w:p>"));
    }

    [Fact]
    public void Extract_MathPropertyElements_NeverReachTheOutput()
    {
        string properties =
            "<m:ctrlPr><m:t>IN_CTRLPR</m:t></m:ctrlPr><m:rPr><m:t>IN_RPR</m:t><m:nor/></m:rPr>"
            + "<m:oMathParaPr><m:t>IN_PARA_PR</m:t></m:oMathParaPr><m:sSupPr><m:ctrlPr><m:t>IN_SSUPPR</m:t></m:ctrlPr></m:sSupPr>";

        Assert.Equal("shown\n", Extract("<w:p>" + Display("<m:oMath>" + properties + MathRun("shown") + "</m:oMath>") + "</w:p>"));
    }

    [Fact]
    public void Extract_MathRunHoldingAWordTextElement_ProjectsIt()
    {
        string body = "<w:p><m:oMath><m:r><m:rPr><m:nor/></m:rPr><w:t>plain words</w:t></m:r></m:oMath></w:p>";

        Assert.Equal("plain words\n", Extract(body));
    }

    [Fact]
    public void Extract_WhitespaceInAMathText_IsKept()
    {
        string body = "<w:p><m:oMath><m:r><m:t xml:space=\"preserve\">a </m:t></m:r><m:r><m:t>b</m:t></m:r></m:oMath></w:p>";

        Assert.Equal("a b\n", Extract(body));
    }

    [Fact]
    public void Extract_EquationInTheStrictMathNamespace_IsProjected()
    {
        string body = $"<w:p><sm:oMath xmlns:sm=\"{WordprocessingFixtures.StrictMathNamespace}\"><sm:r><sm:t>strict</sm:t></sm:r></sm:oMath></w:p>";

        Assert.Equal("strict\n", Extract(body));
    }

    [Fact]
    public void Extract_EquationInsideAlternateContent_IsProjectedFromTheSelectedBranch()
    {
        string body = "<w:p><mc:AlternateContent><mc:Choice Requires=\"w14\">" + Math(MathRun("choice")) + "</mc:Choice><mc:Fallback>" + Math(MathRun("FALLBACK")) + "</mc:Fallback></mc:AlternateContent></w:p>";

        Assert.Equal("choice\n", Extract(body));
    }

    // ---------------------------------------------------------------- where an equation sits

    [Fact]
    public void Extract_EquationsInATableCell_JoinTheCellWithSingleSpaces()
    {
        string cell = "<w:tc><w:p>" + WordprocessingFixtures.R("a") + Math(MathRun("x")) + WordprocessingFixtures.R("b") + "</w:p><w:p>" + Display(Math(MathRun("y"))) + "</w:p></w:tc>";

        Assert.Equal("axb y\n", Extract("<w:tbl><w:tblPr/><w:tr>" + cell + "</w:tr></w:tbl>"));
    }

    [Fact]
    public void Extract_DisplayEquationInTheMiddleOfACell_IsSetApartBySpaces()
    {
        string cell = "<w:tc><w:p>" + WordprocessingFixtures.R("a") + Display(Math(MathRun("x"))) + WordprocessingFixtures.R("b") + "</w:p></w:tc>";

        Assert.Equal("a x b\n", Extract("<w:tbl><w:tblPr/><w:tr>" + cell + "</w:tr></w:tbl>"));
    }

    [Fact]
    public void Extract_EquationInATextBox_IsProjected()
    {
        string box = "<w:r><w:pict><v:shape><v:textbox><w:txbxContent><w:p>" + Math(MathRun("boxed")) + "</w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

        Assert.Equal("boxed\n\n", Extract("<w:p>" + box + "</w:p>"));
    }

    [Fact]
    public void Extract_EquationInsideHyperlinksContentControlsAndInsertions_IsProjected()
    {
        string body =
            "<w:p><w:hyperlink w:anchor=\"a\">" + Math(MathRun("link ")) + "</w:hyperlink>"
            + "<w:sdt><w:sdtPr/><w:sdtContent>" + Math(MathRun("control ")) + "</w:sdtContent></w:sdt>"
            + $"<w:ins {Revision}>" + Math(MathRun("inserted")) + "</w:ins></w:p>";

        Assert.Equal("link control inserted\n", Extract(body));
    }

    // ---------------------------------------------------------------- hidden content

    [Fact]
    public void Extract_MathRunHiddenByDirectFormatting_IsExcludedAndOneSwitchedOffIsKept()
    {
        string equation = Math(MathRun("HIDDEN", "<w:vanish/>") + MathRun("shown") + MathRun("also shown", "<w:vanish w:val=\"0\"/>"));

        Assert.Equal("shownalso shown\n", Extract("<w:p>" + equation + "</w:p>"));
    }

    [Fact]
    public void Extract_MathRunHiddenByACharacterStyleOrTheParagraphOrTheTable_IsExcluded()
    {
        string styles =
            "<w:style w:type=\"character\" w:styleId=\"SecretRun\"><w:rPr><w:vanish/></w:rPr></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"SecretPara\"><w:rPr><w:vanish/></w:rPr></w:style>"
            + "<w:style w:type=\"table\" w:styleId=\"SecretTable\"><w:rPr><w:vanish/></w:rPr></w:style>";

        string byCharacter = "<w:p>" + Math(MathRun("HIDDEN_RUN", "<w:rStyle w:val=\"SecretRun\"/>") + MathRun("shown")) + "</w:p>";

        string byParagraph = "<w:p><w:pPr><w:pStyle w:val=\"SecretPara\"/></w:pPr>" + Math(MathRun("HIDDEN_PARA")) + "</w:p>";

        string byTable = "<w:tbl><w:tblPr><w:tblStyle w:val=\"SecretTable\"/></w:tblPr><w:tr><w:tc><w:p>" + Math(MathRun("HIDDEN_TABLE")) + "</w:p></w:tc></w:tr></w:tbl>";

        Assert.Equal("shown\n\n\n", Extract(byCharacter + byParagraph + byTable, styles));
    }

    [Fact]
    public void Extract_EquationInAVanishedWordRun_IsExcluded()
    {
        string body = "<w:p><w:r><w:rPr><w:vanish/></w:rPr>" + Math(MathRun("HIDDEN")) + "</w:r>" + WordprocessingFixtures.R("shown") + "</w:p>";

        Assert.Equal("shown\n", Extract(body));
    }

    [Fact]
    public void Extract_DeletedMathIsExcludedAndInsertedMathIsKept()
    {
        string equation = Math(
            $"<w:del {Revision}>" + MathRun("DELETED") + "</w:del>"
            + $"<w:ins {Revision}>" + MathRun("kept") + "</w:ins>"
            + $"<w:moveFrom {Revision}>" + MathRun("MOVED_AWAY") + "</w:moveFrom>");

        Assert.Equal("kept\n", Extract("<w:p>" + equation + "</w:p>"));

        Assert.Equal("kept\n", Extract($"<w:p><w:del {Revision}>" + Math(MathRun("DELETED_EQUATION")) + "</w:del>" + Math(MathRun("kept")) + "</w:p>"));
    }

    [Fact]
    public void Extract_EquationInsideFieldCode_IsNotProjected()
    {
        string begin = "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>";

        string separate = "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>";

        string end = "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

        string body = "<w:p>" + begin + Math(MathRun("FIELD_CODE")) + separate + Math(MathRun("result")) + end + "</w:p>";

        Assert.Equal("result\n", Extract(body));
    }

    [Fact]
    public void Extract_MathTextOutsideAnEquation_IsNotProjected()
    {
        string body = "<w:p><m:r><m:t>STRAY_RUN</m:t></m:r><m:t>STRAY_TEXT</m:t><m:f><m:num><m:r><m:t>STRAY_FRACTION</m:t></m:r></m:num></m:f></w:p>";

        Assert.Equal("\n", Extract(body));
    }

    [Fact]
    public void Extract_PhantomThatIsNotShown_IsExcluded()
    {
        string hidden = "<m:phant><m:phantPr><m:show m:val=\"0\"/></m:phantPr><m:e>" + MathRun("HIDDEN_PHANTOM") + "</m:e></m:phant>";

        string zero = "<m:phant><m:phantPr><m:show m:val=\"off\"/><m:zeroWid m:val=\"1\"/></m:phantPr><m:e>" + MathRun("HIDDEN_OFF") + "</m:e></m:phant>";

        Assert.Equal("ab\n", Extract("<w:p>" + Math(MathRun("a") + hidden + zero + MathRun("b")) + "</w:p>"));
    }

    [Theory]
    [InlineData("<m:phantPr><m:show/></m:phantPr>")]
    [InlineData("<m:phantPr><m:show m:val=\"1\"/></m:phantPr>")]
    [InlineData("<m:phantPr><m:zeroWid m:val=\"1\"/></m:phantPr>")]
    [InlineData("")]
    public void Extract_PhantomThatIsShown_IsProjected(string properties)
    {
        string phantom = "<m:phant>" + properties + "<m:e>" + MathRun("seen") + "</m:e></m:phant>";

        Assert.Equal("seen\n", Extract("<w:p>" + Math(phantom) + "</w:p>"));
    }

    [Theory]
    [InlineData("<m:degHide m:val=\"1\"/>")]
    [InlineData("<m:degHide/>")]
    [InlineData("<m:degHide m:val=\"true\"/>")]
    public void Extract_RadicalWhoseDegreeIsHidden_ProjectsOnlyTheRadicand(string hide)
    {
        string radical = "<m:rad><m:radPr>" + hide + "</m:radPr><m:deg>" + MathRun("HIDDEN_DEGREE") + "</m:deg><m:e>" + MathRun("x") + "</m:e></m:rad>";

        Assert.Equal("x\n", Extract("<w:p>" + Math(radical) + "</w:p>"));
    }

    [Fact]
    public void Extract_NaryOperatorWhoseLimitsAreHidden_ProjectsOnlyTheVisibleParts()
    {
        string both = "<m:nary><m:naryPr><m:subHide m:val=\"1\"/><m:supHide m:val=\"1\"/></m:naryPr><m:sub>" + MathRun("HIDDEN_SUB") + "</m:sub><m:sup>" + MathRun("HIDDEN_SUP") + "</m:sup><m:e>" + MathRun("body") + "</m:e></m:nary>";

        string lowerOnly = "<m:nary><m:naryPr><m:subHide m:val=\"1\"/></m:naryPr><m:sub>" + MathRun("HIDDEN_LOWER") + "</m:sub><m:sup>" + MathRun("upper") + "</m:sup><m:e>" + MathRun("body") + "</m:e></m:nary>";

        string upperOnly = "<m:nary><m:naryPr><m:supHide m:val=\"1\"/></m:naryPr><m:sub>" + MathRun("lower") + "</m:sub><m:sup>" + MathRun("HIDDEN_UPPER") + "</m:sup><m:e>" + MathRun("body") + "</m:e></m:nary>";

        string shown = "<m:nary><m:naryPr><m:subHide m:val=\"0\"/><m:supHide m:val=\"0\"/></m:naryPr><m:sub>" + MathRun("s") + "</m:sub><m:sup>" + MathRun("p") + "</m:sup><m:e>" + MathRun("e") + "</m:e></m:nary>";

        Assert.Equal("body" + "upperbody" + "lowerbody" + "spe\n", Extract("<w:p>" + Math(both + lowerOnly + upperOnly + shown) + "</w:p>"));
    }

    [Fact]
    public void Extract_HidingPropertiesAfterTheContentTheyGovern_IsFormatMismatch()
    {
        string late = "<m:rad><m:deg>" + MathRun("EMITTED_EARLY") + "</m:deg><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:e>" + MathRun("x") + "</m:e></m:rad>";

        string twice = "<m:phant><m:phantPr/><m:phantPr><m:show m:val=\"0\"/></m:phantPr><m:e>" + MathRun("x") + "</m:e></m:phant>";

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx("<w:p>" + Math(late) + "</w:p>")));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx("<w:p>" + Math(twice) + "</w:p>")));
    }

    [Fact]
    public void Extract_HidingPropertiesOfAnotherElement_DoNotHideThisOne()
    {
        // degHide belongs to a radical; on a nary operator, or on a plain fraction, it hides nothing.
        string nary = "<m:nary><m:naryPr><m:degHide m:val=\"1\"/></m:naryPr><m:sub>" + MathRun("s") + "</m:sub><m:e>" + MathRun("e") + "</m:e></m:nary>";

        Assert.Equal("se\n", Extract("<w:p>" + Math(nary) + "</w:p>"));
    }

    // ---------------------------------------------------------------- output ceiling

    [Fact]
    public void Extract_OutputCeilingReachedInAnEquation_ReadsNothingFurther()
    {
        string main = $"<w:document {WordprocessingFixtures.Namespaces()}><w:body><w:p><m:oMath><m:r><m:t>aaaaaaaa<<";

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(WordprocessingFixtures.Package(main), maximumCharacters: 4);

        Assert.Equal("aaaa", result.Text);

        Assert.True(result.WasTruncated);
    }
}
