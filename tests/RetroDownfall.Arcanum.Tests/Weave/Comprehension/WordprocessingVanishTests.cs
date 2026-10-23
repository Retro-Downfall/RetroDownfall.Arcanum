using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Formatting-hidden text (<c>w:vanish</c> and <c>w:specVanish</c>): direct formatting, character styles, paragraph
/// styles, <c>basedOn</c> chains, document defaults and the structural rules that keep the resolution honest.
/// </summary>
public sealed class WordprocessingVanishTests
{
    private const string Vanish = "<w:rPr><w:vanish/></w:rPr>";

    private const string VanishOff = "<w:rPr><w:vanish w:val=\"0\"/></w:rPr>";

    private static string Extract(string body, string? styles = null) =>
        WordprocessingFixtures.Extract(Package(body, styles));

    private static byte[] Package(string body, string? styles = null, string? footnotes = null) =>
        WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(body),
            stylesXml: styles is null ? null : WordprocessingFixtures.Styles(styles),
            footnotesXml: footnotes);

    private static string CharacterStyle(string id, string content = "", string? basedOn = null, bool isDefault = false) =>
        $"<w:style w:type=\"character\" w:styleId=\"{id}\"{(isDefault ? " w:default=\"1\"" : string.Empty)}><w:name w:val=\"{id}\"/>"
        + (basedOn is null ? string.Empty : $"<w:basedOn w:val=\"{basedOn}\"/>") + content + "</w:style>";

    private static string ParagraphStyle(string id, string content = "", string? basedOn = null, bool isDefault = false) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"{(isDefault ? " w:default=\"1\"" : string.Empty)}><w:name w:val=\"{id}\"/>"
        + (basedOn is null ? string.Empty : $"<w:basedOn w:val=\"{basedOn}\"/>") + content + "</w:style>";

    private static string StyledParagraph(string styleId, string text) =>
        $"<w:p><w:pPr><w:pStyle w:val=\"{styleId}\"/></w:pPr>{WordprocessingFixtures.R(text)}</w:p>";

    private static string RunWithStyle(string styleId, string text, string extraProperties = "") =>
        WordprocessingFixtures.R(text, $"<w:rStyle w:val=\"{styleId}\"/>{extraProperties}");

    // ---------------------------------------------------------------- direct formatting

    [Fact]
    public void Extract_DirectVanish_HidesTheRunAndNothingElse()
    {
        string text = Extract("<w:p>" + WordprocessingFixtures.R("shown ") + WordprocessingFixtures.R("HIDDEN", "<w:vanish/>") + WordprocessingFixtures.R("end") + "</w:p>");

        Assert.Equal("shown end\n", text);
    }

    [Theory]
    [InlineData("<w:vanish/>", true)]
    [InlineData("<w:vanish w:val=\"1\"/>", true)]
    [InlineData("<w:vanish w:val=\"true\"/>", true)]
    [InlineData("<w:vanish w:val=\"on\"/>", true)]
    [InlineData("<w:vanish w:val=\"yes\"/>", true)]
    [InlineData("<w:vanish w:val=\"False\"/>", true)]
    [InlineData("<w:vanish w:val=\"OFF\"/>", true)]
    [InlineData("<w:vanish val=\"0\"/>", true)]
    [InlineData("<w:specVanish/>", true)]
    [InlineData("<w:specVanish w:val=\"1\"/>", true)]
    [InlineData("<w:vanish w:val=\"0\"/><w:specVanish/>", true)]
    [InlineData("<w:vanish/><w:vanish w:val=\"0\"/>", true)]
    [InlineData("<w:vanish w:val=\"0\"/>", false)]
    [InlineData("<w:vanish w:val=\"false\"/>", false)]
    [InlineData("<w:vanish w:val=\"off\"/>", false)]
    [InlineData("<w:specVanish w:val=\"0\"/>", false)]
    [InlineData("<w:specVanish w:val=\"false\"/><w:vanish w:val=\"off\"/>", false)]
    [InlineData("<w:b/><w:i/>", false)]
    public void Extract_VanishValue_HidesUnlessExplicitlyOff(string properties, bool hidden)
    {
        // Sensitivity: vanish and specVanish both count; only 0, false and off un-hide; anything unrecognised hides.
        string text = Extract("<w:p>" + WordprocessingFixtures.R("x", properties) + "</w:p>");

        Assert.Equal(hidden ? "\n" : "x\n", text);
    }

    [Fact]
    public void Extract_DirectVanishOff_OverridesCharacterParagraphAndDefaultsHiding()
    {
        string styles =
            "<w:docDefaults><w:rPrDefault>" + Vanish + "</w:rPrDefault></w:docDefaults>"
            + CharacterStyle("Hidden", Vanish) + ParagraphStyle("HiddenPara", Vanish);

        string text = Extract(
            "<w:p><w:pPr><w:pStyle w:val=\"HiddenPara\"/></w:pPr>" + RunWithStyle("Hidden", "shown", "<w:vanish w:val=\"0\"/>") + "</w:p>",
            styles);

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_DirectVanishOn_HidesEvenWhenEveryStyleSaysVisible()
    {
        string styles = CharacterStyle("Visible", VanishOff) + ParagraphStyle("VisiblePara", VanishOff);

        string text = Extract(
            "<w:p><w:pPr><w:pStyle w:val=\"VisiblePara\"/></w:pPr>" + RunWithStyle("Visible", "HIDDEN", "<w:vanish/>") + "</w:p>",
            styles);

        Assert.Equal("\n", text);
    }

    [Fact]
    public void Extract_RunPropertiesFromPrettyPrintedXml_AreStillTheFirstChild()
    {
        string text = Extract("<w:p>\n  <w:r>\n    <w:rPr>\n      <w:vanish/>\n    </w:rPr>\n    <w:t>HIDDEN</w:t>\n  </w:r>\n</w:p>");

        Assert.Equal("\n", text);
    }

    // ---------------------------------------------------------------- character and paragraph styles

    [Fact]
    public void Extract_CharacterStyleVanish_HidesTheRunsThatUseIt()
    {
        string styles = CharacterStyle("Hidden", Vanish) + CharacterStyle("Plain");

        string text = Extract("<w:p>" + RunWithStyle("Plain", "a ") + RunWithStyle("Hidden", "HIDDEN") + RunWithStyle("Plain", "b") + "</w:p>", styles);

        Assert.Equal("a b\n", text);
    }

    [Fact]
    public void Extract_ParagraphStyleVanish_HidesTheParagraphsRunsButKeepsItsLine()
    {
        string styles = ParagraphStyle("HiddenPara", Vanish) + ParagraphStyle("Plain");

        string text = Extract(StyledParagraph("Plain", "one") + StyledParagraph("HiddenPara", "HIDDEN") + StyledParagraph("Plain", "two"), styles);

        Assert.Equal("one\n\ntwo\n", text);
    }

    [Fact]
    public void Extract_ParagraphStyleVanish_AlsoHidesRunsInsideWrappers()
    {
        string styles = ParagraphStyle("HiddenPara", Vanish);

        string text = Extract(
            "<w:p><w:pPr><w:pStyle w:val=\"HiddenPara\"/></w:pPr><w:hyperlink w:anchor=\"a\">" + WordprocessingFixtures.R("HIDDEN_LINK") + "</w:hyperlink>"
            + "<w:ins w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\">" + WordprocessingFixtures.R("HIDDEN_INSERTED") + "</w:ins></w:p>",
            styles);

        Assert.Equal("\n", text);
    }

    [Fact]
    public void Extract_AHiddenTextBoxParagraph_DoesNotHideTheEnclosingParagraphsLaterRuns()
    {
        string styles = ParagraphStyle("HiddenPara", Vanish) + ParagraphStyle("Plain");

        string box =
            "<w:r><w:pict><v:shape><v:textbox><w:txbxContent>" + StyledParagraph("HiddenPara", "HIDDEN_IN_BOX") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

        string text = Extract(
            StyledParagraph("HiddenPara", "HIDDEN_OUTER")
            + "<w:p><w:pPr><w:pStyle w:val=\"Plain\"/></w:pPr>" + box + WordprocessingFixtures.R("after box") + "</w:p>",
            styles);

        Assert.Equal("\n\nafter box\n", text);
    }

    [Fact]
    public void Extract_ParagraphStyleIsRestoredAfterANestedParagraph()
    {
        // A hidden paragraph holds a text box whose paragraph is visible; runs after the box are still hidden.
        string styles = ParagraphStyle("HiddenPara", Vanish) + ParagraphStyle("Plain");

        string box =
            "<w:r><w:rPr><w:vanish w:val=\"0\"/></w:rPr><w:pict><v:shape><v:textbox><w:txbxContent>" + StyledParagraph("Plain", "box text") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

        string text = Extract("<w:p><w:pPr><w:pStyle w:val=\"HiddenPara\"/></w:pPr>" + box + WordprocessingFixtures.R("HIDDEN_AFTER_BOX") + "</w:p>", styles);

        Assert.Equal("box text\n\n", text);
    }

    // ---------------------------------------------------------------- basedOn chains

    [Fact]
    public void Extract_VanishInheritedThroughBasedOn_HidesTheDerivedStyle()
    {
        string styles =
            CharacterStyle("Base", Vanish) + CharacterStyle("Middle", basedOn: "Base") + CharacterStyle("Derived", basedOn: "Middle")
            + ParagraphStyle("BasePara", Vanish) + ParagraphStyle("DerivedPara", basedOn: "BasePara");

        string text = Extract(
            "<w:p>" + RunWithStyle("Derived", "HIDDEN_CHAR") + WordprocessingFixtures.R("shown") + "</w:p>" + StyledParagraph("DerivedPara", "HIDDEN_PARA"),
            styles);

        Assert.Equal("shown\n\n", text);
    }

    [Fact]
    public void Extract_DerivedStyleVanishOff_OverridesAHiddenBase()
    {
        string styles = CharacterStyle("Base", Vanish) + CharacterStyle("Derived", VanishOff, basedOn: "Base");

        string text = Extract("<w:p>" + RunWithStyle("Derived", "shown") + "</w:p>", styles);

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_BasedOnAnUndefinedStyle_EndsTheChain()
    {
        string styles = CharacterStyle("Orphan", basedOn: "NoSuchStyle") + CharacterStyle("OrphanHidden", Vanish, basedOn: "NoSuchStyle");

        string text = Extract("<w:p>" + RunWithStyle("Orphan", "shown ") + RunWithStyle("OrphanHidden", "HIDDEN") + "</w:p>", styles);

        Assert.Equal("shown \n", text);
    }

    [Fact]
    public void Extract_BasedOnChainOfExactlyTheMaximumLength_IsResolved()
    {
        string styles = Chain(32, vanishAtBase: true);

        Assert.Equal("\n", Extract("<w:p>" + RunWithStyle("S0", "HIDDEN") + "</w:p>", styles));
    }

    [Fact]
    public void Extract_BasedOnChainOneHopTooLong_IsStructureBounds()
    {
        // Sensitivity: the 32-hop bound on style inheritance chains.
        string styles = Chain(33, vanishAtBase: true);

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), styles)));
    }

    [Fact]
    public void Extract_OverlongChainIsRejectedEvenWhenNoRunUsesIt()
    {
        string styles = Chain(40, vanishAtBase: false);

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), styles)));
    }

    [Fact]
    public void Extract_BasedOnCycle_IsStructureBounds()
    {
        string styles = CharacterStyle("A", basedOn: "B") + CharacterStyle("B", basedOn: "A");

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), styles)));
    }

    [Fact]
    public void Extract_StyleBasedOnItself_IsStructureBounds()
    {
        string styles = ParagraphStyle("Loop", basedOn: "Loop");

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), styles)));
    }

    [Fact]
    public void Extract_BasedOnCycleThatDeclaresVanish_IsStillStructureBounds()
    {
        // Rejection depends on the chain's shape, never on where a vanish happens to sit.
        string styles = CharacterStyle("A", Vanish, basedOn: "B") + CharacterStyle("B", basedOn: "A");

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), styles)));
    }

    [Fact]
    public void Extract_StyleWithTwoBasedOnElements_IsFormatMismatch()
    {
        string styles = "<w:style w:type=\"character\" w:styleId=\"Two\"><w:basedOn w:val=\"A\"/><w:basedOn w:val=\"B\"/></w:style>";

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), styles)));
    }

    private static string Chain(int hops, bool vanishAtBase)
    {
        StringBuilder styles = new();

        for (int index = 0; index <= hops; index++)
        {
            string? basedOn = index < hops ? $"S{index + 1}" : null;

            styles.Append(CharacterStyle($"S{index}", index == hops && vanishAtBase ? Vanish : string.Empty, basedOn));
        }

        return styles.ToString();
    }

    // ---------------------------------------------------------------- document defaults

    [Fact]
    public void Extract_DocumentDefaultsVanish_HidesRunsWithNoOtherSayAndDirectOffRestoresThem()
    {
        string styles = "<w:docDefaults><w:rPrDefault><w:rPr><w:vanish/></w:rPr></w:rPrDefault></w:docDefaults>";

        string text = Extract("<w:p>" + WordprocessingFixtures.R("HIDDEN") + WordprocessingFixtures.R("shown", "<w:vanish w:val=\"0\"/>") + "</w:p>", styles);

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_DocumentDefaultsWithoutVanish_HideNothing()
    {
        string styles = "<w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault><w:pPrDefault/></w:docDefaults>";

        Assert.Equal("x\n", Extract(WordprocessingFixtures.P("x"), styles));
    }

    [Fact]
    public void Extract_DocumentDefaultsVanishOff_HidesNothing()
    {
        string styles = "<w:docDefaults><w:rPrDefault>" + VanishOff + "</w:rPrDefault></w:docDefaults>";

        Assert.Equal("x\n", Extract(WordprocessingFixtures.P("x"), styles));
    }

    [Fact]
    public void Extract_RunNamingSeveralCharacterStyles_IsHiddenIfAnyOfThemHides()
    {
        string styles = CharacterStyle("Hidden", Vanish) + CharacterStyle("Plain");

        Assert.Equal("\n", Extract("<w:p>" + WordprocessingFixtures.R("HIDDEN_A", "<w:rStyle w:val=\"Hidden\"/><w:rStyle w:val=\"Plain\"/>") + "</w:p>", styles));

        Assert.Equal("\n", Extract("<w:p>" + WordprocessingFixtures.R("HIDDEN_B", "<w:rStyle w:val=\"Plain\"/><w:rStyle w:val=\"Hidden\"/>") + "</w:p>", styles));
    }

    [Fact]
    public void Extract_ParagraphNamingSeveralStyles_IsHiddenIfAnyOfThemHides()
    {
        string styles = ParagraphStyle("HiddenPara", Vanish) + ParagraphStyle("Plain");

        string hiddenFirst = "<w:p><w:pPr><w:pStyle w:val=\"HiddenPara\"/><w:pStyle w:val=\"Plain\"/></w:pPr>" + WordprocessingFixtures.R("HIDDEN_A") + "</w:p>";

        string hiddenLast = "<w:p><w:pPr><w:pStyle w:val=\"Plain\"/><w:pStyle w:val=\"HiddenPara\"/></w:pPr>" + WordprocessingFixtures.R("HIDDEN_B") + "</w:p>";

        Assert.Equal("\n\n", Extract(hiddenFirst + hiddenLast, styles));
    }

    [Fact]
    public void Extract_StyleWithTwoRunPropertiesElements_HidesIfEitherHides()
    {
        string hiddenFirst = CharacterStyle("A", Vanish + VanishOff);

        string hiddenLast = CharacterStyle("B", VanishOff + Vanish);

        Assert.Equal("\n\n", Extract("<w:p>" + RunWithStyle("A", "HIDDEN_A") + "</w:p><w:p>" + RunWithStyle("B", "HIDDEN_B") + "</w:p>", hiddenFirst + hiddenLast));
    }

    // ---------------------------------------------------------------- how the layers combine

    [Fact]
    public void Extract_HiddenAtAnyStyleLevel_Hides_AndTogglePropertyXorIsDeliberatelyNotModelled()
    {
        // Word treats vanish as a toggle property across style levels; this extractor takes the conservative union, so
        // text is never emitted that any level hides. Two levels hiding therefore still hide (Word would show it).
        string styles = CharacterStyle("Hidden", Vanish) + CharacterStyle("Off", VanishOff) + ParagraphStyle("HiddenPara", Vanish);

        string text = Extract(
            StyledParagraph("HiddenPara", "A_PARA")
            + "<w:p><w:pPr><w:pStyle w:val=\"HiddenPara\"/></w:pPr>" + RunWithStyle("Hidden", "B_BOTH") + RunWithStyle("Off", "C_PARA_HIDDEN_CHAR_OFF") + "</w:p>"
            + "<w:p>" + RunWithStyle("Hidden", "D_CHAR") + "</w:p>",
            styles);

        Assert.Equal("\n\n\n", text);
    }

    [Fact]
    public void Extract_VisibleStylesAndDefaults_ProjectTheText()
    {
        string styles = ParagraphStyle("Plain", isDefault: true) + CharacterStyle("Emphasis", "<w:rPr><w:i/></w:rPr>");

        Assert.Equal("one two\n", Extract("<w:p>" + WordprocessingFixtures.R("one ") + RunWithStyle("Emphasis", "two") + "</w:p>", styles));
    }

    // ---------------------------------------------------------------- default styles

    [Fact]
    public void Extract_DefaultParagraphStyleVanish_HidesParagraphsWithoutAStyleOrWithAnUnknownOne()
    {
        string styles = ParagraphStyle("Normal", Vanish, isDefault: true) + ParagraphStyle("Shown", VanishOff);

        string text = Extract(
            WordprocessingFixtures.P("A_NO_STYLE") + StyledParagraph("Shown", "shown") + StyledParagraph("Unknown", "B_UNKNOWN_STYLE"),
            styles);

        Assert.Equal("\nshown\n\n", text);
    }

    [Fact]
    public void Extract_DefaultCharacterStyleVanish_HidesRunsWithoutACharacterStyle()
    {
        string styles = CharacterStyle("DefaultFont", Vanish, isDefault: true) + CharacterStyle("Shown", VanishOff);

        string text = Extract("<w:p>" + WordprocessingFixtures.R("A_NO_STYLE") + RunWithStyle("Shown", "shown") + RunWithStyle("Unknown", "B_UNKNOWN") + "</w:p>", styles);

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_UnknownStylesWithoutHiddenDefaults_AreVisible()
    {
        string styles = CharacterStyle("Other") + ParagraphStyle("OtherPara");

        Assert.Equal("shown\n", Extract("<w:p><w:pPr><w:pStyle w:val=\"Unknown\"/></w:pPr>" + RunWithStyle("AlsoUnknown", "shown") + "</w:p>", styles));
    }

    [Fact]
    public void Extract_NoStylesPart_HidesNothingExceptDirectFormatting()
    {
        Assert.Equal("a\n", Extract("<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>" + RunWithStyle("Strong", "a") + "</w:p>"));
    }

    // ---------------------------------------------------------------- duplicate and irrelevant definitions

    [Fact]
    public void Extract_DuplicateStyleId_IsFormatMismatchWhicheverDefinitionHides()
    {
        // The consumer that wins a duplicate is not specified, so a duplicate is never resolved: the part is refused.
        string hiddenSecond = CharacterStyle("Twin") + CharacterStyle("Twin", Vanish);

        string hiddenFirst = CharacterStyle("Twin", Vanish) + CharacterStyle("Twin");

        string differentTypes = CharacterStyle("Twin") + ParagraphStyle("Twin");

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), hiddenSecond)));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), hiddenFirst)));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), differentTypes)));
    }

    [Fact]
    public void Extract_NumberingAndUnrecognisedStyleTypes_AreNotRead()
    {
        string styles =
            "<w:style w:type=\"numbering\" w:styleId=\"List\">" + Vanish + "</w:style>"
            + "<w:style w:styleId=\"Mystery\" w:type=\"mystery\">" + Vanish + "</w:style>"
            + "<w:style w:type=\"numbering\" w:styleId=\"List\">" + Vanish + "</w:style>";

        Assert.Equal("x\n", Extract("<w:p>" + RunWithStyle("List", "x") + RunWithStyle("Mystery", "") + "</w:p>", styles));
    }

    [Fact]
    public void Extract_TableStyleMayShareItsIdentifierWithAParagraphOrCharacterStyle()
    {
        string styles = CharacterStyle("Shared") + TableStyle("Shared", Vanish);

        Assert.Equal("x\n", Extract("<w:p>" + RunWithStyle("Shared", "x") + "</w:p>", styles));
    }

    [Fact]
    public void Extract_StyleWithoutAType_IsAParagraphStyle()
    {
        string styles = "<w:style w:styleId=\"Untyped\">" + Vanish + "</w:style>";

        Assert.Equal("\n", Extract(StyledParagraph("Untyped", "HIDDEN"), styles));
    }

    [Fact]
    public void Extract_StyleWithoutAnId_IsIgnored()
    {
        string styles = "<w:style w:type=\"character\">" + Vanish + "</w:style>";

        Assert.Equal("x\n", Extract(WordprocessingFixtures.P("x"), styles));
    }

    [Fact]
    public void Extract_VanishInAParagraphMarkOrConditionalPropertiesOfAStyle_DoesNotHideTheRuns()
    {
        // Only the style's own w:rPr counts. w:pPr/w:rPr formats the paragraph mark, and a table style's conditional
        // formatting is not modelled.
        string styles = ParagraphStyle("MarkOnly", "<w:pPr><w:rPr><w:vanish/></w:rPr></w:pPr>") + CharacterStyle("Looks", "<w:tblPr/><w:pPr><w:rPr><w:vanish/></w:rPr></w:pPr>");

        Assert.Equal("a\nb\n", Extract(StyledParagraph("MarkOnly", "a") + "<w:p>" + RunWithStyle("Looks", "b") + "</w:p>", styles));
    }

    [Fact]
    public void Extract_LatentStylesAndOtherStylesPartContent_AreIgnored()
    {
        string styles =
            "<w:latentStyles w:defLockedState=\"0\"><w:lsdException w:name=\"Normal\" w:locked=\"0\"/></w:latentStyles>"
            + "<w:docDefaults><w:pPrDefault><w:pPr><w:spacing w:after=\"160\"/></w:pPr></w:pPrDefault></w:docDefaults>"
            + CharacterStyle("Hidden", Vanish);

        Assert.Equal("a\n", Extract("<w:p>" + WordprocessingFixtures.R("a") + "</w:p>", styles));
    }

    // ---------------------------------------------------------------- table styles

    private static string TableStyle(string id, string content = "", string? basedOn = null, bool isDefault = false) =>
        $"<w:style w:type=\"table\" w:styleId=\"{id}\"{(isDefault ? " w:default=\"1\"" : string.Empty)}><w:name w:val=\"{id}\"/>"
        + (basedOn is null ? string.Empty : $"<w:basedOn w:val=\"{basedOn}\"/>") + content + "</w:style>";

    private static string Table(string cellContent, string? styleId = null) =>
        $"<w:tbl><w:tblPr>{(styleId is null ? string.Empty : $"<w:tblStyle w:val=\"{styleId}\"/>")}</w:tblPr><w:tr><w:tc>{cellContent}</w:tc></w:tr></w:tbl>";

    [Fact]
    public void Extract_TableStyleVanish_HidesTheTableAndNothingAfterIt()
    {
        // Sensitivity: honouring the style named by w:tblPr/w:tblStyle.
        string styles = TableStyle("HiddenTable", Vanish) + TableStyle("PlainTable");

        string text = Extract(
            Table(WordprocessingFixtures.P("HIDDEN"), "HiddenTable") + Table(WordprocessingFixtures.P("plain"), "PlainTable") + WordprocessingFixtures.P("shown"),
            styles);

        Assert.Equal("\nplain\nshown\n", text);
    }

    [Fact]
    public void Extract_DirectVanishOff_OverridesATableStyle()
    {
        string styles = TableStyle("HiddenTable", Vanish);

        string text = Extract(Table("<w:p>" + WordprocessingFixtures.R("shown", "<w:vanish w:val=\"0\"/>") + WordprocessingFixtures.R("HIDDEN") + "</w:p>", "HiddenTable"), styles);

        Assert.Equal("shown\n", text);
    }

    [Fact]
    public void Extract_ConditionalTableFormatThatHides_HidesTheWholeTable()
    {
        // Which cells a conditional format reaches is not modelled, so it is taken to reach all of them.
        string styles =
            TableStyle("HeaderHidden", "<w:tblStylePr w:type=\"firstRow\"><w:rPr><w:vanish/></w:rPr></w:tblStylePr>")
            + TableStyle("HeaderBold", "<w:tblStylePr w:type=\"firstRow\"><w:rPr><w:b/></w:rPr></w:tblStylePr>");

        string text = Extract(Table(WordprocessingFixtures.P("HIDDEN"), "HeaderHidden") + Table(WordprocessingFixtures.P("shown"), "HeaderBold"), styles);

        Assert.Equal("\nshown\n", text);
    }

    [Fact]
    public void Extract_TableStyleVanishThroughBasedOn_FollowsTheSameRulesAsOtherStyles()
    {
        string styles =
            TableStyle("Base", Vanish) + TableStyle("Derived", basedOn: "Base") + TableStyle("DerivedOff", VanishOff, basedOn: "Base")
            + TableStyle("ConditionalBase", "<w:tblStylePr w:type=\"lastRow\"><w:rPr><w:vanish/></w:rPr></w:tblStylePr>") + TableStyle("DerivedOfConditional", VanishOff, basedOn: "ConditionalBase");

        string text = Extract(
            Table(WordprocessingFixtures.P("HIDDEN_DERIVED"), "Derived") + Table(WordprocessingFixtures.P("shown"), "DerivedOff") + Table(WordprocessingFixtures.P("HIDDEN_CONDITIONAL"), "DerivedOfConditional"),
            styles);

        Assert.Equal("\nshown\n\n", text);
    }

    [Fact]
    public void Extract_DefaultTableStyleVanish_HidesTablesWithoutAStyleOrWithAnUnknownOne()
    {
        string styles = TableStyle("TableNormal", Vanish, isDefault: true) + TableStyle("Shown", VanishOff);

        string text = Extract(
            Table(WordprocessingFixtures.P("A_NO_STYLE")) + Table(WordprocessingFixtures.P("B_UNKNOWN"), "NoSuchStyle") + Table(WordprocessingFixtures.P("shown"), "Shown"),
            styles);

        Assert.Equal("\n\nshown\n", text);
    }

    [Fact]
    public void Extract_TableStyleNamingAParagraphStyle_FallsBackToTheDefaultTableStyle()
    {
        string styles = ParagraphStyle("HiddenPara", Vanish) + TableStyle("TableNormal", isDefault: true);

        Assert.Equal("shown\n", Extract(Table(WordprocessingFixtures.P("shown"), "HiddenPara"), styles));
    }

    [Fact]
    public void Extract_TableNestedInAHiddenTable_IsHiddenWhateverItsOwnStyleSays()
    {
        string styles = TableStyle("HiddenTable", Vanish) + TableStyle("VisibleTable", VanishOff);

        string inner = Table(WordprocessingFixtures.P("HIDDEN_INNER"), "VisibleTable");

        string text = Extract(Table(WordprocessingFixtures.P("HIDDEN_OUTER") + inner + "<w:p/>", "HiddenTable") + WordprocessingFixtures.P("shown"), styles);

        Assert.Equal("\nshown\n", text);
    }

    [Fact]
    public void Extract_HiddenTableNestedInAVisibleTable_DoesNotHideTheOuterTable()
    {
        string styles = TableStyle("HiddenTable", Vanish);

        string text = Extract(Table(WordprocessingFixtures.P("outer") + Table(WordprocessingFixtures.P("HIDDEN_INNER"), "HiddenTable") + WordprocessingFixtures.P("outer after")), styles);

        Assert.Equal("outer outer after\n", text);
    }

    [Fact]
    public void Extract_TablePropertiesAfterARowOrRepeated_IsFormatMismatch()
    {
        string late = "<w:tbl><w:tr><w:tc>" + WordprocessingFixtures.P("EMITTED_EARLY") + "</w:tc></w:tr><w:tblPr><w:tblStyle w:val=\"HiddenTable\"/></w:tblPr></w:tbl>";

        string twice = "<w:tbl><w:tblPr/><w:tblPr><w:tblStyle w:val=\"HiddenTable\"/></w:tblPr><w:tr><w:tc>" + WordprocessingFixtures.P("x") + "</w:tc></w:tr></w:tbl>";

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(Package(late)));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(Package(twice)));
    }

    [Theory]
    [InlineData("bookmarkStart")]
    [InlineData("bookmarkEnd")]
    [InlineData("commentRangeStart")]
    [InlineData("commentRangeEnd")]
    [InlineData("moveFromRangeStart")]
    [InlineData("moveFromRangeEnd")]
    [InlineData("moveToRangeStart")]
    [InlineData("moveToRangeEnd")]
    [InlineData("customXmlInsRangeStart")]
    [InlineData("customXmlInsRangeEnd")]
    [InlineData("customXmlDelRangeStart")]
    [InlineData("customXmlDelRangeEnd")]
    [InlineData("customXmlMoveFromRangeStart")]
    [InlineData("customXmlMoveFromRangeEnd")]
    [InlineData("customXmlMoveToRangeStart")]
    [InlineData("customXmlMoveToRangeEnd")]
    public void Extract_RangeMarkupBeforeTableProperties_IsAllowedAndTheTableStyleStillApplies(string markup)
    {
        // The schema lets range markup precede w:tblPr, so a bookmark or comment range that starts at a table must not
        // refuse the document; the style named by the properties that follow must still hide the table.
        string styles = TableStyle("HiddenTable", Vanish) + TableStyle("PlainTable");

        string hidden = $"<w:tbl><w:{markup} w:id=\"1\" w:name=\"m\"/><w:tblPr><w:tblStyle w:val=\"HiddenTable\"/></w:tblPr><w:tr><w:tc>" + WordprocessingFixtures.P("HIDDEN") + "</w:tc></w:tr></w:tbl>";

        string plain = $"<w:tbl><w:{markup} w:id=\"2\" w:name=\"m\"/><w:tblPr><w:tblStyle w:val=\"PlainTable\"/></w:tblPr><w:tr><w:tc>" + WordprocessingFixtures.P("plain") + "</w:tc></w:tr></w:tbl>";

        Assert.Equal("\nplain\n", Extract(hidden + plain, styles));
    }

    [Fact]
    public void Extract_RangeMarkupBeforeTableProperties_IsSkippedWithAnythingInsideIt()
    {
        // Range markup is empty by definition; anything inside it is not text a reader sees, and must not be emitted
        // before the table's own hidden flag has been read.
        string styles = TableStyle("HiddenTable", Vanish);

        string table =
            "<w:tbl><w:bookmarkStart w:id=\"1\" w:name=\"m\"><w:r><w:t>INSIDE_MARKUP</w:t></w:r></w:bookmarkStart>"
            + "<w:tblPr><w:tblStyle w:val=\"HiddenTable\"/></w:tblPr><w:tr><w:tc>" + WordprocessingFixtures.P("HIDDEN") + "</w:tc></w:tr></w:tbl>";

        Assert.Equal("\n", Extract(table, styles));
    }

    [Fact]
    public void Extract_TablePropertiesAfterRangeMarkupAndARow_IsFormatMismatch()
    {
        // Range markup excuses nothing once a row has been read.
        string late = "<w:tbl><w:bookmarkStart w:id=\"1\" w:name=\"m\"/><w:tr><w:tc>" + WordprocessingFixtures.P("EMITTED_EARLY") + "</w:tc></w:tr><w:tblPr><w:tblStyle w:val=\"HiddenTable\"/></w:tblPr></w:tbl>";

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(Package(late)));
    }

    [Fact]
    public void Extract_TableWithoutPropertiesOrWithEmptyOnes_IsProjected()
    {
        Assert.Equal("a\nb\n", Extract("<w:tbl><w:tr><w:tc>" + WordprocessingFixtures.P("a") + "</w:tc></w:tr></w:tbl><w:tbl><w:tblPr/><w:tr><w:tc>" + WordprocessingFixtures.P("b") + "</w:tc></w:tr></w:tbl>"));
    }

    [Fact]
    public void Extract_PreviousTableStyleInsideATablePropertiesChange_IsNotCurrent()
    {
        string styles = TableStyle("HiddenTable", Vanish) + TableStyle("PlainTable");

        string nowPlain = "<w:tbl><w:tblPr><w:tblStyle w:val=\"PlainTable\"/><w:tblPrChange w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:tblPr><w:tblStyle w:val=\"HiddenTable\"/></w:tblPr></w:tblPrChange></w:tblPr><w:tr><w:tc>" + WordprocessingFixtures.P("shown") + "</w:tc></w:tr></w:tbl>";

        string nowHidden = "<w:tbl><w:tblPr><w:tblStyle w:val=\"HiddenTable\"/><w:tblPrChange w:id=\"2\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:tblPr><w:tblStyle w:val=\"PlainTable\"/></w:tblPr></w:tblPrChange></w:tblPr><w:tr><w:tc>" + WordprocessingFixtures.P("HIDDEN") + "</w:tc></w:tr></w:tbl>";

        Assert.Equal("shown\n\n", Extract(nowPlain + nowHidden, styles));
    }

    [Fact]
    public void Extract_HiddenTableHoldingATextBox_HidesTheTextBoxToo()
    {
        string styles = TableStyle("HiddenTable", Vanish);

        string box = "<w:p><w:r><w:rPr><w:vanish w:val=\"0\"/></w:rPr><w:pict><v:shape><v:textbox><w:txbxContent>" + WordprocessingFixtures.P("HIDDEN_BOX") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>";

        Assert.Equal("\n", Extract(Table(box, "HiddenTable"), styles));
    }

    [Fact]
    public void Extract_TableStyleCycleAndOverlongTableChain_AreStructureBounds()
    {
        string cycle = TableStyle("A", basedOn: "B") + TableStyle("B", basedOn: "A");

        StringBuilder chain = new();

        for (int index = 0; index <= 33; index++)
        {
            chain.Append(TableStyle($"T{index}", string.Empty, index < 33 ? $"T{index + 1}" : null));
        }

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), cycle)));

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), chain.ToString())));
    }

    [Fact]
    public void Extract_DuplicateTableStyleId_IsFormatMismatch()
    {
        string styles = TableStyle("Twin") + TableStyle("Twin", Vanish);

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), styles)));
    }

    [Fact]
    public void Extract_TableStylesCountTowardsTheStyleRetentionLimit()
    {
        string within = string.Concat(Enumerable.Range(0, 8191).Select(index => CharacterStyle($"s{index}"))) + TableStyle("OneTable");

        string beyond = string.Concat(Enumerable.Range(0, 8192).Select(index => CharacterStyle($"s{index}"))) + TableStyle("OneTable");

        Assert.Equal("x\n", Extract(WordprocessingFixtures.P("x"), within));

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), beyond)));
    }

    // ---------------------------------------------------------------- tracked formatting changes

    [Fact]
    public void Extract_PreviousFormattingInsideARunPropertiesChange_IsNotCurrentFormatting()
    {
        // Sensitivity: only the direct children of w:rPr count. The change record's old properties are history.
        string oldWasHidden = WordprocessingFixtures.R("shown ", "<w:rPrChange w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:rPr><w:vanish/></w:rPr></w:rPrChange>");

        string nowHidden = WordprocessingFixtures.R("HIDDEN", "<w:vanish/><w:rPrChange w:id=\"2\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:rPr><w:vanish w:val=\"0\"/></w:rPr></w:rPrChange>");

        Assert.Equal("shown \n", Extract("<w:p>" + oldWasHidden + nowHidden + "</w:p>"));
    }

    [Fact]
    public void Extract_PreviousStyleInsideAParagraphPropertiesChange_IsNotTheCurrentStyle()
    {
        string styles = ParagraphStyle("HiddenPara", Vanish) + ParagraphStyle("Plain");

        string nowPlain = "<w:p><w:pPr><w:pStyle w:val=\"Plain\"/><w:pPrChange w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:pPr><w:pStyle w:val=\"HiddenPara\"/></w:pPr></w:pPrChange></w:pPr>" + WordprocessingFixtures.R("shown") + "</w:p>";

        string nowHidden = "<w:p><w:pPr><w:pStyle w:val=\"HiddenPara\"/><w:pPrChange w:id=\"2\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:pPr><w:pStyle w:val=\"Plain\"/></w:pPr></w:pPrChange></w:pPr>" + WordprocessingFixtures.R("HIDDEN") + "</w:p>";

        Assert.Equal("shown\n\n", Extract(nowPlain + nowHidden, styles));
    }

    [Fact]
    public void Extract_ParagraphMarkVanish_DoesNotHideTheRuns()
    {
        string text = Extract("<w:p><w:pPr><w:rPr><w:vanish/></w:rPr></w:pPr>" + WordprocessingFixtures.R("shown") + "</w:p>");

        Assert.Equal("shown\n", text);
    }

    // ---------------------------------------------------------------- hidden runs keep their structure

    [Fact]
    public void Extract_HiddenRunContent_EmitsNoTabsBreaksOrHyphens()
    {
        string text = Extract("<w:p><w:r><w:rPr><w:vanish/></w:rPr><w:t>a</w:t><w:tab/><w:br/><w:cr/><w:noBreakHyphen/><w:t>b</w:t></w:r></w:p>");

        Assert.Equal("\n", text);
    }

    [Fact]
    public void Extract_HiddenRunHoldingADrawing_HidesTheWholeDrawingAndItsTextBox()
    {
        string drawing =
            "<w:r><w:rPr><w:vanish/></w:rPr><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"Box\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("HIDDEN_BOX") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

        Assert.Equal("shown\n", Extract("<w:p>" + drawing + WordprocessingFixtures.R("shown") + "</w:p>"));
    }

    [Fact]
    public void Extract_FieldCharactersInHiddenRuns_StillDelimitTheFieldCode()
    {
        // Sensitivity: field state is tracked in hidden runs too, or the code below would surface.
        string hiddenBegin = "<w:r><w:rPr><w:vanish/></w:rPr><w:fldChar w:fldCharType=\"begin\"/></w:r>";

        string hiddenSeparate = "<w:r><w:rPr><w:vanish/></w:rPr><w:fldChar w:fldCharType=\"separate\"/></w:r>";

        string hiddenEnd = "<w:r><w:rPr><w:vanish/></w:rPr><w:fldChar w:fldCharType=\"end\"/></w:r>";

        string text = Extract("<w:p>" + WordprocessingFixtures.R("a") + hiddenBegin + WordprocessingFixtures.R("CODE_TEXT") + hiddenSeparate + WordprocessingFixtures.R("result") + hiddenEnd + WordprocessingFixtures.R("b") + "</w:p>");

        Assert.Equal("aresultb\n", text);
    }

    // ---------------------------------------------------------------- namespaces, defaults and style kinds

    [Fact]
    public void Extract_DefaultParagraphStyleVanish_AlsoHidesParagraphsWhoseParagraphPropertiesNameNoStyle()
    {
        // Sensitivity: a paragraph that has a pPr but no pStyle still takes the default paragraph style.
        string styles = ParagraphStyle("Normal", Vanish, isDefault: true);

        Assert.Equal("\n", Extract("<w:p><w:pPr><w:jc w:val=\"left\"/></w:pPr>" + WordprocessingFixtures.R("HIDDEN") + "</w:p>", styles));
    }

    [Fact]
    public void Extract_DefaultCharacterStyleVanish_AlsoHidesRunsWhoseRunPropertiesNameNoStyle()
    {
        // Sensitivity: a run that has an rPr but no rStyle still takes the default character style.
        string styles = CharacterStyle("DefaultFont", Vanish, isDefault: true);

        Assert.Equal("\n", Extract("<w:p>" + WordprocessingFixtures.R("HIDDEN", "<w:b/>") + "</w:p>", styles));
    }

    [Fact]
    public void Extract_VanishElementsInAnotherNamespace_HideNothing()
    {
        // Sensitivity: only WordprocessingML run properties count; the same local names elsewhere carry no meaning.
        string foreign = "<f:vanish xmlns:f=\"urn:foreign\"/><f:specVanish xmlns:f=\"urn:foreign\"/>";

        Assert.Equal("shown\n", Extract("<w:p>" + WordprocessingFixtures.R("shown", foreign) + "</w:p>"));
    }

    [Fact]
    public void Extract_StyleChildrenInAnotherNamespace_AreIgnored()
    {
        // Sensitivity: a style's rPr and basedOn are WordprocessingML elements; look-alikes in another namespace are not.
        string styles =
            CharacterStyle("Hidden", Vanish)
            + CharacterStyle("ForeignProperties", "<f:rPr xmlns:f=\"urn:foreign\"><w:vanish/></f:rPr>")
            + "<w:style w:type=\"character\" w:styleId=\"ForeignBase\"><f:basedOn xmlns:f=\"urn:foreign\" w:val=\"Hidden\"/></w:style>";

        string text = Extract("<w:p>" + RunWithStyle("ForeignProperties", "a ") + RunWithStyle("ForeignBase", "b") + "</w:p>", styles);

        Assert.Equal("a b\n", text);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    public void Extract_DefaultStyleFlag_MakesTheDefaultParagraphStyleOnlyWhenOn(string flag, bool isDefault)
    {
        string styles = $"<w:style w:type=\"paragraph\" w:styleId=\"Normal\" w:default=\"{flag}\">" + Vanish + "</w:style>";

        Assert.Equal(isDefault ? "\n" : "x\n", Extract(WordprocessingFixtures.P("x"), styles));
    }

    [Fact]
    public void Extract_ConditionalTableFormatsInAParagraphOrCharacterStyle_DoNotHide()
    {
        // Conditional formats (w:tblStylePr) belong to table styles; anywhere else they are not read.
        string conditional = "<w:tblStylePr w:type=\"firstRow\"><w:rPr><w:vanish/></w:rPr></w:tblStylePr>";

        string styles = ParagraphStyle("ParagraphWithConditional", conditional) + CharacterStyle("CharacterWithConditional", conditional);

        string text = Extract(StyledParagraph("ParagraphWithConditional", "a") + "<w:p>" + RunWithStyle("CharacterWithConditional", "b") + "</w:p>", styles);

        Assert.Equal("a\nb\n", text);
    }

    [Fact]
    public void Extract_DefaultTableStyleVanish_HidesATableWithNoPropertiesElementAtAll()
    {
        // Sensitivity: the default table style applies from the start of the table, not only once w:tblPr has been read.
        string styles = TableStyle("TableNormal", Vanish, isDefault: true);

        Assert.Equal("\n", Extract("<w:tbl><w:tr><w:tc>" + WordprocessingFixtures.P("HIDDEN") + "</w:tc></w:tr></w:tbl>", styles));
    }

    [Fact]
    public void Extract_TableNamingSeveralStyles_IsHiddenIfAnyOfThemHides()
    {
        string styles = TableStyle("HiddenTable", Vanish) + TableStyle("PlainTable");

        string hiddenFirst = "<w:tbl><w:tblPr><w:tblStyle w:val=\"HiddenTable\"/><w:tblStyle w:val=\"PlainTable\"/></w:tblPr><w:tr><w:tc>" + WordprocessingFixtures.P("HIDDEN_A") + "</w:tc></w:tr></w:tbl>";

        string hiddenLast = "<w:tbl><w:tblPr><w:tblStyle w:val=\"PlainTable\"/><w:tblStyle w:val=\"HiddenTable\"/></w:tblPr><w:tr><w:tc>" + WordprocessingFixtures.P("HIDDEN_B") + "</w:tc></w:tr></w:tbl>";

        Assert.Equal("\n\n", Extract(hiddenFirst + hiddenLast, styles));
    }

    [Fact]
    public void Extract_UnstyledParagraphInsideAHiddenParagraphsTextBox_StartsFromTheDefaultStyle()
    {
        // Sensitivity: a nested paragraph resets to the default paragraph style rather than inheriting its container's.
        string styles = ParagraphStyle("HiddenPara", Vanish);

        string box =
            "<w:r><w:rPr><w:vanish w:val=\"0\"/></w:rPr><w:pict><v:shape><v:textbox><w:txbxContent>" + WordprocessingFixtures.P("box text") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

        Assert.Equal("box text\n\n", Extract("<w:p><w:pPr><w:pStyle w:val=\"HiddenPara\"/></w:pPr>" + box + "</w:p>", styles));
    }

    // ---------------------------------------------------------------- order of properties

    [Fact]
    public void Extract_RunPropertiesAfterRunContent_IsFormatMismatch()
    {
        // A late w:rPr would hide text that has already been emitted, so the format forbids it.
        Assert.Equal(
            DocumentPackageRejection.FormatMismatch,
            WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx("<w:p><w:r><w:t>EMITTED_EARLY</w:t><w:rPr><w:vanish/></w:rPr></w:r></w:p>")));
    }

    [Fact]
    public void Extract_SecondRunPropertiesElement_IsFormatMismatch()
    {
        Assert.Equal(
            DocumentPackageRejection.FormatMismatch,
            WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx("<w:p><w:r><w:rPr><w:b/></w:rPr><w:rPr><w:vanish/></w:rPr><w:t>x</w:t></w:r></w:p>")));
    }

    [Fact]
    public void Extract_ParagraphPropertiesAfterParagraphContent_IsFormatMismatch()
    {
        Assert.Equal(
            DocumentPackageRejection.FormatMismatch,
            WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx("<w:p>" + WordprocessingFixtures.R("EMITTED_EARLY") + "<w:pPr><w:pStyle w:val=\"HiddenPara\"/></w:pPr></w:p>")));
    }

    [Fact]
    public void Extract_SecondParagraphPropertiesElement_IsFormatMismatch()
    {
        Assert.Equal(
            DocumentPackageRejection.FormatMismatch,
            WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx("<w:p><w:pPr/><w:pPr><w:pStyle w:val=\"HiddenPara\"/></w:pPr>" + WordprocessingFixtures.R("x") + "</w:p>")));
    }

    [Fact]
    public void Extract_EmptyRunPropertiesAndParagraphProperties_AreFine()
    {
        Assert.Equal("x\n", Extract("<w:p><w:pPr/><w:r><w:rPr/><w:t>x</w:t></w:r></w:p>"));
    }

    // ---------------------------------------------------------------- the styles part itself

    [Fact]
    public void Extract_StylesRelationshipWhosePartIsAbsent_IsMissingPart()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("x")),
            documentRelationships: [WordprocessingFixtures.Relationship("rIdS", WordprocessingFixtures.StylesType, "styles.xml")]);

        Assert.Equal(DocumentPackageRejection.MissingPart, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_StylesPartWithAnotherRootElement_IsFormatMismatch()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("x")),
            stylesXml: WordprocessingFixtures.Document(string.Empty));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_MalformedStylesPart_IsMalformedXml()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("x")),
            stylesXml: "<w:styles><w:style");

        Assert.Equal(DocumentPackageRejection.MalformedXml, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_StylesPartIsFoundByRelationshipNotByName()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/document.xml"))),
            ("word/document.xml", WordprocessingFixtures.Document("<w:p>" + RunWithStyle("Hidden", "HIDDEN") + "</w:p>")),
            ("word/_rels/document.xml.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rIdS", WordprocessingFixtures.StylesType, "../custom/theme-styles.xml"))),
            ("custom/theme-styles.xml", WordprocessingFixtures.Styles(CharacterStyle("Hidden", Vanish))),
            ("word/styles.xml", WordprocessingFixtures.Styles(CharacterStyle("Hidden"))));

        Assert.Equal("\n", WordprocessingFixtures.Extract(package));
    }

    [Fact]
    public void Extract_StrictNamespaceStyles_HideLikeTransitionalOnes()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(
                "<w:p>" + RunWithStyle("Hidden", "HIDDEN") + WordprocessingFixtures.R("shown") + "</w:p>",
                WordprocessingFixtures.StrictWordNamespace),
            stylesXml: WordprocessingFixtures.Styles(CharacterStyle("Hidden", Vanish), WordprocessingFixtures.StrictWordNamespace),
            officeDocumentType: WordprocessingFixtures.StrictOfficeDocumentType);

        Assert.Equal("shown\n", WordprocessingFixtures.Extract(package));
    }

    [Fact]
    public void Extract_StrictNamespaceVanishValue_UsesTheStrictAttributeNamespace()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document("<w:p>" + WordprocessingFixtures.R("a", "<w:vanish w:val=\"0\"/>") + WordprocessingFixtures.R("HIDDEN", "<w:vanish/>") + "</w:p>", WordprocessingFixtures.StrictWordNamespace),
            officeDocumentType: WordprocessingFixtures.StrictOfficeDocumentType);

        Assert.Equal("a\n", WordprocessingFixtures.Extract(package));
    }

    // ---------------------------------------------------------------- retention bounds

    [Fact]
    public void Extract_StylesAtTheRetentionLimit_AreAccepted()
    {
        string styles = string.Concat(Enumerable.Range(0, 8192).Select(index => CharacterStyle($"s{index}")));

        Assert.Equal("x\n", Extract(WordprocessingFixtures.P("x"), styles));
    }

    [Fact]
    public void Extract_MoreStylesThanTheRetentionLimit_IsStructureBounds()
    {
        // Sensitivity: the 8,192-style retention cap.
        string styles = string.Concat(Enumerable.Range(0, 8193).Select(index => CharacterStyle($"s{index}")));

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), styles)));
    }

    [Fact]
    public void Extract_StyleIdentifiersAtTheRetainedCharacterLimit_AreAccepted()
    {
        string styles = CharacterStyle(new string('i', 262_144));

        Assert.Equal("x\n", Extract(WordprocessingFixtures.P("x"), styles));
    }

    [Fact]
    public void Extract_BasedOnReferencesCountTowardsTheRetainedCharacterLimit()
    {
        string styles = CharacterStyle("a", basedOn: new string('b', 262_144));

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), styles)));
    }

    [Fact]
    public void Extract_StyleIdentifiersBeyondTheRetainedCharacterLimit_IsStructureBounds()
    {
        // Sensitivity: the 262,144-character cap on retained style identifiers.
        string styles = CharacterStyle(new string('i', 262_145));

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(Package(WordprocessingFixtures.P("x"), styles)));
    }
}
