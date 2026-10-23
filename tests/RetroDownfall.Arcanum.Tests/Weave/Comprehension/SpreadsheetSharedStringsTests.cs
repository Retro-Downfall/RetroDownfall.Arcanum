using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class SpreadsheetSharedStringsTests
{
    [Fact]
    public void Extract_SharedStringCells_ResolveByIndexInAnyOrder()
    {
        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 2), Shared("B1", 0)) + Row(Shared("A2", 1), Shared("B2", 2)))],
            Si("zero") + Si("one") + Si("two"));

        Assert.Equal("Sheet: S\ntwo\tzero\none\ttwo\n", ExtractText(package));
    }

    [Fact]
    public void Extract_SharedStringWithRichTextRuns_ConcatenatesDirectTextAndRunText()
    {
        // Sensitivity: descending into r/t as well as the direct t, and keeping the order of the parts.
        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 0), Shared("B1", 1)))],
            "<si><r><t xml:space=\"preserve\">Rich </t></r><r><rPr><b/><sz val=\"11\"/></rPr><t>bold</t></r><r><t xml:space=\"preserve\"> text</t></r></si>"
            + "<si><t>Direct </t><r><t>then run</t></r></si>");

        Assert.Equal("Sheet: S\nRich bold text\tDirect then run\n", ExtractText(package));
    }

    [Fact]
    public void Extract_WhitespaceOnlyRunBetweenRichTextRuns_IsKept()
    {
        // Sensitivity: whitespace text nodes. A run that is only a space (with or without xml:space="preserve") is the
        // separator between two styled words, so dropping it would glue the words together.
        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 0), Shared("B1", 1)))],
            "<si><r><t>bold</t></r><r><t xml:space=\"preserve\"> </t></r><r><t>red</t></r></si>"
            + "<si><r><t>plain</t></r><r><t> </t></r><r><t>space</t></r></si>");

        Assert.Equal("Sheet: S\nbold red\tplain space\n", ExtractText(package));
    }

    [Fact]
    public void Extract_SharedStringPhoneticRuns_AreExcluded()
    {
        // Sensitivity: skipping rPh and phoneticPr. The phonetic reading is not what the cell displays.
        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 0), Shared("B1", 1)))],
            "<si><t>Base</t><rPh sb=\"0\" eb=\"4\"><t>PHONETICREADING</t></rPh><phoneticPr fontId=\"1\" type=\"noConversion\"/></si>"
            + "<si><r><t>Run</t></r><rPh sb=\"0\" eb=\"3\"><r><t>NESTEDPHONETIC</t></r></rPh></si>");

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\nBase\tRun\n", text);

        Assert.DoesNotContain("PHONETIC", text);
    }

    [Fact]
    public void Extract_EmptySharedStrings_StillOccupyTheirIndex()
    {
        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 3), Shared("B1", 0), Shared("C1", 1), Shared("D1", 2)))],
            "<si/><si><t/></si><si><t></t></si><si><t>fourth</t></si>");

        Assert.Equal("Sheet: S\nfourth\n", ExtractText(package));
    }

    [Fact]
    public void Extract_SharedStringText_KeepsSpacesAndEntitiesExactly()
    {
        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 0)))],
            "<si><t xml:space=\"preserve\">  a &amp; b  </t></si>");

        Assert.Equal("Sheet: S\n  a & b  \n", ExtractText(package));
    }

    [Fact]
    public void Extract_SameSharedStringReferencedTwice_IsProjectedTwice()
    {
        byte[] package = Xlsx([new Sheet("S", Row(Shared("A1", 0), Shared("B1", 0)))], Si("again"));

        Assert.Equal("Sheet: S\nagain\tagain\n", ExtractText(package));
    }

    [Fact]
    public void Extract_LargeSharedStringTable_ResolvesTheLastIndex()
    {
        StringBuilder items = new();

        for (int index = 0; index < 300; index++)
        {
            items.Append(Si($"item{index}"));
        }

        byte[] package = Xlsx([new Sheet("S", Row(Shared("A1", 299), Shared("B1", 150)))], items.ToString());

        Assert.Equal("Sheet: S\nitem299\titem150\n", ExtractText(package));
    }

    [Fact]
    public void Extract_SharedStringIndexBeyondAFullyRetainedTable_ProjectsNothingForThatCellAndIsNotTruncated()
    {
        // Sensitivity: telling "out of range" (silent) apart from "past retention" (truncated).
        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 5), Shared("B1", 0)) + Row(Shared("A2", 2)) + Row(Text("A3", "after")))],
            Si("only") + Si("two"));

        SessionAttachmentExtractionResult result = Extract(package);

        Assert.Equal("Sheet: S\nonly\nafter\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("0x1")]
    [InlineData("1e2")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("")]
    [InlineData("4294967296")]
    [InlineData("4294967297")]
    [InlineData("2147483648")]
    [InlineData("9999999999999999")]
    [InlineData("99999999999999999999999999")]
    [InlineData("1234567890123456789012345678901234567890")]
    public void Extract_SharedStringCellWithAnInvalidIndex_ProjectsNothing(string index)
    {
        // Sensitivity: digits-only parsing and the int range. The table is large enough that a lenient parse of
        // "1.5" (85), "1e2" (632), "abc" (5451) or a wrapped "4294967296" (0) would land on a real string.
        StringBuilder items = new();

        for (int item = 0; item < 6000; item++)
        {
            items.Append(Si($"item{item}"));
        }

        byte[] package = Xlsx(
            [new Sheet("S", Row($"<c r=\"A1\" t=\"s\"><v>{index}</v></c>") + Row(Text("A2", "after")))],
            items.ToString());

        SessionAttachmentExtractionResult result = Extract(package);

        Assert.Equal("Sheet: S\nafter\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void Extract_SharedStringIndexLongerThanTheParseCapacity_IsNotParsedFromItsPrefix()
    {
        // Sensitivity: an over-long value is not an index at all. Its first text node ("1") fits the 16-character capacity,
        // the CDATA node after it does not; reading what fit would resolve index 1 and project "one".
        byte[] package = Xlsx(
            [new Sheet("S", Row("<c r=\"A1\" t=\"s\"><v>1<![CDATA[00000000000000000]]></v></c>") + Row(Text("A2", "after")))],
            Si("zero") + Si("one"));

        SessionAttachmentExtractionResult result = Extract(package);

        Assert.Equal("Sheet: S\nafter\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void Extract_SharedStringCellWithoutAValueElement_ProjectsNothing()
    {
        byte[] package = Xlsx([new Sheet("S", Row("<c r=\"A1\" t=\"s\"/>", "<c r=\"B1\" t=\"s\"><f>x</f></c>", Text("C1", "kept")))], Si("zero"));

        Assert.Equal("Sheet: S\nkept\n", ExtractText(package));
    }

    [Fact]
    public void Extract_SharedStringCellsWithNoSharedStringsPart_ProjectNothing()
    {
        byte[] package = Xlsx(new Sheet("S", Row(Shared("A1", 0)) + Row(Text("A2", "inline"))));

        Assert.Equal("Sheet: S\ninline\n", ExtractText(package));
    }

    [Fact]
    public void Extract_StrictNamespaces_ReadWorkbookWorksheetAndSharedStrings()
    {
        byte[] package = Xlsx(
            [new Sheet("Strict", Row(Shared("A1", 0), Text("B1", "inline")))],
            Si("strict shared"),
            strict: true);

        Assert.Equal("Sheet: Strict\nstrict shared\tinline\n", ExtractText(package));
    }

    // ------------------------------------------------------------ retention (test hook: smaller limits)

    [Fact]
    public void Extract_SharedStringPastTheCharacterRetention_TruncatesAndStopsExtraction()
    {
        // Sensitivity: the character budget. "ABCDE" would be the third string, taking the total to 15 of 10.
        SpreadsheetDocumentExtractor extractor = Extractor(SpreadsheetLimits.Default with { SharedStringCharacters = 10 });

        byte[] package = Xlsx(
            [
                new Sheet("S", Row(Shared("A1", 0), Shared("B1", 1)) + Row(Shared("A2", 2)) + Row(Shared("A3", 0))),
                new Sheet("Later", Row(Text("A1", "LATERSHEET"))),
            ],
            Si("12345") + Si("67890") + Si("ABCDE"));

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.True(result.WasTruncated);

        Assert.Equal("Sheet: S\n12345\t67890\n", result.Text);
    }

    [Fact]
    public void Extract_SharedStringTableExactlyAtTheCharacterRetention_IsFullyRetained()
    {
        // Boundary: 5 + 5 characters against a limit of 10 is within retention, so an out-of-range index stays silent.
        SpreadsheetDocumentExtractor extractor = Extractor(SpreadsheetLimits.Default with { SharedStringCharacters = 10 });

        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 1), Shared("B1", 0), Shared("C1", 2)))],
            Si("12345") + Si("67890"));

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.False(result.WasTruncated);

        Assert.Equal("Sheet: S\n67890\t12345\n", result.Text);
    }

    [Fact]
    public void Extract_SharedStringPastTheCountRetention_TruncatesAndStopsExtraction()
    {
        // Sensitivity: the string-count budget (here 2). The third string is not stored.
        SpreadsheetDocumentExtractor extractor = Extractor(SpreadsheetLimits.Default with { SharedStrings = 2 });

        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 1), Shared("B1", 0)) + Row(Shared("A2", 2)) + Row(Text("A3", "NEVER")))],
            Si("a") + Si("b") + Si("c"));

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.True(result.WasTruncated);

        Assert.Equal("Sheet: S\nb\ta\n", result.Text);
    }

    [Fact]
    public void Extract_TruncationFromASharedStringReference_StopsBeforeLaterSheetsAreRead()
    {
        // Sensitivity: stopping at the truncation. The next sheet is malformed: reading it would be a MalformedXml refusal.
        SpreadsheetDocumentExtractor extractor = Extractor(SpreadsheetLimits.Default with { SharedStrings = 1 });

        byte[] package = Xlsx(
            [
                new Sheet("S", Row(Text("A1", "kept"), Shared("B1", 1))),
                new Sheet("Malformed", string.Empty, Part: "<<< malformed"),
            ],
            Si("a") + Si("b"));

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.True(result.WasTruncated);

        Assert.Equal("Sheet: S\nkept", result.Text);
    }

    [Fact]
    public void Extract_TruncatingReferenceAloneInItsRow_StillStopsBeforeLaterSheetsAreRead()
    {
        // Sensitivity: stopping at the truncation itself. With nothing else written after it (the truncating cell is the
        // only cell of its row and the last of its sheet) nothing else would make the sink refuse, so only an explicit
        // stop keeps the malformed next sheet from being read.
        SpreadsheetDocumentExtractor extractor = Extractor(SpreadsheetLimits.Default with { SharedStrings = 1 });

        byte[] package = Xlsx(
            [
                new Sheet("S", Row(Text("A1", "kept")) + Row(Shared("A2", 1))),
                new Sheet("Malformed", string.Empty, Part: "<<< malformed"),
            ],
            Si("a") + Si("b"));

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.True(result.WasTruncated);

        Assert.Equal("Sheet: S\nkept\n", result.Text);
    }

    [Fact]
    public void Extract_SharedStringTableExactlyAtTheCountRetention_IsFullyRetained()
    {
        SpreadsheetDocumentExtractor extractor = Extractor(SpreadsheetLimits.Default with { SharedStrings = 2 });

        byte[] package = Xlsx([new Sheet("S", Row(Shared("A1", 1), Shared("B1", 2)))], Si("a") + Si("b"));

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.False(result.WasTruncated);

        Assert.Equal("Sheet: S\nb\n", result.Text);
    }

    [Fact]
    public void Extract_FirstSharedStringLargerThanTheRetention_IsNotStoredAndTheReferenceTruncates()
    {
        // A string is stored whole or not at all; a partially retained string would be wrong text.
        SpreadsheetDocumentExtractor extractor = Extractor(SpreadsheetLimits.Default with { SharedStringCharacters = 4 });

        byte[] package = Xlsx([new Sheet("S", Row(Text("A1", "head")) + Row(Shared("A2", 0)))], Si("toolong"));

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.True(result.WasTruncated);

        Assert.Equal("Sheet: S\nhead\n", result.Text);
    }

    [Fact]
    public void Extract_SharedStringWhoseRunsTogetherExceedTheRetention_IsNotStored()
    {
        // Sensitivity: the budget covers the whole string, not each run. Both runs fit the 10-character budget alone;
        // together (12) they do not, so the string is dropped and a reference to it truncates.
        SpreadsheetDocumentExtractor extractor = Extractor(SpreadsheetLimits.Default with { SharedStringCharacters = 10 });

        byte[] package = Xlsx(
            [new Sheet("S", Row(Text("A1", "head")) + Row(Shared("A2", 0)))],
            "<si><r><t>123456</t></r><r><t>789012</t></r></si>");

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.True(result.WasTruncated);

        Assert.Equal("Sheet: S\nhead\n", result.Text);
    }

    [Fact]
    public void Extract_ElementsInTheSharedStringTableOtherThanSi_AreNotStrings()
    {
        // Sensitivity: only si elements are strings. Anything else (an extension list, a stray t) must not take an index
        // and shift the strings after it.
        byte[] package = Xlsx(
            [new Sheet("S", Row(Shared("A1", 1), Shared("B1", 0)))],
            Si("zero")
            + "<extLst><ext><t>NOTASTRING</t></ext></extLst>"
            + "<t>NOTASTRINGEITHER</t>"
            + Si("one"));

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\none\tzero\n", text);

        Assert.DoesNotContain("NOTASTRING", text);
    }

    [Fact]
    public void Extract_UnreferencedStringsPastRetention_StillTruncateOnlyWhenReferenced()
    {
        // The overflowing table is harmless to cells that never reference past the retained prefix.
        SpreadsheetDocumentExtractor extractor = Extractor(SpreadsheetLimits.Default with { SharedStrings = 1 });

        byte[] package = Xlsx([new Sheet("S", Row(Shared("A1", 0), Text("B1", "inline")))], Si("kept") + Si("dropped"));

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.False(result.WasTruncated);

        Assert.Equal("Sheet: S\nkept\tinline\n", result.Text);
    }

    [Fact]
    public void Extract_IndexBeyondAnOverflowedTable_IsTreatedAsPastRetention()
    {
        // After an overflow the table size is unknown, so any index past the retained prefix is conservatively truncation.
        SpreadsheetDocumentExtractor extractor = Extractor(SpreadsheetLimits.Default with { SharedStrings = 1 });

        byte[] package = Xlsx([new Sheet("S", Row(Shared("A1", 99)))], Si("kept") + Si("dropped"));

        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.True(result.WasTruncated);

        Assert.Empty(result.Text);
    }

    private static SpreadsheetDocumentExtractor Extractor(SpreadsheetLimits limits) => new(limits);
}
