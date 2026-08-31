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
