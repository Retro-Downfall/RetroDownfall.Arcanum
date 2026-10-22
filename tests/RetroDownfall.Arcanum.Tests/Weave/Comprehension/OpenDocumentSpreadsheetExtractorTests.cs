using System.Diagnostics;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.OpenDocumentFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class OpenDocumentSpreadsheetExtractorTests
{
    // ---------------------------------------------------------------- projection (plan D9)

    [Fact]
    public async Task Extract_SheetsAndRows_ProjectAsASheetHeaderThenTabSeparatedRows()
    {
        byte[] package = Ods(
            Sheet("Data", Columns(3) + Row(Cell("a") + Cell("b") + Cell("c")) + Row(Cell("d") + Empty() + Cell("f")))
            + Sheet("Other", Row(Cell("x"))));

        Assert.Equal("Sheet: Data\na\tb\tc\nd\tf\nSheet: Other\nx\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_EmptyAndBlankCells_AreSkippedAndASheetWithNothingToShowHasNoHeader()
    {
        byte[] package = Ods(
            Sheet("Blank", Row(Empty() + Empty()) + Row(Cell("  ")) + Row(string.Empty))
            + Sheet("Real", Row(Cell("v"))));

        Assert.Equal("Sheet: Real\nv\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_CellWithSeveralParagraphs_JoinsThemWithASingleSpace()
    {
        byte[] package = Ods(
            Sheet(
                "S",
                Row(
                    "<table:table-cell office:value-type=\"string\"><text:p>Line one</text:p><text:p/><text:p>Line   two</text:p></table:table-cell>"
                    + Cell("next"))));

        Assert.Equal("Sheet: S\nLine one Line two\tnext\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_TabsAndLineBreaksInsideACell_BecomeSpacesSoTheRowStaysOneLine()
    {
        byte[] package = Ods(Sheet("S", Row(Cell("a<text:tab/>b<text:line-break/>c") + Cell("d"))));

        Assert.Equal("Sheet: S\na b c\td\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_ListAndTextBoxInsideACell_AreJoinedIntoTheCellText()
    {
        byte[] package = Ods(
            Sheet(
                "S",
                Row(
                    "<table:table-cell><text:list><text:list-item><text:p>One</text:p></text:list-item><text:list-item><text:p>Two</text:p></text:list-item></text:list>"
                    + "<draw:frame><draw:text-box><text:p>Boxed</text:p></draw:text-box></draw:frame></table:table-cell>")));

        Assert.Equal("Sheet: S\nOne Two Boxed\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_Formulas_AreNeverEmittedOnlyTheirCachedValue()
    {
        // Sensitivity: attribute values (table:formula) never reach the output; the cached text:p does.
        byte[] package = Ods(
            Sheet(
                "S",
                Row(
                    "<table:table-cell table:formula=\"of:=SUM([.A1:.A2])FORMULAMARKER\" office:value-type=\"float\" office:value=\"VALUEMARKER\"><text:p>3</text:p></table:table-cell>"
                    + "<table:table-cell table:formula=\"of:=1+1FORMULAMARKER\" office:value-type=\"float\" office:value=\"VALUEMARKER\"/>")));

        Assert.Equal("Sheet: S\n3\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    // ---------------------------------------------------------------- hidden structure (plan D8)

    [Fact]
    public async Task Extract_SheetWhoseTableStyleIsNotDisplayed_IsExcluded()
    {
        // Sensitivity: the family-table style rule (table:display="false").
        byte[] package = Ods(
            Sheet("Hidden", Row(Cell("HIDDENSHEET")), " table:style-name=\"ta2\"")
            + Sheet("Shown", Row(Cell("v")), " table:style-name=\"ta1\"")
            + Sheet("Derived", Row(Cell("HIDDENDERIVED")), " table:style-name=\"ta3\""),
            automaticStyles: Style("table", "ta1", properties: "<style:table-properties table:display=\"true\"/>")
                + Style("table", "ta2", properties: "<style:table-properties table:display=\"false\"/>")
                + Style("table", "ta3", parent: "ta2"));

        Assert.Equal("Sheet: Shown\nv\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Theory]
    [InlineData("collapse", false)]
    [InlineData("filter", false)]
    [InlineData("visible", true)]
    public async Task Extract_RowVisibility_CollapseAndFilterAreHidden(string visibility, bool shown)
    {
        // Sensitivity: the table:visibility rule on rows.
        byte[] package = Ods(
            Sheet("S", Row(Cell("keep")) + Row(Cell("ROWMARKER"), $" table:visibility=\"{visibility}\"") + Row(Cell("after"))));

        Assert.Equal(shown ? "Sheet: S\nkeep\nROWMARKER\nafter\n" : "Sheet: S\nkeep\nafter\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_RepeatedRow_IsEmittedOnce()
    {
        byte[] package = Ods(Sheet("S", Row(Cell("rep"), " table:number-rows-repeated=\"3\"") + Row(Cell("next"))));

        Assert.Equal("Sheet: S\nrep\nnext\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_RowGroupsAndHeaderRows_AreWalkedAndACollapsedGroupIsNot()
    {
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:table-header-rows>" + Row(Cell("header")) + "</table:table-header-rows>"
                + "<table:table-row-group>" + Row(Cell("grouped")) + "</table:table-row-group>"
                + "<table:table-row-group table:display=\"false\">" + Row(Cell("GROUPMARKER")) + "</table:table-row-group>"
                + "<table:table-rows>" + Row(Cell("plain")) + "</table:table-rows>"));

        Assert.Equal("Sheet: S\nheader\ngrouped\nplain\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_CellsInCollapsedAndFilteredColumns_AreExcluded()
    {
        // Sensitivity: the table:visibility rule on columns, with repeated column elements stored as ranges.
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:table-column/>"
                + "<table:table-column table:visibility=\"collapse\" table:number-columns-repeated=\"2\"/>"
                + "<table:table-column table:visibility=\"visible\"/>"
                + "<table:table-column table:visibility=\"filter\"/>"
                + Row(Cell("A") + Cell("HIDDENB") + Cell("HIDDENC") + Cell("D") + Cell("HIDDENE") + Cell("F"))));

        Assert.Equal("Sheet: S\nA\tD\tF\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_RepeatedCell_IsEmittedOnceWhenAnyColumnItCoversIsVisible()
    {
        // Columns: 0 visible, 1-2 hidden, 3 visible, 4 hidden. The repeated cell covers 1-3 (3 is visible); Z sits in 4.
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:table-column/><table:table-column table:visibility=\"collapse\" table:number-columns-repeated=\"2\"/><table:table-column/><table:table-column table:visibility=\"collapse\"/>"
                + Row(Cell("A") + Cell("R", " table:number-columns-repeated=\"3\"") + Cell("HIDDENZ"))));

        Assert.Equal("Sheet: S\nA\tR\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_RepeatedCellThatCoversOnlyHiddenColumns_IsExcluded()
    {
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:table-column/><table:table-column table:visibility=\"collapse\" table:number-columns-repeated=\"2\"/>"
                + Row(Cell("A") + Cell("HIDDENMARKER", " table:number-columns-repeated=\"2\""))));

        Assert.Equal("Sheet: S\nA\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_RepeatedCell_AdvancesTheColumnIndexOfTheCellsAfterIt()
    {
        // Columns: 0 visible, 1 hidden, 2 visible. A covers 0-1; B must land in column 2 (visible), not column 1.
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:table-column/><table:table-column table:visibility=\"collapse\"/><table:table-column/>"
                + Row(Cell("A", " table:number-columns-repeated=\"2\"") + Cell("B"))));

        Assert.Equal("Sheet: S\nA\tB\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_CoveredCells_AreExcludedButStillAdvanceTheColumnIndex()
    {
        // Columns: 0-1 visible, 2 hidden. The covered cell occupies column 1, so X is in column 2 and is hidden.
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:table-column table:number-columns-repeated=\"2\"/><table:table-column table:visibility=\"collapse\"/>"
                + Row(Cell("A") + Covered("COVEREDMARKER") + Cell("HIDDENX"))));

        Assert.Equal("Sheet: S\nA\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_RepeatedCoveredCell_AdvancesTheColumnIndexByItsRepeatCount()
    {
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:table-column table:number-columns-repeated=\"3\"/><table:table-column table:visibility=\"collapse\"/><table:table-column/>"
                + Row(Cell("A") + Covered("COVEREDMARKER", " table:number-columns-repeated=\"2\"") + Cell("HIDDENX") + Cell("Y"))));

        Assert.Equal("Sheet: S\nA\tY\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_CollapsedColumnGroup_HidesItsColumns()
    {
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:table-column-group table:display=\"false\"><table:table-column table:number-columns-repeated=\"2\"/></table:table-column-group>"
                + "<table:table-column-group><table:table-column/></table:table-column-group>"
                + "<table:table-header-columns><table:table-column/></table:table-header-columns>"
                + Row(Cell("HIDDEN1") + Cell("HIDDEN2") + Cell("C") + Cell("D"))));

        Assert.Equal("Sheet: S\nC\tD\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_ColumnRepeatCountBeyondAnyRealisticValue_SaturatesInsteadOfOverflowing()
    {
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:table-column/><table:table-column table:visibility=\"collapse\" table:number-columns-repeated=\"99999999999999999999\"/>"
                + "<table:table-column table:visibility=\"collapse\" table:number-columns-repeated=\"99999999999999999999\"/>"
                + Row(Cell("A") + Cell("HIDDENMARKER"))));

        Assert.Equal("Sheet: S\nA\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_CellsBeyondTheLastRealColumn_AreExcluded()
    {
        // Sensitivity: column indexes past 16,384 are not columns of the sheet.
        byte[] package = Ods(
            Sheet(
                "S",
                Row(Empty(" table:number-columns-repeated=\"16384\"") + Cell("BEYONDMARKER"))
                + Row(Empty(" table:number-columns-repeated=\"16383\"") + Cell("LAST"))
                + Row(Empty(" table:number-columns-repeated=\"16383\"") + Cell("EDGE", " table:number-columns-repeated=\"2\""))));

        Assert.Equal("Sheet: S\nLAST\nEDGE\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_RowWhoseCellsAreAllHidden_ProducesNoRowAndNoHeader()
    {
        byte[] package = Ods(
            Sheet("S", "<table:table-column table:visibility=\"collapse\"/>" + Row(Cell("HIDDENMARKER"))));

        Assert.Equal(string.Empty, (await ExtractAsync<OpenDocumentSpreadsheetExtractor>(package)).Text);
    }

    [Fact]
    public async Task Extract_ColumnAfterARow_IsFormatMismatch()
    {
        // A late column definition could hide cells that were already read; a consumer might honour it, so refuse.
        byte[] package = Ods(Sheet("S", Row(Cell("a")) + "<table:table-column table:visibility=\"collapse\"/>"));

        await RejectedAsync<OpenDocumentSpreadsheetExtractor>(package, DocumentPackageRejection.FormatMismatch);
    }

    // ---------------------------------------------------------------- other hidden and non-rendered content

    [Fact]
    public async Task Extract_CellAnnotationsAndAlternativeText_AreExcluded()
    {
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:title>TITLEMARKER</table:title><table:desc>DESCMARKER</table:desc>"
                + Row(
                    "<table:table-cell office:value-type=\"string\"><office:annotation><dc:creator>AUTHORMARKER</dc:creator><text:p>NOTEMARKER</text:p></office:annotation><text:p>Visible</text:p></table:table-cell>")));

        Assert.Equal("Sheet: S\nVisible\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_NonSheetChildrenOfTheSpreadsheet_AreNeverProjected()
    {
        byte[] package = Ods(
            "<table:calculation-settings table:case-sensitive=\"false\"/>"
            + "<table:content-validations><table:content-validation table:name=\"val1\"><table:error-message table:title=\"T\"><text:p>VALIDATIONMARKER</text:p></table:error-message></table:content-validation></table:content-validations>"
            + "<table:tracked-changes><table:cell-content-change><text:p>CHANGEMARKER</text:p></table:cell-content-change></table:tracked-changes>"
            + "<table:named-expressions><table:named-range table:name=\"NAMEMARKER\"/></table:named-expressions>"
            + Sheet("S", Row(Cell("v"))));

        Assert.Equal("Sheet: S\nv\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_HiddenTextStylesInsideCells_AreHonoured()
    {
        byte[] package = Ods(
            Sheet(
                "S",
                Row(
                    "<table:table-cell><text:p>Shown <text:span text:style-name=\"T1\">HIDDENMARKER</text:span></text:p><text:p text:style-name=\"P1\">HIDDENPARAGRAPH</text:p></table:table-cell>")),
            automaticStyles: Style("text", "T1", properties: HiddenText) + Style("paragraph", "P1", properties: HiddenText));

        Assert.Equal("Sheet: S\nShown\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_ShapesOnASheet_AreIncludedAfterTheHeader()
    {
        byte[] package = Ods(
            Sheet(
                "S",
                "<table:shapes><draw:frame><draw:text-box><text:p>Floating note</text:p></draw:text-box></draw:frame>"
                + "<draw:frame draw:display=\"none\"><draw:text-box><text:p>HIDDENSHAPE</text:p></draw:text-box></draw:frame></table:shapes>"
                + Row(Cell("v")))
            + Sheet("OnlyShapes", "<table:shapes><draw:custom-shape><text:p>Alone</text:p></draw:custom-shape></table:shapes>"));

        Assert.Equal("Sheet: S\nFloating note\nv\nSheet: OnlyShapes\nAlone\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Theory]
    [InlineData("Plain", "Plain")]
    [InlineData("", "")]
    [InlineData("Tab&#9;and&#10;newline", "Tab and newline")]
    [InlineData("Line&#13;&#10;Sheet: Fake", "Line  Sheet: Fake")]
    [InlineData("Ünïcode ✓", "Ünïcode ✓")]
    public async Task Extract_SheetName_HasControlCharactersReplacedBySpaces(string rawName, string expectedName)
    {
        byte[] package = Ods(Sheet(rawName, Row(Cell("v"))));

        Assert.Equal($"Sheet: {expectedName}\nv\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    // ---------------------------------------------------------------- bounds (plan D5)

    [Fact]
    public async Task Extract_RepeatedCellsAndRows_AreBoundedOnTimeOutputAndAllocation()
    {
        // The acceptance fixture: a non-empty cell repeated 16,384 times inside a row repeated 1,048,576 times, with a
        // handful of ordinary cells. Repeats are arithmetic on indexes: the text appears once and nothing is multiplied.
        byte[] package = Ods(
            Sheet(
                "Big",
                Row(
                    Cell("a") + Cell("b") + Cell("c")
                    + Cell("REPEATED", " table:number-columns-repeated=\"16384\"")
                    + Cell("beyond"),
                    " table:number-rows-repeated=\"1048576\"")
                + Row(Cell("x") + Cell("y"))));

        await ExtractAsync<OpenDocumentSpreadsheetExtractor>(package);

        BoundedDocumentTextSink sink = new(262_144, CancellationToken.None);

        OpenDocumentSpreadsheetExtractor extractor = new();

        Stopwatch watch = Stopwatch.StartNew();

        long before = GC.GetAllocatedBytesForCurrentThread();

        ValueTask extraction = extractor.ExtractAsync(package, sink, CancellationToken.None);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        watch.Stop();

        await extraction;

        SessionAttachmentExtractionResult result = sink.Complete();

        Assert.Equal("Sheet: Big\na\tb\tc\tREPEATED\nx\ty\n", result.Text);

        Assert.False(result.WasTruncated);

        Assert.True(allocated < 4 * 1024 * 1024, $"Extraction allocated {allocated} bytes.");

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Extraction took {watch.Elapsed}.");
    }

    [Fact]
    public async Task Extract_MassivelyRepeatedEmptyRowsAndCells_AreCheapToo()
    {
        string rows = string.Concat(Enumerable.Repeat(
            Row(Empty(" table:number-columns-repeated=\"16384\""), " table:number-rows-repeated=\"1048576\""),
            200));

        byte[] package = Ods(Sheet("Void", rows) + Sheet("Real", Row(Cell("v"))));

        Assert.Equal("Sheet: Real\nv\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_ExactlyTheMaximumCellElements_IsAcceptedAndOneMoreIsStructureBounds()
    {
        // Sensitivity: the 1,048,576 physical cell element bound. Stored entries keep the ratio rule out of the way.
        const string openRow = "<table:table-row>";

        string cells(int count) => string.Concat(Enumerable.Repeat("<table:table-cell/>", count));

        string atLimit = Sheet("S", string.Concat(Enumerable.Repeat(openRow + cells(1024) + "</table:table-row>", 1024)));

        Assert.Equal(OpenDocumentSpreadsheetExtractor.MaximumCellElements, 1024 * 1024);

        Assert.Equal(
            string.Empty,
            (await ExtractAsync<OpenDocumentSpreadsheetExtractor>(StoredPackage(SpreadsheetMime, ContentPart("spreadsheet", atLimit)))).Text);

        string overLimit = Sheet("S", string.Concat(Enumerable.Repeat(openRow + cells(1024) + "</table:table-row>", 1024)) + openRow + cells(1) + "</table:table-row>");

        await RejectedAsync<OpenDocumentSpreadsheetExtractor>(
            StoredPackage(SpreadsheetMime, ContentPart("spreadsheet", overLimit)),
            DocumentPackageRejection.StructureBounds);
    }

    [Fact]
    public async Task Extract_CoveredCellsCountTowardsTheCellBound()
    {
        string covered = string.Concat(Enumerable.Repeat("<table:covered-table-cell/>", OpenDocumentSpreadsheetExtractor.MaximumCellElements + 1));

        await RejectedAsync<OpenDocumentSpreadsheetExtractor>(
            StoredPackage(SpreadsheetMime, ContentPart("spreadsheet", Sheet("S", "<table:table-row>" + covered + "</table:table-row>"))),
            DocumentPackageRejection.StructureBounds);
    }

    [Fact]
    public async Task Extract_ExactlyTheMaximumColumnElements_IsAcceptedAndOneMoreIsStructureBounds()
    {
        string columns(int count) => string.Concat(Enumerable.Repeat("<table:table-column/>", count));

        byte[] atLimit = Ods(Sheet("S", columns(OpenDocumentSpreadsheetExtractor.MaximumColumnElements) + Row(Cell("v"))));

        Assert.Equal("Sheet: S\nv\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(atLimit));

        byte[] overLimit = Ods(Sheet("S", columns(OpenDocumentSpreadsheetExtractor.MaximumColumnElements + 1) + Row(Cell("v"))));

        await RejectedAsync<OpenDocumentSpreadsheetExtractor>(overLimit, DocumentPackageRejection.StructureBounds);
    }

    [Fact]
    public async Task Extract_ColumnElementCountResetsForEachSheet()
    {
        string columns = string.Concat(Enumerable.Repeat("<table:table-column/>", OpenDocumentSpreadsheetExtractor.MaximumColumnElements));

        byte[] package = Ods(Sheet("One", columns + Row(Cell("1"))) + Sheet("Two", columns + Row(Cell("2"))));

        Assert.Equal("Sheet: One\n1\nSheet: Two\n2\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    [Fact]
    public async Task Extract_StopsWritingAfterTheSinkRefusesText()
    {
        SpyTextSink sink = new(acceptedCalls: 3);

        string rows = string.Concat(Enumerable.Range(0, 50).Select(index => Row(Cell($"cell {index}"))));

        await new OpenDocumentSpreadsheetExtractor().ExtractAsync(Ods(Sheet("S", rows)), sink, CancellationToken.None);

        Assert.Equal(4, sink.Calls);
    }

    [Fact]
    public async Task Extract_CellLongerThanTheRetentionBound_IsMarkedTruncated()
    {
        string giant = new('x', 300_000);

        SessionAttachmentExtractionResult result = await ExtractAsync<OpenDocumentSpreadsheetExtractor>(
            Ods(Sheet("S", Row(Cell("first")) + Row(Cell(giant)) + Row(Cell("NEVERREACHED")))));

        Assert.True(result.WasTruncated);

        Assert.StartsWith("Sheet: S\nfirst\nxxxx", result.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("NEVERREACHED", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_CellCutAtTheRetentionBound_NeverLeavesALoneSurrogate()
    {
        string text = new string('x', 262_143) + "\U0001F600tail";

        SessionAttachmentExtractionResult result = await ExtractAsync<OpenDocumentSpreadsheetExtractor>(
            Ods(Sheet("S", Row(Cell(text)))),
            capacity: 1_000_000);

        Assert.True(result.WasTruncated);

        Assert.False(char.IsHighSurrogate(result.Text[^1]));
    }

    // ---------------------------------------------------------------- format checks

    [Theory]
    [InlineData(TextMime)]
    [InlineData(PresentationMime)]
    public async Task Extract_PackageOfAnotherOpenDocumentFormat_IsFormatMismatch(string mimeType)
    {
        byte[] package = Package(mimeType, ContentPart("text", "<text:p>x</text:p>"));

        await RejectedAsync<OpenDocumentSpreadsheetExtractor>(package, DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_SpreadsheetManifestWithAnotherBody_IsFormatMismatch()
    {
        byte[] package = Package(SpreadsheetMime, ContentPart("text", "<text:p>x</text:p>"));

        await RejectedAsync<OpenDocumentSpreadsheetExtractor>(package, DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_EncryptedSpreadsheet_IsEncrypted()
    {
        string manifest = ManifestPart(SpreadsheetMime, declareStyles: false)
            .Replace("</manifest:manifest>", "<manifest:file-entry manifest:full-path=\"Object 1/content.xml\"><manifest:encryption-data/></manifest:file-entry></manifest:manifest>", StringComparison.Ordinal);

        byte[] package = ComprehensionPackageBuilder.Build(
            ("META-INF/manifest.xml", manifest),
            ("content.xml", ContentPart("spreadsheet", Sheet("S", Row(Cell("v"))))));

        await RejectedAsync<OpenDocumentSpreadsheetExtractor>(package, DocumentPackageRejection.Encrypted);
    }

    [Fact]
    public async Task Extract_ManifestWithTheTextutilDocumentType_IsAccepted()
    {
        byte[] package = Ods(Sheet("S", Row(Cell("v"))), documentType: TextutilDocumentType);

        Assert.Equal("Sheet: S\nv\n", await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package));
    }

    // ---------------------------------------------------------------- markup helpers

    private static string Sheet(string name, string content, string attributes = "") =>
        $"<table:table table:name=\"{name}\"{attributes}>{content}</table:table>";

    private static string Columns(int count) => $"<table:table-column table:number-columns-repeated=\"{count}\"/>";

    private static string Row(string cells, string attributes = "") =>
        cells.Length == 0 ? $"<table:table-row{attributes}/>" : $"<table:table-row{attributes}>{cells}</table:table-row>";

    private static string Cell(string text, string attributes = "") =>
        $"<table:table-cell office:value-type=\"string\"{attributes}><text:p>{text}</text:p></table:table-cell>";

    private static string Empty(string attributes = "") => $"<table:table-cell{attributes}/>";

    private static string Covered(string text, string attributes = "") =>
        $"<table:covered-table-cell{attributes}><text:p>{text}</text:p></table:covered-table-cell>";
}
