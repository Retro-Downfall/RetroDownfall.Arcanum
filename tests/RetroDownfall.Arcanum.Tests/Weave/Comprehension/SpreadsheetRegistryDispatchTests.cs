using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>The Excel MIME types, driven through <see cref="SessionAttachmentTextExtractor.ExtractDocumentAsync"/> and the host registry.</summary>
public sealed class SpreadsheetRegistryDispatchTests
{
    private const long MaximumBytes = 1024 * 1024;

    [Theory]
    [InlineData(SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType)]
    [InlineData(SessionAttachmentComprehensionPolicy.ExcelMacroEnabledWorkbookMimeType)]
    [InlineData("APPLICATION/VND.OPENXMLFORMATS-OFFICEDOCUMENT.SPREADSHEETML.SHEET; charset=utf-8")]
    [InlineData("  application/vnd.ms-excel.sheet.macroenabled.12  ")]
    public async Task ExtractDocument_ExcelMimeTypes_DispatchToTheSpreadsheetExtractor(string mimeType)
    {
        byte[] bytes = Xlsx(
            [new Sheet("Registry", Row(Shared("A1", 0), Number("B1", "42")) + Row(Text("A2", "second")))],
            Si("hello"));

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(bytes, mimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("Sheet: Registry\nhello\t42\nsecond\n", result.Text);

        Assert.False(result.WasTruncated);

        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task ExtractDocument_RealProducerWorkbook_ExtractsThroughTheRegistry()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "Excel", "openpyxl-basic.xlsx"));

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(bytes, SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.StartsWith("Sheet: Visible\nName\tScore\tNotes\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractDocument_HiddenContentInAnXlsm_NeverReachesTheResult()
    {
        byte[] bytes = Xlsx(
            new Sheet("Shown", Row(Text("A1", "kept"), Text("B1", "HIDDENCOLUMNTEXT")), Cols: "<cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols>"),
            new Sheet("Secret", Row(Text("A1", "HIDDENSHEETTEXT")), State: "veryHidden"));

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(bytes, SessionAttachmentComprehensionPolicy.ExcelMacroEnabledWorkbookMimeType);

        Assert.Equal("Sheet: Shown\nkept\n", result.Text);
    }

    public static TheoryData<string, string, string> Rejections() => new()
    {
        { "encrypted", "NotEligible", "Encrypted document packages are not supported." },
        { "notzip", "Failed", "Document package is not a valid ZIP container." },
        { "missing", "Failed", "Document package is missing a required part." },
        { "mismatch", "Failed", "Document package does not match its declared format." },
        { "malformed", "Failed", "Document part is not well-formed XML or declares a DTD." },
        { "structure", "Failed", "Document structure exceeds its bounds." },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task ExtractDocument_RefusedPackage_MapsToTheFixedStatusAndReasonWithNoText(string scenario, string status, string reason)
    {
        byte[] bytes = scenario switch
        {
            "encrypted" => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            "notzip" => Encoding.UTF8.GetBytes("PK this is not really a zip package at all, only text"),
            "missing" => ComprehensionPackageBuilder.Build(("readme.txt", "no relationships here")),
            "mismatch" => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "textutil-basic.docx")),
            "malformed" => Xlsx(new Sheet("S", string.Empty, Part: "<<< SECRETWORKSHEETTEXT")),
            "structure" => StructureBoundsPackage(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(bytes, SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType);

        Assert.Equal(status, result.Status.ToString());

        Assert.Equal(reason, result.FailureReason);

        Assert.Empty(result.Text);

        Assert.False(result.WasTruncated);
    }

    [Theory]
    [InlineData(SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType)]
    [InlineData(SessionAttachmentComprehensionPolicy.ExcelMacroEnabledWorkbookMimeType)]
    public async Task ExtractDocument_RefusalsForEachExcelMimeType_MapToTheFixedReasonWithoutPackageText(string mimeType)
    {
        byte[] wordDocument = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "textutil-basic.docx"));

        SessionAttachmentExtractionResult mismatch = await ExtractThroughRegistryAsync(wordDocument, mimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, mismatch.Status);

        Assert.Equal("Document package does not match its declared format.", mismatch.FailureReason);

        Assert.Empty(mismatch.Text);

        byte[] malformed = Xlsx(new Sheet("S", string.Empty, Part: "<worksheet xmlns=\"" + Main + "\"><sheetData><row><c t=\"inlineStr\"><is><t>SECRETWORKSHEETTEXT"));

        SessionAttachmentExtractionResult broken = await ExtractThroughRegistryAsync(malformed, mimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, broken.Status);

        Assert.Equal("Document part is not well-formed XML or declares a DTD.", broken.FailureReason);

        Assert.Empty(broken.Text);

        Assert.DoesNotContain("SECRETWORKSHEETTEXT", broken.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractDocument_WorkbookWithNoVisibleText_IsNotEligible()
    {
        byte[] bytes = Xlsx(new Sheet("Hidden", Row(Text("A1", "HIDDENSHEETTEXT")), State: "hidden"));

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(bytes, SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Empty(result.Text);
    }

    private static byte[] StructureBoundsPackage()
    {
        StringBuilder sheets = new();

        for (int index = 0; index <= SpreadsheetLimits.Default.Sheets; index++)
        {
            sheets.Append("<sheet name=\"H").Append(index).Append("\" sheetId=\"").Append(index + 1).Append("\" state=\"hidden\" r:id=\"rIdNone\"/>");
        }

        return ComprehensionPackageBuilder.Build(
            ("_rels/.rels", Relationships($"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentRelationship("officeDocument")}\" Target=\"xl/workbook.xml\"/>")),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets>{sheets}</sheets></workbook>"));
    }

    private static Task<SessionAttachmentExtractionResult> ExtractThroughRegistryAsync(byte[] bytes, string mimeType)
    {
        SessionAttachmentRecord record = new(
            Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
            "document", "document.xlsx", 1, "untrusted/path", "hash",
            mimeType, bytes.Length, SessionAttachmentKind.Binary,
            DateTimeOffset.UtcNow);

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
        };

        return SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            record,
            settings,
            MaximumBytes,
            CancellationToken.None);
    }
}
