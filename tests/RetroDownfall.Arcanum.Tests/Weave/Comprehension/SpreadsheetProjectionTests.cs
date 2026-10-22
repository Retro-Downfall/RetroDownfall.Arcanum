using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class SpreadsheetProjectionTests
{
    [Fact]
    public void Extract_InlineStringsAndNumbers_ProjectsSheetHeaderThenTabSeparatedRows()
    {
        byte[] package = Xlsx(new Sheet("Data", Row(Text("A1", "Name"), Number("B1", "42")) + Row(Text("A2", "Alpha"), Number("B2", "3.5"))));

        Assert.Equal("Sheet: Data\nName\t42\nAlpha\t3.5\n", ExtractText(package));
    }

    [Fact]
    public void Extract_VisibleSheets_AreProjectedInWorkbookOrderNotPartNameOrder()
    {
        // Sensitivity: iterating <sheets> in document order. A scan or sort by part name would put "Alphabetical" first.
        byte[] package = Xlsx(
            new Sheet("Workbook first", Row(Text("A1", "one")), PartName: "xl/worksheets/zzz.xml"),
            new Sheet("Workbook second", Row(Text("A1", "two")), PartName: "xl/worksheets/aaa.xml"));

        Assert.Equal("Sheet: Workbook first\none\nSheet: Workbook second\ntwo\n", ExtractText(package));
    }

    [Fact]
    public void Extract_SheetWithoutANonEmptyRow_EmitsNoHeader()
    {
        // Sensitivity: writing the header lazily, at the sheet's first non-empty row.
        byte[] package = Xlsx(
            new Sheet("Blank", Row("<c r=\"A1\"/>", "<c r=\"B1\"><v></v></c>") + "<row/>"),
            new Sheet("Filled", Row(Text("A1", "kept"))));

        Assert.Equal("Sheet: Filled\nkept\n", ExtractText(package));
    }

    [Fact]
    public void Extract_WorkbookWithNoNonEmptyCell_YieldsNoText()
    {
        SessionAttachmentExtractionResult result = Extract(Xlsx(new Sheet("Blank", Row("<c r=\"A1\"/>"))));

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Empty(result.Text);
    }

    [Fact]
    public void Extract_BlankCellsAndRows_AreSkippedWithoutExtraSeparators()
    {
        // Sensitivity: joining only the non-empty cells; a naive column-aligned join would give "x\t\ty".
        byte[] package = Xlsx(new Sheet(
            "S",
            Row(Text("A1", "x"), "<c r=\"B1\"/>", Text("C1", "y"))
            + Row("<c r=\"A2\"/>", "<c r=\"B2\" t=\"str\"><v></v></c>")
            + Row(Text("A3", "z"))));

        Assert.Equal("Sheet: S\nx\ty\nz\n", ExtractText(package));
    }

    [Fact]
    public void Extract_CellsAreEmittedInDocumentOrderNotColumnOrder()
    {
        byte[] package = Xlsx(new Sheet("S", Row(Text("C1", "third"), Text("A1", "first"))));

        Assert.Equal("Sheet: S\nthird\tfirst\n", ExtractText(package));
    }

    [Fact]
    public void Extract_ValueTypes_ProjectTheRawCachedValueWithoutNumberFormats()
    {
        // t="str", "e", "d", "n" and an absent t all project the raw v text; a date is its serial number.
        byte[] package = Xlsx(new Sheet(
            "S",
            Row("<c r=\"A1\" t=\"str\"><v>cached text</v></c>")
            + Row("<c r=\"A2\" t=\"e\"><v>#N/A</v></c>")
            + Row("<c r=\"A3\" t=\"d\"><v>2024-01-15T00:00:00Z</v></c>")
            + Row("<c r=\"A4\" t=\"n\"><v>1.5E-3</v></c>")
            + Row("<c r=\"A5\" s=\"3\"><v>45306</v></c>")));

        Assert.Equal("Sheet: S\ncached text\n#N/A\n2024-01-15T00:00:00Z\n1.5E-3\n45306\n", ExtractText(package));
    }

    [Theory]
    [InlineData("1", "TRUE")]
    [InlineData("0", "FALSE")]
    [InlineData("true", "TRUE")]
    [InlineData("false", "FALSE")]
    public void Extract_BooleanCell_ProjectsTrueOrFalse(string raw, string expected)
    {
        byte[] package = Xlsx(new Sheet("S", Row($"<c r=\"A1\" t=\"b\"><v>{raw}</v></c>")));

        Assert.Equal($"Sheet: S\n{expected}\n", ExtractText(package));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("yes")]
    [InlineData("")]
    public void Extract_BooleanCellWithAnUnrecognizedValue_ProjectsNothing(string raw)
    {
        byte[] package = Xlsx(new Sheet("S", Row($"<c r=\"A1\" t=\"b\"><v>{raw}</v></c>") + Row(Text("A2", "after"))));

        Assert.Equal("Sheet: S\nafter\n", ExtractText(package));
    }

    [Fact]
    public void Extract_BooleanValueLongerThanTheParseCapacity_IsNotReadFromItsPrefix()
    {
        // Sensitivity: an over-long boolean value is no boolean at all. Its first text node ("1") fits the 8-character
        // capacity, the CDATA node after it does not; reading what fit would project TRUE.
        byte[] package = Xlsx(new Sheet("S", Row("<c r=\"A1\" t=\"b\"><v>1<![CDATA[000000000]]></v></c>") + Row(Text("A2", "after"))));

        Assert.Equal("Sheet: S\nafter\n", ExtractText(package));
    }

    [Fact]
    public void Extract_FormulaCell_ProjectsTheCachedValueAndNeverTheFormula()
    {
        // Sensitivity: reading only v. The formula text, which can carry hostile instructions, must not reach the output.
        byte[] package = Xlsx(new Sheet(
            "S",
            Row("<c r=\"A1\"><f>SECRETFORMULA(B1)</f><v>7</v></c>")
            + Row("<c r=\"A2\" t=\"str\"><f>CONCAT(\"SECRETFORMULA\",1)</f><v>shown</v></c>")
            + Row("<c r=\"A3\"><f>SECRETFORMULA(2)</f></c>")
            + Row("<c r=\"A4\"><f>SECRETFORMULA(3)</f><v></v></c>")
            + Row("<c r=\"A5\" t=\"b\"><f>SECRETFORMULA(4)</f><v>1</v></c>")));

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\n7\nshown\nTRUE\n", text);

        Assert.DoesNotContain("SECRETFORMULA", text);
    }

    [Fact]
    public void Extract_CellContent_FollowsTheCellTypeAndTakesOnlyTheFirstValue()
    {
        // Sensitivity: an inline string reads <is> and never <v>; every other type reads <v> and never <is>; one value per cell.
        byte[] package = Xlsx(new Sheet(
            "S",
            Row("<c r=\"A1\" t=\"inlineStr\"><v>NOTUSEDVALUE</v><is><t>inline</t></is></c>")
            + Row("<c r=\"A2\" t=\"n\"><is><t>NOTUSEDINLINE</t></is><v>5</v></c>")
            + Row("<c r=\"A3\"><v>1</v><v>NOTUSEDSECOND</v></c>")
            + Row("<c r=\"A4\" t=\"inlineStr\"><is><t>first</t></is><is><t>NOTUSEDSECOND</t></is></c>")
            + Row("<c r=\"A5\" t=\"str\"><extLst><ext><x>NOTUSEDEXT</x></ext></extLst><v>after ext</v></c>")));

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\ninline\n5\n1\nfirst\nafter ext\n", text);

        Assert.DoesNotContain("NOTUSED", text);
    }

    [Fact]
    public void Extract_TextValues_KeepSpacesEntitiesAndCdataExactly()
    {
        byte[] package = Xlsx(new Sheet(
            "S",
            Row("<c r=\"A1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">  padded  </t></is></c>")
            + Row("<c r=\"A2\" t=\"inlineStr\"><is><t>a &amp; b &lt; c &#233;</t></is></c>")
            + Row("<c r=\"A3\" t=\"inlineStr\"><is><t><![CDATA[<raw> & text]]></t></is></c>")));

        Assert.Equal("Sheet: S\n  padded  \na & b < c é\n<raw> & text\n", ExtractText(package));
    }

    [Fact]
    public void Extract_InlineString_ConcatenatesDirectTextAndRunsAndIgnoresPhoneticRuns()
    {
        // Sensitivity: skipping rPh/phoneticPr, and descending into r/t.
        byte[] package = Xlsx(new Sheet(
            "S",
            Row("<c r=\"A1\" t=\"inlineStr\"><is><t>Direct</t><r><rPr><b/></rPr><t> run</t></r><rPh sb=\"0\" eb=\"1\"><t>PHONETICREADING</t></rPh><phoneticPr fontId=\"1\"/></is></c>")));

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\nDirect run\n", text);
    }

    [Fact]
    public void Extract_ElementsNestedInsideTextElements_ContributeNoText()
    {
        // Sensitivity: skipping a nested element instead of reading into it, for both an inline string's t and a cell's v.
        byte[] package = Xlsx(new Sheet(
            "S",
            Row("<c r=\"A1\" t=\"inlineStr\"><is><t>a<x>NESTEDTEXT</x>b</t></is></c>")
            + Row("<c r=\"A2\"><v>1<x>NESTEDVALUE</x>2</v></c>")));

        string text = ExtractText(package);

        Assert.Equal("Sheet: S\nab\n12\n", text);

        Assert.DoesNotContain("NESTED", text);
    }

    [Fact]
    public void Extract_SheetNameWithMarkupCharacters_IsEmittedAsPlainText()
    {
        byte[] package = Xlsx(new Sheet("A &amp; B &lt;i&gt;", Row(Text("A1", "v"))));

        Assert.Equal("Sheet: A & B <i>\nv\n", ExtractText(package));
    }

    [Fact]
    public void Extract_OnlySheetNamesAndCellValuesReachTheOutput()
    {
        // Attribute values (sheetId, r:id), part names and relationship targets are never projected.
        byte[] package = Xlsx(new Sheet("Named", Row(Text("A1", "cell")), PartName: "xl/worksheets/PARTNAMEMARKER.xml"));

        string text = ExtractText(package);

        Assert.Equal("Sheet: Named\ncell\n", text);

        Assert.DoesNotContain("PARTNAMEMARKER", text);

        Assert.DoesNotContain("rId", text);
    }

    [Fact]
    public void Extract_PrefixedElementsAndAnyDeclaredPrefix_AreMatchedByNamespaceNotPrefix()
    {
        // Sensitivity: matching LocalName plus NamespaceURI. Prefixes are arbitrary; the default namespace is not special.
        const string workbook =
            "<x:workbook xmlns:x=\"" + Main + "\" xmlns:rel=\"" + Rel + "\"><x:sheets><x:sheet name=\"Prefixed\" sheetId=\"1\" rel:id=\"rId1\"/></x:sheets></x:workbook>";

        const string sheet =
            "<w:worksheet xmlns:w=\"" + Main + "\"><w:sheetData><w:row><w:c r=\"A1\" t=\"inlineStr\"><w:is><w:t>prefixed ok</w:t></w:is></w:c></w:row></w:sheetData></w:worksheet>";

        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")),
            ("xl/workbook.xml", workbook),
            ("xl/_rels/workbook.xml.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("worksheet")}\" Target=\"worksheets/sheet1.xml\"/>")),
            ("xl/worksheets/sheet1.xml", sheet));

        Assert.Equal("Sheet: Prefixed\nprefixed ok\n", ExtractText(package));
    }

    [Fact]
    public void Extract_ElementsInAnotherNamespaceWithSpreadsheetNames_AreIgnored()
    {
        const string decoyNamespace = "urn:decoy";

        string rows = Row(Text("A1", "real"))
            + $"<row xmlns=\"{decoyNamespace}\"><c r=\"A1\" t=\"inlineStr\"><is><t>DECOYTEXT</t></is></c></row>"
            + Row($"<c xmlns=\"{decoyNamespace}\" r=\"A1\" t=\"inlineStr\"><is><t>DECOYCELL</t></is></c>", Text("B1", "kept"));

        string text = ExtractText(Xlsx(new Sheet("S", rows)));

        Assert.Equal("Sheet: S\nreal\nkept\n", text);
    }
}

/// <summary>Builders for minimal SpreadsheetML packages shared by the spreadsheet extractor tests.</summary>
internal static class ExcelWorkbookFixtures
{
    public const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public const string StrictMain = "http://purl.oclc.org/ooxml/spreadsheetml/main";

    public const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const string StrictRel = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    public const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>One worksheet part. <paramref name="Rows"/> is the inner XML of <c>sheetData</c>.</summary>
    internal sealed record Sheet(
        string Name,
        string Rows,
        string State = "",
        string Cols = "",
        string Kind = "worksheet",
        string? PartName = null,
        string? Part = null,
        string AfterSheetData = "",
        string? RelationshipId = null,
        string? Target = null,
        string? Type = null,
        Func<string, string>? WrapListing = null);

    public static string OfficeDocumentRelationship(string kind, bool strict = false) =>
        (strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships/" : "http://schemas.openxmlformats.org/officeDocument/2006/relationships/") + kind;

    public static string Relationships(params string[] relationships) =>
        $"<Relationships xmlns=\"{PackageRelationshipsNamespace}\">{string.Concat(relationships)}</Relationships>";

    public static string Row(params string[] cells) => $"<row>{string.Concat(cells)}</row>";

    public static string Text(string reference, string text) =>
        $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    public static string Number(string reference, string value) => $"<c r=\"{reference}\"><v>{value}</v></c>";

    public static string Shared(string reference, int index) => $"<c r=\"{reference}\" t=\"s\"><v>{index}</v></c>";

    public static string Si(string text) => $"<si><t>{text}</t></si>";

    /// <summary>
    /// A complete workbook package: package-root officeDocument relationship, workbook part, workbook relationships,
    /// one worksheet part per sheet, and an optional shared strings part (<paramref name="sharedStringItems"/> is the inner XML of <c>sst</c>).
    /// </summary>
    public static byte[] Xlsx(params Sheet[] sheets) => Xlsx(sheets, null);

    public static byte[] Xlsx(
        Sheet[] sheets,
        string? sharedStringItems,
        bool strict = false,
        (string Name, string Content)[]? extraEntries = null,
        string extraWorkbookRelationships = "",
        string extraWorkbookXml = "",
        RawZipEntry[]? rawEntries = null,
        string extraWorkbookXmlAfterSheets = "",
        Func<string, string>? wrapSheets = null)
    {
        string main = strict ? StrictMain : Main;

        string rel = strict ? StrictRel : Rel;

        List<(string Name, string Content)> entries = [];

        entries.Add(("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument", strict)}\" Target=\"xl/workbook.xml\"/>")));

        StringBuilder sheetElements = new();

        StringBuilder relationships = new();

        for (int index = 0; index < sheets.Length; index++)
        {
            Sheet sheet = sheets[index];

            string partName = sheet.PartName ?? $"xl/worksheets/sheet{index + 1}.xml";

            StringBuilder listing = new();

            listing.Append($"<sheet name=\"{sheet.Name}\" sheetId=\"{index + 1}\"");

            if (sheet.State.Length > 0)
            {
                listing.Append($" state=\"{sheet.State}\"");
            }

            listing.Append($" r:id=\"{sheet.RelationshipId ?? $"rIdS{index + 1}"}\"/>");

            sheetElements.Append(sheet.WrapListing is null ? listing.ToString() : sheet.WrapListing(listing.ToString()));

            relationships.Append($"<Relationship Id=\"rIdS{index + 1}\" Type=\"{sheet.Type ?? OfficeDocumentRelationship(sheet.Kind, strict)}\" Target=\"{sheet.Target ?? $"/{partName}"}\"/>");

            entries.Add((partName, sheet.Part ?? $"<worksheet xmlns=\"{main}\" xmlns:r=\"{rel}\">{sheet.Cols}<sheetData>{sheet.Rows}</sheetData>{sheet.AfterSheetData}</worksheet>"));
        }

        if (sharedStringItems is not null)
        {
            relationships.Append($"<Relationship Id=\"rIdSS\" Type=\"{OfficeDocumentRelationship("sharedStrings", strict)}\" Target=\"sharedStrings.xml\"/>");

            entries.Add(("xl/sharedStrings.xml", $"<sst xmlns=\"{main}\">{sharedStringItems}</sst>"));
        }

        relationships.Append(extraWorkbookRelationships);

        string sheetsElement = $"<sheets>{sheetElements}</sheets>";

        entries.Add(("xl/workbook.xml", $"<workbook xmlns=\"{main}\" xmlns:r=\"{rel}\">{extraWorkbookXml}{(wrapSheets is null ? sheetsElement : wrapSheets(sheetsElement))}{extraWorkbookXmlAfterSheets}</workbook>"));

        entries.Add(("xl/_rels/workbook.xml.rels", Relationships(relationships.ToString())));

        if (extraEntries is not null)
        {
            entries.AddRange(extraEntries);
        }

        if (rawEntries is null)
        {
            return ComprehensionPackageBuilder.Build([.. entries]);
        }

        RawZipPackage raw = new();

        foreach ((string name, string content) in entries)
        {
            raw.Entries.Add(RawZipEntry.Deflated(name, content));
        }

        raw.Entries.AddRange(rawEntries);

        return raw.ToBytes();
    }

    /// <summary>Runs the extractor over <paramref name="package"/> and returns the completed sink result.</summary>
    public static SessionAttachmentExtractionResult Extract(
        byte[] package,
        SpreadsheetDocumentExtractor? extractor = null,
        int maximumCharacters = 262_144)
    {
        BoundedDocumentTextSink sink = new(maximumCharacters, CancellationToken.None);

        ValueTask pending = (extractor ?? new SpreadsheetDocumentExtractor()).ExtractAsync(package, sink, CancellationToken.None);

        Assert.True(pending.IsCompletedSuccessfully, "The spreadsheet extractor must complete synchronously.");

        return sink.Complete();
    }

    /// <summary>Extracts and asserts the status is <c>Extracted</c>, returning the text.</summary>
    public static string ExtractText(byte[] package, SpreadsheetDocumentExtractor? extractor = null)
    {
        SessionAttachmentExtractionResult result = Extract(package, extractor);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.False(result.WasTruncated);

        return result.Text;
    }

    /// <summary>Extracts and asserts the extractor refuses with the given closed rejection, whose message names nothing from the package.</summary>
    public static DocumentPackageRejectedException ExtractRejected(
        DocumentPackageRejection expected,
        byte[] package,
        SpreadsheetDocumentExtractor? extractor = null)
    {
        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(() => Extract(package, extractor));

        Assert.Equal(expected, exception.Rejection);

        return exception;
    }
}
