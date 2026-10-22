using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Markup compatibility (<c>mc:AlternateContent</c>) is resolved wherever it can occur, before the selected branch is
/// interpreted: the first <c>mc:Choice</c> whose required prefixes all name namespaces the extractor understands, otherwise
/// the <c>mc:Fallback</c>, never both and never neither when a fallback exists. Hiding, deletions and field-code
/// suppression hold inside the selected branch exactly as they do outside it.
/// </summary>
public sealed class WordprocessingMarkupCompatibilityTests
{
    private const string Vanish = "<w:vanish/>";

    private const string Understood = "w14";

    /// <summary>A prefix declared by the fixture whose namespace the extractor does not understand (the emoji extension).</summary>
    private const string NotUnderstood = "w16se";

    private static string Alternate(string requires, string choice, string? fallback = null) =>
        $"<mc:AlternateContent><mc:Choice Requires=\"{requires}\">{choice}</mc:Choice>"
        + (fallback is null ? string.Empty : $"<mc:Fallback>{fallback}</mc:Fallback>")
        + "</mc:AlternateContent>";

    private static string Extract(string body, string? styles = null) =>
        WordprocessingFixtures.Extract(WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(body),
            stylesXml: styles is null ? null : WordprocessingFixtures.Styles(styles)));

    private static string Run(string content) => $"<w:r>{content}</w:r>";

    private static string HiddenCharacterStyle(string id) =>
        $"<w:style w:type=\"character\" w:styleId=\"{id}\"><w:rPr>{Alternate(Understood, Vanish, Vanish)}</w:rPr></w:style>";

    // ---------------------------------------------------------------- selection

    [Fact]
    public void Extract_TheFirstChoiceThatCanBeTaken_IsProjectedAndNeitherLaterChoicesNorTheFallback()
    {
        string choices =
            $"<mc:Choice Requires=\"{NotUnderstood}\">" + WordprocessingFixtures.R("UNTAKEN") + "</mc:Choice>"
            + "<mc:Choice Requires=\"w14\">" + WordprocessingFixtures.R("first taken") + "</mc:Choice>"
            + "<mc:Choice Requires=\"w15\">" + WordprocessingFixtures.R("LATER_CHOICE") + "</mc:Choice>"
            + "<mc:Fallback>" + WordprocessingFixtures.R("FALLBACK") + "</mc:Fallback>";

        string body = "<w:p><mc:AlternateContent>" + choices + "</mc:AlternateContent>" + WordprocessingFixtures.R(" end") + "</w:p>";

        Assert.Equal("first taken end\n", Extract(body));
    }

    [Fact]
    public void Extract_ChoiceWithNothingInIt_IsStillTheSelectedBranch()
    {
        // Selected, not "has content": the fallback is not shown beside an empty choice.
        string body = "<w:p>" + Alternate(Understood, string.Empty, WordprocessingFixtures.R("FALLBACK")) + WordprocessingFixtures.R("body") + "</w:p>";

        Assert.Equal("body\n", Extract(body));
    }

    [Fact]
    public void Extract_EmptyAlternateContentAndEmptyBranches_ProjectNothing()
    {
        string body = "<w:p><mc:AlternateContent/><mc:AlternateContent><mc:Choice Requires=\"w14\"/><mc:Fallback/></mc:AlternateContent>" + WordprocessingFixtures.R("body") + "</w:p>";

        Assert.Equal("body\n", Extract(body));
    }

    [Fact]
    public void Extract_AlternateContentNestedInTheSelectedBranch_IsResolvedToo()
    {
        string inner = Alternate(NotUnderstood, WordprocessingFixtures.R("INNER_UNTAKEN"), WordprocessingFixtures.R("inner fallback"));

        string outer = Alternate(Understood, WordprocessingFixtures.R("outer ") + inner, WordprocessingFixtures.R("OUTER_FALLBACK"));

        Assert.Equal("outer inner fallback\n", Extract("<w:p>" + outer + "</w:p>"));
    }

    [Fact]
    public void Extract_AlternateContentNestedInTheSelectedFallback_IsResolvedToo()
    {
        string inner = Alternate(Understood, WordprocessingFixtures.R("inner choice"), WordprocessingFixtures.R("INNER_FALLBACK"));

        string outer = Alternate(NotUnderstood, WordprocessingFixtures.R("OUTER_UNTAKEN"), inner);

        Assert.Equal("inner choice\n", Extract("<w:p>" + outer + "</w:p>"));
    }

    [Fact]
    public void Extract_ChoiceFollowingTheFallback_IsFormatMismatch()
    {
        // The fallback comes last; a choice after it leaves what a reader sees ambiguous, so the part is refused.
        string body = "<w:p><mc:AlternateContent><mc:Fallback>" + WordprocessingFixtures.R("fallback") + "</mc:Fallback>"
            + "<mc:Choice Requires=\"w14\">" + WordprocessingFixtures.R("late choice") + "</mc:Choice></mc:AlternateContent></w:p>";

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx(body)));
    }

    [Fact]
    public void Extract_TwoFallbacks_IsFormatMismatch()
    {
        string body = "<w:p><mc:AlternateContent><mc:Fallback>" + WordprocessingFixtures.R("one") + "</mc:Fallback><mc:Fallback>" + WordprocessingFixtures.R("two") + "</mc:Fallback></mc:AlternateContent></w:p>";

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx(body)));
    }

    [Fact]
    public void Extract_ElementsOtherThanChoiceAndFallbackInsideAlternateContent_AreSkipped()
    {
        string body = "<w:p><mc:AlternateContent>" + WordprocessingFixtures.R("STRAY_RUN") + "<mc:Choice Requires=\"w14\">" + WordprocessingFixtures.R("choice") + "</mc:Choice></mc:AlternateContent></w:p>";

        Assert.Equal("choice\n", Extract(body));
    }

    [Fact]
    public void Extract_AnElementUnselectedBranchHoldsNeverAffectsFieldState()
    {
        // A begin in the branch that is not selected must not open a field code region.
        string begin = Run("<w:fldChar w:fldCharType=\"begin\"/>");

        string body = "<w:p>" + Alternate(Understood, WordprocessingFixtures.R("shown"), begin) + WordprocessingFixtures.R(" still shown") + "</w:p>";

        Assert.Equal("shown still shown\n", Extract(body));
    }

    // ---------------------------------------------------------------- inside run properties

    [Fact]
    public void Extract_VanishInsideAlternateContentInsideRunProperties_HidesTheRun()
    {
        string body = "<w:p><w:r><w:rPr>" + Alternate(Understood, Vanish, Vanish) + "</w:rPr><w:t>AC_VANISH_LEAK</w:t></w:r>" + WordprocessingFixtures.R("shown") + "</w:p>";

        Assert.Equal("shown\n", Extract(body));
    }

    [Fact]
    public void Extract_VanishInTheFallbackOfRunProperties_HidesWhenTheChoiceCannotBeTaken()
    {
        string body = "<w:p><w:r><w:rPr>" + Alternate(NotUnderstood, "<w:b/>", Vanish) + "</w:rPr><w:t>HIDDEN</w:t></w:r>" + WordprocessingFixtures.R("shown") + "</w:p>";

        Assert.Equal("shown\n", Extract(body));
    }

    [Fact]
    public void Extract_VanishInABranchThatIsNotSelected_DoesNotHide()
    {
        string notTaken = "<w:p><w:r><w:rPr>" + Alternate(NotUnderstood, Vanish, "<w:b/>") + "</w:rPr><w:t>shown</w:t></w:r></w:p>";

        string notFallback = "<w:p><w:r><w:rPr>" + Alternate(Understood, "<w:b/>", Vanish) + "</w:rPr><w:t>also shown</w:t></w:r></w:p>";

        Assert.Equal("shown\nalso shown\n", Extract(notTaken + notFallback));
    }

    [Fact]
    public void Extract_VanishSwitchedOffInsideAlternateContent_DoesNotHide()
    {
        string body = "<w:p><w:r><w:rPr>" + Alternate(Understood, "<w:vanish w:val=\"0\"/>", "<w:vanish w:val=\"0\"/>") + "</w:rPr><w:t>shown</w:t></w:r></w:p>";

        Assert.Equal("shown\n", Extract(body));
    }

    [Fact]
    public void Extract_RunStyleNamedInsideAlternateContent_AppliesIt()
    {
        string styles = HiddenCharacterStyle("Secret") + "<w:style w:type=\"character\" w:styleId=\"Plain\"/>";

        string body = "<w:p><w:r><w:rPr>" + Alternate(Understood, "<w:rStyle w:val=\"Secret\"/>", "<w:rStyle w:val=\"Secret\"/>") + "</w:rPr><w:t>HIDDEN</w:t></w:r>"
            + "<w:r><w:rPr>" + Alternate(Understood, "<w:rStyle w:val=\"Plain\"/>", "<w:rStyle w:val=\"Secret\"/>") + "</w:rPr><w:t>shown</w:t></w:r></w:p>";

        Assert.Equal("shown\n", Extract(body, styles));
    }

    [Fact]
    public void Extract_StyleVanishInsideAlternateContent_HidesCharacterParagraphAndTableStyles()
    {
        string rPr = "<w:rPr>" + Alternate(Understood, Vanish, Vanish) + "</w:rPr>";

        string styles =
            $"<w:style w:type=\"character\" w:styleId=\"SecretRun\">{rPr}</w:style>"
            + $"<w:style w:type=\"paragraph\" w:styleId=\"SecretPara\">{rPr}</w:style>"
            + $"<w:style w:type=\"table\" w:styleId=\"SecretTable\">{rPr}</w:style>";

        string body =
            "<w:p><w:r><w:rPr><w:rStyle w:val=\"SecretRun\"/></w:rPr><w:t>HIDDEN_RUN</w:t></w:r>" + WordprocessingFixtures.R("shown") + "</w:p>"
            + "<w:p><w:pPr><w:pStyle w:val=\"SecretPara\"/></w:pPr>" + WordprocessingFixtures.R("HIDDEN_PARA") + "</w:p>"
            + "<w:tbl><w:tblPr><w:tblStyle w:val=\"SecretTable\"/></w:tblPr><w:tr><w:tc>" + WordprocessingFixtures.P("HIDDEN_TABLE") + "</w:tc></w:tr></w:tbl>";

        Assert.Equal("shown\n\n\n", Extract(body, styles));
    }

    [Fact]
    public void Extract_StyleRunPropertiesWrappedInAlternateContent_AreRead()
    {
        string wrapped = Alternate(Understood, "<w:rPr>" + Vanish + "</w:rPr>", "<w:rPr>" + Vanish + "</w:rPr>");

        string styles = $"<w:style w:type=\"character\" w:styleId=\"Secret\">{wrapped}</w:style>";

        string body = "<w:p><w:r><w:rPr><w:rStyle w:val=\"Secret\"/></w:rPr><w:t>HIDDEN</w:t></w:r>" + WordprocessingFixtures.R("shown") + "</w:p>";

        Assert.Equal("shown\n", Extract(body, styles));
    }

    [Fact]
    public void Extract_DocumentDefaultsVanishInsideAlternateContent_HidesEverything()
    {
        string styles = "<w:docDefaults><w:rPrDefault><w:rPr>" + Alternate(Understood, Vanish, Vanish) + "</w:rPr></w:rPrDefault></w:docDefaults>";

        Assert.Equal("\n", Extract(WordprocessingFixtures.P("HIDDEN"), styles));
    }

    [Fact]
    public void Extract_RunPropertiesWrappedInAlternateContent_Hide()
    {
        string body = "<w:p><w:r>" + Alternate(Understood, "<w:rPr>" + Vanish + "</w:rPr>", "<w:rPr>" + Vanish + "</w:rPr>") + "<w:t>AC_RPR_LEAK</w:t></w:r>" + WordprocessingFixtures.R("shown") + "</w:p>";

        Assert.Equal("shown\n", Extract(body));
    }

    [Fact]
    public void Extract_RunPropertiesInTheFallback_HideWhenTheChoiceCannotBeTaken()
    {
        string body = "<w:p><w:r>" + Alternate(NotUnderstood, "<w:rPr><w:b/></w:rPr>", "<w:rPr>" + Vanish + "</w:rPr>") + "<w:t>HIDDEN</w:t></w:r>" + WordprocessingFixtures.R("shown") + "</w:p>";

        Assert.Equal("shown\n", Extract(body));
    }

    [Fact]
    public void Extract_RunPropertiesWrappedInAlternateContentAfterContent_IsFormatMismatch()
    {
        // The properties must still come first among a run's children once the alternate content is resolved.
        string body = "<w:p><w:r><w:t>EMITTED_EARLY</w:t>" + Alternate(Understood, "<w:rPr>" + Vanish + "</w:rPr>") + "</w:r></w:p>";

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx(body)));
    }

    // ---------------------------------------------------------------- inside paragraph and table properties

    [Fact]
    public void Extract_ParagraphStyleInsideAlternateContentInsideParagraphProperties_HidesTheParagraph()
    {
        string styles = "<w:style w:type=\"paragraph\" w:styleId=\"Secret\"><w:rPr>" + Vanish + "</w:rPr></w:style>";

        string body = "<w:p><w:pPr>" + Alternate(Understood, "<w:pStyle w:val=\"Secret\"/>", "<w:pStyle w:val=\"Secret\"/>") + "</w:pPr>" + WordprocessingFixtures.R("HIDDEN") + "</w:p>" + WordprocessingFixtures.P("shown");

        Assert.Equal("\nshown\n", Extract(body, styles));
    }

    [Fact]
    public void Extract_ParagraphPropertiesWrappedInAlternateContent_HideTheParagraph()
    {
        string styles = "<w:style w:type=\"paragraph\" w:styleId=\"Secret\"><w:rPr>" + Vanish + "</w:rPr></w:style>";

        string body = "<w:p>" + Alternate(Understood, "<w:pPr><w:pStyle w:val=\"Secret\"/></w:pPr>", "<w:pPr><w:pStyle w:val=\"Secret\"/></w:pPr>") + WordprocessingFixtures.R("HIDDEN") + "</w:p>" + WordprocessingFixtures.P("shown");

        Assert.Equal("\nshown\n", Extract(body, styles));
    }

    [Fact]
    public void Extract_TableStyleInsideAlternateContent_HidesTheTable()
    {
        string styles = "<w:style w:type=\"table\" w:styleId=\"Secret\"><w:rPr>" + Vanish + "</w:rPr></w:style>";

        string inside = "<w:tbl><w:tblPr>" + Alternate(Understood, "<w:tblStyle w:val=\"Secret\"/>", "<w:tblStyle w:val=\"Secret\"/>") + "</w:tblPr><w:tr><w:tc>" + WordprocessingFixtures.P("HIDDEN_A") + "</w:tc></w:tr></w:tbl>";

        string wrapped = "<w:tbl>" + Alternate(Understood, "<w:tblPr><w:tblStyle w:val=\"Secret\"/></w:tblPr>", "<w:tblPr><w:tblStyle w:val=\"Secret\"/></w:tblPr>") + "<w:tr><w:tc>" + WordprocessingFixtures.P("HIDDEN_B") + "</w:tc></w:tr></w:tbl>";

        Assert.Equal("\n\nshown\n", Extract(inside + wrapped + WordprocessingFixtures.P("shown"), styles));
    }

    // ---------------------------------------------------------------- run content delivered through a branch

    [Fact]
    public void Extract_TextInsideAlternateContentInsideARun_IsProjected()
    {
        string body = "<w:p><w:r>" + Alternate(Understood, "<w:t>choice text</w:t>", "<w:t>FALLBACK</w:t>") + "</w:r></w:p>";

        Assert.Equal("choice text\n", Extract(body));
    }

    [Fact]
    public void Extract_TabsAndBreaksInsideAlternateContentInsideARun_KeepTheirRunMeaning()
    {
        string body = "<w:p><w:r><w:t>a</w:t>" + Alternate(Understood, "<w:tab/><w:t>b</w:t><w:br/><w:noBreakHyphen/>", "<w:t>FALLBACK</w:t>") + "</w:r></w:p>";

        Assert.Equal("a\tb\n-\n", Extract(body));
    }

    [Fact]
    public void Extract_Word2016Emoji_IsProjectedFromTheFallbackBecauseTheSymbolExtensionIsNotUnderstood()
    {
        string symbol = "<w16se:symEx w16se:font=\"Segoe UI Emoji\" w16se:char=\"1F600\"/>";

        string body = "<w:p>" + WordprocessingFixtures.R("Happy ") + "<w:r>" + Alternate("w16se", symbol, "<w:t>\U0001F600</w:t>") + "</w:r>" + WordprocessingFixtures.R(" done.") + "</w:p>";

        Assert.Equal("Happy \U0001F600 done.\n", Extract(body));
    }

    [Fact]
    public void Extract_HiddenRunInsideTheSelectedBranch_IsHidden()
    {
        string hidden = "<w:r><w:rPr>" + Vanish + "</w:rPr><w:t>HIDDEN</w:t></w:r>";

        string body = "<w:p>" + Alternate(Understood, hidden + WordprocessingFixtures.R("shown"), hidden) + "</w:p>";

        Assert.Equal("shown\n", Extract(body));
    }

    [Fact]
    public void Extract_HiddenRunInsideTheSelectedFallback_IsHidden()
    {
        string hidden = "<w:r><w:rPr>" + Vanish + "</w:rPr><w:t>HIDDEN</w:t></w:r>";

        string body = "<w:p>" + Alternate(NotUnderstood, WordprocessingFixtures.R("UNTAKEN"), hidden + WordprocessingFixtures.R("shown")) + "</w:p>";

        Assert.Equal("shown\n", Extract(body));
    }

    [Fact]
    public void Extract_DeletionInsideTheSelectedBranch_IsExcluded()
    {
        string deleted = "<w:del w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\">" + WordprocessingFixtures.R("DELETED") + "</w:del>";

        string body = "<w:p>" + Alternate(Understood, deleted + WordprocessingFixtures.R("kept"), deleted) + Alternate(NotUnderstood, string.Empty, deleted + WordprocessingFixtures.R(" kept too")) + "</w:p>";

        Assert.Equal("kept kept too\n", Extract(body));
    }

    [Fact]
    public void Extract_FieldCodeInsideTheSelectedBranch_IsExcluded()
    {
        string begin = Run("<w:fldChar w:fldCharType=\"begin\"/>");

        string separate = Run("<w:fldChar w:fldCharType=\"separate\"/>");

        string end = Run("<w:fldChar w:fldCharType=\"end\"/>");

        string branch = begin + WordprocessingFixtures.R("FIELD_CODE") + separate + WordprocessingFixtures.R("result") + end;

        Assert.Equal("result and result\n", Extract("<w:p>" + Alternate(Understood, branch, "FALLBACK") + WordprocessingFixtures.R(" and ") + Alternate(NotUnderstood, "UNTAKEN", branch) + "</w:p>"));
    }

    [Fact]
    public void Extract_FieldCharacterInsideAlternateContent_StillDelimitsFieldCode()
    {
        string begin = Run(Alternate(Understood, "<w:fldChar w:fldCharType=\"begin\"/>", "<w:fldChar w:fldCharType=\"begin\"/>"));

        string body = "<w:p>" + begin + WordprocessingFixtures.R("FIELD_CODE_LEAK")
            + Run("<w:fldChar w:fldCharType=\"separate\"/>") + WordprocessingFixtures.R("result") + Run("<w:fldChar w:fldCharType=\"end\"/>") + "</w:p>";

        Assert.Equal("result\n", Extract(body));
    }

    [Fact]
    public void Extract_FieldSeparatorAndEndInsideAlternateContent_AreHonoured()
    {
        string separate = Run(Alternate(Understood, "<w:fldChar w:fldCharType=\"separate\"/>", "<w:fldChar w:fldCharType=\"separate\"/>"));

        string end = Run(Alternate(NotUnderstood, "<w:fldChar w:fldCharType=\"end\"/>", "<w:fldChar w:fldCharType=\"end\"/>"));

        string body = "<w:p>" + Run("<w:fldChar w:fldCharType=\"begin\"/>") + WordprocessingFixtures.R("CODE") + separate + WordprocessingFixtures.R("result") + end + WordprocessingFixtures.R(" tail") + "</w:p>";

        Assert.Equal("result tail\n", Extract(body));
    }

    [Fact]
    public void Extract_NoteReferenceInTheBranchThatIsSelected_CountsAndInTheOneThatIsNotDoesNot()
    {
        string selected = Alternate(Understood, WordprocessingFixtures.FootnoteReference(1), WordprocessingFixtures.FootnoteReference(2));

        string fallback = Alternate(NotUnderstood, WordprocessingFixtures.FootnoteReference(3), WordprocessingFixtures.FootnoteReference(4));

        string footnotes = string.Concat(Enumerable.Range(1, 4).Select(id => $"<w:footnote w:id=\"{id}\">{WordprocessingFixtures.P("note " + id)}</w:footnote>"));

        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document("<w:p>" + WordprocessingFixtures.R("body") + selected + fallback + "</w:p>"),
            footnotesXml: WordprocessingFixtures.Footnotes(footnotes));

        Assert.Equal("body\nnote 1\nnote 4\n", WordprocessingFixtures.Extract(package));
    }

    // ---------------------------------------------------------------- drawings

    private static string Anchor(string beforeDocPr, string boxText) =>
        "<w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"114300\" distR=\"114300\" simplePos=\"0\" relativeHeight=\"1\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">"
        + "<wp:simplePos x=\"0\" y=\"0\"/>" + beforeDocPr
        + "<wp:extent cx=\"1\" cy=\"1\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/><wp:docPr id=\"1\" name=\"Text Box 1\"/><wp:cNvGraphicFramePr/>"
        + "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvSpPr txBox=\"1\"/><wps:spPr/>"
        + $"<wps:txbx><w:txbxContent><w:p><w:r><w:t>{boxText}</w:t></w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing>";

    private const string RelativePosition =
        "<mc:AlternateContent><mc:Choice Requires=\"wp14\"><wp:positionH relativeFrom=\"page\"><wp14:pctPosHOffset>4000</wp14:pctPosHOffset></wp:positionH></mc:Choice>"
        + "<mc:Fallback><wp:positionH relativeFrom=\"page\"><wp:posOffset>310515</wp:posOffset></wp:positionH></mc:Fallback></mc:AlternateContent>"
        + "<mc:AlternateContent><mc:Choice Requires=\"wp14\"><wp:positionV relativeFrom=\"page\"><wp14:pctPosVOffset>5000</wp14:pctPosVOffset></wp:positionV></mc:Choice>"
        + "<mc:Fallback><wp:positionV relativeFrom=\"page\"><wp:posOffset>502920</wp:posOffset></wp:positionV></mc:Fallback></mc:AlternateContent>";

    [Fact]
    public void Extract_FloatingObjectWithRelativePositionInAlternateContentBeforeDocPr_IsAcceptedAndProjected()
    {
        // Word 2010+ stores a percentage position as an alternate content around positionH and positionV, before docPr.
        string body = "<w:p>" + WordprocessingFixtures.R("body") + Run(Alternate("wps", Anchor(RelativePosition, "Cover page title"), string.Empty)) + "</w:p>";

        Assert.Equal("body\nCover page title\n\n", Extract(body));
    }

    [Fact]
    public void Extract_FloatingObjectWithRelativeSizeAndPositionExtensions_IsNotRefused()
    {
        // The wp14 extension elements: relative size around the graphic, a relative position, and an element before docPr.
        string extensions = "<wp14:sizeRelH relativeFrom=\"page\"><wp14:pctWidth>0</wp14:pctWidth></wp14:sizeRelH><wp14:sizeRelV relativeFrom=\"margin\"><wp14:pctHeight>0</wp14:pctHeight></wp14:sizeRelV>";

        string anchor = Anchor(RelativePosition + "<wp14:pctPosHOffset>1</wp14:pctPosHOffset><wp14:sizeRelH relativeFrom=\"page\"><wp14:pctWidth>0</wp14:pctWidth></wp14:sizeRelH>", "Relative box")
            .Replace("</wp:anchor>", extensions + "</wp:anchor>");

        Assert.Equal("Relative box\n\n", Extract("<w:p>" + Run(anchor) + "</w:p>"));
    }

    [Fact]
    public void Extract_FloatingObjectWithAPositionAlternateContentThatCannotBeTaken_UsesItsFallback()
    {
        string untaken = "<mc:AlternateContent><mc:Choice Requires=\"w16se\"><wp:positionH relativeFrom=\"page\"><wp:posOffset>1</wp:posOffset></wp:positionH></mc:Choice>"
            + "<mc:Fallback><wp:positionH relativeFrom=\"page\"><wp:posOffset>2</wp:posOffset></wp:positionH></mc:Fallback></mc:AlternateContent>";

        Assert.Equal("Box\n\n", Extract("<w:p>" + Run(Anchor(untaken, "Box")) + "</w:p>"));
    }

    [Fact]
    public void Extract_HiddenFloatingObjectWithRelativePosition_IsStillExcluded()
    {
        string hidden = Anchor(RelativePosition, "HIDDEN_COVER").Replace("<wp:docPr id=\"1\" name=\"Text Box 1\"/>", "<wp:docPr id=\"1\" name=\"Text Box 1\" hidden=\"1\"/>");

        Assert.Equal("body\n", Extract("<w:p>" + Run(hidden) + WordprocessingFixtures.R("body") + "</w:p>"));
    }
}
