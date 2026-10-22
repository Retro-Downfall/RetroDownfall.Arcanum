using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.OpenDocumentFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Test-side builders and runners shared by the three OpenDocument extractor test classes. Packages are valid ZIPs
/// built on <see cref="ComprehensionPackageBuilder"/>; the XML is written by hand so each test owns the exact markup
/// it exercises.
/// </summary>
internal static class OpenDocumentFixtures
{
    public const string TextMime = "application/vnd.oasis.opendocument.text";

    public const string SpreadsheetMime = "application/vnd.oasis.opendocument.spreadsheet";

    public const string PresentationMime = "application/vnd.oasis.opendocument.presentation";

    public const string TextutilDocumentType = "<!DOCTYPE manifest:manifest PUBLIC \"-//OpenOffice.org//DTD Manifest 1.0//EN\" \"Manifest.dtd\">";

    public const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" "
        + "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" "
        + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" "
        + "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" "
        + "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" "
        + "xmlns:presentation=\"urn:oasis:names:tc:opendocument:xmlns:presentation:1.0\" "
        + "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" "
        + "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" "
        + "xmlns:form=\"urn:oasis:names:tc:opendocument:xmlns:form:1.0\" "
        + "xmlns:xlink=\"http://www.w3.org/1999/xlink\" "
        + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" "
        + "xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\"";

    /// <summary>A <c>content.xml</c> whose body is <c>office:&lt;kind&gt;</c> holding <paramref name="body"/>.</summary>
    public static string ContentPart(string kind, string body, string automaticStyles = "", string afterBody = "") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
        + $"<office:document-content {Namespaces} office:version=\"1.3\">"
        + "<office:font-face-decls><style:font-face style:name=\"Liberation Serif\" svg:font-family=\"'Liberation Serif'\"/></office:font-face-decls>"
        + $"<office:automatic-styles>{automaticStyles}</office:automatic-styles>"
        + $"<office:body><office:{kind}>{body}</office:{kind}></office:body>"
        + afterBody
        + "</office:document-content>";

    /// <summary>A <c>styles.xml</c> carrying <paramref name="commonStyles"/> in <c>office:styles</c>.</summary>
    public static string StylesPart(string commonStyles, string automaticStyles = "") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
        + $"<office:document-styles {Namespaces} office:version=\"1.3\">"
        + $"<office:styles>{commonStyles}</office:styles>"
        + $"<office:automatic-styles>{automaticStyles}</office:automatic-styles>"
        + "<office:master-styles/>"
        + "</office:document-styles>";

    public static string ManifestPart(string mimeType, bool declareStyles, string documentType = "", string extraEntries = "") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
        + documentType
        + "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">"
        + $"<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.3\" manifest:media-type=\"{mimeType}\"/>"
        + "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>"
        + (declareStyles ? "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>" : string.Empty)
        + "<manifest:file-entry manifest:full-path=\"meta.xml\" manifest:media-type=\"text/xml\"/>"
        + extraEntries
        + "</manifest:manifest>";

    /// <summary>
    /// A valid package. <paramref name="stylesXml"/> is written and declared when supplied. <paramref name="undeclared"/>
    /// entries are written to the ZIP but never mentioned by the manifest.
    /// </summary>
    public static byte[] Package(
        string mimeType,
        string contentXml,
        string? stylesXml = null,
        string documentType = "",
        params (string Name, string Content)[] undeclared)
    {
        List<(string Name, string Content)> entries =
        [
            ("mimetype", mimeType),
            ("META-INF/manifest.xml", ManifestPart(mimeType, stylesXml is not null, documentType)),
            ("content.xml", contentXml),
            ("meta.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-meta xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"/>"),
        ];

        if (stylesXml is not null)
        {
            entries.Add(("styles.xml", stylesXml));
        }

        entries.AddRange(undeclared);

        return ComprehensionPackageBuilder.Build([.. entries]);
    }

    /// <summary>A package whose entries are stored uncompressed, for tests whose content is large and repetitive.</summary>
    public static byte[] StoredPackage(string mimeType, string contentXml) =>
        ComprehensionPackageBuilder.BuildStored(
            ("mimetype", mimeType),
            ("META-INF/manifest.xml", ManifestPart(mimeType, declareStyles: false)),
            ("content.xml", contentXml));

    public static byte[] Odt(string body, string automaticStyles = "", string? commonStyles = null, string documentType = "") =>
        Package(TextMime, ContentPart("text", body, automaticStyles), commonStyles is null ? null : StylesPart(commonStyles), documentType);

    public static byte[] Ods(string body, string automaticStyles = "", string? commonStyles = null, string documentType = "") =>
        Package(SpreadsheetMime, ContentPart("spreadsheet", body, automaticStyles), commonStyles is null ? null : StylesPart(commonStyles), documentType);

    public static byte[] Odp(string body, string automaticStyles = "", string? commonStyles = null, string documentType = "") =>
        Package(PresentationMime, ContentPart("presentation", body, automaticStyles), commonStyles is null ? null : StylesPart(commonStyles), documentType);

    /// <summary>A style:style element for a family, with optional parent and one properties child.</summary>
    public static string Style(string family, string name, string? parent = null, string properties = "") =>
        $"<style:style style:name=\"{name}\" style:family=\"{family}\""
        + (parent is null ? string.Empty : $" style:parent-style-name=\"{parent}\"")
        + (properties.Length == 0 ? "/>" : $">{properties}</style:style>");

    public const string HiddenText = "<style:text-properties text:display=\"none\"/>";

    public const string VisibleText = "<style:text-properties text:display=\"true\"/>";

    public static async Task<SessionAttachmentExtractionResult> ExtractAsync<TExtractor>(byte[] package, int capacity = 262_144)
        where TExtractor : ISessionAttachmentDocumentExtractor, new()
    {
        BoundedDocumentTextSink sink = new(capacity, CancellationToken.None);

        await new TExtractor().ExtractAsync(package, sink, CancellationToken.None);

        return sink.Complete();
    }

    public static async Task<string> TextOfAsync<TExtractor>(byte[] package)
        where TExtractor : ISessionAttachmentDocumentExtractor, new()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync<TExtractor>(package);

        Assert.False(result.WasTruncated);

        return result.Text;
    }

    public static async Task<DocumentPackageRejectedException> RejectedAsync<TExtractor>(byte[] package, DocumentPackageRejection expected)
        where TExtractor : ISessionAttachmentDocumentExtractor, new()
    {
        DocumentPackageRejectedException exception = await Assert.ThrowsAsync<DocumentPackageRejectedException>(
            async () => await ExtractAsync<TExtractor>(package));

        Assert.Equal(expected, exception.Rejection);

        return exception;
    }
}

/// <summary>A sink that accepts a fixed number of appends, then refuses, and counts every call.</summary>
internal sealed class SpyTextSink(int acceptedCalls) : ISessionAttachmentDocumentTextSink
{
    public int Calls { get; private set; }

    public bool TryAppend(ReadOnlySpan<char> text)
    {
        Calls++;

        return Calls <= acceptedCalls;
    }

    public void MarkTruncated()
    {
    }
}

/// <summary>A sink that accepts everything but cancels a token after a fixed number of appends.</summary>
internal sealed class CancellingSink(CancellationTokenSource cancellation, int cancelAfterCalls) : ISessionAttachmentDocumentTextSink
{
    public int Calls { get; private set; }

    public bool TryAppend(ReadOnlySpan<char> text)
    {
        if (++Calls == cancelAfterCalls)
        {
            cancellation.Cancel();
        }

        return true;
    }

    public void MarkTruncated()
    {
    }
}

/// <summary>A sink that records everything it is given and does no trimming of its own.</summary>
internal sealed class RecordingTextSink : ISessionAttachmentDocumentTextSink
{
    private readonly System.Text.StringBuilder _text = new();

    public string Text => _text.ToString();

    public bool Truncated { get; private set; }

    public bool TryAppend(ReadOnlySpan<char> text)
    {
        _text.Append(text);

        return true;
    }

    public void MarkTruncated() => Truncated = true;
}

public sealed class OpenDocumentPackageTests
{
    private const string Marker = "INJECTEDMARKER";

    // ---------------------------------------------------------------- registry dispatch (SessionAttachmentTextExtractor)

    public static TheoryData<string, string, string> RegistryDocuments() => new()
    {
        { SessionAttachmentComprehensionPolicy.OpenDocumentTextMimeType, "OpenDocument text", "Alpha\nBeta\n" },
        { SessionAttachmentComprehensionPolicy.OpenDocumentSpreadsheetMimeType, "OpenDocument spreadsheet", "Sheet: Data\nAlpha\tBeta\n" },
        { SessionAttachmentComprehensionPolicy.OpenDocumentPresentationMimeType, "OpenDocument presentation", "Slide 1\nAlpha\nBeta\n" },
    };

    private static byte[] DocumentFor(string mimeType) => mimeType switch
    {
        TextMime => Odt("<text:p>Alpha</text:p><text:p>Beta</text:p>"),
        SpreadsheetMime => Ods("<table:table table:name=\"Data\"><table:table-row><table:table-cell><text:p>Alpha</text:p></table:table-cell><table:table-cell><text:p>Beta</text:p></table:table-cell></table:table-row></table:table>"),
        _ => Odp("<draw:page><draw:frame><draw:text-box><text:p>Alpha</text:p><text:p>Beta</text:p></draw:text-box></draw:frame></draw:page>"),
    };

    [Theory]
    [MemberData(nameof(RegistryDocuments))]
    public async Task ExtractDocument_RegistryDispatchesEachOpenDocumentMimeToItsExtractor(string mimeType, string label, string expected)
    {
        Assert.Equal(label, SessionAttachmentComprehensionPolicy.Formats.Single(format => format.MimeType == mimeType).FormatName);

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(mimeType, DocumentFor(mimeType));

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal(expected, result.Text);

        Assert.False(result.WasTruncated);
    }

    [Theory]
    [InlineData(TextMime, "Encrypted")]
    [InlineData(SpreadsheetMime, "Encrypted")]
    [InlineData(PresentationMime, "Encrypted")]
    [InlineData(TextMime, "Mismatch")]
    [InlineData(SpreadsheetMime, "Mismatch")]
    [InlineData(PresentationMime, "Mismatch")]
    [InlineData(TextMime, "Malformed")]
    [InlineData(SpreadsheetMime, "Malformed")]
    [InlineData(PresentationMime, "Malformed")]
    public async Task ExtractDocument_RejectionsMapToTheFixedStatusAndReason(string mimeType, string defect)
    {
        string otherMime = mimeType == TextMime ? SpreadsheetMime : TextMime;

        byte[] package = defect switch
        {
            "Encrypted" => ComprehensionPackageBuilder.Build(
                ("META-INF/manifest.xml", ManifestPart(mimeType, declareStyles: false, extraEntries: "<manifest:file-entry manifest:full-path=\"x\"><manifest:encryption-data/></manifest:file-entry>")),
                ("content.xml", $"{Marker} ciphertext")),
            "Mismatch" => DocumentFor(otherMime),
            _ => Package(mimeType, ContentPart(mimeType == TextMime ? "text" : mimeType == SpreadsheetMime ? "spreadsheet" : "presentation", $"<text:p>{Marker}</text:q>")),
        };

        SessionAttachmentExtractionResult result = await ExtractThroughRegistryAsync(mimeType, package);

        (SessionAttachmentExtractionStatus status, string reason) = defect switch
        {
            "Encrypted" => (SessionAttachmentExtractionStatus.NotEligible, "Encrypted document packages are not supported."),
            "Mismatch" => (SessionAttachmentExtractionStatus.Failed, "Document package does not match its declared format."),
            _ => (SessionAttachmentExtractionStatus.Failed, "Document part is not well-formed XML or declares a DTD."),
        };

        Assert.Equal(status, result.Status);

        Assert.Equal(reason, result.FailureReason);

        Assert.Empty(result.Text);

        Assert.DoesNotContain(Marker, result.FailureReason, StringComparison.Ordinal);
    }

    private static Task<SessionAttachmentExtractionResult> ExtractThroughRegistryAsync(string mimeType, byte[] package)
    {
        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
        };

        SessionAttachmentRecord record = new(
            Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
            "document", "document", 1, "untrusted/path", "hash", mimeType, package.Length, SessionAttachmentKind.Binary,
            DateTimeOffset.UtcNow);

        return SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(package)),
            record,
            settings,
            1024 * 1024,
            CancellationToken.None);
    }

    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    // ---------------------------------------------------------------- container refusals

    [Fact]
    public async Task Extract_EncryptedPackage_IsEncryptedAndNoCipherMaterialLeaves()
    {
        string manifest = ManifestPart(
            TextMime,
            declareStyles: false,
            extraEntries: "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\">"
                + $"<manifest:encryption-data manifest:checksum-type=\"SHA1/1K\" manifest:checksum=\"{Marker}\"/></manifest:file-entry>");

        byte[] package = ComprehensionPackageBuilder.Build(
            ("mimetype", TextMime),
            ("META-INF/manifest.xml", manifest),
            ("content.xml", $"{Marker} ciphertext that is not XML"));

        DocumentPackageRejectedException exception = await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.Encrypted);

        Assert.DoesNotContain(Marker, exception.ToString());
    }

    [Fact]
    public async Task Extract_PasswordProtectedCompoundFile_IsEncrypted()
    {
        byte[] package = [.. CompoundFileSignature, .. new byte[600]];

        await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.Encrypted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(4096)]
    public async Task Extract_BytesThatAreNotAZip_AreNotZip(int length)
    {
        byte[] package = new byte[length];

        Array.Fill(package, (byte)'x');

        await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.NotZip);
    }

    [Fact]
    public async Task Extract_PackageWithoutAManifest_IsMissingAPart()
    {
        byte[] package = ComprehensionPackageBuilder.Build(("mimetype", TextMime), ("content.xml", ContentPart("text", "<text:p>x</text:p>")));

        await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.MissingPart);
    }

    [Fact]
    public async Task Extract_ManifestThatDoesNotDeclareContent_IsMissingAPart()
    {
        string manifest = "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\">"
            + $"<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"{TextMime}\"/></manifest:manifest>";

        byte[] package = ComprehensionPackageBuilder.Build(
            ("META-INF/manifest.xml", manifest),
            ("content.xml", ContentPart("text", "<text:p>x</text:p>")));

        await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.MissingPart);
    }

    [Fact]
    public async Task Extract_DeclaredStylesPartThatIsAbsent_IsMissingAPart()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("META-INF/manifest.xml", ManifestPart(TextMime, declareStyles: true)),
            ("content.xml", ContentPart("text", "<text:p>x</text:p>")));

        await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.MissingPart);
    }

    [Fact]
    public async Task Extract_StoredAndDeflatedEntries_ReadTheSame()
    {
        string content = ContentPart("text", "<text:p>Same</text:p>");

        byte[] stored = ComprehensionPackageBuilder.BuildStored(
            ("META-INF/manifest.xml", ManifestPart(TextMime, declareStyles: false)),
            ("content.xml", content));

        Assert.Equal("Same\n", await TextOfAsync<OpenDocumentTextExtractor>(stored));
    }

    // ---------------------------------------------------------------- DTDs and entities (plan D6)

    [Fact]
    public async Task Extract_ManifestWithTheTextutilDocumentType_IsAcceptedAndTheTextIsExtracted()
    {
        // Sensitivity: the manifest alone is read with DtdProcessing.Ignore.
        byte[] package = Odt("<text:p>From textutil</text:p>", documentType: TextutilDocumentType);

        Assert.Equal("From textutil\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_EntityReferenceInTheManifest_FailsAndTheMarkerNeverAppears()
    {
        // Sensitivity: flipping the manifest's Ignore to Parse expands &x; into the marker and the part is accepted.
        string manifest = ManifestPart(
            TextMime,
            declareStyles: false,
            documentType: $"<!DOCTYPE manifest:manifest [<!ENTITY x \"{Marker}\">]>",
            extraEntries: "<manifest:file-entry manifest:full-path=\"Extra.xml\" manifest:media-type=\"&x;\"/>");

        byte[] package = ComprehensionPackageBuilder.Build(
            ("META-INF/manifest.xml", manifest),
            ("content.xml", ContentPart("text", "<text:p>Body</text:p>")));

        DocumentPackageRejectedException exception = await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.MalformedXml);

        Assert.DoesNotContain(Marker, exception.ToString());
    }

    [Theory]
    [InlineData("content", true)]
    [InlineData("styles", true)]
    [InlineData("content", false)]
    [InlineData("styles", false)]
    public async Task Extract_AnyDocumentTypeInAContentPart_IsMalformedXml(string part, bool withEntity)
    {
        // Sensitivity: content.xml and styles.xml keep DtdProcessing.Prohibit. A bare DOCTYPE (no entity) is accepted by
        // Ignore, so the second pair of rows is what tells Prohibit from Ignore; the first pair tells either from Parse.
        string doctype = withEntity
            ? $"<!DOCTYPE office:document-{part} [<!ENTITY x \"{Marker}\">]>"
            : $"<!DOCTYPE office:document-{part}>";

        string content = ContentPart("text", withEntity ? "<text:p>&x;</text:p>" : "<text:p>Body</text:p>");

        string styles = StylesPart(string.Empty);

        if (part == "content")
        {
            content = content.Replace("?><office:document-content", "?>" + doctype + "<office:document-content", StringComparison.Ordinal);
        }
        else
        {
            styles = styles.Replace("?><office:document-styles", "?>" + doctype + "<office:document-styles", StringComparison.Ordinal);
        }

        byte[] package = Package(TextMime, content, styles);

        DocumentPackageRejectedException exception = await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.MalformedXml);

        Assert.DoesNotContain(Marker, exception.ToString());
    }

    [Fact]
    public async Task Extract_BillionLaughsContent_FailsDeterministicallyWithoutExpansion()
    {
        const string doctype = "<!DOCTYPE office:document-content [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>";

        string content = ContentPart("text", "<text:p>&c;</text:p>")
            .Replace("?><office:document-content", "?>" + doctype + "<office:document-content", StringComparison.Ordinal);

        await RejectedAsync<OpenDocumentTextExtractor>(Package(TextMime, content), DocumentPackageRejection.MalformedXml);
    }

    [Theory]
    [InlineData("<text:p>unclosed")]
    [InlineData("<text:p>&undefined;</text:p>")]
    [InlineData("<text:p></text:q>")]
    public async Task Extract_MalformedContent_IsMalformedXml(string body)
    {
        await RejectedAsync<OpenDocumentTextExtractor>(Odt(body), DocumentPackageRejection.MalformedXml);
    }

    [Fact]
    public async Task Extract_MalformedDeclaredStylesPart_IsMalformedXml()
    {
        byte[] package = Package(TextMime, ContentPart("text", "<text:p>x</text:p>"), "<office:document-styles><unclosed>");

        await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.MalformedXml);
    }

    [Fact]
    public async Task Extract_NestingBeyondTheDepthBound_IsStructureBounds()
    {
        string deep = string.Concat(Enumerable.Repeat("<text:span>", 300)) + "x" + string.Concat(Enumerable.Repeat("</text:span>", 300));

        await RejectedAsync<OpenDocumentTextExtractor>(Odt($"<text:p>{deep}</text:p>"), DocumentPackageRejection.StructureBounds);
    }

    // ---------------------------------------------------------------- root and body shape

    [Fact]
    public async Task Extract_ContentRootThatIsNotDocumentContent_IsFormatMismatch()
    {
        string content = ContentPart("text", "<text:p>x</text:p>")
            .Replace("office:document-content", "office:document", StringComparison.Ordinal);

        await RejectedAsync<OpenDocumentTextExtractor>(Package(TextMime, content), DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_ContentRootInAnotherNamespace_IsFormatMismatch()
    {
        string content = ContentPart("text", "<text:p>x</text:p>")
            .Replace("<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"", "<office:document-content xmlns:office=\"urn:elsewhere\"", StringComparison.Ordinal);

        await RejectedAsync<OpenDocumentTextExtractor>(Package(TextMime, content), DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_StylesRootThatIsNotDocumentStyles_IsFormatMismatch()
    {
        string styles = StylesPart(string.Empty).Replace("office:document-styles", "office:document-meta", StringComparison.Ordinal);

        await RejectedAsync<OpenDocumentTextExtractor>(Package(TextMime, ContentPart("text", "<text:p>x</text:p>"), styles), DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_BodyElementOutsideOfficeBody_IsFormatMismatch()
    {
        string content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + $"<office:document-content {Namespaces}><office:text><text:p>x</text:p></office:text></office:document-content>";

        await RejectedAsync<OpenDocumentTextExtractor>(Package(TextMime, content), DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_MoreThanOneBody_IsFormatMismatch()
    {
        string content = ContentPart("text", "<text:p>first</text:p>")
            .Replace("</office:body>", "</office:body><office:body><office:text><text:p>SECONDMARKER</text:p></office:text></office:body>", StringComparison.Ordinal);

        await RejectedAsync<OpenDocumentTextExtractor>(Package(TextMime, content), DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_BodyHoldingAnotherDocumentKind_IsFormatMismatch()
    {
        // The manifest says text; the body says spreadsheet.
        string content = ContentPart("spreadsheet", "<table:table table:name=\"S\"/>");

        await RejectedAsync<OpenDocumentTextExtractor>(Package(TextMime, content), DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_BodyWithMoreThanOneDocumentElement_IsFormatMismatch()
    {
        string content = ContentPart("text", "<text:p>first</text:p>")
            .Replace("</office:text>", "</office:text><office:text><text:p>SECONDMARKER</text:p></office:text>", StringComparison.Ordinal);

        await RejectedAsync<OpenDocumentTextExtractor>(Package(TextMime, content), DocumentPackageRejection.FormatMismatch);
    }

    [Fact]
    public async Task Extract_NamespacePrefixesAreIrrelevant()
    {
        string content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<o:document-content xmlns:o=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:t=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">"
            + "<o:body><o:text><t:p>Prefixed</t:p></o:text></o:body></o:document-content>";

        Assert.Equal("Prefixed\n", await TextOfAsync<OpenDocumentTextExtractor>(Package(TextMime, content)));
    }

    // ---------------------------------------------------------------- parts that are never read

    [Fact]
    public async Task Extract_UnreferencedAndUnreadParts_AreNeverParsed()
    {
        // Sensitivity: nothing is scanned. The smuggled parts are malformed or carry text that would show if read.
        byte[] package = Package(
            TextMime,
            ContentPart("text", "<text:p>Only this</text:p>"),
            null,
            string.Empty,
            ("Smuggled/evil.xml", "<text:p>SMUGGLEDMARKER</text:p>"),
            ("settings.xml", "<<<not xml at all"),
            ("Thumbnails/thumbnail.png", "\u0089PNG garbage"),
            ("Basic/Standard/Module1.xml", "<script:module xmlns:script=\"urn:x\">SMUGGLEDMARKER</script:module>"),
            ("Object 1/content.xml", "<office:document-content><unclosed>"));

        Assert.Equal("Only this\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_DeclaredButUnusedParts_AreNeverParsedEither()
    {
        // meta.xml is declared by the manifest and present, but malformed: it is metadata and is never opened.
        byte[] package = ComprehensionPackageBuilder.Build(
            ("mimetype", TextMime),
            ("META-INF/manifest.xml", ManifestPart(TextMime, declareStyles: false)),
            ("content.xml", ContentPart("text", "<text:p>Body</text:p>")),
            ("meta.xml", "<<<broken"));

        Assert.Equal("Body\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_EmbeddedPartWithABombSizedDeclarationAndGarbageData_IsNeverInflated()
    {
        RawZipEntry embedding = RawZipEntry.Deflated("Object 1/content.xml", "placeholder");

        embedding.DataAreaBytes = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

        embedding.UncompressedSize = 30_000_000;

        RawZipPackage raw = RawZipPackage.Of(
            RawZipEntry.Stored("mimetype", TextMime),
            RawZipEntry.Deflated("META-INF/manifest.xml", ManifestPart(TextMime, declareStyles: false)),
            RawZipEntry.Deflated("content.xml", ContentPart("text", "<text:p>Fine</text:p>")),
            embedding);

        Assert.Equal("Fine\n", await TextOfAsync<OpenDocumentTextExtractor>(raw.ToBytes()));
    }

    [Fact]
    public async Task Extract_ManifestDeclaredMacroAndObjectParts_WithGarbageDeflateData_AreNeverInflated()
    {
        // The ODF counterpart of a macro-enabled OOXML variant: macros (Basic, Scripts) and embedded objects are declared by
        // the manifest, carry bomb-sized declarations and garbage deflate data, and must never be opened.
        string[] declared = ["Basic/Standard/Module1.xml", "Basic/script-lc.xml", "Scripts/python/evil.py", "Object 1/content.xml", "Pictures/photo.png"];

        string extra = string.Concat(declared.Select(name => $"<manifest:file-entry manifest:full-path=\"{name}\" manifest:media-type=\"application/octet-stream\"/>"));

        List<RawZipEntry> entries =
        [
            RawZipEntry.Stored("mimetype", TextMime),
            RawZipEntry.Deflated("META-INF/manifest.xml", ManifestPart(TextMime, declareStyles: false, extraEntries: extra)),
            RawZipEntry.Deflated("content.xml", ContentPart("text", "<text:p>Fine</text:p>")),
        ];

        foreach (string name in declared)
        {
            RawZipEntry entry = RawZipEntry.Deflated(name, "placeholder");

            entry.DataAreaBytes = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

            entry.UncompressedSize = 30_000_000;

            entries.Add(entry);
        }

        Assert.Equal("Fine\n", await TextOfAsync<OpenDocumentTextExtractor>(RawZipPackage.Of([.. entries]).ToBytes()));
    }

    [Fact]
    public async Task Extract_ExternalReferences_AreNeverFollowedOrEmitted()
    {
        byte[] package = Odt(
            "<text:p>Lead <text:a xlink:type=\"simple\" xlink:href=\"file:///etc/passwd\">linked</text:a>"
            + "<draw:frame><draw:object xlink:href=\"https://example.invalid/EXTERNALMARKER\"/></draw:frame>"
            + "<draw:frame><draw:image xlink:href=\"https://example.invalid/EXTERNALMARKER.png\"/></draw:frame></text:p>");

        Assert.Equal("Lead linked\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    // ---------------------------------------------------------------- bounds on retained state and output

    [Fact]
    public async Task Extract_CancelledToken_Throws()
    {
        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new OpenDocumentTextExtractor().ExtractAsync(Odt("<text:p>x</text:p>"), sink, cancellation.Token));
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Spreadsheet")]
    [InlineData("Presentation")]
    public async Task Extract_CancellationDuringALargeWalk_IsObserved(string format)
    {
        // The sink cancels the token after its tenth append; the reader's every-1,024-nodes check must notice.
        using CancellationTokenSource cancellation = new();

        CancellingSink sink = new(cancellation, cancelAfterCalls: 10);

        string paragraphs = string.Concat(Enumerable.Range(0, 20_000).Select(index => $"<text:p>Paragraph {index}</text:p>"));

        (ISessionAttachmentDocumentExtractor extractor, byte[] package) = format switch
        {
            "Text" => ((ISessionAttachmentDocumentExtractor)new OpenDocumentTextExtractor(), Odt(paragraphs)),
            "Spreadsheet" => (new OpenDocumentSpreadsheetExtractor(), Ods("<table:table table:name=\"S\">"
                + string.Concat(Enumerable.Range(0, 20_000).Select(index => $"<table:table-row><table:table-cell><text:p>Cell {index}</text:p></table:table-cell></table:table-row>"))
                + "</table:table>")),
            _ => (new OpenDocumentPresentationExtractor(), Odp(string.Concat(Enumerable.Range(0, 20_000).Select(index => $"<draw:page><draw:frame><draw:text-box><text:p>Slide text {index}</text:p></draw:text-box></draw:frame></draw:page>")))),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await extractor.ExtractAsync(package, sink, cancellation.Token));

        Assert.True(sink.Calls < 5_000, $"The walk went on for {sink.Calls} appends after cancellation.");
    }

    [Fact]
    public async Task Extract_OutputBeyondTheSinkCeiling_IsTruncatedAtTheCeiling()
    {
        SessionAttachmentExtractionResult result = await ExtractAsync<OpenDocumentTextExtractor>(
            Odt("<text:p>0123456789</text:p><text:p>abcdefghij</text:p>"),
            capacity: 15);

        Assert.True(result.WasTruncated);

        Assert.Equal("0123456789\nabcd", result.Text);
    }

    [Fact]
    public async Task Extract_StopsWritingAfterTheSinkRefusesText()
    {
        SpyTextSink sink = new(acceptedCalls: 2);

        await new OpenDocumentTextExtractor().ExtractAsync(
            Odt(string.Concat(Enumerable.Range(0, 50).Select(index => $"<text:p>Paragraph {index}</text:p>"))),
            sink,
            CancellationToken.None);

        Assert.Equal(3, sink.Calls);
    }

    [Fact]
    public async Task Extract_ParagraphLongerThanTheRetentionBound_IsCutAtTheBoundAndMarkedTruncated()
    {
        // The sink is larger than the bound, so only the extractor's own retention bound can produce this result.
        string giant = new('x', 300_000);

        SessionAttachmentExtractionResult result = await ExtractAsync<OpenDocumentTextExtractor>(
            Odt($"<text:p>First</text:p><text:p>{giant}</text:p><text:p>NEVERREACHED</text:p>"),
            capacity: 1_000_000);

        Assert.True(result.WasTruncated);

        Assert.Equal("First\n".Length + 262_144, result.Text.Length);

        Assert.StartsWith("First\nxxxx", result.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("NEVERREACHED", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_HugeHiddenParagraph_IsDroppedWholeAndLaterTextStillArrives()
    {
        // The hidden-paragraph field sits at the very end of a paragraph larger than the retention bound. Nothing of the
        // paragraph may leak, and the paragraph after it must still be read.
        string giant = new('x', 300_000);

        SessionAttachmentExtractionResult result = await ExtractAsync<OpenDocumentTextExtractor>(
            Odt($"<text:p>{giant}<text:hidden-paragraph text:condition=\"1\" text:is-hidden=\"true\"/></text:p><text:p>After</text:p>"));

        Assert.Equal("After\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public async Task Extract_ExactlyTheMaximumStyleCount_IsAcceptedAndOneMoreIsStructureBounds()
    {
        string styles(int count) => string.Concat(Enumerable.Range(0, count).Select(index => Style("text", $"s{index}")));

        byte[] atLimit = Odt("<text:p>Shown</text:p>", automaticStyles: styles(OpenDocumentStyleTable.MaximumStyles));

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(atLimit));

        byte[] overLimit = Odt("<text:p>Shown</text:p>", automaticStyles: styles(OpenDocumentStyleTable.MaximumStyles + 1));

        await RejectedAsync<OpenDocumentTextExtractor>(overLimit, DocumentPackageRejection.StructureBounds);
    }

    [Fact]
    public async Task Extract_StyleCountSpansBothStyleParts()
    {
        string common = string.Concat(Enumerable.Range(0, 40_000).Select(index => Style("text", $"c{index}")));

        string automatic = string.Concat(Enumerable.Range(0, 25_537).Select(index => Style("text", $"a{index}")));

        await RejectedAsync<OpenDocumentTextExtractor>(
            Odt("<text:p>x</text:p>", automaticStyles: automatic, commonStyles: common),
            DocumentPackageRejection.StructureBounds);
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public async Task Extract_ParentChainOfThirtyTwoHopsResolvesAndOneMoreIsStructureBounds(int hops, bool rejected)
    {
        // Style s0 inherits from s1 ... s<hops>, and the last one is hidden. A chain of exactly 32 hops still reaches it.
        string chain = string.Concat(Enumerable.Range(0, hops).Select(index => Style("paragraph", $"s{index}", parent: $"s{index + 1}")))
            + Style("paragraph", $"s{hops}", properties: HiddenText);

        byte[] package = Odt("<text:p text:style-name=\"s0\">HIDDENMARKER</text:p><text:p>Shown</text:p>", automaticStyles: chain);

        if (rejected)
        {
            await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.StructureBounds);

            return;
        }

        Assert.Equal("Shown\n", await TextOfAsync<OpenDocumentTextExtractor>(package));
    }

    [Fact]
    public async Task Extract_ParentCycle_IsStructureBoundsNotAHang()
    {
        byte[] package = Odt(
            "<text:p text:style-name=\"a\">x</text:p>",
            automaticStyles: Style("paragraph", "a", parent: "b") + Style("paragraph", "b", parent: "a"));

        await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.StructureBounds);
    }

    [Fact]
    public async Task Extract_StyleNameLongerThanTheRetentionBound_IsStructureBounds()
    {
        byte[] package = Odt("<text:p>x</text:p>", automaticStyles: Style("text", new string('n', OpenDocumentStyleTable.MaximumNameLength + 1)));

        await RejectedAsync<OpenDocumentTextExtractor>(package, DocumentPackageRejection.StructureBounds);
    }
}
