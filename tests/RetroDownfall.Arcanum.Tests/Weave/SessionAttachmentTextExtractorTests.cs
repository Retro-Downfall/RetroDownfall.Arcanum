using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class SessionAttachmentTextExtractorTests
{

    [Fact]

    public void SupportedMimeTypes_are_all_extension_reachable()
    {

        IReadOnlySet<string> supportedMimeTypes = SessionAttachmentTextExtractor.SupportedMimeTypes;

        HashSet<string> extensionMimeTypes = AttachmentMimeDetector.ExtensionMimeTypes.Values
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(29, supportedMimeTypes.Count);

        Assert.All(supportedMimeTypes, mimeType => Assert.Contains(mimeType, extensionMimeTypes));

    }

    [Fact]

    public void Detector_produced_textual_mime_types_are_all_extractable()
    {

        IEnumerable<string> textualMimeTypes = AttachmentMimeDetector.ExtensionMimeTypes.Values
            .Where(SessionAttachmentTextExtractor.SupportedMimeTypes.Contains)
            .Append("text/html")
            .Distinct(StringComparer.OrdinalIgnoreCase);

        Assert.All(textualMimeTypes, mimeType =>
        {

            SessionAttachmentExtractionResult result = SessionAttachmentTextExtractor.Extract(
                "alpha\nbeta"u8,
                mimeType,
                "source.txt");

            Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        });

    }

    [Fact]

    public void Extract_recognized_source_mime_type_with_nul_bytes_is_not_eligible()
    {

        SessionAttachmentExtractionResult result = SessionAttachmentTextExtractor.Extract(
            "print('safe')\0"u8,
            "text/x-python",
            "source.py");

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

    }

    public static TheoryData<string, string> SupportedTextTypes => new()
    {
        { "text/plain", "notes.txt" },

        { "text/markdown", "notes.md" },

        { "text/x-csharp", "Program.cs" },

        { "application/json", "data.json" },

        { "application/yaml", "data.yaml" },

        { "application/xml", "data.xml" },

        { "text/csv", "data.csv" },

        { "text/x-log", "server.log" },
    };

    [Theory]

    [MemberData(nameof(SupportedTextTypes))]

    public void Extract_SupportedTextType_ReturnsDeterministicText(string mimeType, string fileName)
    {

        byte[] bytes = Encoding.UTF8.GetBytes("alpha\r\nbeta\rcharlie");

        SessionAttachmentExtractionResult result = SessionAttachmentTextExtractor.Extract(
            bytes,
            mimeType,
            fileName);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("alpha\nbeta\ncharlie", result.Text);

        Assert.False(result.WasTruncated);

    }

    [Fact]

    public void Extract_Html_ReturnsBoundedVisibleTextWithoutScriptsOrStyles()
    {

        byte[] bytes = Encoding.UTF8.GetBytes(
            "<html><head><style>.x{color:red}</style><script>alert(1)</script></head>"
            + "<body><h1>Heading &amp; More</h1><p>Hello<br>world</p></body></html>");

        SessionAttachmentExtractionResult result = SessionAttachmentTextExtractor.Extract(
            bytes,
            "text/html",
            "page.html");

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Contains("Heading & More", result.Text, StringComparison.Ordinal);

        Assert.Contains("Hello", result.Text, StringComparison.Ordinal);

        Assert.Contains("world", result.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("alert", result.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("color:red", result.Text, StringComparison.Ordinal);

    }

    [Theory]

    [InlineData("application/pdf", "document.pdf")]

    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "document.docx")]

    [InlineData("image/png", "image.png")]

    [InlineData("application/octet-stream", "program.bin")]

    [InlineData("application/pdf", "spoofed.txt")]

    public void Extract_UnsupportedOrBinaryType_IsNotEligible(string mimeType, string fileName)
    {

        SessionAttachmentExtractionResult result = SessionAttachmentTextExtractor.Extract(
            [0x00, 0x01, 0x02, 0xFF],
            mimeType,
            fileName);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Equal(string.Empty, result.Text);

    }

    [Fact]

    public void Extract_InvalidUtf8_FailsWithoutReplacementDecoding()
    {

        SessionAttachmentExtractionResult result = SessionAttachmentTextExtractor.Extract(
            [0xC3, 0x28],
            "text/plain",
            "invalid.txt");

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal(string.Empty, result.Text);

    }

    [Fact]

    public void Extract_ReturnsCompleteTextBeyondFormerCharacterCeiling()
    {

        string text = new('a', 200_001);

        byte[] bytes = Encoding.UTF8.GetBytes(text);

        SessionAttachmentExtractionResult result = SessionAttachmentTextExtractor.Extract(
            bytes,
            "text/plain",
            "large.txt");

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal(text, result.Text);

        Assert.False(result.WasTruncated);

    }

    [Fact]

    public void Chunk_ContinuesUntilAllTextIsCovered()
    {

        string text = "one\ntwo😀\nthree\nfour";

        SessionAttachmentTextChunk[] chunks = SessionAttachmentChunker.Chunk(
            text,
            chunkSizeCharacters: 8,
            overlapCharacters: 2);

        Assert.True(chunks.Length > 2);

        Assert.Equal(text.Length, chunks[^1].CharacterEnd);

        Assert.Equal(0, chunks[0].ChunkIndex);

        Assert.Equal(0, chunks[0].CharacterStart);

        Assert.Equal(1, chunks[0].StartLine);

        Assert.True(chunks[0].EndLine >= chunks[0].StartLine);

        Assert.True(chunks[1].CharacterStart < chunks[0].CharacterEnd);

        Assert.All(chunks, static chunk =>
        {

            Assert.False(chunk.Text.Length > 0 && char.IsHighSurrogate(chunk.Text[^1]));

            Assert.False(chunk.Text.Length > 0 && char.IsLowSurrogate(chunk.Text[0]));

        });

    }

    [Fact]

    public void Chunk_ContinuesBeyondFormerPerAttachmentChunkCeiling()
    {

        string text = new('x', 2_000);

        SessionAttachmentTextChunk[] chunks = SessionAttachmentChunker.Chunk(
            text,
            chunkSizeCharacters: 8,
            overlapCharacters: 2);

        Assert.True(chunks.Length > 256);

        Assert.Equal(text.Length, chunks[^1].CharacterEnd);

    }

    [Theory]

    [InlineData("text/plain", "alpha\r\nbeta😀\rcharlie\n&amp;")]

    [InlineData(
        "text/html",
        "<style>hidden</style><h1>Alpha &amp; Beta</h1><script>hidden()</script><p>Gamma<br>Delta</p>")]

    public async Task ReadChunksAsync_MatchesDeterministicExtractionAcrossReadBoundaries(
        string mimeType,
        string source)
    {

        byte[] bytes = Encoding.UTF8.GetBytes(source);

        SessionAttachmentExtractionResult extraction = SessionAttachmentTextExtractor.Extract(
            bytes,
            mimeType,
            "streamed.txt");

        SessionAttachmentTextChunk[] expected = SessionAttachmentChunker.Chunk(
            extraction.Text,
            chunkSizeCharacters: 8,
            overlapCharacters: 2);

        await using ChunkedReadStream stream = new(bytes, maxReadBytes: 3);

        List<SessionAttachmentTextChunk> actual = [];

        await foreach (SessionAttachmentTextChunk chunk in SessionAttachmentTextExtractor.ReadChunksAsync(
                           stream,
                           mimeType,
                           "streamed.txt",
                           chunkSizeCharacters: 8,
                           overlapCharacters: 2))
        {

            actual.Add(chunk);

        }

        Assert.Equal(expected, actual);

        Assert.True(stream.ReadCallCount > 1);

    }

    private sealed class ChunkedReadStream(
        byte[] bytes,
        int maxReadBytes) : MemoryStream(bytes, writable: false)
    {

        public int ReadCallCount { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            ReadCallCount++;

            return base.ReadAsync(
                buffer[..Math.Min(buffer.Length, maxReadBytes)],
                cancellationToken);

        }

    }

}
