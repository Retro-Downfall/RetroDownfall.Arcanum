using System.Buffers.Binary;
using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class AttachmentMimeDetectorTests
{

    [Theory]
    [InlineData("BM25 scoring notes for the ranking experiment.\n", "notes.md", "text/plain")]
    [InlineData("BMW pricing, trim, msrp\n3 Series,base,43000\n", "pricing.csv", "text/csv")]
    [InlineData("BMP header parsing log line one\n", "parser.log", "text/plain")]
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
