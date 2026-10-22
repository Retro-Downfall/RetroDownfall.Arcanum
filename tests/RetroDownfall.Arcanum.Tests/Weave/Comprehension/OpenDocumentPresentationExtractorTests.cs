using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.OpenDocumentFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class OpenDocumentPresentationExtractorTests
{
    private const string HiddenPage = "<style:drawing-page-properties presentation:visibility=\"hidden\"/>";

    private const string VisiblePage = "<style:drawing-page-properties presentation:visibility=\"visible\"/>";

    // ---------------------------------------------------------------- projection (plan D9)

    [Fact]
    public async Task Extract_Slides_AreNumberedByPositionAndTheirShapesProjectAsParagraphs()
    {
        byte[] package = Odp(Page(Frame("Title") + Frame("Body")) + Page(Shape("Second")));

        Assert.Equal("Slide 1\nTitle\nBody\nSlide 2\nSecond\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_SlideWithNoText_StillGetsItsHeader()
    {
        byte[] package = Odp(Page(string.Empty) + Page(Frame("Text")));

        Assert.Equal("Slide 1\nSlide 2\nText\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_GroupsTablesAndMultiParagraphShapes_AreWalkedInDocumentOrder()
    {
        byte[] package = Odp(
            Page(
                "<draw:g><draw:frame><draw:text-box><text:p>One</text:p><text:p>Two</text:p></draw:text-box></draw:frame>"
                + "<draw:custom-shape><text:p>Three</text:p></draw:custom-shape></draw:g>"
                + "<draw:frame><table:table table:name=\"T\"><table:table-column/><table:table-row>"
                + "<table:table-cell><text:p>C1</text:p></table:table-cell><table:table-cell><text:p>C2</text:p></table:table-cell></table:table-row></table:table></draw:frame>"
                + Frame("Four")));

        Assert.Equal("Slide 1\nOne\nTwo\nThree\nC1\tC2\nFour\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_AttributeValuesLikePageAndShapeNames_NeverReachTheOutput()
    {
        byte[] package = Odp(
            "<draw:page draw:name=\"NAMEMARKER\" draw:id=\"IDMARKER\" draw:master-page-name=\"MASTERMARKER\">"
            + "<draw:frame draw:name=\"SHAPENAMEMARKER\" svg:width=\"1cm\"><draw:text-box><text:p>Only this</text:p></draw:text-box></draw:frame></draw:page>");

        Assert.Equal("Slide 1\nOnly this\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_PresentationLevelDeclarations_AreNotProjected()
    {
        byte[] package = Odp(
            "<presentation:header-decl presentation:name=\"hdr1\"><text:p>HEADERDECLMARKER</text:p></presentation:header-decl>"
            + "<presentation:footer-decl presentation:name=\"ftr1\"><text:p>FOOTERDECLMARKER</text:p></presentation:footer-decl>"
            + "<presentation:date-time-decl presentation:name=\"dtd1\" presentation:source=\"fixed\"><text:p>DATEDECLMARKER</text:p></presentation:date-time-decl>"
            + "<presentation:settings presentation:full-screen=\"true\"><presentation:show presentation:name=\"SHOWMARKER\" presentation:pages=\"1\"/></presentation:settings>"
            + Page(Frame("Text")));

        Assert.Equal("Slide 1\nText\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_ElementNamedPageInAnotherNamespace_IsNotASlide()
    {
        // Sensitivity: slides are matched by local name plus the drawing namespace, never by local name alone.
        byte[] package = Odp(
            "<x:page xmlns:x=\"urn:elsewhere\"><draw:frame><draw:text-box><text:p>FOREIGNMARKER</text:p></draw:text-box></draw:frame></x:page>"
            + Page(Frame("Real")));

        Assert.Equal("Slide 1\nReal\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_MasterPageContentInStylesXml_IsNotProjected()
    {
        string styles = StylesPart(string.Empty).Replace(
            "<office:master-styles/>",
            "<office:master-styles><style:master-page style:name=\"Default\"><draw:frame><draw:text-box><text:p>MASTERPAGEMARKER</text:p></draw:text-box></draw:frame></style:master-page></office:master-styles>",
            StringComparison.Ordinal);

        byte[] package = Package(PresentationMime, ContentPart("presentation", Page(Frame("Slide text"))), styles);

        Assert.Equal("Slide 1\nSlide text\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_FrameWithTwoTextBoxes_ProjectsOnlyTheFirst()
    {
        // The children of a frame are alternative representations of one object; only the first is rendered.
        byte[] package = Odp(
            "<draw:page draw:name=\"p1\"><draw:frame><draw:text-box><text:p>Shown</text:p></draw:text-box>"
            + "<draw:text-box><text:p>SECONDBOXMARKER</text:p></draw:text-box></draw:frame></draw:page>");

        Assert.Equal("Slide 1\nShown\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_FrameWhoseFirstRepresentationIsAnImage_ProjectsNoTextFromALaterTextBox()
    {
        byte[] package = Odp(
            Page("<draw:frame><draw:image xlink:href=\"Pictures/a.png\"/><draw:text-box><text:p>ALTERNATIVEMARKER</text:p></draw:text-box></draw:frame>" + Frame("Real")));

        Assert.Equal("Slide 1\nReal\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_TableRows_ProjectTheirCellsJoinedByTabsEachRowOnItsOwnLine()
    {
        byte[] package = Odp(
            Page(
                "<draw:frame><table:table table:name=\"T\"><table:table-column table:number-columns-repeated=\"3\"/>"
                + "<table:table-row><table:table-cell><text:p>Name</text:p></table:table-cell><table:table-cell><text:p>Role</text:p></table:table-cell><table:table-cell><text:p>Score</text:p></table:table-cell></table:table-row>"
                + "<table:table-row><table:table-cell><text:p>Alice</text:p></table:table-cell><table:table-cell><text:p>Engineer</text:p><text:p>Lead</text:p></table:table-cell><table:table-cell><text:p>91</text:p></table:table-cell></table:table-row>"
                + "</table:table></draw:frame>"
                + Frame("After")));

        Assert.Equal("Slide 1\nName\tRole\tScore\nAlice\tEngineer Lead\t91\nAfter\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_NotesInShapes_FollowTheLastSlide()
    {
        byte[] package = Odp(
            Page("<draw:frame><draw:text-box><text:p>One<text:note text:note-class=\"footnote\"><text:note-citation>1</text:note-citation><text:note-body><text:p>Note text</text:p></text:note-body></text:note></text:p></draw:text-box></draw:frame>")
            + Page(Frame("Two")));

        Assert.Equal("Slide 1\nOne\nSlide 2\nTwo\nNote text\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    // ---------------------------------------------------------------- hidden content (plan D8)

    [Fact]
    public async Task Extract_HiddenSlide_IsExcludedButStillCountsInTheNumbering()
    {
        // Sensitivity: the drawing-page style rule, and that hidden pages still advance n.
        byte[] package = Odp(
            Page(Frame("one"))
            + Page(Frame("HIDDENSLIDE"), " draw:style-name=\"dp2\"")
            + Page(Frame("three"), " draw:style-name=\"dp1\"")
            + Page(Frame("HIDDENDERIVED"), " draw:style-name=\"dp3\"")
            + Page(Frame("five"))
            + Page(Frame("six"), " draw:style-name=\"dp4\""),
            automaticStyles: Style("drawing-page", "dp1", properties: VisiblePage)
                + Style("drawing-page", "dp3", parent: "dp2")
                + Style("drawing-page", "dp4", parent: "dp2", properties: VisiblePage),
            commonStyles: Style("drawing-page", "dp2", properties: HiddenPage));

        // Page 6 derives from the hidden style but states presentation:visibility="visible" itself, which wins.
        Assert.Equal("Slide 1\none\nSlide 3\nthree\nSlide 5\nfive\nSlide 6\nsix\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_HiddenSlideThroughACommonStyle_IsExcluded()
    {
        byte[] package = Odp(
            Page(Frame("HIDDENSLIDE"), " draw:style-name=\"dp1\"") + Page(Frame("two")),
            automaticStyles: Style("drawing-page", "dp1", parent: "Hidden"),
            commonStyles: Style("drawing-page", "Hidden", properties: HiddenPage));

        Assert.Equal("Slide 2\ntwo\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_SpeakerNotes_AreExcluded()
    {
        // Sensitivity: the presentation:notes exclusion.
        byte[] package = Odp(
            Page(
                Frame("Slide text")
                + "<presentation:notes draw:style-name=\"dp3\"><draw:page-thumbnail/><draw:frame presentation:class=\"notes\"><draw:text-box><text:p>NOTESMARKER</text:p></draw:text-box></draw:frame></presentation:notes>"));

        Assert.Equal("Slide 1\nSlide text\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("printer", false)]
    [InlineData("always", true)]
    public async Task Extract_ShapeWithDrawDisplayAttribute_IsHiddenWhenNotShownOnScreen(string display, bool shown)
    {
        byte[] package = Odp(Page(Frame("Visible") + $"<draw:custom-shape draw:display=\"{display}\"><text:p>SHAPEMARKER</text:p></draw:custom-shape>"));

        Assert.Equal(shown ? "Slide 1\nVisible\nSHAPEMARKER\n" : "Slide 1\nVisible\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Theory]
    [InlineData("draw:style-name", "graphic")]
    [InlineData("presentation:style-name", "presentation")]
    public async Task Extract_ShapeWhoseStyleHidesIt_IsExcluded(string attribute, string family)
    {
        byte[] package = Odp(
            Page(Frame("Visible") + $"<draw:frame {attribute}=\"S1\"><draw:text-box><text:p>SHAPEMARKER</text:p></draw:text-box></draw:frame>"),
            automaticStyles: Style(family, "S1", properties: "<style:graphic-properties draw:display=\"none\"/>"));

        Assert.Equal("Slide 1\nVisible\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Theory]
    [InlineData("secret", false)]
    [InlineData("printonly", false)]
    [InlineData("screenonly", true)]
    [InlineData("layout", true)]
    [InlineData("undeclared", true)]
    public async Task Extract_ShapeOnAHiddenLayer_IsExcluded(string layer, bool shown)
    {
        // Sensitivity: layer visibility from draw:layer-set (a layer that is not shown on screen hides its shapes).
        string styles = StylesPart(string.Empty).Replace(
            "<office:master-styles/>",
            "<office:master-styles><draw:layer-set><draw:layer draw:name=\"layout\"/><draw:layer draw:name=\"secret\" draw:display=\"none\"/>"
            + "<draw:layer draw:name=\"printonly\" draw:display=\"printer\"/><draw:layer draw:name=\"screenonly\" draw:display=\"screen\"/></draw:layer-set></office:master-styles>",
            StringComparison.Ordinal);

        byte[] package = Package(
            PresentationMime,
            ContentPart("presentation", Page($"<draw:frame draw:layer=\"{layer}\"><draw:text-box><text:p>LAYERMARKER</text:p></draw:text-box></draw:frame>")),
            styles);

        Assert.Equal(shown ? "Slide 1\nLAYERMARKER\n" : "Slide 1\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_LayerSetInContentXml_IsHonouredToo()
    {
        string content = ContentPart("presentation", Page("<draw:frame draw:layer=\"secret\"><draw:text-box><text:p>LAYERMARKER</text:p></draw:text-box></draw:frame>"))
            .Replace("<office:body>", "<office:master-styles><draw:layer-set><draw:layer draw:name=\"secret\" draw:display=\"none\"/></draw:layer-set></office:master-styles><office:body>", StringComparison.Ordinal);

        Assert.Equal("Slide 1\n", await TextOfAsync<OpenDocumentPresentationExtractor>(Package(PresentationMime, content)));
    }

    [Fact]
    public async Task Extract_AlternativeTextAnnotationsAndEmbeddedObjects_AreNotProjected()
    {
        byte[] package = Odp(
            Page(
                "<draw:frame><draw:text-box><text:p>Shown</text:p></draw:text-box><svg:title>TITLEMARKER</svg:title><svg:desc>DESCMARKER</svg:desc></draw:frame>"
                + "<draw:frame><draw:image xlink:href=\"Pictures/a.png\"><text:p>IMAGEMARKER</text:p></draw:image><svg:desc>IMAGEDESCMARKER</svg:desc></draw:frame>"
                + "<draw:frame><draw:object xlink:href=\"./Object 1\"><office:document><office:body><office:chart><text:p>CHARTMARKER</text:p></office:chart></office:body></office:document></draw:object></draw:frame>"
                + "<office:annotation><dc:creator>AUTHORMARKER</dc:creator><text:p>COMMENTMARKER</text:p></office:annotation>"
                + "<office:forms><form:form><text:p>FORMMARKER</text:p></form:form></office:forms>"));

        Assert.Equal("Slide 1\nShown\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_HiddenTextOnASlide_IsExcluded()
    {
        byte[] package = Odp(
            Page(
                "<draw:frame><draw:text-box><text:p>Shown<text:span text:style-name=\"T1\">HIDDENMARKER</text:span><text:hidden-text text:condition=\"x\">HIDDENFIELD</text:hidden-text></text:p>"
                + "<text:p text:style-name=\"P1\">HIDDENPARAGRAPH</text:p></draw:text-box></draw:frame>"),
            automaticStyles: Style("text", "T1", properties: HiddenText) + Style("paragraph", "P1", properties: HiddenText));

        Assert.Equal("Slide 1\nShown\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_CoveredTableCellOnASlide_IsExcluded()
    {
        byte[] package = Odp(
            Page(
                "<draw:frame><table:table><table:table-row><table:table-cell table:number-columns-spanned=\"2\"><text:p>Merged</text:p></table:table-cell>"
                + "<table:covered-table-cell><text:p>COVEREDMARKER</text:p></table:covered-table-cell></table:table-row></table:table></draw:frame>"));

        Assert.Equal("Slide 1\nMerged\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    // ---------------------------------------------------------------- bounds and format checks

    [Fact]
    public async Task Extract_StopsWritingAfterTheSinkRefusesText()
    {
        SpyTextSink sink = new(acceptedCalls: 4);

        string pages = string.Concat(Enumerable.Range(0, 50).Select(index => Page(Frame($"text {index}"))));

        await new OpenDocumentPresentationExtractor().ExtractAsync(Odp(pages), sink, CancellationToken.None);

        Assert.Equal(5, sink.Calls);
    }

    [Fact]
    public async Task Extract_ManySlides_AreTruncatedAtTheSinkCeiling()
    {
        string pages = string.Concat(Enumerable.Range(0, 200).Select(index => Page(Frame($"text {index}"))));

        SessionAttachmentExtractionResult result = await ExtractAsync<OpenDocumentPresentationExtractor>(Odp(pages), capacity: 100);

        Assert.True(result.WasTruncated);

        Assert.Equal(100, result.Text.Length);

        Assert.StartsWith("Slide 1\ntext 0\nSlide 2\ntext 1\n", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TextMime)]
    [InlineData(SpreadsheetMime)]
    public async Task Extract_PackageOfAnotherOpenDocumentFormat_IsFormatMismatch(string mimeType)
    {
        byte[] package = Package(mimeType, ContentPart("text", "<text:p>x</text:p>"));

        await RejectedAsync<OpenDocumentPresentationExtractor>(package, DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_PresentationManifestWithAnotherBody_IsFormatMismatch()
    {
        byte[] package = Package(PresentationMime, ContentPart("text", "<text:p>x</text:p>"));

        await RejectedAsync<OpenDocumentPresentationExtractor>(package, DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_EncryptedPresentation_IsEncrypted()
    {
        string manifest = ManifestPart(PresentationMime, declareStyles: false)
            .Replace("</manifest:manifest>", "<manifest:file-entry manifest:full-path=\"Pictures/a.png\"><manifest:encryption-data/></manifest:file-entry></manifest:manifest>", StringComparison.Ordinal);

        byte[] package = ComprehensionPackageBuilder.Build(
            ("META-INF/manifest.xml", manifest),
            ("content.xml", ContentPart("presentation", Page(Frame("x")))));

        await RejectedAsync<OpenDocumentPresentationExtractor>(package, DocumentPackageRejection.Encrypted);
    }

    [Fact]
    public async Task Extract_ManifestWithTheTextutilDocumentType_IsAccepted()
    {
        byte[] package = Odp(Page(Frame("v")), documentType: TextutilDocumentType);

        Assert.Equal("Slide 1\nv\n", await TextOfAsync<OpenDocumentPresentationExtractor>(package));
    }

    [Fact]
    public async Task Extract_UnreferencedPartsAndEmbeddedBombs_AreNeverOpened()
    {
        RawZipEntry embedding = RawZipEntry.Deflated("Object 1/content.xml", "placeholder");

        embedding.DataAreaBytes = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

        embedding.UncompressedSize = 30_000_000;

        RawZipPackage raw = RawZipPackage.Of(
            RawZipEntry.Stored("mimetype", PresentationMime),
            RawZipEntry.Deflated("META-INF/manifest.xml", ManifestPart(PresentationMime, declareStyles: false)),
            RawZipEntry.Deflated("content.xml", ContentPart("presentation", Page(Frame("Fine")))),
            RawZipEntry.Deflated("Pictures/smuggled.xml", "<<<not xml"),
            embedding);

        Assert.Equal("Slide 1\nFine\n", await TextOfAsync<OpenDocumentPresentationExtractor>(raw.ToBytes()));
    }

    // ---------------------------------------------------------------- markup helpers

    private static string Page(string content, string attributes = "") =>
        $"<draw:page draw:name=\"page1\" draw:master-page-name=\"Default\"{attributes}>{content}</draw:page>";

    private static string Frame(string text) =>
        $"<draw:frame presentation:class=\"outline\" draw:layer=\"layout\" svg:width=\"20cm\"><draw:text-box><text:p>{text}</text:p></draw:text-box></draw:frame>";

    private static string Shape(string text) => $"<draw:custom-shape draw:layer=\"layout\"><text:p>{text}</text:p></draw:custom-shape>";
}
