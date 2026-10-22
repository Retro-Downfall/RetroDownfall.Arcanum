using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The PowerPoint extractor as the production registry reaches it: through <c>ExtractDocumentAsync</c> with each
/// registered PowerPoint MIME type, including how a rejection becomes a status and a fixed, text-free reason.
/// </summary>
public sealed class PresentationRegistryTests
{
    public static TheoryData<string> PowerPointMimeTypes() => new()
    {
        SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType,
        SessionAttachmentComprehensionPolicy.PowerPointMacroEnabledPresentationMimeType,
    };

    private static string Paragraph(string text) => PresentationDeck.Paragraph(text);

    private static string Shape(int id, string paragraphs) => PresentationDeck.Shape(id, paragraphs);

    private static string Slide(string shapes, string rootAttributes = "") => PresentationDeck.Slide(shapes, rootAttributes);

    private static Task<SessionAttachmentExtractionResult> ExtractThroughRegistryAsync(byte[] bytes, string mimeType) =>
        SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            Record(mimeType, bytes.Length),
            Settings(),
            1024 * 1024,
            CancellationToken.None);

    [Theory]
    [MemberData(nameof(PowerPointMimeTypes))]
    public async Task ExtractDocument_PowerPointDeck_ExtractsItsVisibleText(string mimeType)
    {
        byte[] deck = PresentationDeck.Deck(
            Slide(Shape(2, Paragraph("Quarterly review") + Paragraph("Revenue grew"))),
            Slide(Shape(2, Paragraph("HIDDENSLIDETEXT")), " show=\"0\""),
            Slide(Shape(2, Paragraph("Next steps"))));

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(deck, mimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("Slide 1\nQuarterly review\nRevenue grew\nSlide 3\nNext steps\n", result.Text);

        Assert.False(result.WasTruncated);

        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task ExtractDocument_MimeTypeWithCaseAndParameters_StillReachesThePowerPointExtractor()
    {
        byte[] deck = PresentationDeck.Deck(Slide(Shape(2, Paragraph("Dispatched"))));

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(
            deck,
            " " + SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType.ToUpperInvariant() + "; charset=binary");

        Assert.Equal("Slide 1\nDispatched\n", result.Text);
    }

    [Fact]
    public async Task ExtractDocument_MacroEnabledDeck_IsReadAsTextAndNeverOpensItsMacros()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("Macro deck"))));

        string rels = entries.Single(entry => entry.Name == "ppt/_rels/presentation.xml.rels").Content.Replace(
            "</Relationships>",
            PresentationDeck.Rel("rId50", "http://schemas.microsoft.com/office/2006/relationships/vbaProject", "vbaProject.bin") + "</Relationships>",
            StringComparison.Ordinal);

        PresentationDeck.Replace(entries, "ppt/_rels/presentation.xml.rels", rels);

        RawZipPackage package = new();

        foreach ((string name, string content) in entries)
        {
            package.Entries.Add(RawZipEntry.Deflated(name, content));
        }

        package.Entries.Add(new RawZipEntry(ComprehensionPackageBuilder.Utf8Bytes("ppt/vbaProject.bin"), new byte[64], 8)
        {
            DataAreaBytes = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
        });

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(
            package.ToBytes(),
            SessionAttachmentComprehensionPolicy.PowerPointMacroEnabledPresentationMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("Slide 1\nMacro deck\n", result.Text);
    }

    [Theory]
    [MemberData(nameof(PowerPointMimeTypes))]
    public async Task ExtractDocument_WordprocessingPackage_FailsWithTheFormatMismatchReason(string mimeType)
    {
        byte[] docx = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "textutil-basic.docx"));

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(docx, mimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("Document package does not match its declared format.", result.FailureReason);

        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task ExtractDocument_PasswordProtectedPackage_IsNotEligibleWithTheEncryptedReason()
    {
        byte[] compound = new byte[2048];

        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(compound, 0);

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(
            compound,
            SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Equal("Encrypted document packages are not supported.", result.FailureReason);
    }

    [Fact]
    public async Task ExtractDocument_MissingSlidePart_FailsWithAReasonThatNamesNothing()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("DOCUMENTSECRET"))));

        entries.RemoveAll(entry => entry.Name == "ppt/slides/slide1.xml");

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(
            PresentationDeck.Build(entries),
            SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("Document package is missing a required part.", result.FailureReason);
    }

    [Fact]
    public async Task ExtractDocument_MalformedSlide_FailsWithTheMalformedXmlReason()
    {
        byte[] deck = PresentationDeck.Deck($"<p:sld xmlns:p=\"{PresentationDeck.P}\"><oops");

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(
            deck,
            SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("Document part is not well-formed XML or declares a DTD.", result.FailureReason);
    }

    [Fact]
    public async Task ExtractDocument_MoreSlidesThanTheProductionLimit_FailsWithTheStructureBoundsReason()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("x"))));

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, [.. Enumerable.Repeat("rId1", 4097)]));

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(
            PresentationDeck.Build(entries),
            SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("Document structure exceeds its bounds.", result.FailureReason);
    }

    [Fact]
    public async Task ExtractDocument_DeckWhoseSlidesAreAllHidden_IsNotEligible()
    {
        byte[] deck = PresentationDeck.Deck(Slide(Shape(2, Paragraph("HIDDEN")), " show=\"0\""));

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(
            deck,
            SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task ExtractDocument_RejectedDeck_DoesNotBlockTheNextHealthyDeck()
    {
        SessionAttachmentExtractionResult rejected = await ExtractThroughRegistryAsync(
            PresentationDeck.Deck("<broken"),
            SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, rejected.Status);

        SessionAttachmentExtractionResult healthy = await ExtractThroughRegistryAsync(
            PresentationDeck.Deck(Slide(Shape(2, Paragraph("Healthy")))),
            SessionAttachmentComprehensionPolicy.PowerPointPresentationMimeType);

        Assert.Equal("Slide 1\nHealthy\n", healthy.Text);
    }

    private static ArcanumSettings Settings() => new()
    {
        Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
    };

    private static SessionAttachmentRecord Record(string mimeType, long length) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
        "deck", "deck.pptx", 1, "untrusted/path", "hash", mimeType, length, SessionAttachmentKind.Binary,
        DateTimeOffset.UtcNow);
}
