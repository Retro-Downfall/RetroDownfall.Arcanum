using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.OpenDocumentFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class OpenDocumentTextExtractorTests
{
    // ---------------------------------------------------------------- projection (plan D9)

    [Fact]
    public async Task Extract_ParagraphsAndHeadings_EachEndWithANewline()
    {
        byte[] package = Odt("<text:h text:outline-level=\"1\">Title</text:h><text:p>First</text:p><text:p>Second</text:p>");

        Assert.Equal("Title\nFirst\nSecond\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_EmptyParagraph_IsABlankLine()
    {
        byte[] package = Odt("<text:p>A</text:p><text:p/><text:p>B</text:p>");

        Assert.Equal("A\n\nB\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_SpansAndLinks_FlowInlineAndTheirAttributesNeverReachTheOutput()
    {
        byte[] package = Odt(
            "<text:p>Plain <text:span text:style-name=\"T1\">bold</text:span> and "
            + "<text:a xlink:type=\"simple\" xlink:href=\"https://example.invalid/HREFMARKER\">linked</text:a> end.</text:p>");

        string text = await TextOfAsync<OpenDocumentTextExtractor>(package);

        Assert.Equal("Plain bold and linked end.\n", text);
    }

    [Fact]
    public async Task Extract_WhitespaceRuns_CollapseToOneSpaceAndLeadingWhitespaceIsDropped()
    {
        // ODF white-space processing: space, tab, CR and LF collapse to a single space; none survive at paragraph start.
        byte[] package = Odt("<text:p>  \t\n  Hello \t\n  world   again</text:p><text:p>&#13;&#10; second</text:p>");

        Assert.Equal("Hello world again\nsecond\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Theory]
    [InlineData("a <text:span> </text:span>b", "a b")]
    [InlineData("a<text:span> </text:span>b", "a b")]
    [InlineData("a <text:span>  b</text:span>", "a b")]
    [InlineData("<text:span> </text:span>a", "a")]
    [InlineData("a <text:a xlink:href=\"x\"> b</text:a>", "a b")]
    [InlineData("a<text:span xml:space=\"preserve\"> </text:span>b", "a b")]
    public async Task Extract_WhitespaceCollapsesAcrossInlineElementBoundaries(string paragraph, string expected)
    {
        byte[] package = Odt($"<text:p>{paragraph}</text:p>");

        Assert.Equal(expected + "\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Theory]
    [InlineData("a<text:s text:c=\"3\"/>b", "a   b")]
    [InlineData("a<text:s/>b", "a b")]
    [InlineData("a<text:s/> b", "a  b")]
    [InlineData("<text:s text:c=\"2\"/>a", "  a")]
    public async Task Extract_TextS_MaterializesExplicitSpacesWhichDoNotCollapse(string paragraph, string expected)
    {
        byte[] package = Odt($"<text:p>{paragraph}</text:p>");

        Assert.Equal(expected + "\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("64", 64)]
    [InlineData("65", 64)]
    [InlineData("1000000", 64)]
    [InlineData("99999999999999999999", 64)]
    [InlineData("0", 1)]
    [InlineData("-3", 1)]
    [InlineData("junk", 1)]
    [InlineData("", 1)]
    public async Task Extract_TextSCount_IsClampedToOneThroughSixtyFour(string count, int expectedSpaces)
    {
        byte[] package = Odt($"<text:p>x<text:s text:c=\"{count}\"/>y</text:p>");

        Assert.Equal("x" + new string(' ', expectedSpaces) + "y\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_TabAndLineBreak_AreMaterialized()
    {
        byte[] package = Odt("<text:p>a<text:tab/>b<text:line-break/>c</text:p>");

        Assert.Equal("a\tb\nc\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_ListsAndTablesAndPrettyPrinting_YieldOnlyParagraphText()
    {
        byte[] package = Odt(
            "\n  <text:list>\n    <text:list-item>\n      <text:p>One</text:p>\n    </text:list-item>\n"
            + "    <text:list-item><text:p>Two</text:p></text:list-item>\n  </text:list>\n"
            + "  <table:table table:name=\"Table1\">\n"
            + "    <table:table-column/>\n"
            + "    <table:table-row>\n"
            + "      <table:table-cell office:value-type=\"string\">\n<text:p>Cell A1</text:p></table:table-cell>\n"
            + "      <table:table-cell><text:p>Cell B1</text:p></table:table-cell>\n"
            + "    </table:table-row>\n"
            + "  </table:table>\n"
            + "  <text:p>After</text:p>\n");

        Assert.Equal("One\nTwo\nCell A1\nCell B1\nAfter\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_TextBoxAnchoredInAParagraph_IsIncludedOnItsOwnLine()
    {
        byte[] package = Odt(
            "<text:p>Before<draw:frame draw:name=\"Frame1\"><draw:text-box><text:p>Boxed</text:p></draw:text-box></draw:frame>After</text:p>");

        Assert.Equal("Before\nBoxed\nAfter\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_ParagraphThatOnlyAnchorsATextBox_DoesNotAddABlankLine()
    {
        byte[] package = Odt("<text:p><draw:frame><draw:text-box><text:p>Boxed</text:p></draw:text-box></draw:frame></text:p><text:p>Next</text:p>");

        Assert.Equal("Boxed\nNext\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_FootnoteIsIncludedInlineWithItsCitationAndBody()
    {
        byte[] package = Odt(
            "<text:p>Body<text:note text:id=\"ftn1\" text:note-class=\"footnote\"><text:note-citation>1</text:note-citation>"
            + "<text:note-body><text:p>Footnote text</text:p></text:note-body></text:note> continues.</text:p>");

        Assert.Equal("Body1\nFootnote text\ncontinues.\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_AttributeValuesAndNonTextNodes_NeverReachTheOutput()
    {
        byte[] package = Odt(
            "<text:sequence-decls><text:sequence-decl text:display-outline-level=\"0\" text:name=\"ATTRMARKER\"/></text:sequence-decls>"
            + "<text:p text:style-name=\"ATTRMARKER\" xml:id=\"ATTRMARKER\">Only this<text:bookmark text:name=\"ATTRMARKER\"/></text:p>");

        string text = await TextOfAsync<OpenDocumentTextExtractor>(package);

        Assert.Equal("Only this\n", text);
    }

    [Fact]
    public async Task Extract_PackageWithNoVisibleText_DoesNotThrowAndYieldsNothing()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync<OpenDocumentTextExtractor>(Odt(string.Empty));

        Assert.Equal(string.Empty, result.Text);
    }

    // ---------------------------------------------------------------- excluded subtrees (plan D8)

    [Fact]
    public async Task Extract_TrackedChanges_DeletedTextIsExcludedAndInsertedTextIsKept()
    {
        // Sensitivity: the text:tracked-changes exclusion. Deleted text lives only inside text:deletion.
        byte[] package = Odt(
            "<text:tracked-changes text:track-changes=\"true\">"
            + "<text:changed-region text:id=\"ct1\"><text:deletion><office:change-info><dc:creator>REVIEWER</dc:creator><dc:date>2026-01-01T00:00:00</dc:date></office:change-info>"
            + "<text:p>DELETEDMARKER</text:p></text:deletion></text:changed-region>"
            + "<text:changed-region text:id=\"ct2\"><text:insertion><office:change-info><dc:creator>REVIEWER</dc:creator><dc:date>2026-01-01T00:00:00</dc:date></office:change-info></text:insertion></text:changed-region>"
            + "</text:tracked-changes>"
            + "<text:p>Kept <text:change text:change-id=\"ct1\"/><text:change-start text:change-id=\"ct2\"/>inserted<text:change-end text:change-id=\"ct2\"/> text</text:p>");

        Assert.Equal("Kept inserted text\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_Annotation_IsExcludedAndTheRestOfTheParagraphIsKept()
    {
        // Sensitivity: the office:annotation exclusion. Nothing after office:annotation-end is special.
        byte[] package = Odt(
            "<text:p>Visible<office:annotation office:name=\"__Annotation__1\"><dc:creator>AUTHORMARKER</dc:creator><dc:date>2026-01-01T00:00:00</dc:date>"
            + "<text:p>ANNOTATIONMARKER</text:p></office:annotation>"
            + " text<office:annotation-end office:name=\"__Annotation__1\"/> after</text:p>");

        Assert.Equal("Visible text after\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Theory]
    [InlineData("variable-set", "none", false)]
    [InlineData("user-field-get", "none", false)]
    [InlineData("variable-get", "condition", false)]
    [InlineData("variable-set", "value", true)]
    [InlineData("variable-get", "formula", true)]
    public async Task Extract_FieldWhoseDisplayIsNone_IsExcluded(string field, string display, bool shown)
    {
        // Sensitivity: the generalised text:display rule. A "display none" variable field keeps its value as content that
        // is never rendered, which is a hiding place just like a hidden section.
        byte[] package = Odt($"<text:p>Lead<text:{field} text:name=\"v\" text:display=\"{display}\">FIELDMARKER</text:{field}></text:p>");

        Assert.Equal(shown ? "LeadFIELDMARKER\n" : "Lead\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_TableCellWhoseStyleHidesText_IsExcluded()
    {
        // Sensitivity: the table-cell family check in the general walker.
        byte[] package = Odt(
            "<table:table table:name=\"T\"><table:table-column/><table:table-row>"
            + "<table:table-cell table:style-name=\"ce1\"><text:p>HIDDENCELLMARKER</text:p></table:table-cell>"
            + "<table:table-cell table:style-name=\"ce2\"><text:p>Shown</text:p></table:table-cell></table:table-row></table:table>",
            automaticStyles: Style("table-cell", "ce1", properties: HiddenText) + Style("table-cell", "ce2"));

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_LegacyTextAnnotation_IsExcluded()
    {
        // Sensitivity: the text:annotation exclusion (the pre-1.2 spelling of a comment).
        byte[] package = Odt("<text:p>Visible<text:annotation><text:p>LEGACYNOTEMARKER</text:p></text:annotation> end</text:p>");

        Assert.Equal("Visible end\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_TableTitleAndDescription_AreExcluded()
    {
        // Sensitivity: the table:title / table:desc exclusion (accessibility text is not rendered).
        byte[] package = Odt(
            "<table:table table:name=\"T\"><table:title><text:p>TABLETITLEMARKER</text:p></table:title><table:desc><text:p>TABLEDESCMARKER</text:p></table:desc>"
            + "<table:table-column/><table:table-row><table:table-cell><text:p>Cell</text:p></table:table-cell></table:table-row></table:table>");

        Assert.Equal("Cell\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_HiddenTextField_IsExcluded()
    {
        // Sensitivity: the text:hidden-text exclusion.
        byte[] package = Odt(
            "<text:p>Shown<text:hidden-text text:condition=\"x\" text:string-value=\"HIDDENTEXTMARKER\" text:is-hidden=\"true\">HIDDENTEXTMARKER</text:hidden-text> end</text:p>");

        Assert.Equal("Shown end\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Theory]
    [InlineData(" text:is-hidden=\"true\"", false)]
    [InlineData("", false)]
    [InlineData(" text:is-hidden=\"anything-else\"", false)]
    [InlineData(" text:is-hidden=\"false\"", true)]
    public async Task Extract_HiddenParagraphField_DropsTheWholeParagraphUnlessItIsExplicitlyNotHidden(string attribute, bool kept)
    {
        // Sensitivity: the text:hidden-paragraph rule and its default-to-hidden posture.
        byte[] package = Odt(
            $"<text:p>Before</text:p><text:p>HPMARKER<text:hidden-paragraph text:condition=\"true\"{attribute}/> tail</text:p><text:p>After</text:p>");

        string expected = kept ? "Before\nHPMARKER tail\nAfter\n" : "Before\nAfter\n";

        Assert.Equal(expected, await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_HiddenParagraphFieldInsideASpan_DropsTheEnclosingParagraph()
    {
        byte[] package = Odt(
            "<text:p>Before</text:p><text:p>HPMARKER <text:span text:style-name=\"T1\"><text:hidden-paragraph text:condition=\"true\" text:is-hidden=\"true\"/></text:span></text:p><text:p>After</text:p>");

        Assert.Equal("Before\nAfter\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_HiddenParagraphFieldInANestedParagraph_DropsOnlyThatParagraph()
    {
        byte[] package = Odt(
            "<text:p>Outer<draw:frame><draw:text-box><text:p>INNERMARKER<text:hidden-paragraph text:condition=\"1\" text:is-hidden=\"true\"/></text:p>"
            + "<text:p>Inner kept</text:p></draw:text-box></draw:frame></text:p>");

        Assert.Equal("Outer\nInner kept\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Theory]
    [InlineData(" text:display=\"none\"", false)]
    [InlineData(" text:display=\"condition\" text:condition=\"ooow:1 == 1\"", false)]
    [InlineData(" text:display=\"true\"", true)]
    [InlineData("", true)]
    public async Task Extract_Section_IsHiddenWhenItsDisplayIsNoneOrCondition(string attribute, bool shown)
    {
        // Sensitivity: the text:section display rule (none and condition are both hidden; true and absent are shown).
        byte[] package = Odt(
            $"<text:p>Before</text:p><text:section text:name=\"Section1\"{attribute}><text:p>SECTIONMARKER</text:p></text:section><text:p>After</text:p>");

        string expected = shown ? "Before\nSECTIONMARKER\nAfter\n" : "Before\nAfter\n";

        Assert.Equal(expected, await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_IndexSourceTemplates_AreExcludedButTheGeneratedIndexBodyIsKept()
    {
        // Sensitivity: the "-source" local-name rule for text-namespace elements.
        byte[] package = Odt(
            "<text:table-of-content text:name=\"Contents1\">"
            + "<text:table-of-content-source text:outline-level=\"3\"><text:index-title-template>TEMPLATEMARKER</text:index-title-template>"
            + "<text:p>SOURCEPARAGRAPHMARKER</text:p></text:table-of-content-source>"
            + "<text:index-body><text:index-title text:name=\"Contents1_Head\"><text:p>Contents</text:p></text:index-title><text:p>Entry one</text:p></text:index-body>"
            + "</text:table-of-content>"
            + "<text:p>Inline<text:future-index-source>INLINESOURCEMARKER</text:future-index-source> end</text:p>");

        Assert.Equal("Contents\nEntry one\nInline end\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_SvgTitleAndDescription_AreExcludedFromAFrame()
    {
        // Sensitivity: the svg:title / svg:desc exclusion (alt text is not rendered).
        byte[] package = Odt(
            "<text:p><draw:frame draw:name=\"Frame1\"><draw:text-box><text:p>Shown</text:p></draw:text-box>"
            + "<svg:title>TITLEMARKER</svg:title><svg:desc>DESCMARKER</svg:desc></draw:frame></text:p>");

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_Forms_AreExcluded()
    {
        // Sensitivity: the office:forms exclusion.
        byte[] package = Odt(
            "<office:forms form:automatic-focus=\"false\"><form:form form:name=\"Standard\"><text:p>FORMMARKER</text:p></form:form></office:forms><text:p>Body</text:p>");

        Assert.Equal("Body\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_ScriptField_IsExcludedAndTheTextAroundItIsKept()
    {
        // Sensitivity: the text:script exclusion. The element's content is script source (or a link to one), which no
        // reader sees as document text, so it is a place to hide instructions.
        byte[] package = Odt(
            "<text:p>Lead<text:script>SCRIPTMARKER</text:script> end</text:p>"
            + "<text:p>Next<text:script xlink:href=\"https://example.invalid/SCRIPTLINKMARKER.js\"><text:p>SCRIPTPARAGRAPHMARKER</text:p></text:script></text:p>");

        Assert.Equal("Lead end\nNext\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_ImageAlternativeTextAndEmbeddedObjects_AreNotProjected()
    {
        byte[] package = Odt(
            "<text:p>Lead"
            + "<draw:frame><draw:image xlink:href=\"Pictures/a.png\"><text:p>IMAGEMARKER</text:p></draw:image></draw:frame>"
            + "<draw:frame><draw:object xlink:href=\"./Object 1\"><office:document><office:body><office:chart><text:p>CHARTMARKER</text:p></office:chart></office:body></office:document></draw:object></draw:frame>"
            + "<draw:frame><draw:image><office:binary-data>BINARYMARKER</office:binary-data></draw:image></draw:frame>"
            + "</text:p>");

        Assert.Equal("Lead\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("printer", false)]
    [InlineData("always", true)]
    [InlineData("screen", true)]
    public async Task Extract_ShapeWithDrawDisplayAttribute_IsHiddenWhenNotShownOnScreen(string display, bool shown)
    {
        // Sensitivity: the draw:display attribute rule on shapes.
        byte[] package = Odt(
            $"<text:p>Lead<draw:custom-shape draw:display=\"{display}\"><text:p>SHAPEMARKER</text:p></draw:custom-shape></text:p>");

        Assert.Equal(shown ? "Lead\nSHAPEMARKER\n" : "Lead\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Theory]
    [InlineData("draw:style-name", "graphic", "<style:graphic-properties draw:display=\"none\"/>")]
    [InlineData("draw:style-name", "graphic", "<style:graphic-properties draw:display=\"printer\"/>")]
    [InlineData("presentation:style-name", "presentation", "<style:graphic-properties draw:display=\"none\"/>")]
    [InlineData("draw:text-style-name", "paragraph", HiddenText)]
    public async Task Extract_ShapeWhoseStyleHidesIt_IsExcluded(string attribute, string family, string properties)
    {
        // Sensitivity: shape hiding resolved through the style table, in the graphic, presentation and paragraph families.
        byte[] package = Odt(
            $"<text:p>Lead<draw:frame {attribute}=\"S1\"><draw:text-box><text:p>SHAPEMARKER</text:p></draw:text-box></draw:frame></text:p>",
            automaticStyles: Style(family, "S1", properties: properties));

        Assert.Equal("Lead\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_CoveredTableCellContent_IsExcluded()
    {
        byte[] package = Odt(
            "<table:table table:name=\"T\"><table:table-column table:number-columns-repeated=\"2\"/><table:table-row>"
            + "<table:table-cell table:number-columns-spanned=\"2\"><text:p>Merged</text:p></table:table-cell>"
            + "<table:covered-table-cell><text:p>COVEREDMARKER</text:p></table:covered-table-cell></table:table-row></table:table>");

        Assert.Equal("Merged\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    // ---------------------------------------------------------------- style-driven hiding (plan D8)

    [Fact]
    public async Task Extract_ParagraphWithAHiddenAutomaticStyle_IsDropped()
    {
        byte[] package = Odt(
            "<text:p>Before</text:p><text:p text:style-name=\"P1\">HIDDENMARKER</text:p><text:h text:style-name=\"P1\" text:outline-level=\"1\">HIDDENHEADING</text:h><text:p text:style-name=\"P2\">Shown</text:p>",
            automaticStyles: Style("paragraph", "P1", properties: HiddenText) + Style("paragraph", "P2"));

        Assert.Equal("Before\nShown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_SpanWithAHiddenAutomaticTextStyle_IsDroppedWhileTheRestOfTheParagraphIsKept()
    {
        byte[] package = Odt(
            "<text:p>Visible <text:span text:style-name=\"T1\">HIDDENMARKER</text:span>text <text:a xlink:href=\"x\" text:style-name=\"T1\">HIDDENLINK</text:a>end</text:p>",
            automaticStyles: Style("text", "T1", properties: HiddenText));

        Assert.Equal("Visible text end\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_HiddenStyleReachedThroughACommonParentChain_IsHonoured()
    {
        // The parent lives in styles.xml (office:styles); the automatic style in content.xml inherits from it.
        byte[] package = Odt(
            "<text:p>Before</text:p><text:p text:style-name=\"P1\">HIDDENMARKER</text:p><text:p>After</text:p>",
            automaticStyles: Style("paragraph", "P1", parent: "Middle"),
            commonStyles: Style("paragraph", "Middle", parent: "Secret") + Style("paragraph", "Secret", properties: HiddenText));

        Assert.Equal("Before\nAfter\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_ExplicitDisplayTrue_OverridesAHiddenParent()
    {
        byte[] package = Odt(
            "<text:p text:style-name=\"P1\">Shown again</text:p><text:p text:style-name=\"P2\">HIDDENMARKER</text:p>",
            automaticStyles: Style("paragraph", "P1", parent: "Secret", properties: VisibleText) + Style("paragraph", "P2", parent: "Secret"),
            commonStyles: Style("paragraph", "Secret", properties: HiddenText));

        Assert.Equal("Shown again\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_StyleDisplayCondition_IsTreatedAsHidden()
    {
        byte[] package = Odt(
            "<text:p text:style-name=\"P1\">CONDITIONMARKER</text:p><text:p>Shown</text:p>",
            automaticStyles: Style("paragraph", "P1", properties: "<style:text-properties text:display=\"condition\" text:condition=\"none\"/>"));

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_StylesDefinedAfterTheBody_AreStillHonoured()
    {
        // A consumer that loads styles first would hide the paragraph; so must this reader, wherever the styles sit.
        byte[] package = Package(
            TextMime,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + $"<office:document-content {Namespaces} office:version=\"1.3\">"
            + "<office:body><office:text><text:p text:style-name=\"P1\">HIDDENMARKER</text:p><text:p>Shown</text:p></office:text></office:body>"
            + $"<office:automatic-styles>{Style("paragraph", "P1", properties: HiddenText)}</office:automatic-styles>"
            + "</office:document-content>");

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_StyleFamilies_AreNotConfused()
    {
        // A span is judged by the text family and a paragraph by the paragraph family, never the other way round.
        byte[] package = Odt(
            "<text:p text:style-name=\"X\">Paragraph<text:span text:style-name=\"X\">span</text:span></text:p>",
            automaticStyles: Style("text", "X", properties: HiddenText) + Style("paragraph", "X"));

        Assert.Equal("Paragraph\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_AutomaticStyle_OverridesACommonStyleOfTheSameName()
    {
        byte[] package = Odt(
            "<text:p text:style-name=\"P1\">Shown</text:p>",
            automaticStyles: Style("paragraph", "P1"),
            commonStyles: Style("paragraph", "P1", properties: HiddenText));

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Extract_DuplicateStyleDefinitions_HiddenWinsInEitherOrder(bool hiddenFirst)
    {
        string hidden = Style("paragraph", "P1", properties: HiddenText);

        string visible = Style("paragraph", "P1", properties: VisibleText);

        byte[] package = Odt(
            "<text:p text:style-name=\"P1\">HIDDENMARKER</text:p><text:p>Shown</text:p>",
            automaticStyles: hiddenFirst ? hidden + visible : visible + hidden);

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_UnknownStyleReference_IsVisible()
    {
        byte[] package = Odt("<text:p text:style-name=\"Nowhere\">Shown</text:p>");

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_HiddenDefaultParagraphStyle_HidesUnstyledParagraphsButNotOnesThatOverrideIt()
    {
        byte[] package = Odt(
            "<text:p>HIDDENMARKER</text:p><text:p text:style-name=\"P1\">Shown</text:p>",
            automaticStyles: Style("paragraph", "P1", properties: VisibleText),
            commonStyles: "<style:default-style style:family=\"paragraph\"><style:text-properties text:display=\"none\"/></style:default-style>");

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_HiddenStandardParagraphStyle_HidesUnstyledParagraphs()
    {
        // Writer applies the "Standard" paragraph style to a paragraph that names none.
        byte[] package = Odt(
            "<text:p>HIDDENMARKER</text:p><text:p text:style-name=\"P1\">Shown</text:p>",
            automaticStyles: Style("paragraph", "P1", properties: VisibleText),
            commonStyles: Style("paragraph", "Standard", properties: HiddenText));

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_AutomaticStylesOfStylesXml_DoNotApplyToContent()
    {
        // styles.xml automatic styles belong to master pages; a same-named hidden style there must not hide content.
        byte[] package = Package(
            TextMime,
            ContentPart("text", "<text:p text:style-name=\"P1\">Shown</text:p>"),
            StylesPart(string.Empty, automaticStyles: Style("paragraph", "P1", properties: HiddenText)));

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_StylesPartThatTheManifestDoesNotDeclare_IsNeverRead()
    {
        // Sensitivity: styles.xml is read only when manifest.Declares("styles.xml"). The undeclared part is malformed
        // and would hide the paragraph if it were parsed.
        byte[] package = Package(
            TextMime,
            ContentPart("text", "<text:p text:style-name=\"P1\">Shown</text:p>"),
            null,
            string.Empty,
            ("styles.xml", "<office:document-styles><unclosed>"));

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    // ---------------------------------------------------------------- text nodes

    [Fact]
    public async Task Extract_CharacterReferencesAndCData_AreDecodedToPlainText()
    {
        byte[] package = Odt("<text:p>a &amp; b &lt;c&gt; &#x2603; <![CDATA[x < y & z]]></text:p>");

        Assert.Equal("a & b <c> ☃ x < y & z\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_XmlSpacePreserve_DoesNotChangeOdfWhitespaceProcessing()
    {
        byte[] package = Odt("<text:p xml:space=\"preserve\">  a   b </text:p>");

        Assert.Equal("a b \n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_TextOutsideAnyParagraph_IsIgnored()
    {
        byte[] package = Odt(
            "STRAYTEXT<text:s/><text:tab/><text:line-break/><text:list>LISTSTRAY<text:list-item>ITEMSTRAY<text:p>Real</text:p></text:list-item></text:list>");

        Assert.Equal("Real\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_ParagraphCutAtTheRetentionBound_LeavesNoLoneSurrogateEvenForASinkThatDoesNotTrim()
    {
        RecordingTextSink sink = new();

        await new OpenDocumentTextExtractor().ExtractAsync(
            Odt($"<text:p>{new string('x', 262_143)}\U0001F600tail</text:p>"),
            sink,
            CancellationToken.None);

        Assert.True(sink.Truncated);

        Assert.Equal(262_143, sink.Text.Length);

        Assert.False(char.IsHighSurrogate(sink.Text[^1]));
    }

    [Fact]
    public async Task Extract_ParagraphCutAtTheRetentionBound_NeverLeavesALoneSurrogate()
    {
        // 262,143 characters, then a surrogate pair that straddles the 262,144 bound. The sink here is larger than the
        // bound, so only the extractor can keep the output well-formed.
        string text = new string('x', 262_143) + "\U0001F600tail";

        SessionAttachmentExtractionResult result = await ExtractAsync<OpenDocumentTextExtractor>(Odt($"<text:p>{text}</text:p>"), capacity: 1_000_000);

        Assert.True(result.WasTruncated);

        Assert.Equal(262_143, result.Text.Length);

        Assert.False(char.IsHighSurrogate(result.Text[^1]));
    }
}
