using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class SessionAttachmentTextExtractorTests
{

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
            fileName,
            maxCharacters: 100);

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
            "page.html",
            maxCharacters: 100);

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
            fileName,
            maxCharacters: 100);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Equal(string.Empty, result.Text);

    }

    [Fact]

    public void Extract_InvalidUtf8_FailsWithoutReplacementDecoding()
    {

        SessionAttachmentExtractionResult result = SessionAttachmentTextExtractor.Extract(
            [0xC3, 0x28],
            "text/plain",
            "invalid.txt",
            maxCharacters: 100);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, result.Status);

        Assert.Equal(string.Empty, result.Text);

    }

    [Fact]

    public void Extract_CharacterCap_DoesNotSplitSurrogatePair()
    {

        byte[] bytes = Encoding.UTF8.GetBytes("ab😀cd");

        SessionAttachmentExtractionResult result = SessionAttachmentTextExtractor.Extract(
            bytes,
            "text/plain",
            "emoji.txt",
            maxCharacters: 3);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("ab", result.Text);

        Assert.True(result.WasTruncated);

    }

    [Fact]

    public void Chunk_BoundsCountOverlapLinesAndSurrogates()
    {

        string text = "one\ntwo😀\nthree\nfour";

        SessionAttachmentTextChunk[] chunks = SessionAttachmentChunker.Chunk(
            text,
            chunkSizeCharacters: 8,
            overlapCharacters: 2,
            maxChunks: 2);

        Assert.Equal(2, chunks.Length);

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

}
