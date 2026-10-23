using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Excel shows only the top-left cell of a merged range; the values stored in the other cells reappear only on unmerge.
/// The extractor projects what a reader sees, so every merged-away cell is excluded.
/// </summary>
public sealed class SpreadsheetMergedCellsTests
{
    private static string Merges(params string[] references) =>
        $"<mergeCells count=\"{references.Length}\">{string.Concat(references.Select(reference => $"<mergeCell ref=\"{reference}\"/>"))}</mergeCells>";

    private static string Bare(string text) => $"<c t=\"inlineStr\"><is><t>{text}</t></is></c>";

    // ------------------------------------------------------------ which cells are merged away

    [Fact]
    public void Extract_MergedAwayCellsOfARow_AreNotProjected()
    {
        string rows = Row(Text("A1", "Title"), Text("B1", "MERGED_AWAY_LEAK"), Text("C1", "MERGED_AWAY_LEAK2")) + Row(Text("A2", "next"));

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("A1:C1")));

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\nTitle\nnext\n", text);

        Assert.DoesNotContain("MERGED_AWAY", text);
    }

    [Fact]
    public void Extract_MergedAwayCellValue_IsNotEmitted()
    {
        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "Visible"), Text("B1", "MERGED-AWAY-SECRET")), AfterSheetData: Merges("A1:B1")));

        Assert.Equal("Sheet: S\nVisible\n", ExtractText(package));
    }

    [Fact]
    public void Extract_TwoDimensionalMerge_KeepsOnlyItsTopLeftCell()
    {
        string rows = Row(Text("A1", "a1"), Text("B1", "b1"), Text("C1", "c1"))
            + Row(Text("A2", "a2"), Text("B2", "b2"), Text("C2", "c2"))
            + Row(Text("A3", "a3"), Text("B3", "b3"), Text("C3", "c3"));

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("A1:B2")));

        // Row 1 loses B1 and row 2 loses A2 and B2; C1 and C2 lie outside the range and stay.
        Assert.Equal("Sheet: S\na1\tc1\nc2\na3\tb3\tc3\n", ExtractText(package));
    }

    [Fact]
    public void Extract_MergedRangeWhoseAnchorIsEmpty_StillHidesTheOtherCells()
    {
        // Excel shows the blank anchor, not the value stored under the merge.
        string rows = Row("<c r=\"A1\"/>", Text("B1", "UNDER_EMPTY_ANCHOR")) + Row(Text("A2", "next"));

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("A1:B1")));

        Assert.Equal("Sheet: S\nnext\n", ExtractText(package));
    }

    [Fact]
    public void Extract_CellsOutsideAMergedRange_AreKept()
    {
        string rows = Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"), Text("D1", "d")) + Row(Text("A2", "a2"), Text("B2", "b2"));

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("B1:C1")));

        Assert.Equal("Sheet: S\na\tb\td\na2\tb2\n", ExtractText(package));
    }

    [Fact]
    public void Extract_MergeCellsBeforeSheetData_AreStillApplied()
    {
        // The schema puts mergeCells after sheetData; a part that puts them first is read the same way.
        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "shown"), Text("B1", "MERGED_AWAY")), Cols: Merges("A1:B1")));

        Assert.Equal("Sheet: S\nshown\n", ExtractText(package));
    }

    [Fact]
    public void Extract_MergedRangesOfOneSheet_DoNotReachTheNextSheet()
    {
        byte[] package = Xlsx(
            new Sheet("One", Row(Text("A1", "a"), Text("B1", "MERGED_AWAY")), AfterSheetData: Merges("A1:B1")),
            new Sheet("Two", Row(Text("A1", "a"), Text("B1", "b"))));

        Assert.Equal("Sheet: One\na\nSheet: Two\na\tb\n", ExtractText(package));
    }

    [Fact]
    public void Extract_DuplicateMergedRange_StillKeepsItsAnchor()
    {
        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "anchor"), Text("B1", "MERGED_AWAY")), AfterSheetData: Merges("A1:B1", "A1:B1")));

        Assert.Equal("Sheet: S\nanchor\n", ExtractText(package));
    }

    [Fact]
    public void Extract_OverlappingMergedRanges_HideACellThatIsMergedAwayByEither()
    {
        // B2 anchors the second range but lies inside the first one without anchoring it: it is hidden.
        string rows = Row(Text("A1", "a1"), Text("B1", "b1"), Text("C1", "c1"))
            + Row(Text("A2", "a2"), Text("B2", "b2"), Text("C2", "c2"))
            + Row(Text("A3", "a3"), Text("B3", "b3"), Text("C3", "c3"));

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("A1:B2", "B2:C3")));

        Assert.Equal("Sheet: S\na1\tc1\na3\n", ExtractText(package));
    }

    [Fact]
    public void Extract_AdjacentMergedRanges_EachKeepTheirOwnAnchor()
    {
        byte[] package = Xlsx(new Sheet(
            "S",
            Row(Text("A1", "left"), Text("B1", "MERGED_AWAY"), Text("C1", "right"), Text("D1", "MERGED_AWAY")),
            AfterSheetData: Merges("A1:B1", "C1:D1")));

        Assert.Equal("Sheet: S\nleft\tright\n", ExtractText(package));
    }

    [Fact]
    public void Extract_StrictWorkbook_AppliesMergeCellsToo()
    {
        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "kept"), Text("B1", "MERGED_AWAY")), AfterSheetData: Merges("A1:B1"))],
            sharedStringItems: null,
            strict: true);

        Assert.Equal("Sheet: S\nkept\n", ExtractText(package));
    }

    [Fact]
    public void Extract_MergeCellElementsInAnotherNamespace_AreIgnored()
    {
        const string decoy = "<mergeCells xmlns=\"urn:decoy\"><mergeCell ref=\"A1:B1\"/></mergeCells>";

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "a"), Text("B1", "b")), AfterSheetData: decoy));

        Assert.Equal("Sheet: S\na\tb\n", ExtractText(package));
    }

    // ------------------------------------------------------------ how a cell is located

    [Fact]
    public void Extract_CellsAndRowsWithoutReferences_AreLocatedBySequence()
    {
        // Row 1 and 4 carry no number (the row after the previous one); row 3 is explicit; no cell carries a reference.
        string rows = $"<row>{Bare("a1")}{Bare("MERGED_AWAY_1")}</row>"
            + $"<row r=\"3\">{Bare("a3")}{Bare("b3")}</row>"
            + $"<row>{Bare("a4")}{Bare("MERGED_AWAY_4")}</row>";

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("A1:B1", "A4:B4")));

        Assert.Equal("Sheet: S\na1\na3\tb3\na4\n", ExtractText(package));
    }

    [Fact]
    public void Extract_CellWhoseReferenceNamesAMergedRowOtherThanItsRow_IsHidden()
    {
        // The row says 9 and the cell says A1/B1: either placement inside the merge is enough to hide the cell.
        string rows = $"<row r=\"9\">{Text("A1", "anchor")}{Text("B1", "MERGED_AWAY")}</row>";

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("A1:C1")));

        Assert.Equal("Sheet: S\nanchor\n", ExtractText(package));
    }

    [Fact]
    public void Extract_CellInAMergedRowWhoseReferenceNamesAnotherRow_IsHidden()
    {
        string rows = $"<row r=\"9\">{Text("A1", "anchor")}{Text("B1", "MERGED_AWAY")}</row>";

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("A9:C9")));

        Assert.Equal("Sheet: S\nanchor\n", ExtractText(package));
    }

    [Fact]
    public void Extract_RowsInAnyOrder_StillHideTheMergedAwayCells()
    {
        string rows = $"<row r=\"3\">{Text("A3", "a3")}{Text("B3", "MERGED_AWAY_3")}</row>"
            + $"<row r=\"1\">{Text("A1", "a1")}{Text("B1", "MERGED_AWAY_1")}</row>"
            + $"<row r=\"2\">{Text("A2", "a2")}{Text("B2", "b2")}</row>"
            + $"<row r=\"3\">{Text("A3", "again")}{Text("B3", "MERGED_AWAY_AGAIN")}</row>";

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("A1:B1", "A3:B3")));

        Assert.Equal("Sheet: S\na3\na1\na2\tb2\nagain\n", ExtractText(package));
    }

    [Fact]
    public void Extract_MergedRangeSpanningManyRowsAndTheLastColumns_HidesCellsDeepInsideIt()
    {
        string rows = $"<row r=\"1048576\">{Text("XFC1048576", "anchor")}{Text("XFD1048576", "MERGED_AWAY")}</row>"
            + $"<row r=\"2\">{Text("A2", "a2")}{Text("B2", "MERGED_AWAY_B2")}</row>";

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("XFC1048576:XFD1048576", "A1:B1048575")));

        Assert.Equal("Sheet: S\nanchor\n", ExtractText(package));
    }

    // ------------------------------------------------------------ references that merge nothing, or are read leniently

    [Theory]
    [InlineData("A1")]
    [InlineData("A1:A1")]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("A1:")]
    [InlineData(":B1")]
    [InlineData("A0:B1")]
    [InlineData("1A:B1")]
    [InlineData("$A$1:$B$1")]
    [InlineData("A1:B1:C1")]
    [InlineData("A1;B1")]
    [InlineData("XFE1:XFE2")]
    [InlineData("A1048577:B1048577")]
    public void Extract_SingleCellOrUnusableMergeReference_MergesNothing(string reference)
    {
        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "a"), Text("B1", "b")), AfterSheetData: Merges(reference)));

        Assert.Equal("Sheet: S\na\tb\n", ExtractText(package));
    }

    [Fact]
    public void Extract_MergeCellWithoutARefAttribute_MergesNothing()
    {
        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "a"), Text("B1", "b")), AfterSheetData: "<mergeCells><mergeCell/></mergeCells>"));

        Assert.Equal("Sheet: S\na\tb\n", ExtractText(package));
    }

    [Theory]
    [InlineData("C1:A1", "Sheet: S\na\nnext\n")]
    [InlineData("a1:c1", "Sheet: S\na\nnext\n")]
    [InlineData("A1:ZZZZ1", "Sheet: S\na\nnext\n")]
    [InlineData("A1:C99999999999999", "Sheet: S\na\n")]
    public void Extract_MergeReference_IsNormalizedAndClampedToTheSheet(string reference, string expected)
    {
        // Reversed corners, lower case and a far corner past the sheet all still name the cells of A1:C1 and more; the
        // last range reaches every row of A:C, so A2 goes too.
        string rows = Row(Text("A1", "a"), Text("B1", "MERGED_AWAY_B"), Text("C1", "MERGED_AWAY_C")) + Row(Text("A2", "next"));

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges(reference)));

        Assert.Equal(expected, ExtractText(package));
    }

    // ------------------------------------------------------------ bounds

    [Fact]
    public void Extract_ExactlyTheMaximumNumberOfMergeCellElements_IsAccepted()
    {
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { MergedRanges = 3 });

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "a"), Text("B1", "MERGED_AWAY")), AfterSheetData: Merges("A1:B1", "D1:E1", "G1:H1")));

        Assert.Equal("Sheet: S\na\n", ExtractText(package, extractor));
    }

    [Fact]
    public void Extract_OneMoreThanTheMaximumNumberOfMergeCellElements_IsStructureBounds()
    {
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { MergedRanges = 3 });

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "a")), AfterSheetData: Merges("A1:B1", "D1:E1", "G1:H1", "J1:K1")));

        ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
    }

    [Fact]
    public void Extract_MergeCellElementsOfRepeatedMergeCellsElements_CountTowardOneBound()
    {
        // Sensitivity: one running count for the worksheet, so a repeated (schema-invalid) mergeCells element cannot restart it.
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { MergedRanges = 3 });

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "a")), AfterSheetData: Merges("A1:B1", "D1:E1") + Merges("G1:H1", "J1:K1")));

        ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
    }

    [Fact]
    public void Extract_UnusableMergeReferencesCountTowardTheBound()
    {
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { MergedRanges = 2 });

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "a")), AfterSheetData: Merges("junk", "junk", "junk")));

        ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
    }

    [Fact]
    public void Extract_MergeCellBound_IsPerWorksheet()
    {
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { MergedRanges = 2 });

        byte[] package = Xlsx(
            new Sheet("One", Row(Text("A1", "a"), Text("B1", "MERGED_AWAY")), AfterSheetData: Merges("A1:B1", "D1:E1")),
            new Sheet("Two", Row(Text("A1", "b"), Text("B1", "MERGED_AWAY")), AfterSheetData: Merges("A1:B1", "D1:E1")));

        Assert.Equal("Sheet: One\na\nSheet: Two\nb\n", ExtractText(package, extractor));
    }

    [Fact]
    public void Extract_TheMaximumNumberOfMergedRangesOverManyRows_IsProjectedExactly()
    {
        // Sensitivity: the lookup is not a scan over the ranges for every cell (65,536 ranges x 131,072 cells would not finish).
        int maximum = SpreadsheetLimits.Default.MergedRanges;

        StringBuilder rows = new();

        string[] merges = new string[maximum];

        StringBuilder expected = new("Sheet: S\n");

        for (int row = 1; row <= maximum; row++)
        {
            rows.Append("<row r=\"").Append(row).Append("\"><c r=\"A").Append(row).Append("\"><v>").Append(row % 10).Append("</v></c><c r=\"B").Append(row).Append("\"><v>MERGED_AWAY</v></c></row>");

            merges[row - 1] = $"A{row}:B{row}";

            expected.Append(row % 10).Append('\n');
        }

        byte[] package = Xlsx(new Sheet("S", rows.ToString(), AfterSheetData: Merges(merges)));

        Assert.Equal(expected.ToString(), ExtractText(package));
    }

    [Fact]
    public void Extract_MoreMergeCellElementsThanTheDefaultBound_IsStructureBounds()
    {
        string[] references = [.. Enumerable.Range(1, SpreadsheetLimits.Default.MergedRanges + 1).Select(row => $"A{row}:B{row}")];

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "a")), AfterSheetData: Merges(references)));

        ExtractRejected(DocumentPackageRejection.StructureBounds, package);
    }

    [Theory]
    [InlineData(4, "1,700", true)]
    [InlineData(3, "1,700", false)]
    [InlineData(7, "1,700,1", true)]
    [InlineData(6, "1,700,1", false)]
    public void Extract_MergeLookup_IsBoundedByTheMarksItStepsOver(long moves, string rowNumbers, bool accepted)
    {
        // Ranges 1..10 and 500..600 make four marks (starts at 1 and 500, ends after 10 and 600). Visiting rows 1 then 700
        // steps over 1 + 3 marks; going back to row 1 steps over 3 more, which a file whose rows do not ascend pays for.
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { MergeEventMoves = moves });

        string rows = string.Concat(rowNumbers.Split(',').Select(number => $"<row r=\"{number}\">{Text($"A{number}", "v")}</row>"));

        byte[] package = Xlsx(new Sheet("S", rows, AfterSheetData: Merges("A1:B10", "A500:B600")));

        if (accepted)
        {
            Assert.NotEmpty(ExtractText(package, extractor));
        }
        else
        {
            ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
        }
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(7, false)]
    public void Extract_MergeLookupBound_CountsTheMarksOfEverySheetTogether(long moves, bool accepted)
    {
        // Each sheet steps over 1 + 3 marks (rows 1 and 700); the bound is on the whole extraction, so the second sheet
        // arrives with 4 of the budget spent.
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { MergeEventMoves = moves });

        string rows = $"<row r=\"1\">{Text("A1", "v")}</row><row r=\"700\">{Text("A700", "v")}</row>";

        byte[] package = Xlsx(
            new Sheet("One", rows, AfterSheetData: Merges("A1:B10", "A500:B600")),
            new Sheet("Two", rows, AfterSheetData: Merges("A1:B10", "A500:B600")));

        if (accepted)
        {
            Assert.NotEmpty(ExtractText(package, extractor));
        }
        else
        {
            ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
        }
    }

    [Fact]
    public void Extract_RowsThatJumpAcrossManyMergedRangesAgainAndAgain_AreStructureBounds()
    {
        // Sensitivity: the default travel budget. 8,192 ranges spread over the sheet make 16,384 marks; alternating between
        // the first row and a middle row travels about 8,192 of them each time, so 2,200 visits exceed the default budget.
        int ranges = 8192;

        string[] merges = new string[ranges];

        for (int index = 0; index < ranges; index++)
        {
            merges[index] = $"A{index + 1}:B{SpreadsheetLimits.MaximumRow - index}";
        }

        StringBuilder rows = new();

        for (int visit = 0; visit < 2200; visit++)
        {
            int row = visit % 2 == 0 ? 1 : 524_288;

            rows.Append("<row r=\"").Append(row).Append("\"><c r=\"D").Append(row).Append("\"><v>1</v></c></row>");
        }

        byte[] package = Xlsx(new Sheet("S", rows.ToString(), AfterSheetData: Merges(merges)));

        ExtractRejected(DocumentPackageRejection.StructureBounds, package);
    }
}
