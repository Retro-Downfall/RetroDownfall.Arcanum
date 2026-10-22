using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>The Word extractor reached through the real registry host, as every model-context path reaches it.</summary>
public sealed class WordprocessingRegistryDispatchTests
{
    public static TheoryData<string> WordMimeTypes() =>
    [
        SessionAttachmentComprehensionPolicy.WordDocumentMimeType,
        SessionAttachmentComprehensionPolicy.WordMacroEnabledDocumentMimeType,
    ];

    [Theory]
    [MemberData(nameof(WordMimeTypes))]
    public async Task ExtractDocument_WordMimeType_DispatchesToTheWordExtractorAndProjectsTheText(string mimeType)
    {
        byte[] package = WordprocessingFixtures.Docx(WordprocessingFixtures.P("registry text") + "<w:p><w:r><w:t>a</w:t><w:tab/><w:t>b</w:t></w:r></w:p>");

        SessionAttachmentExtractionResult result = await ExtractAsync(package, mimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("registry text\na\tb\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Theory]
    [MemberData(nameof(WordMimeTypes))]
    public async Task ExtractDocument_RealTextutilPackage_ProjectsThroughTheRegistry(string mimeType)
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(WordprocessingRealProducerTests.Read("textutil-multi.docx"), mimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.StartsWith("First paragraph with bold and italic text.\n", result.Text);

        Assert.EndsWith("Last paragraph.\n", result.Text);
    }

    [Fact]
    public async Task ExtractDocument_MimeParametersAndCase_StillReachTheWordExtractor()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(
            WordprocessingFixtures.Docx(WordprocessingFixtures.P("shouted mime")),
            "  APPLICATION/VND.OPENXMLFORMATS-OFFICEDOCUMENT.WORDPROCESSINGML.DOCUMENT ; q=1");

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("shouted mime\n", result.Text);
    }

    [Fact]
    public async Task ExtractDocument_MacroEnabledPackageWithGarbageMacroPayload_ExtractsTheText()
    {
        byte[] garbage = [0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8];

        RawZipEntry macros = RawZipEntry.Deflated("word/vbaProject.bin", garbage);

        macros.DataAreaBytes = garbage;

        macros.UncompressedSize = 30u * 1024 * 1024;

        macros.CompressedSize = (uint)garbage.Length;

        byte[] package = RawZipPackage.Of(
            RawZipEntry.Deflated("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/document.xml"))),
            RawZipEntry.Deflated("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("macro-enabled text"))),
            RawZipEntry.Deflated("word/_rels/document.xml.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rIdVba", WordprocessingFixtures.VbaProjectType, "vbaProject.bin"))),
            macros).ToBytes();

        SessionAttachmentExtractionResult result = await ExtractAsync(package, SessionAttachmentComprehensionPolicy.WordMacroEnabledDocumentMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("macro-enabled text\n", result.Text);
    }

    // ---------------------------------------------------------------- rejections map to the D4 table

    [Fact]
    public async Task ExtractDocument_SpreadsheetPackageDeclaredAsWord_FailsWithTheFormatMismatchReason()
    {
        byte[] workbook = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "xl/workbook.xml"))),
            ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets/></workbook>"));

        SessionAttachmentExtractionResult result = await ExtractAsync(workbook, SessionAttachmentComprehensionPolicy.WordDocumentMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("Document package does not match its declared format.", result.FailureReason);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task ExtractDocument_PasswordProtectedPackage_IsNotEligibleWithTheEncryptedReason()
    {
        byte[] cfb = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[600]];

        SessionAttachmentExtractionResult result = await ExtractAsync(cfb, SessionAttachmentComprehensionPolicy.WordDocumentMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Equal("Encrypted document packages are not supported.", result.FailureReason);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task ExtractDocument_DocumentWithADoctype_FailsWithTheMalformedXmlReason()
    {
        string main = $"<!DOCTYPE w:document [<!ENTITY x \"SECRET\">]><w:document {WordprocessingFixtures.Namespaces()}><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>";

        SessionAttachmentExtractionResult result = await ExtractAsync(WordprocessingFixtures.Package(main), SessionAttachmentComprehensionPolicy.WordDocumentMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("Document part is not well-formed XML or declares a DTD.", result.FailureReason);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task ExtractDocument_TraversalRelationship_FailsWithAReasonThatDoesNotEchoTheName()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("body")),
            documentRelationships: [WordprocessingFixtures.Relationship("rIdC", WordprocessingFixtures.CommentsType, "../../traversal-secret.xml")]);

        SessionAttachmentExtractionResult result = await ExtractAsync(package, SessionAttachmentComprehensionPolicy.WordDocumentMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("Document package contains an unsafe or duplicate entry name.", result.FailureReason);

        Assert.DoesNotContain("traversal-secret", result.FailureReason);
    }

    [Fact]
    public async Task ExtractDocument_ZipBombMainPart_FailsWithTheExpansionBoundsReason()
    {
        byte[] bomb = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/document.xml"))),
            ("word/document.xml", new string('A', 40 * 1024 * 1024)));

        SessionAttachmentExtractionResult result = await ExtractAsync(bomb, SessionAttachmentComprehensionPolicy.WordDocumentMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal("Document package exceeds its expansion bounds.", result.FailureReason);
    }

    [Fact]
    public async Task ExtractDocument_DocumentWhoseTextIsAllHidden_ProjectsOnlyItsParagraphMarks()
    {
        byte[] package = WordprocessingFixtures.Docx(
            "<w:p>" + WordprocessingFixtures.R("HIDDEN", "<w:vanish/>") + "</w:p>"
            + "<w:p><w:del w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:r><w:delText>deleted</w:delText></w:r></w:del></w:p>");

        SessionAttachmentExtractionResult result = await ExtractAsync(package, SessionAttachmentComprehensionPolicy.WordDocumentMimeType);

        Assert.Equal("\n\n", result.Text);

        Assert.DoesNotContain("HIDDEN", result.Text);

        Assert.DoesNotContain("deleted", result.Text);
    }

    [Fact]
    public async Task ExtractDocument_BodyWithNoParagraphs_IsNotEligible()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(
            WordprocessingFixtures.Docx("<w:sectPr/>"),
            SessionAttachmentComprehensionPolicy.WordDocumentMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task ExtractDocument_RejectionDoesNotQuarantineTheWordHost()
    {
        SessionAttachmentExtractionResult rejected = await ExtractAsync(
            System.Text.Encoding.UTF8.GetBytes("not a package at all, just text that is long enough"),
            SessionAttachmentComprehensionPolicy.WordDocumentMimeType);

        SessionAttachmentExtractionResult healthy = await ExtractAsync(
            WordprocessingFixtures.Docx(WordprocessingFixtures.P("after the rejection")),
            SessionAttachmentComprehensionPolicy.WordDocumentMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, rejected.Status);

        Assert.Equal("Document package is not a valid ZIP container.", rejected.FailureReason);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, healthy.Status);

        Assert.Equal("after the rejection\n", healthy.Text);
    }

    [Fact]
    public async Task ExtractDocument_OutputBeyondTheSinkCeiling_IsTruncatedByTheRealHost()
    {
        string body = string.Concat(Enumerable.Range(0, 400).Select(index => WordprocessingFixtures.P(new string('x', 1000) + index)));

        byte[] package = WordprocessingFixtures.Docx(body);

        SessionAttachmentExtractionResult result = await ExtractAsync(package, SessionAttachmentComprehensionPolicy.WordDocumentMimeType, maximumBytes: package.Length);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.True(result.WasTruncated);

        Assert.Equal(262_144, result.Text.Length);
    }

    private static Task<SessionAttachmentExtractionResult> ExtractAsync(byte[] package, string mimeType, long? maximumBytes = null)
    {
        SessionAttachmentRecord record = new(
            Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
            "document", "document.docx", 1, "untrusted/path", "hash",
            mimeType, package.Length, SessionAttachmentKind.Binary,
            DateTimeOffset.UtcNow);

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
        };

        return SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(package)),
            record,
            settings,
            maximumBytes ?? 1024 * 1024,
            CancellationToken.None);
    }
}
