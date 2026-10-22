using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class SpreadsheetBoundsTests
{
    [Fact]
    public void Limits_Default_HoldTheDocumentedBounds()
    {
        SpreadsheetLimits limits = SpreadsheetLimits.Default;

        Assert.Equal(4096, limits.Sheets);

        Assert.Equal(16384, limits.ColumnElements);

        Assert.Equal(1_048_576L, limits.Cells);

        Assert.Equal(1_048_576, limits.SharedStringCharacters);

        Assert.Equal(1_048_576, limits.SharedStrings);

        Assert.Equal(16384, SpreadsheetLimits.MaximumColumn);
    }

    // ------------------------------------------------------------ sheets

    [Fact]
    public void Extract_ExactlyTheMaximumNumberOfSheetElements_IsAccepted()
    {
        byte[] package = WorkbookWithHiddenSheets(SpreadsheetLimits.Default.Sheets - 1, visibleSheet: true);

        Assert.Equal("Sheet: Visible\nshown\n", ExtractText(package));
    }

    [Fact]
    public void Extract_OneMoreThanTheMaximumNumberOfSheetElements_IsStructureBounds()
    {
        byte[] package = WorkbookWithHiddenSheets(SpreadsheetLimits.Default.Sheets, visibleSheet: true);

        ExtractRejected(DocumentPackageRejection.StructureBounds, package);
    }

    [Fact]
    public void Extract_HiddenSheetsCountTowardTheSheetBound()
    {
        // Sensitivity: counting every sheet element before looking at its state.
        byte[] package = WorkbookWithHiddenSheets(SpreadsheetLimits.Default.Sheets + 1, visibleSheet: false);

        ExtractRejected(DocumentPackageRejection.StructureBounds, package);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Extract_SheetBound_IsExactAtTheHookedLimit(int sheetCount, bool accepted)
    {
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { Sheets = 2 });

        Sheet[] sheets = [.. Enumerable.Range(1, sheetCount).Select(index => new Sheet($"S{index}", Row(Text("A1", $"v{index}"))))];

        byte[] package = Xlsx(sheets);

        if (accepted)
        {
            Assert.Equal("Sheet: S1\nv1\nSheet: S2\nv2\n", ExtractText(package, extractor));
        }
        else
        {
            ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
        }
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Extract_SheetBound_CountsSheetElementsAcrossRepeatedSheetsElements(int totalSheetElements, bool accepted)
    {
        // Sensitivity: one running count for the whole workbook. A schema-invalid second <sheets> element must not restart
        // the count, or repeating it would multiply the cap. Every element but the last sits in a leading hidden <sheets>.
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { Sheets = 3 });

        string leadingSheets = "<sheets>"
            + string.Concat(Enumerable.Range(1, totalSheetElements - 1).Select(index => $"<sheet name=\"H{index}\" sheetId=\"{900 + index}\" state=\"hidden\" r:id=\"rIdNone\"/>"))
            + "</sheets>";

        byte[] package = Xlsx(
            [new Sheet("Visible", Row(Text("A1", "shown")))],
            sharedStringItems: null,
            extraWorkbookXml: leadingSheets);

        if (accepted)
        {
            Assert.Equal("Sheet: Visible\nshown\n", ExtractText(package, extractor));
        }
        else
        {
            ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
        }
    }

    // ------------------------------------------------------------ columns

    [Fact]
    public void Extract_ExactlyTheMaximumNumberOfColElements_IsAccepted()
    {
        string cols = "<cols>" + string.Concat(Enumerable.Repeat("<col min=\"1\" max=\"1\" width=\"9\"/>", SpreadsheetLimits.Default.ColumnElements)) + "</cols>";

        Assert.Equal("Sheet: S\nx\n", ExtractText(Xlsx(new Sheet("S", Row(Text("A1", "x")), Cols: cols))));
    }

    [Fact]
    public void Extract_OneMoreThanTheMaximumNumberOfColElements_IsStructureBounds()
    {
        string cols = "<cols>" + string.Concat(Enumerable.Repeat("<col min=\"1\" max=\"1\" width=\"9\"/>", SpreadsheetLimits.Default.ColumnElements + 1)) + "</cols>";

        ExtractRejected(DocumentPackageRejection.StructureBounds, Xlsx(new Sheet("S", Row(Text("A1", "x")), Cols: cols)));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Extract_ColBound_CountsEveryColElementAcrossColsElementsAtTheHookedLimit(int columnElements, bool accepted)
    {
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { ColumnElements = 3 });

        string cols = "<cols><col min=\"1\" max=\"1\"/><col min=\"2\" max=\"2\"/></cols><cols>"
            + string.Concat(Enumerable.Repeat("<col min=\"3\" max=\"3\"/>", columnElements - 2))
            + "</cols>";

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "x")), Cols: cols));

        if (accepted)
        {
            Assert.Equal("Sheet: S\nx\n", ExtractText(package, extractor));
        }
        else
        {
            ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
        }
    }

    [Fact]
    public void Extract_ColBound_AppliesPerWorksheet()
    {
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { ColumnElements = 2 });

        string cols = "<cols><col min=\"1\" max=\"1\"/><col min=\"2\" max=\"2\"/></cols>";

        byte[] package = Xlsx(new Sheet("A", Row(Text("A1", "a")), Cols: cols), new Sheet("B", Row(Text("A1", "b")), Cols: cols));

        Assert.Equal("Sheet: A\na\nSheet: B\nb\n", ExtractText(package, extractor));
    }

    // ------------------------------------------------------------ cells

    [Fact]
    public void Extract_CellBound_AllowsExactlyTheLimitAcrossRows()
    {
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { Cells = 5 });

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c")) + Row(Text("A2", "d"), Text("B2", "e"))));

        Assert.Equal("Sheet: S\na\tb\tc\nd\te\n", ExtractText(package, extractor));
    }

    [Fact]
    public void Extract_CellBound_OneMoreCellIsStructureBounds()
    {
        // Sensitivity: the processed-cell cap. An empty <c/> counts as a physical cell element.
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { Cells = 5 });

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c")) + Row(Text("A2", "d"), "<c r=\"B2\"/>", "<c r=\"C2\"/>")));

        ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
    }

    [Fact]
    public void Extract_CellBound_IsSharedByAllSheetsOfTheExtraction()
    {
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { Cells = 5 });

        byte[] package = Xlsx(
            new Sheet("A", Row(Text("A1", "a"), Text("B1", "b"), Text("C1", "c"))),
            new Sheet("B", Row(Text("A1", "d"), Text("B1", "e"), Text("C1", "f"))));

        ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
    }

    [Fact]
    public void Extract_CellBound_CountsCellsInHiddenColumns()
    {
        // A cell in a hidden column is still a processed element; hiding columns must not be a way around the cap.
        SpreadsheetDocumentExtractor extractor = new(SpreadsheetLimits.Default with { Cells = 3 });

        byte[] package = Xlsx(new Sheet(
            "S",
            Row(Text("A1", "a"), Text("B1", "h1"), Text("C1", "h2"), Text("D1", "d")),
            Cols: "<cols><col min=\"2\" max=\"3\" hidden=\"1\"/></cols>"));

        ExtractRejected(DocumentPackageRejection.StructureBounds, package, extractor);
    }

    [Fact]
    public void Extract_ProductionCellBound_RejectsOnePastTheMaximumInOneRow()
    {
        // Real limit, real parse: 1,048,577 incompressible-enough empty cells would exceed the ratio bound, so vary the references.
        StringBuilder row = new("<row>");

        long count = SpreadsheetLimits.Default.Cells + 1;

        for (long index = 0; index < count; index++)
        {
            row.Append("<c r=\"A").Append(index).Append("\"/>");
        }

        row.Append("</row>");

        byte[] package = Xlsx(new Sheet("S", row.ToString()));

        Assert.Equal(DocumentPackageRejection.StructureBounds, Assert.Throws<DocumentPackageRejectedException>(() => Extract(package)).Rejection);
    }

    // ------------------------------------------------------------ depth

    [Fact]
    public void Extract_DeeplyNestedWorksheetElements_AreStructureBounds()
    {
        // Sensitivity: reading through BoundedXmlReader (depth <= 256) rather than the raw XmlReader.
        string nested = string.Concat(Enumerable.Repeat("<x>", 300)) + string.Concat(Enumerable.Repeat("</x>", 300));

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "x")) + nested));

        ExtractRejected(DocumentPackageRejection.StructureBounds, package);
    }

    [Fact]
    public void Extract_DeeplyNestedSharedStringsElements_AreStructureBounds()
    {
        string nested = string.Concat(Enumerable.Repeat("<x>", 300)) + string.Concat(Enumerable.Repeat("</x>", 300));

        byte[] package = Xlsx([new Sheet("S", Row(Shared("A1", 0)))], Si("one") + nested);

        ExtractRejected(DocumentPackageRejection.StructureBounds, package);
    }

    // ------------------------------------------------------------ sink limits and stopping

    [Fact]
    public void Extract_OutputLongerThanTheSink_IsTruncatedAtTheCeiling()
    {
        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", new string('x', 300_000)))));

        SessionAttachmentExtractionResult result = Extract(package);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.True(result.WasTruncated);

        Assert.Equal(262_144, result.Text.Length);

        Assert.StartsWith("Sheet: S\nxxxx", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Extract_SinkThatRefusesAtAnyPoint_StopsExtractionWithoutFurtherAppends(int allowedAppends)
    {
        // Sensitivity: honouring every TryAppend result (header, separator, cell text and newline writes).
        // The last sheet is malformed: reaching it would throw, so a stop must also skip the remaining sheets.
        byte[] package = Xlsx(
            new Sheet("A", Row(Text("A1", "one"), Text("B1", "two")) + Row(Text("A2", "three"))),
            new Sheet("B", Row(Text("A1", "four"))),
            new Sheet("Malformed", string.Empty, Part: "<<< malformed"));

        StoppingSink sink = new(allowedAppends);

        new SpreadsheetDocumentExtractor().ExtractAsync(package, sink, CancellationToken.None);

        Assert.True(sink.Refused);

        Assert.Equal(0, sink.AppendsAfterRefusal);

        Assert.Equal(allowedAppends, sink.Appended.Count);
    }

    [Fact]
    public void Extract_SinkThatNeverRefuses_ReceivesTheWholeProjectionInPieces()
    {
        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "one"), Text("B1", "two"))));

        StoppingSink sink = new(int.MaxValue);

        new SpreadsheetDocumentExtractor().ExtractAsync(package, sink, CancellationToken.None);

        Assert.False(sink.Refused);

        Assert.Equal("Sheet: S\none\ttwo\n", string.Concat(sink.Appended));
    }

    // ------------------------------------------------------------ cancellation

    [Fact]
    public void Extract_AlreadyCancelledToken_ThrowsOperationCanceled()
    {
        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        byte[] package = Xlsx(new Sheet("S", Row(Text("A1", "x"))));

        BoundedDocumentTextSink sink = new(262_144, CancellationToken.None);

        Assert.Throws<OperationCanceledException>(() => new SpreadsheetDocumentExtractor().ExtractAsync(package, sink, cancellation.Token));
    }

    [Fact]
    public void Extract_CancellationBetweenParts_IsObservedBeforeTheNextWorksheetIsRead()
    {
        // Sensitivity: cancellation is checked between parts. The sink cancels while the first sheet is projected;
        // the second sheet's part must then not be read.
        using CancellationTokenSource cancellation = new();

        byte[] package = Xlsx(
            new Sheet("First", Row(Text("A1", "one"))),
            new Sheet("Second", Row(Text("A1", "two"))));

        CancellingSink sink = new(cancellation);

        Assert.Throws<OperationCanceledException>(() => new SpreadsheetDocumentExtractor().ExtractAsync(package, sink, cancellation.Token));

        Assert.DoesNotContain("two", string.Concat(sink.Appended));
    }

    [Fact]
    public void Extract_CancellationInsideALargeWorksheet_IsObservedWhileParsing()
    {
        // Sensitivity: reading through BoundedXmlReader, which checks the token every 1,024 nodes.
        using CancellationTokenSource cancellation = new();

        StringBuilder rows = new();

        for (int row = 1; row <= 3000; row++)
        {
            rows.Append(Row(Number($"A{row}", row.ToString())));
        }

        byte[] package = Xlsx(new Sheet("S", rows.ToString()));

        CancellingSink sink = new(cancellation);

        Assert.Throws<OperationCanceledException>(() => new SpreadsheetDocumentExtractor().ExtractAsync(package, sink, cancellation.Token));

        Assert.True(sink.Appended.Count < 3000);
    }

    // ------------------------------------------------------------ scale

    [Fact]
    public void Extract_ManyRows_ProjectsEveryRowInOrder()
    {
        StringBuilder rows = new();

        StringBuilder expected = new("Sheet: Big\n");

        for (int row = 1; row <= 5000; row++)
        {
            rows.Append(Row(Number($"A{row}", row.ToString()), Text($"B{row}", $"t{row}")));

            expected.Append(row).Append("\tt").Append(row).Append('\n');
        }

        BoundedDocumentTextSink sink = new(1_000_000, CancellationToken.None);

        new SpreadsheetDocumentExtractor().ExtractAsync(Xlsx(new Sheet("Big", rows.ToString())), sink, CancellationToken.None);

        Assert.Equal(expected.ToString(), sink.Complete().Text);
    }

    // ------------------------------------------------------------ helpers

    private static byte[] WorkbookWithHiddenSheets(int hiddenCount, bool visibleSheet)
    {
        StringBuilder sheets = new();

        for (int index = 0; index < hiddenCount; index++)
        {
            sheets.Append("<sheet name=\"H").Append(index).Append("\" sheetId=\"").Append(index + 1).Append("\" state=\"hidden\" r:id=\"rIdNone\"/>");
        }

        List<(string Name, string Content)> entries =
        [
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")),
        ];

        if (visibleSheet)
        {
            sheets.Append("<sheet name=\"Visible\" sheetId=\"99999\" r:id=\"rIdVisible\"/>");

            entries.Add(("xl/_rels/workbook.xml.rels", Relationships($"<Relationship Id=\"rIdVisible\" Type=\"{OfficeDocumentRelationship("worksheet")}\" Target=\"worksheets/visible.xml\"/>")));

            entries.Add(("xl/worksheets/visible.xml", $"<worksheet xmlns=\"{Main}\"><sheetData>{Row(Text("A1", "shown"))}</sheetData></worksheet>"));
        }

        entries.Add(("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets>{sheets}</sheets></workbook>"));

        return ComprehensionPackageBuilder.Build([.. entries]);
    }

    private sealed class StoppingSink(int allowedAppends) : ISessionAttachmentDocumentTextSink
    {
        public List<string> Appended { get; } = [];

        public bool Refused { get; private set; }

        public int AppendsAfterRefusal { get; private set; }

        public bool TryAppend(ReadOnlySpan<char> text)
        {
            if (Refused)
            {
                AppendsAfterRefusal++;

                return false;
            }

            if (Appended.Count >= allowedAppends)
            {
                Refused = true;

                return false;
            }

            Appended.Add(text.ToString());

            return true;
        }

        public void MarkTruncated()
        {
        }
    }

    private sealed class CancellingSink(CancellationTokenSource cancellation) : ISessionAttachmentDocumentTextSink
    {
        public List<string> Appended { get; } = [];

        public bool TryAppend(ReadOnlySpan<char> text)
        {
            Appended.Add(text.ToString());

            cancellation.Cancel();

            return true;
        }

        public void MarkTruncated()
        {
        }
    }
}
