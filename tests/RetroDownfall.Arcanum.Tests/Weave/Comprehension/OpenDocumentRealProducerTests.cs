using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Fixtures;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.OpenDocumentFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The OpenDocument extractors against packages a real producer wrote (macOS <c>textutil</c>) and against hand-built
/// .ods and .odp packages in LibreOffice's layout. See <c>TestData/Comprehension/OpenDocument/README.md</c> for how each
/// file was made.
/// </summary>
public sealed class OpenDocumentRealProducerTests
{
    private static readonly string[] MetadataAndFurnitureMarkers =
    [
        "METADATATITLEMARKER",
        "METADATACREATORMARKER",
        "SETTINGSMARKER",
        "HEADERMARKER",
        "FOOTERMARKER",
    ];

    // ---------------------------------------------------------------- textutil .odt (a real producer)

    [Fact]
    public async Task TextutilBasicOdt_ExtractsItsParagraph()
    {
        // The manifest carries the OpenOffice DOCTYPE, office:version is 1.0, and the paragraph uses a parent style.
        byte[] package = File.ReadAllBytes(PathOf("textutil-basic.odt", folder: string.Empty));

        Assert.Equal("Alpha\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task TextutilRichOdt_ExtractsRunsTabsBreaksLinksAndTableCells()
    {
        byte[] package = File.ReadAllBytes(PathOf("textutil-rich.odt"));

        string text = await TextOfAsync<OpenDocumentTextExtractor>(package);

        Assert.Equal(
            "Quarterly Report\n"
            + "Plain paragraph with bold, italic and underlined words.\n"
            + "Tabbed\tcolumns\there\n"
            + "First line\nSecond line after a break\n"
            + "Linked words\n"
            + "Cell A1\nCell B1\nCell A2\nCell B2\n"
            + "Closing paragraph.\n",
            text);

        Assert.DoesNotContain("example.com", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("textutil-basic.odt", "")]
    [InlineData("textutil-rich.odt", "OpenDocument")]
    public async Task TextutilOdt_IsRefusedByTheOtherOpenDocumentExtractors(string fileName, string folder)
    {
        byte[] package = File.ReadAllBytes(PathOf(fileName, folder));

        await RejectedAsync<OpenDocumentSpreadsheetExtractor>(package, DocumentPackageRejection.FormatMismatch);

        await RejectedAsync<OpenDocumentPresentationExtractor>(package, DocumentPackageRejection.FormatMismatch);
    }

    // ---------------------------------------------------------------- hand-built .ods

    [Theory]
    [InlineData("handmade-libreoffice-style.ods")]
    [InlineData("handmade-doctype-manifest.ods")]
    public async Task HandmadeOds_ProjectsVisibleCellsAndNothingHidden(string fileName)
    {
        byte[] package = File.ReadAllBytes(PathOf(fileName));

        string text = await TextOfAsync<OpenDocumentSpreadsheetExtractor>(package);

        Assert.Equal(
            "Sheet: Inventory\n"
            + "Item\tQuantity\n"
            + "Widget\t12\n"
            + "Gadget\t7\n"
            + "Total\t19\n"
            + "Merged heading\tTail\n"
            + "Two paragraphs\n",
            text);

        AssertNoMarkers(text, "SECRETCOLUMN", "HIDDENROWTEXT", "FILTEREDROWTEXT", "FORMULAMARKER", "CELLNOTEMARKER", "COVEREDCELLMARKER", "HIDDENSHEETTEXT");
    }

    [Theory]
    [InlineData("handmade-libreoffice-style.ods")]
    [InlineData("handmade-doctype-manifest.ods")]
    public async Task HandmadeOds_IsRefusedByTheOtherOpenDocumentExtractors(string fileName)
    {
        byte[] package = File.ReadAllBytes(PathOf(fileName));

        await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.FormatMismatch);

        await RejectedAsync<OpenDocumentPresentationExtractor>(package, DocumentPackageRejection.FormatMismatch);
    }

    // ---------------------------------------------------------------- hand-built .odp

    [Theory]
    [InlineData("handmade-libreoffice-style.odp")]
    [InlineData("handmade-doctype-manifest.odp")]
    public async Task HandmadeOdp_ProjectsVisibleSlidesAndNothingHidden(string fileName)
    {
        byte[] package = File.ReadAllBytes(PathOf(fileName));

        string text = await TextOfAsync<OpenDocumentPresentationExtractor>(package);

        Assert.Equal(
            "Slide 1\n"
            + "Quarterly Review\n"
            + "Revenue grew 12%\n"
            + "Costs held flat  year on year\n"
            + "Callout\n"
            + "Slide 3\n"
            + "Region\nSales\nNorth\n41\n",
            text);

        AssertNoMarkers(
            text,
            "HIDDENSHAPEMARKER",
            "SLIDECOMMENTMARKER",
            "SPEAKERNOTESMARKER",
            "HIDDENSLIDEMARKER",
            "IMAGETITLEMARKER",
            "IMAGEDESCRIPTIONMARKER",
            "MASTERFOOTERMARKER",
            "MASTERNOTESMARKER",
            "DECLAREDFOOTERMARKER",
            "DECLAREDDATEMARKER");
    }

    [Theory]
    [InlineData("handmade-libreoffice-style.odp")]
    [InlineData("handmade-doctype-manifest.odp")]
    public async Task HandmadeOdp_IsRefusedByTheOtherOpenDocumentExtractors(string fileName)
    {
        byte[] package = File.ReadAllBytes(PathOf(fileName));

        await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.FormatMismatch);

        await RejectedAsync<OpenDocumentSpreadsheetExtractor>(package, DocumentPackageRejection.FormatMismatch);
    }

    // ---------------------------------------------------------------- through the registry

    [Theory]
    [InlineData("textutil-rich.odt", SessionAttachmentComprehensionPolicy.OpenDocumentTextMimeType, "Quarterly Report\n")]
    [InlineData("handmade-libreoffice-style.ods", SessionAttachmentComprehensionPolicy.OpenDocumentSpreadsheetMimeType, "Sheet: Inventory\nItem\tQuantity\n")]
    [InlineData("handmade-libreoffice-style.odp", SessionAttachmentComprehensionPolicy.OpenDocumentPresentationMimeType, "Slide 1\nQuarterly Review\n")]
    public async Task ExtractDocument_RealFilesDispatchThroughTheRegistryUnderTheirMimeType(string fileName, string mimeType, string expectedPrefix)
    {
        byte[] package = File.ReadAllBytes(PathOf(fileName));

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
        };

        SessionAttachmentRecord record = new(
            Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
            "document", fileName, 1, "untrusted/path", "hash", mimeType, package.Length, SessionAttachmentKind.Binary,
            DateTimeOffset.UtcNow);

        SessionAttachmentExtractionResult result = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(package)),
            record,
            settings,
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.StartsWith(expectedPrefix, result.Text, StringComparison.Ordinal);

        AssertNoMarkers(result.Text, MetadataAndFurnitureMarkers);
    }

    private static void AssertNoMarkers(string text, params string[] markers)
    {
        foreach (string marker in markers.Concat(MetadataAndFurnitureMarkers))
        {
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
        }
    }

    private static string PathOf(string fileName, string folder = "OpenDocument") =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", folder, fileName);
}
