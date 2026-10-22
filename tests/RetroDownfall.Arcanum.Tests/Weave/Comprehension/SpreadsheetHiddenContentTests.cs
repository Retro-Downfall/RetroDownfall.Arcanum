using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class SpreadsheetHiddenContentTests
{
    // ------------------------------------------------------------ hidden sheets

    [Theory]
    [InlineData("hidden")]
    [InlineData("veryHidden")]
    public void Extract_HiddenAndVeryHiddenSheets_AreExcluded(string state)
    {
        // Sensitivity: the sheet-state test. Without it the hidden sheet's text is projected.
        byte[] package = Xlsx(
            new Sheet("Visible", Row(Text("A1", "shown"))),
            new Sheet("Secret", Row(Text("A1", "HIDDENSHEETTEXT")), State: state),
            new Sheet("Also visible", Row(Text("A1", "also shown"))));

        string text = ExtractText(package);

        Assert.Equal("Sheet: Visible\nshown\nSheet: Also visible\nalso shown\n", text);

        Assert.DoesNotContain("Secret", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("visible")]
    public void Extract_VisibleSheetStates_AreIncluded(string state)
    {
        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "shown")), State: state));

        Assert.Equal("Sheet: S\nshown\n", ExtractText(package));
    }

    [Theory]
    [InlineData("HIDDEN")]
    [InlineData("Hidden")]
    [InlineData("veryhidden")]
    [InlineData("invisible")]
    public void Extract_UnrecognizedSheetState_IsExcludedRatherThanGuessedVisible(string state)
    {
        // Fail closed: only an absent or "visible" state projects the sheet.
        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "HIDDENSHEETTEXT")), State: state), new Sheet("T", Row(Text("A1", "kept"))));

        Assert.Equal("Sheet: T\nkept\n", ExtractText(package));
    }

    [Fact]
    public void Extract_HiddenSheet_IsNeverResolvedOrRead()
    {
        // A hidden sheet may reference a part that is missing, malformed or not even a relationship: none of it is touched.
        byte[] package = Xlsx(
            new Sheet("Visible", Row(Text("A1", "shown"))),
            new Sheet("Malformed", string.Empty, State: "hidden", Part: "<<< not xml HIDDENSHEETTEXT"),
            new Sheet("Dangling", string.Empty, State: "veryHidden", RelationshipId: "rIdNoSuchRelationship"),
            new Sheet("Wrong root", string.Empty, State: "hidden", Part: "<notAWorksheet xmlns=\"urn:x\"/>"));

        Assert.Equal("Sheet: Visible\nshown\n", ExtractText(package));
    }

    // ------------------------------------------------------------ hidden rows

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public void Extract_HiddenRows_AreExcluded(string hidden)
    {
        // Sensitivity: the row hidden test. The hidden row carries nested cells that must not leak.
        string rows = Row(Text("A1", "first"))
            + $"<row hidden=\"{hidden}\">{Text("A2", "HIDDENROWTEXT")}{Number("B2", "99")}</row>"
            + Row(Text("A3", "third"));

        Assert.Equal("Sheet: S\nfirst\nthird\n", ExtractText(Xlsx(new Sheet("S", rows))));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("")]
    public void Extract_RowsThatAreNotHidden_AreIncluded(string hidden)
    {
        string rows = $"<row hidden=\"{hidden}\">{Text("A1", "shown")}</row>";

        Assert.Equal("Sheet: S\nshown\n", ExtractText(Xlsx(new Sheet("S", rows))));
    }

    [Fact]
    public void Extract_SheetWhoseOnlyRowsAreHidden_EmitsNoHeader()
    {
        byte[] package = Xlsx(new Sheet("S", "<row hidden=\"1\">" + Text("A1", "HIDDENROWTEXT") + "</row>"), new Sheet("T", Row(Text("A1", "kept"))));

        Assert.Equal("Sheet: T\nkept\n", ExtractText(package));
    }

    // ------------------------------------------------------------ hidden columns

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public void Extract_HiddenColumn_ExcludesItsCellsInEveryRow(string hidden)
    {
        // Sensitivity: the cols/col hidden handling and the per-cell column test.
        string rows = Row(Text("A1", "a1"), Text("B1", "HIDDENCOLUMNTEXT"), Text("C1", "c1"))
            + Row(Number("A2", "1"), Number("B2", "2"), Number("C2", "3"))
            + Row(Shared("A3", 0), Shared("B3", 0), Shared("C3", 0));

        byte[] package = Xlsx(
            [new Sheet("S", rows, Cols: $"<cols><col min=\"2\" max=\"2\" hidden=\"{hidden}\" width=\"0\"/></cols>")],
            Si("shared"));

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\na1\tc1\n1\t3\nshared\tshared\n", text);

        Assert.DoesNotContain("HIDDENCOLUMNTEXT", text);
    }

    [Fact]
    public void Extract_HiddenColumnRange_ExcludesEveryColumnInTheRange()
    {
        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"), Text("D1", "d"), Text("E1", "e"));

        byte[] package = Xlsx(new Sheet("S", rows, Cols: "<cols><col min=\"2\" max=\"4\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: S\na\te\n", ExtractText(package));
    }

    [Fact]
    public void Extract_ColumnsThatAreNotHidden_AreIncluded()
    {
        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"));

        byte[] package = Xlsx(new Sheet("S", rows, Cols: "<cols><col min=\"1\" max=\"1\" width=\"20\"/><col min=\"2\" max=\"2\" hidden=\"0\"/><col min=\"3\" max=\"3\" hidden=\"false\"/></cols>"));

        Assert.Equal("Sheet: S\na\tb\tc\n", ExtractText(package));
    }

    [Fact]
    public void Extract_OverlappingAndRepeatedColsElements_HideTheUnionOfTheirRanges()
    {
        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"), Text("D1", "d"), Text("E1", "e"), Text("F1", "f"));

        string cols = "<cols><col min=\"2\" max=\"3\" hidden=\"1\"/><col min=\"3\" max=\"4\" hidden=\"1\"/></cols>"
            + "<cols><col min=\"6\" max=\"6\" hidden=\"1\"/><col min=\"1\" max=\"20\" hidden=\"0\"/></cols>";

        byte[] package = Xlsx(new Sheet("S", rows, Cols: cols));

        Assert.Equal("Sheet: S\na\te\n", ExtractText(package));
    }

    [Fact]
    public void Extract_RangesStartingAtTheSameColumn_HideTheLongerOne()
    {
        // Sensitivity: accumulating start marks. Overwriting them would end the hiding at the shorter range's end.
        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"), Text("D1", "d"), Text("E1", "e"), Text("F1", "f"));

        byte[] package = Xlsx(new Sheet("S", rows, Cols: "<cols><col min=\"2\" max=\"3\" hidden=\"1\"/><col min=\"2\" max=\"5\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: S\na\tf\n", ExtractText(package));
    }

    [Fact]
    public void Extract_EachSheetHasItsOwnHiddenColumns()
    {
        // Sensitivity: clearing the previous sheet's coverage. Stale marks would be summed into the second sheet's.
        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"), Text("D1", "d"), Text("E1", "e"), Text("F1", "f"));

        byte[] package = Xlsx(
            new Sheet("First", rows, Cols: "<cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols>"),
            new Sheet("Second", rows, Cols: "<cols><col min=\"5\" max=\"5\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: First\na\tc\td\te\tf\nSheet: Second\na\tb\tc\td\tf\n", ExtractText(package));
    }

    [Fact]
    public void Extract_InvertedHiddenRange_DoesNotCancelAnotherHiddenRange()
    {
        // Sensitivity: skipping a range whose first column is above its last. Applied as marks it would subtract from the
        // coverage of columns 2-4 and un-hide column 3.
        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "HIDDENCOLUMNTEXT"), Text("D1", "d"), Text("E1", "e"));

        byte[] package = Xlsx(new Sheet("S", rows, Cols: "<cols><col min=\"2\" max=\"4\" hidden=\"1\"/><col min=\"5\" max=\"2\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: S\na\te\n", ExtractText(package));
    }

    [Fact]
    public void Extract_ColsElementAfterSheetData_StillHidesItsColumns()
    {
        // Sensitivity: reading column visibility before the cells, whatever the element order. A lenient consumer honours a
        // late cols element, so a single streaming pass that met the cells first would leak the hidden column.
        byte[] package = Xlsx(new Sheet(
            "S",
            Row(Text("A1", "a"), Text("B1", "HIDDENCOLUMNTEXT"), Text("C1", "c")),
            AfterSheetData: "<cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols>"));

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\na\tc\n", text);

        Assert.DoesNotContain("HIDDENCOLUMNTEXT", text);
    }

    [Theory]
    [InlineData("0", "3", "Sheet: S\nd\n")]
    [InlineData("3", "99999999999999999999", "Sheet: S\na\tb\n")]
    [InlineData("16384", "16384", "Sheet: S\na\tb\tc\td\n")]
    [InlineData("16385", "16390", "Sheet: S\na\tb\tc\td\n")]
    [InlineData("-5", "3", "Sheet: S\na\tb\tc\td\n")]
    [InlineData("3", "2", "Sheet: S\na\tb\tc\td\n")]
    [InlineData("x", "3", "Sheet: S\na\tb\tc\td\n")]
    [InlineData("1", "", "Sheet: S\na\tb\tc\td\n")]
    [InlineData("3", "18446744073709551616", "Sheet: S\na\tb\n")]
    [InlineData("18446744073709551616", "3", "Sheet: S\na\tb\tc\td\n")]
    public void Extract_HiddenColRange_IsClampedToTheAddressableColumns(string min, string max, string expected)
    {
        // Sensitivity: the clamp to 1..16384. "0".."3" hides columns 1-3; "3"..huge hides 3 to the last column (2^64 would
        // wrap to 0 without saturation and hide nothing); a negative, inverted, unparsable, empty or wholly out-of-range
        // range, or a huge minimum, hides nothing among columns 1-4.
        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"), Text("D1", "d"));

        byte[] package = Xlsx(new Sheet("S", rows, Cols: $"<cols><col min=\"{min}\" max=\"{max}\" hidden=\"1\"/></cols>"));

        Assert.Equal(expected, ExtractText(package));
    }

    [Fact]
    public void Extract_HiddenColumnAtTheLastAddressableColumn_ExcludesIt()
    {
        string rows = Row(Text("A1", "a"), Text("XFD1", "last"), Text("XFC1", "previous"));

        byte[] package = Xlsx(new Sheet("S", rows, Cols: "<cols><col min=\"16384\" max=\"16384\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: S\na\tprevious\n", ExtractText(package));
    }

    [Fact]
    public void Extract_HiddenColumnsOfOneSheet_DoNotAffectTheNextSheet()
    {
        byte[] package = Xlsx(
            new Sheet("First", Row(Text("A1", "a"), Text("B1", "b")), Cols: "<cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols>"),
            new Sheet("Second", Row(Text("A1", "c"), Text("B1", "d"))));

        Assert.Equal("Sheet: First\na\nSheet: Second\nc\td\n", ExtractText(package));
    }

    [Fact]
    public void Extract_CellInAHiddenColumn_IsNeverResolvedSoNeverTruncates()
    {
        // The column test comes before shared string resolution: a hidden cell's reference past retention is not a truncation.
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { SharedStrings = 1 });

        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 0), Shared("B1", 1)), Cols: "<cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols>")],
            Si("kept") + Si("dropped"));

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.False(result.WasTruncated);

        Assert.Equal("Sheet: S\nkept\n", result.Text);
    }

    // ------------------------------------------------------------ cell references

    [Theory]
    [InlineData("A1", 1)]
    [InlineData("Z9", 26)]
    [InlineData("AA1", 27)]
    [InlineData("AZ3", 52)]
    [InlineData("BA3", 53)]
    [InlineData("ZZ10", 702)]
    [InlineData("AAA1", 703)]
    [InlineData("XFD1048576", 16384)]
    [InlineData("c5", 3)]
    [InlineData("aa7", 27)]
    [InlineData("C", 3)]
    public void Extract_CellReference_ResolvesItsColumnNumber(string reference, int column)
    {
        // The cell is hidden exactly when its column is the hidden one, so this pins A1-reference to column parsing.
        string hiddenCols = $"<cols><col min=\"{column}\" max=\"{column}\" hidden=\"1\"/></cols>";

        string otherCols = $"<cols><col min=\"{column + 1}\" max=\"{column + 1}\" hidden=\"1\"/></cols>";

        string rows = Row(Text(reference, "CELL"));

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, Extract(Xlsx(new Sheet("S", rows, Cols: hiddenCols))).Status);

        Assert.Equal("Sheet: S\nCELL\n", ExtractText(Xlsx(new Sheet("S", rows, Cols: otherCols))));
    }

    [Fact]
    public void Extract_CellWithoutAReference_TakesTheColumnAfterThePreviousCell()
    {
        // Sensitivity: the "next column after the previous cell" rule. Column 3 is hidden.
        string rows = Row(
            Text("A1", "a"),
            "<c t=\"inlineStr\"><is><t>b</t></is></c>",
            "<c t=\"inlineStr\"><is><t>HIDDENCOLUMNTEXT</t></is></c>",
            "<c t=\"inlineStr\"><is><t>d</t></is></c>");

        byte[] package = Xlsx(new Sheet("S", rows, Cols: "<cols><col min=\"3\" max=\"3\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: S\na\tb\td\n", ExtractText(package));
    }

    [Fact]
    public void Extract_FirstCellWithoutAReference_IsColumnOne()
    {
        byte[] package = Xlsx(new Sheet(
            "S",
            Row("<c t=\"inlineStr\"><is><t>HIDDENCOLUMNTEXT</t></is></c>", Text("B1", "b")),
            Cols: "<cols><col min=\"1\" max=\"1\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: S\nb\n", ExtractText(package));
    }

    [Fact]
    public void Extract_ReferenceThenUnreferencedCell_ContinuesFromTheReferencedColumn()
    {
        // B1 is column 2, so the unreferenced cell after it is column 3 (hidden); the one after that is 4.
        string rows = Row(Text("B1", "b"), "<c t=\"inlineStr\"><is><t>HIDDENCOLUMNTEXT</t></is></c>", "<c t=\"inlineStr\"><is><t>d</t></is></c>");

        byte[] package = Xlsx(new Sheet("S", rows, Cols: "<cols><col min=\"3\" max=\"3\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: S\nb\td\n", ExtractText(package));
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("C5X")]
    [InlineData("A1:B2")]
    [InlineData("AAAA1")]
    [InlineData("XFE1")]
    [InlineData("$B$1")]
    [InlineData("B 1")]
    [InlineData("ZZZ1")]
    [InlineData("RDRNUPCP1")]
    public void Extract_MalformedCellReference_TakesTheColumnAfterThePreviousCell(string reference)
    {
        // After A1 the malformed reference is column 2, which is hidden: it must not fall back to a visible position.
        // "RDRNUPCP1" is eight letters whose base-26 value wraps a 32-bit integer to 646: a parser that does not stop at
        // the first column above XFD would read it as the valid, visible column 646.
        string rows = Row(Text("A1", "a"), $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>HIDDENCOLUMNTEXT</t></is></c>", Text("C1", "c"));

        byte[] package = Xlsx(new Sheet("S", rows, Cols: "<cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: S\na\tc\n", ExtractText(package));
    }

    [Fact]
    public void Extract_UnreferencedCellsPastTheLastColumn_AreProjectedAndNeverFail()
    {
        // A cell without a reference takes the column after the previous one, which can run past XFD. No such column can be
        // hidden, and the column lookup must not run off the end of the hidden column table (column 1 is hidden here, so
        // the table exists).
        string rows = Row(
            Text("XFD1", "last"),
            "<c t=\"inlineStr\"><is><t>beyond one</t></is></c>",
            "<c t=\"inlineStr\"><is><t>beyond two</t></is></c>",
            "<c t=\"inlineStr\"><is><t>beyond three</t></is></c>");

        byte[] package = Xlsx(new Sheet("S", rows, Cols: "<cols><col min=\"1\" max=\"1\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: S\nlast\tbeyond one\tbeyond two\tbeyond three\n", ExtractText(package));
    }

    [Fact]
    public void Extract_ColumnTrackingRestartsAtEachRow()
    {
        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"))
            + Row("<c t=\"inlineStr\"><is><t>x</t></is></c>", "<c t=\"inlineStr\"><is><t>HIDDENCOLUMNTEXT</t></is></c>");

        byte[] package = Xlsx(new Sheet("S", rows, Cols: "<cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols>"));

        Assert.Equal("Sheet: S\na\tc\nx\n", ExtractText(package));
    }

    // ------------------------------------------------------------ never resolved

    [Fact]
    public void Extract_CommentsHeadersFootersDefinedNamesValidationDrawingsAndCharts_AreNeverResolved()
    {
        const string worksheetExtras =
            "<dataValidations count=\"1\"><dataValidation type=\"list\" promptTitle=\"PROMPTTITLETEXT\" prompt=\"PROMPTTEXT\" errorTitle=\"ERRORTITLETEXT\" error=\"ERRORTEXT\"><formula1>\"VALIDATIONLISTTEXT\"</formula1></dataValidation></dataValidations>"
            + "<headerFooter><oddHeader>HEADERTEXT</oddHeader><oddFooter>FOOTERTEXT</oddFooter></headerFooter>"
            + "<drawing r:id=\"rIdDrawing\"/><legacyDrawing r:id=\"rIdVml\"/>";

        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "visible cell")), AfterSheetData: worksheetExtras)],
            sharedStringItems: null,
            extraEntries:
            [
                ("xl/worksheets/_rels/sheet1.xml.rels", Relationships(
                    $"<Relationship Id=\"rIdComments\" Type=\"{OfficeDocumentRelationship("comments")}\" Target=\"../comments1.xml\"/>",
                    "<Relationship Id=\"rIdThreaded\" Type=\"http://schemas.microsoft.com/office/2017/10/relationships/threadedComment\" Target=\"../threadedComments/threadedComment1.xml\"/>",
                    $"<Relationship Id=\"rIdDrawing\" Type=\"{OfficeDocumentRelationship("drawing")}\" Target=\"../drawings/drawing1.xml\"/>",
                    $"<Relationship Id=\"rIdVml\" Type=\"{OfficeDocumentRelationship("vmlDrawing")}\" Target=\"../drawings/vmlDrawing1.vml\"/>")),
                ("xl/comments1.xml", $"<comments xmlns=\"{Main}\"><commentList><comment ref=\"A1\"><text><r><t>COMMENTTEXT</t></r></text></comment></commentList></comments>"),
                ("xl/threadedComments/threadedComment1.xml", "<ThreadedComments xmlns=\"http://schemas.microsoft.com/office/spreadsheetml/2018/threadedcomments\"><threadedComment ref=\"A1\" id=\"{1}\"><text>THREADEDCOMMENTTEXT</text></threadedComment></ThreadedComments>"),
                ("xl/drawings/drawing1.xml", "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><xdr:twoCellAnchor><xdr:sp><xdr:txBody><a:p><a:r><a:t>DRAWINGTEXT</a:t></a:r></a:p></xdr:txBody></xdr:sp></xdr:twoCellAnchor></xdr:wsDr>"),
                ("xl/drawings/vmlDrawing1.vml", "<xml><v:textbox>VMLTEXT</v:textbox></xml>"),
                ("xl/charts/chart1.xml", "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:title>CHARTTEXT</c:title></c:chartSpace>"),
            ],
            extraWorkbookXml: "<definedNames><definedName name=\"DEFINEDNAMEKEY\">DEFINEDNAMETEXT</definedName></definedNames><externalReferences><externalReference r:id=\"rIdExternal\"/></externalReferences>");

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\nvisible cell\n", text);

        foreach (string marker in new[]
        {
            "PROMPT", "ERROR", "VALIDATION", "HEADERTEXT", "FOOTERTEXT", "COMMENTTEXT", "THREADEDCOMMENTTEXT", "DRAWINGTEXT", "VMLTEXT", "CHARTTEXT", "DEFINEDNAME",
        })
        {
            Assert.DoesNotContain(marker, text);
        }
    }

    [Fact]
    public void Extract_ChartsheetAndOtherNonWorksheetSheets_AreSkippedNotRead()
    {
        // Only a "worksheet" relationship is read. The chartsheet part here would fail a worksheet read (wrong root).
        byte[] package = Xlsx(
            new Sheet("Chart", string.Empty, Kind: "chartsheet", Part: $"<chartsheet xmlns=\"{Main}\"><sheetViews/></chartsheet>"),
            new Sheet("Dialog", string.Empty, Kind: "dialogsheet", Part: $"<dialogsheet xmlns=\"{Main}\"/>"),
            new Sheet("Data", Row(Text("A1", "cells"))));

        Assert.Equal("Sheet: Data\ncells\n", ExtractText(package));
    }
}
