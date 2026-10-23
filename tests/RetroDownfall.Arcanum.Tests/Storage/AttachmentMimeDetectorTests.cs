using System.Buffers.Binary;
using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class AttachmentMimeDetectorTests
{

    public static TheoryData<string, string> ExtractableExtensionMappings => new()
    {
        { ".json", "application/json" },
        { ".jsonld", "application/ld+json" },
        { ".toml", "application/toml" },
        { ".xml", "application/xml" },
        { ".php", "application/x-httpd-php" },
        { ".cjs", "application/x-javascript" },
        { ".jsonl", "application/x-ndjson" },
        { ".sh", "application/x-sh" },
        { ".yml", "application/x-yaml" },
        { ".yaml", "application/yaml" },
        { ".csv", "text/csv" },
        { ".js", "text/javascript" },
        { ".md", "text/markdown" },
        { ".markdown", "text/markdown" },
        { ".txt", "text/plain" },
        { ".fs", "text/plain" },
        { ".vb", "text/plain" },
        { ".ps1", "text/plain" },
        { ".ts", "text/plain" },
        { ".css", "text/plain" },
        { ".rst", "text/plain" },
        { ".adoc", "text/plain" },
        { ".asciidoc", "text/plain" },
        { ".tex", "text/plain" },
        { ".patch", "text/plain" },
        { ".diff", "text/plain" },
        { ".ini", "text/plain" },
        { ".srt", "text/plain" },
        { ".vtt", "text/plain" },
        { ".ics", "text/plain" },
        { ".tsv", "text/tab-separated-values" },
        { ".c", "text/x-c" },
        { ".h", "text/x-c" },
        { ".cpp", "text/x-c++" },
        { ".cc", "text/x-c++" },
        { ".cxx", "text/x-c++" },
        { ".cs", "text/x-csharp" },
        { ".go", "text/x-go" },
        { ".java", "text/x-java-source" },
        { ".kt", "text/x-kotlin" },
        { ".log", "text/x-log" },
        { ".py", "text/x-python" },
        { ".rb", "text/x-ruby" },
        { ".rs", "text/x-rust" },
        { ".bash", "text/x-shellscript" },
        { ".sql", "text/x-sql" },
        { ".xsl", "text/xml" },
        { ".cff", "text/yaml" },
    };

    [Theory]

    [MemberData(nameof(ExtractableExtensionMappings))]

    public void Detect_returns_the_canonical_extractor_mime_type_for_each_supported_extension(
        string extension,
        string expectedMimeType)
    {

        Assert.Equal(
            expectedMimeType,
            AttachmentMimeDetector.Detect("source content"u8, "source" + extension));

    }

    [Theory]

    [InlineData(".env")]
    [InlineData(".properties")]
    [InlineData(".cfg")]
    [InlineData(".conf")]

    public void Detect_deliberately_excludes_sensitive_configuration_extensions(string extension)
    {

        Assert.Equal(
            "application/octet-stream",
            AttachmentMimeDetector.Detect("secret=value"u8, "configuration" + extension));

    }

    [Fact]

    public void Detect_preserves_binary_signature_precedence_over_a_textual_extension()
    {

        Assert.Equal(
            "application/pdf",
            AttachmentMimeDetector.Detect("%PDF-1.7"u8, "renamed.py"));

    }

    [Theory]
    [InlineData("BM25 scoring notes for the ranking experiment.\n", "notes.md", "text/markdown")]
    [InlineData("BMW pricing, trim, msrp\n3 Series,base,43000\n", "pricing.csv", "text/csv")]
    [InlineData("BMP header parsing log line one\n", "parser.log", "text/x-log")]
    public void Detect_does_not_treat_text_beginning_with_BM_as_bitmap(
        string content,
        string fileName,
        string expected)
    {

        byte[] bytes = Encoding.UTF8.GetBytes(content);

        Assert.Equal(expected, AttachmentMimeDetector.Detect(bytes, fileName));

    }

    [Fact]
    public void Detect_identifies_a_real_bitmap_without_a_bmp_extension()
    {

        byte[] bytes = CreateBitmap();

        Assert.Equal("image/bmp", AttachmentMimeDetector.Detect(bytes, "capture.dat"));

    }

    [Fact]
    public void Detect_falls_back_to_the_bmp_extension_when_the_header_is_unrecognized()
    {

        byte[] bytes = Encoding.UTF8.GetBytes("BM not really a bitmap");

        Assert.Equal("image/bmp", AttachmentMimeDetector.Detect(bytes, "renamed.bmp"));

    }

    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 }, "application/pdf")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    public void Detect_preserves_other_signatures(byte[] bytes, string expected)
    {

        Assert.Equal(expected, AttachmentMimeDetector.Detect(bytes, "payload.bin"));

    }

    [Theory]
    [InlineData("document.arccomp")]
    [InlineData("document.bin")]
    [InlineData("document")]
    public void Detect_recognizes_closed_container_intake(string fileName)
    {
        string mimeType = AttachmentMimeDetector.Detect("ARCCOMP1\npayload"u8, fileName);

        Assert.Equal(SessionAttachmentComprehensionPolicy.MimeType, mimeType);

        Assert.Equal(SessionAttachmentKind.Binary, SessionAttachmentContentPolicy.Classify(mimeType));
    }

    public static TheoryData<string, string> ComprehensionDocumentExtensionMappings => new()
    {
        { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { ".docm", "application/vnd.ms-word.document.macroenabled.12" },
        { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { ".xlsm", "application/vnd.ms-excel.sheet.macroenabled.12" },
        { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
        { ".pptm", "application/vnd.ms-powerpoint.presentation.macroenabled.12" },
        { ".odt", "application/vnd.oasis.opendocument.text" },
        { ".ods", "application/vnd.oasis.opendocument.spreadsheet" },
        { ".odp", "application/vnd.oasis.opendocument.presentation" },
        { ".vsdx", "application/vnd.ms-visio.drawing" },
        { ".vsdm", "application/vnd.ms-visio.drawing.macroenabled.12" },
    };

    [Theory]
    [MemberData(nameof(ComprehensionDocumentExtensionMappings))]
    public void Detect_maps_each_comprehension_document_extension_to_its_registry_mime(
        string extension,
        string expectedMimeType)
    {
        Assert.Equal(expectedMimeType, AttachmentMimeDetector.Detect("PK\u0003\u0004package"u8, "report" + extension));

        Assert.Equal(
            expectedMimeType,
            AttachmentMimeDetector.Detect("PK\u0003\u0004package"u8, "REPORT" + extension.ToUpperInvariant()));

        Assert.True(SessionAttachmentComprehensionPolicy.TryGetFormat(expectedMimeType, out _));
    }

    [Fact]
    public void Detect_maps_every_registry_mime_from_exactly_one_extension()
    {
        string[] mapped = [.. AttachmentMimeDetector.ExtensionMimeTypes.Values
            .Where(static mimeType => SessionAttachmentComprehensionPolicy.SupportedMimeTypes.Contains(mimeType))
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            [.. SessionAttachmentComprehensionPolicy.SupportedMimeTypes.Order(StringComparer.Ordinal)],
            mapped);
    }

    [Fact]
    public void Detect_classifies_every_registry_mime_as_binary_so_kind_never_becomes_text()
    {
        Assert.All(
            SessionAttachmentComprehensionPolicy.Formats,
            static format => Assert.Equal(
                SessionAttachmentKind.Binary,
                SessionAttachmentContentPolicy.Classify(format.MimeType)));
    }

    [Theory]
    [InlineData(".dotx")]
    [InlineData(".dotm")]
    [InlineData(".xltx")]
    [InlineData(".xltm")]
    [InlineData(".potx")]
    [InlineData(".potm")]
    [InlineData(".vstx")]
    [InlineData(".vstm")]
    [InlineData(".ott")]
    [InlineData(".ots")]
    [InlineData(".otp")]
    [InlineData(".fodt")]
    [InlineData(".rtf")]
    [InlineData(".epub")]
    public void Detect_leaves_templates_and_unsupported_package_formats_unrecognized(string extension)
    {
        Assert.Equal(
            "application/octet-stream",
            AttachmentMimeDetector.Detect("PK\u0003\u0004package"u8, "document" + extension));
    }

    [Theory]
    [InlineData("renamed.bin")]
    [InlineData("renamed")]
    public void Detect_does_not_sniff_zip_content_so_a_renamed_package_stays_ineligible(string fileName)
    {
        string mimeType = AttachmentMimeDetector.Detect("PK\u0003\u0004[Content_Types].xml"u8, fileName);

        Assert.Equal("application/octet-stream", mimeType);

        Assert.False(SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out _));
    }

    [Fact]
    public void Detect_arccomp_extension_preserves_document_identity_for_malformed_input()
    {
        Assert.Equal(SessionAttachmentComprehensionPolicy.MimeType,
            AttachmentMimeDetector.Detect("malformed container"u8, "document.arccomp"));
    }

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("notes.md", "text/markdown")]
    public void Detect_container_looking_plain_text_keeps_existing_textual_extension(string fileName, string expected)
    {
        Assert.Equal(expected, AttachmentMimeDetector.Detect("ARCCOMP1\nordinary notes"u8, fileName));
    }

    private static byte[] CreateBitmap()
    {

        const int headerLength = 14;

        const int dibLength = 40;

        const int pixelLength = 4;

        byte[] bytes = new byte[headerLength + dibLength + pixelLength];

        bytes[0] = 0x42;

        bytes[1] = 0x4D;

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), headerLength + dibLength);

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), dibLength);

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 1);

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 1);

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);

        return bytes;

    }

}
